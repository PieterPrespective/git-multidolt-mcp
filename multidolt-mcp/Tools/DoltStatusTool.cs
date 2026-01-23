using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that gets the current version control status including branch, commit, and local changes
/// </summary>
[McpServerToolType]
public class DoltStatusTool
{
    private readonly ILogger<DoltStatusTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;

    /// <summary>
    /// Initializes a new instance of the DoltStatusTool class
    /// </summary>
    public DoltStatusTool(ILogger<DoltStatusTool> logger, IDoltCli doltCli, ISyncManagerV2 syncManager)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
    }

    /// <summary>
    /// Get the current version control status including active branch, current commit, and any uncommitted local changes in the ChromaDB working copy
    /// </summary>
    [McpServerTool]
    [Description("Get the current version control status including active branch, current commit, and any uncommitted local changes in the ChromaDB working copy.")]
    public virtual async Task<object> DoltStatus(bool verbose = false)
    {
        const string toolName = nameof(DoltStatusTool);
        const string methodName = nameof(DoltStatus);
        
        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"Verbose: {verbose}");
            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Getting status with verbose={verbose}");

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

            // Get current branch
            var currentBranch = await _doltCli.GetCurrentBranchAsync();
            
            // Get current commit
            var headCommit = await _doltCli.GetHeadCommitHashAsync();
            var commitLog = await _doltCli.GetLogAsync(1);
            var currentCommit = commitLog?.FirstOrDefault();

            // Get remote info
            var remotes = await _doltCli.ListRemotesAsync();
            var primaryRemote = remotes?.FirstOrDefault();

            // Get local changes from sync manager
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Gathering repository status information");
            var syncStatus = await _syncManager.GetStatusAsync();
            var localChanges = await _syncManager.GetLocalChangesAsync();

            // Format changes summary
            var changesSummary = new
            {
                added = localChanges?.NewDocuments?.Count ?? 0,
                modified = localChanges?.ModifiedDocuments?.Count ?? 0,
                deleted = localChanges?.DeletedDocuments?.Count ?? 0,
                total = localChanges?.TotalChanges ?? 0
            };

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["branch"] = currentBranch ?? "main",
                ["commit"] = new
                {
                    hash = headCommit ?? "",
                    short_hash = headCommit?.Substring(0, Math.Min(7, headCommit.Length)) ?? "",
                    message = currentCommit?.Message ?? "",
                    author = currentCommit?.Author ?? "",
                    timestamp = currentCommit?.Date.ToString("O") ?? ""
                },
                ["remote"] = primaryRemote != null ? new
                {
                    name = primaryRemote.Name,
                    url = primaryRemote.Url,
                    connected = true // TODO: Check actual connectivity
                } : null,
                ["local_changes"] = new
                {
                    has_changes = changesSummary.total > 0,
                    summary = changesSummary,
                    documents = verbose && localChanges != null ? new
                    {
                        added = localChanges.NewDocuments?.Select(d => d.DocId).ToArray() ?? Array.Empty<string>(),
                        modified = localChanges.ModifiedDocuments?.Select(d => d.DocId).ToArray() ?? Array.Empty<string>(),
                        deleted = localChanges.DeletedDocuments?.Select(d => d.DocId).ToArray() ?? Array.Empty<string>()
                    } : null
                },
                ["sync_state"] = new
                {
                    ahead = 0, // TODO: Calculate commits ahead/behind
                    behind = 0,
                    diverged = false
                },
                ["message"] = $"On branch '{currentBranch}' with {changesSummary.total} uncommitted changes"
            };

            var resultMessage = $"Status for branch '{currentBranch}' with {changesSummary.total} changes";
            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, resultMessage);
            return result;
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to get status: {ex.Message}"
            };
        }
    }
}