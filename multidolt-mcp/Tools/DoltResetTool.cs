using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;
using DMMS.Utilities;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that resets to a specific commit, discarding local changes
/// </summary>
[McpServerToolType]
public class DoltResetTool
{
    private readonly ILogger<DoltResetTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;

    /// <summary>
    /// Initializes a new instance of the DoltResetTool class
    /// </summary>
    public DoltResetTool(ILogger<DoltResetTool> logger, IDoltCli doltCli, ISyncManagerV2 syncManager)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
    }

    /// <summary>
    /// Reset the current branch to a specific commit, updating ChromaDB to match. WARNING: This discards uncommitted local changes
    /// </summary>
    [McpServerTool]
    [Description("Reset the current branch to a specific commit, updating ChromaDB to match. WARNING: This discards uncommitted local changes.")]
    public virtual async Task<object> DoltReset(string target = "HEAD", bool confirm_discard = false)
    {
        const string toolName = nameof(DoltResetTool);
        const string methodName = nameof(DoltReset);
        
        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName,
                $"target: '{target}', confirm_discard: {confirm_discard}");

            // First check if Dolt is available
            var doltCheck = await _doltCli.CheckDoltAvailableAsync();
            if (!doltCheck.Success)
            {
                const string error = "DOLT_EXECUTABLE_NOT_FOUND";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error,
                    message = doltCheck.Error
                };
            }

            // Check if repository is initialized
            var isInitialized = await _doltCli.IsInitializedAsync();
            if (!isInitialized)
            {
                const string error = "NOT_INITIALIZED";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error,
                    message = "No Dolt repository configured. Use dolt_init or dolt_clone first."
                };
            }

            // Get current state
            var fromCommit = await _doltCli.GetHeadCommitHashAsync();

            // Check for uncommitted changes
            var localChanges = await _syncManager.GetLocalChangesAsync();
            bool hasChanges = localChanges?.HasChanges ?? false;

            if (hasChanges && !confirm_discard)
            {
                const string error = "CONFIRMATION_REQUIRED";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error,
                    message = $"You have {localChanges?.TotalChanges ?? 0} uncommitted changes that will be lost. Set confirm_discard=true to proceed.",
                    local_changes = new
                    {
                        added = localChanges?.NewDocuments?.Count ?? 0,
                        modified = localChanges?.ModifiedDocuments?.Count ?? 0,
                        deleted = localChanges?.DeletedDocuments?.Count ?? 0,
                        total = localChanges?.TotalChanges ?? 0
                    }
                };
            }

            // Resolve target (e.g., origin/main -> actual commit hash)
            string targetCommit = target;
            if (target.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            {
                // Fetch first to ensure we have latest remote refs
                var remote = "origin";
                var branch = target.Substring("origin/".Length);
                await _doltCli.FetchAsync(remote);
                // TODO: Get actual remote commit hash
                // For now, we'll let Dolt CLI handle the reference
            }

            // Perform hard reset
            await _doltCli.ResetHardAsync(targetCommit);

            // Sync ChromaDB with the new state - force sync to bypass count optimization after reset
            await _syncManager.FullSyncAsync(forceSync: true);

            // Get new state
            var toCommit = await _doltCli.GetHeadCommitHashAsync();

            // Prepare discarded changes summary
            var discardedChanges = hasChanges ? new
            {
                added = localChanges?.NewDocuments?.Count ?? 0,
                modified = localChanges?.ModifiedDocuments?.Count ?? 0,
                deleted = localChanges?.DeletedDocuments?.Count ?? 0,
                total = localChanges?.TotalChanges ?? 0
            } : new { added = 0, modified = 0, deleted = 0, total = 0 };

            // TODO: Calculate documents restored/removed
            var syncSummary = new
            {
                documents_restored = 0,
                documents_removed = 0
            };

            var response = new
            {
                success = true,
                reset_result = new
                {
                    from_commit = fromCommit ?? "",
                    to_commit = toCommit ?? "",
                    discarded_changes = discardedChanges
                },
                sync_summary = syncSummary,
                message = hasChanges 
                    ? $"Reset to '{target}' and discarded {discardedChanges.total} local changes"
                    : $"Reset to '{target}'"
            };
            
            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, 
                hasChanges 
                    ? $"Reset to '{target}' and discarded {discardedChanges.total} local changes"
                    : $"Reset to '{target}'");
            return response;
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            
            string errorCode = "OPERATION_FAILED";
            if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                errorCode = "COMMIT_NOT_FOUND";
            
            return new
            {
                success = false,
                error = errorCode,
                message = $"Failed to reset: {ex.Message}"
            };
        }
    }
}