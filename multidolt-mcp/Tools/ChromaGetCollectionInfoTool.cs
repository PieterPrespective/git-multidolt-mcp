using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that gets detailed information about a specific ChromaDB collection
/// </summary>
[McpServerToolType]
public class ChromaGetCollectionInfoTool
{
    private readonly ILogger<ChromaGetCollectionInfoTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaGetCollectionInfoTool class
    /// </summary>
    public ChromaGetCollectionInfoTool(ILogger<ChromaGetCollectionInfoTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Get detailed information about a specific collection including its configuration, metadata, and embedding function settings
    /// </summary>
    [McpServerTool]
    [Description("Get detailed information about a specific collection including its configuration, metadata, and embedding function settings.")]
    public virtual async Task<object> GetCollectionInfo(string collection_name)
    {
        const string toolName = nameof(ChromaGetCollectionInfoTool);
        const string methodName = nameof(GetCollectionInfo);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"collection_name: {collection_name}");

        try
        {
            if (string.IsNullOrWhiteSpace(collection_name))
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Collection name is required");
                return new
                {
                    success = false,
                    error = "COLLECTION_NAME_REQUIRED",
                    message = "Collection name is required"
                };
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Getting info for collection: {collection_name}");

            var collection = await _chromaService.GetCollectionAsync(collection_name);
            
            if (collection == null)
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"Collection '{collection_name}' does not exist");
                return new
                {
                    success = false,
                    error = "COLLECTION_NOT_FOUND",
                    message = $"Collection '{collection_name}' does not exist"
                };
            }

            // Get document count
            var documentCount = await _chromaService.GetCollectionCountAsync(collection_name);

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Retrieved info for collection '{collection_name}' with {documentCount} documents");
            return new
            {
                success = true,
                name = collection_name,
                metadata = new Dictionary<string, object>(), // TODO: Extract metadata from collection object
                document_count = documentCount,
                message = $"Collection '{collection_name}' contains {documentCount} documents"
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to get collection info: {ex.Message}"
            };
        }
    }
}