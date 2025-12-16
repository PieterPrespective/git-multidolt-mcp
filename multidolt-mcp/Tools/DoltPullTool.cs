using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that fetches and merges changes from the remote repository
/// </summary>
[McpServerToolType]
public class DoltPullTool
{
    private readonly ILogger<DoltPullTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;

    /// <summary>
    /// Initializes a new instance of the DoltPullTool class
    /// </summary>
    public DoltPullTool(ILogger<DoltPullTool> logger, IDoltCli doltCli, ISyncManagerV2 syncManager)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
    }

    /// <summary>
    /// Fetch changes from the remote and merge them into your current branch. This updates both the Dolt repository and the local ChromaDB with the merged content
    /// </summary>
    [McpServerTool]
    [Description("Fetch changes from the remote and merge them into your current branch. This updates both the Dolt repository and the local ChromaDB with the merged content.")]
    public virtual async Task<object> DoltPull(
        string remote = "origin",
        string? branch = null,
        string if_uncommitted = "abort",
        string? commit_message = null)
    {
        try
        {
            _logger.LogInformation($"[DoltPullTool.DoltPull] Pulling from remote={remote}, branch={branch}, if_uncommitted={if_uncommitted}");

            // Check if repository is initialized
            var isInitialized = await _doltCli.IsInitializedAsync();
            if (!isInitialized)
            {
                return new
                {
                    success = false,
                    error = "NOT_INITIALIZED",
                    message = "No Dolt repository configured. Use dolt_init or dolt_clone first."
                };
            }

            // Check for uncommitted changes
            var localChanges = await _syncManager.GetLocalChangesAsync();
            bool hasChanges = localChanges?.HasChanges ?? false;
            string? preCommitHash = null;

            if (hasChanges)
            {
                switch (if_uncommitted)
                {
                    case "abort":
                        return new
                        {
                            success = false,
                            error = "UNCOMMITTED_CHANGES",
                            message = $"You have {localChanges?.TotalChanges ?? 0} uncommitted changes. Choose an action: 'commit_first' to save your changes, 'reset_first' to discard them, or 'stash' to temporarily save them.",
                            local_changes = new
                            {
                                added = localChanges?.NewDocuments?.Count ?? 0,
                                modified = localChanges?.ModifiedDocuments?.Count ?? 0,
                                deleted = localChanges?.DeletedDocuments?.Count ?? 0
                            }
                        };

                    case "commit_first":
                        commit_message ??= "Auto-commit before pull";
                        await _syncManager.ProcessCommitAsync(commit_message, true, false);
                        preCommitHash = await _doltCli.GetHeadCommitHashAsync();
                        break;

                    case "reset_first":
                        // Reset hard to discard changes
                        await _doltCli.ResetHardAsync("HEAD");
                        break;

                    case "stash":
                        // TODO: Implement stash functionality
                        _logger.LogWarning("Stash functionality not yet implemented");
                        break;
                }
            }

            // Perform the pull operation
            var pullResult = await _syncManager.ProcessPullAsync(
                remote,
                force: if_uncommitted == "reset_first"
            );

            // Get the new commit info
            var newCommitHash = await _doltCli.GetHeadCommitHashAsync();

            // Calculate changes summary
            // TODO: Get actual counts from sync result
            var syncSummary = new
            {
                documents_added = 0,
                documents_modified = 0,
                documents_deleted = 0,
                total_changes = 0
            };

            return new
            {
                success = pullResult.Success,
                action_taken = new
                {
                    uncommitted_handling = if_uncommitted,
                    pre_commit = preCommitHash != null ? new
                    {
                        created = true,
                        hash = preCommitHash
                    } : null
                },
                pull_result = new
                {
                    merge_type = pullResult.WasFastForward ? "fast_forward" : "merge",
                    commits_merged = 0, // TODO: Calculate actual value
                    from_commit = "", // TODO: Get remote commit
                    to_commit = newCommitHash ?? ""
                },
                sync_summary = syncSummary,
                message = pullResult.Success 
                    ? $"Successfully pulled and merged changes from {remote}/{branch ?? "current branch"}"
                    : $"Pull failed: {pullResult.ErrorMessage}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error pulling from remote '{remote}'");
            
            string errorCode = "OPERATION_FAILED";
            if (ex.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase))
                errorCode = "MERGE_CONFLICT";
            else if (ex.Message.Contains("unreachable", StringComparison.OrdinalIgnoreCase))
                errorCode = "REMOTE_UNREACHABLE";
            
            return new
            {
                success = false,
                error = errorCode,
                message = $"Failed to pull from remote: {ex.Message}"
            };
        }
    }
}