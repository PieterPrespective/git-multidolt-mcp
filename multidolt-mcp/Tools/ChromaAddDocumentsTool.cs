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

            // PP13-56-C1: Enhanced logging for audit and debugging purposes
            _logger.LogInformation($"[ChromaAddDocumentsTool] User request: Adding {documentsJson?.Length ?? 0} chars of documents JSON and {idsJson?.Length ?? 0} chars of IDs JSON to collection '{collectionName}'");
            _logger.LogInformation($"[ChromaAddDocumentsTool] Metadata provided: {!string.IsNullOrWhiteSpace(metadatasJson)}, metadata length: {metadatasJson?.Length ?? 0} chars");

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
                
                // PP13-56-C1: Log document details for debugging and audit
                _logger.LogInformation($"[ChromaAddDocumentsTool] Parsed {documents.Count} documents and {ids.Count} IDs");
                if (metadatas != null)
                {
                    _logger.LogInformation($"[ChromaAddDocumentsTool] Parsed {metadatas.Count} metadata entries");
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
            
            // PP13-56-C1: Add is_local_change=true to all metadata entries with enhanced logging
            _logger.LogInformation($"[ChromaAddDocumentsTool] Processing {metadatas.Count} metadata entries for local change marking");
            for (int i = 0; i < metadatas.Count; i++)
            {
                var metadata = metadatas[i];
                var documentId = i < ids.Count ? ids[i] : "unknown";
                var contentLength = i < documents.Count ? documents[i].Length : 0;
                
                metadata["is_local_change"] = true;
                
                // Enhanced logging with document details
                _logger.LogInformation($"[ChromaAddDocumentsTool] Document {i + 1}/{documents.Count}: ID='{documentId}', Content Length={contentLength} chars, Metadata Keys=[{string.Join(", ", metadata.Keys)}], is_local_change=true");
                
                // Log metadata content for debugging (but limit size for readability)
                var metadataJson = JsonSerializer.Serialize(metadata);
                var truncatedMetadata = metadataJson.Length > 200 ? metadataJson.Substring(0, 200) + "..." : metadataJson;
                _logger.LogDebug($"[ChromaAddDocumentsTool] Document '{documentId}' metadata: {truncatedMetadata}");
            }

            // PP13-56-C1: Log operation attempt and result
            _logger.LogInformation($"[ChromaAddDocumentsTool] Attempting to add {documents.Count} documents to collection '{collectionName}' via ChromaDB service");
            
            var startTime = DateTime.UtcNow;
            var result = await _chromaService.AddDocumentsAsync(collectionName, documents, ids, metadatas);
            var duration = DateTime.UtcNow - startTime;
            
            // Enhanced result logging
            if (result)
            {
                _logger.LogInformation($"[ChromaAddDocumentsTool] ✓ Successfully added {documents.Count} documents to collection '{collectionName}' in {duration.TotalMilliseconds:F1}ms");
                _logger.LogInformation($"[ChromaAddDocumentsTool] Document IDs added: [{string.Join(", ", ids.Take(10))}{(ids.Count > 10 ? $" and {ids.Count - 10} more..." : "")}]");
            }
            else
            {
                _logger.LogError($"[ChromaAddDocumentsTool] ❌ Failed to add {documents.Count} documents to collection '{collectionName}' after {duration.TotalMilliseconds:F1}ms");
            }

            return new
            {
                success = result,
                message = result ? $"Successfully added {documents.Count} documents to collection '{collectionName}' in {duration.TotalMilliseconds:F1}ms" : "Failed to add documents",
                document_count = documents.Count,
                collection_name = collectionName,
                duration_ms = duration.TotalMilliseconds,
                document_ids = ids.Take(5).ToArray() // Include first 5 IDs in response
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