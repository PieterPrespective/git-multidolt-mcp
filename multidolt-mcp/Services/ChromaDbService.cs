using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;

namespace DMMS.Services;

/// <summary>
/// Service for interacting with ChromaDB via REST API
/// </summary>
public class ChromaDbService : IChromaDbService
{
    private readonly ILogger<ChromaDbService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ServerConfiguration _configuration;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the ChromaDbService
    /// </summary>
    public ChromaDbService(ILogger<ChromaDbService> logger, IOptions<ServerConfiguration> configuration, HttpClient? httpClient = null)
    {
        _logger = logger;
        _configuration = configuration.Value;
        _httpClient = httpClient ?? new HttpClient();
        
        if (httpClient == null)
        {
            _httpClient.BaseAddress = new Uri($"http://{_configuration.ChromaHost}:{_configuration.ChromaPort}");
        }
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    /// <summary>
    /// Lists all collections in ChromaDB
    /// </summary>
    public async Task<List<string>> ListCollectionsAsync(int? limit = null, int? offset = null)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/v1/collections");
            response.EnsureSuccessStatusCode();
            
            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            
            var collections = new List<string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var collection in doc.RootElement.EnumerateArray())
                {
                    if (collection.TryGetProperty("name", out var nameElement))
                    {
                        collections.Add(nameElement.GetString() ?? string.Empty);
                    }
                }
            }
            
            if (offset.HasValue)
                collections = collections.Skip(offset.Value).ToList();
            if (limit.HasValue)
                collections = collections.Take(limit.Value).ToList();
                
            return collections;
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
            var payload = new
            {
                name,
                metadata = metadata ?? new Dictionary<string, object>()
            };
            
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/api/v1/collections", content);
            response.EnsureSuccessStatusCode();
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error creating collection '{name}'");
            throw;
        }
    }

    /// <summary>
    /// Gets information about a collection
    /// </summary>
    public async Task<object?> GetCollectionAsync(string name)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/v1/collections/{name}");
            response.EnsureSuccessStatusCode();
            
            var jsonString = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<object>(jsonString, _jsonOptions);
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
    public async Task<bool> DeleteCollectionAsync(string name)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"/api/v1/collections/{name}");
            response.EnsureSuccessStatusCode();
            return true;
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
            var payload = new
            {
                ids,
                documents,
                metadatas = metadatas ?? documents.Select(_ => new Dictionary<string, object>()).ToList()
            };
            
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync($"/api/v1/collections/{collectionName}/add", content);
            response.EnsureSuccessStatusCode();
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error adding documents to collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Queries documents in a collection
    /// </summary>
    public async Task<object?> QueryDocumentsAsync(string collectionName, List<string> queryTexts, int nResults = 5, 
        Dictionary<string, object>? where = null, Dictionary<string, object>? whereDocument = null)
    {
        try
        {
            var payload = new
            {
                query_texts = queryTexts,
                n_results = nResults,
                where,
                where_document = whereDocument
            };
            
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync($"/api/v1/collections/{collectionName}/query", content);
            response.EnsureSuccessStatusCode();
            
            var jsonString = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<object>(jsonString, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error querying collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Gets documents from a collection
    /// </summary>
    public async Task<object?> GetDocumentsAsync(string collectionName, List<string>? ids = null, 
        Dictionary<string, object>? where = null, int? limit = null)
    {
        try
        {
            var payload = new
            {
                ids,
                where,
                limit
            };
            
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync($"/api/v1/collections/{collectionName}/get", content);
            response.EnsureSuccessStatusCode();
            
            var jsonString = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<object>(jsonString, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting documents from collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Updates documents in a collection
    /// </summary>
    public async Task<bool> UpdateDocumentsAsync(string collectionName, List<string> ids, 
        List<string>? documents = null, List<Dictionary<string, object>>? metadatas = null)
    {
        try
        {
            var payload = new
            {
                ids,
                documents,
                metadatas
            };
            
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync($"/api/v1/collections/{collectionName}/update", content);
            response.EnsureSuccessStatusCode();
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error updating documents in collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Deletes documents from a collection
    /// </summary>
    public async Task<bool> DeleteDocumentsAsync(string collectionName, List<string> ids)
    {
        try
        {
            var payload = new { ids };
            
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/collections/{collectionName}/delete")
            {
                Content = content
            };
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error deleting documents from collection '{collectionName}'");
            throw;
        }
    }

    /// <summary>
    /// Gets the count of documents in a collection
    /// </summary>
    public async Task<int> GetCollectionCountAsync(string collectionName)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/v1/collections/{collectionName}/count");
            response.EnsureSuccessStatusCode();
            
            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            
            if (doc.RootElement.TryGetProperty("count", out var countElement))
            {
                return countElement.GetInt32();
            }
            
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting count for collection '{collectionName}'");
            throw;
        }
    }
}