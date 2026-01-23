using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;

namespace DMMS.Services;

/// <summary>
/// PP13-79: Implementation of DMMS state initialization.
/// Handles initializing DMMS state based on manifest and configuration,
/// coordinating Dolt operations and ChromaDB synchronization.
/// </summary>
public class DmmsInitializer : IDmmsInitializer
{
    private readonly ILogger<DmmsInitializer> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;
    private readonly IDmmsStateManifest _manifestService;
    private readonly IGitIntegration _gitIntegration;
    private readonly DoltConfiguration _doltConfig;

    public DmmsInitializer(
        ILogger<DmmsInitializer> logger,
        IDoltCli doltCli,
        ISyncManagerV2 syncManager,
        IDmmsStateManifest manifestService,
        IGitIntegration gitIntegration,
        IOptions<DoltConfiguration> doltConfig)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
        _manifestService = manifestService;
        _gitIntegration = gitIntegration;
        _doltConfig = doltConfig.Value;
    }

    /// <inheritdoc />
    public async Task<InitializationResult> InitializeFromManifestAsync(DmmsManifest manifest, string projectRoot)
    {
        _logger.LogInformation("[DmmsInitializer.InitializeFromManifestAsync] Starting initialization from manifest at: {ProjectRoot}", projectRoot);

        try
        {
            // Check initialization mode
            if (manifest.Initialization.Mode == InitializationMode.Disabled)
            {
                _logger.LogInformation("[DmmsInitializer.InitializeFromManifestAsync] Initialization disabled by manifest");
                return new InitializationResult
                {
                    Success = true,
                    ActionTaken = InitializationAction.Skipped
                };
            }

            // Step 1: Check if Dolt repo exists locally
            var repoExists = await _doltCli.IsInitializedAsync();

            if (!repoExists && !string.IsNullOrEmpty(manifest.Dolt.RemoteUrl))
            {
                // Clone from remote
                _logger.LogInformation("[DmmsInitializer.InitializeFromManifestAsync] Dolt repo not found, cloning from remote: {RemoteUrl}",
                    manifest.Dolt.RemoteUrl);

                var cloneResult = await _doltCli.CloneAsync(manifest.Dolt.RemoteUrl);

                if (!cloneResult.Success)
                {
                    _logger.LogError("[DmmsInitializer.InitializeFromManifestAsync] Failed to clone from remote: {Error}", cloneResult.Error);
                    return new InitializationResult
                    {
                        Success = false,
                        ActionTaken = InitializationAction.Failed,
                        ErrorMessage = $"Failed to clone from remote: {cloneResult.Error}"
                    };
                }

                _logger.LogInformation("[DmmsInitializer.InitializeFromManifestAsync] Successfully cloned from remote");
            }
            else if (!repoExists)
            {
                // PP13-81: No remote and no local repo - don't auto-initialize empty repo
                // Instead, return PendingConfiguration and let user configure via ManifestSetRemote or DoltClone
                _logger.LogWarning("[DmmsInitializer.InitializeFromManifestAsync] PP13-81: No Dolt repository found and no remote URL configured.");
                _logger.LogWarning("[DmmsInitializer.InitializeFromManifestAsync] Use DoltInit to create a local repo, DoltClone to clone from remote, or ManifestSetRemote to configure remote URL.");

                return new InitializationResult
                {
                    Success = true,  // Not a failure - just pending configuration
                    ActionTaken = InitializationAction.PendingConfiguration
                };
            }

            // Step 2: Fetch latest from remote if configured
            if (!string.IsNullOrEmpty(manifest.Dolt.RemoteUrl))
            {
                _logger.LogDebug("[DmmsInitializer.InitializeFromManifestAsync] Fetching from remote");

                try
                {
                    await _doltCli.FetchAsync();
                }
                catch (Exception ex)
                {
                    // Log but don't fail - network might be unavailable
                    _logger.LogWarning(ex, "[DmmsInitializer.InitializeFromManifestAsync] Failed to fetch from remote, continuing with local state");
                }
            }

            // Step 3: Checkout specified commit or branch
            InitializationAction action;
            string? targetCommit = null;
            string? targetBranch = null;

            if (!string.IsNullOrEmpty(manifest.Dolt.CurrentCommit))
            {
                // Checkout specific commit
                _logger.LogInformation("[DmmsInitializer.InitializeFromManifestAsync] Checking out commit: {Commit}",
                    manifest.Dolt.CurrentCommit.Substring(0, Math.Min(7, manifest.Dolt.CurrentCommit.Length)));

                var checkoutResult = await _doltCli.CheckoutAsync(manifest.Dolt.CurrentCommit);

                if (!checkoutResult.Success)
                {
                    _logger.LogWarning("[DmmsInitializer.InitializeFromManifestAsync] Failed to checkout commit {Commit}: {Error}. Falling back to branch.",
                        manifest.Dolt.CurrentCommit.Substring(0, Math.Min(7, manifest.Dolt.CurrentCommit.Length)),
                        checkoutResult.Error);

                    // Fallback to branch
                    if (!string.IsNullOrEmpty(manifest.Dolt.CurrentBranch))
                    {
                        checkoutResult = await _doltCli.CheckoutAsync(manifest.Dolt.CurrentBranch);
                        if (checkoutResult.Success)
                        {
                            action = InitializationAction.CheckedOutBranch;
                            targetBranch = manifest.Dolt.CurrentBranch;
                        }
                        else
                        {
                            return new InitializationResult
                            {
                                Success = false,
                                ActionTaken = InitializationAction.Failed,
                                ErrorMessage = $"Failed to checkout branch {manifest.Dolt.CurrentBranch}: {checkoutResult.Error}"
                            };
                        }
                    }
                    else
                    {
                        // Use whatever branch we're on
                        action = InitializationAction.SyncedExisting;
                    }
                }
                else
                {
                    action = InitializationAction.CheckedOutCommit;
                    targetCommit = manifest.Dolt.CurrentCommit;
                }
            }
            else if (!string.IsNullOrEmpty(manifest.Dolt.CurrentBranch))
            {
                // Checkout specific branch
                _logger.LogInformation("[DmmsInitializer.InitializeFromManifestAsync] Checking out branch: {Branch}",
                    manifest.Dolt.CurrentBranch);

                var checkoutResult = await _doltCli.CheckoutAsync(manifest.Dolt.CurrentBranch);

                if (!checkoutResult.Success)
                {
                    _logger.LogWarning("[DmmsInitializer.InitializeFromManifestAsync] Failed to checkout branch {Branch}: {Error}",
                        manifest.Dolt.CurrentBranch, checkoutResult.Error);

                    // Continue with current state
                    action = InitializationAction.SyncedExisting;
                }
                else
                {
                    action = InitializationAction.CheckedOutBranch;
                    targetBranch = manifest.Dolt.CurrentBranch;
                }
            }
            else
            {
                action = InitializationAction.SyncedExisting;
            }

            // Step 4: Sync ChromaDB to match Dolt state
            _logger.LogInformation("[DmmsInitializer.InitializeFromManifestAsync] Syncing ChromaDB to Dolt state");

            var syncResult = await _syncManager.FullSyncAsync(collectionName: null, forceSync: true);

            if (!syncResult.Success)
            {
                _logger.LogError("[DmmsInitializer.InitializeFromManifestAsync] Failed to sync ChromaDB: {Error}", syncResult.ErrorMessage);
                return new InitializationResult
                {
                    Success = false,
                    ActionTaken = InitializationAction.Failed,
                    ErrorMessage = $"Failed to sync ChromaDB: {syncResult.ErrorMessage}"
                };
            }

            // Get final state
            var currentCommit = await _doltCli.GetHeadCommitHashAsync();
            var currentBranch = await _doltCli.GetCurrentBranchAsync();

            _logger.LogInformation("[DmmsInitializer.InitializeFromManifestAsync] Initialization complete. Action: {Action}, Branch: {Branch}, Commit: {Commit}",
                action, currentBranch, currentCommit?.Substring(0, Math.Min(7, currentCommit?.Length ?? 0)));

            return new InitializationResult
            {
                Success = true,
                ActionTaken = action,
                DoltCommit = currentCommit,
                DoltBranch = currentBranch,
                CollectionsSynced = syncResult.TotalChanges
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DmmsInitializer.InitializeFromManifestAsync] Initialization failed with exception");
            return new InitializationResult
            {
                Success = false,
                ActionTaken = InitializationAction.Failed,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<InitializationCheck> CheckInitializationNeededAsync(DmmsManifest manifest)
    {
        _logger.LogDebug("[DmmsInitializer.CheckInitializationNeededAsync] Checking if initialization is needed");

        try
        {
            // Check if Dolt repo exists
            var repoExists = await _doltCli.IsInitializedAsync();

            if (!repoExists)
            {
                return new InitializationCheck
                {
                    NeedsInitialization = true,
                    Reason = "Dolt repository not initialized"
                };
            }

            // Get current state
            var currentCommit = await _doltCli.GetHeadCommitHashAsync();
            var currentBranch = await _doltCli.GetCurrentBranchAsync();

            // Compare with manifest
            var manifestCommit = manifest.Dolt.CurrentCommit;
            var manifestBranch = manifest.Dolt.CurrentBranch;

            // Check commit match
            if (!string.IsNullOrEmpty(manifestCommit))
            {
                if (currentCommit != manifestCommit)
                {
                    return new InitializationCheck
                    {
                        NeedsInitialization = true,
                        Reason = $"Current commit ({currentCommit?.Substring(0, 7) ?? "none"}) does not match manifest ({manifestCommit.Substring(0, 7)})",
                        CurrentDoltCommit = currentCommit,
                        ManifestDoltCommit = manifestCommit,
                        CurrentBranch = currentBranch,
                        ManifestBranch = manifestBranch
                    };
                }
            }
            else if (!string.IsNullOrEmpty(manifestBranch))
            {
                // Check branch match
                if (currentBranch != manifestBranch)
                {
                    return new InitializationCheck
                    {
                        NeedsInitialization = true,
                        Reason = $"Current branch ({currentBranch ?? "none"}) does not match manifest ({manifestBranch})",
                        CurrentDoltCommit = currentCommit,
                        ManifestDoltCommit = manifestCommit,
                        CurrentBranch = currentBranch,
                        ManifestBranch = manifestBranch
                    };
                }
            }

            // State matches
            return new InitializationCheck
            {
                NeedsInitialization = false,
                Reason = "Current state matches manifest",
                CurrentDoltCommit = currentCommit,
                ManifestDoltCommit = manifestCommit,
                CurrentBranch = currentBranch,
                ManifestBranch = manifestBranch
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DmmsInitializer.CheckInitializationNeededAsync] Error checking initialization state");
            return new InitializationCheck
            {
                NeedsInitialization = true,
                Reason = $"Error checking state: {ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async Task<SyncResultV2> SyncToCommitAsync(string doltCommit, string? branch = null)
    {
        _logger.LogInformation("[DmmsInitializer.SyncToCommitAsync] Syncing to commit: {Commit}",
            doltCommit.Substring(0, Math.Min(7, doltCommit.Length)));

        try
        {
            // Checkout the commit
            var checkoutResult = await _doltCli.CheckoutAsync(doltCommit);

            if (!checkoutResult.Success)
            {
                return new SyncResultV2
                {
                    Status = SyncStatusV2.Failed,
                    ErrorMessage = $"Failed to checkout commit: {checkoutResult.Error}"
                };
            }

            // Sync ChromaDB
            var syncResult = await _syncManager.FullSyncAsync(collectionName: null, forceSync: true);

            return syncResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DmmsInitializer.SyncToCommitAsync] Failed to sync to commit");
            return new SyncResultV2
            {
                Status = SyncStatusV2.Failed,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<SyncResultV2> SyncToBranchAsync(string branchName)
    {
        _logger.LogInformation("[DmmsInitializer.SyncToBranchAsync] Syncing to branch: {Branch}", branchName);

        try
        {
            // Checkout the branch
            var checkoutResult = await _doltCli.CheckoutAsync(branchName);

            if (!checkoutResult.Success)
            {
                return new SyncResultV2
                {
                    Status = SyncStatusV2.Failed,
                    ErrorMessage = $"Failed to checkout branch: {checkoutResult.Error}"
                };
            }

            // Sync ChromaDB
            var syncResult = await _syncManager.FullSyncAsync(collectionName: null, forceSync: true);

            return syncResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DmmsInitializer.SyncToBranchAsync] Failed to sync to branch");
            return new SyncResultV2
            {
                Status = SyncStatusV2.Failed,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<DmmsInitializationState> GetCurrentStateAsync()
    {
        var state = new DmmsInitializationState();

        try
        {
            // Check Dolt state
            state.DoltInitialized = await _doltCli.IsInitializedAsync();

            if (state.DoltInitialized)
            {
                state.CurrentDoltCommit = await _doltCli.GetHeadCommitHashAsync();
                state.CurrentDoltBranch = await _doltCli.GetCurrentBranchAsync();
            }

            // Check Git state
            var currentDir = Directory.GetCurrentDirectory();
            state.IsGitRepository = await _gitIntegration.IsGitRepositoryAsync(currentDir);

            if (state.IsGitRepository)
            {
                state.ProjectRoot = await _gitIntegration.GetGitRootAsync(currentDir);
                state.CurrentGitCommit = await _gitIntegration.GetCurrentGitCommitAsync(currentDir);

                // Check manifest
                if (state.ProjectRoot != null)
                {
                    state.ManifestExists = await _manifestService.ManifestExistsAsync(state.ProjectRoot);

                    if (state.ManifestExists)
                    {
                        var manifest = await _manifestService.ReadManifestAsync(state.ProjectRoot);
                        if (manifest != null)
                        {
                            state.ManifestDoltCommit = manifest.Dolt.CurrentCommit;
                            state.ManifestDoltBranch = manifest.Dolt.CurrentBranch;

                            // Check if state matches manifest
                            state.StateMatchesManifest =
                                (string.IsNullOrEmpty(manifest.Dolt.CurrentCommit) || state.CurrentDoltCommit == manifest.Dolt.CurrentCommit) &&
                                (string.IsNullOrEmpty(manifest.Dolt.CurrentBranch) || state.CurrentDoltBranch == manifest.Dolt.CurrentBranch);
                        }
                    }
                }
            }
            else
            {
                // Not in Git repo, check from current directory
                state.ProjectRoot = currentDir;
                state.ManifestExists = await _manifestService.ManifestExistsAsync(currentDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DmmsInitializer.GetCurrentStateAsync] Error getting current state");
        }

        return state;
    }
}
