using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DMMS.Models;
using Microsoft.Extensions.Logging;

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
        private readonly ILogger<SyncManagerV2> _logger;

        public SyncManagerV2(
            IDoltCli dolt,
            IChromaDbService chromaService,
            ILogger<SyncManagerV2> logger)
        {
            _dolt = dolt;
            _chromaService = chromaService;
            _logger = logger;
            
            // Initialize detectors and syncers
            _deltaDetector = new DeltaDetectorV2(dolt, logger: null);
            _chromaToDoltDetector = new ChromaToDoltDetector(chromaService, dolt, logger: null);
            
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
                var collections = await _chromaService.ListCollectionsAsync();
                var currentCollection = collections.FirstOrDefault() ?? "default";
                
                return await _chromaToDoltDetector.DetectLocalChangesAsync(currentCollection);
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
                    var collections = await _chromaService.ListCollectionsAsync();
                    var currentCollection = collections.FirstOrDefault() ?? "default";
                    
                    var stageResult = await _chromaToDoltSyncer.StageLocalChangesAsync(currentCollection);
                    
                    result.StagedFromChroma = stageResult.TotalStaged;
                    
                    if (stageResult.Status == StageStatus.Failed)
                    {
                        result.Status = SyncStatusV2.Failed;
                        result.ErrorMessage = stageResult.ErrorMessage;
                        return result;
                    }
                    
                    _logger.LogInformation("Staged {Count} changes from ChromaDB", stageResult.TotalStaged);
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
                if (!force)
                {
                    var localChanges = await GetLocalChangesAsync();
                    if (localChanges.HasChanges)
                    {
                        result.Status = SyncStatusV2.LocalChangesExist;
                        result.LocalChanges = localChanges;
                        _logger.LogWarning("Checkout blocked: {Count} local changes exist", localChanges.TotalChanges);
                        return result;
                    }
                }

                // Execute checkout
                var checkoutResult = await _dolt.CheckoutAsync(targetBranch, createNew);
                
                if (!checkoutResult.Success)
                {
                    result.Status = SyncStatusV2.Failed;
                    result.ErrorMessage = checkoutResult.Error;
                    return result;
                }

                // Full sync to update ChromaDB with new branch content
                var collections = await _chromaService.ListCollectionsAsync();
                var currentCollection = collections.FirstOrDefault() ?? "default";
                
                var syncResult = await FullSyncAsync(currentCollection);
                
                result.Added = syncResult.Added;
                result.Modified = syncResult.Modified;
                result.Deleted = syncResult.Deleted;
                result.ChunksProcessed = syncResult.ChunksProcessed;
                result.Status = syncResult.Status;

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

        public async Task<MergeSyncResultV2> ProcessMergeAsync(string sourceBranch, bool force = false)
        {
            var result = new MergeSyncResultV2 { Direction = SyncDirection.DoltToChroma };
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

                // Sync merged changes to ChromaDB
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
                    result.ErrorMessage = "Push failed";
                    return result;
                }

                result.Status = SyncStatusV2.Completed;
                result.CommitHash = await _dolt.GetHeadCommitHashAsync();
                
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

                // Full sync to update ChromaDB after reset
                var collections = await _chromaService.ListCollectionsAsync();
                var currentCollection = collections.FirstOrDefault() ?? "default";
                
                var syncResult = await FullSyncAsync(currentCollection);
                
                result.Added = syncResult.Added;
                result.Modified = syncResult.Modified;
                result.Deleted = syncResult.Deleted;
                result.ChunksProcessed = syncResult.ChunksProcessed;
                result.Status = syncResult.Status;
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

        public async Task<SyncResultV2> FullSyncAsync(string? collectionName = null)
        {
            var result = new SyncResultV2 { Direction = SyncDirection.DoltToChroma };
            
            try
            {
                collectionName ??= (await _chromaService.ListCollectionsAsync()).FirstOrDefault() ?? "default";
                
                _logger.LogInformation("Performing full sync for collection {Collection}", collectionName);
                
                // Get all documents from Dolt
                var documents = await _deltaDetector.GetAllDocumentsAsync(collectionName);
                
                // Clear existing collection in ChromaDB
                await _chromaService.DeleteCollectionAsync(collectionName);
                await _chromaService.CreateCollectionAsync(collectionName);
                
                // Sync all documents
                foreach (var doc in documents)
                {
                    var chromaEntries = DocumentConverterUtilityV2.ConvertDoltToChroma(
                        doc, await _dolt.GetHeadCommitHashAsync());
                    
                    await _chromaService.AddDocumentsAsync(
                        collectionName,
                        chromaEntries.Ids,
                        chromaEntries.Documents,
                        chromaEntries.Metadatas);
                    
                    result.Added++;
                    result.ChunksProcessed += chromaEntries.Count;
                }
                
                // Update sync state
                var commitHash = await _dolt.GetHeadCommitHashAsync();
                await _deltaDetector.UpdateSyncStateAsync(collectionName, commitHash, result.Added, result.ChunksProcessed);
                
                result.Status = SyncStatusV2.Completed;
                _logger.LogInformation("Full sync completed: {Added} documents, {Chunks} chunks", 
                    result.Added, result.ChunksProcessed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform full sync");
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
                            chromaEntries.Ids,
                            chromaEntries.Documents,
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
                            chromaEntries.Ids,
                            chromaEntries.Documents,
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
                            chromaEntries.Ids,
                            chromaEntries.Documents,
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
    }
}