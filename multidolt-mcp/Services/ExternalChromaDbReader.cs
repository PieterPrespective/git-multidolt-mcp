using Microsoft.Extensions.Logging;
using DMMS.Models;
using DMMS.Utilities;
using Python.Runtime;
using System.Collections.Concurrent;

namespace DMMS.Services
{
    /// <summary>
    /// Service for reading from external ChromaDB databases (read-only access).
    /// Uses Python.NET with PersistentClient to access external database files.
    /// Maintains a cache of clients to avoid repeated initialization overhead.
    /// </summary>
    public class ExternalChromaDbReader : IExternalChromaDbReader, IDisposable
    {
        private readonly ILogger<ExternalChromaDbReader> _logger;
        private readonly ConcurrentDictionary<string, string> _externalClientIds = new();
        private bool _disposed = false;

        /// <summary>
        /// Initializes a new instance of ExternalChromaDbReader
        /// </summary>
        /// <param name="logger">Logger for diagnostic information</param>
        public ExternalChromaDbReader(ILogger<ExternalChromaDbReader> logger)
        {
            _logger = logger;
            _logger.LogInformation("ExternalChromaDbReader initialized");
        }

        /// <inheritdoc />
        public async Task<ExternalDbValidationResult> ValidateExternalDbAsync(string dbPath)
        {
            _logger.LogInformation("Validating external ChromaDB at path: {Path}", dbPath);

            try
            {
                // Check if path exists
                if (!Directory.Exists(dbPath))
                {
                    return new ExternalDbValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Directory does not exist: {dbPath}",
                        DbPath = dbPath
                    };
                }

                // Check for chroma.sqlite3 file
                var sqlitePath = Path.Combine(dbPath, "chroma.sqlite3");
                if (!File.Exists(sqlitePath))
                {
                    return new ExternalDbValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Not a valid ChromaDB database (missing chroma.sqlite3): {dbPath}",
                        DbPath = dbPath
                    };
                }

                // Try to initialize a client and get collection info
                var result = await PythonContext.ExecuteAsync(() =>
                {
                    var clientId = GetOrCreateExternalClientId(dbPath);
                    dynamic client = ChromaClientPool.GetOrCreateClient(clientId, $"persistent:{dbPath}");

                    // List collections
                    dynamic collections = client.list_collections();
                    var collectionCount = 0;
                    long totalDocuments = 0;

                    foreach (dynamic collection in collections)
                    {
                        collectionCount++;
                        totalDocuments += collection.count();
                    }

                    return new ExternalDbValidationResult
                    {
                        IsValid = true,
                        DbPath = dbPath,
                        CollectionCount = collectionCount,
                        TotalDocuments = totalDocuments
                    };
                }, timeoutMs: 30000, operationName: $"ValidateExternalDb_{Path.GetFileName(dbPath)}");

