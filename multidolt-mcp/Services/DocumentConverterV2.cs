using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Embranch.Models;

namespace Embranch.Services
{
    /// <summary>
    /// V2 Utility class for bidirectional conversion between Dolt and ChromaDB.
    /// Supports generalized schema with JSON metadata preservation.
    /// </summary>
    public static class DocumentConverterUtilityV2
    {
        private const int DefaultChunkSize = 512;
        private const int DefaultChunkOverlap = 50;

        #region Dolt to ChromaDB Conversion

        /// <summary>
        /// Convert a generalized Dolt document to ChromaDB entries with chunking and metadata
        /// </summary>
        /// <param name="doc">The source document from Dolt database (V2 format)</param>
        /// <param name="currentCommit">The current Dolt commit hash for tracking</param>
        /// <param name="chunkSize">Size of each text chunk in characters</param>
        /// <param name="chunkOverlap">Number of overlapping characters between chunks</param>
        /// <returns>ChromaDB-ready entries with IDs, documents, and metadata</returns>
        public static ChromaEntriesV2 ConvertDoltToChroma(
            DoltDocumentV2 doc, 
            string currentCommit,
            int chunkSize = DefaultChunkSize,
            int chunkOverlap = DefaultChunkOverlap)
        {
            // 1. Chunk the content with overlap for context preservation
            var chunks = ChunkContent(doc.Content, chunkSize, chunkOverlap);
            
            // 2. Generate deterministic IDs for each chunk
            var ids = chunks.Select((_, i) => $"{doc.DocId}_chunk_{i}").ToList();
            
            // 3. Build metadata for each chunk (merge user metadata with system fields)
            var metadatas = chunks.Select((_, i) => 
                BuildChunkMetadata(doc, currentCommit, i, chunks.Count)).ToList();
            
            return new ChromaEntriesV2(ids, chunks, metadatas);
        }

        /// <summary>
        /// Convert a DocumentDeltaV2 to ChromaDB entries
        /// </summary>
        public static ChromaEntriesV2 ConvertDeltaToChroma(
            DocumentDeltaV2 delta,
            string currentCommit,
            int chunkSize = DefaultChunkSize,
            int chunkOverlap = DefaultChunkOverlap)
        {
            // Parse metadata from JSON string
            var metadata = delta.GetMetadataDict() ?? new Dictionary<string, object>();

            // Create DoltDocumentV2 from delta
            var doc = new DoltDocumentV2(
                DocId: delta.DocId,
                CollectionName: delta.CollectionName,
                Content: delta.Content,
                ContentHash: delta.ContentHash,
                Title: delta.Title,
                DocType: delta.DocType,
                Metadata: metadata
            );

            return ConvertDoltToChroma(doc, currentCommit, chunkSize, chunkOverlap);
        }

        /// <summary>
        /// Build metadata for a single chunk, preserving ALL user metadata
        /// </summary>
        private static Dictionary<string, object> BuildChunkMetadata(
            DoltDocumentV2 doc,
            string currentCommit,
            int chunkIndex,
            int totalChunks)
        {
            var metadata = new Dictionary<string, object>();

            // First, add ALL user metadata from the document
            if (doc.Metadata != null)
            {
                foreach (var kvp in doc.Metadata)
                {
                    metadata[kvp.Key] = kvp.Value ?? "";
                }
            }

            // Then add/override with system metadata
            metadata["source_id"] = doc.DocId;
            metadata["collection_name"] = doc.CollectionName;
            metadata["content_hash"] = doc.ContentHash;
            metadata["dolt_commit"] = currentCommit;
            
            // Chunk positioning information
            metadata["chunk_index"] = chunkIndex;
            metadata["total_chunks"] = totalChunks;
            
            // Add extracted fields if they exist
            if (!string.IsNullOrEmpty(doc.Title))
                metadata["title"] = doc.Title;
            if (!string.IsNullOrEmpty(doc.DocType))
                metadata["doc_type"] = doc.DocType;

            // Flag to track if this was synced from Dolt (not a local change)
            metadata["is_local_change"] = false;

            return metadata;
        }

