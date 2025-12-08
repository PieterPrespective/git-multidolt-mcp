using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;

namespace DMMS.Services;

/// <summary>
/// Persistent ChromaDB service using local data directory (file-based storage)
/// </summary>
public class ChromaPersistentDbService : IChromaDbService
{
    private readonly ILogger<ChromaPersistentDbService> _logger;
    private readonly ServerConfiguration _configuration;
    private readonly string _dataPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly JsonSerializerOptions _jsonlOptions;

    /// <summary>
    /// Initializes a new instance of ChromaPersistentDbService
    /// </summary>
    public ChromaPersistentDbService(ILogger<ChromaPersistentDbService> logger, IOptions<ServerConfiguration> configuration)
    {
        _logger = logger;
        _configuration = configuration.Value;
        _dataPath = Path.GetFullPath(_configuration.ChromaDataPath);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        _jsonlOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false  // JSONL requires single-line JSON
        };

        // Ensure data directory exists
        Directory.CreateDirectory(_dataPath);
        _logger.LogInformation($"Initialized persistent ChromaDB at: {_dataPath}");
    }

    /// <summary>
    /// Lists all collections
    /// </summary>
    public Task<List<string>> ListCollectionsAsync(int? limit = null, int? offset = null)
    {
        try
        {
            var collections = Directory.GetDirectories(_dataPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .ToList();

            if (offset.HasValue)
                collections = collections.Skip(offset.Value).ToList();
            if (limit.HasValue)
                collections = collections.Take(limit.Value).ToList();

            return Task.FromResult(collections);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing collections");
            throw;
        }
    }

    /// <summary>
    /// Creates a new collection
    /// </summary>
    public async Task<bool> CreateCollectionAsync(string name, Dictionary<string, object>? metadata = null)
    {
        try
        {
            var collectionPath = Path.Combine(_dataPath, name);
            if (Directory.Exists(collectionPath))
            {
                throw new InvalidOperationException($"Collection '{name}' already exists");
            }

            Directory.CreateDirectory(collectionPath);

            // Store collection metadata
            var metadataPath = Path.Combine(collectionPath, "metadata.json");
            var collectionInfo = new
            {
                name,
                metadata = metadata ?? new Dictionary<string, object>(),
                created_at = DateTime.UtcNow.ToString("O"),
                document_count = 0
            };

            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(collectionInfo, _jsonOptions));
            _logger.LogInformation($"Created collection '{name}' at {collectionPath}");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error creating collection '{name}'");
            throw;
        }
    }

    /// <summary>
    /// Gets collection information
    /// </summary>
    public async Task<object?> GetCollectionAsync(string name)
    {
        try
        {
            var metadataPath = Path.Combine(_dataPath, name, "metadata.json");
            if (!File.Exists(metadataPath))
            {
                throw new InvalidOperationException($"Collection '{name}' does not exist");
            }

            var metadataJson = await File.ReadAllTextAsync(metadataPath);
            return JsonSerializer.Deserialize<object>(metadataJson, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting collection '{name}'");
            throw;
        }
    }

    /// <summary>
    /// Deletes a collection
    /// </summary>
    public Task<bool> DeleteCollectionAsync(string name)
    {
        try
        {
            var collectionPath = Path.Combine(_dataPath, name);
            if (!Directory.Exists(collectionPath))
            {
                throw new InvalidOperationException($"Collection '{name}' does not exist");
            }

            Directory.Delete(collectionPath, recursive: true);
            _logger.LogInformation($"Deleted collection '{name}'");

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error deleting collection '{name}'");
            throw;
        }
    }

    /// <summary>
    /// Adds documents to a collection
    /// </summary>
    public async Task<bool> AddDocumentsAsync(string collectionName, List<string> documents, List<string> ids, List<Dictionary<string, object>>? metadatas = null)
    {
        try
        {
            var collectionPath = Path.Combine(_dataPath, collectionName);
            if (!Directory.Exists(collectionPath))
            {
                throw new InvalidOperationException($"Collection '{collectionName}' does not exist");
            }

            var documentsPath = Path.Combine(collectionPath, "documents.jsonl");

            // Read existing document count for ID validation
            var existingIds = new HashSet<string>();
            if (File.Exists(documentsPath))
            {
                var lines = await File.ReadAllLinesAsync(documentsPath);
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        try
                        {
                            var doc = JsonSerializer.Deserialize<JsonElement>(line, _jsonlOptions);
                            if (doc.TryGetProperty("id", out var idProp))
                            {
                                existingIds.Add(idProp.GetString() ?? "");
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning("Skipping malformed JSON line during ID check: {Line}, Error: {Message}", line, ex.Message);
                        }
                    }
                }
            }

            // Check for duplicate IDs
            var duplicates = ids.Where(id => existingIds.Contains(id)).ToList();
            if (duplicates.Any())
            {
                throw new InvalidOperationException($"Documents with IDs already exist: {string.Join(", ", duplicates)}");
            }

            // Append new documents to JSONL file
            using (var writer = new StreamWriter(documentsPath, append: true))
            {
                for (int i = 0; i < documents.Count; i++)
                {
                    var document = new
                    {
                        id = ids[i],
                        document = documents[i],
                        metadata = metadatas?[i] ?? new Dictionary<string, object>(),
                        created_at = DateTime.UtcNow.ToString("O")
                    };

                    await writer.WriteLineAsync(JsonSerializer.Serialize(document, _jsonlOptions));
                }
                await writer.FlushAsync();
            }

            // Small delay to ensure file is released
            await Task.Delay(10);
            
            // Update collection metadata
            await UpdateCollectionMetadata(collectionName);

            _logger.LogInformation($"Added {documents.Count} documents to collection '{collectionName}'");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error adding documents to collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Queries documents (simple text search - no embeddings in this implementation)
    /// </summary>
    public async Task<object?> QueryDocumentsAsync(string collectionName, List<string> queryTexts, int nResults = 5,
        Dictionary<string, object>? where = null, Dictionary<string, object>? whereDocument = null)
    {
        try
        {
            var documents = await LoadDocumentsAsync(collectionName);
            var allIds = new List<List<string>>();
            var allDocuments = new List<List<string>>();
            var allMetadatas = new List<List<Dictionary<string, object>>>();
            var allDistances = new List<List<double>>();

            foreach (var queryText in queryTexts)
            {
                var matches = documents
                    .Where(doc => doc.Document.Contains(queryText, StringComparison.OrdinalIgnoreCase))
                    .Take(nResults)
                    .ToList();

                allIds.Add(matches.Select(doc => doc.Id).ToList());
                allDocuments.Add(matches.Select(doc => doc.Document).ToList());
                allMetadatas.Add(matches.Select(doc => doc.Metadata).ToList());
                allDistances.Add(matches.Select(doc => CalculateSimpleDistance(queryText, doc.Document)).ToList());
            }

            return new
            {
                ids = allIds,
                documents = allDocuments,
                metadatas = allMetadatas,
                distances = allDistances
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error querying collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Gets documents from collection
    /// </summary>
    public async Task<object?> GetDocumentsAsync(string collectionName, List<string>? ids = null,
        Dictionary<string, object>? where = null, int? limit = null)
    {
        try
        {
            var documents = await LoadDocumentsAsync(collectionName);

            if (ids != null && ids.Any())
            {
                documents = documents.Where(doc => ids.Contains(doc.Id)).ToList();
            }

            if (limit.HasValue)
            {
                documents = documents.Take(limit.Value).ToList();
            }

            return new
            {
                ids = documents.Select(d => d.Id).ToList(),
                documents = documents.Select(d => d.Document).ToList(),
                metadatas = documents.Select(d => d.Metadata).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting documents from collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Updates documents in collection
    /// </summary>
    public async Task<bool> UpdateDocumentsAsync(string collectionName, List<string> ids,
        List<string>? documents = null, List<Dictionary<string, object>>? metadatas = null)
    {
        try
        {
            var existingDocs = await LoadDocumentsAsync(collectionName);
            var updatedDocs = new List<DocumentRecord>();

            foreach (var doc in existingDocs)
            {
                var updateIndex = ids.IndexOf(doc.Id);
                if (updateIndex >= 0)
                {
                    updatedDocs.Add(new DocumentRecord
                    {
                        Id = doc.Id,
                        Document = documents?[updateIndex] ?? doc.Document,
                        Metadata = metadatas?[updateIndex] ?? doc.Metadata,
                        CreatedAt = doc.CreatedAt,
                        UpdatedAt = DateTime.UtcNow.ToString("O")
                    });
                }
                else
                {
                    updatedDocs.Add(doc);
                }
            }

            await SaveDocumentsAsync(collectionName, updatedDocs);
            _logger.LogInformation($"Updated {ids.Count} documents in collection '{collectionName}'");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error updating documents in collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Deletes documents from collection
    /// </summary>
    public async Task<bool> DeleteDocumentsAsync(string collectionName, List<string> ids)
    {
        try
        {
            var existingDocs = await LoadDocumentsAsync(collectionName);
            var filteredDocs = existingDocs.Where(doc => !ids.Contains(doc.Id)).ToList();

            await SaveDocumentsAsync(collectionName, filteredDocs);
            await UpdateCollectionMetadata(collectionName);

            _logger.LogInformation($"Deleted {ids.Count} documents from collection '{collectionName}'");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error deleting documents from collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Gets collection document count
    /// </summary>
    public async Task<int> GetCollectionCountAsync(string collectionName)
    {
        try
        {
            var documents = await LoadDocumentsAsync(collectionName);
            return documents.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting count for collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Loads all documents from a collection
    /// </summary>
    private async Task<List<DocumentRecord>> LoadDocumentsAsync(string collectionName)
    {
        var documentsPath = Path.Combine(_dataPath, collectionName, "documents.jsonl");
        if (!File.Exists(documentsPath))
        {
            return new List<DocumentRecord>();
        }

        var documents = new List<DocumentRecord>();
        var lines = await File.ReadAllLinesAsync(documentsPath);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(line, _jsonlOptions);
                documents.Add(new DocumentRecord
                {
                    Id = doc.GetProperty("id").GetString() ?? "",
                    Document = doc.GetProperty("document").GetString() ?? "",
                    Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        doc.GetProperty("metadata").GetRawText(), _jsonlOptions) ?? new Dictionary<string, object>(),
                    CreatedAt = doc.TryGetProperty("created_at", out var created) ? created.GetString() ?? "" : "",
                    UpdatedAt = doc.TryGetProperty("updated_at", out var updated) ? updated.GetString() : null
                });
            }
            catch (JsonException ex)
            {
                // Skip malformed JSON lines and log the error
                _logger.LogWarning("Skipping malformed JSON line in collection {CollectionName}: {Line}, Error: {Message}", 
                    collectionName, line, ex.Message);
            }
        }

        return documents;
    }

    /// <summary>
    /// Saves documents to collection file
    /// </summary>
    private async Task SaveDocumentsAsync(string collectionName, List<DocumentRecord> documents)
    {
        var documentsPath = Path.Combine(_dataPath, collectionName, "documents.jsonl");
        using (var writer = new StreamWriter(documentsPath, append: false))
        {
            foreach (var doc in documents)
            {
                var document = new
                {
                    id = doc.Id,
                    document = doc.Document,
                    metadata = doc.Metadata,
                    created_at = doc.CreatedAt,
                    updated_at = doc.UpdatedAt
                };

                await writer.WriteLineAsync(JsonSerializer.Serialize(document, _jsonlOptions));
            }
            await writer.FlushAsync();
        }
    }

    /// <summary>
    /// Updates collection metadata with current document count
    /// </summary>
    private async Task UpdateCollectionMetadata(string collectionName)
    {
        var metadataPath = Path.Combine(_dataPath, collectionName, "metadata.json");
        if (!File.Exists(metadataPath)) return;

        var metadataJson = await File.ReadAllTextAsync(metadataPath);
        var metadata = JsonSerializer.Deserialize<JsonElement>(metadataJson);

        var documents = await LoadDocumentsAsync(collectionName);
        var updatedMetadata = new
        {
            name = metadata.GetProperty("name").GetString(),
            metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(
                metadata.GetProperty("metadata").GetRawText()),
            created_at = metadata.GetProperty("created_at").GetString(),
            document_count = documents.Count,
            last_updated = DateTime.UtcNow.ToString("O")
        };

        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(updatedMetadata, _jsonOptions));
    }

    /// <summary>
    /// Simple text similarity calculation (placeholder for real embedding similarity)
    /// </summary>
    private static double CalculateSimpleDistance(string query, string document)
    {
        var queryWords = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var docWords = document.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var commonWords = queryWords.Intersect(docWords).Count();
        
        return commonWords > 0 ? 1.0 / (1.0 + commonWords) : 1.0;
    }

    /// <summary>
    /// Document record structure
    /// </summary>
    private class DocumentRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Document { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = new();
        public string CreatedAt { get; set; } = string.Empty;
        public string? UpdatedAt { get; set; }
    }
}