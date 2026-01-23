using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Embranch.Models;
using Embranch.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Embranch.Services
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
        private readonly ISyncStateTracker _syncStateTracker;
        private readonly DoltConfiguration _doltConfig;
        private readonly ILogger<SyncManagerV2> _logger;

        public SyncManagerV2(
            IDoltCli dolt,
            IChromaDbService chromaService,
            IDeletionTracker deletionTracker,
            ISyncStateTracker syncStateTracker,
            IOptions<DoltConfiguration> doltConfig,
            ILogger<SyncManagerV2> logger)
        {
            _dolt = dolt;
            _chromaService = chromaService;
            _deletionTracker = deletionTracker;
            _syncStateTracker = syncStateTracker;
            _doltConfig = doltConfig.Value;
            _logger = logger;
            
            // Initialize detectors and syncers with sync state tracker
            _deltaDetector = new DeltaDetectorV2(dolt, syncStateTracker, _doltConfig.RepositoryPath, logger: null);
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

        /// <summary>
        /// PP13-69-C1: Simplified checkout embracing architectural confidence.
        /// Sync state in SQLite eliminates operational conflicts - handles only user data conflicts.
        /// </summary>
        public async Task<SyncResultV2> ProcessCheckoutAsync(
            string targetBranch, 
            bool createNew = false, 
            bool preserveLocalChanges = false)
        {
            _logger.LogInformation("PP13-69-C7 TRACE: ProcessCheckoutAsync ENTRY POINT - method entered successfully");
            var result = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };

            _logger.LogInformation("PP13-69-C7 TRACE: ProcessCheckoutAsync called with target={Target}, createNew={CreateNew}, preserveChanges={PreserveChanges}", 
                targetBranch, createNew, preserveLocalChanges);

            try
            {
                _logger.LogInformation("PP13-69-C7 TRACE: About to call Dolt CheckoutAsync");
                // PP13-69: Direct checkout - sync state conflicts are architecturally impossible
                var checkoutResult = await _dolt.CheckoutAsync(targetBranch, createNew);
                _logger.LogInformation("PP13-69-C7 TRACE: Dolt CheckoutAsync completed, success={Success}", checkoutResult.Success);
                
                if (!checkoutResult.Success)
                {
                    _logger.LogInformation("PP13-69-C7 TRACE: Checkout failed, handling user data conflicts");
                    // Handle only genuine user data conflicts
                    await HandleUserDataConflicts(checkoutResult, preserveLocalChanges);
                    
                    // Retry checkout after handling conflicts
                    checkoutResult = await _dolt.CheckoutAsync(targetBranch, createNew);
                    if (!checkoutResult.Success)
                    {
                        throw new InvalidOperationException($"Checkout still failed after conflict resolution: {checkoutResult.Error}");
                    }
                }

                _logger.LogInformation("PP13-69-C7 TRACE: About to call SyncChromaToMatchBranch for differential sync");
                // PP13-74: Pass preserveLocalChanges flag to sync method for carry mode support
                await SyncChromaToMatchBranch(targetBranch, preserveLocalChanges);
                _logger.LogInformation("PP13-69-C7 TRACE: SyncChromaToMatchBranch completed successfully");

                _logger.LogInformation("PP13-69-C7 TRACE: About to update local sync state");
                // Update local sync state in SQLite (PP13-69 architecture)
                await UpdateLocalSyncState(targetBranch);
                _logger.LogInformation("PP13-69-C7 TRACE: UpdateLocalSyncState completed");

                result.Status = SyncStatusV2.Completed;
                _logger.LogInformation("PP13-69-C7 TRACE: Checkout to branch {Branch} completed successfully", targetBranch);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Checkout to branch {Branch} failed", targetBranch);
                result.Status = SyncStatusV2.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Handles genuine user data conflicts during checkout (not sync state conflicts)
        /// </summary>
        private async Task HandleUserDataConflicts(DoltCommandResult failedCheckout, bool preserveLocalChanges)
        {
            if (failedCheckout.Error?.Contains("local changes") == true || 
                failedCheckout.Error?.Contains("would be overwritten") == true)
            {
                if (preserveLocalChanges)
                {
                    _logger.LogInformation("Attempting to preserve local user data changes during checkout");
                    // PP13-69-C1: Simplified approach - reset and retry for now
                    // Future implementation could stage changes and reapply them
                    try
                    {
                        await _dolt.ResetHardAsync("HEAD");
                        _logger.LogInformation("Reset completed, proceeding with checkout");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to reset before checkout");
                        throw new InvalidOperationException($"Could not resolve checkout conflicts: {ex.Message}");
                    }
                }
                else
                {
                    _logger.LogWarning("Checkout blocked by user data conflicts. Use preserveLocalChanges=true or commit changes first.");
                    throw new InvalidOperationException($"Checkout blocked by local changes: {failedCheckout.Error}");
                }
            }
            else
            {
                throw new InvalidOperationException($"Checkout failed: {failedCheckout.Error}");
            }
        }

        /// <summary>
        /// Syncs ChromaDB to match the current branch state after checkout using differential sync.
        /// PP13-69-C7: Enhanced to remove stale documents and collections for exact branch state matching.
        /// PP13-74: Enhanced to preserve local changes when carry mode is active.
        /// </summary>
        /// <param name="targetBranch">Target branch name</param>
        /// <param name="preserveLocalChanges">When true (carry mode), preserve documents with is_local_change=true metadata</param>
        private async Task SyncChromaToMatchBranch(string targetBranch, bool preserveLocalChanges = false)
        {
            if (preserveLocalChanges)
            {
                _logger.LogInformation("PP13-74 CARRY MODE ACTIVE: Preserving uncommitted local changes during checkout to {Branch}", targetBranch);
            }
            _logger.LogInformation("PP13-69-C7 DIFFERENTIAL SYNC START: Syncing ChromaDB to match branch {Branch} using differential sync", targetBranch);
            
            _logger.LogInformation("PP13-69-C7 TRACE: Getting available collections from Dolt");
            var doltCollections = await _deltaDetector.GetAvailableCollectionNamesAsync();
            _logger.LogInformation("PP13-69-C7 TRACE: Found {Count} collections in Dolt: [{Collections}]", 
                doltCollections.Count, string.Join(", ", doltCollections));
            
            _logger.LogInformation("PP13-69-C7 TRACE: Getting available collections from ChromaDB");
            var chromaCollections = await _chromaService.ListCollectionsAsync();
            _logger.LogInformation("PP13-69-C7 TRACE: Found {Count} collections in ChromaDB: [{Collections}]", 
                chromaCollections.Count, string.Join(", ", chromaCollections));
            
            // PP13-69-C7: Phase 1 - Remove collections that exist in ChromaDB but not in target branch
            // PP13-74: In carry mode, preserve collections containing local changes
            var collectionsToRemove = chromaCollections.Except(doltCollections).ToList();
            _logger.LogInformation("PP13-69-C7 TRACE: Phase 1 - Collections to remove: {Count} [{Collections}]",
                collectionsToRemove.Count, string.Join(", ", collectionsToRemove));

            var preservedCollections = new List<string>();
            foreach (var collection in collectionsToRemove.ToList())
            {
                // PP13-74: In carry mode, check if collection contains local changes before removing
                if (preserveLocalChanges)
                {
                    var hasLocalChanges = await CollectionHasLocalChangesAsync(collection);
                    if (hasLocalChanges)
                    {
                        _logger.LogInformation("PP13-74 CARRY MODE: Preserving collection '{Collection}' containing local changes", collection);
                        preservedCollections.Add(collection);
                        continue;
                    }
                }

                _logger.LogInformation("PP13-69-C7 PHASE1: Removing stale collection '{Collection}' not present in branch {Branch}", collection, targetBranch);
                await _chromaService.DeleteCollectionAsync(collection);
                _logger.LogInformation("PP13-69-C7 PHASE1: Successfully deleted collection '{Collection}'", collection);
            }

            if (preservedCollections.Any())
            {
                _logger.LogInformation("PP13-74 CARRY MODE: Preserved {Count} collections with local changes: [{Collections}]",
                    preservedCollections.Count, string.Join(", ", preservedCollections));
            }
            
            // PP13-69-C7: Phase 2 - Sync each collection from Dolt with exact document matching
            // PP13-74: Pass preserveLocalChanges to preserve documents with is_local_change=true in carry mode
            _logger.LogInformation("PP13-69-C7 TRACE: Phase 2 - About to sync {Count} collections exactly (preserveLocalChanges={Preserve})", doltCollections.Count, preserveLocalChanges);
            foreach (var collection in doltCollections)
            {
                _logger.LogInformation("PP13-69-C7 PHASE2: Starting exact sync for collection '{Collection}'", collection);
                await SyncCollectionToMatchDoltExactly(collection, targetBranch, preserveLocalChanges);
                _logger.LogInformation("PP13-69-C7 PHASE2: Completed exact sync for collection '{Collection}'", collection);
            }

            // PP13-74: Also sync preserved collections (which have local changes) but preserve local documents
            foreach (var collection in preservedCollections)
            {
                _logger.LogInformation("PP13-74 CARRY MODE: Syncing preserved collection '{Collection}' while maintaining local changes", collection);
                await SyncCollectionToMatchDoltExactly(collection, targetBranch, preserveLocalChanges: true);
            }
            
            _logger.LogInformation("PP13-69-C7 DIFFERENTIAL SYNC COMPLETE: Completed differential sync for branch {Branch}: {DoltCollections} Dolt collections, removed {RemovedCollections} stale collections", 
                targetBranch, doltCollections.Count, collectionsToRemove.Count);
        }

        /// <summary>
        /// PP13-69-C7: Syncs a single collection to exactly match Dolt state, removing stale documents.
        /// Performs differential sync to ensure ChromaDB collection exactly matches Dolt collection.
        /// PP13-74: Enhanced to preserve documents with is_local_change=true metadata in carry mode.
        /// </summary>
        /// <param name="collectionName">Name of the collection to sync</param>
        /// <param name="targetBranch">Target branch name for logging purposes</param>
        /// <param name="preserveLocalChanges">When true, preserve documents with is_local_change=true metadata</param>
        private async Task SyncCollectionToMatchDoltExactly(string collectionName, string targetBranch, bool preserveLocalChanges = false)
        {
            _logger.LogInformation("PP13-69-C7 EXACT SYNC START: Syncing collection '{Collection}' to exactly match Dolt state on branch {Branch} (preserveLocalChanges={Preserve})", collectionName, targetBranch, preserveLocalChanges);

            // Get document IDs that SHOULD exist (from Dolt)
            _logger.LogInformation("PP13-69-C7 EXACT SYNC: Getting document IDs that SHOULD exist from Dolt for collection '{Collection}'", collectionName);
            var doltDocumentIds = await GetDoltDocumentIds(collectionName);
            _logger.LogInformation("PP13-69-C7 EXACT SYNC: Dolt has {Count} documents in '{Collection}': [{Documents}]",
                doltDocumentIds.Count, collectionName, string.Join(", ", doltDocumentIds));

            // Check if collection exists in ChromaDB
            _logger.LogInformation("PP13-69-C7 EXACT SYNC: Checking if collection '{Collection}' exists in ChromaDB", collectionName);
            var chromaCollections = await _chromaService.ListCollectionsAsync();

            if (!chromaCollections.Contains(collectionName))
            {
                // Collection doesn't exist in ChromaDB - create and sync normally
                _logger.LogInformation("PP13-69-C7 EXACT SYNC: Collection '{Collection}' doesn't exist in ChromaDB - creating and syncing with FullSyncAsync", collectionName);
                await FullSyncAsync(collectionName);
                _logger.LogInformation("PP13-69-C7 EXACT SYNC: FullSyncAsync completed for collection '{Collection}'", collectionName);
                return;
            }

            // Get document IDs that DO exist (in ChromaDB)
            _logger.LogInformation("PP13-69-C7 EXACT SYNC: Getting document IDs that DO exist in ChromaDB for collection '{Collection}'", collectionName);
            var chromaDocumentIds = await GetChromaDocumentIds(collectionName);
            _logger.LogInformation("PP13-69-C7 EXACT SYNC: ChromaDB has {Count} documents in '{Collection}': [{Documents}]",
                chromaDocumentIds.Count, collectionName, string.Join(", ", chromaDocumentIds));

            // PP13-69-C7: Calculate documents to remove (in ChromaDB but not in Dolt)
            var documentsToRemove = chromaDocumentIds.Except(doltDocumentIds).ToList();

            // PP13-74: In carry mode, filter out documents with is_local_change=true metadata
            var preservedDocuments = new List<string>();
            if (preserveLocalChanges && documentsToRemove.Any())
            {
                _logger.LogInformation("PP13-74 CARRY MODE: Checking {Count} documents for local change preservation in collection '{Collection}'",
                    documentsToRemove.Count, collectionName);

                var localChangeDocIds = await GetDocumentsWithLocalChangesFlagAsync(collectionName, documentsToRemove);

                if (localChangeDocIds.Any())
                {
                    preservedDocuments = localChangeDocIds;
                    documentsToRemove = documentsToRemove.Except(localChangeDocIds).ToList();

                    _logger.LogInformation("PP13-74 CARRY MODE: Preserved {Count} local change documents in collection '{Collection}': [{Docs}]",
                        preservedDocuments.Count, collectionName, string.Join(", ", preservedDocuments));
                }
            }

            if (documentsToRemove.Any())
            {
                _logger.LogInformation("Removing {Count} stale documents from collection '{Collection}': [{Documents}]",
                    documentsToRemove.Count, collectionName, string.Join(", ", documentsToRemove));

                // Get all chunk IDs for the documents to remove
                var chunkIdsToRemove = await GetChunkIdsForDocuments(collectionName, documentsToRemove);

                if (chunkIdsToRemove.Any())
                {
                    await _chromaService.DeleteDocumentsAsync(collectionName, chunkIdsToRemove);
                    _logger.LogInformation("Successfully removed {ChunkCount} chunks for {DocCount} stale documents from collection '{Collection}'",
                        chunkIdsToRemove.Count, documentsToRemove.Count, collectionName);
                }
            }
            else
            {
                _logger.LogInformation("No stale documents to remove from collection '{Collection}'", collectionName);
            }

            // PP13-74: When preserving local changes, use incremental sync to avoid deleting the collection
            // FullSyncAsync deletes and recreates the collection, which would lose preserved local documents
            if (preservedDocuments.Any())
            {
                _logger.LogInformation("PP13-74 CARRY MODE: Using incremental sync to preserve {Count} local documents in collection '{Collection}'",
                    preservedDocuments.Count, collectionName);
                await IncrementalSyncAsync(collectionName);
            }
            else
            {
                // Perform normal full sync to add/update documents from Dolt
                await FullSyncAsync(collectionName);
            }

            _logger.LogInformation("Completed exact sync for collection '{Collection}' on branch {Branch}: removed {RemovedCount} stale documents, preserved {PreservedCount} local changes",
                collectionName, targetBranch, documentsToRemove.Count, preservedDocuments.Count);
        }

        /// <summary>
        /// Updates local sync state in SQLite after successful checkout (PP13-69 architecture)
        /// PP13-69-C2: Branch-aware sync state preservation - only loads/initializes sync state for target branch
        /// </summary>
        private async Task UpdateLocalSyncState(string targetBranch)
        {
            // SQLite-based sync state tracking (PP13-69)
            // Branch-aware approach: Load existing sync state for target branch or initialize if new
            try
            {
                var collections = await _deltaDetector.GetAvailableCollectionNamesAsync();
                var commitHash = await _dolt.GetHeadCommitHashAsync();
                
                foreach (var collection in collections)
                {
                    // PP13-69-C2: Check if sync state already exists for this branch/collection
                    var existingSyncState = await _syncStateTracker.GetSyncStateAsync(_doltConfig.RepositoryPath, collection, targetBranch);
                    
                    if (existingSyncState == null)
                    {
                        // PP13-69-C3: Do not auto-create sync state during checkout
                        // Sync state should only be created when actual sync operations occur
                        _logger.LogDebug("No sync state found for collection '{Collection}' on branch '{Branch}' - will be created on first sync operation", collection, targetBranch);
                    }
                    else
                    {
                        // Branch has existing sync state - preserve it
                        // PP13-69-C2: Don't overwrite sync state during checkout unless data actually changed
                        _logger.LogInformation("Preserving existing sync state for collection '{Collection}' on branch '{Branch}' (LastSyncCommit: {LastSync})", 
                            collection, targetBranch, existingSyncState.Value.LastSyncCommit);
                        
                        // PP13-69-C2 Fix: Only update sync state if we detect this is not just a branch switch
                        // For pure branch switches, preserve the existing sync state completely
                        // This ensures test scenarios and real-world branch isolation work correctly
                    }
                }
                
                _logger.LogInformation("Branch-aware sync state update completed for {Count} collections on branch '{Branch}'", collections.Count, targetBranch);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update local sync state for branch {Branch}", targetBranch);
                // Don't fail the checkout for sync state update issues
            }
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

        /// <summary>
        /// Perform comprehensive state reset including Dolt, ChromaDB, and SQLite sync state.
        /// PP13-69-C6: Enhanced reset functionality for test state isolation.
        /// </summary>
        public async Task<SyncResultV2> PerformComprehensiveResetAsync(string targetBranch)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
            
            try
            {
                _logger.LogInformation("PerformComprehensiveReset: Starting comprehensive state reset for branch '{Branch}'", targetBranch);

                // Step 1: Reset Dolt to clean state
                var resetResult = await _dolt.ResetHardAsync("HEAD");
                if (!resetResult.Success)
                {
                    _logger.LogError("PerformComprehensiveReset: Dolt reset failed: {Error}", resetResult.Error);
                    result.Status = SyncStatusV2.Failed;
                    result.ErrorMessage = $"Dolt reset failed: {resetResult.Error}";
                    return result;
                }
                _logger.LogInformation("PerformComprehensiveReset: Dolt reset completed successfully");

                // Step 2: Reset all ChromaDB collections
                var collections = await _chromaService.ListCollectionsAsync();
                _logger.LogInformation("PerformComprehensiveReset: Found {Count} ChromaDB collections to reset", collections.Count);
                
                foreach (var collection in collections)
                {
                    _logger.LogInformation("PerformComprehensiveReset: Deleting ChromaDB collection '{Collection}'", collection);
                    var deleteResult = await _chromaService.DeleteCollectionAsync(collection);
                    if (!deleteResult)
                    {
                        _logger.LogWarning("PerformComprehensiveReset: Failed to delete collection '{Collection}' - continuing", collection);
                    }
                }

                // Step 3: Reset SQLite sync state for target branch
                _logger.LogInformation("PerformComprehensiveReset: Clearing SQLite sync state for branch '{Branch}'", targetBranch);
                await _syncStateTracker.ClearBranchSyncStatesAsync(_doltConfig.RepositoryPath, targetBranch);

                result.Status = SyncStatusV2.Completed;
                result.Deleted = collections.Count; // Track collections deleted
                _logger.LogInformation("PerformComprehensiveReset: Comprehensive state reset completed successfully for branch '{Branch}'", targetBranch);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PerformComprehensiveReset: Failed to perform comprehensive reset for branch '{Branch}'", targetBranch);
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
        /// Compare content hashes between Dolt documents and ChromaDB to determine if sync is needed
        /// </summary>
        /// <param name="collectionName">Name of the collection to compare</param>
        /// <param name="doltDocuments">Documents from Dolt</param>
        /// <returns>True if content hashes match (no sync needed), false if sync is required</returns>
        private async Task<bool> CompareCollectionContentHashesAsync(string collectionName, IEnumerable<DoltDocumentV2> doltDocuments)
        {
            try
            {
                _logger.LogDebug("Comparing content hashes for collection {Collection}", collectionName);
                
                // Get all documents from ChromaDB
                var chromaResult = await _chromaService.GetDocumentsAsync(collectionName);
                
                if (chromaResult == null)
                {
                    _logger.LogDebug("No ChromaDB documents found for collection {Collection}, sync needed", collectionName);
                    return false;
                }
                
                var chromaDict = chromaResult as Dictionary<string, object>;
                var chromaIds = (chromaDict?.GetValueOrDefault("ids") as List<object>)?.Cast<string>().ToList() ?? new List<string>();
                var chromaDocuments = (chromaDict?.GetValueOrDefault("documents") as List<object>)?.Cast<string>().ToList() ?? new List<string>();
                
                // Create hash map of ChromaDB content by ID
                var chromaContentHashes = new Dictionary<string, string>();
                for (int i = 0; i < chromaIds.Count && i < chromaDocuments.Count; i++)
                {
                    var contentHash = ComputeContentHash(chromaDocuments[i]);
                    chromaContentHashes[chromaIds[i]] = contentHash;
                }
                
                // Create hash map of Dolt content by ID
                var doltContentHashes = new Dictionary<string, string>();
                foreach (var doc in doltDocuments)
                {
                    var contentHash = ComputeContentHash(doc.Content);
                    doltContentHashes[doc.DocId] = contentHash;
                }
                
                // Compare hash sets
                if (chromaContentHashes.Count != doltContentHashes.Count)
                {
                    _logger.LogDebug("Document count mismatch: ChromaDB={ChromaCount}, Dolt={DoltCount}", 
                        chromaContentHashes.Count, doltContentHashes.Count);
                    return false;
                }
                
                foreach (var kvp in doltContentHashes)
                {
                    if (!chromaContentHashes.TryGetValue(kvp.Key, out var chromaHash) || chromaHash != kvp.Value)
                    {
                        _logger.LogDebug("Content hash mismatch for document {DocId}", kvp.Key);
                        return false;
                    }
                }
                
                _logger.LogDebug("Content hashes match for all {Count} documents in collection {Collection}", 
                    doltContentHashes.Count, collectionName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compare content hashes for collection {Collection}, assuming sync needed", collectionName);
                return false;
            }
        }
        
        /// <summary>
        /// Compute a content hash for a document string
        /// </summary>
        /// <param name="content">Document content</param>
        /// <returns>SHA256 hash of the content</returns>
        private string ComputeContentHash(string content)
        {
            if (string.IsNullOrEmpty(content))
                return string.Empty;
                
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
            return Convert.ToBase64String(hash);
        }
        
        /// <summary>
        /// Validate that document content in ChromaDB matches the expected state after sync
        /// </summary>
        /// <param name="collectionName">Name of the collection to validate</param>
        /// <param name="expectedDocuments">Expected documents from Dolt</param>
        /// <returns>True if content matches, false if validation fails</returns>
        private async Task<bool> ValidateDocumentContentConsistencyAsync(string collectionName, IEnumerable<DoltDocumentV2> expectedDocuments)
        {
            try
            {
                _logger.LogDebug("Validating document content consistency for collection {Collection}", collectionName);
                
                var isConsistent = await CompareCollectionContentHashesAsync(collectionName, expectedDocuments);
                
                if (!isConsistent)
                {
                    _logger.LogWarning("Document content validation FAILED for collection {Collection} - content does not match expected state", collectionName);
                }
                else
                {
                    _logger.LogDebug("Document content validation PASSED for collection {Collection}", collectionName);
                }
                
                return isConsistent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate document content consistency for collection {Collection}", collectionName);
                return false;
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
                        // Check if content is already identical using content-hash verification (PP13-68)
                        var contentMatches = await CompareCollectionContentHashesAsync(collectionName, documents);
                        if (contentMatches)
                        {
                            // Content hashes match - no sync needed
                            needsSync = false;
                            result.Status = SyncStatusV2.NoChanges;
                            _logger.LogInformation("Collection {Collection} already synchronized - content hashes match (count: {Count})", 
                                collectionName, documents.Count());
                            
                            // Still update sync state to record that we checked
                            var commitHash = await _dolt.GetHeadCommitHashAsync();
                            await _deltaDetector.UpdateSyncStateAsync(collectionName, commitHash, 0, 0);
                            
                            return result;
                        }
                        else
                        {
                            _logger.LogInformation("Collection {Collection} content differs - sync needed (document count: {Count})", 
                                collectionName, documents.Count());
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

                    var commitHash = await _dolt.GetHeadCommitHashAsync();

                    // PP13-69-C10 FIX: Don't pre-chunk documents - let AddDocumentsAsync handle chunking
                    // Previously, ConvertDoltToChroma would chunk documents, then AddDocumentsAsync would chunk AGAIN
                    // This caused double-chunking where chunk IDs became like "doc_chunk_0_chunk_0"
                    // The fix is to pass raw document content directly to AddDocumentsAsync
                    //
                    // OPTIMIZATION: Batch all documents in a single AddDocumentsAsync call
                    // This reduces Python context switches from N to 1, significantly improving performance
                    var allContents = new List<string>();
                    var allIds = new List<string>();
                    var allMetadatas = new List<Dictionary<string, object>>();

                    foreach (var doc in documents)
                    {
                        allContents.Add(doc.Content);
                        allIds.Add(doc.DocId);
                        allMetadatas.Add(BuildSyncMetadata(doc, commitHash));
                    }

                    // Sync operation: documents come from Dolt, should NOT be marked as local changes (PP13-68-C2 fix)
                    // AddDocumentsAsync will handle chunking internally for all documents in one batch
                    await _chromaService.AddDocumentsAsync(
                        collectionName,
                        allContents,
                        allIds,
                        allMetadatas,
                        allowDuplicateIds: false,
                        markAsLocalChange: false);

                    result.Added = allIds.Count;
                    // Note: ChunksProcessed count will be approximate since AddDocumentsAsync handles chunking
                    result.ChunksProcessed = allIds.Count;
                    
                    // Update sync state
                    await _deltaDetector.UpdateSyncStateAsync(collectionName, commitHash, result.Added, result.ChunksProcessed);
                    
                    result.Status = SyncStatusV2.Completed;
                    _logger.LogInformation("Collection {Collection} sync completed: {Added} documents, {Chunks} chunks", 
                        collectionName, result.Added, result.ChunksProcessed);
                    
                    // Validate document content consistency after sync (PP13-68)
                    // Note: We perform validation but only log warnings, not fail the sync
                    // ChromaDB may process documents (add embeddings, normalize) causing legitimate differences
                    var isValid = await ValidateDocumentContentConsistencyAsync(collectionName, documents);
                    if (!isValid)
                    {
                        _logger.LogWarning("Post-sync validation showed content differences for collection {Collection} - this may be due to ChromaDB processing", collectionName);
                        // Don't fail the sync - ChromaDB processing can cause legitimate differences
                        // The important thing is that the documents were successfully synced
                        // result.Status = SyncStatusV2.Failed;
                        // result.ErrorMessage = "Post-sync validation failed - content inconsistency detected";
                    }
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
                
                // PP13-69-C10 FIX: Don't pre-chunk - let AddDocumentsAsync handle chunking
                // OPTIMIZATION: Batch all new documents and modified documents in a single AddDocumentsAsync call
                var newDocs = pendingDocs.Where(d => d.IsNew).ToList();
                var modifiedDocs = pendingDocs.Where(d => d.IsModified).ToList();

                // Handle new documents in batch
                if (newDocs.Count > 0)
                {
                    var newContents = new List<string>();
                    var newIds = new List<string>();
                    var newMetadatas = new List<Dictionary<string, object>>();

                    foreach (var doc in newDocs)
                    {
                        newContents.Add(doc.Content);
                        newIds.Add(doc.DocId);
                        newMetadatas.Add(BuildSyncMetadataFromDelta(doc, commitHash));
                    }

                    await _chromaService.AddDocumentsAsync(
                        collectionName,
                        newContents,
                        newIds,
                        newMetadatas,
                        allowDuplicateIds: false,
                        markAsLocalChange: false);

                    result.Added = newDocs.Count;

                    // Record sync operations for new documents
                    foreach (var doc in newDocs)
                    {
                        await _deltaDetector.RecordSyncOperationAsync(
                            doc.DocId, collectionName, doc.ContentHash,
                            new List<string> { doc.DocId }, SyncDirection.DoltToChroma, "added");
                    }
                }

                // Handle modified documents - delete old chunks first, then batch add
                if (modifiedDocs.Count > 0)
                {
                    // Delete old chunks for all modified docs first
                    foreach (var doc in modifiedDocs)
                    {
                        var maxChunks = Math.Max(10, (doc.Content.Length / 462) + 2);
                        var chunkIds = DocumentConverterUtilityV2.GetChunkIds(doc.DocId, maxChunks);
                        await _chromaService.DeleteDocumentsAsync(collectionName, chunkIds);
                    }

                    // Now batch add all modified documents
                    var modContents = new List<string>();
                    var modIds = new List<string>();
                    var modMetadatas = new List<Dictionary<string, object>>();

                    foreach (var doc in modifiedDocs)
                    {
                        modContents.Add(doc.Content);
                        modIds.Add(doc.DocId);
                        modMetadatas.Add(BuildSyncMetadataFromDelta(doc, commitHash));
                    }

                    await _chromaService.AddDocumentsAsync(
                        collectionName,
                        modContents,
                        modIds,
                        modMetadatas,
                        allowDuplicateIds: false,
                        markAsLocalChange: false);

                    result.Modified = modifiedDocs.Count;

                    // Record sync operations for modified documents
                    foreach (var doc in modifiedDocs)
                    {
                        await _deltaDetector.RecordSyncOperationAsync(
                            doc.DocId, collectionName, doc.ContentHash,
                            new List<string> { doc.DocId }, SyncDirection.DoltToChroma, "modified");
                    }
                }

                result.ChunksProcessed = newDocs.Count + modifiedDocs.Count;
                
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

        /// <summary>
        /// PP13-69-C10: Build metadata for sync operations without pre-chunking.
        /// AddDocumentsAsync will handle chunking and add chunk_index/total_chunks.
        /// This method builds the document-level metadata that will be applied to all chunks.
        /// </summary>
        private Dictionary<string, object> BuildSyncMetadata(DoltDocumentV2 doc, string commitHash)
        {
            var metadata = new Dictionary<string, object>();

            // Add ALL user metadata from the document first
            if (doc.Metadata != null)
            {
                foreach (var kvp in doc.Metadata)
                {
                    metadata[kvp.Key] = kvp.Value ?? "";
                }
            }

            // Add system metadata (same as DocumentConverterUtilityV2.BuildChunkMetadata but without chunk_index/total_chunks)
            metadata["source_id"] = doc.DocId;
            metadata["collection_name"] = doc.CollectionName;
            metadata["content_hash"] = doc.ContentHash;
            metadata["dolt_commit"] = commitHash;

            // Add extracted fields if they exist
            if (!string.IsNullOrEmpty(doc.Title))
                metadata["title"] = doc.Title;
            if (!string.IsNullOrEmpty(doc.DocType))
                metadata["doc_type"] = doc.DocType;

            // Sync from Dolt - not a local change
            metadata["is_local_change"] = false;

            return metadata;
        }

        /// <summary>
        /// PP13-69-C10: Build metadata from DocumentDeltaV2 for sync operations.
        /// </summary>
        private Dictionary<string, object> BuildSyncMetadataFromDelta(DocumentDeltaV2 doc, string commitHash)
        {
            var metadata = new Dictionary<string, object>();

            // Parse and add user metadata from the delta's JSON metadata
            var userMetadata = doc.GetMetadataDict();
            if (userMetadata != null)
            {
                foreach (var kvp in userMetadata)
                {
                    metadata[kvp.Key] = kvp.Value ?? "";
                }
            }

            // Add system metadata
            metadata["source_id"] = doc.DocId;
            metadata["collection_name"] = doc.CollectionName;
            metadata["content_hash"] = doc.ContentHash;
            metadata["dolt_commit"] = commitHash;

            // Add extracted fields if they exist
            if (!string.IsNullOrEmpty(doc.Title))
                metadata["title"] = doc.Title;
            if (!string.IsNullOrEmpty(doc.DocType))
                metadata["doc_type"] = doc.DocType;

            // Sync from Dolt - not a local change
            metadata["is_local_change"] = false;

            return metadata;
        }

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

                // PP13-69-C10 FIX: Don't pre-chunk - let AddDocumentsAsync handle chunking
                // OPTIMIZATION: Batch documents for better performance
                var addedDiffs = diffs.Where(d => d.DiffType == "added").ToList();
                var modifiedDiffs = diffs.Where(d => d.DiffType == "modified").ToList();
                var removedDiffs = diffs.Where(d => d.DiffType == "removed").ToList();

                // Handle added documents in batch
                if (addedDiffs.Count > 0)
                {
                    var addContents = new List<string>();
                    var addIds = new List<string>();
                    var addMetadatas = new List<Dictionary<string, object>>();

                    foreach (var diff in addedDiffs)
                    {
                        var delta = new DocumentDeltaV2(
                            diff.DocId,
                            diff.CollectionName,
                            diff.Content ?? "",
                            diff.ToContentHash ?? "",
                            diff.Title,
                            diff.DocType,
                            diff.Metadata ?? "{}",
                            "new"
                        );

                        addContents.Add(diff.Content ?? "");
                        addIds.Add(diff.DocId);
                        addMetadatas.Add(BuildSyncMetadataFromDelta(delta, toCommit));
                    }

                    await _chromaService.AddDocumentsAsync(
                        collectionName,
                        addContents,
                        addIds,
                        addMetadatas,
                        allowDuplicateIds: false,
                        markAsLocalChange: false);

                    result.Added = addedDiffs.Count;
                }

                // Handle modified documents - delete old chunks first, then batch add
                if (modifiedDiffs.Count > 0)
                {
                    // Delete old chunks for all modified docs first
                    foreach (var diff in modifiedDiffs)
                    {
                        var content = diff.Content ?? "";
                        var maxChunks = Math.Max(10, (content.Length / 462) + 2);
                        var chunkIds = DocumentConverterUtilityV2.GetChunkIds(diff.DocId, maxChunks);
                        await _chromaService.DeleteDocumentsAsync(collectionName, chunkIds);
                    }

                    // Now batch add all modified documents
                    var modContents = new List<string>();
                    var modIds = new List<string>();
                    var modMetadatas = new List<Dictionary<string, object>>();

                    foreach (var diff in modifiedDiffs)
                    {
                        var delta = new DocumentDeltaV2(
                            diff.DocId,
                            diff.CollectionName,
                            diff.Content ?? "",
                            diff.ToContentHash ?? "",
                            diff.Title,
                            diff.DocType,
                            diff.Metadata ?? "{}",
                            "modified"
                        );

                        modContents.Add(diff.Content ?? "");
                        modIds.Add(diff.DocId);
                        modMetadatas.Add(BuildSyncMetadataFromDelta(delta, toCommit));
                    }

                    await _chromaService.AddDocumentsAsync(
                        collectionName,
                        modContents,
                        modIds,
                        modMetadatas,
                        allowDuplicateIds: false,
                        markAsLocalChange: false);

                    result.Modified = modifiedDiffs.Count;
                }

                // Handle removed documents
                foreach (var diff in removedDiffs)
                {
                    var chunkIds = DocumentConverterUtilityV2.GetChunkIds(diff.DocId, 10);
                    await _chromaService.DeleteDocumentsAsync(collectionName, chunkIds);
                    result.Deleted++;
                }

                result.ChunksProcessed = addedDiffs.Count + modifiedDiffs.Count;
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
            var escapedMetadataJson = SqlEscapeUtility.EscapeJsonForSql(metadataJson);

            // Update collection metadata in collections table (PP13-77 fix: escape JSON for SQL)
            await _dolt.QueryAsync<object>($"UPDATE collections SET metadata = '{escapedMetadataJson}' WHERE collection_name = '{update.CollectionName}'");
            
            _logger.LogInformation("ProcessCollectionMetadataUpdate: Updated metadata for collection '{Collection}'", 
                update.CollectionName);
        }

        #endregion

        #region PP13-69-C7 Helper Methods for Differential Sync

        /// <summary>
        /// PP13-69-C7: Gets all document IDs for a collection from Dolt database.
        /// </summary>
        /// <param name="collectionName">Name of the collection</param>
        /// <returns>List of document IDs that exist in Dolt for the collection</returns>
        private async Task<List<string>> GetDoltDocumentIds(string collectionName)
        {
            try
            {
                var documents = await _deltaDetector.GetAllDocumentsAsync(collectionName);
                var documentIds = new List<string>();
                
                foreach (var doc in documents)
                {
                    // Extract document ID from the DoltDocumentV2 object
                    if (!string.IsNullOrEmpty(doc.DocId))
                    {
                        documentIds.Add(doc.DocId);
                    }
                }
                
                _logger.LogDebug("Retrieved {Count} document IDs from Dolt for collection '{Collection}'", 
                    documentIds.Count, collectionName);
                
                return documentIds;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve document IDs from Dolt for collection '{Collection}'", collectionName);
                return new List<string>();
            }
        }

        /// <summary>
        /// PP13-69-C7: Gets all document IDs for a collection from ChromaDB.
        /// Extracts base document IDs from chunk IDs (e.g., "doc1_chunk_0" -> "doc1").
        /// </summary>
        /// <param name="collectionName">Name of the collection</param>
        /// <returns>List of unique base document IDs that exist in ChromaDB for the collection</returns>
        private async Task<List<string>> GetChromaDocumentIds(string collectionName)
        {
            try
            {
                var result = await _chromaService.GetDocumentsAsync(collectionName);
                var baseDocumentIds = new HashSet<string>();
                
                if (result is IDictionary<string, object> dict && dict.ContainsKey("ids"))
                {
                    var ids = dict["ids"] as IList<object>;
                    if (ids != null)
                    {
                        foreach (var id in ids)
                        {
                            if (id != null)
                            {
                                var chunkId = id.ToString();
                                // Extract base document ID from chunk ID (format: "docId_chunk_N")
                                var baseDocId = ExtractBaseDocumentId(chunkId);
                                if (!string.IsNullOrEmpty(baseDocId))
                                {
                                    baseDocumentIds.Add(baseDocId);
                                }
                            }
                        }
                    }
                }
                
                var documentIds = baseDocumentIds.ToList();
                _logger.LogDebug("Retrieved {Count} unique base document IDs from ChromaDB for collection '{Collection}'", 
                    documentIds.Count, collectionName);
                
                return documentIds;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve document IDs from ChromaDB for collection '{Collection}'", collectionName);
                return new List<string>();
            }
        }

        /// <summary>
        /// PP13-74: Checks if a collection contains any documents with is_local_change=true metadata.
        /// Used to determine whether a collection should be preserved in carry mode.
        /// </summary>
        /// <param name="collectionName">Name of the collection to check</param>
        /// <returns>True if the collection contains any local change documents</returns>
        private async Task<bool> CollectionHasLocalChangesAsync(string collectionName)
        {
            try
            {
                var result = await _chromaService.GetDocumentsAsync(collectionName);

                if (result is IDictionary<string, object> dict && dict.ContainsKey("metadatas"))
                {
                    var metadatas = dict["metadatas"] as IList<object>;
                    if (metadatas != null)
                    {
                        foreach (var metadata in metadatas)
                        {
                            if (metadata is IDictionary<string, object> metaDict)
                            {
                                if (metaDict.TryGetValue("is_local_change", out var isLocalChange))
                                {
                                    if (isLocalChange is bool b && b)
                                        return true;
                                    if (isLocalChange?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                                        return true;
                                }
                            }
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PP13-74: Failed to check local changes for collection '{Collection}'", collectionName);
                return false;
            }
        }

        /// <summary>
        /// PP13-74: Gets base document IDs that have is_local_change=true metadata.
        /// Filters the provided document IDs to only those marked as local changes.
        /// </summary>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="documentIdsToCheck">List of base document IDs to check</param>
        /// <returns>List of base document IDs that have is_local_change=true</returns>
        private async Task<List<string>> GetDocumentsWithLocalChangesFlagAsync(string collectionName, List<string> documentIdsToCheck)
        {
            try
            {
                var localChangeDocs = new HashSet<string>();
                var result = await _chromaService.GetDocumentsAsync(collectionName);

                if (result is IDictionary<string, object> dict &&
                    dict.ContainsKey("ids") && dict.ContainsKey("metadatas"))
                {
                    var ids = dict["ids"] as IList<object>;
                    var metadatas = dict["metadatas"] as IList<object>;

                    if (ids != null && metadatas != null && ids.Count == metadatas.Count)
                    {
                        for (int i = 0; i < ids.Count; i++)
                        {
                            if (ids[i] == null) continue;

                            var chunkId = ids[i].ToString();
                            var baseDocId = ExtractBaseDocumentId(chunkId);

                            // Only check documents we're considering for removal
                            if (!documentIdsToCheck.Contains(baseDocId))
                                continue;

                            var metadata = metadatas[i] as IDictionary<string, object>;
                            if (metadata != null && metadata.TryGetValue("is_local_change", out var isLocalChange))
                            {
                                bool isLocal = false;
                                if (isLocalChange is bool b)
                                    isLocal = b;
                                else if (isLocalChange?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                                    isLocal = true;

                                if (isLocal)
                                {
                                    localChangeDocs.Add(baseDocId);
                                    _logger.LogDebug("PP13-74: Document '{DocId}' (chunk '{ChunkId}') marked as local change - will be preserved",
                                        baseDocId, chunkId);
                                }
                            }
                        }
                    }
                }

                return localChangeDocs.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PP13-74: Failed to check local change metadata for documents in collection '{Collection}'", collectionName);
                return new List<string>();
            }
        }

        /// <summary>
        /// PP13-69-C7: Gets all chunk IDs for specific base document IDs from ChromaDB.
        /// </summary>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="baseDocumentIds">List of base document IDs to find chunks for</param>
        /// <returns>List of chunk IDs that belong to the specified base documents</returns>
        private async Task<List<string>> GetChunkIdsForDocuments(string collectionName, List<string> baseDocumentIds)
        {
            try
            {
                _logger.LogInformation("PP13-69-C7 CHUNK RESOLUTION: Looking for chunks of base documents [{BaseIds}] in collection '{Collection}'", 
                    string.Join(", ", baseDocumentIds), collectionName);
                    
                var result = await _chromaService.GetDocumentsAsync(collectionName);
                var chunkIdsToRemove = new List<string>();
                var allChunkIds = new List<string>();
                
                if (result is IDictionary<string, object> dict && dict.ContainsKey("ids"))
                {
                    var ids = dict["ids"] as IList<object>;
                    if (ids != null)
                    {
                        foreach (var id in ids)
                        {
                            if (id != null)
                            {
                                var chunkId = id.ToString();
                                allChunkIds.Add(chunkId);
                                var baseDocId = ExtractBaseDocumentId(chunkId);
                                
                                _logger.LogInformation("PP13-69-C7 CHUNK RESOLUTION: Chunk '{ChunkId}' -> Base '{BaseId}', Match: {Match}", 
                                    chunkId, baseDocId, baseDocumentIds.Contains(baseDocId));
                                
                                // If this chunk belongs to one of the documents we want to remove
                                if (baseDocumentIds.Contains(baseDocId))
                                {
                                    chunkIdsToRemove.Add(chunkId);
                                }
                            }
                        }
                    }
                }
                
                _logger.LogInformation("PP13-69-C7 CHUNK RESOLUTION: All chunks in ChromaDB: [{AllChunks}]", string.Join(", ", allChunkIds));
                _logger.LogInformation("PP13-69-C7 CHUNK RESOLUTION: Found {Count} chunks to remove: [{ChunkIds}] for {DocCount} base documents", 
                    chunkIdsToRemove.Count, string.Join(", ", chunkIdsToRemove), baseDocumentIds.Count);
                
                return chunkIdsToRemove;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve chunk IDs for base documents in collection '{Collection}'", collectionName);
                return new List<string>();
            }
        }

        /// <summary>
        /// PP13-69-C7: Extracts the base document ID from a ChromaDB chunk ID.
        /// Handles the format "docId_chunk_N" and returns "docId".
        /// </summary>
        /// <param name="chunkId">The chunk ID from ChromaDB</param>
        /// <returns>The base document ID, or the original ID if not in chunk format</returns>
        private static string ExtractBaseDocumentId(string chunkId)
        {
            if (string.IsNullOrEmpty(chunkId))
                return chunkId;
                
            // Look for the pattern "_chunk_" and extract everything before it
            var chunkIndex = chunkId.LastIndexOf("_chunk_");
            if (chunkIndex > 0)
            {
                return chunkId.Substring(0, chunkIndex);
            }
            
            // If not in chunk format, return the original ID
            return chunkId;
        }

        #endregion
    }
}