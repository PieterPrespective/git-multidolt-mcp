using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that queries documents from a ChromaDB collection
/// </summary>
[McpServerToolType]
public class ChromaQueryDocumentsTool
{
    private readonly ILogger<ChromaQueryDocumentsTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaQueryDocumentsTool class
    /// </summary>
    public ChromaQueryDocumentsTool(ILogger<ChromaQueryDocumentsTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Queries documents from a ChromaDB collection using semantic search
    /// </summary>
    [McpServerTool]
    [Description("Query documents from a Chroma collection with advanced filtering.")]
    public virtual async Task<object> QueryDocuments(string collectionName, string queryTextsJson, int nResults = 5, 
        string? whereJson = null, string? whereDocumentJson = null)
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

            if (string.IsNullOrWhiteSpace(queryTextsJson))
            {
                return new
                {
                    success = false,
                    error = "Query texts JSON is required"
                };
            }

            _logger.LogInformation($"Querying collection '{collectionName}'");

            List<string> queryTexts;
            Dictionary<string, object>? where = null;
            Dictionary<string, object>? whereDocument = null;

            try
            {
                queryTexts = JsonSerializer.Deserialize<List<string>>(queryTextsJson) ?? new List<string>();

                if (!string.IsNullOrWhiteSpace(whereJson))
                {
                    where = JsonSerializer.Deserialize<Dictionary<string, object>>(whereJson);
                }

                if (!string.IsNullOrWhiteSpace(whereDocumentJson))
                {
                    whereDocument = JsonSerializer.Deserialize<Dictionary<string, object>>(whereDocumentJson);
                }
            }
            catch (JsonException ex)
            {
                return new
                {
                    success = false,
                    error = $"Invalid JSON format: {ex.Message}"
                };
            }

            if (queryTexts.Count == 0)
            {
                return new
                {
                    success = false,
                    error = "Query texts list cannot be empty"
                };
            }

            var result = await _chromaService.QueryDocumentsAsync(collectionName, queryTexts, nResults, where, whereDocument);

            return new
            {
                success = true,
                result = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying documents");
            return new
            {
                success = false,
                error = $"Failed to query documents: {ex.Message}"
            };
        }
    }
}