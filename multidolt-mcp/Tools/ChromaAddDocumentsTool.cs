using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

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
        const string toolName = nameof(ChromaAddDocumentsTool);
        const string methodName = nameof(AddDocuments);
        
        try
        {
            // PP13-59: Safe logging implementation with standardized tool entry logging
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, 
                $"Collection: '{collectionName}', Documents: {documentsJson?.Length ?? 0} chars, IDs: {idsJson?.Length ?? 0} chars, Metadata: {metadatasJson?.Length ?? 0} chars");

            if (string.IsNullOrWhiteSpace(collectionName))
            {
                const string error = "Collection name is required";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
                };
            }

            if (string.IsNullOrWhiteSpace(documentsJson))
            {
                const string error = "Documents JSON is required";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
                };
            }

            if (string.IsNullOrWhiteSpace(idsJson))
            {
                const string error = "IDs JSON is required";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
                };
            }

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
                ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Parsed {documents.Count} documents and {ids.Count} IDs");
                if (metadatas != null)
                {
                    ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Parsed {metadatas.Count} metadata entries");
                }
            }
            catch (JsonException ex)
            {
                var error = $"Invalid JSON format: {ex.Message}";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
                };
            }

            if (documents.Count != ids.Count)
            {
                const string error = "Documents and IDs lists must have the same length";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
                };
            }

            if (metadatas != null && metadatas.Count != documents.Count)
            {
                const string error = "Metadatas list must have the same length as documents";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
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
            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Processing {metadatas.Count} metadata entries for local change marking");
            for (int i = 0; i < metadatas.Count; i++)
            {
                var metadata = metadatas[i];
                var documentId = i < ids.Count ? ids[i] : "unknown";
                var contentLength = i < documents.Count ? documents[i].Length : 0;
                
                metadata["is_local_change"] = true;
                
                // Enhanced logging with document details
                ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Document {i + 1}/{documents.Count}: ID='{documentId}', Content Length={contentLength} chars, Metadata Keys=[{string.Join(", ", metadata.Keys)}], is_local_change=true");
                
                // Log metadata content for debugging (but limit size for readability)
                var metadataJson = JsonSerializer.Serialize(metadata);
                var truncatedMetadata = metadataJson.Length > 200 ? metadataJson.Substring(0, 200) + "..." : metadataJson;
                ToolLoggingUtility.LogToolDebug(_logger, toolName, $"Document '{documentId}' metadata: {truncatedMetadata}");
            }

            // PP13-56-C1: Log operation attempt and result
            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Attempting to add {documents.Count} documents to collection '{collectionName}' via ChromaDB service");
            
            var startTime = DateTime.UtcNow;
            var result = await _chromaService.AddDocumentsAsync(collectionName, documents, ids, metadatas);
            var duration = DateTime.UtcNow - startTime;
            
            // Enhanced result logging
            if (result)
            {
                ToolLoggingUtility.LogToolInfo(_logger, toolName, $"✓ Successfully added {documents.Count} documents to collection '{collectionName}' in {duration.TotalMilliseconds:F1}ms");
                ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Document IDs added: [{string.Join(", ", ids.Take(10))}{(ids.Count > 10 ? $" and {ids.Count - 10} more..." : "")}]");
            }
            else
            {
                ToolLoggingUtility.LogToolError(_logger, toolName, $"❌ Failed to add {documents.Count} documents to collection '{collectionName}' after {duration.TotalMilliseconds:F1}ms");
            }

            var response = new
            {
                success = result,
                message = result ? $"Successfully added {documents.Count} documents to collection '{collectionName}' in {duration.TotalMilliseconds:F1}ms" : "Failed to add documents",
                document_count = documents.Count,
                collection_name = collectionName,
                duration_ms = duration.TotalMilliseconds,
                document_ids = ids.Take(5).ToArray() // Include first 5 IDs in response
            };

            var resultMessage = $"Added {documents.Count} documents to '{collectionName}' in {duration.TotalMilliseconds:F1}ms";
            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, resultMessage);
            return response;
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = $"Failed to add documents: {ex.Message}"
            };
        }
    }
}