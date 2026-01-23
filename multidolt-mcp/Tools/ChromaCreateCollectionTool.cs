using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

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
        const string toolName = nameof(ChromaCreateCollectionTool);
        const string methodName = nameof(CreateCollection);
        
        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, 
                $"Collection: '{collectionName}', EmbeddingFunction: '{embeddingFunctionName}', Metadata: {metadataJson?.Length ?? 0} chars");
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                const string error = "Collection name is required";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
                };
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Creating collection '{collectionName}' with embedding function '{embeddingFunctionName}'");

            Dictionary<string, object>? metadata = null;
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                try
                {
                    metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);
                }
                catch (JsonException ex)
                {
                    var error = $"Invalid metadata JSON: {ex.Message}";
                    ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                    return new
                    {
                        success = false,
                        error = error
                    };
                }
            }

            var startTime = DateTime.UtcNow;
            var result = await _chromaService.CreateCollectionAsync(collectionName, metadata);
            var duration = DateTime.UtcNow - startTime;

            var response = new
            {
                success = result,
                message = result ? $"Successfully created collection '{collectionName}'" : "Failed to create collection"
            };

            if (result)
            {
                var resultMessage = $"Created collection '{collectionName}' in {duration.TotalMilliseconds:F1}ms";
                ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, resultMessage);
            }
            else
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Failed to create collection");
            }
            
            return response;
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = $"Failed to create collection: {ex.Message}"
            };
        }
    }
}