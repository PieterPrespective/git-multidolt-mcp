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

    /// <summary>
    /// Initializes a new instance of ChromaPythonService
    /// </summary>
    public ChromaPythonService(ILogger<ChromaPythonService> logger, IOptions<ServerConfiguration> configuration)
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

        // Client initialization is deferred until first use
        _logger.LogInformation("Created ChromaPythonService with client ID: {ClientId}", _clientId);
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
    public async Task<bool> AddDocumentsAsync(string collectionName, List<string> documents, List<string> ids, List<Dictionary<string, object>>? metadatas = null, bool allowDuplicateIds = false)
    {
        
        await EnsureClientInitializedAsync();
        
        // Check for duplicate IDs if not allowed (this can happen outside Python thread)
        if (!allowDuplicateIds)
        {
            var existingDocs = await GetDocumentsAsync(collectionName, ids, null, 1);

            _logger.LogInformation($"Gotten Existing Documents");

            if (existingDocs != null && existingDocs is Dictionary<string, object> result)
            {
                if (result.TryGetValue("ids", out var existingIds) && existingIds is List<object> idList && idList.Count > 0)
                {
                    _logger.LogInformation($"Found conflicting ID!");

                    var existingId = idList[0]?.ToString();
                    throw new InvalidOperationException($"Document with ID '{existingId}' already exists in collection '{collectionName}'");
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
            PyObject pyIds = ConvertListToPyList(ids);
            PyObject pyDocuments = ConvertListToPyList(documents);
            PyObject? pyMetadatas = null;

            // Ensure all documents have is_local_change=true metadata for Phase 2 change detection
            List<Dictionary<string, object>> finalMetadatas;
            if (metadatas != null && metadatas.Count > 0)
            {
                finalMetadatas = metadatas.Select(meta => 
                {
                    var newMeta = new Dictionary<string, object>(meta);
                    newMeta["is_local_change"] = true;
                    return newMeta;
                }).ToList();
            }
            else
            {
                // Create metadata with is_local_change=true for all documents
                finalMetadatas = ids.Select(_ => new Dictionary<string, object> 
                { 
                    ["is_local_change"] = true 
                }).ToList();
            }

            pyMetadatas = ConvertMetadatasToPyList(finalMetadatas);
            
            // Add documents to collection (always with metadata now for change detection)
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
                _logger.LogError($"Failed to add documents to collection '{collectionName}': {ex.Message}");
                return false;
            }

            _logger.LogInformation($"Added {documents.Count} documents to collection '{collectionName}'");
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
    public async Task<object?> GetDocumentsAsync(string collectionName, List<string>? ids = null,
        Dictionary<string, object>? where = null, int? limit = null)
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

                dynamic results;
                if (pyIds != null && pyWhere != null && limit.HasValue)
                {
                    results = collection.get(ids: pyIds, where: pyWhere, limit: limit.Value);
                }
                else if (pyIds != null && pyWhere != null)
                {
                    results = collection.get(ids: pyIds, where: pyWhere);
                }
                else if (pyIds != null && limit.HasValue)
                {
                    results = collection.get(ids: pyIds, limit: limit.Value);
                }
                else if (pyWhere != null && limit.HasValue)
                {
                    results = collection.get(where: pyWhere, limit: limit.Value);
                }
                else if (pyIds != null)
                {
                    results = collection.get(ids: pyIds);
                }
                else if (pyWhere != null)
                {
                    results = collection.get(where: pyWhere);
                }
                else if (limit.HasValue)
                {
                    results = collection.get(limit: limit.Value);
                }
                else
                {
                    results = collection.get();
                }
                
                // Convert results to C# objects
                var result = new Dictionary<string, object>
                {
                    ["ids"] = ConvertPyListToList(results["ids"]),
                    ["documents"] = results["documents"] != null ? ConvertPyListToList(results["documents"]) : new List<object>(),
                    ["metadatas"] = results["metadatas"] != null ? ConvertPyListToMetadatasList(results["metadatas"]) : new List<object>()
                };

                return (object?)result;
            }
            catch (PythonException ex)
            {
                _logger.LogError($"Failed to get documents from collection '{collectionName}' - Python error: {ex.Message}");
                return new Dictionary<string, object>
                {
                    ["ids"] = new List<object>(),
                    ["documents"] = new List<object>(),
                    ["metadatas"] = new List<object>()
                };
            }
        }, timeoutMs: 60000, operationName: $"GetDocuments_{collectionName}");
    }

    /// <summary>
    /// Updates documents in a ChromaDB collection
    /// </summary>
    public async Task<bool> UpdateDocumentsAsync(string collectionName, List<string> ids,
        List<string>? documents = null, List<Dictionary<string, object>>? metadatas = null)
    {
        await EnsureClientInitializedAsync();
        
        if (documents == null && metadatas == null)
        {
            throw new ArgumentException("At least one of documents or metadatas must be provided");
        }
        
        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                dynamic client = ChromaClientPool.GetClient(_clientId);
                dynamic collection = client.get_collection(name: collectionName);
                
                PyObject pyIds = ConvertListToPyList(ids);
                PyObject? pyDocuments = documents != null ? ConvertListToPyList(documents) : null;
                PyObject? pyMetadatas = metadatas != null ? ConvertMetadatasToPyList(metadatas) : null;

                if (pyDocuments != null && pyMetadatas != null)
                {
                    collection.update(ids: pyIds, documents: pyDocuments, metadatas: pyMetadatas);
                }
                else if (pyDocuments != null)
                {
                    collection.update(ids: pyIds, documents: pyDocuments);
                }
                else if (pyMetadatas != null)
                {
                    collection.update(ids: pyIds, metadatas: pyMetadatas);
                }

                _logger.LogInformation($"Updated {ids.Count} documents in collection '{collectionName}'");
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
    /// Deletes documents from a ChromaDB collection
    /// </summary>
    public async Task<bool> DeleteDocumentsAsync(string collectionName, List<string> ids)
    {
        await EnsureClientInitializedAsync();
        
        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                dynamic client = ChromaClientPool.GetClient(_clientId);
                dynamic collection = client.get_collection(name: collectionName);
                PyObject pyIds = ConvertListToPyList(ids);
                collection.delete(ids: pyIds);
                
                _logger.LogInformation($"Deleted {ids.Count} documents from collection '{collectionName}'");
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
    /// Converts a Python dictionary to C# dictionary
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
                if (pyObj.HasAttr("__iter__") && !pyType.Name.Equals("str"))
                {
                    result[keyStr] = ConvertPyListToList(value);
                }
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