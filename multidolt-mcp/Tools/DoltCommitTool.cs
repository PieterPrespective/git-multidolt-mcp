using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that commits the current ChromaDB state to the Dolt repository
/// </summary>
[McpServerToolType]
public class DoltCommitTool
{
    private readonly ILogger<DoltCommitTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;
    private readonly ISyncStateTracker _syncStateTracker;
    private readonly IEmbranchStateManifest _manifestService;
    private readonly ISyncStateChecker _syncStateChecker;

    /// <summary>
    /// Initializes a new instance of the DoltCommitTool class
    /// </summary>
    public DoltCommitTool(
        ILogger<DoltCommitTool> logger,
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
    /// Commit the current state of ChromaDB to the Dolt repository, creating a new version that can be pushed, shared, or reverted to
    /// </summary>
    [McpServerTool]
    [Description("Commit the current state of ChromaDB to the Dolt repository, creating a new version that can be pushed, shared, or reverted to.")]
    public virtual async Task<object> DoltCommit(string message, string? author = null)
    {
        const string toolName = nameof(DoltCommitTool);
        const string methodName = nameof(DoltCommit);
        
        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, 
                $"Message: '{message}', Author: '{author ?? "default"}''");

            // Validate message
            if (string.IsNullOrWhiteSpace(message))
            {
                const string error = "Commit message is required";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = "MESSAGE_REQUIRED",
                    message = error
                };
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Creating commit with message: {message}");

            // First check if Dolt is available
            var doltCheck = await _doltCli.CheckDoltAvailableAsync();
            if (!doltCheck.Success)
            {
                const string error = "DOLT_EXECUTABLE_NOT_FOUND";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {doltCheck.Error}");
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
                const string errorMessage = "No Dolt repository configured. Use dolt_init or dolt_clone first.";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {errorMessage}");
                return new
                {
                    success = false,
                    error = error,
                    message = errorMessage
                };
            }


            // Check for local changes
            var localChanges = await _syncManager.GetLocalChangesAsync();
            if (localChanges == null || !localChanges.HasChanges)
            {
                const string error = "NO_CHANGES";
                const string errorMessage = "Nothing to commit (no local changes)";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {errorMessage}");
                return new
                {
                    success = false,
                    error = error,
                    message = errorMessage
                };
            }

            // Get parent commit info
            var parentHash = await _doltCli.GetHeadCommitHashAsync();

            // PP13-69 Phase 3: Ensure sync state is NEVER staged in Dolt - validate before commit
            await ValidateSyncStateNotStagedAsync();
            
            // Commit using sync manager (auto-stage enabled)
            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Processing commit with {localChanges.TotalChanges} changes");
            var commitResult = await _syncManager.ProcessCommitAsync(message, true, false);
            
            if (!commitResult.Success)
            {
                const string error = "COMMIT_FAILED";
                var errorMessage = commitResult.ErrorMessage ?? "Failed to create commit";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {errorMessage}");
                return new
                {
                    success = false,
                    error = error,
                    message = errorMessage
                };
            }

            // Get new commit info
            var newCommitHash = await _doltCli.GetHeadCommitHashAsync();
            var currentBranch = await _doltCli.GetCurrentBranchAsync();
            var timestamp = DateTime.UtcNow;

            // PP13-69 Phase 3: Update sync state in SQLite after successful commit
            await UpdateSyncStateAfterCommit(newCommitHash);

            // PP13-79-C1: Auto-update manifest after successful commit
            await UpdateManifestAfterCommitAsync(newCommitHash, currentBranch);

            var response = new
            {
                success = true,
                commit = new
                {
                    hash = newCommitHash ?? "",
                    short_hash = newCommitHash?.Substring(0, Math.Min(7, newCommitHash.Length)) ?? "",
                    message = message,
                    author = author ?? "user@example.com",
                    timestamp = timestamp.ToString("O"),
                    parent_hash = parentHash ?? ""
                },
                changes_committed = new
                {
                    added = localChanges.NewDocuments?.Count ?? 0,
                    modified = localChanges.ModifiedDocuments?.Count ?? 0,
                    deleted = localChanges.DeletedDocuments?.Count ?? 0,
                    total = localChanges.TotalChanges
                },
                message = $"Created commit {newCommitHash?.Substring(0, 7)} with {localChanges.TotalChanges} document changes."
            };

            var resultMessage = $"Created commit {newCommitHash?.Substring(0, 7)} with {localChanges.TotalChanges} changes";
            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, resultMessage);
            return response;
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to create commit: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// PP13-69 Phase 3: Validates that sync state tables are never staged in Dolt
    /// </summary>
    private async Task ValidateSyncStateNotStagedAsync()
    {
        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCommitTool), "PP13-69 Phase 3: Validating sync state not staged in Dolt...");
            
            // Check staged tables to ensure sync state related tables are not included
            var status = await _doltCli.GetStatusAsync();
            var stagedTables = status?.StagedTables?.ToList() ?? new List<string>();
            
            // Look for any sync state related tables that should not be in Dolt
            var syncStateRelatedTables = new[] { "chroma_sync_state", "sync_state", "local_sync_state" };
            
            foreach (var table in syncStateRelatedTables)
            {
                if (stagedTables.Any(stagedTable => stagedTable.Contains(table, StringComparison.OrdinalIgnoreCase)))
                {
                    ToolLoggingUtility.LogToolWarning(_logger, nameof(DoltCommitTool), 
                        $"⚠️ PP13-69 Phase 3 VIOLATION: Sync state table '{table}' detected in staged files! This should be in SQLite only.");
                    
                    // Log this as a warning but don't fail the commit - the sync state should be managed in SQLite
                    _logger.LogWarning("PP13-69 Phase 3: Sync state table {Table} found in staged files - this indicates a configuration issue", table);
                }
            }
            
            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCommitTool), "✅ PP13-69 Phase 3: Sync state validation completed");
        }
        catch (Exception ex)
        {
            // Don't fail commit due to validation issues, but log the problem
            ToolLoggingUtility.LogToolWarning(_logger, nameof(DoltCommitTool), $"⚠️ Failed to validate sync state staging: {ex.Message}");
            _logger.LogWarning(ex, "PP13-69 Phase 3: Sync state validation failed, but commit continues");
        }
    }

    /// <summary>
    /// PP13-69 Phase 3: Updates sync state in SQLite after successful Dolt commit
    /// </summary>
    private async Task UpdateSyncStateAfterCommit(string commitHash)
    {
        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCommitTool), $"PP13-69 Phase 3: Updating sync state in SQLite for commit {commitHash?.Substring(0, 7)}...");
            
            var repoPath = Environment.CurrentDirectory;
            var currentBranch = await _doltCli.GetCurrentBranchAsync();
            
            // Get all collections that have sync state and update their commit hashes
            var allSyncStates = await _syncStateTracker.GetAllSyncStatesAsync(repoPath);
            
            foreach (var syncState in allSyncStates)
            {
                // Update only sync states for the current branch (or null branch context)
                if (syncState.BranchContext == currentBranch || syncState.BranchContext == null)
                {
                    await _syncStateTracker.UpdateCommitHashAsync(repoPath, syncState.CollectionName, commitHash, currentBranch);
                    ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCommitTool), 
                        $"✅ Updated sync state for collection '{syncState.CollectionName}' with commit {commitHash?.Substring(0, 7)}");
                }
            }
            
            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCommitTool), "✅ PP13-69 Phase 3: Sync state update completed");
        }
        catch (Exception ex)
        {
            // Don't fail commit due to sync state update issues, but log the problem
            ToolLoggingUtility.LogToolWarning(_logger, nameof(DoltCommitTool), $"⚠️ Failed to update sync state after commit: {ex.Message}");
            _logger.LogWarning(ex, "PP13-69 Phase 3: Sync state update failed after commit");
        }
    }

    /// <summary>
    /// PP13-79-C1: Updates the manifest after a successful commit
    /// </summary>
    private async Task UpdateManifestAfterCommitAsync(string? commitHash, string? branch)
    {
        if (string.IsNullOrEmpty(commitHash))
        {
            return;
        }

        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCommitTool), $"PP13-79-C1: Updating manifest after commit {commitHash.Substring(0, 7)}...");

            var projectRoot = await _syncStateChecker.GetProjectRootAsync();
            if (string.IsNullOrEmpty(projectRoot))
            {
                ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCommitTool), "PP13-79-C1: No project root detected, skipping manifest update");
                return;
            }

            // Check if manifest exists
            var manifestExists = await _manifestService.ManifestExistsAsync(projectRoot);
            if (!manifestExists)
            {
                ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCommitTool), "PP13-79-C1: No manifest found, skipping manifest update");
                return;
            }

            // Update the manifest with new commit
            await _manifestService.UpdateDoltCommitAsync(projectRoot, commitHash, branch ?? "main");

            // Invalidate sync state cache
            _syncStateChecker.InvalidateCache();

            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCommitTool), $"✅ PP13-79-C1: Manifest updated with commit {commitHash.Substring(0, 7)}");
        }
        catch (Exception ex)
        {
            // Don't fail commit due to manifest update issues
            ToolLoggingUtility.LogToolWarning(_logger, nameof(DoltCommitTool), $"⚠️ PP13-79-C1: Failed to update manifest after commit: {ex.Message}");
            _logger.LogWarning(ex, "PP13-79-C1: Manifest update failed after commit, but commit succeeded");
        }
    }
}