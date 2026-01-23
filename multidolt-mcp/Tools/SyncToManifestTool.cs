using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Models;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// PP13-79: MCP tool to sync Embranch state to match the manifest
/// Syncs local Embranch state (Dolt + ChromaDB) to match the manifest-specified state
/// </summary>
[McpServerToolType]
public class SyncToManifestTool
{
    private readonly ILogger<SyncToManifestTool> _logger;
    private readonly IEmbranchStateManifest _manifestService;
    private readonly IEmbranchInitializer _initializer;
    private readonly IDoltCli _doltCli;
    private readonly IGitIntegration _gitIntegration;

    public SyncToManifestTool(
        ILogger<SyncToManifestTool> logger,
        IEmbranchStateManifest manifestService,
        IEmbranchInitializer initializer,
        IDoltCli doltCli,
        IGitIntegration gitIntegration)
    {
        _logger = logger;
        _manifestService = manifestService;
        _initializer = initializer;
        _doltCli = doltCli;
        _gitIntegration = gitIntegration;
    }

    /// <summary>
    /// Sync local Embranch state (Dolt + ChromaDB) to match the manifest.
    /// This will checkout the specified Dolt commit/branch and sync ChromaDB accordingly.
    /// Optionally, you can override the target commit or branch to sync to a different state.
    /// </summary>
    [McpServerTool]
    [Description("Sync local Embranch state (Dolt + ChromaDB) to match the manifest. This will checkout the specified Dolt commit/branch and sync ChromaDB accordingly. Use target_commit or target_branch to override the manifest.")]
    public virtual async Task<object> SyncToManifest(
        bool? force = false,
        string? target_commit = null,
        string? target_branch = null,
        string? project_root = null)
    {
        const string toolName = nameof(SyncToManifestTool);
        const string methodName = nameof(SyncToManifest);

        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName,
                $"Force: {force}, TargetCommit: {target_commit ?? "none"}, TargetBranch: {target_branch ?? "none"}");

            // Determine project root
            string resolvedProjectRoot;
            if (!string.IsNullOrEmpty(project_root))
            {
                resolvedProjectRoot = project_root;
            }
            else
            {
                var gitRoot = await _gitIntegration.GetGitRootAsync(Directory.GetCurrentDirectory());
                resolvedProjectRoot = gitRoot ?? Directory.GetCurrentDirectory();
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Using project root: {resolvedProjectRoot}");

            // Check for manifest unless we have an override
            DmmsManifest? manifest = null;
            if (string.IsNullOrEmpty(target_commit) && string.IsNullOrEmpty(target_branch))
            {
                manifest = await _manifestService.ReadManifestAsync(resolvedProjectRoot);
                if (manifest == null)
                {
                    var error = "No manifest found and no target specified. Use init_manifest to create a manifest or specify target_commit/target_branch.";
                    ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                    return new
                    {
                        success = false,
                        error = "MANIFEST_NOT_FOUND",
                        message = error
                    };
                }
            }

            // Get current state before sync
            var beforeCommit = await _doltCli.GetHeadCommitHashAsync();
            var beforeBranch = await _doltCli.GetCurrentBranchAsync();

            // Check for local changes if not forcing
            if (force != true)
            {
                var status = await _doltCli.GetStatusAsync();
                var hasChanges = (status?.StagedTables?.Any() ?? false) || (status?.ModifiedTables?.Any() ?? false);

                if (hasChanges)
                {
                    ToolLoggingUtility.LogToolWarning(_logger, toolName, "Local changes exist - sync blocked");
                    return new
                    {
                        success = false,
                        error = "LOCAL_CHANGES_EXIST",
                        message = "Local changes exist in Dolt. Commit or discard changes first, or use force=true to override.",
                        current_state = new
                        {
                            branch = beforeBranch,
                            commit = beforeCommit?.Substring(0, Math.Min(7, beforeCommit?.Length ?? 0)),
                            staged_tables = status?.StagedTables?.Count() ?? 0,
                            modified_tables = status?.ModifiedTables?.Count() ?? 0
                        }
                    };
                }
            }

            // Perform sync
            SyncResultV2 syncResult;
            string targetDescription;

            if (!string.IsNullOrEmpty(target_commit))
            {
                // Sync to specific commit (override)
                targetDescription = $"commit {target_commit.Substring(0, Math.Min(7, target_commit.Length))}";
                ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Syncing to override target: {targetDescription}");
                syncResult = await _initializer.SyncToCommitAsync(target_commit, target_branch);
            }
            else if (!string.IsNullOrEmpty(target_branch))
            {
                // Sync to specific branch (override)
                targetDescription = $"branch {target_branch}";
                ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Syncing to override target: {targetDescription}");
                syncResult = await _initializer.SyncToBranchAsync(target_branch);
            }
            else if (manifest != null)
            {
                // Sync to manifest state
                if (!string.IsNullOrEmpty(manifest.Dolt.CurrentCommit))
                {
                    targetDescription = $"manifest commit {manifest.Dolt.CurrentCommit.Substring(0, 7)}";
                }
                else if (!string.IsNullOrEmpty(manifest.Dolt.CurrentBranch))
                {
                    targetDescription = $"manifest branch {manifest.Dolt.CurrentBranch}";
                }
                else
                {
                    targetDescription = "manifest state";
                }

                ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Syncing to: {targetDescription}");
                var initResult = await _initializer.InitializeFromManifestAsync(manifest, resolvedProjectRoot);

                syncResult = new SyncResultV2
                {
                    Status = initResult.Success ? SyncStatusV2.Completed : SyncStatusV2.Failed,
                    ErrorMessage = initResult.ErrorMessage,
                    CommitHash = initResult.DoltCommit,
                    Added = initResult.CollectionsSynced
                };
            }
            else
            {
                // Should not reach here
                return new
                {
                    success = false,
                    error = "INVALID_STATE",
                    message = "No sync target could be determined"
                };
            }

            // Get state after sync
            var afterCommit = await _doltCli.GetHeadCommitHashAsync();
            var afterBranch = await _doltCli.GetCurrentBranchAsync();

            if (syncResult.Success)
            {
                ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName,
                    $"Synced to {targetDescription} - Now at {afterBranch}/{afterCommit?.Substring(0, 7)}");

                return new
                {
                    success = true,
                    message = $"Successfully synced to {targetDescription}",
                    sync_details = new
                    {
                        target = targetDescription,
                        forced = force == true,
                        before = new
                        {
                            branch = beforeBranch,
                            commit = beforeCommit?.Substring(0, Math.Min(7, beforeCommit?.Length ?? 0))
                        },
                        after = new
                        {
                            branch = afterBranch,
                            commit = afterCommit?.Substring(0, Math.Min(7, afterCommit?.Length ?? 0))
                        },
                        changes_applied = syncResult.TotalChanges
                    }
                };
            }
            else
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, syncResult.ErrorMessage ?? "Unknown error");

                return new
                {
                    success = false,
                    error = "SYNC_FAILED",
                    message = $"Failed to sync to {targetDescription}: {syncResult.ErrorMessage}",
                    current_state = new
                    {
                        branch = afterBranch,
                        commit = afterCommit?.Substring(0, Math.Min(7, afterCommit?.Length ?? 0))
                    }
                };
            }
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to sync: {ex.Message}"
            };
        }
    }
}