        #endregion

        #region ChromaDB to Dolt Conversion (NEW)

        /// <summary>
        /// Convert ChromaDB chunks back to a Dolt document (reassemble chunks)
        /// </summary>
        /// <param name="chunks">List of chunks from ChromaDB query</param>
        /// <returns>Reassembled DoltDocumentV2</returns>
        public static DoltDocumentV2? ConvertChromaToDolt(List<ChromaChunk> chunks)
        {
            if (chunks == null || chunks.Count == 0)
                return null;

            // Sort chunks by index to reassemble in correct order
            var sortedChunks = chunks
                .OrderBy(c => GetChunkIndex(c.Metadata))
                .ToList();

            // Reassemble content from chunks (remove overlaps)
            var content = ReassembleContent(sortedChunks);

            // Get metadata from first chunk (all chunks should have same document metadata)
            var firstChunkMeta = sortedChunks[0].Metadata;

            // Separate system fields from user metadata
            var (systemFields, userMetadata) = SeparateMetadata(firstChunkMeta);

            // Create DoltDocumentV2
            return new DoltDocumentV2(
                DocId: systemFields.DocId,
                CollectionName: systemFields.CollectionName,
                Content: content,
                ContentHash: CalculateContentHash(content),
                Title: systemFields.Title,
                DocType: systemFields.DocType,
                Metadata: userMetadata
            );
        }

        /// <summary>
        /// Convert a ChromaDocument to DoltDocumentV2
        /// </summary>
        public static DoltDocumentV2 ConvertChromaToDolt(ChromaDocument chromaDoc)
        {
            // Separate system fields from user metadata
            var userMetadata = new Dictionary<string, object>(chromaDoc.Metadata);
            
            // Remove system fields from user metadata
            userMetadata.Remove("source_id");
            userMetadata.Remove("collection_name");
            userMetadata.Remove("content_hash");
            userMetadata.Remove("dolt_commit");
            userMetadata.Remove("chunk_index");
            userMetadata.Remove("total_chunks");
            userMetadata.Remove("is_local_change");

            return new DoltDocumentV2(
                DocId: chromaDoc.DocId,
                CollectionName: chromaDoc.CollectionName,
                Content: chromaDoc.Content,
                ContentHash: chromaDoc.ContentHash,
                Title: chromaDoc.GetTitle(),
                DocType: chromaDoc.GetDocType(),
                Metadata: userMetadata
            );
        }

