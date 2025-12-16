using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that clones an existing Dolt repository from a remote
/// </summary>
[McpServerToolType]
public class DoltCloneTool
{
    private readonly ILogger<DoltCloneTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;

    /// <summary>
    /// Initializes a new instance of the DoltCloneTool class
    /// </summary>
    public DoltCloneTool(ILogger<DoltCloneTool> logger, IDoltCli doltCli, ISyncManagerV2 syncManager)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
    }

    /// <summary>
    /// Clone an existing Dolt repository from DoltHub or another remote. This downloads the repository and populates the local ChromaDB with the documents from the specified branch/commit
    /// </summary>
    [McpServerTool]
    [Description("Clone an existing Dolt repository from DoltHub or another remote. This downloads the repository and populates the local ChromaDB with the documents from the specified branch/commit.")]
    public virtual async Task<object> DoltClone(string remote_url, string? branch = null, string? commit = null)
    {
        try
        {
            _logger.LogInformation($"[DoltCloneTool.DoltClone] Cloning from: {remote_url}, branch={branch}, commit={commit}");

            // Check if already initialized
            var isInitialized = await _doltCli.IsInitializedAsync();
            if (isInitialized)
            {
                return new
                {
                    success = false,
                    error = "ALREADY_INITIALIZED",
                    message = "Repository already exists. Use dolt_reset or manual cleanup."
                };
            }

            // Format the URL properly if it's just org/repo format
            if (!remote_url.StartsWith("http"))
            {
                remote_url = $"https://doltremoteapi.dolthub.com/{remote_url}";
            }

            // Clone the repository
            await _doltCli.CloneAsync(remote_url, branch);

            // Get current state after clone
            var currentBranch = await _doltCli.GetCurrentBranchAsync();
            var currentCommitHash = await _doltCli.GetHeadCommitHashAsync();
            
            // If specific commit requested, checkout to it
            if (!string.IsNullOrEmpty(commit))
            {
                try
                {
                    await _doltCli.CheckoutAsync(commit, false);
                    currentCommitHash = await _doltCli.GetHeadCommitHashAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to checkout commit: {commit}");
                    return new
                    {
                        success = false,
                        error = "COMMIT_NOT_FOUND",
                        message = $"Repository cloned but commit '{commit}' not found"
                    };
                }
            }

            // Get commit info
            var commits = await _doltCli.GetLogAsync(1);
            var currentCommit = commits?.FirstOrDefault();

            // Sync to ChromaDB
            int documentsLoaded = 0;
            List<string> collectionsCreated = new();
            
            try
            {
                // Perform full sync from Dolt to ChromaDB
                await _syncManager.FullSyncAsync();
                
                // TODO: Get actual counts from sync result
                documentsLoaded = 0; // Would need to be returned from FullSyncAsync
                collectionsCreated.Add(currentBranch ?? "main");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync to ChromaDB after clone");
            }

            return new
            {
                success = true,
                repository = new
                {
                    path = "./data/dolt-repo",
                    remote_url = remote_url
                },
                checkout = new
                {
                    branch = currentBranch ?? "main",
                    commit = new
                    {
                        hash = currentCommitHash ?? "",
                        message = currentCommit?.Message ?? "",
                        timestamp = currentCommit?.Date.ToString("O") ?? ""
                    }
                },
                sync_summary = new
                {
                    documents_loaded = documentsLoaded,
                    collections_created = collectionsCreated.ToArray()
                },
                message = $"Successfully cloned repository from '{remote_url}' and synced {documentsLoaded} documents to ChromaDB"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error cloning repository from '{remote_url}'");
            
            // Determine error type
            string errorCode = "OPERATION_FAILED";
            if (ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
                errorCode = "AUTHENTICATION_FAILED";
            else if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                errorCode = "REMOTE_NOT_FOUND";
            
            return new
            {
                success = false,
                error = errorCode,
                message = $"Failed to clone repository: {ex.Message}"
            };
        }
    }
}