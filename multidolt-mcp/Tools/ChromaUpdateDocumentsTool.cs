using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that updates existing documents in a ChromaDB collection
/// </summary>
[McpServerToolType]
public class ChromaUpdateDocumentsTool
{
    private readonly ILogger<ChromaUpdateDocumentsTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaUpdateDocumentsTool class
    /// </summary>
    public ChromaUpdateDocumentsTool(ILogger<ChromaUpdateDocumentsTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Update existing documents' content, metadata, or embeddings. The document must already exist in the collection
    /// </summary>
    [McpServerTool]
    [Description("Update existing documents' content, metadata, or embeddings. The document must already exist in the collection.")]
    public virtual async Task<object> UpdateDocuments(
        string collection_name,
        List<string> ids,
        List<string>? documents = null,
        List<Dictionary<string, object>>? metadatas = null)
    {
        const string toolName = nameof(ChromaUpdateDocumentsTool);
        const string methodName = nameof(UpdateDocuments);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"collection_name: {collection_name}, ids_count: {ids?.Count}, documents_count: {documents?.Count}, metadatas_count: {metadatas?.Count}");

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

            // Validate input
            if (ids == null || ids.Count == 0)
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Document IDs are required for update");
                return new
                {
                    success = false,
                    error = "INVALID_INPUT",
                    message = "Document IDs are required for update"
                };
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Updating {ids.Count} documents in collection: {collection_name}");

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

            // Validate array lengths match
            if ((documents != null && documents.Count != ids.Count) ||
                (metadatas != null && metadatas.Count != ids.Count))
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "All arrays (documents, metadatas) must match the length of ids array");
                return new
                {
                    success = false,
                    error = "LENGTH_MISMATCH",
                    message = "All arrays (documents, metadatas) must match the length of ids array"
                };
            }

            // At least one field must be provided for update
            if (documents == null && metadatas == null)
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "At least one of documents or metadatas must be provided");
                return new
                {
                    success = false,
                    error = "NO_UPDATE_DATA",
                    message = "At least one of documents or metadatas must be provided"
                };
            }

            // Enhance metadata with local change flag and content hash
            if (metadatas == null)
            {
                metadatas = ids.Select(_ => new Dictionary<string, object>()).ToList();
            }

            for (int i = 0; i < metadatas.Count; i++)
            {
                // Set local change flag
                metadatas[i]["is_local_change"] = true;
                
                // Calculate and store content hash if document content is being updated
                if (documents != null && i < documents.Count)
                {
                    var contentHash = DocumentConverterUtilityV2.CalculateContentHash(documents[i]);
                    metadatas[i]["content_hash"] = contentHash;
                    
                    // Log the update for audit trail
                    _logger.LogInformation($"UpdateDocuments: Document {ids[i]} content hash updated to {contentHash}");
                }
                
                // Add update metadata
                metadatas[i]["last_updated"] = DateTime.UtcNow.ToString("O");
                metadatas[i]["update_source"] = "mcp_tool";
            }

            // Update documents (PP13-68-C2: this is user action, keep markAsLocalChange=true)
            await _chromaService.UpdateDocumentsAsync(
                collection_name,
                ids,
                documents: documents,
                metadatas: metadatas,
                markAsLocalChange: true
            );

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Successfully updated {ids.Count} documents in collection '{collection_name}'");
            return new
            {
                success = true,
                collection_name = collection_name,
                documents_updated = ids.Count,
                ids = ids.ToArray(),
                message = $"Successfully updated {ids.Count} documents in collection '{collection_name}'"
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to update documents: {ex.Message}"
            };
        }
    }
}