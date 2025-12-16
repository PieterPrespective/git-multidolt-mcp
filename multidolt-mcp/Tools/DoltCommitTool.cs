using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that commits the current ChromaDB state to the Dolt repository
/// </summary>
[McpServerToolType]
public class DoltCommitTool
{
    private readonly ILogger<DoltCommitTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;

    /// <summary>
    /// Initializes a new instance of the DoltCommitTool class
    /// </summary>
    public DoltCommitTool(ILogger<DoltCommitTool> logger, IDoltCli doltCli, ISyncManagerV2 syncManager)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
    }

    /// <summary>
    /// Commit the current state of ChromaDB to the Dolt repository, creating a new version that can be pushed, shared, or reverted to
    /// </summary>
    [McpServerTool]
    [Description("Commit the current state of ChromaDB to the Dolt repository, creating a new version that can be pushed, shared, or reverted to.")]
    public virtual async Task<object> DoltCommit(string message, string? author = null)
    {
        try
        {
            _logger.LogInformation($"[DoltCommitTool.DoltCommit] Creating commit with message: {message}");

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

            // Validate message
            if (string.IsNullOrWhiteSpace(message))
            {
                return new
                {
                    success = false,
                    error = "MESSAGE_REQUIRED",
                    message = "Commit message is required"
                };
            }

            // Check for local changes
            var localChanges = await _syncManager.GetLocalChangesAsync();
            if (localChanges == null || !localChanges.HasChanges)
            {
                return new
                {
                    success = false,
                    error = "NO_CHANGES",
                    message = "Nothing to commit (no local changes)"
                };
            }

            // Get parent commit info
            var parentHash = await _doltCli.GetHeadCommitHashAsync();

            // Commit using sync manager (auto-stage enabled)
            var commitResult = await _syncManager.ProcessCommitAsync(message, true, false);
            
            if (!commitResult.Success)
            {
                return new
                {
                    success = false,
                    error = "COMMIT_FAILED",
                    message = commitResult.ErrorMessage ?? "Failed to create commit"
                };
            }

            // Get new commit info
            var newCommitHash = await _doltCli.GetHeadCommitHashAsync();
            var timestamp = DateTime.UtcNow;

            return new
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating commit");
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to create commit: {ex.Message}"
            };
        }
    }
}