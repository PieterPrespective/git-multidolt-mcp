using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that initializes a new Dolt repository
/// </summary>
[McpServerToolType]
public class DoltInitTool
{
    private readonly ILogger<DoltInitTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;
    private readonly IEmbranchStateManifest _manifestService;
    private readonly ISyncStateChecker _syncStateChecker;

    /// <summary>
    /// Initializes a new instance of the DoltInitTool class
    /// </summary>
    public DoltInitTool(
        ILogger<DoltInitTool> logger,
        IDoltCli doltCli,
        ISyncManagerV2 syncManager,
        IEmbranchStateManifest manifestService,
        ISyncStateChecker syncStateChecker)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
        _manifestService = manifestService;
        _syncStateChecker = syncStateChecker;
    }

    /// <summary>
    /// Initialize a new Dolt repository for version control. Use this when starting a new knowledge base or adding version control to an existing ChromaDB collection
    /// </summary>
    [McpServerTool]
    [Description("Initialize a new Dolt repository for version control. Use this when starting a new knowledge base or adding version control to an existing ChromaDB collection. For cloning an existing repository, use dolt_clone instead.")]
    public virtual async Task<object> DoltInit(
        string? remote_url = null,
        string initial_branch = "main",
        bool import_existing = true,
        string commit_message = "Initial import from ChromaDB")
    {
        const string toolName = nameof(DoltInitTool);
        const string methodName = nameof(DoltInit);
        
        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName,
                $"remote_url: '{remote_url ?? "<null>"}', initial_branch: '{initial_branch}', import_existing: {import_existing}, commit_message: '{commit_message}'");

            // First check if Dolt is available
            ToolLoggingUtility.LogToolDebug(_logger, toolName, "Checking if Dolt executable is available...");
            var doltCheck = await _doltCli.CheckDoltAvailableAsync();
            ToolLoggingUtility.LogToolDebug(_logger, toolName, $"Dolt availability check result: Success={doltCheck.Success}, Error='{doltCheck.Error}'");
            
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
            
            ToolLoggingUtility.LogToolDebug(_logger, toolName, "Dolt executable is available");

            // Check if already initialized
            ToolLoggingUtility.LogToolDebug(_logger, toolName, "Checking if repository is already initialized...");
            var isInitialized = await _doltCli.IsInitializedAsync();
            ToolLoggingUtility.LogToolDebug(_logger, toolName, $"Repository initialization check result: {isInitialized}");
            
            if (isInitialized)
            {
                const string error = "ALREADY_INITIALIZED";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error,
                    message = "Repository already exists. Use dolt_status to check state."
                };
            }

            // Initialize repository
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Initializing new Dolt repository...");
            var initResult = await _doltCli.InitAsync();
            ToolLoggingUtility.LogToolDebug(_logger, toolName, $"Init result: Success={initResult.Success}, Error='{initResult.Error}', Output='{initResult.Output}'");
            
            if (!initResult.Success)
            {
                const string error = "INIT_FAILED";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error,
                    message = $"Failed to initialize repository: {initResult.Error}"
                };
            }

            // Configure remote if provided
            bool remoteConfigured = false;
            if (!string.IsNullOrEmpty(remote_url))
            {
                try
                {
                    // Format the URL properly if it's just org/repo format
                    if (!remote_url.StartsWith("http"))
                    {
                        remote_url = $"https://doltremoteapi.dolthub.com/{remote_url}";
                    }
                    
                    await _doltCli.AddRemoteAsync("origin", remote_url);
                    remoteConfigured = true;
                }
                catch (Exception ex)
                {
                    ToolLoggingUtility.LogToolWarning(_logger, toolName, $"Failed to configure remote: {remote_url} - {ex.Message}");
                }
            }

            // Import existing ChromaDB data if requested
            string? initialCommitHash = null;
            int documentsImported = 0;
            List<string> collections = new();

            if (import_existing)
            {
                try
                {
                    // Use sync manager to import existing data - need collection name
                    // For now, use a default collection name or get it from somewhere
                    string defaultCollectionName = "default";
                    await _syncManager.InitializeVersionControlAsync(defaultCollectionName);
                    
                    // Stage and commit if there are changes
                    var changes = await _syncManager.GetLocalChangesAsync();
                    if (changes != null && changes.HasChanges)
                    {
                        await _syncManager.StageLocalChangesAsync(defaultCollectionName);
                        var commitResult = await _syncManager.ProcessCommitAsync(commit_message, true, false);
                        initialCommitHash = await _doltCli.GetHeadCommitHashAsync();
                        
                        documentsImported = (changes.NewDocuments?.Count ?? 0) + 
                                          (changes.ModifiedDocuments?.Count ?? 0);
                        // TODO: Get actual collection names
                        collections.Add("imported_collections");
                    }
                }
                catch (Exception ex)
                {
                    ToolLoggingUtility.LogToolWarning(_logger, toolName, $"Failed to import existing ChromaDB data: {ex.Message}");
                }
            }

            var response = new
            {
                success = true,
                repository = new
                {
                    path = "./data/dolt-repo",
                    branch = initial_branch,
                    commit = initialCommitHash != null ? new
                    {
                        hash = initialCommitHash,
                        message = commit_message
                    } : null
                },
                remote = new
                {
                    configured = remoteConfigured,
                    name = remoteConfigured ? "origin" : null,
                    url = remoteConfigured ? remote_url : null
                },
                import_summary = new
                {
                    documents_imported = documentsImported,
                    collections = collections.ToArray()
                },
                message = remoteConfigured 
                    ? $"Repository initialized with {documentsImported} documents. Remote 'origin' configured. Use dolt_push to upload to DoltHub."
                    : $"Repository initialized with {documentsImported} documents."
            };
            
            // PP13-79-C1: Create/update manifest after successful init
            await CreateOrUpdateManifestAfterInitAsync(initialCommitHash, initial_branch, remote_url);

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName,
                $"Repository initialized with {documentsImported} documents, remote configured: {remoteConfigured}");
            return response;
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to initialize repository: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// PP13-79-C1: Creates or updates the manifest after successful init
    /// </summary>
    private async Task CreateOrUpdateManifestAfterInitAsync(string? commitHash, string branch, string? remoteUrl)
    {
        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltInitTool), "PP13-79-C1: Creating/updating manifest after init...");

            var projectRoot = await _syncStateChecker.GetProjectRootAsync();
            if (string.IsNullOrEmpty(projectRoot))
            {
                projectRoot = Directory.GetCurrentDirectory();
            }

            // Check if manifest exists
            var existingManifest = await _manifestService.ReadManifestAsync(projectRoot);

            if (existingManifest != null)
            {
                // Update existing manifest
                var updatedDolt = existingManifest.Dolt with
                {
                    CurrentCommit = commitHash,
                    CurrentBranch = branch,
                    RemoteUrl = remoteUrl ?? existingManifest.Dolt.RemoteUrl,
                    DefaultBranch = branch
                };

                var updatedManifest = existingManifest with
                {
                    Dolt = updatedDolt,
                    UpdatedAt = DateTime.UtcNow
                };

                await _manifestService.WriteManifestAsync(projectRoot, updatedManifest);
            }
            else
            {
                // Create new manifest
                var newManifest = _manifestService.CreateDefaultManifest(
                    remoteUrl: remoteUrl,
                    defaultBranch: branch,
                    initMode: "auto"
                );

                // Update with current state
                var doltWithCommit = newManifest.Dolt with
                {
                    CurrentCommit = commitHash,
                    CurrentBranch = branch
                };

                newManifest = newManifest with
                {
                    Dolt = doltWithCommit,
                    UpdatedAt = DateTime.UtcNow
                };

                await _manifestService.WriteManifestAsync(projectRoot, newManifest);
            }

            _syncStateChecker.InvalidateCache();

            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltInitTool), $"✅ PP13-79-C1: Manifest created/updated");
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolWarning(_logger, nameof(DoltInitTool), $"⚠️ PP13-79-C1: Failed to create/update manifest: {ex.Message}");
        }
    }
}