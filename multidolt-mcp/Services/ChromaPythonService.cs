using DMMS.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Python.Runtime;
using System.Collections;
using System.Text.Json;
using System.Xml.Linq;

namespace DMMS.Services;

/// <summary>
/// ChromaDB service using Python.NET to interact with the chromadb Python library
/// This provides a robust implementation that directly uses the official Python library
/// </summary>
public class ChromaPythonService : IChromaDbService, IDisposable
{
    private readonly ILogger<ChromaPythonService> _logger;
    private readonly ServerConfiguration _configuration;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _clientId;
    private readonly string _configurationString;
    private bool _disposed = false;
    private bool _clientInitialized = false;
    private readonly object _initLock = new object();
    private readonly IDocumentIdResolver? _idResolver;

    /// <summary>
    /// Initializes a new instance of ChromaPythonService
    /// </summary>
    public ChromaPythonService(ILogger<ChromaPythonService> logger, IOptions<ServerConfiguration> configuration, IDocumentIdResolver? idResolver = null)
    {
        _logger = logger;
        _configuration = configuration.Value;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        // Generate unique client ID and configuration string
        _clientId = GenerateClientId();
        _configurationString = GenerateConfigurationString();
        
        // Set up the client pool logger
        ChromaClientPool.SetLogger(_logger);

        // Initialize ID resolver (will create internal instance if not provided)
        _idResolver = idResolver;

        // Client initialization is deferred until first use
        _logger.LogInformation("Created ChromaPythonService with client ID: {ClientId}", _clientId);
    }

    /// <summary>
    /// Creates a temporary DocumentIdResolver for fallback scenarios
    /// </summary>
    private DocumentIdResolver CreateTemporaryResolver()
    {
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
        var tempLogger = loggerFactory.CreateLogger<DocumentIdResolver>();
        return new DocumentIdResolver(this, tempLogger);
    }

    /// <summary>
    /// Ensures the ChromaDB client is initialized
    /// </summary>
    private async Task EnsureClientInitializedAsync()
    {
        if (_clientInitialized)
            return;

        // Use a lock-free approach with double-check pattern
        if (!_clientInitialized)
        {
            lock (_initLock)
            {
                if (!_clientInitialized)
                {
                    // Initialize client on the Python thread
                    var initTask = InitializeChromaClientAsync();
                    initTask.Wait(); // We must wait synchronously within the lock
                    _clientInitialized = true;
                }
            }
        }
    }

