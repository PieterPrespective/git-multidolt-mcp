using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;
using DMMS.Utilities;

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
        const string toolName = nameof(DoltFetchTool);
        const string methodName = nameof(DoltFetch);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"remote: {remote}, branch: {branch}");

        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Fetching from remote={remote}, branch={branch}");

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

            // Check if remote exists
            var remotes = await _doltCli.ListRemotesAsync();
            if (remotes?.Any(r => r.Name == remote) != true)
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"Remote '{remote}' not configured");
                return new
                {
                    success = false,
                    error = "REMOTE_NOT_FOUND",
                    message = $"Remote '{remote}' not configured"
                };
            }

            // Get branch state BEFORE fetch
            var branchesBeforeFetch = (await _doltCli.ListAllBranchesAsync()).ToList();
            var remoteCommitsBefore = branchesBeforeFetch
                .Where(b => b.IsRemote)
                .ToDictionary(b => b.Name, b => b.LastCommitHash);

            // Perform fetch
            var fetchResult = await _doltCli.FetchAsync(remote);

            // Get branch state AFTER fetch
            var branchesAfterFetch = (await _doltCli.ListAllBranchesAsync()).ToList();
            var remoteCommitsAfter = branchesAfterFetch
                .Where(b => b.IsRemote)
                .ToDictionary(b => b.Name, b => b.LastCommitHash);

            // Get current branch info
            var currentBranch = await _doltCli.GetCurrentBranchAsync();

            // Identify new branches (exist after but not before)
            var newBranches = remoteCommitsAfter.Keys
                .Where(name => !remoteCommitsBefore.ContainsKey(name))
                .Select(name => name.Replace("remotes/origin/", ""))
                .ToList();

            // Identify updated branches (commit hash changed)
            var branchesUpdated = remoteCommitsAfter
                .Where(kvp => remoteCommitsBefore.ContainsKey(kvp.Key) &&
                             remoteCommitsBefore[kvp.Key] != kvp.Value)
                .Select(kvp => new
                {
                    branch = kvp.Key.Replace("remotes/origin/", ""),
                    from_commit = remoteCommitsBefore[kvp.Key],
                    to_commit = kvp.Value
                })
                .ToList<object>();

            // List all available remote branches for reference
            var availableRemoteBranches = branchesAfterFetch
                .Where(b => b.IsRemote)
                .Select(b => b.Name.Replace("remotes/origin/", ""))
                .ToList();

            // Calculate total changes
            int totalCommitsFetched = branchesUpdated.Count + newBranches.Count;

            var currentBranchStatus = new
            {
                branch = currentBranch ?? "main",
                behind = 0,  // Would require additional git log analysis to calculate precisely
                ahead = 0
            };

            string successMessage;
            if (newBranches.Any())
            {
                successMessage = $"Fetched {newBranches.Count} new branch(es) from remote '{remote}': {string.Join(", ", newBranches)}";
            }
            else if (branchesUpdated.Any())
            {
                successMessage = $"Fetched updates for {branchesUpdated.Count} branch(es) from remote '{remote}'";
            }
            else
            {
                successMessage = "Already up to date with remote.";
            }

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, successMessage);
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
                available_remote_branches = availableRemoteBranches,
                current_branch_status = currentBranchStatus,
                message = successMessage
            };
        }
        catch (Exception ex)
        {
            string errorCode = "OPERATION_FAILED";
            if (ex.Message.Contains("unreachable", StringComparison.OrdinalIgnoreCase))
                errorCode = "REMOTE_UNREACHABLE";
            
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = errorCode,
                message = $"Failed to fetch from remote: {ex.Message}"
            };
        }
    }
}