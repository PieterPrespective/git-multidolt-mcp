using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that gets the document count in a ChromaDB collection
/// </summary>
[McpServerToolType]
public class ChromaGetCollectionCountTool
{
    private readonly ILogger<ChromaGetCollectionCountTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaGetCollectionCountTool class
    /// </summary>
    public ChromaGetCollectionCountTool(ILogger<ChromaGetCollectionCountTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Gets the number of documents in a ChromaDB collection
    /// </summary>
    [McpServerTool]
    [Description("Get the number of documents in a Chroma collection.")]
    public virtual async Task<object> GetCollectionCount(string collectionName)
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

            _logger.LogInformation($"Getting document count for collection '{collectionName}'");

            var count = await _chromaService.GetCollectionCountAsync(collectionName);

            return new
            {
                success = true,
                count = count.ToString()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting collection count");
            return new
            {
                success = false,
                error = $"Failed to get collection count: {ex.Message}"
            };
        }
    }
}