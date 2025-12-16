using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;
using DMMS.Models;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that lists available branches in the Dolt repository
/// </summary>
[McpServerToolType]
public class DoltBranchesTool
{
    private readonly ILogger<DoltBranchesTool> _logger;
    private readonly IDoltCli _doltCli;

    /// <summary>
    /// Initializes a new instance of the DoltBranchesTool class
    /// </summary>
    public DoltBranchesTool(ILogger<DoltBranchesTool> logger, IDoltCli doltCli)
    {
        _logger = logger;
        _doltCli = doltCli;
    }

    /// <summary>
    /// List all branches available on the remote Dolt repository, including their latest commit information
    /// </summary>
    [McpServerTool]
    [Description("List all branches available on the remote Dolt repository, including their latest commit information.")]
    public virtual async Task<object> DoltBranches(bool include_local = true, string? filter = null)
    {
        try
        {
            _logger.LogInformation($"[DoltBranchesTool.DoltBranches] Listing branches with include_local={include_local}, filter={filter}");

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

            // Get current branch
            var currentBranch = await _doltCli.GetCurrentBranchAsync();

            // Get all branches
            var allBranches = await _doltCli.ListBranchesAsync();
            
            var branches = new List<object>();
            foreach (var branch in allBranches ?? Enumerable.Empty<BranchInfo>())
            {
                // Apply filter if provided
                if (!string.IsNullOrEmpty(filter))
                {
                    if (!branch.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                branches.Add(new
                {
                    name = branch.Name,
                    is_current = branch.IsCurrent,
                    is_local = true, // BranchInfo only has local branches
                    is_remote = false, // BranchInfo only has local branches
                    latest_commit = new
                    {
                        hash = branch.LastCommitHash ?? "",
                        short_hash = branch.LastCommitHash?.Substring(0, Math.Min(7, branch.LastCommitHash.Length)) ?? "",
                        message = "", // TODO: Get commit message
                        timestamp = "" // TODO: Get commit timestamp
                    },
                    ahead = 0, // TODO: Calculate if local branch
                    behind = 0  // TODO: Calculate if local branch
                });
            }

            return new
            {
                success = true,
                current_branch = currentBranch ?? "main",
                branches = branches.ToArray(),
                total_count = branches.Count,
                message = $"Found {branches.Count} branches"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing branches");
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to list branches: {ex.Message}"
            };
        }
    }
}