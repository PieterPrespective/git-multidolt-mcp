using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;

namespace DMMS.Tools;

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
        try
        {
            _logger.LogInformation($"[ChromaUpdateDocumentsTool.UpdateDocuments] Updating {ids.Count} documents in collection: {collection_name}");

            // Validate input
            if (ids == null || ids.Count == 0)
            {
                return new
                {
                    success = false,
                    error = "INVALID_INPUT",
                    message = "Document IDs are required for update"
                };
            }

            // Check if collection exists
            var collection = await _chromaService.GetCollectionAsync(collection_name);
            if (collection == null)
            {
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
                return new
                {
                    success = false,
                    error = "NO_UPDATE_DATA",
                    message = "At least one of documents or metadatas must be provided"
                };
            }

            // Update documents
            await _chromaService.UpdateDocumentsAsync(
                collection_name,
                ids,
                documents: documents,
                metadatas: metadatas
            );

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
            _logger.LogError(ex, $"Error updating documents in '{collection_name}'");
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to update documents: {ex.Message}"
            };
        }
    }
}