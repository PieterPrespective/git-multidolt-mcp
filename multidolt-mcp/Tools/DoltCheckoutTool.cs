using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that switches to a different branch or commit
/// </summary>
[McpServerToolType]
public class DoltCheckoutTool
{
    private readonly ILogger<DoltCheckoutTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;
    private readonly ISyncStateTracker _syncStateTracker;
    private readonly IEmbranchStateManifest _manifestService;
    private readonly ISyncStateChecker _syncStateChecker;

    /// <summary>
    /// Initializes a new instance of the DoltCheckoutTool class
    /// </summary>
    public DoltCheckoutTool(
        ILogger<DoltCheckoutTool> logger,
        IDoltCli doltCli,
        ISyncManagerV2 syncManager,
        ISyncStateTracker syncStateTracker,
        IEmbranchStateManifest manifestService,
        ISyncStateChecker syncStateChecker)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
        _syncStateTracker = syncStateTracker;
        _manifestService = manifestService;
        _syncStateChecker = syncStateChecker;
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
        const string toolName = nameof(DoltCheckoutTool);
        const string methodName = nameof(DoltCheckout);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"target: {target}, create_branch: {create_branch}, if_uncommitted: {if_uncommitted}");

        try
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Target branch/commit is required");
                return new
                {
                    success = false,
                    error = "TARGET_REQUIRED",
                    message = "Target branch or commit is required"
                };
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Checking out: {target}, create={create_branch}, if_uncommitted={if_uncommitted}");

            // First check if Dolt is available
            var doltCheck = await _doltCli.CheckDoltAvailableAsync();
            if (!doltCheck.Success)
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, doltCheck.Error ?? "Dolt executable not found");
                return new
                {
                    success = false,
                    error = "DOLT_EXECUTABLE_NOT_FOUND",
                    message = doltCheck.Error
                };
            }

            // Check if repository is initialized
            var isInitialized = await _doltCli.IsInitializedAsync();
            if (!isInitialized)
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "No Dolt repository configured. Use dolt_init or dolt_clone first.");
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
                        ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"Checkout to '{target}' blocked: You have {localChanges?.TotalChanges ?? 0} uncommitted changes");
                        return new
                        {
                            success = false,
                            error = "UNCOMMITTED_local changes",
                            message = $"Checkout to '{target}' blocked: You have {localChanges?.TotalChanges ?? 0} uncommitted local changes across {localChanges?.GetAffectedCollectionNames()?.Count() ?? 0} collection(s). Choose an action:",
                            suggested_actions = new[]
                            {
                                "'commit_first' - Save your changes with a commit before switching branches",
                                "'reset_first' - Discard all uncommitted changes permanently", 
                                "'carry' - Bring uncommitted changes to the new branch (may conflict)"
                            },
                            local_changes = new
                            {
                                added = localChanges?.NewDocuments?.Count ?? 0,
                                modified = localChanges?.ModifiedDocuments?.Count ?? 0,
                                deleted = localChanges?.DeletedDocuments?.Count ?? 0,
                                affected_collections = localChanges?.GetAffectedCollectionNames()?.ToArray() ?? new string[0]
                            },
                            troubleshooting = new
                            {
                                note = "If you recently committed changes but still see this error, there may be a metadata cleanup issue. Try 'reset_first' or check the sync manager logs.",
                                current_branch = fromBranch ?? "unknown"
                            }
                        };

                    case "commit_first":
                        commit_message ??= $"WIP: Changes before switching to {target}";
                        ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Committing {localChanges?.TotalChanges ?? 0} changes before checkout");
                        var commitResult = await _syncManager.ProcessCommitAsync(commit_message, true, false);
                        if (!commitResult.Success)
                        {
                            ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"Failed to commit changes before checkout: {commitResult.ErrorMessage}");
                            return new
                            {
                                success = false,
                                error = "COMMIT_FAILED",
                                message = $"Failed to commit changes before checkout: {commitResult.ErrorMessage}",
                                details = new
                                {
                                    attempted_changes = localChanges?.TotalChanges ?? 0,
                                    commit_message = commit_message
                                },
                                recovery_info = new
                                {
                                    can_rollback = false, // Changes were not committed, no rollback needed
                                    original_branch = fromBranch ?? "unknown",
                                    suggestion = "Check the error details and resolve sync issues before retrying"
                                }
                            };
                        }
                        
                        // Wait a moment and re-verify changes were committed
                        await Task.Delay(1000);
                        var postCommitChanges = await _syncManager.GetLocalChangesAsync();
                        if (postCommitChanges?.HasChanges ?? false)
                        {
                            ToolLoggingUtility.LogToolWarning(_logger, toolName, $"WARNING: {postCommitChanges.TotalChanges} changes still detected after commit. This may indicate a metadata cleanup issue.");
                            // Continue anyway as this may be a false positive from Phase 2 issues
                        }
                        else
                        {
                            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Successfully committed changes, no local changes remaining");
                        }
                        break;

                    case "reset_first":
                        await _doltCli.ResetHardAsync("HEAD");
                        break;

                    case "carry":
                        // Changes will be carried over to new branch
                        ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Carrying {localChanges?.TotalChanges ?? 0} uncommitted changes to new branch '{target}'");
                        // Validate that carry is actually supported for this checkout operation
                        if (localChanges?.TotalChanges > 0)
                        {
                            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Change details: {localChanges.NewDocuments?.Count ?? 0} new, {localChanges.ModifiedDocuments?.Count ?? 0} modified, {localChanges.DeletedDocuments?.Count ?? 0} deleted");
                        }
                        break;
                }
            }

            // Store checkpoint state for potential rollback
            var checkpointState = new
            {
                original_branch = fromBranch,
                original_commit = fromCommit,
                had_uncommitted_changes = hasChanges,
                uncommitted_action_taken = if_uncommitted
            };
            
            // Perform checkout using sync manager
            // PP13-69-C1: Force parameter eliminated - sync state conflicts architecturally impossible
            bool preserveLocalChanges = if_uncommitted == "carry"; // Carry mode preserves local changes
            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Initiating checkout to '{target}' (create={create_branch}, carry_mode={preserveLocalChanges})");
            
            // Handle reset_first mode before checkout
            if (if_uncommitted == "reset_first")
            {
                ToolLoggingUtility.LogToolInfo(_logger, toolName, "Reset first mode: performing comprehensive state reset before checkout");
                
                // PP13-69-C6: Comprehensive reset implementation
                var currentBranch = await _doltCli.GetCurrentBranchAsync();
                var resetResult = await _syncManager.PerformComprehensiveResetAsync(currentBranch ?? "main");
                
                if (resetResult.Status != SyncStatusV2.Completed)
                {
                    ToolLoggingUtility.LogToolError(_logger, toolName, $"Comprehensive reset failed: {resetResult.ErrorMessage}");
                    return new
                    {
                        success = false,
                        error = "RESET_FAILED",
                        message = $"Comprehensive state reset failed: {resetResult.ErrorMessage}",
                        details = new
                        {
                            current_branch = currentBranch ?? "unknown",
                            reset_status = resetResult.Status.ToString(),
                            collections_deleted = resetResult.Deleted
                        }
                    };
                }
                
                ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Comprehensive state reset completed successfully. Deleted {resetResult.Deleted} collections.");
            }
            
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "PP13-69-C7 TRACE: About to call ProcessCheckoutAsync from DoltCheckoutTool");
            var checkoutResult = await _syncManager.ProcessCheckoutAsync(target, create_branch, preserveLocalChanges);
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "PP13-69-C7 TRACE: ProcessCheckoutAsync returned from DoltCheckoutTool");

            if (checkoutResult.Status != SyncStatusV2.Completed)
            {
                ToolLoggingUtility.LogToolError(_logger, toolName, $"Checkout failed: {checkoutResult.ErrorMessage}");
                
                string errorCode = "OPERATION_FAILED";
                string detailedMessage = checkoutResult.ErrorMessage ?? $"Failed to checkout '{target}'";
                
                if (checkoutResult.ErrorMessage?.Contains("not found", StringComparison.OrdinalIgnoreCase) ?? false)
                {
                    errorCode = create_branch ? "CANNOT_CREATE_BRANCH" : "BRANCH_NOT_FOUND";
                    if (!create_branch)
                    {
                        detailedMessage = $"Branch '{target}' does not exist. Use create_branch=true to create it, or verify the branch name.";
                    }
                }
                else if (checkoutResult.ErrorMessage?.Contains("conflict", StringComparison.OrdinalIgnoreCase) ?? false)
                {
                    errorCode = "MERGE_CONFLICT";
                    detailedMessage = $"Checkout failed due to conflicts. Your local changes conflict with '{target}'. Try 'reset_first' to discard changes, or manually resolve conflicts.";
                }

                // Attempt recovery if we made any changes
                if (hasChanges && if_uncommitted == "commit_first")
                {
                    ToolLoggingUtility.LogToolWarning(_logger, toolName, "Checkout failed after commit. System state may be inconsistent.");
                    detailedMessage += $" Note: Changes were committed before the failed checkout. You may need to manually switch back to '{checkpointState.original_branch}' if needed.";
                }
                
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, detailedMessage);
                
                return new
                {
                    success = false,
                    error = errorCode,
                    message = detailedMessage,
                    recovery_info = new
                    {
                        can_rollback = hasChanges && if_uncommitted == "commit_first",
                        original_branch = checkpointState.original_branch,
                        suggestion = errorCode == "BRANCH_NOT_FOUND" ? $"Run with create_branch=true to create '{target}'" : "Check the error details and try again"
                    }
                };
            }
            
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Checkout succeeded, verifying final state...");

            // Get new state
            var toBranch = await _doltCli.GetCurrentBranchAsync();
            var toCommit = await _doltCli.GetHeadCommitHashAsync();

            // PP13-69 Phase 3: Reconstruct sync state after successful checkout
            await ReconstructSyncStateAfterCheckout(toBranch);

            // PP13-79-C1: Update manifest after successful checkout
            // Note: For checkout to specific commit (detached HEAD), toBranch may be null
            await UpdateManifestAfterCheckoutAsync(toCommit, toBranch, target, create_branch);

            // Post-checkout validation
            var postCheckoutValidation = new
            {
                branch_switched = toBranch != fromBranch || toBranch == target,
                commit_changed = toCommit != fromCommit,
                expected_branch = target,
                actual_branch = toBranch
            };

            if (toBranch != target)
            {
                ToolLoggingUtility.LogToolWarning(_logger, toolName, $"Post-checkout validation warning: Expected branch '{target}' but currently on '{toBranch}'");
            }

            // Check final state consistency 
            var finalChanges = await _syncManager.GetLocalChangesAsync();
            var finalStateInfo = new
            {
                has_uncommitted_changes = finalChanges?.HasChanges ?? false,
                uncommitted_count = finalChanges?.TotalChanges ?? 0,
                expected_uncommitted = if_uncommitted == "carry" ? (hasChanges ? localChanges?.TotalChanges ?? 0 : 0) : 0
            };

            if (if_uncommitted != "carry" && (finalChanges?.HasChanges ?? false))
            {
                ToolLoggingUtility.LogToolWarning(_logger, toolName, $"Post-checkout validation warning: Unexpected {finalChanges.TotalChanges} uncommitted changes detected after '{if_uncommitted}' mode checkout");
            }

            // Calculate sync changes from checkout result
            var syncSummary = new
            {
                documents_added = checkoutResult.Added,
                documents_modified = checkoutResult.Modified,
                documents_deleted = checkoutResult.Deleted,
                total_changes = checkoutResult.TotalChanges,
                collections_synced = checkoutResult.ChunksProcessed
            };

            string successMessage = create_branch 
                ? $"Created and switched to new branch '{target}'. Synced {syncSummary.total_changes} documents across {syncSummary.collections_synced} collection(s)."
                : $"Switched to '{target}'. Synced {syncSummary.total_changes} documents across {syncSummary.collections_synced} collection(s).";
            
            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, successMessage);
            return new
            {
                success = true,
                action_taken = new
                {
                    uncommitted_handling = hasChanges ? if_uncommitted : "none",
                    branch_created = create_branch,
                    uncommitted_changes_before = hasChanges ? localChanges?.TotalChanges ?? 0 : 0,
                    uncommitted_changes_after = finalStateInfo.uncommitted_count
                },
                checkout_result = new
                {
                    from_branch = fromBranch ?? "",
                    from_commit = fromCommit ?? "",
                    to_branch = toBranch ?? target,
                    to_commit = toCommit ?? "",
                    validation_passed = postCheckoutValidation.branch_switched && (toBranch == target)
                },
                sync_summary = syncSummary,
                final_state = finalStateInfo,
                validation = postCheckoutValidation,
                message = create_branch 
                    ? $"Created and switched to new branch '{target}'. Synced {syncSummary.total_changes} documents across {syncSummary.collections_synced} collection(s)."
                    : $"Switched to '{target}'. Synced {syncSummary.total_changes} documents across {syncSummary.collections_synced} collection(s).",
                troubleshooting = finalStateInfo.has_uncommitted_changes && if_uncommitted != "carry" 
                    ? new { warning = "Unexpected uncommitted changes detected after checkout. This may indicate metadata cleanup issues." }
                    : null
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            
            return new
            {
                success = false,
                error = "UNEXPECTED_ERROR",
                message = $"Checkout to '{target}' failed due to an unexpected error: {ex.Message}",
                error_details = new
                {
                    exception_type = ex.GetType().Name,
                    stack_trace = ex.StackTrace?.Split('\n').Take(5).ToArray(), // First 5 lines only
                    target_branch = target,
                    if_uncommitted_mode = if_uncommitted,
                    create_branch_requested = create_branch
                },
                recovery_suggestions = new[]
                {
                    "Check that the Dolt repository is properly initialized",
                    "Verify the target branch name is correct",
                    "Try 'reset_first' mode if you have uncommitted changes",
                    "Check system resources and Python.NET availability",
                    "Review the full logs for more detailed error information"
                },
                support_info = new
                {
                    log_context = toolName,
                    error_timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                    operation = "checkout"
                }
            };
        }
    }

    /// <summary>
    /// PP13-69 Phase 3: Reconstructs sync state in SQLite after successful branch checkout
    /// </summary>
    private async Task ReconstructSyncStateAfterCheckout(string targetBranch)
    {
        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCheckoutTool), $"PP13-69 Phase 3: Reconstructing sync state for branch '{targetBranch}'...");
            
            // Use the SQLite sync state tracker's reconstruction capability
            var repoPath = Environment.CurrentDirectory; // Current working directory
            var reconstructionResult = await _syncStateTracker.ReconstructSyncStateAsync(repoPath, targetBranch);
            
            if (reconstructionResult)
            {
                ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCheckoutTool), $"✅ Successfully reconstructed sync state for branch '{targetBranch}'");
            }
            else
            {
                ToolLoggingUtility.LogToolWarning(_logger, nameof(DoltCheckoutTool), $"⚠️ Sync state reconstruction returned false for branch '{targetBranch}' - continuing anyway");
            }
        }
        catch (Exception ex)
        {
            // Don't fail the entire checkout operation due to sync state reconstruction issues
            ToolLoggingUtility.LogToolWarning(_logger, nameof(DoltCheckoutTool), $"⚠️ Failed to reconstruct sync state for branch '{targetBranch}': {ex.Message}");
            _logger.LogWarning(ex, "PP13-69 Phase 3: Sync state reconstruction failed, but checkout operation continues");
        }
    }

    /// <summary>
    /// PP13-79-C1: Updates the manifest after a successful checkout.
    /// Handles both branch checkout and commit checkout (detached HEAD).
    /// </summary>
    private async Task UpdateManifestAfterCheckoutAsync(string? commitHash, string? branch, string target, bool createdBranch)
    {
        if (string.IsNullOrEmpty(commitHash))
        {
            return;
        }

        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCheckoutTool), $"PP13-79-C1: Updating manifest after checkout to {target}...");

            var projectRoot = await _syncStateChecker.GetProjectRootAsync();
            if (string.IsNullOrEmpty(projectRoot))
            {
                ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCheckoutTool), "PP13-79-C1: No project root detected, skipping manifest update");
                return;
            }

            // Check if manifest exists
            var manifest = await _manifestService.ReadManifestAsync(projectRoot);
            if (manifest == null)
            {
                ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCheckoutTool), "PP13-79-C1: No manifest found, skipping manifest update");
                return;
            }

            // Update the manifest
            // For detached HEAD (checkout specific commit), branch may be null
            var branchToSet = branch ?? (IsCommitHash(target) ? null : target);

            var updatedDolt = manifest.Dolt with
            {
                CurrentCommit = commitHash,
                CurrentBranch = branchToSet
            };

            var updatedManifest = manifest with
            {
                Dolt = updatedDolt,
                UpdatedAt = DateTime.UtcNow
            };

            await _manifestService.WriteManifestAsync(projectRoot, updatedManifest);

            // Invalidate sync state cache
            _syncStateChecker.InvalidateCache();

            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCheckoutTool),
                $"✅ PP13-79-C1: Manifest updated - branch: {branchToSet ?? "(detached)"}, commit: {commitHash.Substring(0, 7)}");
        }
        catch (Exception ex)
        {
            // Don't fail checkout due to manifest update issues
            ToolLoggingUtility.LogToolWarning(_logger, nameof(DoltCheckoutTool), $"⚠️ PP13-79-C1: Failed to update manifest after checkout: {ex.Message}");
            _logger.LogWarning(ex, "PP13-79-C1: Manifest update failed after checkout, but checkout succeeded");
        }
    }

    /// <summary>
    /// Checks if the target looks like a commit hash (hex string of appropriate length)
    /// </summary>
    private static bool IsCommitHash(string target)
    {
        // Dolt commit hashes are typically 32 characters (full) or 7+ (abbreviated)
        if (string.IsNullOrEmpty(target) || target.Length < 7)
        {
            return false;
        }

        return target.All(c => char.IsLetterOrDigit(c) && (char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')));
    }
}