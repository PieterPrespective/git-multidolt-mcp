using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that provides a sample of documents from a ChromaDB collection
/// </summary>
[McpServerToolType]
public class ChromaPeekCollectionTool
{
    private readonly ILogger<ChromaPeekCollectionTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaPeekCollectionTool class
    /// </summary>
    public ChromaPeekCollectionTool(ILogger<ChromaPeekCollectionTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// View a sample of documents from a collection. Useful for quickly understanding what kind of content is stored without querying
    /// </summary>
    [McpServerTool]
    [Description("View a sample of documents from a collection. Useful for quickly understanding what kind of content is stored without querying.")]
    public virtual async Task<object> PeekCollection(string collection_name, int limit = 5)
    {
        const string toolName = nameof(ChromaPeekCollectionTool);
        const string methodName = nameof(PeekCollection);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"collection_name: {collection_name}, limit: {limit}");

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

            // Validate limit
            if (limit < 1) limit = 5;
            if (limit > 20) limit = 20;

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Peeking at collection: {collection_name} with limit={limit}");

            // Check if collection exists
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

            // Get sample documents
            var result = await _chromaService.GetDocumentsAsync(
                collection_name,
                ids: null,
                where: null,
                limit: limit
            );

            // Get total count
            var totalCount = await _chromaService.GetCollectionCountAsync(collection_name);

            var documents = new List<object>();
            if (result != null)
            {
                // Since result is object?, we need to handle it dynamically
                try
                {
                    var resultDict = result as IDictionary<string, object>;
                    if (resultDict?.TryGetValue("ids", out var idsObj) == true && idsObj is IList<object> ids)
                    {
                        for (int i = 0; i < ids.Count; i++)
                        {
                            var doc = new Dictionary<string, object>
                            {
                                ["id"] = ids[i]
                            };

                            if (resultDict.TryGetValue("documents", out var docsObj) && docsObj is IList<object> docs && i < docs.Count)
                                doc["document"] = docs[i];
                            
                            if (resultDict.TryGetValue("metadatas", out var metasObj) && metasObj is IList<object> metas && i < metas.Count)
                                doc["metadata"] = metas[i];

                            documents.Add(doc);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ToolLoggingUtility.LogToolWarning(_logger, toolName, "Failed to parse ChromaDB result, treating as empty");
                }
            }

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Successfully peeked collection '{collection_name}', showing {documents.Count} of {totalCount} documents");
            return new
            {
                success = true,
                collection_name = collection_name,
                documents = documents.ToArray(),
                total_in_collection = totalCount,
                message = $"Showing {documents.Count} of {totalCount} documents from collection '{collection_name}'"
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to peek collection: {ex.Message}"
            };
        }
    }
}