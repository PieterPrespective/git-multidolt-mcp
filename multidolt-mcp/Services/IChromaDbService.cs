namespace DMMS.Services;

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
    Task<bool> AddDocumentsAsync(string collectionName, List<string> documents, List<string> ids, List<Dictionary<string, object>>? metadatas = null);

    /// <summary>
    /// Queries documents in a collection
    /// </summary>
    Task<object?> QueryDocumentsAsync(string collectionName, List<string> queryTexts, int nResults = 5, 
        Dictionary<string, object>? where = null, Dictionary<string, object>? whereDocument = null);

    /// <summary>
    /// Gets documents from a collection
    /// </summary>
    Task<object?> GetDocumentsAsync(string collectionName, List<string>? ids = null, 
        Dictionary<string, object>? where = null, int? limit = null);

    /// <summary>
    /// Updates documents in a collection
    /// </summary>
    Task<bool> UpdateDocumentsAsync(string collectionName, List<string> ids, 
        List<string>? documents = null, List<Dictionary<string, object>>? metadatas = null);

    /// <summary>
    /// Deletes documents from a collection
    /// </summary>
    Task<bool> DeleteDocumentsAsync(string collectionName, List<string> ids);

    /// <summary>
    /// Gets the count of documents in a collection
    /// </summary>
    Task<int> GetCollectionCountAsync(string collectionName);
}