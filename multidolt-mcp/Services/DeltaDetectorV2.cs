using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DMMS.Models;
using Microsoft.Extensions.Logging;

namespace DMMS.Services
{
    /// <summary>
    /// V2 Service for detecting changes between Dolt and ChromaDB using generalized schema.
    /// Simplified queries using single documents table instead of UNION across multiple tables.
    /// </summary>
    public class DeltaDetectorV2
    {
        private readonly IDoltCli _dolt;
        private readonly ILogger<DeltaDetectorV2>? _logger;

        /// <summary>
        /// Initialize the DeltaDetectorV2 with a Dolt CLI interface
        /// </summary>
        public DeltaDetectorV2(IDoltCli dolt, ILogger<DeltaDetectorV2>? logger = null)
        {
            _dolt = dolt;
            _logger = logger;
        }

        /// <summary>
        /// Find documents that need syncing from Dolt to ChromaDB (new or modified).
        /// Simplified query using single documents table.
        /// </summary>
        /// <param name="collectionName">The ChromaDB collection name to check against</param>
        /// <returns>Collection of documents that need to be synced</returns>
        public async Task<IEnumerable<DocumentDeltaV2>> GetPendingSyncDocumentsAsync(string collectionName)
        {
            _logger?.LogDebug("Finding pending sync documents for collection {Collection}", collectionName);

            // Simplified query using single documents table with generalized schema
            var sql = $@"
                SELECT 
                    d.doc_id,
                    d.collection_name,
                    d.content,
                    d.content_hash,
                    d.title,
                    d.doc_type,
                    CAST(d.metadata AS CHAR) as metadata,
                    CASE 
                        WHEN dsl.content_hash IS NULL THEN 'new'
                        WHEN dsl.content_hash != d.content_hash THEN 'modified'
                    END as change_type
                FROM documents d
                LEFT JOIN document_sync_log dsl 
                    ON dsl.doc_id = d.doc_id
                    AND dsl.collection_name = d.collection_name
                    AND dsl.sync_direction = 'dolt_to_chroma'
                WHERE d.collection_name = '{collectionName}'
                    AND (dsl.content_hash IS NULL 
                         OR dsl.content_hash != d.content_hash)";

            try
            {
                var deltas = await _dolt.QueryAsync<DocumentDeltaV2>(sql);
                var deltaList = deltas.ToList();
                
                _logger?.LogInformation("Found {Count} documents pending sync: {New} new, {Modified} modified",
                    deltaList.Count,
                    deltaList.Count(d => d.IsNew),
                    deltaList.Count(d => d.IsModified));
                
                return deltaList;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get pending sync documents for collection {Collection}", collectionName);
                throw new DoltException($"Failed to get pending sync documents: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Find documents deleted from Dolt that need to be removed from ChromaDB.
        /// Simplified query using single documents table.
        /// </summary>
        /// <param name="collectionName">The ChromaDB collection name to check</param>
        /// <returns>Collection of deleted documents</returns>
        public async Task<IEnumerable<DeletedDocumentV2>> GetDeletedDocumentsAsync(string collectionName)
        {
            _logger?.LogDebug("Finding deleted documents for collection {Collection}", collectionName);

            // Find documents in sync log that no longer exist in documents table
            var sql = $@"
                SELECT 
                    dsl.doc_id,
                    dsl.collection_name,
                    CAST(dsl.chroma_chunk_ids AS CHAR) as chunk_ids
                FROM document_sync_log dsl
                LEFT JOIN documents d 
                    ON d.doc_id = dsl.doc_id 
                    AND d.collection_name = dsl.collection_name
                WHERE dsl.collection_name = '{collectionName}'
                    AND dsl.sync_direction = 'dolt_to_chroma'
                    AND d.doc_id IS NULL";

            try
            {
                var deleted = await _dolt.QueryAsync<DeletedDocumentV2>(sql);
                var deletedList = deleted.ToList();
                
                _logger?.LogInformation("Found {Count} deleted documents to remove from ChromaDB", 
                    deletedList.Count);
                
                return deletedList;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get deleted documents for collection {Collection}", collectionName);
                throw new DoltException($"Failed to get deleted documents: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get all documents from a collection (for full sync operations)
        /// </summary>
        /// <param name="collectionName">The collection to retrieve documents from</param>
        /// <returns>All documents in the collection</returns>
        public async Task<IEnumerable<DoltDocumentV2>> GetAllDocumentsAsync(string collectionName)
        {
            _logger?.LogDebug("Getting all documents for collection {Collection}", collectionName);

            var sql = $@"
                SELECT 
                    doc_id,
                    collection_name,
                    content,
                    content_hash,
                    title,
                    doc_type,
                    CAST(metadata AS CHAR) as metadata_json
                FROM documents
                WHERE collection_name = '{collectionName}'
                ORDER BY doc_id";

            try
            {
                var results = await _dolt.QueryAsync<dynamic>(sql);
                var documents = new List<DoltDocumentV2>();

                foreach (var row in results)
                {
                    // Parse metadata JSON - try both possible column names
                    string metadataJson = "";
                    try
                    {
                        // Try the alias first
                        metadataJson = row.metadata_json?.ToString() ?? "";
                    }
                    catch
                    {
                        try
                        {
                            // Fallback to the original column name
                            metadataJson = row.metadata?.ToString() ?? "";
                        }
                        catch
                        {
                            // If neither work, use empty string
                            metadataJson = "";
                        }
                    }
                    
                    var metadataDict = string.IsNullOrEmpty(metadataJson) 
                        ? new Dictionary<string, object>()
                        : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson) 
                            ?? new Dictionary<string, object>();

                    // Handle both dynamic and JsonElement result types
                    string docId = GetFieldValue(row, "doc_id");
                    string rowCollectionName = GetFieldValue(row, "collection_name"); 
                    string content = GetFieldValue(row, "content");
                    string contentHash = GetFieldValue(row, "content_hash");
                    string title = GetFieldValue(row, "title");
                    string docType = GetFieldValue(row, "doc_type");
                    
                    documents.Add(new DoltDocumentV2(
                        DocId: docId,
                        CollectionName: rowCollectionName,
                        Content: content,
                        ContentHash: contentHash,
                        Title: title,
                        DocType: docType,
                        Metadata: metadataDict
                    ));
                }

                _logger?.LogInformation("Retrieved {Count} documents from collection {Collection}", 
                    documents.Count, collectionName);

                return documents;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get all documents for collection {Collection}", collectionName);
                throw new DoltException($"Failed to get all documents: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Helper method to safely extract field values from dynamic results (handles both dynamic and JsonElement types)
        /// </summary>
        private static string GetFieldValue(dynamic row, string fieldName)
        {
            try
            {
                // If it's a JsonElement, use proper JsonElement access
                if (row is System.Text.Json.JsonElement jsonElement)
                {
                    if (jsonElement.TryGetProperty(fieldName, out var property))
                    {
                        return property.GetString() ?? "";
                    }
                    return "";
                }

                // Try as ExpandoObject (dynamic dictionary)
                if (row is System.Dynamic.ExpandoObject expandoObj)
                {
                    var dict = expandoObj as IDictionary<string, object>;
                    if (dict.TryGetValue(fieldName, out var value))
                    {
                        return value?.ToString() ?? "";
                    }
                    return "";
                }

                // Fallback: try direct dynamic property access
                return ((object)row).GetType().GetProperty(fieldName)?.GetValue(row)?.ToString() ?? "";
            }
            catch
            {
                // Final fallback: try direct dynamic access
                try
                {
                    var result = (string)row[fieldName];
                    return result ?? "";
                }
                catch
                {
                    return "";
                }
            }
        }

        /// <summary>
        /// Use Dolt's DOLT_DIFF for commit-to-commit comparison on documents table
        /// </summary>
        /// <param name="fromCommit">Starting commit</param>
        /// <param name="toCommit">Ending commit</param>
        /// <param name="collectionName">Collection to analyze</param>
        /// <returns>Differences between commits</returns>
        public async Task<IEnumerable<DiffRowV2>> GetCommitDiffAsync(
            string fromCommit, 
            string toCommit,
            string? collectionName = null)
        {
            _logger?.LogDebug("Getting diff between {From} and {To} for collection {Collection}", 
                fromCommit, toCommit, collectionName ?? "all");

            var whereClause = collectionName != null 
                ? $"WHERE to_collection_name = '{collectionName}' OR from_collection_name = '{collectionName}'"
                : "";

            // Use DOLT_DIFF on documents table
            var sql = $@"
                SELECT 
                    diff_type,
                    to_doc_id as doc_id,
                    to_collection_name as collection_name,
                    from_content_hash,
                    to_content_hash,
                    to_content as content,
                    to_title as title,
                    to_doc_type as doc_type,
                    CAST(to_metadata AS CHAR) as metadata
                FROM DOLT_DIFF('{fromCommit}', '{toCommit}', 'documents')
                {whereClause}";

            try
            {
                var diffs = await _dolt.QueryAsync<DiffRowV2>(sql);
                var diffList = diffs.ToList();
                
                _logger?.LogInformation("Found {Count} differences: {Added} added, {Modified} modified, {Deleted} deleted",
                    diffList.Count,
                    diffList.Count(d => d.DiffType == "added"),
                    diffList.Count(d => d.DiffType == "modified"),
                    diffList.Count(d => d.DiffType == "removed"));
                
                return diffList;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get commit diff between {From} and {To}", fromCommit, toCommit);
                throw new DoltException($"Failed to get commit diff: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Check if a collection exists in Dolt
        /// </summary>
        public async Task<bool> CollectionExistsAsync(string collectionName)
        {
            var sql = $@"
                SELECT COUNT(*) as count
                FROM collections
                WHERE collection_name = '{collectionName}'";

            try
            {
                var results = await _dolt.QueryAsync<dynamic>(sql);
            var result = results.FirstOrDefault();
                return result?.count > 0;
            }
            catch
            {
                // Table might not exist yet
                return false;
            }
        }

        /// <summary>
        /// Get all available collection names from the Dolt database
        /// </summary>
        /// <returns>List of collection names that exist in the documents table</returns>
        public async Task<List<string>> GetAvailableCollectionNamesAsync()
        {
            _logger?.LogInformation("PP13-49: GetAvailableCollectionNamesAsync method ENTRY - This confirms enhanced code is deployed");
            _logger?.LogInformation("PP13-49: DoltCli instance: {DoltInstance}, Logger: {Logger}", 
                _dolt?.GetType().FullName ?? "NULL", 
                _logger?.GetType().FullName ?? "NULL");
            _logger?.LogInformation("GetAvailableCollectionNamesAsync: Starting collection discovery");

            var collections = new List<string>();

            // First try: Query documents table for actual collection names
            var documentsSql = @"
                SELECT DISTINCT collection_name
                FROM documents
                WHERE collection_name IS NOT NULL AND collection_name != ''
                ORDER BY collection_name";

            try
            {
                _logger?.LogInformation("PP13-49: About to execute SQL on documents table");
                _logger?.LogInformation("GetAvailableCollectionNamesAsync: Querying documents table with SQL: {Sql}", documentsSql);
                
                var results = await _dolt.QueryAsync<dynamic>(documentsSql);
                
                _logger?.LogInformation("PP13-49: SQL execution completed successfully");
                _logger?.LogInformation("GetAvailableCollectionNamesAsync: Query returned {Count} rows", results?.Count() ?? 0);
                
                if (results != null)
                {
                    _logger?.LogInformation("PP13-49: Results object type: {Type}", results.GetType().FullName);
                }

                _logger?.LogInformation("PP13-49: Starting to iterate through {Count} result rows", results?.Count() ?? 0);
                
                foreach (var row in results)
                {
                    _logger?.LogInformation("PP13-49: Processing row: {Row}", (object)(row?.ToString() ?? "NULL"));
                    
                    if (row?.collection_name != null)
                    {
                        var collectionName = row.collection_name.ToString();
                        collections.Add(collectionName);
                        _logger?.LogInformation("PP13-49: Successfully extracted collection name: {Collection}", (object)collectionName);
                        _logger?.LogInformation("GetAvailableCollectionNamesAsync: Found collection from documents: {Collection}", (object)collectionName);
                    }
                    else
                    {
                        _logger?.LogWarning("PP13-49: Row has null collection_name: {Row}", (object)(row?.ToString() ?? "NULL"));
                    }
                }

                if (collections.Any())
                {
                    _logger?.LogInformation("GetAvailableCollectionNamesAsync: Successfully found {Count} collections from documents table: {Collections}", 
                        collections.Count, string.Join(", ", collections));
                    return collections;
                }
                else
                {
                    _logger?.LogWarning("GetAvailableCollectionNamesAsync: No collections found in documents table, trying collections table");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "PP13-49: EXCEPTION in documents table query - Type: {ExceptionType}, Message: {Message}", ex.GetType().FullName, ex.Message);
                _logger?.LogWarning(ex, "GetAvailableCollectionNamesAsync: Failed to query documents table: {Message}", ex.Message);
            }

            // Second try: Query collections table directly
            var collectionsSql = @"
                SELECT DISTINCT collection_name
                FROM collections
                WHERE collection_name IS NOT NULL AND collection_name != ''
                ORDER BY collection_name";

            try
            {
                _logger?.LogInformation("GetAvailableCollectionNamesAsync: Querying collections table with SQL: {Sql}", collectionsSql);
                var results = await _dolt.QueryAsync<dynamic>(collectionsSql);
                _logger?.LogInformation("GetAvailableCollectionNamesAsync: Collections table query returned {Count} rows", results?.Count() ?? 0);

                foreach (var row in results)
                {
                    if (row?.collection_name != null)
                    {
                        var collectionName = row.collection_name.ToString();
                        collections.Add(collectionName);
                        _logger?.LogInformation("GetAvailableCollectionNamesAsync: Found collection from collections table: {Collection}", (object)collectionName);
                    }
                }

                if (collections.Any())
                {
                    _logger?.LogInformation("GetAvailableCollectionNamesAsync: Successfully found {Count} collections from collections table: {Collections}", 
                        collections.Count, string.Join(", ", collections));
                    return collections;
                }
                else
                {
                    _logger?.LogWarning("GetAvailableCollectionNamesAsync: No collections found in collections table either");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "GetAvailableCollectionNamesAsync: Failed to query collections table: {Message}", ex.Message);
            }

            // Third try: Check what tables actually exist
            try
            {
                _logger?.LogInformation("GetAvailableCollectionNamesAsync: Checking what tables exist in database");
                var showTablesSql = "SHOW TABLES";
                var tableResults = await _dolt.QueryAsync<dynamic>(showTablesSql);
                _logger?.LogInformation("GetAvailableCollectionNamesAsync: Found {Count} tables in database", tableResults?.Count() ?? 0);
                
                foreach (var tableRow in tableResults)
                {
                    var tableName = tableRow?.Tables_in_dolt_repo_10?.ToString() ?? 
                                   tableRow?.Tables_in_dolt_repo?.ToString() ?? 
                                   tableRow?.Table?.ToString() ??
                                   tableRow?.GetType().GetProperties().FirstOrDefault()?.GetValue(tableRow)?.ToString();
                    _logger?.LogInformation("GetAvailableCollectionNamesAsync: Found table: {Table}", (object)(tableName ?? "unknown"));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "GetAvailableCollectionNamesAsync: Failed to show tables: {Message}", ex.Message);
            }

            _logger?.LogError("PP13-49: FINAL FALLBACK - No collections found in any table, returning empty list");
            _logger?.LogWarning("GetAvailableCollectionNamesAsync: No collections found in any table, returning empty list");
            return new List<string>();
        }

        /// <summary>
        /// Get sync state for a collection
        /// </summary>
        public async Task<ChromaSyncStateV2?> GetSyncStateAsync(string collectionName)
        {
            var sql = $@"
                SELECT 
                    collection_name,
                    last_sync_commit,
                    last_sync_at,
                    document_count,
                    chunk_count,
                    embedding_model,
                    sync_status,
                    local_changes_count,
                    error_message,
                    CAST(metadata AS CHAR) as metadata
                FROM chroma_sync_state
                WHERE collection_name = '{collectionName}'";

            try
            {
                var results = await _dolt.QueryAsync<dynamic>(sql);
                var result = results.FirstOrDefault();
                if (result == null) return null;
                
                return new ChromaSyncStateV2(
                    CollectionName: result.collection_name,
                    LastSyncCommit: result.last_sync_commit,
                    LastSyncAt: result.last_sync_at,
                    DocumentCount: result.document_count,
                    ChunkCount: result.chunk_count,
                    EmbeddingModel: result.embedding_model,
                    SyncStatus: result.sync_status,
                    LocalChangesCount: result.local_changes_count,
                    ErrorMessage: result.error_message,
                    Metadata: result.metadata
                );
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get sync state for collection {Collection}", collectionName);
                return null;
            }
        }

        /// <summary>
        /// Update sync state for a collection
        /// </summary>
        public async Task UpdateSyncStateAsync(string collectionName, string commitHash, int documentCount, int chunkCount)
        {
            // Ensure collection exists in collections table before updating sync state
            await EnsureCollectionExistsAsync(collectionName);

            var sql = $@"
                INSERT INTO chroma_sync_state 
                    (collection_name, last_sync_commit, last_sync_at, document_count, chunk_count, sync_status)
                VALUES 
                    ('{collectionName}', '{commitHash}', NOW(), {documentCount}, {chunkCount}, 'synced')
                ON DUPLICATE KEY UPDATE
                    last_sync_commit = VALUES(last_sync_commit),
                    last_sync_at = VALUES(last_sync_at),
                    document_count = VALUES(document_count),
                    chunk_count = VALUES(chunk_count),
                    sync_status = VALUES(sync_status),
                    error_message = NULL";

            try
            {
                await _dolt.ExecuteAsync(sql);
                _logger?.LogDebug("Updated sync state for collection {Collection}", collectionName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to update sync state for collection {Collection}", collectionName);
                throw new DoltException($"Failed to update sync state: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Record a sync operation in the log
        /// </summary>
        public async Task RecordSyncOperationAsync(
            string docId,
            string collectionName,
            string contentHash,
            List<string> chunkIds,
            SyncDirection direction,
            string action)
        {
            var chunkIdsJson = System.Text.Json.JsonSerializer.Serialize(chunkIds);
            
            var sql = $@"
                INSERT INTO document_sync_log 
                    (doc_id, collection_name, content_hash, chroma_chunk_ids, sync_direction, sync_action, synced_at)
                VALUES 
                    ('{docId}', '{collectionName}', '{contentHash}', '{chunkIdsJson}', 
                     '{direction.ToString().ToLower()}', '{action}', NOW())
                ON DUPLICATE KEY UPDATE
                    content_hash = VALUES(content_hash),
                    chroma_chunk_ids = VALUES(chroma_chunk_ids),
                    sync_direction = VALUES(sync_direction),
                    sync_action = VALUES(sync_action),
                    synced_at = VALUES(synced_at)";

            try
            {
                await _dolt.ExecuteAsync(sql);
                _logger?.LogDebug("Recorded sync operation for document {DocId} in collection {Collection}", 
                    docId, collectionName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to record sync operation for document {DocId}", docId);
                throw new DoltException($"Failed to record sync operation: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Ensures that a collection exists in the Dolt collections table before sync operations
        /// </summary>
        private async Task EnsureCollectionExistsAsync(string collectionName)
        {
            try
            {
                _logger?.LogDebug("Ensuring collection '{Collection}' exists in Dolt database", collectionName);

                // Use INSERT IGNORE to create collection only if it doesn't exist
                var insertSql = $@"
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
                        '{collectionName}',
                        '{collectionName}',
                        'Collection auto-created during sync operation',
                        'default',
                        512,
                        50,
                        0,
                        JSON_OBJECT('created_by', 'auto_sync', 'created_at', NOW())
                    )";
                
                await _dolt.ExecuteAsync(insertSql);
                _logger?.LogDebug("Ensured collection '{Collection}' exists in Dolt database", collectionName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to ensure collection '{Collection}' exists in Dolt database", collectionName);
                throw new InvalidOperationException($"Failed to create collection '{collectionName}' in Dolt: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Represents a diff row from DOLT_DIFF (V2)
    /// </summary>
    public class DiffRowV2
    {
        public string DiffType { get; set; } = "";  // "added", "modified", "removed"
        public string DocId { get; set; } = "";
        public string CollectionName { get; set; } = "";
        public string? FromContentHash { get; set; }
        public string? ToContentHash { get; set; }
        public string? Content { get; set; }
        public string? Title { get; set; }
        public string? DocType { get; set; }
        public string? Metadata { get; set; }
    }
}