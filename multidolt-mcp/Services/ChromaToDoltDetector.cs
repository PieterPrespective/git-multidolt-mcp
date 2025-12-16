using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DMMS.Models;
using Microsoft.Extensions.Logging;

namespace DMMS.Services
{
    /// <summary>
    /// Detects changes in ChromaDB that need to be staged to Dolt.
    /// Implements the "working copy" detection logic for bidirectional sync.
    /// </summary>
    public class ChromaToDoltDetector
    {
        private readonly IChromaDbService _chroma;
        private readonly IDoltCli _dolt;
        private readonly ILogger<ChromaToDoltDetector>? _logger;

        public ChromaToDoltDetector(
            IChromaDbService chroma,
            IDoltCli dolt,
            ILogger<ChromaToDoltDetector>? logger = null)
        {
            _chroma = chroma;
            _dolt = dolt;
            _logger = logger;
        }

        /// <summary>
        /// Detect all local changes in a ChromaDB collection that haven't been staged to Dolt
        /// </summary>
        /// <param name="collectionName">The ChromaDB collection to check</param>
        /// <returns>Summary of new, modified, and deleted documents</returns>
        public async Task<LocalChanges> DetectLocalChangesAsync(string collectionName)
        {
            _logger?.LogDebug("Detecting local changes in ChromaDB collection {Collection}", collectionName);

            try
            {
                // Get documents flagged as local changes
                var flaggedChanges = await GetFlaggedLocalChangesAsync(collectionName);
                _logger?.LogInformation("GetFlaggedLocalChangesAsync returned {Count} flagged changes", flaggedChanges.Count);
                
                // FALLBACK: If no flagged changes found, but we suspect there should be changes,
                // compare all ChromaDB documents with Dolt to find new documents
                if (flaggedChanges.Count == 0)
                {
                    _logger?.LogInformation("No flagged local changes found, checking for new documents in ChromaDB");
                    var fallbackDocs = await FindChromaOnlyDocumentsAsync(collectionName);
                    _logger?.LogInformation("FindChromaOnlyDocumentsAsync returned {Count} ChromaDB-only documents", fallbackDocs.Count);
                    if (fallbackDocs.Count > 0)
                    {
                        _logger?.LogInformation("Found {Count} documents in ChromaDB that don't exist in Dolt - treating as local changes", fallbackDocs.Count);
                        flaggedChanges = fallbackDocs;
                    }
                    else
                    {
                        _logger?.LogInformation("No ChromaDB-only documents found either");
                    }
                }
                else
                {
                    _logger?.LogInformation("Using {Count} flagged changes found by GetFlaggedLocalChangesAsync", flaggedChanges.Count);
                }
                
                // Compare content hashes to detect modifications
                var hashMismatches = await CompareContentHashesAsync(collectionName);
                
                // Find documents that exist only in ChromaDB (excluding fallback docs already processed)
                var chromaOnlyDocsFromComparison = await FindChromaOnlyDocumentsAsync(collectionName);
                
                // Find documents deleted from ChromaDB but still in Dolt
                var deletedDocs = await FindDeletedDocumentsAsync(collectionName);

                // Combine and deduplicate results
                var newDocuments = new List<ChromaDocument>();
                var modifiedDocuments = new List<ChromaDocument>();
                
                // Documents flagged as local changes (includes fallback documents)
                foreach (var doc in flaggedChanges)
                {
                    if (!await DocumentExistsInDoltAsync(doc.DocId, collectionName))
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
                
                // Documents only in ChromaDB (new) - exclude those already found via fallback
                foreach (var doc in chromaOnlyDocsFromComparison)
                {
                    if (!newDocuments.Any(n => n.DocId == doc.DocId))
                    {
                        newDocuments.Add(doc);
                    }
                }

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
            _logger?.LogDebug("Finding ChromaDB-only documents in collection {Collection}", collectionName);

            try
            {
                // Get all documents from ChromaDB
                var chromaDocs = await GetAllChromaDocumentsAsync(collectionName);
                
                // Get all document IDs from Dolt
                var doltIds = await GetDoltDocumentIdsAsync(collectionName);
                
                // Find documents that don't exist in Dolt
                var chromaOnly = chromaDocs
                    .Where(c => !doltIds.Contains(c.DocId))
                    .ToList();
                
                _logger?.LogDebug("Found {Count} documents only in ChromaDB", chromaOnly.Count);
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
                // Get all document IDs from Dolt
                var doltDocs = await GetDoltDocumentsAsync(collectionName);
                
                // Get all document IDs from ChromaDB
                var chromaIds = await GetChromaDocumentIdsAsync(collectionName);
                
                // Find documents in Dolt but not in ChromaDB
                var deleted = new List<DeletedDocumentV2>();
                
                foreach (var doltDoc in doltDocs)
                {
                    if (!chromaIds.Contains(doltDoc.DocId))
                    {
                        // Get chunk IDs from sync log for cleanup
                        var chunkIds = await GetChunkIdsFromSyncLogAsync(doltDoc.DocId, collectionName);
                        
                        deleted.Add(new DeletedDocumentV2
                        {
                            DocId = doltDoc.DocId,
                            CollectionName = collectionName,
                            ChunkIds = System.Text.Json.JsonSerializer.Serialize(chunkIds)
                        });
                    }
                }
                
                _logger?.LogDebug("Found {Count} deleted documents", deleted.Count);
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

            var results = await _dolt.QueryAsync<dynamic>(sql);
            var hashes = new Dictionary<string, string>();
            
            foreach (var row in results)
            {
                hashes[row.doc_id] = row.content_hash;
            }
            
            return hashes;
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

            var results = await _dolt.QueryAsync<dynamic>(sql);
            return results.Select(r => ((string)r.doc_id, (string)r.content_hash)).ToList();
        }

        /// <summary>
        /// Check if a document exists in Dolt
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

        #endregion
    }
}