    /// <summary>
    /// Initializes the ChromaDB client asynchronously on the Python thread
    /// </summary>
    private async Task<bool> InitializeChromaClientAsync()
    {
        // For PersistentClient, check compatibility first
        if (!string.IsNullOrEmpty(_configuration.ChromaDataPath))
        {
            string dataPath = Path.GetFullPath(_configuration.ChromaDataPath);
            Directory.CreateDirectory(dataPath);
            
            // Check for ChromaDB version compatibility issues and migrate if needed
            _logger.LogInformation("Checking ChromaDB compatibility for pre-existing database");
            if (!await ChromaCompatibilityHelper.EnsureCompatibilityAsync(_logger, dataPath))
            {
                _logger.LogWarning("ChromaDB compatibility check failed - proceeding with initialization anyway");
            }
        }
        
        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                // Create client using the new client pool
                dynamic client = ChromaClientPool.GetOrCreateClient(_clientId, _configurationString);
                
                _logger.LogInformation("Initialized ChromaDB client {ClientId} with config: {Config}", _clientId, _configurationString);

                // For backward compatibility, set this as the default client
                ChromaDbReferences.SetDefaultClientId(_clientId);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ChromaDB client");
                throw new InvalidOperationException("Failed to initialize ChromaDB client", ex);
            }
        }, timeoutMs: 30000, operationName: "InitializeChromaClient");
    }

    /// <summary>
    /// Lists all collections in ChromaDB
    /// </summary>
    public async Task<List<string>> ListCollectionsAsync(int? limit = null, int? offset = null)
    {
        await EnsureClientInitializedAsync();
        
        _logger.LogInformation($"[ChromaPythonService.ListCollectionsAsync] Attempting to list # collections with limit { ((limit.HasValue) ? limit.Value.ToString() : "Null") }, offset: {((offset.HasValue) ? offset.Value.ToString() : "Null")}");

        return await PythonContext.ExecuteAsync(() =>
        {
            _logger.LogInformation($"[ChromaPythonService.ListCollectionsAsync] executing on Python thread");
            
            try
            {
                dynamic client = ChromaClientPool.GetClient(_clientId);
                dynamic collections = client.list_collections();
                var result = new List<string>();
                
                foreach (dynamic collection in collections)
                {
                    result.Add(collection.ToString());
                }

                if (offset.HasValue)
                    result = result.Skip(offset.Value).ToList();
                if (limit.HasValue)
                    result = result.Take(limit.Value).ToList();

                _logger.LogInformation($"[ChromaPythonService.ListCollectionsAsync] returning result");
                return result;
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to list collections - Python error: {ex.Message}");
                return new List<string>(); // Return empty list on error
            }
        }, timeoutMs: 30000, operationName: "ListCollections");
    }

    /// <summary>
    /// Creates a new collection in ChromaDB
    /// </summary>
    public async Task<bool> CreateCollectionAsync(string name, Dictionary<string, object>? metadata = null)
    {
        await EnsureClientInitializedAsync();
        
        _logger.LogInformation($"Attempting to create collection '{name}'; python context is running: {PythonContext.IsInitialized}");
        
        
        // Apply retry logic with exponential backoff for CreateCollection operations
        // This addresses Python.NET deadlock issues similar to those fixed in PP13-52-C2
        const int maxRetries = 3;
        int[] retryDelays = { 100, 500, 1000 }; // Exponential backoff delays in ms

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // Use shorter timeout for each attempt to detect deadlock quickly
                int attemptTimeout = attempt == maxRetries - 1 ? 15000 : 5000; // Last attempt gets longer timeout
                
                _logger.LogInformation($"CreateCollection attempt {attempt + 1}/{maxRetries} for collection '{name}' with timeout {attemptTimeout}ms");
                
                var result = await PythonContext.ExecuteAsync(() =>
                {
                    
                    _logger.LogInformation($"Before PyObject");
                    dynamic client = ChromaClientPool.GetClient(_clientId);
                    PyObject? metadataObj = null;

                    if (metadata != null && metadata.Count > 0)
                    {
                        metadataObj = ConvertDictionaryToPyDict(metadata);
                    }

                    _logger.LogInformation($"Attempting to create collection within Python '{name}', with metadata: {(metadataObj != null)}, client exists: {(client != null)}");
                if (metadataObj != null)
                {
                        try
                        {
                            client!.create_collection(name: name, metadata: metadataObj);
                        }
                        catch (PythonException ex)
                        {
                            _logger.LogError($"Failed to create collection '{name}' with metadata - with error: " + ex.Message);
                            // Re-throw to be caught outside the Python context
                            throw;
                        }
                    }
                else
                {
                    try
                    {
                        client!.create_collection(name: name);
                    }
                    catch (PythonException ex)
                    {
                        _logger.LogError($"Failed to create collection '{name}' - with error: " + ex.Message);
                        // Re-throw to be caught outside the Python context
                        throw;
                    }
                 }

                    _logger.LogInformation($"Created collection '{name}'");
                    return true;

                }, timeoutMs: attemptTimeout, operationName: $"CreateCollection_{name}_Attempt{attempt + 1}");
                
                //var result = true;
                // Success - return immediately
                return result;
            }
            catch (PythonException)
            {
                // Re-throw PythonException immediately without retry
                // This is expected behavior for duplicate collections
                throw;
            }
            catch (TimeoutException ex) when (attempt < maxRetries - 1)
            {
                // Log the timeout and retry
                _logger.LogWarning($"CreateCollection attempt {attempt + 1} timed out for collection '{name}': {ex.Message}. Retrying after {retryDelays[attempt]}ms...");
                await Task.Delay(retryDelays[attempt]);

                // Force a small GC collection to help clear any Python.NET state
                GC.Collect(0, GCCollectionMode.Optimized);
            }
            catch (Exception ex) when (attempt < maxRetries - 1 && ex.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase))
            {
                // Handle potential deadlock-related exceptions
                _logger.LogWarning($"CreateCollection attempt {attempt + 1} encountered potential deadlock for collection '{name}': {ex.Message}. Retrying after {retryDelays[attempt]}ms...");
                await Task.Delay(retryDelays[attempt]);
            }
        }

        // If we get here, all retries failed - throw the last exception
        throw new InvalidOperationException($"Failed to create collection '{name}' after {maxRetries} attempts due to repeated timeouts or deadlocks");
    }

    /// <summary>
    /// Gets a collection from ChromaDB
    /// </summary>
    public async Task<object?> GetCollectionAsync(string name)
    {
        await EnsureClientInitializedAsync();
        
        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                dynamic client = ChromaClientPool.GetClient(_clientId);
                dynamic collection = client.get_collection(name: name);
                
                var result = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["id"] = collection.id.ToString(),
                    ["metadata"] = ConvertPyDictToDictionary(collection.metadata)
                };
                
                return (object?)result;
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to get collection '{name}' - Python error: {ex.Message}");
                // Re-throw PythonException for non-existent collections as this is expected behavior
                throw;
            }
        }, timeoutMs: 30000, operationName: $"GetCollection_{name}");
    }

    /// <summary>
    /// Deletes a collection from ChromaDB
    /// </summary>
    public async Task<bool> DeleteCollectionAsync(string name)
    {
        await EnsureClientInitializedAsync();
        
        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                dynamic client = ChromaClientPool.GetClient(_clientId);
                client.delete_collection(name: name);
                _logger.LogInformation($"Deleted collection '{name}'");
                return true;
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to delete collection '{name}' - Python error: {ex.Message}");
                return false;
            }
        }, timeoutMs: 30000, operationName: $"DeleteCollection_{name}");
    }

    /// <summary>
    /// Adds documents to a ChromaDB collection
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="documents">List of document content</param>
    /// <param name="ids">List of document IDs</param>
    /// <param name="metadatas">Optional metadata for documents</param>
    /// <param name="allowDuplicateIds">Whether to allow duplicate IDs</param>
    /// <param name="markAsLocalChange">Whether to mark documents as local changes (default: true for user-initiated additions, false for sync operations from Dolt)</param>
    public async Task<bool> AddDocumentsAsync(string collectionName, List<string> documents, List<string> ids, List<Dictionary<string, object>>? metadatas = null, bool allowDuplicateIds = false, bool markAsLocalChange = true)
    {
        
        await EnsureClientInitializedAsync();
        
        // Process documents with chunking
        var allChunkIds = new List<string>();
        var allChunkDocuments = new List<string>();
        var allChunkMetadatas = new List<Dictionary<string, object>>();
        
        for (int i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            var documentId = ids[i];
            var documentMetadata = metadatas?.ElementAtOrDefault(i) ?? new Dictionary<string, object>();
            
            // Check if this is already a chunk ID (prevent double-chunking)
            if (_idResolver?.IsChunkId(documentId) == true)
            {
                _logger.LogInformation($"Document '{documentId}' is already a chunk - adding directly without re-chunking");
                
                // Add directly without re-chunking
                allChunkIds.Add(documentId);
                allChunkDocuments.Add(document);
                
                // Preserve existing metadata, just add the local change flag
                var directMetadata = new Dictionary<string, object>(documentMetadata)
                {
                    ["is_local_change"] = markAsLocalChange
                };
                allChunkMetadatas.Add(directMetadata);
                continue;
            }
            
            // Chunk the document using DocumentConverter
            var chunks = DocumentConverterUtility.ChunkContent(document, chunkSize: 512, chunkOverlap: 50);
            
            _logger.LogInformation($"Document '{documentId}' chunked into {chunks.Count} chunks (original length: {document.Length} chars)");
            
            // Optimization: For single chunk documents, use original ID without _chunk_0 suffix
            if (chunks.Count == 1)
            {
                _logger.LogDebug($"Single chunk for document '{documentId}' - using original ID");
                allChunkIds.Add(documentId);
                allChunkDocuments.Add(chunks[0]);
                
                var singleChunkMetadata = new Dictionary<string, object>(documentMetadata)
                {
                    ["source_id"] = documentId,  // Self-reference for single chunks
                    ["chunk_index"] = 0,
                    ["total_chunks"] = 1,
                    ["is_local_change"] = markAsLocalChange
                };
                allChunkMetadatas.Add(singleChunkMetadata);
            }
            else
            {
                // Multiple chunks - use standard chunking with _chunk_# suffix
                for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
                {
                    var chunkId = $"{documentId}_chunk_{chunkIndex}";
                    allChunkIds.Add(chunkId);
                    allChunkDocuments.Add(chunks[chunkIndex]);
                    
                    var chunkMetadata = new Dictionary<string, object>(documentMetadata)
                    {
                        ["source_id"] = documentId,  // Base document ID for expansion
                        ["chunk_index"] = chunkIndex,
                        ["total_chunks"] = chunks.Count,
                        ["is_local_change"] = markAsLocalChange
                    };
                    allChunkMetadatas.Add(chunkMetadata);
                }
            }
        }
        
        // Check for duplicate IDs if not allowed (this can happen outside Python thread)
        if (!allowDuplicateIds)
        {
            var existingDocs = await GetDocumentsAsync(collectionName, allChunkIds, null, 1);

            _logger.LogInformation($"Checked for existing chunk documents");

            if (existingDocs != null && existingDocs is Dictionary<string, object> result)
            {
                if (result.TryGetValue("ids", out var existingIds) && existingIds is List<object> idList && idList.Count > 0)
                {
                    _logger.LogInformation($"Found conflicting chunk ID!");

                    var existingId = idList[0]?.ToString();
                    throw new InvalidOperationException($"Document chunk with ID '{existingId}' already exists in collection '{collectionName}'");
                }
            }
        }

        return await PythonContext.ExecuteAsync(() =>
        {
            dynamic client = ChromaClientPool.GetClient(_clientId);
            dynamic collection = null;
            try
            {
                collection = client.get_or_create_collection(name: collectionName);
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to get or create collection '{collectionName}': {ex.Message}");
                return false;
            }
            
            // Convert C# lists to Python lists
            PyObject pyIds = ConvertListToPyList(allChunkIds);
            PyObject pyDocuments = ConvertListToPyList(allChunkDocuments);
            PyObject pyMetadatas = ConvertMetadatasToPyList(allChunkMetadatas);
            
            // Add chunks to collection
            try
            {
                collection.add(
                    ids: pyIds,
                    documents: pyDocuments,
                    metadatas: pyMetadatas
                );
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to add document chunks to collection '{collectionName}': {ex.Message}");
                return false;
            }

            _logger.LogInformation($"Added {documents.Count} documents as {allChunkIds.Count} chunks to collection '{collectionName}'");
            return true;
        }, timeoutMs: 60000, operationName: $"AddDocuments_{collectionName}");
        
    }

    /// <summary>
    /// Queries documents in a ChromaDB collection
    /// </summary>
    public async Task<object?> QueryDocumentsAsync(string collectionName, List<string> queryTexts, int nResults = 5,
        Dictionary<string, object>? where = null, Dictionary<string, object>? whereDocument = null)
    {
        await EnsureClientInitializedAsync();
        
        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                dynamic client = ChromaClientPool.GetClient(_clientId);
                dynamic collection = client.get_collection(name: collectionName);
                
                PyObject pyQueryTexts = ConvertListToPyList(queryTexts);
                PyObject? pyWhere = where != null ? ConvertDictionaryToPyDict(where) : null;
                PyObject? pyWhereDocument = whereDocument != null ? ConvertDictionaryToPyDict(whereDocument) : null;

                dynamic results;
                if (pyWhere != null && pyWhereDocument != null)
                {
                    results = collection.query(
                        query_texts: pyQueryTexts,
                        n_results: nResults,
                        where: pyWhere,
                        where_document: pyWhereDocument
                    );
                }
                else if (pyWhere != null)
                {
                    results = collection.query(
                        query_texts: pyQueryTexts,
                        n_results: nResults,
                        where: pyWhere
                    );
                }
                else if (pyWhereDocument != null)
                {
                    results = collection.query(
                        query_texts: pyQueryTexts,
                        n_results: nResults,
                        where_document: pyWhereDocument
                    );
                }
                else
                {
                    results = collection.query(
                        query_texts: pyQueryTexts,
                        n_results: nResults
                    );
                }

                // Convert results to C# objects
                var result = new Dictionary<string, object>
                {
                    ["ids"] = ConvertPyListToList(results["ids"]),
                    ["documents"] = results["documents"] != null ? ConvertPyListToList(results["documents"]) : new List<object>(),
                    ["metadatas"] = results["metadatas"] != null ? ConvertPyListToMetadatasList(results["metadatas"]) : new List<object>(),
                    ["distances"] = ConvertPyListToList(results["distances"])
                };

                return (object?)result;
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to query documents in collection '{collectionName}' - Python error: {ex.Message}");
                return new Dictionary<string, object>
                {
                    ["ids"] = new List<object>(),
                    ["documents"] = new List<object>(),
                    ["metadatas"] = new List<object>(),
                    ["distances"] = new List<object>()
                };
            }
        }, timeoutMs: 60000, operationName: $"QueryDocuments_{collectionName}");
    }

    /// <summary>
    /// Gets documents from a ChromaDB collection
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="ids">Optional list of document IDs to retrieve</param>
    /// <param name="where">Optional metadata filter</param>
    /// <param name="limit">Optional limit on number of documents to return</param>
    /// <param name="inclEmbeddings">Whether to include embeddings in the response (default: false for backward compatibility)</param>
    public async Task<object?> GetDocumentsAsync(string collectionName, List<string>? ids = null,
        Dictionary<string, object>? where = null, int? limit = null, bool inclEmbeddings = false)
    {
        await EnsureClientInitializedAsync();

        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                dynamic client = ChromaClientPool.GetClient(_clientId);
                dynamic collection = client.get_collection(name: collectionName);

                PyObject? pyIds = ids != null && ids.Count > 0 ? ConvertListToPyList(ids) : null;
                PyObject? pyWhere = where != null ? ConvertDictionaryToPyDict(where) : null;

                // Build include list based on inclEmbeddings parameter
                var includeItems = new List<string> { "documents", "metadatas" };
                if (inclEmbeddings)
                {
                    includeItems.Add("embeddings");
                }
                PyObject pyInclude = ConvertListToPyList(includeItems);

                dynamic results;
                if (pyIds != null && pyWhere != null && limit.HasValue)
                {
                    results = collection.get(ids: pyIds, where: pyWhere, limit: limit.Value, include: pyInclude);
                }
                else if (pyIds != null && pyWhere != null)
                {
                    results = collection.get(ids: pyIds, where: pyWhere, include: pyInclude);
                }
                else if (pyIds != null && limit.HasValue)
                {
                    results = collection.get(ids: pyIds, limit: limit.Value, include: pyInclude);
                }
                else if (pyWhere != null && limit.HasValue)
                {
                    results = collection.get(where: pyWhere, limit: limit.Value, include: pyInclude);
                }
                else if (pyIds != null)
                {
                    results = collection.get(ids: pyIds, include: pyInclude);
                }
                else if (pyWhere != null)
                {
                    results = collection.get(where: pyWhere, include: pyInclude);
                }
                else if (limit.HasValue)
                {
                    results = collection.get(limit: limit.Value, include: pyInclude);
                }
                else
                {
                    results = collection.get(include: pyInclude);
                }

                // Convert results to C# objects
                var result = new Dictionary<string, object>
                {
                    ["ids"] = ConvertPyListToList(results["ids"]),
                    ["documents"] = results["documents"] != null ? ConvertPyListToList(results["documents"]) : new List<object>(),
                    ["metadatas"] = results["metadatas"] != null ? ConvertPyListToMetadatasList(results["metadatas"]) : new List<object>()
                };

                // Include embeddings if requested
                if (inclEmbeddings && results["embeddings"] != null)
                {
                    result["embeddings"] = ConvertPyEmbeddingsToList(results["embeddings"]);
                }

                return (object?)result;
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to get documents from collection '{collectionName}' - Python error: {ex.Message}");
                var errorResult = new Dictionary<string, object>
                {
                    ["ids"] = new List<object>(),
                    ["documents"] = new List<object>(),
                    ["metadatas"] = new List<object>()
                };
                if (inclEmbeddings)
                {
                    errorResult["embeddings"] = new List<object>();
                }
                return errorResult;
            }
        }, timeoutMs: 60000, operationName: $"GetDocuments_{collectionName}");
    }

    /// <summary>
    /// Updates documents in a ChromaDB collection with optional chunk expansion
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="ids">List of document IDs to update</param>
    /// <param name="documents">Optional new document content</param>
    /// <param name="metadatas">Optional new metadata for documents</param>
    /// <param name="markAsLocalChange">Whether to mark documents as local changes (default: true for user-initiated updates, false for sync operations)</param>
    /// <param name="expandChunks">Whether to expand base document IDs to all their chunks (default: true)</param>
    public async Task<bool> UpdateDocumentsAsync(string collectionName, List<string> ids,
        List<string>? documents = null, List<Dictionary<string, object>>? metadatas = null, bool markAsLocalChange = true, bool expandChunks = true)
    {
        await EnsureClientInitializedAsync();
        
        if (documents == null && metadatas == null)
        {
            throw new ArgumentException("At least one of documents or metadatas must be provided");
        }

        // Handle document content updates with chunk expansion
        if (documents != null && expandChunks)
        {
            // When updating document content with base IDs, we need to:
            // 1. Delete all existing chunks for these documents
            // 2. Re-add documents with new content using proper chunking
            // This ensures proper rechunking when document length changes
            
            var resolver = _idResolver ?? CreateTemporaryResolver();
            var baseIds = resolver.ExtractUniqueBaseDocumentIds(ids);
            
            // If we're dealing with base IDs and updating content, handle rechunking
            if (baseIds.Count > 0 && !ids.Any(id => resolver.IsChunkId(id)))
            {
                _logger.LogInformation($"Performing document update with rechunking for {baseIds.Count} documents");
                
                // Delete all existing chunks for these documents
                var chunkIdsToDelete = await resolver.ExpandMultipleToChunkIdsAsync(collectionName, baseIds);
                if (chunkIdsToDelete.Count > 0)
                {
                    await DeleteDocumentsAsync(collectionName, chunkIdsToDelete, expandChunks: false);
                }
                
                // Re-add documents with new content and proper chunking
                _logger.LogInformation($"Re-adding {baseIds.Count} documents with updated content");
                
                // Create metadata for re-addition
                List<Dictionary<string, object>>? reAddMetadatas = null;
                if (metadatas != null && metadatas.Count > 0)
                {
                    reAddMetadatas = new List<Dictionary<string, object>>();
                    for (int i = 0; i < baseIds.Count; i++)
                    {
                        var metadataIndex = Math.Min(i, metadatas.Count - 1);
                        var metadata = new Dictionary<string, object>(metadatas[metadataIndex]);
                        metadata["is_local_change"] = markAsLocalChange;
                        reAddMetadatas.Add(metadata);
                    }
                }
                
                // Re-add using AddDocumentsAsync which handles chunking properly
                var reAddResult = await AddDocumentsAsync(collectionName, documents, baseIds, 
                    reAddMetadatas, allowDuplicateIds: true, markAsLocalChange: markAsLocalChange);
                
                if (!reAddResult)
                {
                    _logger.LogError($"Failed to re-add documents after deletion during update");
                    return false;
                }
                
                _logger.LogInformation($"Successfully updated {baseIds.Count} documents with rechunking");
                return true; // Complete - no need to continue with normal update path
            }
        }

        // Expand IDs for metadata-only updates or when not rechunking
        List<string> actualIdsToUpdate = ids;
        if (expandChunks && documents == null) // Metadata-only update
        {
            var resolver = _idResolver ?? CreateTemporaryResolver();
            actualIdsToUpdate = await resolver.ExpandMultipleToChunkIdsAsync(collectionName, ids);
            if (actualIdsToUpdate.Count > ids.Count)
            {
                _logger.LogInformation($"Expanded {ids.Count} document IDs to {actualIdsToUpdate.Count} chunk IDs for metadata update");
            }
        }
        
        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                dynamic client = ChromaClientPool.GetClient(_clientId);
                dynamic collection = client.get_collection(name: collectionName);
                
                PyObject pyIds = ConvertListToPyList(actualIdsToUpdate);
                PyObject? pyDocuments = documents != null ? ConvertListToPyList(documents) : null;
                
                // Handle metadata with proper is_local_change flag (PP13-68-C2 fix)
                List<Dictionary<string, object>> finalMetadatas;
                if (metadatas != null && metadatas.Count > 0)
                {
                    // If we expanded chunks, we need to replicate metadata for each chunk
                    if (actualIdsToUpdate.Count > metadatas.Count)
                    {
                        // Replicate the first metadata for all chunks (assumes single document update)
                        var baseMeta = metadatas[0];
                        finalMetadatas = actualIdsToUpdate.Select(id =>
                        {
                            var newMeta = new Dictionary<string, object>(baseMeta);
                            newMeta["is_local_change"] = markAsLocalChange;
                            return newMeta;
                        }).ToList();
                    }
                    else
                    {
                        finalMetadatas = metadatas.Select(meta =>
                        {
                            var newMeta = new Dictionary<string, object>(meta);
                            newMeta["is_local_change"] = markAsLocalChange;
                            return newMeta;
                        }).ToList();
                    }
                }
                else
                {
                    // Create metadata with appropriate is_local_change flag for all IDs
                    finalMetadatas = actualIdsToUpdate.Select(_ => new Dictionary<string, object>
                    {
                        ["is_local_change"] = markAsLocalChange
                    }).ToList();
                }
                
                PyObject pyMetadatas = ConvertMetadatasToPyList(finalMetadatas);

                // Always update with metadata (for is_local_change flag management)
                if (pyDocuments != null)
                {
                    collection.update(ids: pyIds, documents: pyDocuments, metadatas: pyMetadatas);
                }
                else
                {
                    collection.update(ids: pyIds, metadatas: pyMetadatas);
                }

                _logger.LogInformation($"Updated {actualIdsToUpdate.Count} documents/chunks in collection '{collectionName}'");
                return true;
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to update documents in collection '{collectionName}' - Python error: {ex.Message}");
                return false;
            }
        }, timeoutMs: 60000, operationName: $"UpdateDocuments_{collectionName}");
    }

    /// <summary>
    /// Deletes documents from a ChromaDB collection with optional chunk expansion
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="ids">List of document IDs to delete</param>
    /// <param name="expandChunks">Whether to expand base document IDs to all their chunks (default: true)</param>
    public async Task<bool> DeleteDocumentsAsync(string collectionName, List<string> ids, bool expandChunks = true)
    {
        await EnsureClientInitializedAsync();
        
        // Expand base IDs to chunk IDs if requested and resolver is available
        List<string> actualIdsToDelete = ids;
        if (expandChunks && _idResolver != null)
        {
            actualIdsToDelete = await _idResolver.ExpandMultipleToChunkIdsAsync(collectionName, ids);
            if (actualIdsToDelete.Count > ids.Count)
            {
                _logger.LogInformation($"Expanded {ids.Count} document IDs to {actualIdsToDelete.Count} chunk IDs for deletion");
            }
        }
        else if (expandChunks && _idResolver == null)
        {
            // Create a temporary resolver for this operation
            // Using a logger factory to create the correct logger type
            var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });
            var tempLogger = loggerFactory.CreateLogger<DocumentIdResolver>();
            var tempResolver = new DocumentIdResolver(this, tempLogger);
            actualIdsToDelete = await tempResolver.ExpandMultipleToChunkIdsAsync(collectionName, ids);
            if (actualIdsToDelete.Count > ids.Count)
            {
                _logger.LogInformation($"Expanded {ids.Count} document IDs to {actualIdsToDelete.Count} chunk IDs for deletion");
            }
        }
        
        // Handle case where no documents exist to delete (this is not an error)
        if (actualIdsToDelete.Count == 0)
        {
            _logger.LogInformation($"No documents found to delete in collection '{collectionName}' - operation succeeded");
            return true;
        }

        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                dynamic client = ChromaClientPool.GetClient(_clientId);
                dynamic collection = client.get_collection(name: collectionName);
                PyObject pyIds = ConvertListToPyList(actualIdsToDelete);
                collection.delete(ids: pyIds);
                
                _logger.LogInformation($"Deleted {actualIdsToDelete.Count} documents/chunks from collection '{collectionName}'");
                return true;
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to delete documents from collection '{collectionName}' - Python error: {ex.Message}");
                return false;
            }
        }, timeoutMs: 30000, operationName: $"DeleteDocuments_{collectionName}");
    }

    /// <summary>
    /// Gets the document count in a ChromaDB collection
    /// </summary>
    public async Task<int> GetCollectionCountAsync(string collectionName)
    {
        await EnsureClientInitializedAsync();
        
        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                dynamic client = ChromaClientPool.GetClient(_clientId);
                dynamic collection = client.get_collection(name: collectionName);
                int count = collection.count();
                return count;
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to get collection count for '{collectionName}' - Python error: {ex.Message}");
                return 0; // Return 0 on error
            }
        }, timeoutMs: 30000, operationName: $"GetCollectionCount_{collectionName}");
    }

    /// <summary>
    /// Gets the count of documents in a collection (alias for GetCollectionCountAsync for consistency)
    /// </summary>
    public async Task<int> GetDocumentCountAsync(string collectionName)
    {
        return await GetCollectionCountAsync(collectionName);
    }

    #region Python Conversion Helpers

    /// <summary>
    /// Converts a C# list to a Python list
    /// </summary>
    private PyObject ConvertListToPyList(List<string> list)
    {
        try
        {
            dynamic pyList = PythonEngine.Eval("[]");
            foreach (var item in list)
            {
                try
                {
                    pyList.append(item);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to append item '{item}' to Python list: {ex.Message}. Skipping item.");
                }
            }
            return pyList;
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Critical failure in ConvertListToPyList: {ex.Message}. Returning empty list.");
            // Return empty Python list as fallback
            return PythonEngine.Eval("[]");
        }
    }

    /// <summary>
    /// Converts a C# dictionary to a Python dictionary
    /// </summary>
    private PyObject ConvertDictionaryToPyDict(Dictionary<string, object> dict)
    {
        try
        {
            dynamic pyDict = PythonEngine.Eval("{}");
            
            foreach (var kvp in dict)
            {
                try
                {
                    if (kvp.Value is Dictionary<string, object> nestedDict)
                    {
                        try
                        {
                            pyDict[kvp.Key] = ConvertDictionaryToPyDict(nestedDict);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning($"Failed to convert nested dictionary for key '{kvp.Key}': {ex.Message}. Using string representation.");
                            pyDict[kvp.Key] = new PyString(nestedDict.ToString());
                        }
                    }
                    else if (kvp.Value is List<object> listValue)
                    {
                        try
                        {
                            dynamic pyList = PythonEngine.Eval("[]");
                            foreach (var item in listValue)
                            {
                                try
                                {
                                    pyList.append(item);
                                }
                                catch (Exception ex)
                                {
                                    _logger?.LogWarning($"Failed to append list item for key '{kvp.Key}': {ex.Message}. Skipping item.");
                                }
                            }
                            pyDict[kvp.Key] = pyList;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning($"Failed to create Python list for key '{kvp.Key}': {ex.Message}. Using string representation.");
                            pyDict[kvp.Key] = new PyString(string.Join(", ", listValue));
                        }
                    }
                    else
                    {
                        try
                        {
                            // Handle different value types explicitly with error handling
                            if (kvp.Value is string stringValue)
                            {
                                pyDict[kvp.Key] = new PyString(stringValue);
                            }
                            else if (kvp.Value is int intValue)
                            {
                                pyDict[kvp.Key] = new PyInt(intValue);
                            }
                            else if (kvp.Value is bool boolValue)
                            {
                                pyDict[kvp.Key] = boolValue.ToPython();
                            }
                            else if (kvp.Value is float floatValue)
                            {
                                pyDict[kvp.Key] = new PyFloat(floatValue);
                            }
                            else if (kvp.Value is double doubleValue)
                            {
                                pyDict[kvp.Key] = new PyFloat(doubleValue);
                            }
                            else
                            {
                                pyDict[kvp.Key] = new PyString(kvp.Value?.ToString() ?? "");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning($"Failed to convert value for key '{kvp.Key}' (type: {kvp.Value?.GetType().Name}): {ex.Message}. Using string representation.");
                            pyDict[kvp.Key] = new PyString(kvp.Value?.ToString() ?? "");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to process key '{kvp.Key}': {ex.Message}. Skipping this key.");
                }
            }
            return pyDict;
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Critical failure in ConvertDictionaryToPyDict: {ex.Message}. Returning empty dictionary.");
            // Return empty Python dictionary as fallback
            return PythonEngine.Eval("{}");
        }
    }

    /// <summary>
    /// Converts a list of metadata dictionaries to Python list
    /// </summary>
    private PyObject ConvertMetadatasToPyList(List<Dictionary<string, object>> metadatas)
    {
        try
        {
            dynamic pyList = PythonEngine.Eval("[]");
            foreach (var metadata in metadatas)
            {
                try
                {
                    pyList.append(ConvertDictionaryToPyDict(metadata));
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to append metadata dictionary to Python list: {ex.Message}. Adding empty dict as fallback.");
                    try
                    {
                        // Add empty dict as fallback
                        pyList.append(PythonEngine.Eval("{}"));
                    }
                    catch (Exception ex2)
                    {
                        _logger?.LogWarning($"Failed to append empty dict fallback: {ex2.Message}. Skipping metadata item.");
                    }
                }
            }
            return pyList;
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Critical failure in ConvertMetadatasToPyList: {ex.Message}. Returning empty list.");
            // Return empty Python list as fallback
            return PythonEngine.Eval("[]");
        }
    }

    /// <summary>
    /// Converts a Python list to C# list
    /// </summary>
    private List<object> ConvertPyListToList(dynamic pyList)
    {
        var result = new List<object>();
        foreach (var item in pyList)
        {
            if (item is PyObject pyObj)
            {
                // Check if it's a nested list (not a string)
                var pyType = pyObj.GetPythonType();
                if (pyObj.HasAttr("__iter__") && !pyType.Name.Equals("str"))
                {
                    result.Add(ConvertPyListToList(item));
                }
                else
                {
                    result.Add(item.ToString());
                }
            }
            else
            {
                result.Add(item?.ToString() ?? string.Empty);
            }
        }
        return result;
    }

    /// <summary>
    /// Converts Python embeddings (list of numpy arrays or lists) to C# List of List of float.
    /// ChromaDB returns embeddings as a list of numpy arrays, each representing an embedding vector.
    /// </summary>
    private List<List<float>> ConvertPyEmbeddingsToList(dynamic pyEmbeddings)
    {
        var result = new List<List<float>>();
        if (pyEmbeddings == null) return result;

        try
        {
            foreach (var embedding in pyEmbeddings)
            {
                var embeddingVector = new List<float>();
                if (embedding == null)
                {
                    result.Add(embeddingVector);
                    continue;
                }

                // Handle numpy arrays and Python lists
                // First convert to Python list if it's a numpy array
                dynamic embeddingList;
                if (embedding is PyObject pyObj)
                {
                    var pyType = pyObj.GetPythonType();
                    if (pyType.Name.Equals("ndarray"))
                    {
                        // Convert numpy array to Python list using tolist()
                        embeddingList = embedding.tolist();
                    }
                    else
                    {
                        embeddingList = embedding;
                    }
                }
                else
                {
                    embeddingList = embedding;
                }

                // Iterate through the embedding values
                foreach (var value in embeddingList)
                {
                    try
                    {
                        if (value is PyObject pyVal)
                        {
                            // Try to convert to float
                            float floatVal = (float)Convert.ToDouble(pyVal.ToString());
                            embeddingVector.Add(floatVal);
                        }
                        else if (value is double dVal)
                        {
                            embeddingVector.Add((float)dVal);
                        }
                        else if (value is float fVal)
                        {
                            embeddingVector.Add(fVal);
                        }
                        else
                        {
                            // Fallback: parse as string
                            float floatVal = float.Parse(value?.ToString() ?? "0");
                            embeddingVector.Add(floatVal);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"Failed to convert embedding value: {ex.Message}. Using 0.0 as fallback.");
                        embeddingVector.Add(0.0f);
                    }
                }
                result.Add(embeddingVector);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Failed to convert embeddings: {ex.Message}. Returning empty list.");
            return new List<List<float>>();
        }

        return result;
    }

    /// <summary>
    /// Converts Python list of metadata dictionaries to C# list
    /// </summary>
    private List<object> ConvertPyListToMetadatasList(dynamic pyList)
    {
        var result = new List<object>();
        if (pyList == null) return result;
        
        foreach (var item in pyList)
        {
            if (item == null)
            {
                result.Add(new Dictionary<string, object>());
                continue;
            }
            
            if (item is PyObject pyObj)
            {
                // Check if it's iterable (list of metadata dictionaries)
                var pyType = pyObj.GetPythonType();
                if (pyObj.HasAttr("__iter__") && !pyType.Name.Equals("str") && !pyType.Name.Equals("dict"))
                {
                    var subList = new List<Dictionary<string, object>>();
                    foreach (var metadata in item)
                    {
                        if (metadata != null)
                        {
                            subList.Add(ConvertPyDictToDictionary(metadata));
                        }
                        else
                        {
                            subList.Add(new Dictionary<string, object>());
                        }
                    }
                    result.Add(subList);
                }
                else if (pyType.Name.Equals("dict"))
                {
                    // It's a single dictionary, not a list of dictionaries
                    result.Add(ConvertPyDictToDictionary(item));
                }
                else
                {
                    result.Add(new Dictionary<string, object>());
                }
            }
            else
            {
                result.Add(ConvertPyDictToDictionary(item));
            }
        }
        return result;
    }

    /// <summary>
    /// Converts a Python dictionary to C# dictionary.
    /// Properly handles Python types including bool, int, float, and str.
    /// </summary>
    private Dictionary<string, object> ConvertPyDictToDictionary(dynamic pyDict)
    {
        var result = new Dictionary<string, object>();
        if (pyDict == null)
            return result;

        foreach (var key in pyDict)
        {
            string keyStr = key.ToString();
            var value = pyDict[key];

            if (value is PyObject pyObj)
            {
                var pyType = pyObj.GetPythonType();
                var typeName = pyType.Name.ToString();

                // Handle Python boolean - must check before other types
                if (typeName.Equals("bool"))
                {
                    // Python bool toString() returns "True" or "False"
                    result[keyStr] = value.ToString().Equals("True", StringComparison.OrdinalIgnoreCase);
                }
                // Handle Python integer
                else if (typeName.Equals("int"))
                {
                    if (long.TryParse(value.ToString(), out long longVal))
                    {
                        // Use int if it fits, otherwise long
                        if (longVal >= int.MinValue && longVal <= int.MaxValue)
                            result[keyStr] = (int)longVal;
                        else
                            result[keyStr] = longVal;
                    }
                    else
                    {
                        result[keyStr] = value.ToString();
                    }
                }
                // Handle Python float
                else if (typeName.Equals("float"))
                {
                    if (double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double doubleVal))
                    {
                        result[keyStr] = doubleVal;
                    }
                    else
                    {
                        result[keyStr] = value.ToString();
                    }
                }
                // Handle iterable types (lists, etc.) but not strings
                else if (pyObj.HasAttr("__iter__") && !typeName.Equals("str"))
                {
                    result[keyStr] = ConvertPyListToList(value);
                }
                // Handle strings and other types
                else
                {
                    result[keyStr] = value.ToString();
                }
            }
            else
            {
                result[keyStr] = value?.ToString() ?? string.Empty;
            }
        }
        return result;
    }

    #endregion

    /// <summary>
    /// Generates a unique client ID for this service instance
    /// </summary>
    private string GenerateClientId()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var guid = Guid.NewGuid().ToString("N")[..8]; // First 8 characters of GUID
        return $"ChromaPythonService_{timestamp}_{guid}";
    }

    /// <summary>
    /// Generates a configuration string for the client pool
    /// </summary>
    private string GenerateConfigurationString()
    {
        if (!string.IsNullOrEmpty(_configuration.ChromaDataPath))
        {
            return $"persistent:{_configuration.ChromaDataPath}";
        }
        else if (!string.IsNullOrEmpty(_configuration.ChromaHost))
        {
            return $"http:{_configuration.ChromaHost}:{_configuration.ChromaPort}";
        }
        else
        {
            throw new InvalidOperationException("ChromaDB configuration must specify either ChromaDataPath (persistent) or ChromaHost (http)");
        }
    }

    /// <summary>
    /// Gets the client ID for this service instance
    /// </summary>
    public string GetClientId() => _clientId;

    /// <summary>
    /// Forces immediate disposal for testing scenarios (bypasses grace period)
    /// </summary>
    public async Task DisposeImmediatelyAsync()
    {
        if (!_disposed)
        {
            _logger?.LogInformation("Force disposing ChromaPythonService immediately with client ID: {ClientId}", _clientId);
            
            // Use immediate disposal to bypass the 5-second delay
            await PythonContext.ExecuteAsync(() =>
            {
                ChromaClientPool.DisposeClientImmediately(_clientId);
                return true;
            }, timeoutMs: 10000, operationName: "ImmediateClientDisposal");
            
            _clientInitialized = false;
            _disposed = true;
            
            _logger?.LogInformation("ChromaPythonService immediately disposed successfully");
        }
    }

    /// <summary>
    /// Disposes of the Python resources
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes of the Python resources
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _logger?.LogInformation("Disposing ChromaPythonService with client ID: {ClientId}", _clientId);
                
                // Dispose this specific client from the pool
                ChromaClientPool.DisposeClient(_clientId);
                _clientInitialized = false;
                
                _logger?.LogInformation("ChromaPythonService disposed successfully");
            }
            _disposed = true;
        }
    }
}