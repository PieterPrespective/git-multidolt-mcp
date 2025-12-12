using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DMMS.Models;

namespace DMMS.Services
{
    /// <summary>
    /// Utility class for converting documents between Dolt database format and ChromaDB format.
    /// Handles content chunking with overlap and maintains deterministic chunk IDs for consistency.
    /// </summary>
    public static class DocumentConverterUtility
    {
        private const int DefaultChunkSize = 512;
        private const int DefaultChunkOverlap = 50;

        /// <summary>
        /// Convert a Dolt document to ChromaDB entries with chunking and metadata
        /// </summary>
        /// <param name="doc">The source document from Dolt database</param>
        /// <param name="currentCommit">The current Dolt commit hash for tracking</param>
        /// <param name="chunkSize">Size of each text chunk in characters</param>
        /// <param name="chunkOverlap">Number of overlapping characters between chunks</param>
        /// <returns>ChromaDB-ready entries with IDs, documents, and metadata</returns>
        public static ChromaEntries ConvertDoltToChroma(
            DoltDocument doc, 
            string currentCommit,
            int chunkSize = DefaultChunkSize,
            int chunkOverlap = DefaultChunkOverlap)
        {
            // 1. Chunk the content with overlap for context preservation
            var chunks = ChunkContent(doc.Content, chunkSize, chunkOverlap);
            
            // 2. Generate deterministic IDs for each chunk
            var ids = chunks.Select((_, i) => $"{doc.SourceId}_chunk_{i}").ToList();
            
            // 3. Build metadata for each chunk (includes back-references and searchable fields)
            var metadatas = chunks.Select((_, i) => BuildChunkMetadata(doc, currentCommit, i, chunks.Count)).ToList();
            
            return new ChromaEntries(ids, chunks, metadatas);
        }

        /// <summary>
        /// Convert a DocumentDelta (from change detection) to ChromaDB entries
        /// </summary>
        /// <param name="delta">The document delta containing change information</param>
        /// <param name="currentCommit">The current Dolt commit hash</param>
        /// <param name="chunkSize">Size of each text chunk</param>
        /// <param name="chunkOverlap">Overlap between chunks</param>
        /// <returns>ChromaDB-ready entries</returns>
        public static ChromaEntries ConvertDeltaToChroma(
            DocumentDelta delta,
            string currentCommit,
            int chunkSize = DefaultChunkSize,
            int chunkOverlap = DefaultChunkOverlap)
        {
            // Parse metadata JSON if it's a string
            var metadata = string.IsNullOrEmpty(delta.Metadata) 
                ? new Dictionary<string, object>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(delta.Metadata) 
                    ?? new Dictionary<string, object>();

            // Create DoltDocument from delta
            var doc = new DoltDocument(
                SourceTable: delta.SourceTable,
                SourceId: delta.SourceId,
                Content: delta.Content,
                ContentHash: delta.ContentHash,
                ProjectId: metadata.GetValueOrDefault("project_id")?.ToString(),
                IssueNumber: TryParseInt(metadata.GetValueOrDefault("issue_number")),
                LogType: metadata.GetValueOrDefault("log_type")?.ToString(),
                Title: metadata.GetValueOrDefault("title")?.ToString(),
                Category: metadata.GetValueOrDefault("category")?.ToString(),
                ToolName: metadata.GetValueOrDefault("tool_name")?.ToString()
            );

            return ConvertDoltToChroma(doc, currentCommit, chunkSize, chunkOverlap);
        }

        /// <summary>
        /// Chunk content with overlap for context preservation in embeddings.
        /// Overlap ensures that context at chunk boundaries is not lost.
        /// </summary>
        /// <param name="content">The content to chunk</param>
        /// <param name="chunkSize">Maximum size of each chunk</param>
        /// <param name="chunkOverlap">Number of characters to overlap between chunks</param>
        /// <returns>List of text chunks</returns>
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
        /// Reconstruct chunk IDs for a document (useful for deletion operations).
        /// Uses deterministic ID generation to ensure consistency across operations.
        /// </summary>
        /// <param name="sourceId">The source document ID</param>
        /// <param name="totalChunks">Total number of chunks for this document</param>
        /// <returns>List of chunk IDs that would be generated for this document</returns>
        public static List<string> GetChunkIds(string sourceId, int totalChunks)
        {
            if (totalChunks <= 0)
                return new List<string>();
                
            return Enumerable.Range(0, totalChunks)
                .Select(i => $"{sourceId}_chunk_{i}")
                .ToList();
        }

        /// <summary>
        /// Calculate SHA-256 hash of content for change detection
        /// </summary>
        /// <param name="content">The content to hash</param>
        /// <returns>Hexadecimal SHA-256 hash string</returns>
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
        /// Build metadata for a single chunk, including both tracking and searchable fields
        /// </summary>
        private static Dictionary<string, object> BuildChunkMetadata(
            DoltDocument doc,
            string currentCommit,
            int chunkIndex,
            int totalChunks)
        {
            var metadata = new Dictionary<string, object>
            {
                // Back-references for sync tracking and validation
                ["source_table"] = doc.SourceTable,
                ["source_id"] = doc.SourceId,
                ["content_hash"] = doc.ContentHash,
                ["dolt_commit"] = currentCommit,
                
                // Chunk positioning information
                ["chunk_index"] = chunkIndex,
                ["total_chunks"] = totalChunks,
                
                // Searchable metadata fields (from source document)
                ["project_id"] = doc.ProjectId ?? "",
                ["issue_number"] = doc.IssueNumber,
                ["log_type"] = doc.LogType ?? "",
                ["title"] = doc.Title ?? "",
                ["category"] = doc.Category ?? "",
                ["tool_name"] = doc.ToolName ?? "",
                ["tool_version"] = doc.ToolVersion ?? ""
            };

            // Add any additional custom metadata if present
            if (doc.CustomMetadata != null)
            {
                foreach (var kvp in doc.CustomMetadata)
                {
                    if (!metadata.ContainsKey(kvp.Key))
                    {
                        metadata[kvp.Key] = kvp.Value ?? "";
                    }
                }
            }

            return metadata;
        }

        /// <summary>
        /// Helper to safely parse integer from object
        /// </summary>
        private static int TryParseInt(object? value)
        {
            if (value == null) return 0;
            
            return value switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                float f => (int)f,
                string s when int.TryParse(s, out var result) => result,
                JsonElement element when element.ValueKind == JsonValueKind.Number => element.GetInt32(),
                _ => 0
            };
        }
    }

    /// <summary>
    /// Data structure representing entries ready to be added to ChromaDB.
    /// Includes parallel lists of IDs, documents (text chunks), and metadata.
    /// </summary>
    public record ChromaEntries(
        List<string> Ids,
        List<string> Documents,
        List<Dictionary<string, object>> Metadatas
    )
    {
        /// <summary>
        /// Validate that all lists have the same length
        /// </summary>
        public bool IsValid => 
            Ids.Count == Documents.Count && 
            Documents.Count == Metadatas.Count;
        
        /// <summary>
        /// Get the number of chunks/entries
        /// </summary>
        public int Count => Ids.Count;
    }
}