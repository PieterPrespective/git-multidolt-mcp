using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;
using DMMS.Models;
using DMMS.Utilities;

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
    private readonly ISyncStateTracker _syncStateTracker;
    private readonly string _repositoryPath;
    private readonly DoltConfiguration _doltConfig;
    private readonly IDmmsStateManifest _manifestService;
    private readonly ISyncStateChecker _syncStateChecker;

    /// <summary>
    /// Initializes a new instance of the DoltCloneTool class
    /// </summary>
    public DoltCloneTool(
        ILogger<DoltCloneTool> logger,
        IDoltCli doltCli,
        ISyncManagerV2 syncManager,
        ISyncStateTracker syncStateTracker,
        IOptions<DoltConfiguration> config,
        IDmmsStateManifest manifestService,
        ISyncStateChecker syncStateChecker)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
        _syncStateTracker = syncStateTracker;
        _repositoryPath = config.Value.RepositoryPath;
        _doltConfig = config.Value;
        _manifestService = manifestService;
        _syncStateChecker = syncStateChecker;
    }

    /// <summary>
    /// Clone an existing Dolt repository from DoltHub or another remote. This downloads the repository and populates the local ChromaDB with the documents from the specified branch/commit
    /// </summary>
    [McpServerTool]
    [Description("Clone an existing Dolt repository from DoltHub or another remote. This downloads the repository and populates the local ChromaDB with the documents from the specified branch/commit.")]
    public virtual async Task<object> DoltClone(string remote_url, string? branch = null, string? commit = null)
    {
        const string toolName = nameof(DoltCloneTool);
        const string methodName = nameof(DoltClone);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"remote_url: {remote_url}, branch: {branch}, commit: {commit}");

        try
        {
            if (string.IsNullOrWhiteSpace(remote_url))
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Remote URL is required");
                return new
                {
                    success = false,
                    error = "REMOTE_URL_REQUIRED",
                    message = "Remote URL is required for clone operation"
                };
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Cloning from: {remote_url}, branch={branch}, commit={commit}");

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

            // Check if already initialized
            var isInitialized = await _doltCli.IsInitializedAsync();
            if (isInitialized)
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Repository already exists. Use dolt_reset or manual cleanup.");
                return new
                {
                    success = false,
                    error = "ALREADY_INITIALIZED",
                    message = "Repository already exists. Use dolt_reset or manual cleanup."
                };
            }

            // Format the URL properly based on the input format
            string formattedUrl = remote_url;
            
            // Handle different URL formats
            if (!remote_url.StartsWith("http") && !remote_url.StartsWith("file://"))
            {
                // Check if it's a local file path (contains backslash or starts with drive letter)
                if (remote_url.Contains('\\') || remote_url.Contains(':') || remote_url.StartsWith('/'))
                {
                    // Local file path - use file:// protocol
                    // Convert backslashes to forward slashes and ensure proper file URI format
                    var normalizedPath = remote_url.Replace('\\', '/');
                    if (!normalizedPath.StartsWith('/'))
                    {
                        // Windows path like C:/path - needs three slashes after file:
                        formattedUrl = $"file:///{normalizedPath}";
                    }
                    else
                    {
                        // Unix-style path
                        formattedUrl = $"file://{normalizedPath}";
                    }
                    _logger.LogInformation($"[DoltCloneTool.DoltClone] Formatted local path '{remote_url}' to '{formattedUrl}'");
                }
                else
                {
                    // Assume it's a DoltHub org/repo format
                    formattedUrl = $"https://doltremoteapi.dolthub.com/{remote_url}";
                    ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Formatted DoltHub repo '{remote_url}' to '{formattedUrl}'");
                }
            }

            // PP13-56-C1: Removed pre-clone URL validation that creates temporary test directories
            // This test code was running in production and causing file locking issues
            // URL validation will now happen during actual clone operation
            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Starting clone operation for: '{formattedUrl}'");

            // Clone the repository and check for success
            // IMPORTANT: Pass "." to clone into the current directory (_repositoryPath), not a subdirectory
            // This prevents the duplicate database issue where a subdirectory would be created
            var cloneResult = await _doltCli.CloneAsync(formattedUrl, ".");
            bool isCloneSuccessful = cloneResult.Success;
            bool remoteConfigured = false;
            
            if (!cloneResult.Success)
            {
                _logger.LogWarning($"[DoltCloneTool.DoltClone] Clone operation failed: {cloneResult.Error}");
                _logger.LogInformation($"[DoltCloneTool.DoltClone] Clone command attempted with URL: '{formattedUrl}'");
                _logger.LogInformation($"[DoltCloneTool.DoltClone] Working directory: {Environment.CurrentDirectory}");
                
                // Check if the failure was due to empty repository (no Dolt data)
                bool isEmptyRepoError = cloneResult.Error?.Contains("no Dolt data", StringComparison.OrdinalIgnoreCase) == true;
                _logger.LogInformation($"[DoltCloneTool.DoltClone] Empty repository error detected: {isEmptyRepoError}");
                _logger.LogInformation($"[DoltCloneTool.DoltClone] Full error message: '{cloneResult.Error}'");
                
                if (isEmptyRepoError)
                {
                    _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Empty repository detected, attempting fallback: init + remote setup");
                    
                    string fallbackStep = "starting";
                    // Try fallback: initialize repository and manually add remote
                    try
                    {
                        // Clean up any corrupted .dolt directory from failed clone
                        fallbackStep = "cleaning corrupted repository";
                        _logger.LogInformation($"[DoltCloneTool.DoltClone] Fallback step: {fallbackStep}");
                        
                        // Use the configured repository path
                        var doltDir = Path.Combine(_repositoryPath, ".dolt");
                        
                        if (Directory.Exists(doltDir))
                        {
                            _logger.LogInformation($"[DoltCloneTool.DoltClone] Found corrupted .dolt directory at '{doltDir}' from failed clone, removing it");
                            try
                            {
                                Directory.Delete(doltDir, true);
                                _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Successfully removed corrupted .dolt directory");
                            }
                            catch (Exception cleanupEx)
                            {
                                _logger.LogWarning(cleanupEx, $"[DoltCloneTool.DoltClone] Failed to remove corrupted .dolt directory: {cleanupEx.Message}");
                            }
                        }
                        
                        // Now check if repository can be initialized
                        fallbackStep = "checking initialization status";
                        _logger.LogInformation($"[DoltCloneTool.DoltClone] Fallback step: {fallbackStep}");
                        
                        var isPartiallyInitialized = await _doltCli.IsInitializedAsync();
                        _logger.LogInformation($"[DoltCloneTool.DoltClone] Repository initialization status after cleanup: {isPartiallyInitialized}");
                        
                        if (!isPartiallyInitialized)
                        {
                            // Initialize a new repository
                            fallbackStep = "initializing repository";
                            _logger.LogInformation($"[DoltCloneTool.DoltClone] Fallback step: {fallbackStep}");
                            
                            var initResult = await _doltCli.InitAsync();
                            _logger.LogInformation($"[DoltCloneTool.DoltClone] Init result - Success: {initResult.Success}, Error: '{initResult.Error}', Output: '{initResult.Output}'");
                            
                            if (!initResult.Success)
                            {
                                // Check if the error is because it's already initialized (from partial clone)
                                if (initResult.Error?.Contains("already been initialized", StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    _logger.LogInformation($"[DoltCloneTool.DoltClone] Repository was partially initialized by failed clone, continuing with remote setup");
                                    isPartiallyInitialized = true;
                                }
                                else
                                {
                                    _logger.LogError($"[DoltCloneTool.DoltClone] ❌ Fallback init failed at step '{fallbackStep}': {initResult.Error}");
                                    throw new Exception($"Failed to initialize repository: {initResult.Error}");
                                }
                            }
                            else
                            {
                                _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Repository initialized successfully");
                            }
                        }
                        else
                        {
                            _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Repository already partially initialized (from failed clone), proceeding with remote setup");
                        }
                        
                        // Add the remote manually and validate URL connectivity
                        fallbackStep = "validating remote URL";
                        _logger.LogInformation($"[DoltCloneTool.DoltClone] Fallback step: {fallbackStep} with URL: '{formattedUrl}'");
                        
                        var remoteResult = await _doltCli.AddRemoteAsync("origin", formattedUrl);
                        _logger.LogInformation($"[DoltCloneTool.DoltClone] Add remote result - Success: {remoteResult.Success}, Error: '{remoteResult.Error}', Output: '{remoteResult.Output}'");
                        
                        // Enhanced remote validation: check for invalid URL errors
                        if (!remoteResult.Success && remoteResult.Error != null)
                        {
                            // Check for invalid remote URL patterns
                            bool isInvalidUrlError = remoteResult.Error.Contains("do not exist", StringComparison.OrdinalIgnoreCase) ||
                                                   remoteResult.Error.Contains("Invalid repository ID", StringComparison.OrdinalIgnoreCase) ||
                                                   remoteResult.Error.Contains("repository not found", StringComparison.OrdinalIgnoreCase) ||
                                                   remoteResult.Error.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
                                                   remoteResult.Error.Contains("invalid remote", StringComparison.OrdinalIgnoreCase) ||
                                                   remoteResult.Error.Contains("could not connect", StringComparison.OrdinalIgnoreCase);
                            
                            if (isInvalidUrlError)
                            {
                                _logger.LogError($"[DoltCloneTool.DoltClone] ❌ Invalid remote URL detected early: {remoteResult.Error}");
                                
                                // Clean up the partially initialized repository
                                try
                                {
                                    var invalidRemoteDoltDir = Path.Combine(_repositoryPath, ".dolt");
                                    if (Directory.Exists(invalidRemoteDoltDir))
                                    {
                                        Directory.Delete(invalidRemoteDoltDir, true);
                                        _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Cleaned up invalid remote setup");
                                    }
                                }
                                catch (Exception cleanupEx)
                                {
                                    _logger.LogWarning(cleanupEx, $"[DoltCloneTool.DoltClone] Failed to clean up after invalid remote detection: {cleanupEx.Message}");
                                }
                                
                                // Return immediate error without proceeding to clone
                                return new
                                {
                                    success = false,
                                    error = "INVALID_REMOTE_URL",
                                    message = $"Invalid remote URL: '{formattedUrl}'. {remoteResult.Error}",
                                    attempted_url = formattedUrl,
                                    validation_error = remoteResult.Error,
                                    suggestion = formattedUrl.Contains("www.dolthub.com/repositories/") ? 
                                        $"Try using the correct DoltHub URL format: 'https://doltremoteapi.dolthub.com/{ExtractUsernameAndRepo(formattedUrl)}' or shorthand '{ExtractUsernameAndRepo(formattedUrl)}'" :
                                        "Please verify the remote URL format and ensure the repository exists and is accessible."
                                };
                            }
                        }
                        
                        if (remoteResult.Success)
                        {
                            _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Remote 'origin' configured successfully");
                            
                            // Create initial commit to make repository fully functional
                            fallbackStep = "creating initial commit";
                            _logger.LogInformation($"[DoltCloneTool.DoltClone] Fallback step: {fallbackStep}");
                            
                            try
                            {
                                // Create a temporary table to enable initial commit
                                await _doltCli.ExecuteAsync("CREATE TABLE IF NOT EXISTS __init_temp__ (id INT PRIMARY KEY, created_at DATETIME DEFAULT CURRENT_TIMESTAMP)");
                                _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Created temporary initialization table");
                                
                                // Add and commit the table
                                var addResult = await _doltCli.AddAllAsync();
                                _logger.LogInformation($"[DoltCloneTool.DoltClone] Add result - Success: {addResult.Success}, Error: '{addResult.Error}'");
                                
                                if (!addResult.Success)
                                {
                                    throw new Exception($"Failed to stage initial table: {addResult.Error}");
                                }
                                
                                var commitResult = await _doltCli.CommitAsync("Initial empty repository commit");
                                _logger.LogInformation($"[DoltCloneTool.DoltClone] Initial commit result - Success: {commitResult.Success}, Hash: '{commitResult.CommitHash}', Message: '{commitResult.Message}'");
                                
                                if (!commitResult.Success)
                                {
                                    throw new Exception($"Failed to create initial commit: {commitResult.Message}");
                                }
                                
                                // Clean up the temporary table
                                await _doltCli.ExecuteAsync("DROP TABLE __init_temp__");
                                _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Dropped temporary initialization table");
                                
                                // Add and commit the cleanup
                                var cleanupAddResult = await _doltCli.AddAllAsync();
                                if (cleanupAddResult.Success)
                                {
                                    var cleanupCommitResult = await _doltCli.CommitAsync("Cleaned up initialization table");
                                    _logger.LogInformation($"[DoltCloneTool.DoltClone] Cleanup commit result - Success: {cleanupCommitResult.Success}, Hash: '{cleanupCommitResult.CommitHash}'");
                                }
                                
                                // Set up branch tracking (optional step - some Dolt versions may not support this)
                                fallbackStep = "setting up branch tracking";
                                _logger.LogInformation($"[DoltCloneTool.DoltClone] Fallback step: {fallbackStep}");
                                
                                try
                                {
                                    var activeBranch = await _doltCli.GetCurrentBranchAsync();
                                    _logger.LogInformation($"[DoltCloneTool.DoltClone] Current branch: {activeBranch}");
                                    
                                    // Note: Some versions of Dolt may not support branch tracking setup
                                    // This is optional and won't fail the entire operation if it doesn't work
                                }
                                catch (Exception trackingEx)
                                {
                                    _logger.LogInformation($"[DoltCloneTool.DoltClone] Branch tracking setup not available: {trackingEx.Message}");
                                    // This is non-critical, continue without branch tracking
                                }
                                
                                remoteConfigured = true;
                                _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Fallback successful: Repository initialized with initial commits and remote 'origin' configured");
                                
                                // Create the required database schema for sync operations
                                await CreateSyncDatabaseSchemaAsync();
                                
                                // PP13-69 Phase 3: Initialize sync state in SQLite (not Dolt)
                                await InitializeSyncStateInSqliteAsync();
                            }
                            catch (Exception commitEx)
                            {
                                _logger.LogError(commitEx, $"[DoltCloneTool.DoltClone] ❌ Failed to create initial commit: {commitEx.Message}");
                                throw new Exception($"Failed to create initial commit: {commitEx.Message}", commitEx);
                            }
                            
                            // Verify remote was actually added and repository state is correct
                            try
                            {
                                var remotesCheck = await _doltCli.ListRemotesAsync();
                                var remotesList = remotesCheck?.ToList() ?? new List<RemoteInfo>();
                                _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Verification: Found {remotesList.Count} remotes configured");
                                foreach (var remote in remotesList)
                                {
                                    _logger.LogInformation($"[DoltCloneTool.DoltClone] Remote: {remote.Name} -> {remote.Url}");
                                }
                                
                                // Verify we have commits
                                var headCommit = await _doltCli.GetHeadCommitHashAsync();
                                _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Repository has HEAD commit: {headCommit}");
                            }
                            catch (Exception verifyEx)
                            {
                                _logger.LogWarning(verifyEx, $"[DoltCloneTool.DoltClone] Could not verify repository state: {verifyEx.Message}");
                            }
                        }
                        else
                        {
                            _logger.LogError($"[DoltCloneTool.DoltClone] ❌ Failed to configure remote at step '{fallbackStep}': {remoteResult.Error}");
                        }
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, $"[DoltCloneTool.DoltClone] ❌ Fallback initialization failed at step '{fallbackStep}': {fallbackEx.Message}");
                        
                        // Return specific fallback failure error
                        return new
                        {
                            success = false,
                            error = "FALLBACK_FAILED",
                            message = $"Repository is empty at '{formattedUrl}'. Attempted fallback initialization but failed at step '{fallbackStep}': {fallbackEx.Message}",
                            attempted_url = formattedUrl,
                            fallback_step = fallbackStep,
                            original_clone_error = cloneResult.Error
                        };
                    }
                    
                    // Log final fallback status
                    _logger.LogInformation($"[DoltCloneTool.DoltClone] Fallback completion status - Remote configured: {remoteConfigured}");
                }
                
                // If not an empty repo error, or fallback didn't succeed, return error
                if (!remoteConfigured)
                {
                    _logger.LogWarning($"[DoltCloneTool.DoltClone] Final error handling - Empty repo error: {isEmptyRepoError}, Remote configured: {remoteConfigured}");
                    
                    if (isEmptyRepoError)
                    {
                        // This should have been handled by fallback logic above, but something went wrong
                        _logger.LogError($"[DoltCloneTool.DoltClone] ❌ Empty repository detected but fallback did not succeed");
                        return new
                        {
                            success = false,
                            error = "FALLBACK_INCOMPLETE",
                            message = $"Repository is empty at '{formattedUrl}'. Fallback was attempted but remote configuration was not completed successfully.",
                            attempted_url = formattedUrl,
                            original_clone_error = cloneResult.Error
                        };
                    }
                    else
                    {
                        // Determine specific error type for non-empty repo failures
                        string errorCode = "CLONE_FAILED";
                        string errorMessage = cloneResult.Error ?? "Failed to clone repository";
                        
                        if (errorMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase))
                        {
                            errorCode = "AUTHENTICATION_FAILED";
                        }
                        else if (errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase))
                        {
                            errorCode = "REMOTE_NOT_FOUND";
                            errorMessage = $"Repository not found at '{formattedUrl}'";
                        }
                        else if (errorMessage.Contains("Invalid repository ID", StringComparison.OrdinalIgnoreCase))
                        {
                            errorCode = "INVALID_URL";
                            errorMessage = $"Invalid repository URL format: '{formattedUrl}'";
                        }
                        
                        _logger.LogInformation($"[DoltCloneTool.DoltClone] Returning non-empty repository error: {errorCode}");
                        
                        return new
                        {
                            success = false,
                            error = errorCode,
                            message = errorMessage,
                            attempted_url = formattedUrl,
                            clone_error = cloneResult.Error
                        };
                    }
                }
            }
            else
            {
                remoteConfigured = true; // Clone succeeded, remote should be set automatically
                _logger.LogInformation($"[DoltCloneTool.DoltClone] Clone succeeded from '{formattedUrl}'");
                
                // If a specific branch was requested, checkout to it after successful clone
                if (!string.IsNullOrEmpty(branch))
                {
                    _logger.LogInformation($"[DoltCloneTool.DoltClone] Checking out to requested branch: '{branch}'");
                    try
                    {
                        var checkoutResult = await _doltCli.CheckoutAsync(branch, false);
                        if (checkoutResult.Success)
                        {
                            _logger.LogInformation($"[DoltCloneTool.DoltClone] ✓ Successfully checked out to branch '{branch}'");
                        }
                        else
                        {
                            _logger.LogWarning($"[DoltCloneTool.DoltClone] Failed to checkout to branch '{branch}': {checkoutResult.Error}");
                            // Don't fail the entire operation if branch checkout fails - repository is still usable
                        }
                    }
                    catch (Exception branchEx)
                    {
                        _logger.LogWarning(branchEx, $"[DoltCloneTool.DoltClone] Exception checking out to branch '{branch}'");
                        // Don't fail the entire operation if branch checkout fails
                    }
                }
            }

            // Get current state after clone (handle empty repositories)
            string? currentBranch = null;
            string? currentCommitHash = null;
            
            try
            {
                currentBranch = await _doltCli.GetCurrentBranchAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DoltCloneTool.DoltClone] Failed to get current branch, likely empty repository. Defaulting to 'main'");
                currentBranch = branch ?? "main"; // Use requested branch or default to 'main'
            }
            
            try
            {
                currentCommitHash = await _doltCli.GetHeadCommitHashAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DoltCloneTool.DoltClone] Failed to get current commit hash, likely empty repository");
                currentCommitHash = null; // Empty repository has no commits
            }
            
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

            // Get commit info (handle empty repositories)
            IEnumerable<CommitInfo>? commits = null;
            CommitInfo? currentCommit = null;
            
            try
            {
                commits = await _doltCli.GetLogAsync(1);
                currentCommit = commits?.FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DoltCloneTool.DoltClone] Failed to get commit log, likely empty repository");
                // Empty repository has no commits, this is fine
            }

            // Ensure database is ready before sync (PP13-49 fix)
            _logger.LogInformation($"[DoltCloneTool.DoltClone] Ensuring database readiness before sync operation");
            bool isDatabaseReady = await EnsureDatabaseReadyAsync(maxRetries: 8, delayMs: 250);
            
            if (!isDatabaseReady)
            {
                _logger.LogWarning($"[DoltCloneTool.DoltClone] ⚠️ Database readiness timeout - proceeding with sync anyway");
            }

            // PP13-69 Phase 3: Initialize SQLite sync state tracking for successful clone
            await InitializeSyncStateInSqliteAsync();
            
            // Sync to ChromaDB
            int documentsLoaded = 0;
            List<string> collectionsCreated = new();
            bool syncSucceeded = false;
            string? syncError = null;
            
            try
            {
                // Perform full sync from Dolt to ChromaDB
                var syncResult = await _syncManager.FullSyncAsync();
                
                if (syncResult.Status == SyncStatusV2.Completed)
                {
                    syncSucceeded = true;
                    documentsLoaded = syncResult.Added + syncResult.Modified;
                    
                    // Try to discover actual collection names that were created
                    try
                    {
                        var doltCollections = await _doltCli.QueryAsync<dynamic>("SELECT DISTINCT collection_name FROM documents WHERE collection_name IS NOT NULL AND collection_name != ''");
                        foreach (var row in doltCollections)
                        {
                            if (row?.collection_name != null)
                            {
                                collectionsCreated.Add(row.collection_name.ToString());
                            }
                        }
                        
                        // If no collections found, use the branch name as fallback
                        if (!collectionsCreated.Any())
                        {
                            collectionsCreated.Add(currentBranch ?? "main");
                        }
                    }
                    catch
                    {
                        // Fallback to branch name if unable to query
                        collectionsCreated.Add(currentBranch ?? "main");
                    }
                    
                    _logger.LogInformation("Successfully synced {DocumentCount} documents to ChromaDB in {CollectionCount} collections", 
                        documentsLoaded, collectionsCreated.Count);
                }
                else
                {
                    syncError = syncResult.ErrorMessage ?? "Sync failed with unknown error";
                    _logger.LogError("Failed to sync to ChromaDB after clone: {Error}", syncError);
                }
            }
            catch (Exception ex)
            {
                syncError = ex.Message;
                _logger.LogError(ex, "Failed to sync to ChromaDB after clone");
            }

            string successMessage = !isCloneSuccessful
                ? $"Repository was empty at '{formattedUrl}'. Initialized local repository with initial commits and configured remote 'origin'. Repository is now ready for use."
                : syncSucceeded
                    ? $"Successfully cloned repository from '{formattedUrl}' and synced {documentsLoaded} documents to ChromaDB"
                    : $"Successfully cloned repository from '{formattedUrl}' but failed to sync to ChromaDB: {syncError}. Documents can be manually synced later.";

            // PP13-79-C1: Create/update manifest after successful clone
            await CreateOrUpdateManifestAfterCloneAsync(formattedUrl, currentBranch, currentCommitHash);

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, successMessage);
            return new
            {
                success = true,
                repository = new
                {
                    path = "./data/dolt-repo",
                    remote_url = formattedUrl
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
                    sync_succeeded = syncSucceeded,
                    documents_loaded = documentsLoaded,
                    collections_created = collectionsCreated.ToArray(),
                    sync_error = syncError
                },
                message = !isCloneSuccessful 
                    ? $"Repository was empty at '{formattedUrl}'. Initialized local repository with initial commits and configured remote 'origin'. Repository is now ready for use."
                    : currentCommit is null 
                        ? $"Successfully cloned empty repository from '{formattedUrl}'. Repository has no commits yet."
                        : syncSucceeded
                            ? $"Successfully cloned repository from '{formattedUrl}' and synced {documentsLoaded} documents to ChromaDB"
                            : $"Successfully cloned repository from '{formattedUrl}' but failed to sync to ChromaDB: {syncError}. Documents can be manually synced later."
            };
        }
        catch (Exception ex)
        {
            // Determine error type
            string errorCode = "OPERATION_FAILED";
            if (ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
                errorCode = "AUTHENTICATION_FAILED";
            else if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                errorCode = "REMOTE_NOT_FOUND";
            
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = errorCode,
                message = $"Failed to clone repository: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Creates the required database schema for sync operations after fallback initialization
    /// </summary>
    private async Task CreateSyncDatabaseSchemaAsync()
    {
        try
        {
            _logger.LogInformation("[DoltCloneTool.CreateSyncDatabaseSchemaAsync] Creating database schema for sync operations");

            // Create collections table
            var createCollectionsTableSql = @"
CREATE TABLE IF NOT EXISTS collections (
    collection_name VARCHAR(255) PRIMARY KEY,
    display_name VARCHAR(255),
    description TEXT,
    embedding_model VARCHAR(100) DEFAULT 'default',
    chunk_size INT DEFAULT 512,
    chunk_overlap INT DEFAULT 50,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    document_count INT DEFAULT 0,
    metadata JSON,
    
    INDEX idx_created_at (created_at),
    INDEX idx_updated_at (updated_at)
)";
            await _doltCli.ExecuteAsync(createCollectionsTableSql);
            _logger.LogInformation("[DoltCloneTool.CreateSyncDatabaseSchemaAsync] ✓ Created collections table");

            // Create documents table
            var createDocumentsTableSql = @"
CREATE TABLE IF NOT EXISTS documents (
    doc_id VARCHAR(64) NOT NULL,
    collection_name VARCHAR(255) NOT NULL,
    content LONGTEXT NOT NULL,
    content_hash CHAR(64) NOT NULL,
    title VARCHAR(500),
    doc_type VARCHAR(100),
    metadata JSON NOT NULL,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    
    PRIMARY KEY (doc_id, collection_name),
    FOREIGN KEY (collection_name) REFERENCES collections(collection_name) ON DELETE CASCADE,
    INDEX idx_content_hash (content_hash),
    INDEX idx_title (title),
    INDEX idx_doc_type (doc_type),
    INDEX idx_created_at (created_at),
    INDEX idx_updated_at (updated_at)
)";
            await _doltCli.ExecuteAsync(createDocumentsTableSql);
            _logger.LogInformation("[DoltCloneTool.CreateSyncDatabaseSchemaAsync] ✓ Created documents table");

            // Create chroma_sync_state table
            var createChromaSyncStateSql = @"
CREATE TABLE IF NOT EXISTS chroma_sync_state (
    collection_name VARCHAR(255) PRIMARY KEY,
    last_sync_commit VARCHAR(40),
    last_sync_at DATETIME,
    document_count INT DEFAULT 0,
    chunk_count INT DEFAULT 0,
    embedding_model VARCHAR(100),
    sync_status ENUM('synced', 'pending', 'error', 'in_progress', 'local_changes') DEFAULT 'pending',
    local_changes_count INT DEFAULT 0,
    error_message TEXT,
    metadata JSON,
    
    FOREIGN KEY (collection_name) REFERENCES collections(collection_name) ON DELETE CASCADE,
    INDEX idx_sync_status (sync_status),
    INDEX idx_last_sync_at (last_sync_at)
)";
            await _doltCli.ExecuteAsync(createChromaSyncStateSql);
            _logger.LogInformation("[DoltCloneTool.CreateSyncDatabaseSchemaAsync] ✓ Created chroma_sync_state table");

            // Create document_sync_log table
            var createDocumentSyncLogSql = @"
CREATE TABLE IF NOT EXISTS document_sync_log (
    id INT AUTO_INCREMENT PRIMARY KEY,
    doc_id VARCHAR(64) NOT NULL,
    collection_name VARCHAR(255) NOT NULL,
    content_hash CHAR(64) NOT NULL,
    chroma_chunk_ids JSON,
    synced_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    sync_direction ENUM('dolt_to_chroma', 'chroma_to_dolt') NOT NULL,
    sync_action ENUM('added', 'modified', 'deleted', 'staged') NOT NULL,
    embedding_model VARCHAR(100),
    
    UNIQUE KEY uk_doc_collection (doc_id, collection_name),
    FOREIGN KEY (collection_name) REFERENCES collections(collection_name) ON DELETE CASCADE,
    INDEX idx_content_hash (content_hash),
    INDEX idx_synced_at (synced_at),
    INDEX idx_sync_direction (sync_direction)
)";
            await _doltCli.ExecuteAsync(createDocumentSyncLogSql);
            _logger.LogInformation("[DoltCloneTool.CreateSyncDatabaseSchemaAsync] ✓ Created document_sync_log table");

            // Create local_changes table
            var createLocalChangesSql = @"
CREATE TABLE IF NOT EXISTS local_changes (
    doc_id VARCHAR(64) NOT NULL,
    collection_name VARCHAR(255) NOT NULL,
    change_type ENUM('new', 'modified', 'deleted') NOT NULL,
    detected_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    content_hash_chroma CHAR(64),
    content_hash_dolt CHAR(64),
    metadata JSON,
    
    PRIMARY KEY (doc_id, collection_name),
    FOREIGN KEY (collection_name) REFERENCES collections(collection_name) ON DELETE CASCADE,
    INDEX idx_change_type (change_type),
    INDEX idx_detected_at (detected_at)
)";
            await _doltCli.ExecuteAsync(createLocalChangesSql);
            _logger.LogInformation("[DoltCloneTool.CreateSyncDatabaseSchemaAsync] ✓ Created local_changes table");

            // Create sync_operations table (optional but useful for auditing)
            var createSyncOperationsSql = @"
CREATE TABLE IF NOT EXISTS sync_operations (
    operation_id INT AUTO_INCREMENT PRIMARY KEY,
    operation_type ENUM('init', 'commit', 'push', 'pull', 'merge', 'checkout', 'reset', 'stage') NOT NULL,
    dolt_branch VARCHAR(255) NOT NULL,
    dolt_commit_before VARCHAR(40),
    dolt_commit_after VARCHAR(40),
    chroma_collections_affected JSON,
    documents_added INT DEFAULT 0,
    documents_modified INT DEFAULT 0,
    documents_deleted INT DEFAULT 0,
    documents_staged INT DEFAULT 0,
    chunks_processed INT DEFAULT 0,
    operation_status ENUM('started', 'completed', 'failed', 'rolled_back', 'blocked') NOT NULL,
    blocked_reason VARCHAR(255),
    error_message TEXT,
    started_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    completed_at DATETIME,
    metadata JSON,
    
    INDEX idx_operation_type (operation_type),
    INDEX idx_operation_status (operation_status),
    INDEX idx_started_at (started_at),
    INDEX idx_branch (dolt_branch)
)";
            await _doltCli.ExecuteAsync(createSyncOperationsSql);
            _logger.LogInformation("[DoltCloneTool.CreateSyncDatabaseSchemaAsync] ✓ Created sync_operations table");

            // Stage and commit the schema
            var addSchemaResult = await _doltCli.AddAllAsync();
            if (!addSchemaResult.Success)
            {
                _logger.LogWarning($"[DoltCloneTool.CreateSyncDatabaseSchemaAsync] Failed to stage schema tables: {addSchemaResult.Error}");
            }
            else
            {
                var commitSchemaResult = await _doltCli.CommitAsync("Create database schema for sync operations");
                if (commitSchemaResult.Success)
                {
                    _logger.LogInformation($"[DoltCloneTool.CreateSyncDatabaseSchemaAsync] ✓ Schema committed successfully: {commitSchemaResult.CommitHash}");
                }
                else
                {
                    _logger.LogWarning($"[DoltCloneTool.CreateSyncDatabaseSchemaAsync] Failed to commit schema: {commitSchemaResult.Message}");
                }
            }

            _logger.LogInformation("[DoltCloneTool.CreateSyncDatabaseSchemaAsync] ✓ Database schema creation completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[DoltCloneTool.CreateSyncDatabaseSchemaAsync] ❌ Failed to create database schema: {ex.Message}");
            throw new Exception($"Failed to create database schema: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extracts username/repository from DoltHub URLs for suggestion purposes
    /// </summary>
    /// <param name="url">The DoltHub URL to parse</param>
    /// <returns>Username/repository format if extractable, otherwise the original URL</returns>
    private string ExtractUsernameAndRepo(string url)
    {
        try
        {
            // Handle various DoltHub URL formats
            if (url.Contains("www.dolthub.com/repositories/"))
            {
                // Format: https://www.dolthub.com/repositories/username/database-name
                var parts = url.Split('/');
                if (parts.Length >= 6)
                {
                    var username = parts[4];
                    var repoName = parts[5];
                    return $"{username}/{repoName}";
                }
            }
            else if (url.Contains("www.dolthub.com/") && !url.Contains("/repositories/"))
            {
                // Format: https://www.dolthub.com/username/database-name
                var parts = url.Split('/');
                if (parts.Length >= 5)
                {
                    var username = parts[3];
                    var repoName = parts[4];
                    return $"{username}/{repoName}";
                }
            }
            
            return url; // Return original if parsing fails
        }
        catch
        {
            return url; // Return original if any parsing error occurs
        }
    }

    /// <summary>
    /// PP13-69 Phase 3: Initializes sync state in SQLite (not Dolt) for proper SyncManagerV2 integration
    /// </summary>
    private async Task InitializeSyncStateInSqliteAsync()
    {
        try
        {
            _logger.LogInformation("[DoltCloneTool.InitializeSyncStateInSqliteAsync] PP13-69 Phase 3: Initializing sync state in SQLite for repository");

            // Get the current commit hash for tracking
            string? currentCommit = null;
            try
            {
                currentCommit = await _doltCli.GetHeadCommitHashAsync();
                _logger.LogInformation($"[DoltCloneTool.InitializeSyncStateInSqliteAsync] Current commit hash: {currentCommit}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DoltCloneTool.InitializeSyncStateInSqliteAsync] Could not get current commit hash, proceeding without it");
            }

            // Create a default collection entry if none exists (ensures SyncManager can track operations)
            var initializeCollectionSql = @"
INSERT IGNORE INTO collections (
    collection_name,
    display_name,
    description,
    embedding_model,
    chunk_size,
    chunk_overlap,
    document_count,
    metadata
) VALUES (
    'default',
    'Default Collection',
    'Default collection created during repository initialization',
    'default',
    512,
    50,
    0,
    JSON_OBJECT('created_by', 'fallback_initialization')
)";
            await _doltCli.ExecuteAsync(initializeCollectionSql);
            _logger.LogInformation("[DoltCloneTool.InitializeSyncStateAsync] ✓ Created default collection entry");

            // Initialize sync state for the default collection
            var initializeSyncStateSql = $@"
INSERT IGNORE INTO chroma_sync_state (
    collection_name,
    last_sync_commit,
    last_sync_at,
    document_count,
    chunk_count,
    embedding_model,
    sync_status,
    local_changes_count,
    metadata
) VALUES (
    'default',
    {(currentCommit != null ? $"'{currentCommit}'" : "NULL")},
    CURRENT_TIMESTAMP,
    0,
    0,
    'default',
    'synced',
    0,
    JSON_OBJECT('initialized_by', 'fallback_initialization', 'repository_type', 'empty_clone')
)";
            await _doltCli.ExecuteAsync(initializeSyncStateSql);
            // PP13-69 Phase 3: Use SQLite sync state tracker instead of Dolt tables
            var repoPath = _repositoryPath;
            var defaultCollection = "default";
            var currentBranch = "main";
            
            try
            {
                // Initialize SQLite sync state tracker
                await _syncStateTracker.InitializeAsync(repoPath);
                _logger.LogInformation($"[DoltCloneTool.InitializeSyncStateInSqliteAsync] ✓ Initialized SQLite sync state tracker");
                
                // Create sync state record for default collection
                var syncStateRecord = new SyncStateRecord(repoPath, defaultCollection, currentBranch)
                    .WithSyncUpdate(currentCommit, 0, 0, "default");
                    
                await _syncStateTracker.UpdateSyncStateAsync(repoPath, defaultCollection, syncStateRecord);
                _logger.LogInformation($"[DoltCloneTool.InitializeSyncStateInSqliteAsync] ✓ Created sync state record in SQLite for collection '{defaultCollection}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[DoltCloneTool.InitializeSyncStateInSqliteAsync] Failed to initialize SQLite sync state: {ex.Message}");
                // Continue without failing the entire operation
            }

            // Record the initialization operation in sync_operations
            var recordOperationSql = $@"
INSERT INTO sync_operations (
    operation_type,
    dolt_branch,
    dolt_commit_after,
    chroma_collections_affected,
    documents_added,
    documents_modified,
    documents_deleted,
    chunks_processed,
    operation_status,
    metadata
) VALUES (
    'init',
    'main',
    {(currentCommit != null ? $"'{currentCommit}'" : "NULL")},
    '[""default""]',
    0,
    0,
    0,
    0,
    'completed',
    JSON_OBJECT('operation', 'fallback_initialization', 'schema_created', true, 'timestamp', NOW())
)";
            await _doltCli.ExecuteAsync(recordOperationSql);
            _logger.LogInformation("[DoltCloneTool.InitializeSyncStateAsync] ✓ Recorded initialization operation in audit log");

            // Stage and commit the sync state initialization
            var addSyncStateResult = await _doltCli.AddAllAsync();
            if (!addSyncStateResult.Success)
            {
                _logger.LogWarning($"[DoltCloneTool.InitializeSyncStateAsync] Failed to stage sync state initialization: {addSyncStateResult.Error}");
            }
            else
            {
                var commitSyncStateResult = await _doltCli.CommitAsync("Initialize sync state for empty repository");
                if (commitSyncStateResult.Success)
                {
                    _logger.LogInformation($"[DoltCloneTool.InitializeSyncStateAsync] ✓ Sync state initialization committed successfully: {commitSyncStateResult.CommitHash}");
                }
                else
                {
                    _logger.LogWarning($"[DoltCloneTool.InitializeSyncStateAsync] Failed to commit sync state initialization: {commitSyncStateResult.Message}");
                }
            }

            _logger.LogInformation("[DoltCloneTool.InitializeSyncStateAsync] ✓ Sync state initialization completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[DoltCloneTool.InitializeSyncStateAsync] ❌ Failed to initialize sync state: {ex.Message}");
            throw new Exception($"Failed to initialize sync state: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Ensures the Dolt database is fully ready for queries after clone completion.
    /// This addresses the PP13-49 timing issue where clone returns success before 
    /// the database is fully queryable, causing GetAvailableCollectionNamesAsync to return 0 collections.
    /// </summary>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="delayMs">Delay between attempts in milliseconds</param>
    /// <returns>True if database is ready, false if timeout reached</returns>
    private async Task<bool> EnsureDatabaseReadyAsync(int maxRetries = 5, int delayMs = 500)
    {
        _logger.LogInformation("[DoltCloneTool.EnsureDatabaseReadyAsync] 🔍 Starting database readiness verification after clone");
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] Attempt {Attempt}/{MaxRetries} - Testing database readiness", 
                    attempt, maxRetries);

                // Test 1: Verify basic database connectivity
                _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] Test 1: Basic connectivity");
                var tablesResult = await _doltCli.QueryAsync<dynamic>("SHOW TABLES");
                var tablesList = tablesResult.ToList();
                _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] ✓ Database responsive, found {Count} tables", tablesList.Count);
                
                // Log all table names for debugging  
                foreach (var table in tablesList)
                {
                    var tableName = GetTableNameFromResult(table);
                    _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] Found table: '{TableName}'", (object)(tableName ?? "unknown"));
                    
                    // Enhanced diagnostics for table structure investigation
                    _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] DIAGNOSTICS - Table object type: {Type}", (object)(table?.GetType()?.FullName ?? "null"));
                    
                    // Log all properties and their values - avoid dynamic null comparison with JsonElement
                    bool shouldLogProperties = false;
                    try
                    {
                        if (table is System.Text.Json.JsonElement jsonElement)
                        {
                            shouldLogProperties = jsonElement.ValueKind != System.Text.Json.JsonValueKind.Null && 
                                                jsonElement.ValueKind != System.Text.Json.JsonValueKind.Undefined;
                        }
                        else
                        {
                            shouldLogProperties = table != null;
                        }
                    }
                    catch
                    {
                        shouldLogProperties = false;
                    }
                    
                    if (shouldLogProperties)
                    {
                        var tableType = table.GetType();
                        foreach (var prop in tableType.GetProperties())
                        {
                            try
                            {
                                var value = prop.GetValue(table);
                                _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] DIAGNOSTICS - Property '{PropertyName}' = '{PropertyValue}'", 
                                    (object)prop.Name, (object)(value?.ToString() ?? "null"));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] DIAGNOSTICS - Property '{PropertyName}' failed: {Error}", 
                                    (object)prop.Name, (object)ex.Message);
                            }
                        }
                    }
                }

                // Test 2: Verify documents table exists and is queryable
                _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] Test 2: Documents table accessibility");
                bool documentsTableExists = tablesList.Any(table => 
                {
                    // Handle different possible column names for table listing
                    var tableName = GetTableNameFromResult(table);
                    return "documents".Equals(tableName, StringComparison.OrdinalIgnoreCase);
                });

                if (documentsTableExists)
                {
                    // Test 3: Verify we can query the documents table structure and content
                    _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] Test 3: Documents table query test");
                    var documentsCount = await _doltCli.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM documents");
                    _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] ✓ Documents table queryable, contains {Count} documents", documentsCount);

                    // Test 4: Verify collection discovery query works (the exact query that was failing)
                    _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] Test 4: Collection discovery query test");
                    var collectionsResult = await _doltCli.QueryAsync<dynamic>(
                        "SELECT DISTINCT collection_name FROM documents WHERE collection_name IS NOT NULL AND collection_name != ''");
                    var collections = collectionsResult.ToList();
                    _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] ✓ Collection discovery query successful, found {Count} collections", collections.Count);

                    foreach (var collection in collections)
                    {
                        var collectionName = GetCollectionNameFromResult(collection);
                        _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] Found collection: {Collection}", (object)(collectionName ?? "unknown"));
                    }

                    _logger.LogInformation("[DoltCloneTool.EnsureDatabaseReadyAsync] ✅ Database readiness verified on attempt {Attempt}/{MaxRetries}", 
                        attempt, maxRetries);
                    return true;
                }
                else
                {
                    _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] Documents table not found yet, attempt {Attempt}/{MaxRetries}", 
                        attempt, maxRetries);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DoltCloneTool.EnsureDatabaseReadyAsync] Database not ready yet on attempt {Attempt}/{MaxRetries}: {Error}", 
                    attempt, maxRetries, ex.Message);
            }

            // Wait before next attempt (unless this was the last attempt)
            if (attempt < maxRetries)
            {
                _logger.LogDebug("[DoltCloneTool.EnsureDatabaseReadyAsync] Waiting {Delay}ms before next attempt", delayMs);
                await Task.Delay(delayMs);
            }
        }

        _logger.LogWarning("[DoltCloneTool.EnsureDatabaseReadyAsync] ⚠️ Database readiness timeout after {MaxRetries} attempts", maxRetries);
        return false;
    }

    /// <summary>
    /// Helper method to extract table name from dynamic query result
    /// PP13-49-C1: Enhanced to handle System.Text.Json.JsonElement objects properly
    /// </summary>
    private static string? GetTableNameFromResult(dynamic tableRow)
    {
        try
        {
            // PP13-49-C1 FIX: Handle JsonElement objects properly
            if (tableRow is System.Text.Json.JsonElement jsonElement)
            {
                return GetTableNameFromJsonElement(jsonElement);
            }
            
            // Legacy fallback for other dynamic object types
            return GetTableNameFromDynamicObject(tableRow);
        }
        catch (Exception ex)
        {
            // Enhanced error logging for debugging
            Console.WriteLine($"[GetTableNameFromResult] Error extracting table name: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract table name from JsonElement object (SHOW TABLES returns JsonElement via DoltCli.QueryAsync)
    /// PP13-49-C1: Root cause fix for JsonElement handling
    /// </summary>
    private static string? GetTableNameFromJsonElement(System.Text.Json.JsonElement jsonElement)
    {
        try
        {
            // JsonElement requires specific property access methods
            // The SHOW TABLES command returns objects like: {"Tables_in_database-name": "table_name"}
            
            // First, try to enumerate all properties to find Tables_in_* pattern
            if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var property in jsonElement.EnumerateObject())
                {
                    // Check for Tables_in_* pattern (this is the standard SHOW TABLES output format)
                    if (property.Name.StartsWith("Tables_in_", StringComparison.OrdinalIgnoreCase))
                    {
                        var tableName = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(tableName))
                        {
                            return tableName;
                        }
                    }
                }
                
                // Fallback: try common property names
                string[] commonPropertyNames = { "Table", "table_name", "name", "Name" };
                foreach (var propName in commonPropertyNames)
                {
                    if (jsonElement.TryGetProperty(propName, out var property))
                    {
                        var tableName = property.GetString();
                        if (!string.IsNullOrWhiteSpace(tableName))
                        {
                            return tableName;
                        }
                    }
                }
                
                // Final fallback: get the first string property value
                foreach (var property in jsonElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var tableName = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(tableName))
                        {
                            return tableName;
                        }
                    }
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetTableNameFromJsonElement] Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Legacy method for non-JsonElement dynamic objects
    /// </summary>
    private static string? GetTableNameFromDynamicObject(dynamic tableRow)
    {
        try
        {
            // Try different possible column names from SHOW TABLES output
            // The column name format is "Tables_in_{database_name}" but C# property names can't have hyphens
            // So we need to check all properties and find the one that starts with "Tables_in_"
            
            // First try the common cases
            var result = tableRow?.Tables_in_dolt_repo?.ToString() ??
                        tableRow?.Tables_in_dolt_repo_10?.ToString() ?? 
                        tableRow?.Table?.ToString();
            
            if (result != null)
                return result;
                
            // If that fails, examine all properties to find Tables_in_* pattern
            var type = tableRow?.GetType();
            if (type != null)
            {
                foreach (var property in type.GetProperties())
                {
                    if (property.Name.StartsWith("Tables_in_", StringComparison.OrdinalIgnoreCase))
                    {
                        return property.GetValue(tableRow)?.ToString();
                    }
                }
                
                // Final fallback - get the first property value
                var firstProperty = type.GetProperties().FirstOrDefault();
                return firstProperty?.GetValue(tableRow)?.ToString();
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetTableNameFromDynamicObject] Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Helper method to extract collection name from dynamic query result
    /// PP13-49-C1: Enhanced to handle System.Text.Json.JsonElement objects properly
    /// </summary>
    private static string? GetCollectionNameFromResult(dynamic collectionRow)
    {
        try
        {
            // PP13-49-C1 FIX: Handle JsonElement objects properly
            if (collectionRow is System.Text.Json.JsonElement jsonElement)
            {
                return GetCollectionNameFromJsonElement(jsonElement);
            }
            
            // Legacy fallback for other dynamic object types
            return collectionRow?.collection_name?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract collection name from JsonElement object (collection queries return JsonElement)
    /// PP13-49-C1: Root cause fix for JsonElement handling in collection discovery
    /// </summary>
    private static string? GetCollectionNameFromJsonElement(System.Text.Json.JsonElement jsonElement)
    {
        try
        {
            if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // Try to get the collection_name property
                if (jsonElement.TryGetProperty("collection_name", out var collectionNameProperty))
                {
                    return collectionNameProperty.GetString();
                }
                
                // Fallback: try other possible property names
                string[] possibleNames = { "name", "Name", "collectionName", "collection" };
                foreach (var propName in possibleNames)
                {
                    if (jsonElement.TryGetProperty(propName, out var property))
                    {
                        return property.GetString();
                    }
                }
                
                // Final fallback: get the first string property value
                foreach (var property in jsonElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }
                }
            }
            
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// PP13-79-C1: Creates or updates the manifest after successful clone
    /// Sets remote_url, current_branch, and current_commit
    /// </summary>
    private async Task CreateOrUpdateManifestAfterCloneAsync(string remoteUrl, string? branch, string? commitHash)
    {
        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCloneTool), "PP13-79-C1: Creating/updating manifest after clone...");

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
                    RemoteUrl = remoteUrl,
                    CurrentCommit = commitHash,
                    CurrentBranch = branch ?? "main",
                    DefaultBranch = branch ?? "main"
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
                    defaultBranch: branch ?? "main",
                    initMode: "auto"
                );

                // Update with current state
                var doltWithState = newManifest.Dolt with
                {
                    CurrentCommit = commitHash,
                    CurrentBranch = branch ?? "main"
                };

                newManifest = newManifest with
                {
                    Dolt = doltWithState,
                    UpdatedAt = DateTime.UtcNow
                };

                await _manifestService.WriteManifestAsync(projectRoot, newManifest);
            }

            _syncStateChecker.InvalidateCache();

            ToolLoggingUtility.LogToolInfo(_logger, nameof(DoltCloneTool),
                $"✅ PP13-79-C1: Manifest created/updated with remote: {remoteUrl}, branch: {branch ?? "main"}");
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolWarning(_logger, nameof(DoltCloneTool), $"⚠️ PP13-79-C1: Failed to create/update manifest: {ex.Message}");
        }
    }
}