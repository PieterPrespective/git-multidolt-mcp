using Embranch.Models;

namespace Embranch.Services
{
    /// <summary>
    /// Interface for reading from external ChromaDB databases (read-only access).
    /// Provides methods to validate, list collections, and retrieve documents from
    /// an external ChromaDB database without modifying it.
    /// </summary>
    public interface IExternalChromaDbReader
    {
        /// <summary>
        /// Validates that the specified path contains a valid ChromaDB database.
        /// Checks for existence of chroma.sqlite3 and database accessibility.
        /// </summary>
        /// <param name="dbPath">Path to the external ChromaDB database folder</param>
        /// <returns>Validation result with database statistics if valid</returns>
        Task<ExternalDbValidationResult> ValidateExternalDbAsync(string dbPath);

        /// <summary>
        /// Lists all collections in the external database.
        /// </summary>
        /// <param name="dbPath">Path to the external ChromaDB database folder</param>
        /// <returns>List of collection information including name and document count</returns>
        Task<List<ExternalCollectionInfo>> ListExternalCollectionsAsync(string dbPath);

        /// <summary>
        /// Lists collection names that match a wildcard pattern.
        /// </summary>
        /// <param name="dbPath">Path to the external ChromaDB database folder</param>
        /// <param name="pattern">Wildcard pattern to match (e.g., "project_*", "*_docs")</param>
        /// <returns>List of collection names matching the pattern</returns>
        Task<List<string>> ListMatchingCollectionsAsync(string dbPath, string pattern);

        /// <summary>
        /// Gets documents from an external collection with optional ID pattern filtering.
        /// </summary>
        /// <param name="dbPath">Path to the external ChromaDB database folder</param>
        /// <param name="collectionName">Name of the collection to read from</param>
        /// <param name="documentIdPatterns">Optional list of document ID patterns to filter (supports wildcards)</param>
        /// <returns>List of documents with content, metadata, and computed hashes</returns>
        Task<List<ExternalDocument>> GetExternalDocumentsAsync(
            string dbPath,
            string collectionName,
            List<string>? documentIdPatterns = null);

        /// <summary>
        /// Gets collection metadata from external database.
        /// </summary>
        /// <param name="dbPath">Path to the external ChromaDB database folder</param>
        /// <param name="collectionName">Name of the collection</param>
        /// <returns>Collection metadata dictionary, or null if collection doesn't exist</returns>
        Task<Dictionary<string, object>?> GetExternalCollectionMetadataAsync(
            string dbPath,
            string collectionName);

        /// <summary>
        /// Gets the count of documents in an external collection.
        /// </summary>
        /// <param name="dbPath">Path to the external ChromaDB database folder</param>
        /// <param name="collectionName">Name of the collection</param>
        /// <returns>Number of documents in the collection</returns>
        Task<int> GetExternalCollectionCountAsync(string dbPath, string collectionName);

        /// <summary>
        /// Checks if a collection exists in the external database.
        /// </summary>
        /// <param name="dbPath">Path to the external ChromaDB database folder</param>
        /// <param name="collectionName">Name of the collection to check</param>
        /// <returns>True if the collection exists</returns>
        Task<bool> CollectionExistsAsync(string dbPath, string collectionName);
    }
}
