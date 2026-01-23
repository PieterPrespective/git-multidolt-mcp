using System.Collections.Generic;
using System.Threading.Tasks;

namespace Embranch.Services
{
    /// <summary>
    /// Service for resolving document IDs between base IDs and chunk IDs in ChromaDB.
    /// Handles the mismatch between how documents are stored (as chunks) and how they are referenced (by base ID).
    /// </summary>
    public interface IDocumentIdResolver
    {
        /// <summary>
        /// Expands a base document ID to all its chunk IDs in the specified collection.
        /// For example: "doc1" -> ["doc1_chunk_0", "doc1_chunk_1", "doc1_chunk_2"]
        /// </summary>
        /// <param name="collectionName">The collection to search in</param>
        /// <param name="documentId">The base document ID or chunk ID</param>
        /// <returns>List of all chunk IDs for the document</returns>
        Task<List<string>> ExpandToChunkIdsAsync(string collectionName, string documentId);
        
        /// <summary>
        /// Extracts the base document ID from a chunk ID.
        /// For example: "doc1_chunk_0" -> "doc1"
        /// </summary>
        /// <param name="chunkId">The chunk ID</param>
        /// <returns>The base document ID</returns>
        string ExtractBaseDocumentId(string chunkId);
        
        /// <summary>
        /// Checks if the given ID is a chunk ID (contains _chunk_ pattern).
        /// </summary>
        /// <param name="id">The ID to check</param>
        /// <returns>True if it's a chunk ID, false if it's a base ID</returns>
        bool IsChunkId(string id);
        
        /// <summary>
        /// Expands multiple base document IDs to all their chunk IDs.
        /// Efficiently handles batch operations.
        /// </summary>
        /// <param name="collectionName">The collection to search in</param>
        /// <param name="documentIds">List of base document IDs or chunk IDs</param>
        /// <returns>List of all chunk IDs for all documents</returns>
        Task<List<string>> ExpandMultipleToChunkIdsAsync(string collectionName, List<string> documentIds);
        
        /// <summary>
        /// Gets all base document IDs from a list of mixed IDs (base and chunk IDs).
        /// Removes duplicates and returns unique base IDs.
        /// </summary>
        /// <param name="ids">Mixed list of base and chunk IDs</param>
        /// <returns>Unique list of base document IDs</returns>
        List<string> ExtractUniqueBaseDocumentIds(List<string> ids);
    }
}