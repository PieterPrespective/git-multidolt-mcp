using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

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
        const string toolName = nameof(ChromaGetCollectionCountTool);
        const string methodName = nameof(GetCollectionCount);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"collectionName: {collectionName}");

        try
        {
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Collection name is required");
                return new
                {
                    success = false,
                    error = "Collection name is required"
                };
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Getting document count for collection '{collectionName}'");

            var count = await _chromaService.GetCollectionCountAsync(collectionName);

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Retrieved count: {count} for collection '{collectionName}'");
            return new
            {
                success = true,
                count = count.ToString()
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = $"Failed to get collection count: {ex.Message}"
            };
        }
    }
}