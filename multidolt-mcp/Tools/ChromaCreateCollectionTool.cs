using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that creates a new collection in ChromaDB
/// </summary>
[McpServerToolType]
public class ChromaCreateCollectionTool
{
    private readonly ILogger<ChromaCreateCollectionTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaCreateCollectionTool class
    /// </summary>
    public ChromaCreateCollectionTool(ILogger<ChromaCreateCollectionTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Creates a new Chroma collection with configurable parameters
    /// </summary>
    [McpServerTool]
    [Description("Create a new Chroma collection with configurable HNSW parameters.")]
    public virtual async Task<object> CreateCollection(string collectionName, string? embeddingFunctionName = "default", string? metadataJson = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                return new
                {
                    success = false,
                    error = "Collection name is required"
                };
            }

            _logger.LogInformation($"Creating collection '{collectionName}' with embedding function '{embeddingFunctionName}'");

            Dictionary<string, object>? metadata = null;
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                try
                {
                    metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);
                }
                catch (JsonException ex)
                {
                    return new
                    {
                        success = false,
                        error = $"Invalid metadata JSON: {ex.Message}"
                    };
                }
            }

            var result = await _chromaService.CreateCollectionAsync(collectionName, metadata);

            return new
            {
                success = result,
                message = result ? $"Successfully created collection '{collectionName}'" : "Failed to create collection"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating collection");
            return new
            {
                success = false,
                error = $"Failed to create collection: {ex.Message}"
            };
        }
    }
}