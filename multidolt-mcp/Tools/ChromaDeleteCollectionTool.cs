using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that deletes a collection from ChromaDB
/// </summary>
[McpServerToolType]
public class ChromaDeleteCollectionTool
{
    private readonly ILogger<ChromaDeleteCollectionTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaDeleteCollectionTool class
    /// </summary>
    public ChromaDeleteCollectionTool(ILogger<ChromaDeleteCollectionTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Deletes a collection from ChromaDB
    /// </summary>
    [McpServerTool]
    [Description("Delete a collection from ChromaDB.")]
    public virtual async Task<object> DeleteCollection(string collectionName)
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

            _logger.LogInformation($"Deleting collection '{collectionName}'");

            var result = await _chromaService.DeleteCollectionAsync(collectionName);

            return new
            {
                success = result,
                message = result ? $"Successfully deleted collection '{collectionName}'" : "Failed to delete collection"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting collection");
            return new
            {
                success = false,
                error = $"Failed to delete collection: {ex.Message}"
            };
        }
    }
}