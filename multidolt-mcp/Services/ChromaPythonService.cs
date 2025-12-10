using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Python.Runtime;
using DMMS.Models;

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
    private dynamic? _chromadb;
    private dynamic? _client;
    private bool _disposed = false;
    private static bool _pythonInitialized = false;
    private static readonly object _pythonLock = new object();

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

        InitializePython();
        InitializeChromaClient();
    }

    /// <summary>
    /// Initializes the Python runtime
    /// </summary>
    private void InitializePython()
    {
        lock (_pythonLock)
        {
            if (!_pythonInitialized)
            {
                try
                {
                    // Set Python DLL path - adjust based on system
                    string pythonDll = GetPythonDllPath();
                    if (!string.IsNullOrEmpty(pythonDll))
                    {
                        Runtime.PythonDLL = pythonDll;
                    }
                    
                    PythonEngine.Initialize();
                    _pythonInitialized = true;
                    _logger.LogInformation("Python runtime initialized successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize Python runtime");
                    throw new InvalidOperationException("Failed to initialize Python runtime", ex);
                }
            }
        }
    }

    /// <summary>
    /// Gets the Python DLL path based on the system
    /// </summary>
    private string GetPythonDllPath()
    {
        // Try to detect Python installation
        if (OperatingSystem.IsWindows())
        {
            // Common Python installation paths on Windows
            string[] possiblePaths = 
            {
                @"C:\ProgramData\anaconda3\python311.dll",
                @"C:\Python311\python311.dll",
                @"C:\Python312\python312.dll",
                @"C:\Python310\python310.dll",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python311\python311.dll",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python312\python312.dll",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python310\python310.dll"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    _logger.LogInformation($"Found Python DLL at: {path}");
                    return path;
                }
            }
        }

        // Return empty string to use system default
        return string.Empty;
    }

    /// <summary>
    /// Initializes the ChromaDB client
    /// </summary>
    private void InitializeChromaClient()
    {
        using (Py.GIL())
        {
            try
            {


                _chromadb = Py.Import("chromadb");
                
                // Create appropriate client based on configuration
                if (!string.IsNullOrEmpty(_configuration.ChromaDataPath))
                {
                    // Use PersistentClient for file-based storage
                    string dataPath = Path.GetFullPath(_configuration.ChromaDataPath);
                    Directory.CreateDirectory(dataPath);
                    _client = _chromadb.PersistentClient(
                        path: dataPath);
                    _logger.LogInformation($"Initialized ChromaDB PersistentClient at: {dataPath}");
                }
                else
                {
                    // Use HttpClient for remote ChromaDB server
                    _client = _chromadb.HttpClient(
                        host: _configuration.ChromaHost,
                        port: _configuration.ChromaPort
                    );
                    _logger.LogInformation($"Initialized ChromaDB HttpClient at {_configuration.ChromaHost}:{_configuration.ChromaPort}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ChromaDB client");
                throw new InvalidOperationException("Failed to initialize ChromaDB client", ex);
            }
        }
    }

    /// <summary>
    /// Lists all collections in ChromaDB
    /// </summary>
    public Task<List<string>> ListCollectionsAsync(int? limit = null, int? offset = null)
    {
        using (Py.GIL())
        {
            try
            {
                dynamic collections = _client!.list_collections();
                var result = new List<string>();
                
                foreach (dynamic collection in collections)
                {
                    result.Add(collection.ToString());
                }

                if (offset.HasValue)
                    result = result.Skip(offset.Value).ToList();
                if (limit.HasValue)
                    result = result.Take(limit.Value).ToList();

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing collections");
                throw;
            }
        }
    }

    /// <summary>
    /// Creates a new collection in ChromaDB
    /// </summary>
    public Task<bool> CreateCollectionAsync(string name, Dictionary<string, object>? metadata = null)
    {
        _logger.LogInformation($"Attempting to create collection '{name}'");
        
        using (Py.GIL())
        {
            try
            {
                _logger.LogInformation($"Before PyObject");
                PyObject? metadataObj = null;
                if (metadata != null && metadata.Count > 0)
                {
                    metadataObj = ConvertDictionaryToPyDict(metadata);
                }

                _logger.LogInformation($"Attempting to create collection within Python '{name}'");
                if (metadataObj != null)
                {
                    _client!.create_collection(name: name, metadata: metadataObj);
                }
                else
                {
                    _client!.create_collection(name: name);
                }
                
                _logger.LogInformation($"Created collection '{name}'");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating collection '{name}'");
                throw;
            }
        }
    }

    /// <summary>
    /// Gets a collection from ChromaDB
    /// </summary>
    public Task<object?> GetCollectionAsync(string name)
    {
        using (Py.GIL())
        {
            try
            {
                dynamic collection = _client!.get_collection(name: name);
                
                var result = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["id"] = collection.id.ToString(),
                    ["metadata"] = ConvertPyDictToDictionary(collection.metadata)
                };
                
                return Task.FromResult((object?)result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting collection '{name}'");
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes a collection from ChromaDB
    /// </summary>
    public Task<bool> DeleteCollectionAsync(string name)
    {
        using (Py.GIL())
        {
            try
            {
                _client!.delete_collection(name: name);
                _logger.LogInformation($"Deleted collection '{name}'");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting collection '{name}'");
                throw;
            }
        }
    }

    /// <summary>
    /// Adds documents to a ChromaDB collection
    /// </summary>
    public async Task<bool> AddDocumentsAsync(string collectionName, List<string> documents, List<string> ids, List<Dictionary<string, object>>? metadatas = null, bool allowDuplicateIds = false)
    {
        using (Py.GIL())
        {
            try
            {
                dynamic collection = _client!.get_or_create_collection(name: collectionName);
                
                // Check for duplicate IDs if not allowed
                if (!allowDuplicateIds)
                {
                    // Check if any of the IDs already exist
                    var existingDocs = await GetDocumentsAsync(collectionName, ids, null, 1);
                    if (existingDocs != null && existingDocs is Dictionary<string, object> result)
                    {
                        if (result.TryGetValue("ids", out var existingIds) && existingIds is List<object> idList && idList.Count > 0)
                        {
                            // Find which ID exists
                            var existingId = idList[0]?.ToString();
                            throw new InvalidOperationException($"Document with ID '{existingId}' already exists in collection '{collectionName}'");
                        }
                    }
                }
                
                // Convert C# lists to Python lists
                PyObject pyIds = ConvertListToPyList(ids);
                PyObject pyDocuments = ConvertListToPyList(documents);
                PyObject? pyMetadatas = null;

                if (metadatas != null && metadatas.Count > 0)
                {
                    pyMetadatas = ConvertMetadatasToPyList(metadatas);
                }

                // Add documents to collection
                if (pyMetadatas != null)
                {
                    collection.add(
                        ids: pyIds,
                        documents: pyDocuments,
                        metadatas: pyMetadatas
                    );
                }
                else
                {
                    collection.add(
                        ids: pyIds,
                        documents: pyDocuments
                    );
                }

                _logger.LogInformation($"Added {documents.Count} documents to collection '{collectionName}'");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error adding documents to collection '{collectionName}'");
                throw;
            }
        }
    }

    /// <summary>
    /// Queries documents in a ChromaDB collection
    /// </summary>
    public Task<object?> QueryDocumentsAsync(string collectionName, List<string> queryTexts, int nResults = 5,
        Dictionary<string, object>? where = null, Dictionary<string, object>? whereDocument = null)
    {
        using (Py.GIL())
        {
            try
            {
                dynamic collection = _client.get_collection(name: collectionName);
                
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

                return Task.FromResult((object?)result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error querying collection '{collectionName}'");
                throw;
            }
        }
    }

    /// <summary>
    /// Gets documents from a ChromaDB collection
    /// </summary>
    public Task<object?> GetDocumentsAsync(string collectionName, List<string>? ids = null,
        Dictionary<string, object>? where = null, int? limit = null)
    {
        using (Py.GIL())
        {
            try
            {
                dynamic collection = _client.get_collection(name: collectionName);
                
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

                return Task.FromResult((object?)result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting documents from collection '{collectionName}'");
                throw;
            }
        }
    }

    /// <summary>
    /// Updates documents in a ChromaDB collection
    /// </summary>
    public Task<bool> UpdateDocumentsAsync(string collectionName, List<string> ids,
        List<string>? documents = null, List<Dictionary<string, object>>? metadatas = null)
    {
        using (Py.GIL())
        {
            try
            {
                dynamic collection = _client.get_collection(name: collectionName);
                
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
                else
                {
                    throw new ArgumentException("At least one of documents or metadatas must be provided");
                }

                _logger.LogInformation($"Updated {ids.Count} documents in collection '{collectionName}'");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating documents in collection '{collectionName}'");
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes documents from a ChromaDB collection
    /// </summary>
    public Task<bool> DeleteDocumentsAsync(string collectionName, List<string> ids)
    {
        using (Py.GIL())
        {
            try
            {
                dynamic collection = _client.get_collection(name: collectionName);
                PyObject pyIds = ConvertListToPyList(ids);
                collection.delete(ids: pyIds);
                
                _logger.LogInformation($"Deleted {ids.Count} documents from collection '{collectionName}'");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting documents from collection '{collectionName}'");
                throw;
            }
        }
    }

    /// <summary>
    /// Gets the document count in a ChromaDB collection
    /// </summary>
    public Task<int> GetCollectionCountAsync(string collectionName)
    {
        using (Py.GIL())
        {
            try
            {
                dynamic collection = _client.get_collection(name: collectionName);
                int count = collection.count();
                return Task.FromResult(count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting count for collection '{collectionName}'");
                throw;
            }
        }
    }

    #region Python Conversion Helpers

    /// <summary>
    /// Converts a C# list to a Python list
    /// </summary>
    private PyObject ConvertListToPyList(List<string> list)
    {
        dynamic pyList = PythonEngine.Eval("[]");
        foreach (var item in list)
        {
            pyList.append(item);
        }
        return pyList;
    }

    /// <summary>
    /// Converts a C# dictionary to a Python dictionary
    /// </summary>
    private PyObject ConvertDictionaryToPyDict(Dictionary<string, object> dict)
    {
        dynamic pyDict = PythonEngine.Eval("{}");
        foreach (var kvp in dict)
        {
            if (kvp.Value is Dictionary<string, object> nestedDict)
            {
                pyDict[kvp.Key] = ConvertDictionaryToPyDict(nestedDict);
            }
            else if (kvp.Value is List<object> listValue)
            {
                dynamic pyList = PythonEngine.Eval("[]");
                foreach (var item in listValue)
                {
                    pyList.append(item);
                }
                pyDict[kvp.Key] = pyList;
            }
            else
            {
                // Handle different value types explicitly
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
                    pyDict[kvp.Key] = boolValue;
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
        }
        return pyDict;
    }

    /// <summary>
    /// Converts a list of metadata dictionaries to Python list
    /// </summary>
    private PyObject ConvertMetadatasToPyList(List<Dictionary<string, object>> metadatas)
    {
        dynamic pyList = PythonEngine.Eval("[]");
        foreach (var metadata in metadatas)
        {
            pyList.append(ConvertDictionaryToPyDict(metadata));
        }
        return pyList;
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
    /// Disposes of the Python resources
    /// </summary>
    public void Dispose()
    {
        ChromaPythonService self = this;
        _logger?.LogInformation($"Attempting chroma service dispose: {_disposed}");
        //await Task.Factory.StartNew(async () => {

            Dispose(true);
            GC.SuppressFinalize(self);
            _logger?.LogInformation("Disposed Chroma Service");
            //await Task.FromResult(true);
            
        //});

        
    }

    /// <summary>
    /// Disposes of the Python resources
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        _logger?.LogInformation($"Starting Chroma Service Disposal {disposing} vs {_disposed}");
        if (!_disposed)
        {
            if (disposing)
            {
                // Production-safe disposal: only cleanup connections, preserve data
                using (Py.GIL())
                {
                    try
                    {
                        if (_client != null)
                        {
                            // Clear system cache to release file handles (but preserve collections/data)
                            try
                            {
                                _client.clear_system_cache();
                                _logger?.LogInformation("Cleared ChromaDB system cache");
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Error clearing ChromaDB system cache");
                            }
                        }
                        else
                        {
                            _logger?.LogWarning("ChromaDB client is null during disposal");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error during ChromaDB cleanup");
                    }
                    finally
                    {
                        // Nullify references
                        _chromadb!.Dispose();
                        _chromadb = null;
                        _client!.Dispose();
                        _client = null;

                        

                    }
                }

                // Force Python garbage collection
                try
                {
                    using (Py.GIL())
                    {
                        using(dynamic gc = Py.Import("gc"))
                        {
                            gc.collect();
                            gc.waitForPendingFinalizers();
                            _logger?.LogInformation("Forced Python garbage collection");
                        }
                    }

                    //lock (_pythonLock)
                    //{
                    //    PythonEngine.Shutdown();
                    //    _pythonInitialized = false;
                    //}
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error forcing Python garbage collection");
                }
            }
            _disposed = true;
        }
    }
}