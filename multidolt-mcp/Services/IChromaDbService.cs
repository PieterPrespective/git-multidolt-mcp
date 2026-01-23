namespace Embranch.Services;

/// <summary>
/// Interface for ChromaDB service operations
/// </summary>
public interface IChromaDbService
{
    /// <summary>
    /// Lists all collections in ChromaDB
    /// </summary>
    Task<List<string>> ListCollectionsAsync(int? limit = null, int? offset = null);

    /// <summary>
    /// Creates a new collection
    /// </summary>
    Task<bool> CreateCollectionAsync(string name, Dictionary<string, object>? metadata = null);

    /// <summary>
    /// Gets information about a collection
    /// </summary>
    Task<object?> GetCollectionAsync(string name);

    /// <summary>
    /// Deletes a collection
    /// </summary>
    Task<bool> DeleteCollectionAsync(string name);

    /// <summary>
    /// Adds documents to a collection
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="documents">List of document content</param>
    /// <param name="ids">List of document IDs</param>
    /// <param name="metadatas">Optional metadata for documents</param>
    /// <param name="allowDuplicateIds">Whether to allow duplicate IDs</param>
    /// <param name="markAsLocalChange">Whether to mark documents as local changes (default: true for user-initiated additions, false for sync operations)</param>
    Task<bool> AddDocumentsAsync(string collectionName, List<string> documents, List<string> ids, List<Dictionary<string, object>>? metadatas = null, bool allowDuplicateIds = false, bool markAsLocalChange = true);

    /// <summary>
    /// Queries documents in a collection
    /// </summary>
    Task<object?> QueryDocumentsAsync(string collectionName, List<string> queryTexts, int nResults = 5, 
        Dictionary<string, object>? where = null, Dictionary<string, object>? whereDocument = null);

    /// <summary>
    /// Gets documents from a collection
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="ids">Optional list of document IDs to retrieve</param>
    /// <param name="where">Optional metadata filter</param>
    /// <param name="limit">Optional limit on number of documents to return</param>
    /// <param name="inclEmbeddings">Whether to include embeddings in the response (default: false for backward compatibility)</param>
    Task<object?> GetDocumentsAsync(string collectionName, List<string>? ids = null,
        Dictionary<string, object>? where = null, int? limit = null, bool inclEmbeddings = false);

    /// <summary>
    /// Updates documents in a collection
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="ids">List of document IDs to update</param>
    /// <param name="documents">Optional new document content</param>
    /// <param name="metadatas">Optional new metadata for documents</param>
    /// <param name="markAsLocalChange">Whether to mark documents as local changes (default: true for user-initiated updates, false for sync operations)</param>
    /// <param name="expandChunks">Whether to expand base document IDs to all their chunks (default: true)</param>
    Task<bool> UpdateDocumentsAsync(string collectionName, List<string> ids, 
        List<string>? documents = null, List<Dictionary<string, object>>? metadatas = null, bool markAsLocalChange = true, bool expandChunks = true);

    /// <summary>
    /// Deletes documents from a collection
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="ids">List of document IDs to delete</param>
    /// <param name="expandChunks">Whether to expand base document IDs to all their chunks (default: true)</param>
    Task<bool> DeleteDocumentsAsync(string collectionName, List<string> ids, bool expandChunks = true);

    /// <summary>
    /// Gets the count of documents in a collection
    /// </summary>
    Task<int> GetCollectionCountAsync(string collectionName);

    /// <summary>
    /// Gets the count of documents in a collection (alias for GetCollectionCountAsync for consistency)
    /// </summary>
    Task<int> GetDocumentCountAsync(string collectionName);
}