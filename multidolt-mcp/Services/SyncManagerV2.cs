using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DMMS.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DMMS.Services
{
    /// <summary>
    /// V2 Sync Manager with bidirectional synchronization between Dolt and ChromaDB.
    /// Treats ChromaDB as working copy and Dolt as version control repository.
    /// </summary>
    public class SyncManagerV2 : ISyncManagerV2
    {
        private readonly IDoltCli _dolt;
        private readonly IChromaDbService _chromaService;
        private readonly DeltaDetectorV2 _deltaDetector;
        private readonly ChromaToDoltDetector _chromaToDoltDetector;
        private readonly ChromaToDoltSyncer _chromaToDoltSyncer;
        private readonly IDeletionTracker _deletionTracker;
        private readonly DoltConfiguration _doltConfig;
        private readonly ILogger<SyncManagerV2> _logger;

        public SyncManagerV2(
            IDoltCli dolt,
            IChromaDbService chromaService,
            IDeletionTracker deletionTracker,
            IOptions<DoltConfiguration> doltConfig,
            ILogger<SyncManagerV2> logger)
        {
            _dolt = dolt;
            _chromaService = chromaService;
            _deletionTracker = deletionTracker;
            _doltConfig = doltConfig.Value;
            _logger = logger;
            
            // Initialize detectors and syncers
            _deltaDetector = new DeltaDetectorV2(dolt, logger: null);
            _chromaToDoltDetector = new ChromaToDoltDetector(chromaService, dolt, deletionTracker, doltConfig, logger: null);
            
            // Create a logger for ChromaToDoltSyncer so we can debug the document retrieval issue
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var syncerLogger = loggerFactory.CreateLogger<ChromaToDoltSyncer>();
            _chromaToDoltSyncer = new ChromaToDoltSyncer(chromaService, dolt, _chromaToDoltDetector, logger: syncerLogger);
        }

        #region Initialization

        public async Task<InitResult> InitializeVersionControlAsync(
            string collectionName, 
            string initialCommitMessage = "Initial import from ChromaDB")
        {
            _logger.LogInformation("Initializing version control for collection {Collection}", collectionName);

            try
            {
                // Use ChromaToDoltSyncer to import from ChromaDB
                var repositoryPath = "./"; // Use current directory as repository path
                var result = await _chromaToDoltSyncer.InitializeFromChromaAsync(
                    collectionName, repositoryPath, initialCommitMessage);

                if (result.Success)
                {
                    _logger.LogInformation("Successfully initialized version control for {Collection} with {Count} documents",
                        collectionName, result.DocumentsImported);
                }
                else
                {
                    _logger.LogError("Failed to initialize version control: {Error}", result.ErrorMessage);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize version control for collection {Collection}", collectionName);
                return new InitResult(InitStatus.Failed, 0, null, ex.Message);
            }
        }

        #endregion

        #region Status Operations

        public async Task<StatusSummary> GetStatusAsync()
        {
            try
            {
                var branch = await _dolt.GetCurrentBranchAsync();
                var commit = await _dolt.GetHeadCommitHashAsync();
                var collections = await _chromaService.ListCollectionsAsync();
                var currentCollection = collections.FirstOrDefault() ?? "default";
                
                // Check for uncommitted Dolt changes
                var doltStatus = await _dolt.GetStatusAsync();
                var hasUncommittedDolt = doltStatus.HasStagedChanges || doltStatus.HasUnstagedChanges;
                
                // Check for local ChromaDB changes
                var localChanges = await _chromaToDoltDetector.DetectLocalChangesAsync(currentCollection);
                var hasUncommittedChroma = localChanges.HasChanges;

                return new StatusSummary
                {
                    Branch = branch,
                    CurrentCommit = commit,
                    CollectionName = currentCollection,
                    LocalChanges = localChanges,
                    HasUncommittedDoltChanges = hasUncommittedDolt,
                    HasUncommittedChromaChanges = hasUncommittedChroma
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get status");
                throw;
            }
        }

        public async Task<LocalChanges> GetLocalChangesAsync()
        {
            try
            {
                // Log Python.NET queue state before starting operation
                var queueStats = PythonContext.GetQueueStats();
                _logger.LogInformation(
                    "GetLocalChangesAsync starting - Python.NET queue size: {QueueSize}, over threshold: {OverThreshold}",
                    queueStats.QueueSize, queueStats.IsOverThreshold);
                
                var collections = await _chromaService.ListCollectionsAsync();
                
                if (!collections.Any())
                {
                    _logger.LogInformation("No collections found, checking default collection");
                    return await _chromaToDoltDetector.DetectLocalChangesAsync("default");
                }
                
                if (collections.Count == 1)
                {
                    _logger.LogInformation("Single collection found: {Collection}", collections[0]);
                    _logger.LogInformation("GetLocalChangesAsync: Using detector instance {DetectorHash} for collection {Collection}", 
                        _chromaToDoltDetector.GetHashCode(), collections[0]);
                    
                    var singleResult = await _chromaToDoltDetector.DetectLocalChangesAsync(collections[0]);
                    _logger.LogInformation("GetLocalChangesAsync: Detection completed for {Collection} - found {TotalChanges} changes", 
                        collections[0], singleResult.TotalChanges);
                    
                    return singleResult;
                }
                
                // Multiple collections - use async batching to prevent Python.NET queue saturation
                _logger.LogInformation("Multiple collections found ({Count}), using async batch processing to prevent queue saturation", 
                    collections.Count);
                
                // Create collection check tasks with proper async isolation
                var collectionCheckTasks = collections.Select(collectionName => 
                    Task.Run(async () => 
                    {
                        try 
                        {
                            return await _chromaToDoltDetector.DetectLocalChangesAsync(collectionName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to detect changes in collection {Collection}, skipping", collectionName);
                            return new LocalChanges(
                                new List<ChromaDocument>(), 
                                new List<ChromaDocument>(), 
                                new List<DeletedDocumentV2>());
                        }
                    })
                ).ToList();
                
                // Execute all collection checks with timeout handling
                var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(45000));
                
                try
                {
                    var results = await Task.WhenAll(collectionCheckTasks).WaitAsync(timeoutCts.Token);
                    _logger.LogInformation("Successfully completed async batch processing for {Count} collections", collections.Count);
                
                    // Aggregate all changes from all collections
                    var allNewDocs = new List<ChromaDocument>();
                    var allModifiedDocs = new List<ChromaDocument>();
                    var allDeletedDocs = new List<DeletedDocumentV2>();
                    
                    foreach (var collectionChanges in results)
                    {
                        allNewDocs.AddRange(collectionChanges.NewDocuments);
                        allModifiedDocs.AddRange(collectionChanges.ModifiedDocuments);
                        allDeletedDocs.AddRange(collectionChanges.DeletedDocuments);
                    }
                    
                    var totalChanges = allNewDocs.Count + allModifiedDocs.Count + allDeletedDocs.Count;
                    _logger.LogInformation(
                        "GetLocalChangesAsync completed - found {TotalChanges} changes across {Collections} collections ({New} new, {Modified} modified, {Deleted} deleted)",
                        totalChanges, collections.Count, allNewDocs.Count, allModifiedDocs.Count, allDeletedDocs.Count);
                    
                    return new LocalChanges(allNewDocs, allModifiedDocs, allDeletedDocs);
                }
                catch (OperationCanceledException ex) when (timeoutCts.Token.IsCancellationRequested)
                {
                    _logger.LogError(ex, "GetLocalChangesAsync timed out after 45 seconds for {Count} collections", collections.Count);
                    throw new TimeoutException($"GetLocalChangesAsync timed out after 45 seconds for {collections.Count} collections", ex);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "GetLocalChangesAsync failed during batch processing for {Count} collections", collections.Count);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get local changes");
                return new LocalChanges(
                    new List<ChromaDocument>(), 
                    new List<ChromaDocument>(), 
                    new List<DeletedDocumentV2>());
            }
        }

        #endregion

        #region State Consistency

        /// <summary>
        /// Ensures Dolt working directory is in a clean state for reliable document operations.
        /// This addresses PP13-58: prevents document operations from reading inconsistent states
        /// where some operations read working directory and others read committed state.
        /// </summary>
        /// <param name="autoCommitUncommittedChanges">If true, commits any pending changes. If false, resets to clean state.</param>
        /// <returns>True if working directory is now clean, false if issues remain</returns>
        public async Task<bool> EnsureCleanWorkingDirectoryAsync(bool autoCommitUncommittedChanges = true)
        {
            try
            {
                _logger.LogDebug("Checking Dolt working directory status for state consistency");
                
                // Check current working directory status
                var status = await _dolt.GetStatusAsync();
                
                if (!status.HasUnstagedChanges && !status.HasStagedChanges)
                {
                    _logger.LogDebug("Working directory already clean - no state consistency issues");
                    return true;
                }
                
                _logger.LogInformation("Working directory has uncommitted changes - ensuring state consistency");
                _logger.LogDebug("Status: HasUnstagedChanges={Unstaged}, HasStagedChanges={Staged}", 
                    status.HasUnstagedChanges, status.HasStagedChanges);
                
                if (autoCommitUncommittedChanges)
                {
                    // Option A: Auto-commit pending changes to ensure they're not lost during reset operations
                    _logger.LogInformation("Auto-committing pending changes to ensure state consistency");
                    
                    // Stage any unstaged changes
                    if (status.HasUnstagedChanges)
                    {
                        await _dolt.AddAllAsync();
                        _logger.LogDebug("Staged all pending changes");
                    }
                    
                    // Commit staged changes  
                    if (status.HasStagedChanges || status.HasUnstagedChanges)
                    {
                        var commitMessage = "Auto-commit for working directory state consistency (PP13-58)";
                        var commitResult = await _dolt.CommitAsync(commitMessage);
                        
                        if (commitResult.Success)
                        {
                            _logger.LogInformation("Successfully auto-committed changes for state consistency");
                            return true;
                        }
                        else
                        {
                            _logger.LogWarning("Failed to auto-commit changes: {Error}", commitResult.Message);
                            // Fall back to reset approach
                        }
                    }
                }
                
                // Option B: Reset to clean committed state (fallback or by choice)
                _logger.LogInformation("Resetting working directory to clean committed state");
                
                try
                {
                    await _dolt.ResetHardAsync("HEAD");
                    _logger.LogInformation("Successfully reset working directory to clean state");
                    
                    // Verify the reset worked
                    var postResetStatus = await _dolt.GetStatusAsync();
                    if (!postResetStatus.HasUnstagedChanges && !postResetStatus.HasStagedChanges)
                    {
                        _logger.LogDebug("Verified working directory is now clean after reset");
                        return true;
                    }
                    else
                    {
                        _logger.LogWarning("Working directory still has changes after reset - may indicate persistent issues");
                        return false;
                    }
                }
                catch (Exception resetEx)
                {
                    _logger.LogError(resetEx, "Failed to reset working directory to clean state");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure clean working directory state");
                return false;
            }
        }

        #endregion

        #region Commit Processing

        public async Task<SyncResultV2> ProcessCommitAsync(
            string message, 
            bool autoStageFromChroma = true, 
            bool syncBackToChroma = false)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.ChromaToDolt };
            var branch = await _dolt.GetCurrentBranchAsync();
            var beforeCommit = await _dolt.GetHeadCommitHashAsync();

            _logger.LogInformation("Processing commit on branch {Branch} with message: {Message}", branch, message);

            try
            {
                // Auto-stage local changes from ChromaDB if requested
                if (autoStageFromChroma)
                {
                    // PP13-56-C1: Fix collection selection logic - identify collections with actual changes
                    var allCollections = await _chromaService.ListCollectionsAsync();
                    _logger.LogInformation("ProcessCommitAsync: Found {Count} collections: [{Collections}]", 
                        allCollections.Count, string.Join(", ", allCollections));
                    
                    // Detect changes across all collections to identify which have local changes
                    var allLocalChanges = await GetLocalChangesAsync();
                    
                    if (!allLocalChanges.HasChanges)
                    {
                        _logger.LogInformation("ProcessCommitAsync: No local changes found across any collections");
                        result.StagedFromChroma = 0;
                    }
                    else
                    {
                        _logger.LogInformation("ProcessCommitAsync: Found {TotalChanges} changes across all collections, identifying source collections", 
                            allLocalChanges.TotalChanges);
                        
                        // Check each collection individually to find which ones have changes
                        var collectionsWithChanges = new List<string>();
                        
                        foreach (var collectionName in allCollections)
                        {
                            var collectionChanges = await _chromaToDoltDetector.DetectLocalChangesAsync(collectionName);
                            if (collectionChanges.HasChanges)
                            {
                                collectionsWithChanges.Add(collectionName);
                                _logger.LogInformation("ProcessCommitAsync: Collection '{Collection}' has {Changes} changes", 
                                    collectionName, collectionChanges.TotalChanges);
                            }
                            else
                            {
                                _logger.LogInformation("ProcessCommitAsync: Collection '{Collection}' has no changes", collectionName);
                            }
                        }
                        
                        if (!collectionsWithChanges.Any())
                        {
                            _logger.LogWarning("ProcessCommitAsync: No collections with changes found despite total changes detected. Using fallback to first collection.");
                            var fallbackCollection = allCollections.FirstOrDefault() ?? "default";
                            collectionsWithChanges.Add(fallbackCollection);
                        }
                        
                        // Process each collection with changes
                        int totalStaged = 0;
                        foreach (var collectionWithChanges in collectionsWithChanges)
                        {
                            _logger.LogInformation("ProcessCommitAsync: Processing collection '{Collection}' with changes", collectionWithChanges);
                            
                            var localChanges = await _chromaToDoltDetector.DetectLocalChangesAsync(collectionWithChanges);
                            _logger.LogInformation("ProcessCommitAsync: Staging {TotalChanges} changes for collection {Collection}", 
                                localChanges.TotalChanges, collectionWithChanges);
                            
                            var stageResult = await _chromaToDoltSyncer.StageLocalChangesAsync(collectionWithChanges, localChanges);
                            
                            if (stageResult.Status == StageStatus.Failed)
                            {
                                result.Status = SyncStatusV2.Failed;
                                result.ErrorMessage = $"Failed to stage changes from collection '{collectionWithChanges}': {stageResult.ErrorMessage}";
                                return result;
                            }
                            
                            totalStaged += stageResult.TotalStaged;
                            _logger.LogInformation("ProcessCommitAsync: Staged {Count} changes from collection '{Collection}'", 
                                stageResult.TotalStaged, collectionWithChanges);
                        }
                        
                        result.StagedFromChroma = totalStaged;
                        _logger.LogInformation("ProcessCommitAsync: Total staged {Count} changes from {Collections} collections with changes", 
                            totalStaged, collectionsWithChanges.Count);
                    }
                }
                
                // PP13-61: Stage collection-level changes (deletion, rename, metadata updates)
                _logger.LogInformation("ProcessCommitAsync: Checking for pending collection changes");
                var collectionResult = await StageCollectionChangesAsync();
                
                if (collectionResult.Status == SyncStatusV2.Failed)
                {
                    result.Status = SyncStatusV2.Failed;
                    result.ErrorMessage = $"Failed to stage collection changes: {collectionResult.ErrorMessage}";
                    return result;
                }
                
                if (collectionResult.TotalCollectionChanges > 0)
                {
                    _logger.LogInformation("ProcessCommitAsync: Staged {Count} collection changes ({Summary})", 
                        collectionResult.TotalCollectionChanges, collectionResult.GetSummary());
                    
                    // Add collection sync summary to result data
                    result.Data = new { CollectionChanges = collectionResult };
                }
                else
                {
                    _logger.LogInformation("ProcessCommitAsync: No collection changes to stage");
                }
                
                // Stage any remaining Dolt changes
                await _dolt.AddAllAsync();
                
                // Commit
                var commitResult = await _dolt.CommitAsync(message);
                
                if (!commitResult.Success)
                {
                    result.Status = SyncStatusV2.Failed;
                    result.ErrorMessage = commitResult.Message;
                    return result;
                }

                result.CommitHash = commitResult.CommitHash;
                
                // PP13-61: Mark collection changes as committed and cleanup tracking records
                if (collectionResult.TotalCollectionChanges > 0)
                {
                    _logger.LogInformation("ProcessCommitAsync: Processing collection changes post-commit cleanup");
                    
                    try
                    {
                        // Get pending deletions to mark them as committed
                        var pendingDeletions = await _deletionTracker.GetPendingCollectionDeletionsAsync(_doltConfig.RepositoryPath);
                        
                        // Mark each operation as committed
                        foreach (var deletion in pendingDeletions)
                        {
                            await _deletionTracker.MarkCollectionDeletionCommittedAsync(
                                _doltConfig.RepositoryPath, 
                                deletion.CollectionName, 
                                deletion.OperationType);
                        }
                        
                        // Cleanup committed records
                        await _deletionTracker.CleanupCommittedCollectionDeletionsAsync(_doltConfig.RepositoryPath);
                        
                        _logger.LogInformation("ProcessCommitAsync: Collection changes cleanup completed successfully");
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning(cleanupEx, "ProcessCommitAsync: Collection changes cleanup failed, but commit was successful");
                    }
                }
                
                // Phase 2: Post-commit validation to ensure local changes were properly cleared
                _logger.LogInformation("ProcessCommitAsync: Performing post-commit validation to ensure local changes were cleared");
                try
                {
                    await Task.Delay(100); // Brief pause to allow metadata updates to propagate
                    
                    var postCommitChanges = await GetLocalChangesAsync();
                    if (postCommitChanges.HasChanges)
                    {
                        _logger.LogWarning("ProcessCommitAsync: {TotalChanges} local changes still detected after commit - may indicate metadata cleanup issue", 
                            postCommitChanges.TotalChanges);
                        
                        // Note: Not failing the commit, but logging for analysis
                    }
                    else
                    {
                        _logger.LogInformation("ProcessCommitAsync: Post-commit validation successful - no local changes remaining");
                    }
                }
                catch (Exception validationEx)
                {
                    _logger.LogWarning(validationEx, "ProcessCommitAsync: Post-commit validation failed, but commit was successful");
                }
                
                // Optionally sync back to ChromaDB
                if (syncBackToChroma)
                {
                    var collections = await _chromaService.ListCollectionsAsync();
                    var currentCollection = collections.FirstOrDefault() ?? "default";
                    
                    var syncResult = await SyncDoltToChromaAsync(currentCollection, beforeCommit, commitResult.CommitHash);
                    
                    result.Added = syncResult.Added;
                    result.Modified = syncResult.Modified;
                    result.Deleted = syncResult.Deleted;
                    result.ChunksProcessed = syncResult.ChunksProcessed;
                }

                result.Status = SyncStatusV2.Completed;
                _logger.LogInformation("Commit completed successfully: {Hash}", commitResult.CommitHash);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process commit");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #endregion

        #region Pull Processing

        public async Task<SyncResultV2> ProcessPullAsync(string remote = "origin", bool force = false)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
            var branch = await _dolt.GetCurrentBranchAsync();
            var beforeCommit = await _dolt.GetHeadCommitHashAsync();

            _logger.LogInformation("Processing pull from {Remote} to branch {Branch}", remote, branch);

            try
            {
                // Check for local changes if not forcing
                if (!force)
                {
                    var localChanges = await GetLocalChangesAsync();
                    if (localChanges.HasChanges)
                    {
                        result.Status = SyncStatusV2.LocalChangesExist;
                        result.LocalChanges = localChanges;
                        _logger.LogWarning("Pull blocked: {Count} local changes exist", localChanges.TotalChanges);
                        return result;
                    }
                }

                // Execute pull
                var pullResult = await _dolt.PullAsync(remote);
                
                if (!pullResult.Success)
                {
                    result.Status = SyncStatusV2.Failed;
                    result.ErrorMessage = pullResult.Message;
                    return result;
                }

                var afterCommit = await _dolt.GetHeadCommitHashAsync();
                result.CommitHash = afterCommit;
                result.WasFastForward = pullResult.Success;

                // Sync changes to ChromaDB
                if (beforeCommit != afterCommit)
                {
                    var collections = await _chromaService.ListCollectionsAsync();
                    var currentCollection = collections.FirstOrDefault() ?? "default";
                    
                    var syncResult = await SyncDoltToChromaAsync(currentCollection, beforeCommit, afterCommit);
                    
                    result.Added = syncResult.Added;
                    result.Modified = syncResult.Modified;
                    result.Deleted = syncResult.Deleted;
                    result.ChunksProcessed = syncResult.ChunksProcessed;
                }

                result.Status = SyncStatusV2.Completed;
                _logger.LogInformation("Pull completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process pull");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #endregion

        #region Checkout Processing

        public async Task<SyncResultV2> ProcessCheckoutAsync(
            string targetBranch, 
            bool createNew = false, 
            bool force = false)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
            var currentBranch = await _dolt.GetCurrentBranchAsync();

            _logger.LogInformation("Processing checkout from {Current} to {Target}", currentBranch, targetBranch);

            try
            {
                // Check for local changes if not forcing
                // Note: For branch switching, we should only block if there are actual uncommitted changes,
                // not just differences between ChromaDB and Dolt that are due to being on different branches
                if (!force)
                {
                    // For branch switching, we generally allow it without checking local changes
                    // For same-branch operations, we need to be more careful but still allow sync operations
                    if (currentBranch == targetBranch)
                    {
                        _logger.LogInformation("ProcessCheckoutAsync: Already on target branch {Branch} - will perform full reset and sync to ensure ChromaDB matches branch state", targetBranch);
                        // When already on the target branch, we should reset ChromaDB to match Dolt state
                        // This handles cases where uncommitted changes exist in ChromaDB
                        force = true; // Force the operation to ensure clean state
                    }
                    else
                    {
                        _logger.LogInformation("ProcessCheckoutAsync: Switching from branch {Current} to {Target} - skipping local change check as differences are expected", 
                            currentBranch, targetBranch);
                    }
                }

                // Execute checkout
                var checkoutResult = await _dolt.CheckoutAsync(targetBranch, createNew);
                
                if (!checkoutResult.Success)
                {
                    // If checkout failed due to uncommitted changes, try resetting and retrying
                    if (checkoutResult.Error?.Contains("local changes") == true || 
                        checkoutResult.Error?.Contains("would be overwritten") == true)
                    {
                        _logger.LogInformation("ProcessCheckoutAsync: Checkout blocked by local changes, attempting reset and retry");
                        
                        try
                        {
                            await _dolt.ResetHardAsync("HEAD");
                            checkoutResult = await _dolt.CheckoutAsync(targetBranch, createNew);
                            _logger.LogInformation("ProcessCheckoutAsync: Successfully reset and retried checkout");
                        }
                        catch (Exception resetEx)
                        {
                            _logger.LogWarning("ProcessCheckoutAsync: Failed to reset and retry checkout: {Message}", resetEx.Message);
                        }
                    }
                }
                
                if (!checkoutResult.Success)
                {
                    result.Status = SyncStatusV2.Failed;
                    result.ErrorMessage = checkoutResult.Error;
                    return result;
                }

                // PP13-58: Ensure working directory is in clean state before document operations
                _logger.LogInformation("ProcessCheckoutAsync: Ensuring working directory state consistency before sync operations");
                var isCleanState = await EnsureCleanWorkingDirectoryAsync(autoCommitUncommittedChanges: true);
                if (!isCleanState)
                {
                    _logger.LogWarning("ProcessCheckoutAsync: Could not achieve clean working directory state - sync may encounter inconsistencies");
                }

                // Full sync to update ChromaDB with new branch content across ALL collections
                // When forcing (e.g., same branch checkout), we need to ensure ChromaDB is completely reset
                var doltCollections = await _deltaDetector.GetAvailableCollectionNamesAsync();
                
                _logger.LogInformation("ProcessCheckoutAsync: Found {Count} collections in Dolt to sync for branch {Branch}, force={Force}", 
                    doltCollections.Count, targetBranch, force);
                
                // If forcing sync (same branch), ensure we clear ChromaDB collections first
                if (force && currentBranch == targetBranch)
                {
                    _logger.LogInformation("ProcessCheckoutAsync: Force sync on same branch - clearing all ChromaDB collections first");
                    var chromaCollections = await _chromaService.ListCollectionsAsync();
                    foreach (var chromaCollection in chromaCollections)
                    {
                        await _chromaService.DeleteCollectionAsync(chromaCollection);
                        _logger.LogInformation("ProcessCheckoutAsync: Deleted ChromaDB collection '{Collection}' for clean reset", chromaCollection);
                    }
                }
                
                // Also clean up any ChromaDB collections that don't exist in Dolt
                var chromaOnlyCollections = (await _chromaService.ListCollectionsAsync())
                    .Except(doltCollections)
                    .ToList();
                
                if (chromaOnlyCollections.Any())
                {
                    _logger.LogInformation("ProcessCheckoutAsync: Found {Count} ChromaDB-only collections to clean up", chromaOnlyCollections.Count);
                    foreach (var orphanedCollection in chromaOnlyCollections)
                    {
                        await _chromaService.DeleteCollectionAsync(orphanedCollection);
                        _logger.LogInformation("ProcessCheckoutAsync: Deleted orphaned ChromaDB collection '{Collection}'", orphanedCollection);
                    }
                }
                
                var aggregatedResult = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
                aggregatedResult.Status = SyncStatusV2.Completed; // Start with success, will be overridden if any fail
                
                foreach (var collection in doltCollections)
                {
                    _logger.LogInformation("ProcessCheckoutAsync: Syncing collection '{Collection}' for branch checkout to {Branch}", 
                        collection, targetBranch);
                    
                    var collectionResult = await FullSyncAsync(collection);
                    
                    // Aggregate results
                    aggregatedResult.Added += collectionResult.Added;
                    aggregatedResult.Modified += collectionResult.Modified;
                    aggregatedResult.Deleted += collectionResult.Deleted;
                    aggregatedResult.ChunksProcessed += collectionResult.ChunksProcessed;
                    
                    if (collectionResult.Status != SyncStatusV2.Completed)
                    {
                        _logger.LogError("ProcessCheckoutAsync: Failed to sync collection '{Collection}': {Error}", 
                            collection, collectionResult.ErrorMessage);
                        aggregatedResult.Status = collectionResult.Status;
                        aggregatedResult.ErrorMessage = $"Failed to sync collection '{collection}': {collectionResult.ErrorMessage}";
                        break; // Stop on first failure
                    }
                    else
                    {
                        _logger.LogInformation("ProcessCheckoutAsync: Successfully synced collection '{Collection}' - Added: {Added}, Modified: {Modified}, Deleted: {Deleted}", 
                            collection, collectionResult.Added, collectionResult.Modified, collectionResult.Deleted);
                    }
                }
                
                result.Added = aggregatedResult.Added;
                result.Modified = aggregatedResult.Modified;
                result.Deleted = aggregatedResult.Deleted;
                result.ChunksProcessed = aggregatedResult.ChunksProcessed;
                result.Status = aggregatedResult.Status;
                result.ErrorMessage = aggregatedResult.ErrorMessage;

                // Validate branch state after sync (temporarily disabled for debugging)
                if (false && aggregatedResult.Status == SyncStatusV2.Completed)
                {
                    var (isValid, validationError) = await ValidateBranchStateAsync(targetBranch);
                    if (!isValid)
                    {
                        _logger.LogWarning("ProcessCheckoutAsync: Branch state validation failed after sync: {Error}", validationError);
                        // Note: We still consider the checkout successful since the sync completed,
                        // but log the validation warning for monitoring
                    }
                }

                _logger.LogInformation("Checkout to branch {Branch} completed", targetBranch);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process checkout");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #endregion

        #region Merge Processing

        public async Task<Services.MergeSyncResultV2> ProcessMergeAsync(string sourceBranch, bool force = false, List<ConflictResolutionRequest>? resolutions = null)
        {
            var result = new Services.MergeSyncResultV2();
            var targetBranch = await _dolt.GetCurrentBranchAsync();
            var beforeCommit = await _dolt.GetHeadCommitHashAsync();

            _logger.LogInformation("Processing merge from {Source} to {Target}", sourceBranch, targetBranch);

            try
            {
                // Check for local changes if not forcing
                if (!force)
                {
                    var localChanges = await GetLocalChangesAsync();
                    if (localChanges.HasChanges)
                    {
                        result.Status = SyncStatusV2.LocalChangesExist;
                        result.LocalChanges = localChanges;
                        _logger.LogWarning("Merge blocked: {Count} local changes exist", localChanges.TotalChanges);
                        return result;
                    }
                }

                // Execute merge
                var mergeResult = await _dolt.MergeAsync(sourceBranch);
                
                if (!mergeResult.Success)
                {
                    if (mergeResult.HasConflicts)
                    {
                        result.HasConflicts = true;
                        result.Status = SyncStatusV2.Conflicts;
                        
                        // Get conflict information
                        var conflicts = await _dolt.GetConflictsAsync("documents");
                        foreach (var conflict in conflicts)
                        {
                            result.Conflicts.Add(new ConflictInfoV2
                            {
                                DocId = conflict.RowId,
                                CollectionName = "default",
                                ConflictType = "content",
                                OurVersion = System.Text.Json.JsonSerializer.Serialize(conflict.OurValues),
                                TheirVersion = System.Text.Json.JsonSerializer.Serialize(conflict.TheirValues)
                            });
                        }
                    }
                    else
                    {
                        result.Status = SyncStatusV2.Failed;
                        result.ErrorMessage = mergeResult.Message;
                    }
                    return result;
                }

                var afterCommit = await _dolt.GetHeadCommitHashAsync();
                result.CommitHash = afterCommit;

                // Sync merged changes to ChromaDB across ALL collections
                if (beforeCommit != afterCommit)
                {
                    // PP13-58: Ensure working directory is in clean state after merge before sync operations
                    _logger.LogInformation("ProcessMergeAsync: Ensuring working directory state consistency after merge");
                    var isCleanState = await EnsureCleanWorkingDirectoryAsync(autoCommitUncommittedChanges: true);
                    if (!isCleanState)
                    {
                        _logger.LogWarning("ProcessMergeAsync: Could not achieve clean working directory state - sync may encounter inconsistencies");
                    }

                    var doltCollections = await _deltaDetector.GetAvailableCollectionNamesAsync();
                    
                    _logger.LogInformation("ProcessMergeAsync: Found {Count} collections in Dolt to sync after merge", 
                        doltCollections.Count);
                    
                    var aggregatedSyncResult = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
                    
                    foreach (var collection in doltCollections)
                    {
                        _logger.LogInformation("ProcessMergeAsync: Syncing collection '{Collection}' after merge", collection);
                        
                        var collectionSyncResult = await SyncDoltToChromaAsync(collection, beforeCommit, afterCommit);
                        
                        // Aggregate results
                        aggregatedSyncResult.Added += collectionSyncResult.Added;
                        aggregatedSyncResult.Modified += collectionSyncResult.Modified;
                        aggregatedSyncResult.Deleted += collectionSyncResult.Deleted;
                        aggregatedSyncResult.ChunksProcessed += collectionSyncResult.ChunksProcessed;
                        
                        _logger.LogInformation("ProcessMergeAsync: Synced collection '{Collection}' - Added: {Added}, Modified: {Modified}, Deleted: {Deleted}", 
                            collection, collectionSyncResult.Added, collectionSyncResult.Modified, collectionSyncResult.Deleted);
                    }
                    
                    result.Added = aggregatedSyncResult.Added;
                    result.Modified = aggregatedSyncResult.Modified;
                    result.Deleted = aggregatedSyncResult.Deleted;
                    result.ChunksProcessed = aggregatedSyncResult.ChunksProcessed;
                }

                result.Status = SyncStatusV2.Completed;
                _logger.LogInformation("Merge completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process merge");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #endregion

        #region Push Processing

        public async Task<SyncResultV2> ProcessPushAsync(string remote = "origin", string? branch = null)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.ChromaToDolt };
            
            try
            {
                var currentBranch = branch ?? await _dolt.GetCurrentBranchAsync();
                
                _logger.LogInformation("Pushing branch {Branch} to {Remote}", currentBranch, remote);
                
                var pushResult = await _dolt.PushAsync(remote, currentBranch);
                
                if (!pushResult.Success)
                {
                    result.Status = SyncStatusV2.Failed;
                    result.ErrorMessage = pushResult.Message ?? "Push failed";
                    return result;
                }

                result.Status = SyncStatusV2.Completed;
                result.CommitHash = await _dolt.GetHeadCommitHashAsync();
                result.Data = pushResult; // Store the detailed push result
                
                _logger.LogInformation("Push completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process push");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #endregion

        #region Reset Processing

        public async Task<SyncResultV2> ProcessResetAsync(string targetCommit, bool hard = false)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
            
            try
            {
                _logger.LogInformation("Resetting to commit {Commit} (hard: {Hard})", targetCommit, hard);
                
                var resetResult = hard 
                    ? await _dolt.ResetHardAsync(targetCommit)
                    : await _dolt.ResetSoftAsync(targetCommit);
                
                if (!resetResult.Success)
                {
                    result.Status = SyncStatusV2.Failed;
                    result.ErrorMessage = resetResult.Error;
                    return result;
                }

                // PP13-58: Reset operations inherently create clean state, but verify for consistency
                _logger.LogInformation("ProcessResetAsync: Verifying working directory state consistency after reset");
                var isCleanState = await EnsureCleanWorkingDirectoryAsync(autoCommitUncommittedChanges: false);
                if (!isCleanState)
                {
                    _logger.LogWarning("ProcessResetAsync: Working directory not clean after reset - this indicates a potential issue");
                }

                // Full sync to update ChromaDB after reset across ALL collections
                var doltCollections = await _deltaDetector.GetAvailableCollectionNamesAsync();
                
                _logger.LogInformation("ProcessResetAsync: Found {Count} collections in Dolt to sync after reset", 
                    doltCollections.Count);
                
                var aggregatedResult = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
                aggregatedResult.Status = SyncStatusV2.Completed; // Start with success, will be overridden if any fail
                
                foreach (var collection in doltCollections)
                {
                    _logger.LogInformation("ProcessResetAsync: Syncing collection '{Collection}' after reset", collection);
                    
                    var collectionResult = await FullSyncAsync(collection);
                    
                    // Aggregate results
                    aggregatedResult.Added += collectionResult.Added;
                    aggregatedResult.Modified += collectionResult.Modified;
                    aggregatedResult.Deleted += collectionResult.Deleted;
                    aggregatedResult.ChunksProcessed += collectionResult.ChunksProcessed;
                    
                    if (collectionResult.Status != SyncStatusV2.Completed)
                    {
                        _logger.LogError("ProcessResetAsync: Failed to sync collection '{Collection}': {Error}", 
                            collection, collectionResult.ErrorMessage);
                        aggregatedResult.Status = collectionResult.Status;
                        aggregatedResult.ErrorMessage = $"Failed to sync collection '{collection}': {collectionResult.ErrorMessage}";
                        break; // Stop on first failure
                    }
                }
                
                result.Added = aggregatedResult.Added;
                result.Modified = aggregatedResult.Modified;
                result.Deleted = aggregatedResult.Deleted;
                result.ChunksProcessed = aggregatedResult.ChunksProcessed;
                result.Status = aggregatedResult.Status;
                result.CommitHash = targetCommit;

                _logger.LogInformation("Reset completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process reset");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #endregion

        #region Change Detection

        public async Task<bool> HasPendingChangesAsync()
        {
            try
            {
                // Check Dolt changes
                var doltStatus = await _dolt.GetStatusAsync();
                if (doltStatus.HasStagedChanges || doltStatus.HasUnstagedChanges) return true;
                
                // Check ChromaDB changes
                var localChanges = await GetLocalChangesAsync();
                return localChanges.HasChanges;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check for pending changes");
                return false;
            }
        }

        public async Task<PendingChangesV2> GetPendingChangesAsync()
        {
            var result = new PendingChangesV2();
            
            try
            {
                var collections = await _chromaService.ListCollectionsAsync();
                var currentCollection = collections.FirstOrDefault() ?? "default";
                
                // Get Dolt pending changes
                var doltChanges = await _deltaDetector.GetPendingSyncDocumentsAsync(currentCollection);
                foreach (var change in doltChanges)
                {
                    if (change.IsNew)
                        result.DoltNewDocuments.Add(change);
                    else if (change.IsModified)
                        result.DoltModifiedDocuments.Add(change);
                }
                
                var doltDeleted = await _deltaDetector.GetDeletedDocumentsAsync(currentCollection);
                result.DoltDeletedDocuments.AddRange(doltDeleted);
                
                // Get ChromaDB local changes
                result.ChromaLocalChanges = await GetLocalChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pending changes");
            }
            
            return result;
        }

        #endregion

        #region Manual Sync Operations

        public async Task<SyncResultV2> FullSyncAsync(string? collectionName = null, bool forceSync = false)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
            
            try
            {
                // If no collection name provided, sync all collections found in Dolt database
                if (collectionName == null)
                {
                    _logger.LogInformation("PP13-49: About to call GetAvailableCollectionNamesAsync from SyncManagerV2");
                    _logger.LogInformation("PP13-49: DeltaDetector instance type: {Type}", _deltaDetector?.GetType().FullName ?? "NULL");
                    
                    var doltCollections = await _deltaDetector.GetAvailableCollectionNamesAsync();
                    
                    _logger.LogInformation("PP13-49: GetAvailableCollectionNamesAsync returned {Count} collections: [{Collections}]", 
                        doltCollections?.Count ?? 0, 
                        doltCollections != null ? string.Join(", ", doltCollections) : "NULL");
                    
                    if (doltCollections.Any())
                    {
                        _logger.LogInformation("Found {Count} collections in Dolt database, syncing all: {Collections}", 
                            doltCollections.Count, string.Join(", ", doltCollections));
                        
                        // Sync all collections found in Dolt
                        bool anyChanges = false;
                        foreach (var collection in doltCollections)
                        {
                            var collectionResult = await SyncSingleCollectionAsync(collection, forceSync);
                            result.Added += collectionResult.Added;
                            result.Modified += collectionResult.Modified;
                            result.Deleted += collectionResult.Deleted;
                            result.ChunksProcessed += collectionResult.ChunksProcessed;
                            
                            if (collectionResult.Status == SyncStatusV2.Failed)
                            {
                                result.Status = SyncStatusV2.Failed;
                                result.ErrorMessage = collectionResult.ErrorMessage;
                                return result;
                            }
                            else if (collectionResult.Status == SyncStatusV2.Completed)
                            {
                                anyChanges = true;
                            }
                        }
                        
                        result.Status = anyChanges ? SyncStatusV2.Completed : SyncStatusV2.NoChanges;
                        _logger.LogInformation("Full sync completed for all collections: {Added} documents, {Chunks} chunks", 
                            result.Added, result.ChunksProcessed);
                        return result;
                    }
                    else
                    {
                        collectionName = (await _chromaService.ListCollectionsAsync()).FirstOrDefault() ?? "default";
                        _logger.LogInformation("No collections found in Dolt, using collection: {Collection}", collectionName);
                    }
                }
                
                // Sync single collection
                return await SyncSingleCollectionAsync(collectionName, forceSync);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform full sync");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Synchronize a single collection from Dolt to ChromaDB
        /// </summary>
        /// <param name="collectionName">Name of the collection to sync</param>
        /// <param name="forceSync">Force sync by deleting and recreating collection, bypassing count optimization</param>
        private async Task<SyncResultV2> SyncSingleCollectionAsync(string collectionName, bool forceSync = false)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
            
            try
            {
                _logger.LogInformation("Performing full sync for collection {Collection}", collectionName);
                
                // Get all documents from Dolt
                var documents = await _deltaDetector.GetAllDocumentsAsync(collectionName);
                
                // Check if collection exists and if content is already identical
                var existingCollections = await _chromaService.ListCollectionsAsync();
                bool needsSync = true;
                
                if (existingCollections.Contains(collectionName))
                {
                    if (forceSync)
                    {
                        // Force sync requested - delete collection for clean rebuild (bypassing count optimization)
                        _logger.LogInformation("Force sync requested - deleting collection {Collection} for clean rebuild", collectionName);
                        await _chromaService.DeleteCollectionAsync(collectionName);
                        needsSync = true;
                    }
                    else
                    {
                        // Check if content is already identical using count optimization
                        var existingCount = await _chromaService.GetDocumentCountAsync(collectionName);
                        if (existingCount == documents.Count())
                        {
                            // For simplicity, if counts match, assume content is already synchronized
                            // A more thorough check could compare individual document content
                            needsSync = false;
                            result.Status = SyncStatusV2.NoChanges;
                            _logger.LogInformation("Collection {Collection} already synchronized - no changes needed (count match: {Count})", collectionName, existingCount);
                            
                            // Still update sync state to record that we checked
                            var commitHash = await _dolt.GetHeadCommitHashAsync();
                            await _deltaDetector.UpdateSyncStateAsync(collectionName, commitHash, 0, 0);
                            
                            return result;
                        }
                        
                        if (needsSync)
                        {
                            _logger.LogInformation("Deleting existing collection {Collection} - content differs", collectionName);
                            await _chromaService.DeleteCollectionAsync(collectionName);
                        }
                    }
                }
                else
                {
                    _logger.LogInformation("Collection {Collection} does not exist, will create", collectionName);
                }
                
                if (needsSync)
                {
                    _logger.LogInformation("Creating collection {Collection}", collectionName);
                    await _chromaService.CreateCollectionAsync(collectionName);
                    
                    // Sync all documents
                    foreach (var doc in documents)
                    {
                        var chromaEntries = DocumentConverterUtilityV2.ConvertDoltToChroma(
                            doc, await _dolt.GetHeadCommitHashAsync());
                        
                        await _chromaService.AddDocumentsAsync(
                            collectionName,
                            chromaEntries.Documents,
                            chromaEntries.Ids,
                            chromaEntries.Metadatas);
                        
                        result.Added++;
                        result.ChunksProcessed += chromaEntries.Count;
                    }
                    
                    // Update sync state
                    var commitHash = await _dolt.GetHeadCommitHashAsync();
                    await _deltaDetector.UpdateSyncStateAsync(collectionName, commitHash, result.Added, result.ChunksProcessed);
                    
                    result.Status = SyncStatusV2.Completed;
                    _logger.LogInformation("Collection {Collection} sync completed: {Added} documents, {Chunks} chunks", 
                        collectionName, result.Added, result.ChunksProcessed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform sync for collection {Collection}", collectionName);
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }
            
            return result;
        }

        public async Task<SyncResultV2> IncrementalSyncAsync(string? collectionName = null)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
            
            try
            {
                collectionName ??= (await _chromaService.ListCollectionsAsync()).FirstOrDefault() ?? "default";
                
                _logger.LogInformation("Performing incremental sync for collection {Collection}", collectionName);
                
                // Get pending changes
                var pendingDocs = await _deltaDetector.GetPendingSyncDocumentsAsync(collectionName);
                var deletedDocs = await _deltaDetector.GetDeletedDocumentsAsync(collectionName);
                
                var commitHash = await _dolt.GetHeadCommitHashAsync();
                
                // Process changes
                foreach (var doc in pendingDocs)
                {
                    var chromaEntries = DocumentConverterUtilityV2.ConvertDeltaToChroma(doc, commitHash);
                    
                    if (doc.IsNew)
                    {
                        await _chromaService.AddDocumentsAsync(
                            collectionName,
                            chromaEntries.Documents,
                            chromaEntries.Ids,
                            chromaEntries.Metadatas);
                        result.Added++;
                    }
                    else if (doc.IsModified)
                    {
                        // Delete old chunks
                        var chunkIds = DocumentConverterUtilityV2.GetChunkIds(doc.DocId, chromaEntries.Count);
                        await _chromaService.DeleteDocumentsAsync(collectionName, chunkIds);
                        
                        // Add updated chunks
                        await _chromaService.AddDocumentsAsync(
                            collectionName,
                            chromaEntries.Documents,
                            chromaEntries.Ids,
                            chromaEntries.Metadatas);
                        result.Modified++;
                    }
                    
                    result.ChunksProcessed += chromaEntries.Count;
                    
                    // Record sync operation
                    await _deltaDetector.RecordSyncOperationAsync(
                        doc.DocId, collectionName, doc.ContentHash,
                        chromaEntries.Ids, SyncDirection.DoltToChroma, 
                        doc.IsNew ? "added" : "modified");
                }
                
                // Process deletions
                foreach (var deleted in deletedDocs)
                {
                    var chunkIds = deleted.GetChunkIdList();
                    await _chromaService.DeleteDocumentsAsync(collectionName, chunkIds);
                    result.Deleted++;
                    
                    await _deltaDetector.RecordSyncOperationAsync(
                        deleted.DocId, collectionName, "",
                        chunkIds, SyncDirection.DoltToChroma, "deleted");
                }
                
                // Update sync state
                await _deltaDetector.UpdateSyncStateAsync(collectionName, commitHash, 
                    result.Added + result.Modified, result.ChunksProcessed);
                
                result.Status = result.TotalChanges > 0 ? SyncStatusV2.Completed : SyncStatusV2.NoChanges;
                _logger.LogInformation("Incremental sync completed: {Total} changes", result.TotalChanges);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform incremental sync");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }
            
            return result;
        }

        public async Task<StageResult> StageLocalChangesAsync(string collectionName)
        {
            try
            {
                _logger.LogInformation("Staging local changes from collection {Collection}", collectionName);
                return await _chromaToDoltSyncer.StageLocalChangesAsync(collectionName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stage local changes");
                return new StageResult(StageStatus.Failed, 0, 0, 0, ex.Message);
            }
        }

        public async Task<StageResult> StageLocalChangesAsync(string collectionName, LocalChanges localChanges)
        {
            try
            {
                _logger.LogInformation("Staging pre-detected local changes from collection {Collection}", collectionName);
                _logger.LogInformation("SyncManagerV2: Passing {TotalChanges} pre-detected changes to syncer", localChanges.TotalChanges);
                
                return await _chromaToDoltSyncer.StageLocalChangesAsync(collectionName, localChanges);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stage pre-detected local changes");
                return new StageResult(StageStatus.Failed, 0, 0, 0, ex.Message);
            }
        }

        #endregion

        #region Import/Export

        public async Task<SyncResultV2> ImportFromChromaAsync(string sourceCollection, string? commitMessage = null)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.ChromaToDolt };
            
            try
            {
                _logger.LogInformation("Importing from ChromaDB collection {Collection}", sourceCollection);
                
                commitMessage ??= $"Import from ChromaDB collection {sourceCollection}";
                
                var repositoryPath = "./"; // Use current directory as repository path
                var importResult = await _chromaToDoltSyncer.InitializeFromChromaAsync(
                    sourceCollection, repositoryPath, commitMessage);
                
                if (importResult.Success)
                {
                    result.Status = SyncStatusV2.Completed;
                    result.Added = importResult.DocumentsImported;
                    result.CommitHash = importResult.CommitHash;
                    _logger.LogInformation("Import completed: {Count} documents", importResult.DocumentsImported);
                }
                else
                {
                    result.Status = SyncStatusV2.Failed;
                    result.ErrorMessage = importResult.ErrorMessage;
                    _logger.LogError("Import failed: {Error}", importResult.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import from ChromaDB");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }
            
            return result;
        }

        #endregion

        #region Helper Methods

        private async Task<SyncResultV2> SyncDoltToChromaAsync(
            string collectionName, 
            string fromCommit, 
            string toCommit)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
            
            try
            {
                // Get changes between commits
                var diffs = await _deltaDetector.GetCommitDiffAsync(fromCommit, toCommit, collectionName);
                
                foreach (var diff in diffs)
                {
                    if (diff.DiffType == "added" || diff.DiffType == "modified")
                    {
                        // Create DocumentDeltaV2 from diff
                        var delta = new DocumentDeltaV2(
                            diff.DocId,
                            diff.CollectionName,
                            diff.Content ?? "",
                            diff.ToContentHash ?? "",
                            diff.Title,
                            diff.DocType,
                            diff.Metadata ?? "{}",
                            diff.DiffType == "added" ? "new" : "modified"
                        );
                        
                        var chromaEntries = DocumentConverterUtilityV2.ConvertDeltaToChroma(delta, toCommit);
                        
                        if (diff.DiffType == "modified")
                        {
                            // Delete old chunks first
                            var chunkIds = DocumentConverterUtilityV2.GetChunkIds(diff.DocId, chromaEntries.Count);
                            await _chromaService.DeleteDocumentsAsync(collectionName, chunkIds);
                        }
                        
                        // Add new/updated chunks
                        await _chromaService.AddDocumentsAsync(
                            collectionName,
                            chromaEntries.Documents,
                            chromaEntries.Ids,
                            chromaEntries.Metadatas);
                        
                        if (diff.DiffType == "added")
                            result.Added++;
                        else
                            result.Modified++;
                        
                        result.ChunksProcessed += chromaEntries.Count;
                    }
                    else if (diff.DiffType == "removed")
                    {
                        // Delete chunks from ChromaDB
                        var chunkIds = DocumentConverterUtilityV2.GetChunkIds(diff.DocId, 10); // Estimate chunks
                        await _chromaService.DeleteDocumentsAsync(collectionName, chunkIds);
                        result.Deleted++;
                    }
                }
                
                result.Status = result.TotalChanges > 0 ? SyncStatusV2.Completed : SyncStatusV2.NoChanges;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync from Dolt to ChromaDB");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }
            
            return result;
        }

        #endregion

        #region Branch State Validation

        /// <summary>
        /// Validates that ChromaDB state matches Dolt state after branch operations
        /// </summary>
        private async Task<(bool IsValid, string ErrorMessage)> ValidateBranchStateAsync(string branchName)
        {
            try
            {
                _logger.LogInformation("ValidateBranchStateAsync: Starting validation for branch {Branch}", branchName);
                
                var doltCollections = await _deltaDetector.GetAvailableCollectionNamesAsync();
                var chromaCollections = await _chromaService.ListCollectionsAsync();
                
                _logger.LogInformation("ValidateBranchStateAsync: Found {DoltCount} collections in Dolt, {ChromaCount} in ChromaDB",
                    doltCollections.Count, chromaCollections.Count);
                
                var errors = new List<string>();
                
                foreach (var collection in doltCollections)
                {
                    // Check if collection exists in ChromaDB
                    if (!chromaCollections.Contains(collection))
                    {
                        errors.Add($"Collection '{collection}' exists in Dolt but not in ChromaDB");
                        continue;
                    }
                    
                    // Get document counts from both sources
                    var doltDocs = await _deltaDetector.GetDocumentCountAsync(collection);
                    var chromaDocs = await _chromaService.GetDocumentCountAsync(collection);
                    
                    _logger.LogInformation("ValidateBranchStateAsync: Collection '{Collection}' - Dolt: {DoltCount} docs, ChromaDB: {ChromaCount} docs",
                        collection, doltDocs, chromaDocs);
                    
                    if (doltDocs != chromaDocs)
                    {
                        errors.Add($"Collection '{collection}' document count mismatch - Dolt: {doltDocs}, ChromaDB: {chromaDocs}");
                    }
                }
                
                // Check for collections in ChromaDB that aren't in Dolt (shouldn't happen after sync)
                foreach (var chromaCollection in chromaCollections)
                {
                    if (!doltCollections.Contains(chromaCollection))
                    {
                        errors.Add($"Collection '{chromaCollection}' exists in ChromaDB but not in Dolt");
                    }
                }
                
                if (errors.Any())
                {
                    var errorMessage = $"Branch state validation failed: {string.Join("; ", errors)}";
                    _logger.LogError("ValidateBranchStateAsync: {ErrorMessage}", errorMessage);
                    return (false, errorMessage);
                }
                
                _logger.LogInformation("ValidateBranchStateAsync: Validation successful for branch {Branch}", branchName);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ValidateBranchStateAsync: Failed to validate branch state");
                return (false, $"Validation error: {ex.Message}");
            }
        }

        #endregion

        #region Collection-Level Sync Operations (PP13-61)

        /// <summary>
        /// Synchronize collection-level changes (deletion, rename, metadata updates) from ChromaDB to Dolt
        /// </summary>
        public async Task<CollectionSyncResult> SyncCollectionChangesAsync()
        {
            _logger.LogInformation("SyncCollectionChangesAsync: Starting collection-level sync operation");
            
            var result = new CollectionSyncResult { Status = SyncStatusV2.InProgress };
            
            try
            {
                // Step 1: Get pending collection deletions from tracking database
                var pendingDeletions = await _deletionTracker.GetPendingCollectionDeletionsAsync(_doltConfig.RepositoryPath);
                _logger.LogInformation("SyncCollectionChangesAsync: Found {Count} pending collection operations", pendingDeletions.Count);

                if (pendingDeletions.Count == 0)
                {
                    _logger.LogInformation("SyncCollectionChangesAsync: No collection changes to sync");
                    result.Status = SyncStatusV2.NoChanges;
                    return result;
                }

                // Step 2: Group operations by type
                var deletionOps = pendingDeletions.Where(d => d.OperationType == "deletion").ToList();
                var renameOps = pendingDeletions.Where(d => d.OperationType == "rename").ToList();
                var updateOps = pendingDeletions.Where(d => d.OperationType == "metadata_update").ToList();

                _logger.LogInformation("SyncCollectionChangesAsync: Processing {DeleteCount} deletions, {RenameCount} renames, {UpdateCount} updates", 
                    deletionOps.Count, renameOps.Count, updateOps.Count);

                // Step 3: Process collection deletions (includes cascade document deletion)
                foreach (var deletion in deletionOps)
                {
                    try
                    {
                        await ProcessCollectionDeletion(deletion, result);
                        result.CollectionsDeleted++;
                        result.DeletedCollectionNames.Add(deletion.CollectionName);
                        
                        // Mark as committed
                        await _deletionTracker.MarkCollectionDeletionCommittedAsync(_doltConfig.RepositoryPath, deletion.CollectionName, "deletion");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SyncCollectionChangesAsync: Failed to process collection deletion for '{Collection}'", deletion.CollectionName);
                        result.Status = SyncStatusV2.Failed;
                        result.ErrorMessage = $"Failed to process collection deletion for '{deletion.CollectionName}': {ex.Message}";
                        return result;
                    }
                }

                // Step 4: Process collection renames
                foreach (var rename in renameOps)
                {
                    try
                    {
                        await ProcessCollectionRename(rename, result);
                        result.CollectionsRenamed++;
                        result.RenamedCollectionNames.Add($"{rename.OriginalName} -> {rename.NewName}");
                        
                        // Mark as committed
                        await _deletionTracker.MarkCollectionDeletionCommittedAsync(_doltConfig.RepositoryPath, rename.CollectionName, "rename");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SyncCollectionChangesAsync: Failed to process collection rename for '{Collection}'", rename.CollectionName);
                        result.Status = SyncStatusV2.Failed;
                        result.ErrorMessage = $"Failed to process collection rename for '{rename.CollectionName}': {ex.Message}";
                        return result;
                    }
                }

                // Step 5: Process collection metadata updates
                foreach (var update in updateOps)
                {
                    try
                    {
                        await ProcessCollectionMetadataUpdate(update, result);
                        result.CollectionsUpdated++;
                        result.UpdatedCollectionNames.Add(update.CollectionName);
                        
                        // Mark as committed
                        await _deletionTracker.MarkCollectionDeletionCommittedAsync(_doltConfig.RepositoryPath, update.CollectionName, "metadata_update");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SyncCollectionChangesAsync: Failed to process collection update for '{Collection}'", update.CollectionName);
                        result.Status = SyncStatusV2.Failed;
                        result.ErrorMessage = $"Failed to process collection update for '{update.CollectionName}': {ex.Message}";
                        return result;
                    }
                }

                // Step 6: Commit changes if any operations were processed
                if (result.TotalCollectionChanges > 0)
                {
                    var commitMessage = $"Collection sync: {result.GetSummary()}";
                    var commitResult = await _dolt.CommitAsync(commitMessage);
                    result.CommitHash = commitResult.CommitHash;
                    _logger.LogInformation("SyncCollectionChangesAsync: Committed collection changes with hash {CommitHash}", result.CommitHash);
                }

                // Step 7: Cleanup committed collection deletion records
                await _deletionTracker.CleanupCommittedCollectionDeletionsAsync(_doltConfig.RepositoryPath);

                result.Status = SyncStatusV2.Completed;
                _logger.LogInformation("SyncCollectionChangesAsync: Collection sync completed successfully. {Summary}", result.GetSummary());
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SyncCollectionChangesAsync: Collection sync operation failed");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = $"Collection sync failed: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Stage collection-level changes (deletion, rename, metadata updates) to Dolt
        /// </summary>
        public async Task<CollectionSyncResult> StageCollectionChangesAsync()
        {
            _logger.LogInformation("StageCollectionChangesAsync: Starting collection-level staging operation");
            
            var result = new CollectionSyncResult { Status = SyncStatusV2.InProgress };
            
            try
            {
                // Step 1: Get pending collection deletions from tracking database
                var pendingDeletions = await _deletionTracker.GetPendingCollectionDeletionsAsync(_doltConfig.RepositoryPath);
                _logger.LogInformation("StageCollectionChangesAsync: Found {Count} pending collection operations", pendingDeletions.Count);

                if (pendingDeletions.Count == 0)
                {
                    _logger.LogInformation("StageCollectionChangesAsync: No collection changes to stage");
                    result.Status = SyncStatusV2.NoChanges;
                    return result;
                }

                // Step 2: Execute collection operations before staging (with conflict resolution)
                var deletedCollections = new HashSet<string>(); // Track collections that have been deleted
                
                foreach (var deletion in pendingDeletions)
                {
                    // Conflict resolution: Skip operations on collections that have already been deleted
                    if (deletedCollections.Contains(deletion.CollectionName) && deletion.OperationType != "deletion")
                    {
                        _logger.LogInformation("StageCollectionChangesAsync: Skipping {OperationType} for collection '{Collection}' - collection already deleted (conflict resolution)", 
                            deletion.OperationType, deletion.CollectionName);
                        continue;
                    }

                    switch (deletion.OperationType)
                    {
                        case "deletion":
                            await ProcessCollectionDeletion(deletion, result);
                            deletedCollections.Add(deletion.CollectionName); // Mark collection as deleted
                            break;
                        case "rename":
                            await ProcessCollectionRename(deletion, result);
                            break;
                        case "metadata_update":
                            await ProcessCollectionMetadataUpdate(deletion, result);
                            break;
                        default:
                            _logger.LogWarning("StageCollectionChangesAsync: Unknown operation type '{OperationType}' for collection '{Collection}'", 
                                deletion.OperationType, deletion.CollectionName);
                            break;
                    }
                }

                // Step 3: Stage collection table changes to Dolt
                await _dolt.AddAsync("collections");
                _logger.LogInformation("StageCollectionChangesAsync: Staged collection table changes");

                // Step 4: Stage document table changes (for cascade deletions)
                await _dolt.AddAsync("documents");
                _logger.LogInformation("StageCollectionChangesAsync: Staged document table changes");

                // Step 5: Count operations by type for reporting
                var deletionOps = pendingDeletions.Where(d => d.OperationType == "deletion").ToList();
                var renameOps = pendingDeletions.Where(d => d.OperationType == "rename").ToList();
                var updateOps = pendingDeletions.Where(d => d.OperationType == "metadata_update").ToList();

                result.CollectionsDeleted = deletionOps.Count;
                result.CollectionsRenamed = renameOps.Count;
                result.CollectionsUpdated = updateOps.Count;

                // Populate names for reporting
                result.DeletedCollectionNames.AddRange(deletionOps.Select(d => d.CollectionName));
                result.RenamedCollectionNames.AddRange(renameOps.Select(r => $"{r.OriginalName} -> {r.NewName}"));
                result.UpdatedCollectionNames.AddRange(updateOps.Select(u => u.CollectionName));

                result.Status = SyncStatusV2.Completed;
                _logger.LogInformation("StageCollectionChangesAsync: Collection staging completed successfully. {Summary}", result.GetSummary());
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StageCollectionChangesAsync: Collection staging operation failed");
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = $"Collection staging failed: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Process a collection deletion operation including cascade document deletion
        /// </summary>
        private async Task ProcessCollectionDeletion(CollectionDeletionRecord deletion, CollectionSyncResult result)
        {
            _logger.LogInformation("ProcessCollectionDeletion: Processing deletion for collection '{Collection}'", deletion.CollectionName);

            // Step 1: Check if documents table exists before querying it
            var tablesResult = await _dolt.QueryAsync<Dictionary<string, object>>("SHOW TABLES");
            bool documentsTableExists = tablesResult?.Any(row => row.Values.Any(v => v.ToString() == "documents")) ?? false;
            
            var documentIds = new List<string>();
            
            if (documentsTableExists)
            {
                // Get all documents in the collection before deletion
                var documentsResult = await _dolt.QueryAsync<Dictionary<string, object>>($"SELECT doc_id FROM documents WHERE collection_name = '{deletion.CollectionName}'");
                
                if (documentsResult != null)
                {
                    documentIds.AddRange(documentsResult.Select(row => row["doc_id"].ToString()!));
                }
            }
            else
            {
                _logger.LogInformation("ProcessCollectionDeletion: Documents table doesn't exist - no documents to cascade delete");
            }

            _logger.LogInformation("ProcessCollectionDeletion: Found {Count} documents to cascade delete", documentIds.Count);
            result.DocumentsDeletedByCollectionDeletion += documentIds.Count;

            // Step 2: Delete all documents in the collection (cascade deletion)
            if (documentsTableExists && documentIds.Count > 0)
            {
                await _dolt.QueryAsync<object>($"DELETE FROM documents WHERE collection_name = '{deletion.CollectionName}'");
                _logger.LogInformation("ProcessCollectionDeletion: Cascade deleted {Count} documents", documentIds.Count);
            }

            // Step 3: Delete the collection from Dolt collections table (if table exists)
            bool collectionsTableExists = tablesResult?.Any(row => row.Values.Any(v => v.ToString() == "collections")) ?? false;
            if (collectionsTableExists)
            {
                await _dolt.QueryAsync<object>($"DELETE FROM collections WHERE collection_name = '{deletion.CollectionName}'");
                _logger.LogInformation("ProcessCollectionDeletion: Deleted collection '{Collection}' from Dolt", deletion.CollectionName);
            }
            else
            {
                _logger.LogInformation("ProcessCollectionDeletion: Collections table doesn't exist - no collection record to delete");
            }
        }

        /// <summary>
        /// Process a collection rename operation
        /// </summary>
        private async Task ProcessCollectionRename(CollectionDeletionRecord rename, CollectionSyncResult result)
        {
            _logger.LogInformation("ProcessCollectionRename: Processing rename from '{OldName}' to '{NewName}'", 
                rename.OriginalName, rename.NewName);

            // Step 1: Update collection name in collections table
            await _dolt.QueryAsync<object>($"UPDATE collections SET collection_name = '{rename.NewName}' WHERE collection_name = '{rename.OriginalName}'");
            
            // Step 2: Update collection name in documents table
            await _dolt.QueryAsync<object>($"UPDATE documents SET collection_name = '{rename.NewName}' WHERE collection_name = '{rename.OriginalName}'");
            
            _logger.LogInformation("ProcessCollectionRename: Renamed collection from '{OldName}' to '{NewName}'", 
                rename.OriginalName, rename.NewName);
        }

        /// <summary>
        /// Process a collection metadata update operation
        /// </summary>
        private async Task ProcessCollectionMetadataUpdate(CollectionDeletionRecord update, CollectionSyncResult result)
        {
            _logger.LogInformation("ProcessCollectionMetadataUpdate: Processing metadata update for collection '{Collection}'", 
                update.CollectionName);

            // Parse the new metadata from the record
            var newMetadata = string.IsNullOrEmpty(update.NewName) ? new Dictionary<string, object>() : 
                JsonSerializer.Deserialize<Dictionary<string, object>>(update.NewName) ?? new Dictionary<string, object>();

            var metadataJson = JsonSerializer.Serialize(newMetadata);

            // Update collection metadata in collections table
            await _dolt.QueryAsync<object>($"UPDATE collections SET metadata = '{metadataJson}' WHERE collection_name = '{update.CollectionName}'");
            
            _logger.LogInformation("ProcessCollectionMetadataUpdate: Updated metadata for collection '{Collection}'", 
                update.CollectionName);
        }

        #endregion
    }
}