using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that queries documents from a ChromaDB collection
/// </summary>
[McpServerToolType]
public class ChromaQueryDocumentsTool
{
    private readonly ILogger<ChromaQueryDocumentsTool> _logger;
    private readonly IChromaDbService _chromaService;

    /// <summary>
    /// Initializes a new instance of the ChromaQueryDocumentsTool class
    /// </summary>
    public ChromaQueryDocumentsTool(ILogger<ChromaQueryDocumentsTool> logger, IChromaDbService chromaService)
    {
        _logger = logger;
        _chromaService = chromaService;
    }

    /// <summary>
    /// Queries documents from a ChromaDB collection using semantic search
    /// </summary>
    [McpServerTool]
    [Description("Query documents from a Chroma collection with advanced filtering.")]
    public virtual async Task<object> QueryDocuments(string collectionName, string queryTextsJson, int nResults = 5, 
        string? whereJson = null, string? whereDocumentJson = null)
    {
        const string toolName = nameof(ChromaQueryDocumentsTool);
        const string methodName = nameof(QueryDocuments);
        
        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, 
                $"Collection: '{collectionName}', QueryTexts: {queryTextsJson?.Length ?? 0} chars, nResults: {nResults}");
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

            if (string.IsNullOrWhiteSpace(queryTextsJson))
            {
                const string error = "Query texts JSON is required";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
                };
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Querying collection '{collectionName}'");

            List<string> queryTexts;
            Dictionary<string, object>? where = null;
            Dictionary<string, object>? whereDocument = null;

            try
            {
                queryTexts = JsonSerializer.Deserialize<List<string>>(queryTextsJson) ?? new List<string>();

                if (!string.IsNullOrWhiteSpace(whereJson))
                {
                    where = JsonSerializer.Deserialize<Dictionary<string, object>>(whereJson);
                }

                if (!string.IsNullOrWhiteSpace(whereDocumentJson))
                {
                    whereDocument = JsonSerializer.Deserialize<Dictionary<string, object>>(whereDocumentJson);
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

            if (queryTexts.Count == 0)
            {
                const string error = "Query texts list cannot be empty";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
                };
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Executing query with {queryTexts.Count} query texts, nResults={nResults}");
            var startTime = DateTime.UtcNow;
            var result = await _chromaService.QueryDocumentsAsync(collectionName, queryTexts, nResults, where, whereDocument);
            var duration = DateTime.UtcNow - startTime;

            // Cast to dictionary and extract values for proper JSON structure
            if (result is Dictionary<string, object> resultDict)
            {
                var response = new
                {
                    ids = resultDict.TryGetValue("ids", out var ids) ? ids : null,
                    embeddings = resultDict.TryGetValue("embeddings", out var embeddings) ? embeddings : null,
                    documents = resultDict.TryGetValue("documents", out var documents) ? documents : null,
                    uris = resultDict.TryGetValue("uris", out var uris) ? uris : null,
                    data = resultDict.TryGetValue("data", out var data) ? data : null,
                    metadatas = resultDict.TryGetValue("metadatas", out var metadatas) ? metadatas : null,
                    distances = resultDict.TryGetValue("distances", out var distances) ? distances : null,
                    included = new[] { "documents", "metadatas", "distances", "ids" }
                };
                
                var resultMessage = $"Query completed in {duration.TotalMilliseconds:F1}ms with {queryTexts.Count} queries";
                ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, resultMessage);
                return response;
            }

            const string formatError = "Unexpected result format from query operation";
            ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, formatError);
            return new
            {
                success = false,
                error = formatError
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = $"Failed to query documents: {ex.Message}"
            };
        }
    }
}