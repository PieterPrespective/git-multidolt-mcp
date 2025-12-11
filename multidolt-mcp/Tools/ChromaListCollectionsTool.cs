using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that lists all collections in ChromaDB
/// </summary>
[McpServerToolType]
public class ChromaListCollectionsTool
{
    private readonly ILogger<ChromaListCollectionsTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaListCollectionsTool class
    /// </summary>
    public ChromaListCollectionsTool(ILogger<ChromaListCollectionsTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Lists all collection names in the Chroma database with pagination support
    /// </summary>
    [McpServerTool]
    [Description("List all collection names in the Chroma database with pagination support.")]
    public virtual async Task<object> ListCollections(int? limit = null, int? offset = null)
    {
        try
        {
            _logger.LogInformation($"[ChromaListCollectionsTool.ListCollections] Listing collections with limit={limit}, offset={offset}");

            var collections = await _chromaService.ListCollectionsAsync(limit, offset);

            _logger.LogInformation($"[ChromaListCollectionsTool.ListCollections] gotten output: { ((collections == null) ? "Null" : string.Join(',', collections.ToArray())) }");


            if (collections.Count == 0)
            {
                collections.Add("__NO_COLLECTIONS_FOUND__");
            }

            return new
            {
                success = true,
                collections = string.Join("\n", collections)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing collections");
            return new
            {
                success = false,
                error = $"Failed to list collections: {ex.Message}"
            };
        }
    }
}