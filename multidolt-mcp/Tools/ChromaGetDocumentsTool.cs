using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that retrieves documents from a ChromaDB collection by ID or filter
/// </summary>
[McpServerToolType]
public class ChromaGetDocumentsTool
{
    private readonly ILogger<ChromaGetDocumentsTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaGetDocumentsTool class
    /// </summary>
    public ChromaGetDocumentsTool(ILogger<ChromaGetDocumentsTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Retrieve documents by their IDs or using metadata filters. Unlike query, this does not use semantic search - it returns exact matches
    /// </summary>
    [McpServerTool]
    [Description("Retrieve documents by their IDs or using metadata filters. Unlike query, this does not use semantic search - it returns exact matches.")]
    public virtual async Task<object> GetDocuments(
        string collection_name,
        List<string>? ids = null,
        Dictionary<string, object>? where = null,
        Dictionary<string, object>? where_document = null,
        List<string>? include = null,
        int limit = 100,
        int offset = 0)
    {
        const string toolName = nameof(ChromaGetDocumentsTool);
        const string methodName = nameof(GetDocuments);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"collection_name: {collection_name}, ids_count: {ids?.Count}, limit: {limit}, offset: {offset}");

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

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Getting documents from collection: {collection_name}");

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

            // Default include fields
            if (include == null || include.Count == 0)
            {
                include = new List<string> { "documents", "metadatas" };
            }

            // Get documents
            var result = await _chromaService.GetDocumentsAsync(
                collection_name,
                ids: ids,
                where: where,
                limit: limit
            );

            var documents = new List<object>();
            if (result != null)
            {
                // Since result is object?, we need to handle it dynamically
                try
                {
                    var resultDict = result as IDictionary<string, object>;
                    if (resultDict?.TryGetValue("ids", out var idsObj) == true && idsObj is IList<object> resultIds)
                    {
                        for (int i = 0; i < resultIds.Count; i++)
                        {
                            var doc = new Dictionary<string, object>
                            {
                                ["id"] = resultIds[i]
                            };

                            if (include.Contains("documents") && resultDict.TryGetValue("documents", out var docsObj) && docsObj is IList<object> docs && i < docs.Count)
                                doc["document"] = docs[i];
                            
                            if (include.Contains("metadatas") && resultDict.TryGetValue("metadatas", out var metasObj) && metasObj is IList<object> metas && i < metas.Count)
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

            // Calculate if there are more results
            var hasMore = documents.Count == limit;

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Retrieved {documents.Count} documents from collection '{collection_name}'");
            return new
            {
                success = true,
                collection_name = collection_name,
                documents = documents.ToArray(),
                total_matching = documents.Count,
                has_more = hasMore,
                message = $"Retrieved {documents.Count} documents from collection '{collection_name}'"
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to get documents: {ex.Message}"
            };
        }
    }
}