using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

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
        const string toolName = nameof(ChromaListCollectionsTool);
        const string methodName = nameof(ListCollections);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"limit: {limit}, offset: {offset}");

        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Listing collections with limit={limit}, offset={offset}");

            var collections = await _chromaService.ListCollectionsAsync(limit, offset);

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Retrieved collections: {((collections == null) ? "Null" : string.Join(',', collections.ToArray()))}");

            // Note: Keep empty list instead of adding placeholder for proper JSON format
            var totalCount = collections?.Count ?? 0;
            
            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Successfully listed {totalCount} collections");
            return new
            {
                collections = collections?.ToArray() ?? Array.Empty<string>(),
                total_count = totalCount
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = $"Failed to list collections: {ex.Message}"
            };
        }
    }
}