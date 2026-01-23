using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that lists commits on a specific branch
/// </summary>
[McpServerToolType]
public class DoltCommitsTool
{
    private readonly ILogger<DoltCommitsTool> _logger;
    private readonly IDoltCli _doltCli;

    /// <summary>
    /// Initializes a new instance of the DoltCommitsTool class
    /// </summary>
    public DoltCommitsTool(ILogger<DoltCommitsTool> logger, IDoltCli doltCli)
    {
        _logger = logger;
        _doltCli = doltCli;
    }

    /// <summary>
    /// List commits on a specified branch, including commit messages, authors, and timestamps. Returns most recent commits first
    /// </summary>
    [McpServerTool]
    [Description("List commits on a specified branch, including commit messages, authors, and timestamps. Returns most recent commits first.")]
    public virtual async Task<object> DoltCommits(
        string? branch = null,
        int limit = 20,
        int offset = 0,
        string? since = null,
        string? until = null)
    {
        const string toolName = nameof(DoltCommitsTool);
        const string methodName = nameof(DoltCommits);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"branch: {branch}, limit: {limit}, offset: {offset}, since: {since}, until: {until}");

        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Listing commits on branch={branch}, limit={limit}, offset={offset}");

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

            // Use current branch if not specified
            if (string.IsNullOrEmpty(branch))
            {
                branch = await _doltCli.GetCurrentBranchAsync();
            }

            // Validate limit
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            // Get commit log
            var commits = await _doltCli.GetLogAsync(limit + offset);
            
            // Apply offset
            if (commits != null && offset > 0)
            {
                commits = commits.Skip(offset).Take(limit).ToList();
            }
            
            // Apply date filters if provided
            if (!string.IsNullOrEmpty(since) || !string.IsNullOrEmpty(until))
            {
                var sinceDate = !string.IsNullOrEmpty(since) ? DateTime.Parse(since) : DateTime.MinValue;
                var untilDate = !string.IsNullOrEmpty(until) ? DateTime.Parse(until) : DateTime.MaxValue;
                
                commits = commits?.Where(c => 
                    c.Date >= sinceDate && c.Date <= untilDate
                ).ToList();
            }

            var formattedCommits = new List<object>();
            string? previousHash = null;
            
            foreach (var commit in commits)
            {
                // TODO: Get actual stats from commit diff
                var stats = new
                {
                    documents_added = 0,
                    documents_modified = 0,
                    documents_deleted = 0
                };

                formattedCommits.Add(new
                {
                    hash = commit.Hash ?? "",
                    short_hash = commit.Hash?.Substring(0, Math.Min(7, commit.Hash.Length)) ?? "",
                    message = commit.Message ?? "",
                    author = commit.Author ?? "",
                    timestamp = commit.Date.ToString("O"),
                    parent_hash = previousHash,
                    stats = stats
                });
                
                previousHash = commit.Hash;
            }

            var hasMore = commits != null && commits.Count() == limit;

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Found {formattedCommits.Count} commits on branch '{branch}'");
            return new
            {
                success = true,
                branch = branch,
                commits = formattedCommits.ToArray(),
                total_commits = formattedCommits.Count,
                has_more = hasMore,
                message = $"Found {formattedCommits.Count} commits on branch '{branch}'"
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to list commits: {ex.Message}"
            };
        }
    }
}