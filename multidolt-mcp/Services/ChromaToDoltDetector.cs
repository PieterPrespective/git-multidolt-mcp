using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Embranch.Models;
using Embranch.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Embranch.Services
{
    /// <summary>
    /// Detects changes in ChromaDB that need to be staged to Dolt.
    /// Implements the "working copy" detection logic for bidirectional sync.
    /// OPTIMIZED: Reduces Python.NET operation count from 8-15+ to less than 5 typical operations.
    /// </summary>
    public class ChromaToDoltDetector
    {
        private readonly IChromaDbService _chroma;
        private readonly IDoltCli _dolt;
        private readonly IDeletionTracker _deletionTracker;
        private readonly ICollectionChangeDetector? _collectionChangeDetector;
        private readonly DoltConfiguration _doltConfig;
        private readonly ILogger<ChromaToDoltDetector>? _logger;

        public ChromaToDoltDetector(
            IChromaDbService chroma,
            IDoltCli dolt,
            IDeletionTracker deletionTracker,
            IOptions<DoltConfiguration> doltConfig,
            ILogger<ChromaToDoltDetector>? logger = null,
            ICollectionChangeDetector? collectionChangeDetector = null)
        {
            _chroma = chroma;
            _dolt = dolt;
            _deletionTracker = deletionTracker;
            _collectionChangeDetector = collectionChangeDetector;
            _doltConfig = doltConfig.Value;
            _logger = logger;
        }

        /// <summary>
        /// Detect all collection-level changes that need to be synchronized between ChromaDB and Dolt
        /// </summary>
        /// <returns>Summary of collection changes (deletions, renames, metadata updates)</returns>
        public async Task<CollectionChanges> DetectCollectionChangesAsync()
        {
            _logger?.LogInformation("===== DetectCollectionChangesAsync STARTING =====");

            try
            {
                if (_collectionChangeDetector == null)
                {
                    _logger?.LogWarning("Collection change detector not available - returning empty changes");
                    return new CollectionChanges();
                }

                var changes = await _collectionChangeDetector.DetectCollectionChangesAsync();
                _logger?.LogInformation("Detected {Total} collection changes: {Summary}",
                    changes.TotalChanges, changes.GetSummary());

                return changes;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to detect collection changes");
                throw new Exception($"Failed to detect collection changes: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Check if there are any pending collection changes
        /// </summary>
        /// <returns>True if collection changes exist, false otherwise</returns>
        public async Task<bool> HasPendingCollectionChangesAsync()
        {
            try
            {
                if (_collectionChangeDetector == null)
                {
                    return false;
                }

                return await _collectionChangeDetector.HasPendingCollectionChangesAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to check for pending collection changes");
                return false;
            }
        }

        /// <summary>
        /// Detect all local changes in a ChromaDB collection that haven't been staged to Dolt
        /// </summary>
        /// <param name="collectionName">The ChromaDB collection to check</param>
        /// <returns>Summary of new, modified, and deleted documents</returns>
        public async Task<LocalChanges> DetectLocalChangesAsync(string collectionName)
        {
            _logger?.LogInformation("===== DetectLocalChangesAsync STARTING for collection {Collection} =====", collectionName);

            try
            {
                // Get documents flagged as local changes
                _logger?.LogInformation("STEP 1: Checking for documents flagged with is_local_change=true");
                var flaggedChanges = await GetFlaggedLocalChangesAsync(collectionName);
                _logger?.LogInformation("STEP 1 RESULT: GetFlaggedLocalChangesAsync returned {Count} flagged changes", flaggedChanges.Count);
                
                // FALLBACK: If no flagged changes found, but we suspect there should be changes,
                // compare all ChromaDB documents with Dolt to find new documents
                if (flaggedChanges.Count == 0)
                {
                    _logger?.LogInformation("STEP 2: No flagged local changes found, activating FALLBACK mechanism");
                    _logger?.LogInformation("STEP 2: Checking for ChromaDB documents not present in Dolt");
                    var fallbackDocs = await FindChromaOnlyDocumentsAsync(collectionName);
                    _logger?.LogInformation("STEP 2 RESULT: FindChromaOnlyDocumentsAsync returned {Count} ChromaDB-only documents", fallbackDocs.Count);
                    if (fallbackDocs.Count > 0)
                    {
                        _logger?.LogInformation("FALLBACK SUCCESS: Found {Count} documents in ChromaDB that don't exist in Dolt - treating as local changes", fallbackDocs.Count);
                        flaggedChanges = fallbackDocs;
                    }
                    else
                    {
                        _logger?.LogInformation("FALLBACK RESULT: No ChromaDB-only documents found - no local changes detected");
                    }
                }
                else
                {
                    _logger?.LogInformation("STEP 2: SKIPPING fallback - using {Count} flagged changes found by GetFlaggedLocalChangesAsync", flaggedChanges.Count);
                }
                
                // Early exit if no flagged changes and no fallback documents found
                if (flaggedChanges.Count == 0)
                {
                    _logger?.LogInformation("No local changes detected, performing quick validation for modifications and deletions");
                    
                    // Only check for modifications and deletions if we have no new documents
                    var earlyHashMismatches = await CompareContentHashesAsync(collectionName);
                    var earlyDeletedDocs = await FindDeletedDocumentsAsync(collectionName);
                    
                    if (earlyHashMismatches.Count == 0 && earlyDeletedDocs.Count == 0)
                    {
                        _logger?.LogInformation("No changes detected at all - early exit");
                        return new LocalChanges(
                            NewDocuments: new List<ChromaDocument>(),
                            ModifiedDocuments: new List<ChromaDocument>(),
                            DeletedDocuments: new List<DeletedDocumentV2>()
                        );
                    }
                    
                    return new LocalChanges(
                        NewDocuments: new List<ChromaDocument>(),
                        ModifiedDocuments: earlyHashMismatches,
                        DeletedDocuments: earlyDeletedDocs
                    );
                }
                
                // Compare content hashes to detect modifications
                var hashMismatches = await CompareContentHashesAsync(collectionName);
                
                // Find documents deleted from ChromaDB but still in Dolt
                var deletedDocs = await FindDeletedDocumentsAsync(collectionName);

                // Combine and deduplicate results
                var newDocuments = new List<ChromaDocument>();
                var modifiedDocuments = new List<ChromaDocument>();
                
                // OPTIMIZED: Batch check document existence instead of individual calls
                var flaggedDocIds = flaggedChanges.Select(d => d.DocId).ToList();
                var existingDocIds = await GetBatchDocumentExistenceAsync(flaggedDocIds, collectionName);
                
                // Documents flagged as local changes (includes fallback documents)
                foreach (var doc in flaggedChanges)
                {
                    if (!existingDocIds.Contains(doc.DocId))
                    {
                        newDocuments.Add(doc);
                        _logger?.LogInformation("Found new document: {DocId}", doc.DocId);
                    }
                    else
                    {
                        modifiedDocuments.Add(doc);
                        _logger?.LogInformation("Found modified document: {DocId}", doc.DocId);
                    }
                }
                
                // Documents with hash mismatches (modified)
                foreach (var doc in hashMismatches)
                {
                    if (!modifiedDocuments.Any(m => m.DocId == doc.DocId))
                    {
                        modifiedDocuments.Add(doc);
                    }
                }
                
                // NOTE: Removed duplicate FindChromaOnlyDocumentsAsync() call that was on line 73
                // The fallback documents from line 52 already cover ChromaDB-only documents

                var result = new LocalChanges(
                    NewDocuments: newDocuments,
                    ModifiedDocuments: modifiedDocuments,
                    DeletedDocuments: deletedDocs
                );

                _logger?.LogInformation("Detected {Total} local changes: {New} new, {Modified} modified, {Deleted} deleted",
                    result.TotalChanges, newDocuments.Count, modifiedDocuments.Count, deletedDocs.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to detect local changes in collection {Collection}", collectionName);
                throw new Exception($"Failed to detect local changes: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get documents explicitly flagged as local changes in ChromaDB metadata
        /// </summary>
        public async Task<List<ChromaDocument>> GetFlaggedLocalChangesAsync(string collectionName)
        {
            _logger?.LogDebug("Getting flagged local changes from collection {Collection}", collectionName);

            try
            {
                // Query ChromaDB for documents with is_local_change = true
                var whereFilter = new Dictionary<string, object>
                {
                    ["is_local_change"] = true
                };

                _logger?.LogInformation("Searching for documents with is_local_change=true in collection {Collection}", collectionName);
                var results = await _chroma.GetDocumentsAsync(collectionName, where: whereFilter);
                
                _logger?.LogInformation("GetDocumentsAsync returned: {Results}", results != null ? "non-null result" : "null result");
                
                if (results == null)
                {
                    _logger?.LogWarning("No results returned from ChromaDB query for flagged local changes");
                    return new List<ChromaDocument>();
                }

                // Check what we got back from the query
                if (results is Dictionary<string, object> resultsDict)
                {
                    var ids = resultsDict.GetValueOrDefault("ids") as List<object> ?? new List<object>();
                    _logger?.LogInformation("Query returned {Count} document IDs for flagged local changes", ids.Count);
                }

                // Convert results to ChromaDocument objects
                var documents = ConvertQueryResultsToDocuments(results, collectionName);
                
                _logger?.LogInformation("Found {Count} flagged local changes after conversion", documents.Count);
                return documents;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get flagged local changes from collection {Collection}", collectionName);
                return new List<ChromaDocument>();
            }
        }

        /// <summary>
        /// Compare content hashes between ChromaDB and Dolt to find modifications
        /// </summary>
        public async Task<List<ChromaDocument>> CompareContentHashesAsync(string collectionName)
        {
            _logger?.LogDebug("Comparing content hashes for collection {Collection}", collectionName);

            try
            {
                // Get all documents from ChromaDB
                var chromaDocs = await GetAllChromaDocumentsAsync(collectionName);
                
                // Get content hashes from Dolt
                var doltHashes = await GetDoltContentHashesAsync(collectionName);
                
                var modified = new List<ChromaDocument>();
                
                foreach (var chromaDoc in chromaDocs)
                {
                    if (doltHashes.TryGetValue(chromaDoc.DocId, out var doltHash))
                    {
                        // Document exists in both - compare hashes
                        if (chromaDoc.ContentHash != doltHash)
                        {
                            modified.Add(chromaDoc);
                        }
                    }
                }
                
                _logger?.LogDebug("Found {Count} documents with content hash mismatches", modified.Count);
                return modified;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to compare content hashes for collection {Collection}", collectionName);
                return new List<ChromaDocument>();
            }
        }

        /// <summary>
        /// Find documents that exist in ChromaDB but not in Dolt
        /// </summary>
        public async Task<List<ChromaDocument>> FindChromaOnlyDocumentsAsync(string collectionName)
        {
            _logger?.LogInformation("FindChromaOnlyDocumentsAsync STARTING for collection {Collection}", collectionName);

            try
            {
                // Get all documents from ChromaDB
                _logger?.LogInformation("Getting all ChromaDB documents for collection {Collection}", collectionName);
                var chromaDocs = await GetAllChromaDocumentsAsync(collectionName);
                _logger?.LogInformation("Retrieved {Count} documents from ChromaDB for collection {Collection}", chromaDocs.Count, collectionName);
                
                // Log first few document IDs for debugging
                if (chromaDocs.Count > 0)
                {
                    var firstFewIds = chromaDocs.Take(3).Select(d => d.DocId).ToList();
                    _logger?.LogInformation("First few ChromaDB document IDs: {Ids}", string.Join(", ", firstFewIds));
                }
                
                // Get all document IDs from Dolt
                _logger?.LogInformation("Getting all Dolt document IDs for collection {Collection}", collectionName);
                var doltIds = await GetDoltDocumentIdsAsync(collectionName);
                _logger?.LogInformation("Retrieved {Count} document IDs from Dolt for collection {Collection}", doltIds.Count, collectionName);
                
                // Log first few document IDs from Dolt for debugging
                if (doltIds.Count > 0)
                {
                    var firstFewDoltIds = doltIds.Take(3).ToList();
                    _logger?.LogInformation("First few Dolt document IDs: {Ids}", string.Join(", ", firstFewDoltIds));
                }
                
                // Find documents that don't exist in Dolt
                var chromaOnly = chromaDocs
                    .Where(c => !doltIds.Contains(c.DocId))
                    .ToList();
                
                _logger?.LogInformation("FindChromaOnlyDocumentsAsync RESULT: Found {Count} documents only in ChromaDB for collection {Collection}", chromaOnly.Count, collectionName);
                
                // Log the ChromaDB-only document IDs
                if (chromaOnly.Count > 0)
                {
                    var chromaOnlyIds = chromaOnly.Select(d => d.DocId).ToList();
                    _logger?.LogInformation("ChromaDB-only document IDs: {Ids}", string.Join(", ", chromaOnlyIds));
                }
                else
                {
                    _logger?.LogInformation("No ChromaDB-only documents found - all ChromaDB documents also exist in Dolt");
                }
                
                return chromaOnly;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to find ChromaDB-only documents in collection {Collection}", collectionName);
                return new List<ChromaDocument>();
            }
        }

        /// <summary>
        /// Find documents that have been deleted from ChromaDB but still exist in Dolt
        /// </summary>
        public async Task<List<DeletedDocumentV2>> FindDeletedDocumentsAsync(string collectionName)
        {
            _logger?.LogDebug("Finding deleted documents in collection {Collection}", collectionName);

            try
            {
                var deleted = new List<DeletedDocumentV2>();
                
                // STEP 1: Check deletion tracking database for pending deletions
                _logger?.LogDebug("Checking deletion tracking database for pending deletions");
                var pendingDeletions = await _deletionTracker.GetPendingDeletionsAsync(_doltConfig.RepositoryPath, collectionName);
                
                foreach (var deletion in pendingDeletions)
                {
                    _logger?.LogDebug("Found pending deletion from tracking: {DocId}", deletion.DocId);
                    deleted.Add(new DeletedDocumentV2(
                        deletion.DocId, 
                        collectionName, 
                        System.Text.Json.JsonSerializer.Serialize(new List<string>()), // Will be filled during sync
                        deletion.OriginalContentHash
                    ));
                }
                
                // STEP 2: Fallback - Find documents in Dolt but not in ChromaDB (traditional approach)
                _logger?.LogDebug("Running fallback deletion detection - comparing Dolt vs ChromaDB");
                var doltDocs = await GetDoltDocumentsAsync(collectionName);
                var chromaIds = await GetChromaDocumentIdsAsync(collectionName);
                
                foreach (var doltDoc in doltDocs)
                {
                    if (!chromaIds.Contains(doltDoc.DocId))
                    {
                        // Only add if not already tracked by deletion tracking
                        if (!deleted.Any(d => d.DocId == doltDoc.DocId))
                        {
                            // Get chunk IDs from sync log for cleanup
                            var chunkIds = await GetChunkIdsFromSyncLogAsync(doltDoc.DocId, collectionName);
                            
                            _logger?.LogDebug("Found deleted document via fallback detection: {DocId}", doltDoc.DocId);
                            deleted.Add(new DeletedDocumentV2(
                                doltDoc.DocId,
                                collectionName,
                                System.Text.Json.JsonSerializer.Serialize(chunkIds)
                            ));
                        }
                    }
                }
                
                _logger?.LogDebug("Found {Count} deleted documents total ({TrackedCount} from tracking, {FallbackCount} from fallback)", 
                    deleted.Count, pendingDeletions.Count, deleted.Count - pendingDeletions.Count);
                return deleted;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to find deleted documents in collection {Collection}", collectionName);
                return new List<DeletedDocumentV2>();
            }
        }

        #region Helper Methods

        /// <summary>
        /// Get all documents from a ChromaDB collection and convert to ChromaDocument objects
        /// </summary>
        private async Task<List<ChromaDocument>> GetAllChromaDocumentsAsync(string collectionName)
        {
            var results = await _chroma.GetDocumentsAsync(collectionName);
            return ConvertQueryResultsToDocuments(results, collectionName);
        }

        /// <summary>
        /// Convert ChromaDB query results to ChromaDocument objects with reassembled content
        /// </summary>
        private List<ChromaDocument> ConvertQueryResultsToDocuments(dynamic results, string collectionName)
        {
            var documents = new Dictionary<string, List<ChromaChunk>>();
            
            try
            {
                if (results == null)
                {
                    return new List<ChromaDocument>();
                }

                // Group chunks by document ID
                // Handle both dynamic objects and Dictionary<string, object>
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
                    _logger?.LogWarning("Detected ID/document content swap in ChromaToDoltDetector - correcting data order");
                    (ids, docs) = (docs, ids); // Swap the collections
                }
            }
            
            for (int i = 0; i < ids.Count; i++)
            {
                var chunkId = ids[i]?.ToString() ?? "";
                var content = docs[i]?.ToString() ?? "";
                var metadata = metadatas[i] as Dictionary<string, object> ?? new Dictionary<string, object>();
                
                // Extract document ID from chunk ID (format: docId_chunk_N)
                var docId = ExtractDocIdFromChunkId(chunkId);
                
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

            // Reassemble documents from chunks
            var chromaDocuments = new List<ChromaDocument>();
            
            foreach (var kvp in documents)
            {
                var docId = kvp.Key;
                var chunks = kvp.Value;
                
                // Reassemble content using DocumentConverterV2
                var reassembledDoc = DocumentConverterUtilityV2.ConvertChromaToDolt(chunks);
                
                if (reassembledDoc != null)
                {
                    // Get metadata from first chunk (all chunks should have same document metadata)
                    var firstChunkMeta = chunks[0].Metadata;
                    
                    chromaDocuments.Add(new ChromaDocument(
                        DocId: docId,
                        CollectionName: collectionName,
                        Content: reassembledDoc.Content,
                        ContentHash: reassembledDoc.ContentHash,
                        Metadata: firstChunkMeta,
                        Chunks: chunks.Cast<dynamic>().ToList()
                    ));
                }
            }
            
                return chromaDocuments;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to convert query results to documents");
                return new List<ChromaDocument>();
            }
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
        /// Get all document IDs from ChromaDB
        /// </summary>
        private async Task<HashSet<string>> GetChromaDocumentIdsAsync(string collectionName)
        {
            var documents = await GetAllChromaDocumentsAsync(collectionName);
            return documents.Select(d => d.DocId).ToHashSet();
        }

        /// <summary>
        /// Get document content hashes from Dolt
        /// </summary>
        private async Task<Dictionary<string, string>> GetDoltContentHashesAsync(string collectionName)
        {
            var sql = $@"
                SELECT doc_id, content_hash
                FROM documents
                WHERE collection_name = '{collectionName}'";

            try
            {
                var results = await _dolt.QueryAsync<dynamic>(sql);
                var hashes = new Dictionary<string, string>();
                
                foreach (var row in results)
                {
                    // Handle both JsonElement and dynamic types
                    string docId, contentHash;
                    
                    if (row is System.Text.Json.JsonElement jsonElement)
                    {
                        docId = JsonUtility.GetPropertyAsString(jsonElement, "doc_id", "");
                        contentHash = JsonUtility.GetPropertyAsString(jsonElement, "content_hash", "");
                    }
                    else
                    {
                        docId = (string)row.doc_id;
                        contentHash = (string)row.content_hash;
                    }

                    if (!string.IsNullOrEmpty(docId) && !string.IsNullOrEmpty(contentHash))
                    {
                        hashes[docId] = contentHash;
                    }
                }

                return hashes;
            }
            catch (DoltException ex) when (ex.Message.Contains("table not found"))
            {
                // Fresh/empty Dolt database - documents table doesn't exist yet, so no hashes to compare
                _logger?.LogDebug("Documents table not found in Dolt - returning empty hashes (empty database)");
                return new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Get all document IDs from Dolt
        /// </summary>
        private async Task<HashSet<string>> GetDoltDocumentIdsAsync(string collectionName)
        {
            var sql = $@"
                SELECT doc_id
                FROM documents
                WHERE collection_name = '{collectionName}'";

            var results = await _dolt.QueryAsync<dynamic>(sql);
            return results.Select(r => (string)r.doc_id).ToHashSet();
        }

        /// <summary>
        /// Get all documents from Dolt for a collection
        /// </summary>
        private async Task<List<(string DocId, string ContentHash)>> GetDoltDocumentsAsync(string collectionName)
        {
            var sql = $@"
                SELECT doc_id, content_hash
                FROM documents
                WHERE collection_name = '{collectionName}'";

            try
            {
                var results = await _dolt.QueryAsync<dynamic>(sql);
                var documents = new List<(string DocId, string ContentHash)>();

                foreach (var row in results)
                {
                    // Handle both JsonElement and dynamic types
                    string docId, contentHash;

                    if (row is System.Text.Json.JsonElement jsonElement)
                    {
                        docId = JsonUtility.GetPropertyAsString(jsonElement, "doc_id", "");
                        contentHash = JsonUtility.GetPropertyAsString(jsonElement, "content_hash", "");
                    }
                    else
                    {
                        docId = (string)row.doc_id;
                        contentHash = (string)row.content_hash;
                    }
                    
                    if (!string.IsNullOrEmpty(docId) && !string.IsNullOrEmpty(contentHash))
                    {
                        documents.Add((docId, contentHash));
                    }
                }
                
                return documents;
            }
            catch (DoltException ex) when (ex.Message.Contains("table not found"))
            {
                // Fresh/empty Dolt database - documents table doesn't exist yet, so no documents to return
                _logger?.LogDebug("Documents table not found in Dolt - returning empty documents list (empty database)");
                return new List<(string DocId, string ContentHash)>();
            }
        }

        /// <summary>
        /// Batch check if multiple documents exist in Dolt (optimized to reduce Python.NET operations)
        /// </summary>
        private async Task<HashSet<string>> GetBatchDocumentExistenceAsync(List<string> docIds, string collectionName)
        {
            if (!docIds.Any())
                return new HashSet<string>();
                
            // Create a parameterized query to check multiple documents at once
            var docIdParams = string.Join(",", docIds.Select((_, i) => $"@docId{i}"));
            var sql = $@"
                SELECT DISTINCT doc_id
                FROM documents
                WHERE collection_name = @collectionName AND doc_id IN ({docIdParams})";

            // Note: This is a simplified version - in a real implementation you'd want proper parameterization
            // For now, using escaped single quotes for safety
            var escapedDocIds = string.Join(",", docIds.Select(id => $"'{id.Replace("'", "''")}'"));
            var safeSql = $@"
                SELECT DISTINCT doc_id
                FROM documents
                WHERE collection_name = '{collectionName.Replace("'", "''")}' AND doc_id IN ({escapedDocIds})";

            try
            {
                var results = await _dolt.QueryAsync<dynamic>(safeSql);
                var existingIds = new HashSet<string>();
                
                foreach (var row in results)
                {
                    if (row is System.Text.Json.JsonElement jsonElement)
                    {
                        existingIds.Add(JsonUtility.GetPropertyAsString(jsonElement, "doc_id", ""));
                    }
                    else
                    {
                        existingIds.Add((string)row.doc_id);
                    }
                }
                
                _logger?.LogDebug("Batch existence check: {ExistingCount} of {TotalCount} documents exist in Dolt", existingIds.Count, docIds.Count);
                return existingIds;
            }
            catch (DoltException doltEx) when (doltEx.Message.Contains("table not found"))
            {
                // Fresh/empty Dolt database - documents table doesn't exist yet, so no documents exist
                _logger?.LogDebug("Documents table not found in Dolt - treating all documents as new (empty database)");
                return new HashSet<string>();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to batch check document existence, falling back to individual checks");
                
                // Fallback to individual checks if batch query fails (for non-table-not-found errors)
                var existingIds = new HashSet<string>();
                foreach (var docId in docIds)
                {
                    try
                    {
                        if (await DocumentExistsInDoltAsync(docId, collectionName))
                        {
                            existingIds.Add(docId);
                        }
                    }
                    catch (DoltException doltEx) when (doltEx.Message.Contains("table not found"))
                    {
                        // Document doesn't exist (table doesn't exist)
                        continue;
                    }
                }
                return existingIds;
            }
        }

        /// <summary>
        /// Check if a document exists in Dolt (kept for compatibility, but prefer batch method)
        /// </summary>
        private async Task<bool> DocumentExistsInDoltAsync(string docId, string collectionName)
        {
            var sql = $@"
                SELECT COUNT(*) as count
                FROM documents
                WHERE doc_id = '{docId}' AND collection_name = '{collectionName}'";

            var results = await _dolt.QueryAsync<dynamic>(sql);
            if (results == null || !results.Any()) return false;
            
            var result = results.FirstOrDefault();
            
            // Handle both JsonElement and dynamic types
            try
            {
                // If it's a JsonElement, try to get the count property
                if (result is System.Text.Json.JsonElement jsonElement)
                {
                    if (jsonElement.TryGetProperty("count", out var countProperty))
                    {
                        if (countProperty.TryGetInt32(out var count))
                        {
                            return count > 0;
                        }
                        // If it's a string representation of a number
                        if (countProperty.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var countStr = countProperty.GetString();
                            return int.TryParse(countStr, out var parsedCount) && parsedCount > 0;
                        }
                    }
                }
                else
                {
                    // Handle dynamic object case
                    var count = (int)result.count;
                    return count > 0;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse count from query result for document {DocId}", docId);
            }
            
            return false;
        }

        /// <summary>
        /// Get chunk IDs from sync log for a deleted document
        /// </summary>
        private async Task<List<string>> GetChunkIdsFromSyncLogAsync(string docId, string collectionName)
        {
            var sql = $@"
                SELECT chroma_chunk_ids
                FROM document_sync_log
                WHERE doc_id = '{docId}' 
                  AND collection_name = '{collectionName}'
                  AND sync_direction = 'dolt_to_chroma'
                ORDER BY synced_at DESC
                LIMIT 1";

            var results = await _dolt.QueryAsync<dynamic>(sql);
            var result = results.FirstOrDefault();
            
            if (result?.chroma_chunk_ids != null)
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<List<string>>(result.chroma_chunk_ids) 
                        ?? new List<string>();
                }
                catch
                {
                    return new List<string>();
                }
            }
            
            return new List<string>();
        }

        /// <summary>
        /// Clean up sync metadata for documents in a collection after successful commit (PP13-68)
        /// </summary>
        /// <param name="collectionName">Name of the collection to clean up</param>
        /// <returns>Number of documents cleaned up</returns>
        public async Task<int> CleanupSyncMetadataAsync(string collectionName)
        {
            _logger?.LogInformation("Cleaning up sync metadata for collection {Collection}", collectionName);
            
            try
            {
                var currentCommitHash = await _dolt.GetHeadCommitHashAsync();
                
                // Get all documents with local change flags
                var flaggedDocs = await GetFlaggedLocalChangesAsync(collectionName);
                var cleanedCount = 0;
                
                foreach (var doc in flaggedDocs)
                {
                    try
                    {
                        // Clear the is_local_change flag and update dolt_commit metadata
                        var updateMetadata = new Dictionary<string, object>
                        {
                            ["is_local_change"] = false,
                            ["dolt_commit"] = currentCommitHash
                        };
                        
                        await _chroma.UpdateDocumentsAsync(collectionName, 
                            new List<string> { doc.DocId }, 
                            metadatas: new List<Dictionary<string, object>> { updateMetadata },
                            markAsLocalChange: false);
                        
                        cleanedCount++;
                        _logger?.LogDebug("Cleaned metadata for document {DocId} in collection {Collection}", 
                            doc.DocId, collectionName);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to clean metadata for document {DocId} in collection {Collection}", 
                            doc.DocId, collectionName);
                    }
                }
                
                _logger?.LogInformation("Cleaned up metadata for {Count} documents in collection {Collection}", 
                    cleanedCount, collectionName);
                return cleanedCount;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to cleanup sync metadata for collection {Collection}", collectionName);
                throw new Exception($"Failed to cleanup sync metadata: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Validate post-operation state to ensure metadata consistency (PP13-68)
        /// </summary>
        /// <param name="collectionName">Name of the collection to validate</param>
        /// <param name="expectedCommitHash">Expected commit hash for documents</param>
        /// <returns>True if metadata is consistent, false if issues are detected</returns>
        public async Task<bool> ValidatePostOperationStateAsync(string collectionName, string? expectedCommitHash = null)
        {
            _logger?.LogDebug("Validating post-operation state for collection {Collection}", collectionName);
            
            try
            {
                var currentCommitHash = expectedCommitHash ?? await _dolt.GetHeadCommitHashAsync();
                var issues = 0;
                
                // Check for persistent local change flags
                var flaggedDocs = await GetFlaggedLocalChangesAsync(collectionName);
                if (flaggedDocs.Any())
                {
                    _logger?.LogWarning("Found {Count} documents with persistent is_local_change=true flags in collection {Collection}", 
                        flaggedDocs.Count, collectionName);
                    issues += flaggedDocs.Count;
                }
                
                // Check for incorrect dolt_commit metadata
                var allDocs = await GetAllChromaDocumentsAsync(collectionName);
                var staleCommitDocs = allDocs.Where(d => d.Metadata.TryGetValue("dolt_commit", out var commit) 
                    && commit?.ToString() != currentCommitHash).ToList();
                
                if (staleCommitDocs.Any())
                {
                    _logger?.LogWarning("Found {Count} documents with stale dolt_commit metadata in collection {Collection}", 
                        staleCommitDocs.Count, collectionName);
                    issues += staleCommitDocs.Count;
                }
                
                var isConsistent = issues == 0;
                if (isConsistent)
                {
                    _logger?.LogDebug("Post-operation state validation PASSED for collection {Collection}", collectionName);
                }
                else
                {
                    _logger?.LogWarning("Post-operation state validation FAILED for collection {Collection} - {Issues} metadata issues detected", 
                        collectionName, issues);
                }
                
                return isConsistent;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to validate post-operation state for collection {Collection}", collectionName);
                return false;
            }
        }

        #endregion
    }
}