                _logger.LogInformation("External database validation successful: {CollectionCount} collections, {TotalDocuments} documents",
                    result.CollectionCount, result.TotalDocuments);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate external ChromaDB at path: {Path}", dbPath);
                return new ExternalDbValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Failed to access database: {ex.Message}",
                    DbPath = dbPath
                };
            }
        }

        /// <inheritdoc />
        public async Task<List<ExternalCollectionInfo>> ListExternalCollectionsAsync(string dbPath)
        {
            _logger.LogInformation("Listing collections in external ChromaDB: {Path}", dbPath);

            return await PythonContext.ExecuteAsync(() =>
            {
                var result = new List<ExternalCollectionInfo>();
                var clientId = GetOrCreateExternalClientId(dbPath);
                dynamic client = ChromaClientPool.GetOrCreateClient(clientId, $"persistent:{dbPath}");

                dynamic collections = client.list_collections();
                foreach (dynamic collection in collections)
                {
                    var name = collection.name.ToString();
                    var count = (int)collection.count();
                    var metadata = ConvertPyDictToDictionary(collection.metadata);

                    result.Add(new ExternalCollectionInfo
                    {
                        Name = name,
                        DocumentCount = count,
                        Metadata = metadata
                    });
                }

                _logger.LogInformation("Found {Count} collections in external database", result.Count);
                return result;
            }, timeoutMs: 30000, operationName: $"ListExternalCollections_{Path.GetFileName(dbPath)}");
        }

        /// <inheritdoc />
        public async Task<List<string>> ListMatchingCollectionsAsync(string dbPath, string pattern)
        {
            _logger.LogInformation("Listing collections matching pattern '{Pattern}' in external ChromaDB: {Path}", pattern, dbPath);

            var allCollections = await ListExternalCollectionsAsync(dbPath);
            var collectionNames = allCollections.Select(c => c.Name).ToList();

            if (!WildcardMatcher.HasWildcard(pattern))
            {
                // Exact match
                return collectionNames.Contains(pattern) ? new List<string> { pattern } : new List<string>();
            }

            // Wildcard match
            var matches = WildcardMatcher.GetMatches(pattern, collectionNames);
            _logger.LogInformation("Pattern '{Pattern}' matched {Count} collections", pattern, matches.Count);
            return matches;
        }

        /// <inheritdoc />
        public async Task<List<ExternalDocument>> GetExternalDocumentsAsync(
            string dbPath,
            string collectionName,
            List<string>? documentIdPatterns = null)
        {
            _logger.LogInformation("Getting documents from external collection '{Collection}' in {Path}", collectionName, dbPath);

            return await PythonContext.ExecuteAsync(() =>
            {
                var result = new List<ExternalDocument>();
                var clientId = GetOrCreateExternalClientId(dbPath);
                dynamic client = ChromaClientPool.GetOrCreateClient(clientId, $"persistent:{dbPath}");

                dynamic collection;
                try
                {
                    collection = client.get_collection(name: collectionName);
                }
                catch (PythonException)
                {
                    _logger.LogWarning("Collection '{Collection}' not found in external database", collectionName);
                    return result;
                }

                // Get all documents from the collection
                dynamic allDocs = collection.get();

                var ids = ConvertPyListToStringList(allDocs["ids"]);
                var documents = allDocs["documents"] != null ? ConvertPyListToStringList(allDocs["documents"]) : new List<string>();
                var metadatas = allDocs["metadatas"] != null ? ConvertPyListToMetadatasList(allDocs["metadatas"]) : new List<Dictionary<string, object>>();

                // Process each document
                for (int i = 0; i < ids.Count; i++)
                {
                    var docId = ids[i];
                    var content = i < documents.Count ? documents[i] : string.Empty;
                    var metadata = i < metadatas.Count ? metadatas[i] : new Dictionary<string, object>();

                    // Apply document ID filtering if patterns are provided
                    if (documentIdPatterns != null && documentIdPatterns.Count > 0)
                    {
                        bool matches = false;
                        foreach (var pattern in documentIdPatterns)
                        {
                            if (WildcardMatcher.IsMatch(pattern, docId))
                            {
                                matches = true;
                                break;
                            }
                        }
                        if (!matches) continue;
                    }

                    // Compute content hash for conflict detection
                    var contentHash = ImportUtility.ComputeContentHash(content);

                    result.Add(new ExternalDocument
                    {
                        DocId = docId,
                        CollectionName = collectionName,
                        Content = content,
                        ContentHash = contentHash,
                        Metadata = metadata
                    });
                }

                _logger.LogInformation("Retrieved {Count} documents from external collection '{Collection}'", result.Count, collectionName);
                return result;
            }, timeoutMs: 60000, operationName: $"GetExternalDocs_{collectionName}");
        }

        /// <inheritdoc />
        public async Task<Dictionary<string, object>?> GetExternalCollectionMetadataAsync(string dbPath, string collectionName)
        {
            _logger.LogInformation("Getting metadata for external collection '{Collection}' in {Path}", collectionName, dbPath);

            return await PythonContext.ExecuteAsync(() =>
            {
                var clientId = GetOrCreateExternalClientId(dbPath);
                dynamic client = ChromaClientPool.GetOrCreateClient(clientId, $"persistent:{dbPath}");

                try
                {
                    dynamic collection = client.get_collection(name: collectionName);
                    return ConvertPyDictToDictionary(collection.metadata);
                }
                catch (PythonException)
                {
                    _logger.LogWarning("Collection '{Collection}' not found in external database", collectionName);
                    return null;
                }
            }, timeoutMs: 30000, operationName: $"GetExternalCollectionMetadata_{collectionName}");
        }

        /// <inheritdoc />
        public async Task<int> GetExternalCollectionCountAsync(string dbPath, string collectionName)
        {
            _logger.LogInformation("Getting document count for external collection '{Collection}' in {Path}", collectionName, dbPath);

            return await PythonContext.ExecuteAsync(() =>
            {
                var clientId = GetOrCreateExternalClientId(dbPath);
                dynamic client = ChromaClientPool.GetOrCreateClient(clientId, $"persistent:{dbPath}");

                try
                {
                    dynamic collection = client.get_collection(name: collectionName);
                    return (int)collection.count();
                }
                catch (PythonException)
                {
                    _logger.LogWarning("Collection '{Collection}' not found in external database", collectionName);
                    return 0;
                }
            }, timeoutMs: 30000, operationName: $"GetExternalCollectionCount_{collectionName}");
        }

        /// <inheritdoc />
        public async Task<bool> CollectionExistsAsync(string dbPath, string collectionName)
        {
            _logger.LogInformation("Checking if collection '{Collection}' exists in external database {Path}", collectionName, dbPath);

            return await PythonContext.ExecuteAsync(() =>
            {
                var clientId = GetOrCreateExternalClientId(dbPath);
                dynamic client = ChromaClientPool.GetOrCreateClient(clientId, $"persistent:{dbPath}");

                try
                {
                    client.get_collection(name: collectionName);
                    return true;
                }
                catch (PythonException)
                {
                    return false;
                }
            }, timeoutMs: 30000, operationName: $"CollectionExists_{collectionName}");
        }

        #region Helper Methods

        /// <summary>
        /// Gets or creates a unique client ID for the external database path.
        /// Uses a stable hash of the path to allow client reuse.
        /// </summary>
        /// <param name="dbPath">Path to the external database</param>
        /// <returns>Client ID for the ChromaClientPool</returns>
        private string GetOrCreateExternalClientId(string dbPath)
        {
            var normalizedPath = Path.GetFullPath(dbPath).ToLowerInvariant();

            return _externalClientIds.GetOrAdd(normalizedPath, _ =>
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                var pathHash = ImportUtility.ComputeContentHash(normalizedPath)[..8];
                return $"ExternalChromaDb_{pathHash}_{timestamp}";
            });
        }

        /// <summary>
        /// Converts a Python dictionary to a C# dictionary
        /// </summary>
        private Dictionary<string, object> ConvertPyDictToDictionary(dynamic pyDict)
        {
            var result = new Dictionary<string, object>();
            if (pyDict == null) return result;

            try
            {
                foreach (var key in pyDict)
                {
                    string keyStr = key.ToString();
                    var value = pyDict[key];

                    if (value is PyObject pyObj)
                    {
                        var pyType = pyObj.GetPythonType();
                        var typeName = pyType.Name.ToString();

                        if (typeName.Equals("bool"))
                        {
                            result[keyStr] = value.ToString().Equals("True", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (typeName.Equals("int"))
                        {
                            if (long.TryParse(value.ToString(), out long longVal))
                            {
                                result[keyStr] = longVal >= int.MinValue && longVal <= int.MaxValue ? (int)longVal : longVal;
                            }
                            else
                            {
                                result[keyStr] = value.ToString();
                            }
                        }
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error converting Python dictionary to C# dictionary");
            }

            return result;
        }

        /// <summary>
        /// Converts a Python list to a C# list of strings
        /// </summary>
        private List<string> ConvertPyListToStringList(dynamic pyList)
        {
            var result = new List<string>();
            if (pyList == null) return result;

            try
            {
                foreach (var item in pyList)
                {
                    result.Add(item?.ToString() ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error converting Python list to C# string list");
            }

            return result;
        }

        /// <summary>
        /// Converts a Python list of metadata dictionaries to C# list
        /// </summary>
        private List<Dictionary<string, object>> ConvertPyListToMetadatasList(dynamic pyList)
        {
            var result = new List<Dictionary<string, object>>();
            if (pyList == null) return result;

            try
            {
                foreach (var item in pyList)
                {
                    if (item == null)
                    {
                        result.Add(new Dictionary<string, object>());
                    }
                    else
                    {
                        result.Add(ConvertPyDictToDictionary(item));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error converting Python metadata list to C# list");
            }

            return result;
        }

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Disposes of all external client connections
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected disposal implementation
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _logger.LogInformation("Disposing ExternalChromaDbReader with {Count} cached clients", _externalClientIds.Count);

                    // Dispose all cached external clients
                    foreach (var clientId in _externalClientIds.Values)
                    {
                        try
                        {
                            ChromaClientPool.DisposeClient(clientId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error disposing external client {ClientId}", clientId);
                        }
                    }

                    _externalClientIds.Clear();
                    _logger.LogInformation("ExternalChromaDbReader disposed successfully");
                }

                _disposed = true;
            }
        }

        #endregion
    }
}
