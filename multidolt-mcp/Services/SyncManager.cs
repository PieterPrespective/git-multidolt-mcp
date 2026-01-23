using Embranch.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Embranch.Services
{
    /// <summary>
    /// Coordinates synchronization between Dolt version control and ChromaDB vector database.
    /// Implements the sync manager from Section 5.2 of the Dolt Interface Implementation Plan.
    /// </summary>
    public class SyncManager : ISyncManager
    {
        private readonly IDoltCli _dolt;
        private readonly IChromaDbService _chromaManager;
        private readonly DeltaDetector _deltaDetector;
        private readonly ILogger<SyncManager> _logger;

        public SyncManager(
            IDoltCli dolt,
            IChromaDbService chromaManager,
            ILogger<SyncManager> logger)
        {
            _dolt = dolt;
            _chromaManager = chromaManager;
            _deltaDetector = new DeltaDetector(dolt);
            _logger = logger;
        }

        // ==================== Commit Processing ====================

        public async Task<SyncResult> ProcessCommitAsync(string message, bool syncAfter = true)
        {
            var result = new SyncResult();
            var branch = await _dolt.GetCurrentBranchAsync();
            var beforeCommit = await _dolt.GetHeadCommitHashAsync();

            _logger.LogInformation("Processing commit on branch {Branch} with message: {Message}", branch, message);

            // Log operation start
            var operationId = await LogOperationStartAsync("commit", branch, beforeCommit);

            try
            {
                // Stage and commit in Dolt
                await _dolt.AddAllAsync();
                var commitResult = await _dolt.CommitAsync(message);
                
                if (!commitResult.Success)
                {
                    await LogOperationFailedAsync(operationId, commitResult.Message);
                    result.Status = SyncStatus.Failed;
                    result.ErrorMessage = commitResult.Message;
                    return result;
                }

                var afterCommit = commitResult.CommitHash;
                result.CommitHash = afterCommit;
                
                if (syncAfter)
                {
                    _logger.LogInformation("Syncing changes between commits {Before} and {After}", beforeCommit, afterCommit);
                    
                    // Find changes using DOLT_DIFF
                    var issueChanges = await _deltaDetector.GetCommitDiffAsync(beforeCommit, afterCommit, "issue_logs");
                    var knowledgeChanges = await _deltaDetector.GetCommitDiffAsync(beforeCommit, afterCommit, "knowledge_docs");
                    var allChanges = issueChanges.Concat(knowledgeChanges).ToList();

                    // Process each change
                    foreach (var change in allChanges)
                    {
                        await ProcessDiffRowAsync(change, branch, result);
                    }

                    // Update sync state
                    await UpdateSyncStateAsync(branch, afterCommit, result);
                }

                await LogOperationCompletedAsync(operationId, afterCommit, result);
                result.Status = SyncStatus.Completed;
            }
            catch (Exception ex)
            {
                await LogOperationFailedAsync(operationId, ex.Message);
                _logger.LogError(ex, "Failed to process commit");
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ==================== Pull Processing ====================

        public async Task<SyncResult> ProcessPullAsync(string remote = "origin")
        {
            var result = new SyncResult();
            var branch = await _dolt.GetCurrentBranchAsync();
            var beforeCommit = await _dolt.GetHeadCommitHashAsync();

            _logger.LogInformation("Processing pull from {Remote} to branch {Branch}", remote, branch);

            var operationId = await LogOperationStartAsync("pull", branch, beforeCommit);

            try
            {
                // Pull from remote
                var pullResult = await _dolt.PullAsync(remote, branch);
                
                if (!pullResult.Success)
                {
                    if (pullResult.HasConflicts)
                    {
                        result.Status = SyncStatus.Conflicts;
                        result.ErrorMessage = "Pull resulted in conflicts. Resolve before syncing.";
                    }
                    else
                    {
                        result.Status = SyncStatus.Failed;
                        result.ErrorMessage = pullResult.Message;
                    }
                    await LogOperationFailedAsync(operationId, result.ErrorMessage);
                    return result;
                }

                var afterCommit = await _dolt.GetHeadCommitHashAsync();
                result.CommitHash = afterCommit;
                result.WasFastForward = pullResult.WasFastForward;

                // If no changes (same commit), return early
                if (beforeCommit == afterCommit)
                {
                    result.Status = SyncStatus.NoChanges;
                    await LogOperationCompletedAsync(operationId, afterCommit, result);
                    return result;
                }

                // Sync all changes between commits
                await SyncCommitRangeAsync(beforeCommit, afterCommit, branch, result);

                await LogOperationCompletedAsync(operationId, afterCommit, result);
                result.Status = SyncStatus.Completed;
            }
            catch (Exception ex)
            {
                await LogOperationFailedAsync(operationId, ex.Message);
                _logger.LogError(ex, "Failed to process pull");
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ==================== Checkout Processing ====================

        public async Task<SyncResult> ProcessCheckoutAsync(string targetBranch, bool createNew = false)
        {
            var result = new SyncResult();
            var previousBranch = await _dolt.GetCurrentBranchAsync();

            _logger.LogInformation("Processing checkout to branch {Branch} (create: {Create})", targetBranch, createNew);

            var operationId = await LogOperationStartAsync("checkout", targetBranch, null);

            try
            {
                // Checkout in Dolt
                var checkoutResult = await _dolt.CheckoutAsync(targetBranch, createNew);
                
                if (!checkoutResult.Success)
                {
                    result.Status = SyncStatus.Failed;
                    result.ErrorMessage = checkoutResult.Error;
                    await LogOperationFailedAsync(operationId, result.ErrorMessage);
                    return result;
                }

                // Get collection name for target branch
                var collectionName = GetCollectionName(targetBranch);
                var collections = await _chromaManager.ListCollectionsAsync();
                var collectionExists = collections.Contains(collectionName);

                if (createNew)
                {
                    // New branch: clone collection from parent branch if it exists
                    var parentCollection = GetCollectionName(previousBranch);
                    if (collections.Contains(parentCollection))
                    {
                        await CloneCollectionAsync(parentCollection, collectionName);
                        _logger.LogInformation("Created collection {Collection} from {Parent}", 
                            collectionName, parentCollection);
                    }
                    else
                    {
                        // Create empty collection and do full sync
                        await _chromaManager.CreateCollectionAsync(collectionName, new Dictionary<string, object>
                        {
                            ["dolt_branch"] = targetBranch,
                            ["source"] = "dolt"
                        });
                        await FullSyncToChromaAsync(targetBranch, collectionName, result);
                    }
                }
                else if (!collectionExists)
                {
                    // Existing branch but no collection: full sync
                    await _chromaManager.CreateCollectionAsync(collectionName, new Dictionary<string, object>
                    {
                        ["dolt_branch"] = targetBranch,
                        ["source"] = "dolt"
                    });
                    await FullSyncToChromaAsync(targetBranch, collectionName, result);
                }
                else
                {
                    // Existing collection: check if incremental sync needed
                    var lastSyncCommit = await _deltaDetector.GetLastSyncCommitAsync(collectionName);
                    var currentCommit = await _dolt.GetHeadCommitHashAsync();

                    if (lastSyncCommit != currentCommit)
                    {
                        await SyncCommitRangeAsync(lastSyncCommit ?? "HEAD~1000", currentCommit, targetBranch, result);
                    }
                }

                var afterCommit = await _dolt.GetHeadCommitHashAsync();
                result.CommitHash = afterCommit;
                await LogOperationCompletedAsync(operationId, afterCommit, result);
                result.Status = SyncStatus.Completed;
            }
            catch (Exception ex)
            {
                await LogOperationFailedAsync(operationId, ex.Message);
                _logger.LogError(ex, "Failed to process checkout");
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ==================== Merge Processing ====================

        public async Task<MergeSyncResult> ProcessMergeAsync(string sourceBranch)
        {
            var result = new MergeSyncResult();
            var targetBranch = await _dolt.GetCurrentBranchAsync();
            var beforeCommit = await _dolt.GetHeadCommitHashAsync();

            _logger.LogInformation("Processing merge from {Source} to {Target}", sourceBranch, targetBranch);

            var operationId = await LogOperationStartAsync("merge", targetBranch, beforeCommit);

            try
            {
                // Attempt merge
                var mergeResult = await _dolt.MergeAsync(sourceBranch);

                if (mergeResult.HasConflicts)
                {
                    result.HasConflicts = true;
                    result.Conflicts = (await _dolt.GetConflictsAsync("issue_logs"))
                        .Concat(await _dolt.GetConflictsAsync("knowledge_docs"))
                        .ToList();
                    result.Status = SyncStatus.Conflicts;
                    await LogOperationFailedAsync(operationId, "Merge conflicts detected");
                    return result;
                }

                if (!mergeResult.Success)
                {
                    result.Status = SyncStatus.Failed;
                    result.ErrorMessage = mergeResult.Message;
                    await LogOperationFailedAsync(operationId, result.ErrorMessage);
                    return result;
                }

                var afterCommit = mergeResult.MergeCommitHash ?? await _dolt.GetHeadCommitHashAsync();
                result.CommitHash = afterCommit;

                // Sync merged changes
                await SyncCommitRangeAsync(beforeCommit, afterCommit, targetBranch, result);

                await LogOperationCompletedAsync(operationId, afterCommit, result);
                result.Status = SyncStatus.Completed;
            }
            catch (Exception ex)
            {
                await LogOperationFailedAsync(operationId, ex.Message);
                _logger.LogError(ex, "Failed to process merge");
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ==================== Reset Processing ====================

        public async Task<SyncResult> ProcessResetAsync(string targetCommit)
        {
            var result = new SyncResult();
            var branch = await _dolt.GetCurrentBranchAsync();
            var beforeCommit = await _dolt.GetHeadCommitHashAsync();

            _logger.LogInformation("Processing reset to {Commit} on branch {Branch}", targetCommit, branch);

            var operationId = await LogOperationStartAsync("reset", branch, beforeCommit);

            try
            {
                // Reset Dolt
                var resetResult = await _dolt.ResetHardAsync(targetCommit);
                
                if (!resetResult.Success)
                {
                    result.Status = SyncStatus.Failed;
                    result.ErrorMessage = resetResult.Error;
                    await LogOperationFailedAsync(operationId, result.ErrorMessage);
                    return result;
                }

                // Full regeneration (reset can go forward or backward)
                var collectionName = GetCollectionName(branch);
                
                // Delete existing collection
                await _chromaManager.DeleteCollectionAsync(collectionName);
                
                // Full sync from reset state
                await FullSyncToChromaAsync(branch, collectionName, result);

                result.CommitHash = targetCommit;
                await LogOperationCompletedAsync(operationId, targetCommit, result);
                result.Status = SyncStatus.Completed;
            }
            catch (Exception ex)
            {
                await LogOperationFailedAsync(operationId, ex.Message);
                _logger.LogError(ex, "Failed to process reset");
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ==================== Change Detection ====================

        public async Task<bool> HasPendingChangesAsync()
        {
            var branch = await _dolt.GetCurrentBranchAsync();
            var collectionName = GetCollectionName(branch);
            var lastSyncCommit = await _deltaDetector.GetLastSyncCommitAsync(collectionName);
            var currentCommit = await _dolt.GetHeadCommitHashAsync();

            return lastSyncCommit != currentCommit;
        }

        public async Task<PendingChanges> GetPendingChangesAsync()
        {
            var branch = await _dolt.GetCurrentBranchAsync();
            var collectionName = GetCollectionName(branch);

            var pendingDocs = await _deltaDetector.GetPendingSyncDocumentsAsync(collectionName);
            var deletedDocs = await _deltaDetector.GetDeletedDocumentsAsync(collectionName);

            return new PendingChanges
            {
                NewDocuments = pendingDocs.Where(d => d.ChangeType == "new").ToList(),
                ModifiedDocuments = pendingDocs.Where(d => d.ChangeType == "modified").ToList(),
                DeletedDocuments = deletedDocs.ToList()
            };
        }

        // ==================== Manual Sync Operations ====================

        public async Task<SyncResult> FullSyncAsync(string? collectionName = null)
        {
            var result = new SyncResult();
            var branch = await _dolt.GetCurrentBranchAsync();
            collectionName ??= GetCollectionName(branch);

            _logger.LogInformation("Starting full sync for branch {Branch} to collection {Collection}", branch, collectionName);

            try
            {
                await FullSyncToChromaAsync(branch, collectionName, result);
                result.Status = SyncStatus.Completed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform full sync");
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public async Task<SyncResult> IncrementalSyncAsync(string? collectionName = null)
        {
            var result = new SyncResult();
            var branch = await _dolt.GetCurrentBranchAsync();
            collectionName ??= GetCollectionName(branch);

            _logger.LogInformation("Starting incremental sync for branch {Branch} to collection {Collection}", branch, collectionName);

            try
            {
                var pendingChanges = await GetPendingChangesAsync();
                
                if (!pendingChanges.HasChanges)
                {
                    result.Status = SyncStatus.NoChanges;
                    return result;
                }

                // Process new and modified documents
                foreach (var doc in pendingChanges.NewDocuments.Concat(pendingChanges.ModifiedDocuments))
                {
                    await ProcessDocumentDelta(doc, collectionName, result);
                }

                // Process deleted documents
                foreach (var deleted in pendingChanges.DeletedDocuments)
                {
                    await RemoveDocumentFromChromaAsync(deleted.SourceId, collectionName);
                    result.Deleted++;
                }

                var currentCommit = await _dolt.GetHeadCommitHashAsync();
                await UpdateSyncStateAsync(branch, currentCommit, result);
                
                result.Status = SyncStatus.Completed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform incremental sync");
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ==================== Helper Methods ====================

        private async Task SyncCommitRangeAsync(
            string fromCommit, 
            string toCommit, 
            string branch,
            SyncResult result)
        {
            _logger.LogDebug("Syncing commit range {From} to {To} for branch {Branch}", fromCommit, toCommit, branch);
            
            var issueChanges = await _deltaDetector.GetCommitDiffAsync(fromCommit, toCommit, "issue_logs");
            var knowledgeChanges = await _deltaDetector.GetCommitDiffAsync(fromCommit, toCommit, "knowledge_docs");

            foreach (var change in issueChanges.Concat(knowledgeChanges))
            {
                await ProcessDiffRowAsync(change, branch, result);
            }

            await UpdateSyncStateAsync(branch, toCommit, result);
        }

        private async Task ProcessDiffRowAsync(DiffRow diff, string branch, SyncResult result)
        {
            var collectionName = GetCollectionName(branch);

            switch (diff.DiffType?.ToLowerInvariant())
            {
                case "added":
                    await AddDocumentToChromaAsync(diff, collectionName);
                    result.Added++;
                    break;

                case "modified":
                    await UpdateDocumentInChromaAsync(diff, collectionName);
                    result.Modified++;
                    break;

                case "removed":
                    await RemoveDocumentFromChromaAsync(diff.SourceId, collectionName);
                    result.Deleted++;
                    break;
            }
        }

        private async Task ProcessDocumentDelta(DocumentDelta delta, string collectionName, SyncResult result)
        {
            if (delta.IsNew)
            {
                await AddDocumentDeltaToChromaAsync(delta, collectionName);
                result.Added++;
            }
            else if (delta.IsModified)
            {
                await UpdateDocumentDeltaInChromaAsync(delta, collectionName);
                result.Modified++;
            }
        }

        private async Task AddDocumentToChromaAsync(DiffRow diff, string collectionName)
        {
            // Chunk the content
            var chunks = ChunkContent(diff.ToContent);
            var chunkIds = chunks.Select((_, i) => $"{diff.SourceId}_chunk_{i}").ToList();

            _logger!.LogDebug("Chunked document {SourceId} into {ChunkCount} chunks", diff.SourceId, chunks.Count);

                // Build metadata for each chunk
                var metadatas = chunks.Select((_, i) => new Dictionary<string, object>
            {
                ["source_id"] = diff.SourceId,
                ["content_hash"] = diff.ToContentHash,
                ["chunk_index"] = i,
                ["total_chunks"] = chunks.Count
            }).ToList();

            _logger.LogDebug("Built metadata for {ChunkCount} chunks of document {SourceId}", 
                chunks.Count, diff.SourceId);

            // Add to ChromaDB - sync operation, should not mark as local change (PP13-68-C2 fix)
            await _chromaManager.AddDocumentsAsync(collectionName, chunks, chunkIds, metadatas, allowDuplicateIds: false, markAsLocalChange: false);

            // Update sync log
            await UpdateDocumentSyncLogAsync(diff, collectionName, chunkIds, "added");

            _logger.LogDebug("Added document {SourceId} as {ChunkCount} chunks to {Collection}", 
                diff.SourceId, chunks.Count, collectionName);
        }

        private async Task AddDocumentDeltaToChromaAsync(DocumentDelta delta, string collectionName)
        {
            // Chunk the content
            var chunks = ChunkContent(delta.Content);
            var chunkIds = chunks.Select((_, i) => $"{delta.SourceId}_chunk_{i}").ToList();

            // Build metadata for each chunk
            var metadatas = chunks.Select((_, i) => new Dictionary<string, object>
            {
                ["source_id"] = delta.SourceId,
                ["content_hash"] = delta.ContentHash,
                ["chunk_index"] = i,
                ["total_chunks"] = chunks.Count,
                ["source_table"] = delta.SourceTable
            }).ToList();

            // Add to ChromaDB - sync operation, should not mark as local change (PP13-68-C2 fix)
            await _chromaManager.AddDocumentsAsync(collectionName, chunks, chunkIds, metadatas, allowDuplicateIds: false, markAsLocalChange: false);

            // Update sync log  
            await UpdateDocumentDeltaSyncLogAsync(delta, collectionName, chunkIds, "added");
            
            _logger.LogDebug("Added document delta {SourceId} as {ChunkCount} chunks to {Collection}", 
                delta.SourceId, chunks.Count, collectionName);
        }

        private async Task UpdateDocumentInChromaAsync(DiffRow diff, string collectionName)
        {
            // Remove old chunks first
            await RemoveDocumentFromChromaAsync(diff.SourceId, collectionName);
            
            // Add new chunks
            await AddDocumentToChromaAsync(diff, collectionName);
        }

        private async Task UpdateDocumentDeltaInChromaAsync(DocumentDelta delta, string collectionName)
        {
            // Remove old chunks first
            await RemoveDocumentFromChromaAsync(delta.SourceId, collectionName);
            
            // Add new chunks
            await AddDocumentDeltaToChromaAsync(delta, collectionName);
        }

        private async Task RemoveDocumentFromChromaAsync(string sourceId, string collectionName)
        {
            try
            {
                // Get existing chunk IDs from sync log
                var sql = $@"SELECT chunk_ids FROM document_sync_log 
                             WHERE source_id = '{sourceId}' AND chroma_collection = '{collectionName}'";
                var chunkIdsJson = await _dolt.ExecuteScalarAsync<string?>(sql);
                
                if (!string.IsNullOrEmpty(chunkIdsJson))
                {
                    var chunkIds = JsonSerializer.Deserialize<List<string>>(chunkIdsJson) ?? new List<string>();
                    if (chunkIds.Count > 0)
                    {
                        await _chromaManager.DeleteDocumentsAsync(collectionName, chunkIds);
                        _logger.LogDebug("Removed {ChunkCount} chunks for document {SourceId} from {Collection}", 
                            chunkIds.Count, sourceId, collectionName);
                    }
                }

                // Remove from sync log
                await _dolt.ExecuteAsync(
                    $"DELETE FROM document_sync_log WHERE source_id = '{sourceId}' AND chroma_collection = '{collectionName}'");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove document {SourceId} from collection {Collection}", sourceId, collectionName);
            }
        }

        private async Task FullSyncToChromaAsync(string branch, string collectionName, SyncResult result)
        {
            _logger.LogInformation("Starting full sync for branch {Branch}", branch);

            // Ensure collection exists
            var collections = await _chromaManager.ListCollectionsAsync();
            if (!collections.Contains(collectionName))
            {
                await _chromaManager.CreateCollectionAsync(collectionName, new Dictionary<string, object>
                {
                    ["dolt_branch"] = branch,
                    ["source"] = "dolt"
                });
            }
            
            // Clear existing sync state for this collection
            await _dolt.ExecuteAsync($"DELETE FROM document_sync_log WHERE chroma_collection = '{collectionName}'");

            
            // Get all documents from both tables
            var issueLogsQuery = @"
                SELECT 
                    'issue_logs' as source_table,
                    log_id as source_id, 
                    content, 
                    content_hash, 
                    project_id as identifier 
                FROM issue_logs";
            
            var knowledgeDocsQuery = @"
                SELECT 
                    'knowledge_docs' as source_table,
                    doc_id as source_id, 
                    content, 
                    content_hash, 
                    tool_name as identifier 
                FROM knowledge_docs";

            var issueLogDocs = await _dolt.QueryAsync<DocumentRecord>(issueLogsQuery);
            var knowledgeDocDocs = await _dolt.QueryAsync<DocumentRecord>(knowledgeDocsQuery);

            var combinedLogs = issueLogDocs.Concat(knowledgeDocDocs);
            _logger.LogInformation("# of dolt issue logs {noOfLogs}", combinedLogs.Count());

            foreach (var doc in combinedLogs)
            {
                var diff = new DiffRow("added", doc.SourceId, "", doc.ContentHash, doc.Content, new());

                var chunks = ChunkContent(diff.ToContent);
                //_logger.LogDebug("Dif to content {content} diff to content hash {contentHash}", diff.ToContent, diff.ToContentHash);
                //await AddDocumentToChromaAsync(diff, collectionName, true);
                result.Added++;
            }
            /*
            var currentCommit = await _dolt.GetHeadCommitHashAsync();
            await UpdateSyncStateAsync(branch, currentCommit, result);
            */
        }

        private async Task CloneCollectionAsync(string sourceCollection, string targetCollection)
        {
            // Get all documents from source collection
            var sourceData = await _chromaManager.GetDocumentsAsync(sourceCollection);
            
            // Create target collection
            await _chromaManager.CreateCollectionAsync(targetCollection);
            
            // Copy documents if source has data
            // Note: This is a simplified implementation - real cloning would preserve embeddings
            _logger.LogInformation("Created empty collection {Target} (full cloning with embeddings not yet implemented)", targetCollection);
        }

        private string GetCollectionName(string branch)
        {
            var safeBranch = branch.Replace("/", "-").Replace("_", "-").Replace(" ", "-");
            if (safeBranch.Length > 20) safeBranch = safeBranch.Substring(0, 20);
            return $"vmrag_{safeBranch}";
        }

        private List<string> ChunkContent(string content, int chunkSize = 512, int overlap = 50)
        {
            var chunks = new List<string>();
            var start = 0;

            int maxPasses = Math.Max((content.Length / (chunkSize - overlap)) + 5, 10); // Prevent infinite loops

            while (start < content.Length && maxPasses > 0)
            {
                var end = Math.Min(start + chunkSize, content.Length);
                chunks.Add(content.Substring(start, end - start));
                maxPasses--;
                //CASE: move start backwards with overlap (so we don't miss content); only do this while we haven't reached the end
                start = (end < content.Length) ? end - overlap : end;

                //CASE : we have reached the end, so break
                if (start >= end)
                {
                    break;
                }
                
            }

            if(maxPasses <= 0)
            {
                throw new Exception($"Chunking content exceeded maximum passes, possible infinite loop detected. Content Length {content.Length}, Start: {start}, Chunksize: {chunkSize}, Overlap: {overlap}");
            }

            return chunks;
        }

        // Sync state and logging methods...
        private async Task UpdateSyncStateAsync(string branch, string commit, SyncResult result)
        {
            var collectionName = GetCollectionName(branch);
            var sql = $@"
                INSERT INTO chroma_sync_state 
                    (collection_name, last_sync_commit, last_sync_at, document_count, sync_status)
                VALUES 
                    ('{collectionName}', '{commit}', NOW(), {result.Added}, 'synced')
                ON DUPLICATE KEY UPDATE
                    last_sync_commit = '{commit}',
                    last_sync_at = NOW(),
                    document_count = document_count + {result.Added} - {result.Deleted},
                    sync_status = 'synced'";
            
            await _dolt.ExecuteAsync(sql);
        }

        private async Task UpdateDocumentSyncLogAsync(
            DiffRow diff, 
            string collectionName, 
            List<string> chunkIds,
            string action)
        {
            var chunkIdsJson = JsonSerializer.Serialize(chunkIds);
            var sourceTable = InferSourceTable(diff.SourceId);
            
            var sql = $@"
                INSERT INTO document_sync_log 
                    (source_table, source_id, content_hash, chroma_collection, chunk_ids, synced_at, sync_action)
                VALUES 
                    ('{sourceTable}', '{diff.SourceId}', '{diff.ToContentHash}', '{collectionName}', 
                     '{chunkIdsJson}', NOW(), '{action}')
                ON DUPLICATE KEY UPDATE
                    content_hash = '{diff.ToContentHash}',
                    chunk_ids = '{chunkIdsJson}',
                    synced_at = NOW(),
                    sync_action = '{action}'";
            
            await _dolt.ExecuteAsync(sql);
        }

        private async Task UpdateDocumentDeltaSyncLogAsync(
            DocumentDelta delta, 
            string collectionName, 
            List<string> chunkIds,
            string action)
        {
            var chunkIdsJson = JsonSerializer.Serialize(chunkIds);
            
            var sql = $@"
                INSERT INTO document_sync_log 
                    (source_table, source_id, content_hash, chroma_collection, chunk_ids, synced_at, sync_action)
                VALUES 
                    ('{delta.SourceTable}', '{delta.SourceId}', '{delta.ContentHash}', '{collectionName}', 
                     '{chunkIdsJson}', NOW(), '{action}')
                ON DUPLICATE KEY UPDATE
                    content_hash = '{delta.ContentHash}',
                    chunk_ids = '{chunkIdsJson}',
                    synced_at = NOW(),
                    sync_action = '{action}'";
            
            await _dolt.ExecuteAsync(sql);
        }

        private string InferSourceTable(string sourceId)
        {
            // Simple heuristic based on ID patterns
            return sourceId.StartsWith("log") || sourceId.Contains("log") ? "issue_logs" : "knowledge_docs";
        }

        private async Task<int> LogOperationStartAsync(string operationType, string branch, string? beforeCommit)
        {
            var sql = $@"
                INSERT INTO sync_operations 
                    (operation_type, dolt_branch, dolt_commit_before, operation_status, started_at)
                VALUES 
                    ('{operationType}', '{branch}', '{beforeCommit ?? ""}', 'started', NOW())";
            
            await _dolt.ExecuteAsync(sql);
            
            // Get the auto-increment ID
            var idResult = await _dolt.ExecuteScalarAsync<int>("SELECT LAST_INSERT_ID()");
            return idResult;
        }

        private async Task LogOperationCompletedAsync(int operationId, string afterCommit, SyncResult result)
        {
            var sql = $@"
                UPDATE sync_operations 
                SET 
                    dolt_commit_after = '{afterCommit}',
                    documents_added = {result.Added},
                    documents_modified = {result.Modified},
                    documents_deleted = {result.Deleted},
                    chunks_processed = {result.ChunksProcessed},
                    operation_status = 'completed',
                    completed_at = NOW()
                WHERE operation_id = {operationId}";
            
            await _dolt.ExecuteAsync(sql);
        }

        private async Task LogOperationFailedAsync(int operationId, string errorMessage)
        {
            var escapedError = errorMessage.Replace("'", "''");
            var sql = $@"
                UPDATE sync_operations 
                SET 
                    operation_status = 'failed',
                    error_message = '{escapedError}',
                    completed_at = NOW()
                WHERE operation_id = {operationId}";
            
            await _dolt.ExecuteAsync(sql);
        }

        // ==================== ChromaDB to Dolt Import ====================

        public async Task<SyncResult> ImportFromChromaAsync(string sourceCollection, string? commitMessage = null)
    {
        var result = new SyncResult();
        
        _logger.LogInformation("Starting import from ChromaDB collection {Collection} to Dolt", sourceCollection);

        try
        {
            // Check if the collection exists
            var collections = await _chromaManager.ListCollectionsAsync();
            if (!collections.Contains(sourceCollection))
            {
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = $"Collection '{sourceCollection}' does not exist in ChromaDB";
                return result;
            }

            // Get all documents from the collection
            var allDocsObj = await _chromaManager.GetDocumentsAsync(sourceCollection);
            var allDocs = allDocsObj as Dictionary<string, object>;
            
            if (allDocs == null || !allDocs.ContainsKey("ids") || !allDocs.ContainsKey("documents"))
            {
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = "Failed to retrieve documents from ChromaDB";
                return result;
            }

            var ids = allDocs["ids"] as List<object>;
            var documents = allDocs["documents"] as List<object>;
            var metadatas = allDocs.ContainsKey("metadatas") ? allDocs["metadatas"] as List<object> : null;

            if (ids == null || documents == null)
            {
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = "Invalid document structure from ChromaDB";
                return result;
            }

            // Process documents and group by source_id (reconstruct from chunks)
            var reconstructedDocs = new Dictionary<string, ReconstructedDocument>();
            
            for (int i = 0; i < ids.Count; i++)
            {
                var chunkId = ids[i]?.ToString();
                var content = documents[i]?.ToString();
                var metadata = metadatas?[i] as Dictionary<string, object>;
                
                if (string.IsNullOrEmpty(chunkId) || string.IsNullOrEmpty(content))
                    continue;

                // Extract source_id from chunk_id (format: sourceId_chunk_N)
                var sourceId = ExtractSourceIdFromChunkId(chunkId);
                if (string.IsNullOrEmpty(sourceId))
                    continue;

                if (!reconstructedDocs.ContainsKey(sourceId))
                {
                    reconstructedDocs[sourceId] = new ReconstructedDocument
                    {
                        SourceId = sourceId,
                        Chunks = new SortedDictionary<int, string>(),
                        Metadata = metadata ?? new Dictionary<string, object>()
                    };
                }

                // Extract chunk index
                var chunkIndex = ExtractChunkIndex(chunkId);
                reconstructedDocs[sourceId].Chunks[chunkIndex] = content;
            }

            // Ensure projects table has at least one entry for foreign key constraints
            try
            {
                await _dolt.ExecuteAsync(@"
                    INSERT INTO projects (project_id, name, repository_url, metadata) 
                    VALUES ('imported', 'Imported Data', 'https://chromadb.import', JSON_OBJECT('source', 'chromadb'))");
            }
            catch (Exception ex)
            {
                // Ignore if project already exists (duplicate key)
                _logger.LogDebug("Project 'imported' may already exist: {Error}", ex.Message);
            }

            // Insert reconstructed documents into Dolt
            foreach (var doc in reconstructedDocs.Values)
            {
                var fullContent = string.Join("", doc.Chunks.Values);
                var contentHash = DocumentConverterUtility.CalculateContentHash(fullContent);
                
                // Determine table based on source_id pattern or metadata
                var isIssueLog = doc.SourceId.StartsWith("issue_") || 
                                 doc.SourceId.StartsWith("dspline-test") ||
                                 doc.Metadata.ContainsKey("project_id");
                
                if (isIssueLog)
                {
                    // Insert into issue_logs table
                    var projectId = doc.Metadata.GetValueOrDefault("project_id")?.ToString() ?? "imported";
                    var issueNumber = doc.Metadata.GetValueOrDefault("issue_number") as int? ?? 0;
                    var title = doc.Metadata.GetValueOrDefault("title")?.ToString() ?? "Imported from ChromaDB";
                    var logType = doc.Metadata.GetValueOrDefault("log_type")?.ToString() ?? "imported";
                    
                    await _dolt.ExecuteAsync($@"
                        INSERT INTO issue_logs (log_id, project_id, issue_number, title, content, content_hash, log_type, metadata)
                        VALUES ('{doc.SourceId}', '{projectId}', {issueNumber}, '{EscapeSql(title)}', '{EscapeSql(fullContent)}', 
                                '{contentHash}', '{logType}', JSON_OBJECT('imported_from', '{sourceCollection}'))
                        ON DUPLICATE KEY UPDATE
                            content = VALUES(content),
                            content_hash = VALUES(content_hash),
                            metadata = JSON_MERGE_PRESERVE(metadata, VALUES(metadata))");
                    
                    result.Added++;
                }
                else
                {
                    // Insert into knowledge_docs table
                    var category = doc.Metadata.GetValueOrDefault("category")?.ToString() ?? "imported";
                    var toolName = doc.Metadata.GetValueOrDefault("tool_name")?.ToString() ?? 
                                  (doc.SourceId.Contains("DSpline") ? "DSpline" : "Unknown");
                    var toolVersion = doc.Metadata.GetValueOrDefault("tool_version")?.ToString() ?? "1.0.0";
                    var title = doc.Metadata.GetValueOrDefault("title")?.ToString() ?? "Imported from ChromaDB";
                    
                    await _dolt.ExecuteAsync($@"
                        INSERT INTO knowledge_docs (doc_id, category, tool_name, tool_version, title, content, content_hash, metadata)
                        VALUES ('{doc.SourceId}', '{category}', '{EscapeSql(toolName)}', '{toolVersion}', 
                                '{EscapeSql(title)}', '{EscapeSql(fullContent)}', '{contentHash}', 
                                JSON_OBJECT('imported_from', '{sourceCollection}'))
                        ON DUPLICATE KEY UPDATE
                            content = VALUES(content),
                            content_hash = VALUES(content_hash),
                            metadata = JSON_MERGE_PRESERVE(metadata, VALUES(metadata))");
                    
                    result.Added++;
                }
            }

            // Commit the imported data if requested
            if (!string.IsNullOrEmpty(commitMessage))
            {
                await _dolt.AddAllAsync();
                var commitResult = await _dolt.CommitAsync(commitMessage);
                result.CommitHash = commitResult.CommitHash;
            }

            result.Status = SyncStatus.Completed;
            _logger.LogInformation("Successfully imported {Count} documents from ChromaDB to Dolt", result.Added);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import from ChromaDB to Dolt");
            result.Status = SyncStatus.Failed;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private string ExtractSourceIdFromChunkId(string chunkId)
    {
        // Extract source_id from chunk_id (format: sourceId_chunk_N)
        var lastChunkIndex = chunkId.LastIndexOf("_chunk_");
        if (lastChunkIndex > 0)
        {
            return chunkId.Substring(0, lastChunkIndex);
        }
        // If no chunk pattern found, use the whole ID
        return chunkId;
    }

    private int ExtractChunkIndex(string chunkId)
    {
        // Extract chunk index from chunk_id (format: sourceId_chunk_N)
        var lastChunkIndex = chunkId.LastIndexOf("_chunk_");
        if (lastChunkIndex > 0 && lastChunkIndex + 7 < chunkId.Length)
        {
            var indexStr = chunkId.Substring(lastChunkIndex + 7);
            if (int.TryParse(indexStr, out var index))
            {
                return index;
            }
        }
        return 0;
    }

    private string EscapeSql(string value)
    {
        // Basic SQL escaping for string values
        return value.Replace("'", "''").Replace("\\", "\\\\");
    }

        private class ReconstructedDocument
        {
            public string SourceId { get; set; } = "";
            public SortedDictionary<int, string> Chunks { get; set; } = new();
            public Dictionary<string, object> Metadata { get; set; } = new();
        }
    }

    /// <summary>
    /// Helper class for querying document data
    /// </summary>
    internal class DocumentRecord
    {
        public string SourceTable { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string Content { get; set; } = "";
        public string ContentHash { get; set; } = "";
        public string Identifier { get; set; } = "";
        
        public DocumentRecord() { }
        
        public DocumentRecord(string sourceTable, string sourceId, string content, string contentHash, string identifier)
        {
            SourceTable = sourceTable;
            SourceId = sourceId;
            Content = content;
            ContentHash = contentHash;
            Identifier = identifier;
        }
    }
}