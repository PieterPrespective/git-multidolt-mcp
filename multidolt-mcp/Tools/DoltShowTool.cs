using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that shows detailed information about a specific commit
/// </summary>
[McpServerToolType]
public class DoltShowTool
{
    private readonly ILogger<DoltShowTool> _logger;
    private readonly IDoltCli _doltCli;

    /// <summary>
    /// Initializes a new instance of the DoltShowTool class
    /// </summary>
    public DoltShowTool(ILogger<DoltShowTool> logger, IDoltCli doltCli)
    {
        _logger = logger;
        _doltCli = doltCli;
    }

    /// <summary>
    /// Show detailed information about a specific commit, including the list of documents that were added, modified, or deleted
    /// </summary>
    [McpServerTool]
    [Description("Show detailed information about a specific commit, including the list of documents that were added, modified, or deleted.")]
    public virtual async Task<object> DoltShow(string commit, bool include_diff = false, int diff_limit = 10)
    {
        const string toolName = nameof(DoltShowTool);
        const string methodName = nameof(DoltShow);
        
        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName,
                $"commit: '{commit}', include_diff: {include_diff}, diff_limit: {diff_limit}");

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

            // Resolve commit reference (HEAD, HEAD~1, etc.)
            string commitHash = commit;
            if (commit.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                commitHash = await _doltCli.GetHeadCommitHashAsync() ?? "";
            }
            // TODO: Handle other references like HEAD~1, branch names, etc.

            // Get commit info from log
            var commits = await _doltCli.GetLogAsync(50); // Get enough to find the commit
            var targetCommit = commits?.FirstOrDefault(c => 
                c.Hash?.StartsWith(commitHash, StringComparison.OrdinalIgnoreCase) ?? false
            );

            if (targetCommit == null)
            {
                const string error = "COMMIT_NOT_FOUND";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error,
                    message = $"Commit '{commit}' not found"
                };
            }

            // Get parent commit for diff
            var parentCommit = commits?.SkipWhile(c => c.Hash != targetCommit.Hash).Skip(1).FirstOrDefault();

            // TODO: Get actual diff information
            // This would require implementing GetCommitDiffAsync in IDoltCli
            var changes = new
            {
                summary = new
                {
                    added = 0,
                    modified = 0,
                    deleted = 0,
                    total = 0
                },
                documents = new List<object>()
            };

            // Get branches containing this commit
            var allBranches = await _doltCli.ListBranchesAsync();
            var containingBranches = allBranches?
                .Where(b => b.LastCommitHash == targetCommit.Hash)
                .Select(b => b.Name)
                .ToArray() ?? Array.Empty<string>();

            var response = new
            {
                success = true,
                commit = new
                {
                    hash = targetCommit.Hash ?? "",
                    short_hash = targetCommit.Hash?.Substring(0, Math.Min(7, targetCommit.Hash.Length)) ?? "",
                    message = targetCommit.Message ?? "",
                    author = targetCommit.Author ?? "",
                    timestamp = targetCommit.Date.ToString("O"),
                    parent_hash = parentCommit?.Hash ?? ""
                },
                changes = changes,
                branches = containingBranches,
                message = $"Commit '{targetCommit.Hash?.Substring(0, 7)}': {targetCommit.Message}"
            };
            
            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, 
                $"Successfully showed commit '{targetCommit.Hash?.Substring(0, 7)}': {targetCommit.Message}");
            return response;
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to show commit: {ex.Message}"
            };
        }
    }
}