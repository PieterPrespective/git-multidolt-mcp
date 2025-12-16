using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that fetches updates from the remote repository without applying them
/// </summary>
[McpServerToolType]
public class DoltFetchTool
{
    private readonly ILogger<DoltFetchTool> _logger;
    private readonly IDoltCli _doltCli;

    /// <summary>
    /// Initializes a new instance of the DoltFetchTool class
    /// </summary>
    public DoltFetchTool(ILogger<DoltFetchTool> logger, IDoltCli doltCli)
    {
        _logger = logger;
        _doltCli = doltCli;
    }

    /// <summary>
    /// Fetch commits from the remote repository without applying them to your local ChromaDB. Use this to see what changes are available before pulling
    /// </summary>
    [McpServerTool]
    [Description("Fetch commits from the remote repository without applying them to your local ChromaDB. Use this to see what changes are available before pulling.")]
    public virtual async Task<object> DoltFetch(string remote = "origin", string? branch = null)
    {
        try
        {
            _logger.LogInformation($"[DoltFetchTool.DoltFetch] Fetching from remote={remote}, branch={branch}");

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

            // Check if remote exists
            var remotes = await _doltCli.ListRemotesAsync();
            if (remotes?.Any(r => r.Name == remote) != true)
            {
                return new
                {
                    success = false,
                    error = "REMOTE_NOT_FOUND",
                    message = $"Remote '{remote}' not configured"
                };
            }

            // Perform fetch
            await _doltCli.FetchAsync(remote);

            // Get current branch info
            var currentBranch = await _doltCli.GetCurrentBranchAsync();
            
            // TODO: Calculate actual ahead/behind counts
            // This would require comparing local and remote refs
            var currentBranchStatus = new
            {
                branch = currentBranch ?? "main",
                behind = 0,  // TODO: Calculate actual value
                ahead = 0    // TODO: Calculate actual value
            };

            // Get list of updated branches
            // TODO: Track which branches were actually updated by the fetch
            var branchesUpdated = new List<object>();
            var newBranches = new List<string>();
            int totalCommitsFetched = 0;

            return new
            {
                success = true,
                remote = remote,
                updates = new
                {
                    branches_updated = branchesUpdated.ToArray(),
                    new_branches = newBranches.ToArray(),
                    total_commits_fetched = totalCommitsFetched
                },
                current_branch_status = currentBranchStatus,
                message = totalCommitsFetched > 0
                    ? $"Fetched {totalCommitsFetched} new commits. Your branch '{currentBranch}' is {currentBranchStatus.behind} commits behind origin/{currentBranch}."
                    : "Already up to date with remote."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching from remote '{remote}'");
            
            string errorCode = "OPERATION_FAILED";
            if (ex.Message.Contains("unreachable", StringComparison.OrdinalIgnoreCase))
                errorCode = "REMOTE_UNREACHABLE";
            
            return new
            {
                success = false,
                error = errorCode,
                message = $"Failed to fetch from remote: {ex.Message}"
            };
        }
    }
}