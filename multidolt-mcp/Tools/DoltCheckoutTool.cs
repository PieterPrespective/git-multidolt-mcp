using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that switches to a different branch or commit
/// </summary>
[McpServerToolType]
public class DoltCheckoutTool
{
    private readonly ILogger<DoltCheckoutTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;

    /// <summary>
    /// Initializes a new instance of the DoltCheckoutTool class
    /// </summary>
    public DoltCheckoutTool(ILogger<DoltCheckoutTool> logger, IDoltCli doltCli, ISyncManagerV2 syncManager)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
    }

    /// <summary>
    /// Switch to a different branch or commit. This updates the local ChromaDB to reflect the documents at that branch/commit
    /// </summary>
    [McpServerTool]
    [Description("Switch to a different branch or commit. This updates the local ChromaDB to reflect the documents at that branch/commit.")]
    public virtual async Task<object> DoltCheckout(
        string target,
        bool create_branch = false,
        string? from = null,
        string if_uncommitted = "abort",
        string? commit_message = null)
    {
        try
        {
            _logger.LogInformation($"[DoltCheckoutTool.DoltCheckout] Checking out: {target}, create={create_branch}, if_uncommitted={if_uncommitted}");

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

            // Get current state
            var fromBranch = await _doltCli.GetCurrentBranchAsync();
            var fromCommit = await _doltCli.GetHeadCommitHashAsync();

            // Check for uncommitted changes
            var localChanges = await _syncManager.GetLocalChangesAsync();
            bool hasChanges = localChanges?.HasChanges ?? false;

            if (hasChanges)
            {
                switch (if_uncommitted)
                {
                    case "abort":
                        return new
                        {
                            success = false,
                            error = "UNCOMMITTED_CHANGES",
                            message = $"You have {localChanges?.TotalChanges ?? 0} uncommitted changes. Choose an action: 'commit_first' to save your changes, 'reset_first' to discard them, or 'carry' to bring changes to the new branch.",
                            local_changes = new
                            {
                                added = localChanges?.NewDocuments?.Count ?? 0,
                                modified = localChanges?.ModifiedDocuments?.Count ?? 0,
                                deleted = localChanges?.DeletedDocuments?.Count ?? 0
                            }
                        };

                    case "commit_first":
                        commit_message ??= $"WIP: Changes before switching to {target}";
                        await _syncManager.ProcessCommitAsync(commit_message, true, false);
                        break;

                    case "reset_first":
                        await _doltCli.ResetHardAsync("HEAD");
                        break;

                    case "carry":
                        // Changes will be carried over to new branch
                        // This is the default behavior in most cases
                        break;
                }
            }

            // Perform checkout using sync manager
            bool forceOverwrite = if_uncommitted == "reset_first";
            var checkoutResult = await _syncManager.ProcessCheckoutAsync(target, create_branch, forceOverwrite);

            if (!checkoutResult.Success)
            {
                string errorCode = "OPERATION_FAILED";
                if (checkoutResult.ErrorMessage?.Contains("not found", StringComparison.OrdinalIgnoreCase) ?? false)
                    errorCode = create_branch ? "CANNOT_CREATE_BRANCH" : "BRANCH_NOT_FOUND";
                
                return new
                {
                    success = false,
                    error = errorCode,
                    message = checkoutResult.ErrorMessage ?? $"Failed to checkout '{target}'"
                };
            }

            // Get new state
            var toBranch = await _doltCli.GetCurrentBranchAsync();
            var toCommit = await _doltCli.GetHeadCommitHashAsync();

            // TODO: Calculate sync changes
            var syncSummary = new
            {
                documents_added = 0,
                documents_modified = 0,
                documents_deleted = 0,
                total_changes = 0
            };

            return new
            {
                success = true,
                action_taken = new
                {
                    uncommitted_handling = hasChanges ? if_uncommitted : "none",
                    branch_created = create_branch
                },
                checkout_result = new
                {
                    from_branch = fromBranch ?? "",
                    from_commit = fromCommit ?? "",
                    to_branch = toBranch ?? target,
                    to_commit = toCommit ?? ""
                },
                sync_summary = syncSummary,
                message = create_branch 
                    ? $"Created and switched to new branch '{target}'"
                    : $"Switched to '{target}'"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error checking out '{target}'");
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to checkout: {ex.Message}"
            };
        }
    }
}