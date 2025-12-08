using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that deletes documents from a ChromaDB collection
/// </summary>
[McpServerToolType]
public class ChromaDeleteDocumentsTool
{
    private readonly ILogger<ChromaDeleteDocumentsTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaDeleteDocumentsTool class
    /// </summary>
    public ChromaDeleteDocumentsTool(ILogger<ChromaDeleteDocumentsTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Deletes specific documents from a ChromaDB collection
    /// </summary>
    [McpServerTool]
    [Description("Delete specific documents from a collection.")]
    public virtual async Task<object> DeleteDocuments(string collectionName, string idsJson)
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

            if (string.IsNullOrWhiteSpace(idsJson))
            {
                return new
                {
                    success = false,
                    error = "IDs JSON is required"
                };
            }

            _logger.LogInformation($"Deleting documents from collection '{collectionName}'");

            List<string> ids;
            try
            {
                ids = JsonSerializer.Deserialize<List<string>>(idsJson) ?? new List<string>();
            }
            catch (JsonException ex)
            {
                return new
                {
                    success = false,
                    error = $"Invalid IDs JSON format: {ex.Message}"
                };
            }

            if (ids.Count == 0)
            {
                return new
                {
                    success = false,
                    error = "IDs list cannot be empty"
                };
            }

            var result = await _chromaService.DeleteDocumentsAsync(collectionName, ids);

            return new
            {
                success = result,
                message = result ? $"Successfully deleted {ids.Count} documents from collection '{collectionName}'" : "Failed to delete documents"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting documents");
            return new
            {
                success = false,
                error = $"Failed to delete documents: {ex.Message}"
            };
        }
    }
}