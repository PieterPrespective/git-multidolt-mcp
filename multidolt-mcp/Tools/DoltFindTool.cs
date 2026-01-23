using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that searches for commits by hash or message
/// </summary>
[McpServerToolType]
public class DoltFindTool
{
    private readonly ILogger<DoltFindTool> _logger;
    private readonly IDoltCli _doltCli;

    /// <summary>
    /// Initializes a new instance of the DoltFindTool class
    /// </summary>
    public DoltFindTool(ILogger<DoltFindTool> logger, IDoltCli doltCli)
    {
        _logger = logger;
        _doltCli = doltCli;
    }

    /// <summary>
    /// Search for commits by partial hash or message content. Useful for finding specific commits when you don't have the full hash
    /// </summary>
    [McpServerTool]
    [Description("Search for commits by partial hash or message content. Useful for finding specific commits when you don't have the full hash.")]
    public virtual async Task<object> DoltFind(
        string query,
        string search_type = "all",
        string? branch = null,
        int limit = 10)
    {
        const string toolName = nameof(DoltFindTool);
        const string methodName = nameof(DoltFind);
        
        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, 
                $"Query: '{query}', SearchType: '{search_type}', Branch: '{branch}', Limit: {limit}");

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

            // Validate search type
            if (!new[] { "all", "hash", "message" }.Contains(search_type))
            {
                search_type = "all";
            }

            // Get commits from specified branch or all
            // TODO: Support filtering by branch
            var commits = await _doltCli.GetLogAsync(1000); // Get more commits for searching
            
            var results = new List<object>();
            foreach (var commit in commits)
            {
                bool matches = false;
                string matchType = "";

                // Search by hash
                if (search_type == "all" || search_type == "hash")
                {
                    if (commit.Hash?.StartsWith(query, StringComparison.OrdinalIgnoreCase) ?? false)
                    {
                        matches = true;
                        matchType = "hash";
                    }
                }

                // Search by message
                if (!matches && (search_type == "all" || search_type == "message"))
                {
                    if (commit.Message?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                    {
                        matches = true;
                        matchType = "message";
                    }
                }

                if (matches)
                {
                    results.Add(new
                    {
                        hash = commit.Hash ?? "",
                        short_hash = commit.Hash?.Substring(0, Math.Min(7, commit.Hash.Length)) ?? "",
                        message = commit.Message ?? "",
                        author = commit.Author ?? "",
                        timestamp = commit.Date.ToString("O"),
                        branch = branch ?? "unknown", // TODO: Determine actual branch
                        match_type = matchType
                    });

                    if (results.Count >= limit)
                        break;
                }
            }

            var response = new
            {
                success = true,
                query = query,
                results = results.ToArray(),
                total_found = results.Count,
                message = results.Count > 0 
                    ? $"Found {results.Count} commits matching '{query}'"
                    : $"No commits found matching '{query}'"
            };
            
            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, 
                $"Found {results.Count} commits matching '{query}'");
            return response;
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to search commits: {ex.Message}"
            };
        }
    }
}