        /// <summary>
        /// Reassemble content from chunks, handling overlaps
        /// </summary>
        private static string ReassembleContent(List<ChromaChunk> sortedChunks)
        {
            if (sortedChunks.Count == 0)
                return "";

            if (sortedChunks.Count == 1)
                return sortedChunks[0].Document;

            var sb = new StringBuilder();
            sb.Append(sortedChunks[0].Document);

            for (int i = 1; i < sortedChunks.Count; i++)
            {
                var currentChunk = sortedChunks[i].Document;
                var previousChunk = sortedChunks[i - 1].Document;
                
                // Find and remove overlap
                var overlapStart = FindOverlap(previousChunk, currentChunk, DefaultChunkOverlap);
                if (overlapStart > 0)
                {
                    sb.Append(currentChunk.Substring(overlapStart));
                }
                else
                {
                    // No overlap found, append entire chunk
                    sb.Append(currentChunk);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Find the overlap between end of previous chunk and beginning of current chunk
        /// </summary>
        private static int FindOverlap(string previous, string current, int maxOverlap)
        {
            var minLength = Math.Min(Math.Min(previous.Length, current.Length), maxOverlap);
            
            for (int overlap = minLength; overlap > 0; overlap--)
            {
                if (previous.EndsWith(current.Substring(0, overlap)))
                {
                    return overlap;
                }
            }
            
            return 0;
        }

        /// <summary>
        /// Separate system fields from user metadata
        /// </summary>
        private static (SystemFields system, Dictionary<string, object> user) SeparateMetadata(
            Dictionary<string, object> metadata)
        {
            var systemFields = new SystemFields
            {
                DocId = ExtractAndRemove(metadata, "source_id") ?? "",
                CollectionName = ExtractAndRemove(metadata, "collection_name") ?? "",
                Title = ExtractAndRemove(metadata, "title"),
                DocType = ExtractAndRemove(metadata, "doc_type")
            };

            // Remove other system fields
            metadata.Remove("content_hash");
            metadata.Remove("dolt_commit");
            metadata.Remove("chunk_index");
            metadata.Remove("total_chunks");
            metadata.Remove("is_local_change");

            return (systemFields, metadata);
        }

        /// <summary>
        /// Extract a value from dictionary and remove the key
        /// </summary>
        private static string? ExtractAndRemove(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out var value))
            {
                dict.Remove(key);
                return value?.ToString();
            }
            return null;
        }

        /// <summary>
        /// Get chunk index from metadata
        /// </summary>
        private static int GetChunkIndex(Dictionary<string, object> metadata)
        {
            if (metadata.TryGetValue("chunk_index", out var value))
            {
                return value switch
                {
                    int i => i,
                    long l => (int)l,
                    double d => (int)d,
                    string s when int.TryParse(s, out var result) => result,
                    JsonElement element when element.ValueKind == JsonValueKind.Number => element.GetInt32(),
                    _ => 0
                };
            }
            return 0;
        }

        #endregion

        #region Common Utilities

        /// <summary>
        /// Chunk content with overlap for context preservation in embeddings
        /// </summary>
        public static List<string> ChunkContent(string content, int chunkSize = DefaultChunkSize, int chunkOverlap = DefaultChunkOverlap)
        {
            if (string.IsNullOrEmpty(content))
                return new List<string> { "" };
            
            // Validate parameters
            if (chunkSize <= 0)
                throw new ArgumentException("Chunk size must be positive", nameof(chunkSize));
            if (chunkOverlap < 0)
                throw new ArgumentException("Chunk overlap cannot be negative", nameof(chunkOverlap));
            if (chunkOverlap >= chunkSize)
                throw new ArgumentException("Chunk overlap must be less than chunk size", nameof(chunkOverlap));
                
            var chunks = new List<string>();
            var start = 0;
            
            while (start < content.Length)
            {
                var length = Math.Min(chunkSize, content.Length - start);
                chunks.Add(content.Substring(start, length));
                
                // Move forward by (chunkSize - overlap) to create overlapping chunks
                start += chunkSize - chunkOverlap;
                
                // Prevent infinite loop on small content
                if (start <= 0 && chunks.Count > 0) break;
            }
            
            return chunks;
        }

        /// <summary>
        /// Calculate SHA-256 hash of content for change detection
        /// </summary>
        public static string CalculateContentHash(string content)
        {
            if (string.IsNullOrEmpty(content))
                return string.Empty;
                
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(content);
            var hash = sha256.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Get chunk IDs for a document (useful for deletion operations)
        /// </summary>
        public static List<string> GetChunkIds(string docId, int totalChunks)
        {
            if (totalChunks <= 0)
                return new List<string>();
                
            return Enumerable.Range(0, totalChunks)
                .Select(i => $"{docId}_chunk_{i}")
                .ToList();
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Temporary structure for system fields extraction
        /// </summary>
        private class SystemFields
        {
            public string DocId { get; set; } = "";
            public string CollectionName { get; set; } = "";
            public string? Title { get; set; }
            public string? DocType { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// V2 Data structure for ChromaDB entries with bidirectional support
    /// </summary>
    public record ChromaEntriesV2(
        List<string> Ids,
        List<string> Documents,
        List<Dictionary<string, object>> Metadatas
    )
    {
        public bool IsValid => 
            Ids.Count == Documents.Count && 
            Documents.Count == Metadatas.Count;
        
        public int Count => Ids.Count;
    }

    /// <summary>
    /// Represents a single chunk from ChromaDB
    /// </summary>
    public record ChromaChunk(
        string Id,
        string Document,
        Dictionary<string, object> Metadata,
        float? Distance = null
    );
}