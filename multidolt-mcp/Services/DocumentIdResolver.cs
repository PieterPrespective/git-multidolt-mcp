using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Embranch.Services
{
    /// <summary>
    /// Implementation of document ID resolution for chunk-aware operations.
    /// Handles conversion between base document IDs and chunk IDs transparently.
    /// </summary>
    public class DocumentIdResolver : IDocumentIdResolver
    {
        private readonly IChromaDbService _chromaService;
        private readonly ILogger<DocumentIdResolver> _logger;
        
        // Regex pattern to identify and extract from chunk IDs
        private static readonly Regex ChunkIdPattern = new Regex(@"^(.+)_chunk_(\d+)$", RegexOptions.Compiled);
        
        // Cache for chunk ID mappings (collection -> base doc ID -> chunk IDs)
        private readonly Dictionary<string, Dictionary<string, List<string>>> _chunkIdCache = new();
        private readonly object _cacheLock = new object();

        /// <summary>
        /// Initializes a new instance of DocumentIdResolver
        /// </summary>
        public DocumentIdResolver(IChromaDbService chromaService, ILogger<DocumentIdResolver> logger)
        {
            _chromaService = chromaService ?? throw new ArgumentNullException(nameof(chromaService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>
        public async Task<List<string>> ExpandToChunkIdsAsync(string collectionName, string documentId)
        {
            // If it's already a chunk ID, return it as-is
            if (IsChunkId(documentId))
            {
                return new List<string> { documentId };
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_chunkIdCache.TryGetValue(collectionName, out var collectionCache) &&
                    collectionCache.TryGetValue(documentId, out var cachedChunkIds))
                {
                    _logger.LogDebug("Cache hit for document {DocumentId} in collection {Collection}", 
                        documentId, collectionName);
                    return new List<string>(cachedChunkIds);
                }
            }

            // Query ChromaDB for all chunks of this document
            var chunkIds = await QueryChunkIdsForDocumentAsync(collectionName, documentId);
            
            // Update cache
            lock (_cacheLock)
            {
                if (!_chunkIdCache.ContainsKey(collectionName))
                {
                    _chunkIdCache[collectionName] = new Dictionary<string, List<string>>();
                }
                _chunkIdCache[collectionName][documentId] = new List<string>(chunkIds);
            }

            _logger.LogInformation("Expanded document {DocumentId} to {ChunkCount} chunks in collection {Collection}", 
                documentId, chunkIds.Count, collectionName);
            
            return chunkIds;
        }

        /// <inheritdoc/>
        public string ExtractBaseDocumentId(string chunkId)
        {
            var match = ChunkIdPattern.Match(chunkId);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            // If not a chunk ID, return as-is (it's already a base ID)
            return chunkId;
        }

        /// <inheritdoc/>
        public bool IsChunkId(string id)
        {
            return ChunkIdPattern.IsMatch(id);
        }

        /// <inheritdoc/>
        public async Task<List<string>> ExpandMultipleToChunkIdsAsync(string collectionName, List<string> documentIds)
        {
            if (documentIds == null || documentIds.Count == 0)
            {
                return new List<string>();
            }

            var allChunkIds = new HashSet<string>();
            
            // Separate chunk IDs from base IDs
            var chunkIds = documentIds.Where(IsChunkId).ToList();
            var baseIds = documentIds.Where(id => !IsChunkId(id)).ToList();
            
            // Add chunk IDs directly
            foreach (var chunkId in chunkIds)
            {
                allChunkIds.Add(chunkId);
            }
            
            // Expand base IDs to chunks
            foreach (var baseId in baseIds)
            {
                var chunks = await ExpandToChunkIdsAsync(collectionName, baseId);
                foreach (var chunk in chunks)
                {
                    allChunkIds.Add(chunk);
                }
            }

            var result = allChunkIds.ToList();
            _logger.LogInformation("Expanded {InputCount} IDs to {ChunkCount} chunk IDs in collection {Collection}", 
                documentIds.Count, result.Count, collectionName);
            
            return result;
        }

        /// <inheritdoc/>
        public List<string> ExtractUniqueBaseDocumentIds(List<string> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return new List<string>();
            }

            var baseIds = new HashSet<string>();
            
            foreach (var id in ids)
            {
                baseIds.Add(ExtractBaseDocumentId(id));
            }
            
            return baseIds.ToList();
        }

        /// <summary>
        /// Queries ChromaDB for all chunk IDs belonging to a base document
        /// </summary>
        private async Task<List<string>> QueryChunkIdsForDocumentAsync(string collectionName, string baseDocumentId)
        {
            try
            {
                // Query using metadata if possible (most efficient)
                var where = new Dictionary<string, object>
                {
                    ["source_id"] = baseDocumentId  // DocumentConverterV2 stores base ID in source_id metadata
                };
                
                var result = await _chromaService.GetDocumentsAsync(collectionName, null, where, null);
                
                if (result is Dictionary<string, object> dict && 
                    dict.TryGetValue("ids", out var idsObj) && 
                    idsObj is List<object> idList)
                {
                    var chunkIds = idList.Select(id => id?.ToString() ?? "").Where(id => !string.IsNullOrEmpty(id)).ToList();
                    
                    if (chunkIds.Count > 0)
                    {
                        return chunkIds;
                    }
                }
                
                // Fallback: Query all documents and filter client-side
                _logger.LogWarning("Metadata query failed for document {DocumentId}, falling back to pattern matching", 
                    baseDocumentId);
                
                return await QueryChunkIdsByPatternAsync(collectionName, baseDocumentId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query chunk IDs for document {DocumentId} in collection {Collection}", 
                    baseDocumentId, collectionName);
                
                // Last resort: assume single chunk with _chunk_0
                return new List<string> { $"{baseDocumentId}_chunk_0" };
            }
        }

        /// <summary>
        /// Fallback method to query chunk IDs by pattern matching
        /// </summary>
        private async Task<List<string>> QueryChunkIdsByPatternAsync(string collectionName, string baseDocumentId)
        {
            // Get all documents in collection (limited query for performance)
            var result = await _chromaService.GetDocumentsAsync(collectionName, null, null, 10000);
            
            if (result is Dictionary<string, object> dict && 
                dict.TryGetValue("ids", out var idsObj) && 
                idsObj is List<object> idList)
            {
                var pattern = $"^{Regex.Escape(baseDocumentId)}_chunk_\\d+$";
                var regex = new Regex(pattern);
                
                var chunkIds = idList
                    .Select(id => id?.ToString() ?? "")
                    .Where(id => !string.IsNullOrEmpty(id) && regex.IsMatch(id))
                    .ToList();
                
                if (chunkIds.Count > 0)
                {
                    return chunkIds;
                }
            }
            
            // If no chunks found, return empty list (document doesn't exist)
            _logger.LogWarning("No chunks found for document {DocumentId} in collection {Collection}", 
                baseDocumentId, collectionName);
            return new List<string>();
        }

        /// <summary>
        /// Clears the chunk ID cache for a specific collection or all collections
        /// </summary>
        public void ClearCache(string? collectionName = null)
        {
            lock (_cacheLock)
            {
                if (collectionName != null)
                {
                    _chunkIdCache.Remove(collectionName);
                    _logger.LogDebug("Cleared cache for collection {Collection}", collectionName);
                }
                else
                {
                    _chunkIdCache.Clear();
                    _logger.LogDebug("Cleared entire chunk ID cache");
                }
            }
        }
    }
}