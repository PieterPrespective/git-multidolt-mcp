using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that adds documents to a ChromaDB collection
/// </summary>
[McpServerToolType]
public class ChromaAddDocumentsTool
{
    private readonly ILogger<ChromaAddDocumentsTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaAddDocumentsTool class
    /// </summary>
    public ChromaAddDocumentsTool(ILogger<ChromaAddDocumentsTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Adds documents to a ChromaDB collection
    /// </summary>
    [McpServerTool]
    [Description("Add documents to a Chroma collection.")]
    public virtual async Task<object> AddDocuments(string collectionName, string documentsJson, string idsJson, string? metadatasJson = null)
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

            if (string.IsNullOrWhiteSpace(documentsJson))
            {
                return new
                {
                    success = false,
                    error = "Documents JSON is required"
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

            _logger.LogInformation($"Adding documents to collection '{collectionName}'");

            List<string> documents;
            List<string> ids;
            List<Dictionary<string, object>>? metadatas = null;

            try
            {
                documents = JsonSerializer.Deserialize<List<string>>(documentsJson) ?? new List<string>();
                ids = JsonSerializer.Deserialize<List<string>>(idsJson) ?? new List<string>();

                if (!string.IsNullOrWhiteSpace(metadatasJson))
                {
                    metadatas = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(metadatasJson);
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

            if (documents.Count != ids.Count)
            {
                return new
                {
                    success = false,
                    error = "Documents and IDs lists must have the same length"
                };
            }

            if (metadatas != null && metadatas.Count != documents.Count)
            {
                return new
                {
                    success = false,
                    error = "Metadatas list must have the same length as documents"
                };
            }

            // PP13-51 FIX: Ensure all documents added via MCP tool are marked as local changes
            // This allows them to be detected during commit operations
            if (metadatas == null)
            {
                metadatas = new List<Dictionary<string, object>>();
                for (int i = 0; i < documents.Count; i++)
                {
                    metadatas.Add(new Dictionary<string, object>());
                }
            }
            
            // Add is_local_change=true to all metadata entries
            foreach (var metadata in metadatas)
            {
                metadata["is_local_change"] = true;
                _logger.LogInformation($"Setting is_local_change=true for document added via MCP tool");
            }

            var result = await _chromaService.AddDocumentsAsync(collectionName, documents, ids, metadatas);

            return new
            {
                success = result,
                message = result ? $"Successfully added {documents.Count} documents to collection '{collectionName}'" : "Failed to add documents"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding documents");
            return new
            {
                success = false,
                error = $"Failed to add documents: {ex.Message}"
            };
        }
    }
}