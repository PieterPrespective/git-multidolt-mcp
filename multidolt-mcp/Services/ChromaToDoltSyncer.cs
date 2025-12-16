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
    /// Stages changes from ChromaDB to Dolt (like 'git add').
    /// Handles the actual data transfer from working copy to version control.
    /// </summary>
    public class ChromaToDoltSyncer
    {
        private readonly IChromaDbService _chroma;
        private readonly IDoltCli _dolt;
        private readonly ChromaToDoltDetector _detector;
        private readonly ILogger<ChromaToDoltSyncer>? _logger;

        public ChromaToDoltSyncer(
            IChromaDbService chroma,
            IDoltCli dolt,
            ChromaToDoltDetector detector,
            ILogger<ChromaToDoltSyncer>? logger = null)
        {
            _chroma = chroma;
            _dolt = dolt;
            _detector = detector;
            _logger = logger;
        }

        /// <summary>
        /// Stage all local changes from ChromaDB to Dolt (like git add .)
        /// </summary>
        /// <param name="collectionName">The collection with local changes</param>
        /// <returns>Result indicating what was staged</returns>
        public async Task<StageResult> StageLocalChangesAsync(string collectionName)
        {
            _logger?.LogInformation("Staging local changes from ChromaDB collection {Collection} to Dolt", collectionName);

            try
            {
                // Detect local changes
                var localChanges = await _detector.DetectLocalChangesAsync(collectionName);
                
                if (!localChanges.HasChanges)
                {
                    _logger?.LogInformation("No local changes to stage");
                    return new StageResult(StageStatus.NoChanges, 0, 0, 0);
                }

                int added = 0, modified = 0, deleted = 0;
                var errors = new List<string>();

                // Process new documents
                foreach (var doc in localChanges.NewDocuments)
                {
                    try
                    {
                        await InsertDocumentToDoltAsync(doc, collectionName);
                        await ClearLocalChangeFlagAsync(collectionName, doc.DocId);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to insert {doc.DocId}: {ex.Message}");
                        _logger?.LogError(ex, "Failed to insert document {DocId}", doc.DocId);
                    }
                }

                // Process modified documents
                foreach (var doc in localChanges.ModifiedDocuments)
                {
                    try
                    {
                        await UpdateDocumentInDoltAsync(doc);
                        await ClearLocalChangeFlagAsync(collectionName, doc.DocId);
                        modified++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to update {doc.DocId}: {ex.Message}");
                        _logger?.LogError(ex, "Failed to update document {DocId}", doc.DocId);
                    }
                }

                // Process deleted documents
                foreach (var doc in localChanges.DeletedDocuments)
                {
                    try
                    {
                        await DeleteDocumentFromDoltAsync(doc);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to delete {doc.DocId}: {ex.Message}");
                        _logger?.LogError(ex, "Failed to delete document {DocId}", doc.DocId);
                    }
                }

                // Stage changes in Dolt
                await _dolt.AddAllAsync();
                
                // Update local changes tracking
                await UpdateLocalChangesTrackingAsync(collectionName);

                var status = errors.Any() ? StageStatus.Failed : StageStatus.Completed;
                var errorMessage = errors.Any() ? string.Join("; ", errors) : null;

                _logger?.LogInformation("Staging completed: {Added} added, {Modified} modified, {Deleted} deleted",
                    added, modified, deleted);

                return new StageResult(status, added, modified, deleted, errorMessage);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to stage local changes from collection {Collection}", collectionName);
                return new StageResult(StageStatus.Failed, 0, 0, 0, ex.Message);
            }
        }

        /// <summary>
        /// Initialize version control for an existing ChromaDB collection
        /// </summary>
        /// <param name="collectionName">The collection to version control</param>
        /// <param name="repositoryPath">Path to Dolt repository</param>
        /// <param name="initialCommitMessage">Commit message for initial import</param>
        /// <returns>Result of initialization</returns>
        public async Task<InitResult> InitializeFromChromaAsync(
            string collectionName, 
            string repositoryPath,
            string initialCommitMessage = "Initial import from ChromaDB")
        {
            _logger?.LogInformation("Initializing version control for ChromaDB collection {Collection}", collectionName);

            try
            {
                // Ensure schema tables exist
                await CreateSchemaTablesAsync();

                // Create collection record in Dolt
                await CreateCollectionRecordAsync(collectionName);

                // Get all documents from ChromaDB
                var chromaDocs = await GetAllChromaDocumentsAsync(collectionName);
                
                if (!chromaDocs.Any())
                {
                    _logger?.LogWarning("No documents found in collection {Collection}", collectionName);
                    return new InitResult(InitStatus.Completed, 0, "", "No documents to import");
                }

                int imported = 0;

                // Insert each document into Dolt
                foreach (var doc in chromaDocs)
                {
                    try
                    {
                        await InsertDocumentToDoltAsync(doc, collectionName);
                        imported++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to import document {DocId}", doc.DocId);
                    }
                }

                // Stage all changes
                await _dolt.AddAllAsync();

                // Commit the import
                var commitResult = await _dolt.CommitAsync(initialCommitMessage);

                // Clear all local change flags in ChromaDB
                await ClearAllLocalChangeFlagsAsync(collectionName);

                // Record initial sync state
                await RecordInitialSyncStateAsync(collectionName, commitResult.CommitHash, imported);

                _logger?.LogInformation("Successfully initialized version control with {Count} documents", imported);

                return new InitResult(InitStatus.Completed, imported, commitResult.CommitHash);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize version control for collection {Collection}", collectionName);
                return new InitResult(InitStatus.Failed, 0, null, ex.Message);
            }
        }

        /// <summary>
        /// Insert a new document from ChromaDB into Dolt
        /// </summary>
        public async Task InsertDocumentToDoltAsync(ChromaDocument doc, string collectionName)
        {
            _logger?.LogInformation("Inserting document {DocId} into Dolt", doc.DocId);

            // Convert to DoltDocumentV2
            var doltDoc = DocumentConverterUtilityV2.ConvertChromaToDolt(doc);
            
            _logger?.LogInformation("Converted ChromaDocument to DoltDocument: DocId='{DocId}', ContentLength={ContentLength}", 
                doltDoc.DocId, doltDoc.Content.Length);
            
            // Check if DocId is too long
            if (doltDoc.DocId.Length > 64)
            {
                _logger?.LogError("DocId '{DocId}' is too long ({Length} chars) for schema VARCHAR(64)", 
                    doltDoc.DocId, doltDoc.DocId.Length);
                throw new InvalidOperationException($"DocId is too long: {doltDoc.DocId.Length} characters (max 64)");
            }
            
            // Prepare metadata JSON
            var metadataJson = JsonSerializer.Serialize(doltDoc.Metadata ?? new Dictionary<string, object>());
            
            // Escape single quotes in content for SQL
            var escapedContent = doltDoc.Content.Replace("'", "''");
            var escapedTitle = doltDoc.Title?.Replace("'", "''");
            var escapedDocType = doltDoc.DocType?.Replace("'", "''");
            
            var sql = $@"
                INSERT INTO documents 
                    (doc_id, collection_name, content, content_hash, title, doc_type, metadata, created_at, updated_at)
                VALUES 
                    ('{doltDoc.DocId}', '{collectionName}', '{escapedContent}', '{doltDoc.ContentHash}',
                     {(escapedTitle != null ? $"'{escapedTitle}'" : "NULL")},
                     {(escapedDocType != null ? $"'{escapedDocType}'" : "NULL")},
                     '{metadataJson}', NOW(), NOW())";

            _logger?.LogDebug("Executing SQL: INSERT with DocId='{DocId}'", doltDoc.DocId);
            await _dolt.ExecuteAsync(sql);
            
            // Record in sync log
            await RecordSyncOperationAsync(doc.DocId, collectionName, doltDoc.ContentHash, 
                SyncDirection.ChromaToDolt, "staged");
        }

        /// <summary>
        /// Update an existing document in Dolt with changes from ChromaDB
        /// </summary>
        public async Task UpdateDocumentInDoltAsync(ChromaDocument doc)
        {
            _logger?.LogDebug("Updating document {DocId} in Dolt", doc.DocId);

            // Convert to DoltDocumentV2
            var doltDoc = DocumentConverterUtilityV2.ConvertChromaToDolt(doc);
            
            // Prepare metadata JSON
            var metadataJson = JsonSerializer.Serialize(doltDoc.Metadata ?? new Dictionary<string, object>());
            
            // Escape single quotes for SQL
            var escapedContent = doltDoc.Content.Replace("'", "''");
            var escapedTitle = doltDoc.Title?.Replace("'", "''");
            var escapedDocType = doltDoc.DocType?.Replace("'", "''");
            
            var sql = $@"
                UPDATE documents 
                SET content = '{escapedContent}',
                    content_hash = '{doltDoc.ContentHash}',
                    title = {(escapedTitle != null ? $"'{escapedTitle}'" : "NULL")},
                    doc_type = {(escapedDocType != null ? $"'{escapedDocType}'" : "NULL")},
                    metadata = '{metadataJson}',
                    updated_at = NOW()
                WHERE doc_id = '{doltDoc.DocId}' 
                  AND collection_name = '{doltDoc.CollectionName}'";

            await _dolt.ExecuteAsync(sql);
            
            // Record in sync log
            await RecordSyncOperationAsync(doc.DocId, doc.CollectionName, doltDoc.ContentHash, 
                SyncDirection.ChromaToDolt, "modified");
        }

        /// <summary>
        /// Delete a document from Dolt that was deleted in ChromaDB
        /// </summary>
        public async Task DeleteDocumentFromDoltAsync(DeletedDocumentV2 doc)
        {
            _logger?.LogDebug("Deleting document {DocId} from Dolt", doc.DocId);

            var sql = $@"
                DELETE FROM documents 
                WHERE doc_id = '{doc.DocId}' 
                  AND collection_name = '{doc.CollectionName}'";

            await _dolt.ExecuteAsync(sql);
            
            // Record in sync log
            await RecordSyncOperationAsync(doc.DocId, doc.CollectionName, "", 
                SyncDirection.ChromaToDolt, "deleted");
        }

        /// <summary>
        /// Clear the local change flag for a document in ChromaDB
        /// </summary>
        public async Task ClearLocalChangeFlagAsync(string collectionName, string docId)
        {
            _logger?.LogDebug("Clearing local change flag for document {DocId}", docId);

            try
            {
                // Update metadata to set is_local_change = false
                var whereFilter = new Dictionary<string, object>
                {
                    ["source_id"] = docId
                };

                var results = await _chroma.GetDocumentsAsync(collectionName, where: whereFilter);
                
                if (results != null)
                {
                    var resultsDict = results as Dictionary<string, object> ?? new Dictionary<string, object>();
                    var ids = (resultsDict.GetValueOrDefault("ids") as List<object>) ?? new List<object>();
                    var metadatas = (resultsDict.GetValueOrDefault("metadatas") as List<object>) ?? new List<object>();
                    
                    // Update each chunk's metadata
                    for (int i = 0; i < ids.Count; i++)
                    {
                        var chunkId = ids[i]?.ToString();
                        var metadata = metadatas[i] as Dictionary<string, object> ?? new Dictionary<string, object>();
                        
                        metadata["is_local_change"] = false;
                        
                        // Update the chunk in ChromaDB
                        await _chroma.UpdateDocumentsAsync(collectionName, new List<string> { chunkId! }, metadatas: new List<Dictionary<string, object>> { metadata });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to clear local change flag for document {DocId}", docId);
            }
        }

        /// <summary>
        /// Create the necessary schema tables if they don't exist
        /// </summary>
        public async Task CreateSchemaTablesAsync()
        {
            _logger?.LogDebug("Ensuring schema tables exist");

            // Execute essential schema tables directly
            var schemaTables = new[]
            {
                @"CREATE TABLE IF NOT EXISTS collections (
                    collection_name VARCHAR(255) PRIMARY KEY,
                    display_name VARCHAR(255),
                    description TEXT,
                    embedding_model VARCHAR(100) DEFAULT 'default',
                    chunk_size INT DEFAULT 512,
                    chunk_overlap INT DEFAULT 50,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                    document_count INT DEFAULT 0,
                    metadata JSON
                );",
                
                @"CREATE TABLE IF NOT EXISTS documents (
                    doc_id VARCHAR(64) NOT NULL,
                    collection_name VARCHAR(255) NOT NULL,
                    content LONGTEXT NOT NULL,
                    content_hash CHAR(64) NOT NULL,
                    title VARCHAR(500),
                    doc_type VARCHAR(100),
                    metadata JSON NOT NULL,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                    PRIMARY KEY (doc_id, collection_name)
                );",
                
                @"CREATE TABLE IF NOT EXISTS chroma_sync_state (
                    collection_name VARCHAR(255) PRIMARY KEY,
                    last_sync_commit VARCHAR(40),
                    last_sync_at DATETIME,
                    document_count INT DEFAULT 0,
                    chunk_count INT DEFAULT 0,
                    embedding_model VARCHAR(100),
                    sync_status VARCHAR(50) DEFAULT 'pending',
                    local_changes_count INT DEFAULT 0,
                    error_message TEXT,
                    metadata JSON
                );",
                
                @"CREATE TABLE IF NOT EXISTS document_sync_log (
                    id INT AUTO_INCREMENT PRIMARY KEY,
                    doc_id VARCHAR(64) NOT NULL,
                    collection_name VARCHAR(255) NOT NULL,
                    content_hash CHAR(64) NOT NULL,
                    chroma_chunk_ids JSON,
                    synced_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    sync_direction VARCHAR(50) NOT NULL,
                    sync_action VARCHAR(50) NOT NULL,
                    embedding_model VARCHAR(100)
                );",
                
                @"CREATE TABLE IF NOT EXISTS local_changes (
                    doc_id VARCHAR(64) NOT NULL,
                    collection_name VARCHAR(255) NOT NULL,
                    change_type VARCHAR(50) NOT NULL,
                    detected_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    content_hash_chroma CHAR(64),
                    content_hash_dolt CHAR(64),
                    metadata JSON,
                    PRIMARY KEY (doc_id, collection_name)
                );"
            };

            foreach (var tableSql in schemaTables)
            {
                try
                {
                    await _dolt.ExecuteAsync(tableSql);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("Schema table creation warning: {Message}", ex.Message);
                }
            }
        }

        #region Helper Methods

        /// <summary>
        /// Get all documents from a ChromaDB collection
        /// </summary>
        private async Task<List<ChromaDocument>> GetAllChromaDocumentsAsync(string collectionName)
        {
            _logger?.LogInformation("Getting all documents from ChromaDB collection {Collection}", collectionName);
            var results = await _chroma.GetDocumentsAsync(collectionName);
            
            _logger?.LogInformation("ChromaDB GetDocumentsAsync returned: {Results}", 
                results != null ? "non-null result" : "null result");
            
            if (results != null)
            {
                var resultsDict = results as Dictionary<string, object> ?? new Dictionary<string, object>();
                var ids = (resultsDict.GetValueOrDefault("ids") as List<object>) ?? new List<object>();
                _logger?.LogInformation("ChromaDB returned {Count} document IDs: {Ids}", 
                    ids.Count, string.Join(", ", ids));
            }
            
            var documents = ConvertQueryResultsToDocuments(results, collectionName);
            _logger?.LogInformation("ConvertQueryResultsToDocuments returned {Count} documents", documents.Count);
            
            return documents;
        }

        /// <summary>
        /// Convert ChromaDB query results to ChromaDocument objects
        /// </summary>
        private List<ChromaDocument> ConvertQueryResultsToDocuments(dynamic results, string collectionName)
        {
            // Similar to ChromaToDoltDetector implementation
            var documents = new Dictionary<string, List<ChromaChunk>>();
            
            if (results == null)
            {
                _logger?.LogWarning("ConvertQueryResultsToDocuments: results is null");
                return new List<ChromaDocument>();
            }

            var resultsDict = results as Dictionary<string, object> ?? new Dictionary<string, object>();
            var ids = (resultsDict.GetValueOrDefault("ids") as List<object>) ?? new List<object>();
            var docs = (resultsDict.GetValueOrDefault("documents") as List<object>) ?? new List<object>();
            var metadatas = (resultsDict.GetValueOrDefault("metadatas") as List<object>) ?? new List<object>();
            
            // Detect and fix ID/document content swap issue
            // If the first ID looks like document content (long text), swap the collections
            if (ids.Count > 0 && docs.Count > 0)
            {
                var firstId = ids[0]?.ToString() ?? "";
                var firstDoc = docs[0]?.ToString() ?? "";
                
                // If ID is very long (>64 chars) and looks like content, but the document is short and looks like an ID, swap them
                if (firstId.Length > 64 && firstDoc.Length <= 64 && 
                    (firstId.Contains("\n") || firstId.Contains(" ")) && 
                    !firstDoc.Contains("\n") && !firstDoc.Contains(" "))
                {
                    _logger?.LogWarning("Detected ID/document content swap - correcting data order");
                    (ids, docs) = (docs, ids); // Swap the collections
                }
            }
            
            _logger?.LogInformation("ConvertQueryResultsToDocuments: Processing {IdsCount} IDs, {DocsCount} docs, {MetasCount} metadatas",
                ids.Count, docs.Count, metadatas.Count);
            
            for (int i = 0; i < ids.Count; i++)
            {
                var chunkId = ids[i]?.ToString() ?? "";
                var content = docs[i]?.ToString() ?? "";
                var metadata = metadatas[i] as Dictionary<string, object> ?? new Dictionary<string, object>();
                
                _logger?.LogInformation("Processing chunk {Index}: ID='{ChunkId}', ContentLength={ContentLength}", 
                    i, chunkId, content.Length);
                
                var docId = ExtractDocIdFromChunkId(chunkId);
                _logger?.LogInformation("Extracted docId '{DocId}' from chunkId '{ChunkId}'", docId, chunkId);
                
                // Check if this is a V1/direct document (no chunking metadata)
                // If there's no "source_id" metadata, this document was added directly to ChromaDB
                if (!metadata.ContainsKey("source_id") && !metadata.ContainsKey("chunk_index"))
                {
                    _logger?.LogInformation("Document '{ChunkId}' appears to be a direct ChromaDB document (no V2 chunking metadata)", chunkId);
                    
                    // For direct documents, treat the chunkId as the actual docId and add source_id metadata
                    metadata["source_id"] = chunkId;
                    metadata["chunk_index"] = 0;
                    metadata["total_chunks"] = 1;
                    docId = chunkId; // Use the actual ID as the document ID
                }
                
                if (!documents.ContainsKey(docId))
                {
                    documents[docId] = new List<ChromaChunk>();
                }
                
                documents[docId].Add(new ChromaChunk(
                    Id: chunkId,
                    Document: content,
                    Metadata: metadata
                ));
            }

            _logger?.LogInformation("ConvertQueryResultsToDocuments: Grouped into {DocumentCount} documents", documents.Count);

            var chromaDocuments = new List<ChromaDocument>();
            
            foreach (var kvp in documents)
            {
                var docId = kvp.Key;
                var chunks = kvp.Value;
                
                _logger?.LogDebug("Processing document '{DocId}' with {ChunkCount} chunks", docId, chunks.Count);
                
                var reassembledDoc = DocumentConverterUtilityV2.ConvertChromaToDolt(chunks);
                
                if (reassembledDoc != null)
                {
                    _logger?.LogInformation("Successfully reassembled document: Original docId='{OriginalDocId}', Reassembled DocId='{ReassembledDocId}', ContentLength={ContentLength}", 
                        docId, reassembledDoc.DocId, reassembledDoc.Content.Length);
                    var firstChunkMeta = chunks[0].Metadata;
                    
                    _logger?.LogInformation("Creating ChromaDocument with DocId='{DocId}', ContentLength={ContentLength}", 
                        docId, reassembledDoc.Content.Length);
                    
                    chromaDocuments.Add(new ChromaDocument(
                        DocId: docId,
                        CollectionName: collectionName,
                        Content: reassembledDoc.Content,
                        ContentHash: reassembledDoc.ContentHash,
                        Metadata: firstChunkMeta,
                        Chunks: chunks.Cast<dynamic>().ToList()
                    ));
                }
                else
                {
                    _logger?.LogWarning("Failed to reassemble document '{DocId}' - ConvertChromaToDolt returned null", docId);
                }
            }
            
            _logger?.LogInformation("ConvertQueryResultsToDocuments: Final result has {ChromaDocCount} documents", chromaDocuments.Count);
            return chromaDocuments;
        }

        /// <summary>
        /// Extract document ID from chunk ID
        /// </summary>
        private string ExtractDocIdFromChunkId(string chunkId)
        {
            var lastChunkIndex = chunkId.LastIndexOf("_chunk_");
            return lastChunkIndex > 0 ? chunkId.Substring(0, lastChunkIndex) : chunkId;
        }

        /// <summary>
        /// Create a collection record in Dolt
        /// </summary>
        private async Task CreateCollectionRecordAsync(string collectionName)
        {
            var sql = $@"
                INSERT IGNORE INTO collections 
                    (collection_name, display_name, description, created_at, updated_at)
                VALUES 
                    ('{collectionName}', '{collectionName}', 'Imported from ChromaDB', NOW(), NOW())";

            await _dolt.ExecuteAsync(sql);
        }

        /// <summary>
        /// Clear all local change flags in a collection
        /// </summary>
        private async Task ClearAllLocalChangeFlagsAsync(string collectionName)
        {
            // This would update all documents in ChromaDB to clear is_local_change flags
            // Implementation depends on ChromaDB API capabilities
            _logger?.LogDebug("Clearing all local change flags in collection {Collection}", collectionName);
        }

        /// <summary>
        /// Record initial sync state after import
        /// </summary>
        private async Task RecordInitialSyncStateAsync(string collectionName, string commitHash, int documentCount)
        {
            var sql = $@"
                INSERT INTO chroma_sync_state 
                    (collection_name, last_sync_commit, last_sync_at, document_count, sync_status)
                VALUES 
                    ('{collectionName}', '{commitHash}', NOW(), {documentCount}, 'synced')
                ON DUPLICATE KEY UPDATE
                    last_sync_commit = VALUES(last_sync_commit),
                    last_sync_at = VALUES(last_sync_at),
                    document_count = VALUES(document_count),
                    sync_status = VALUES(sync_status)";

            await _dolt.ExecuteAsync(sql);
        }

        /// <summary>
        /// Record a sync operation in the log
        /// </summary>
        private async Task RecordSyncOperationAsync(
            string docId, 
            string collectionName, 
            string contentHash,
            SyncDirection direction, 
            string action)
        {
            var sql = $@"
                INSERT INTO document_sync_log 
                    (doc_id, collection_name, content_hash, sync_direction, sync_action, synced_at)
                VALUES 
                    ('{docId}', '{collectionName}', '{contentHash}', 
                     '{direction.ToString().ToLower()}', '{action}', NOW())
                ON DUPLICATE KEY UPDATE
                    content_hash = VALUES(content_hash),
                    sync_direction = VALUES(sync_direction),
                    sync_action = VALUES(sync_action),
                    synced_at = VALUES(synced_at)";

            await _dolt.ExecuteAsync(sql);
        }

        /// <summary>
        /// Update local changes tracking in sync state
        /// </summary>
        private async Task UpdateLocalChangesTrackingAsync(string collectionName)
        {
            var sql = $@"
                UPDATE chroma_sync_state
                SET local_changes_count = 0,
                    sync_status = 'synced',
                    last_sync_at = NOW()
                WHERE collection_name = '{collectionName}'";

            await _dolt.ExecuteAsync(sql);
        }

        #endregion
    }
}