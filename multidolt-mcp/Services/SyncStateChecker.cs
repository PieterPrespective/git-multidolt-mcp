using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;

namespace DMMS.Services;

/// <summary>
/// PP13-79-C1: Implementation of sync state checking between local Dolt and manifest.
/// Compares local Dolt state with manifest and determines if sync is needed and safe.
/// </summary>
public class SyncStateChecker : ISyncStateChecker
{
    private readonly ILogger<SyncStateChecker> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;
    private readonly IDmmsStateManifest _manifestService;
    private readonly IGitIntegration _gitIntegration;
    private readonly ServerConfiguration _serverConfig;

    // Cache for performance - invalidated by Dolt operations
    private SyncStateCheckResult? _cachedResult;
    private DateTime _cacheTime;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Initializes a new instance of SyncStateChecker
    /// </summary>
    public SyncStateChecker(
        ILogger<SyncStateChecker> logger,
        IDoltCli doltCli,
        ISyncManagerV2 syncManager,
        IDmmsStateManifest manifestService,
        IGitIntegration gitIntegration,
        IOptions<ServerConfiguration> serverConfig)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
        _manifestService = manifestService;
        _gitIntegration = gitIntegration;
        _serverConfig = serverConfig.Value;
    }

    /// <inheritdoc />
    public async Task<SyncStateCheckResult> CheckSyncStateAsync()
    {
        // Return cached result if still valid
        if (_cachedResult != null && DateTime.UtcNow - _cacheTime < CacheExpiry)
        {
            _logger.LogDebug("[SyncStateChecker.CheckSyncStateAsync] Returning cached result");
            return _cachedResult;
        }

        _logger.LogDebug("[SyncStateChecker.CheckSyncStateAsync] Checking sync state...");

        try
        {
            // Get project root
            var projectRoot = await GetProjectRootAsync();
            if (string.IsNullOrEmpty(projectRoot))
            {
                return CacheResult(new SyncStateCheckResult
                {
                    IsInSync = true, // No project root = nothing to sync
                    Reason = "No project root detected",
                    ManifestExists = false
                });
            }

            // Check for manifest
            var manifest = await _manifestService.ReadManifestAsync(projectRoot);
            if (manifest == null)
            {
                return CacheResult(new SyncStateCheckResult
                {
                    IsInSync = true, // No manifest = nothing to sync to
                    Reason = "No manifest found",
                    ManifestExists = false
                });
            }

            // Check if Dolt is initialized
            var doltInitialized = await _doltCli.IsInitializedAsync();
            if (!doltInitialized)
            {
                return CacheResult(new SyncStateCheckResult
                {
                    IsInSync = false,
                    Reason = "Dolt repository not initialized",
                    ManifestExists = true,
                    DoltInitialized = false,
                    ManifestCommit = manifest.Dolt.CurrentCommit,
                    ManifestBranch = manifest.Dolt.CurrentBranch
                });
            }

            // Get current Dolt state
            var currentCommit = await _doltCli.GetHeadCommitHashAsync();
            var currentBranch = await _doltCli.GetCurrentBranchAsync();

            // Check for local changes
            var localChanges = await _syncManager.GetLocalChangesAsync();
            var hasLocalChanges = localChanges?.HasChanges ?? false;

            // Compare with manifest
            var manifestCommit = manifest.Dolt.CurrentCommit;
            var manifestBranch = manifest.Dolt.CurrentBranch;

            // Determine sync status
            bool commitMatches = string.IsNullOrEmpty(manifestCommit) ||
                                 currentCommit == manifestCommit;
            bool branchMatches = string.IsNullOrEmpty(manifestBranch) ||
                                 currentBranch == manifestBranch;

            bool isInSync = commitMatches && branchMatches;

            // Check if local is ahead (has commits not in manifest)
            bool localAhead = false;
            if (!isInSync && !string.IsNullOrEmpty(manifestCommit) && !string.IsNullOrEmpty(currentCommit))
            {
                // If manifest commit is an ancestor of current commit, we're ahead
                localAhead = await IsCommitAncestorAsync(manifestCommit, currentCommit);
            }

            string reason;
            if (isInSync)
            {
                reason = "Local state matches manifest";
            }
            else if (!commitMatches && !branchMatches)
            {
                reason = $"Both branch and commit differ: local {currentBranch}/{TruncateCommit(currentCommit)} vs manifest {manifestBranch}/{TruncateCommit(manifestCommit)}";
            }
            else if (!commitMatches)
            {
                reason = $"Commit differs: local {TruncateCommit(currentCommit)} vs manifest {TruncateCommit(manifestCommit)}";
            }
            else
            {
                reason = $"Branch differs: local {currentBranch ?? "none"} vs manifest {manifestBranch ?? "none"}";
            }

            return CacheResult(new SyncStateCheckResult
            {
                IsInSync = isInSync,
                HasLocalChanges = hasLocalChanges,
                LocalAheadOfManifest = localAhead,
                LocalCommit = currentCommit,
                LocalBranch = currentBranch,
                ManifestCommit = manifestCommit,
                ManifestBranch = manifestBranch,
                Reason = reason,
                ManifestExists = true,
                DoltInitialized = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SyncStateChecker.CheckSyncStateAsync] Error checking sync state");
            return new SyncStateCheckResult
            {
                IsInSync = true, // Assume in sync on error to avoid blocking operations
                Reason = $"Error checking sync state: {ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsSafeToSyncAsync()
    {
        var state = await CheckSyncStateAsync();

        // Already in sync = safe (no sync needed)
        if (state.IsInSync)
        {
            return true;
        }

        // Has local changes = not safe
        if (state.HasLocalChanges)
        {
            _logger.LogDebug("[SyncStateChecker.IsSafeToSyncAsync] Not safe: has local changes");
            return false;
        }

        // Local ahead of manifest = not safe (would lose commits)
        if (state.LocalAheadOfManifest)
        {
            _logger.LogDebug("[SyncStateChecker.IsSafeToSyncAsync] Not safe: local ahead of manifest");
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<OutOfSyncWarning?> GetOutOfSyncWarningAsync()
    {
        var state = await CheckSyncStateAsync();

        // No warning if in sync or no manifest
        if (state.IsInSync || !state.ManifestExists)
        {
            return null;
        }

        string message;
        string actionRequired;

        if (state.HasLocalChanges)
        {
            message = "Local Dolt state differs from manifest. You have uncommitted changes that would be lost if synced.";
            actionRequired = "Commit your local changes, then call sync_to_manifest to synchronize with the manifest.";
        }
        else if (state.LocalAheadOfManifest)
        {
            message = "Local Dolt state is ahead of manifest. You have commits not recorded in the manifest.";
            actionRequired = "Call update_manifest to record your current state, or sync_to_manifest to reset to manifest state.";
        }
        else
        {
            message = "Local Dolt state differs from manifest.";
            actionRequired = "Call sync_to_manifest to synchronize with the manifest state.";
        }

        return new OutOfSyncWarning
        {
            Type = "out_of_sync",
            Message = message,
            LocalState = new SyncStateInfo
            {
                Branch = state.LocalBranch,
                Commit = state.LocalCommit
            },
            ManifestState = new SyncStateInfo
            {
                Branch = state.ManifestBranch,
                Commit = state.ManifestCommit
            },
            ActionRequired = actionRequired
        };
    }

    /// <inheritdoc />
    public void InvalidateCache()
    {
        _logger.LogDebug("[SyncStateChecker.InvalidateCache] Cache invalidated");
        _cachedResult = null;
    }

    /// <inheritdoc />
    public async Task<string?> GetProjectRootAsync()
    {
        // Use configured project root if set
        if (!string.IsNullOrEmpty(_serverConfig.ProjectRoot))
        {
            return _serverConfig.ProjectRoot;
        }

        // Auto-detect from Git if enabled
        if (_serverConfig.AutoDetectProjectRoot)
        {
            var gitRoot = await _gitIntegration.GetGitRootAsync(Directory.GetCurrentDirectory());
            if (!string.IsNullOrEmpty(gitRoot))
            {
                return gitRoot;
            }
        }

        // Fall back to current directory
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Caches a result and returns it
    /// </summary>
    private SyncStateCheckResult CacheResult(SyncStateCheckResult result)
    {
        _cachedResult = result;
        _cacheTime = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    /// Truncates a commit hash to 7 characters for display, handling null and short values.
    /// </summary>
    private static string TruncateCommit(string? commit)
    {
        if (string.IsNullOrEmpty(commit))
            return "none";
        return commit.Length > 7 ? commit.Substring(0, 7) : commit;
    }

    /// <summary>
    /// Checks if ancestorCommit is an ancestor of descendantCommit
    /// </summary>
    private async Task<bool> IsCommitAncestorAsync(string ancestorCommit, string descendantCommit)
    {
        try
        {
            // Use dolt log to check if ancestor is in history of descendant
            var logResult = await _doltCli.GetLogAsync(limit: 100);

            if (logResult == null || !logResult.Any())
            {
                return false;
            }

            // Check if we find the ancestor before the descendant in the log
            bool foundDescendant = false;
            foreach (var commit in logResult)
            {
                if (commit.Hash == descendantCommit)
                {
                    foundDescendant = true;
                }
                else if (foundDescendant && commit.Hash == ancestorCommit)
                {
                    return true; // ancestor is in history of descendant
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SyncStateChecker.IsCommitAncestorAsync] Error checking commit ancestry");
            return false;
        }
    }
}
