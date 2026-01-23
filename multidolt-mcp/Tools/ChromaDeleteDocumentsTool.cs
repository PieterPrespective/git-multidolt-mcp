using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Embranch.Models;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that deletes documents from a ChromaDB collection
/// </summary>
[McpServerToolType]
public class ChromaDeleteDocumentsTool
{
    private readonly ILogger<ChromaDeleteDocumentsTool> _logger;
    private readonly IChromaDbService _chromaService;
    private readonly IDeletionTracker _deletionTracker;
    private readonly IDoltCli _doltCli;
    private readonly DoltConfiguration _doltConfig;

    /// <summary>
    /// Initializes a new instance of the ChromaDeleteDocumentsTool class
    /// </summary>
    public ChromaDeleteDocumentsTool(ILogger<ChromaDeleteDocumentsTool> logger, IChromaDbService chromaService, 
        IDeletionTracker deletionTracker, IDoltCli doltCli, IOptions<DoltConfiguration> doltConfig)
    {
        _logger = logger;
        _chromaService = chromaService;
        _deletionTracker = deletionTracker;
        _doltCli = doltCli;
        _doltConfig = doltConfig.Value;
    }

    /// <summary>
    /// Deletes specific documents from a ChromaDB collection
    /// </summary>
    [McpServerTool]
    [Description("Delete specific documents from a collection.")]
    public virtual async Task<object> DeleteDocuments(string collectionName, string idsJson)
    {
        const string toolName = nameof(ChromaDeleteDocumentsTool);
        const string methodName = nameof(DeleteDocuments);
        
        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, 
                $"Collection: '{collectionName}', IDs: {idsJson?.Length ?? 0} chars");
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

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Deleting documents from collection '{collectionName}'");

            List<string> ids;
            try
            {
                ids = JsonSerializer.Deserialize<List<string>>(idsJson) ?? new List<string>();
            }
            catch (JsonException ex)
            {
                var error = $"Invalid IDs JSON format: {ex.Message}";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
                };
            }

            if (ids.Count == 0)
            {
                const string error = "IDs list cannot be empty";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = error
                };
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Attempting to delete {ids.Count} documents from collection '{collectionName}'");
            var startTime = DateTime.UtcNow;

            // STEP 1: Get original document data before deletion
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Retrieving original document data before deletion");
            
            // Important: Use GetDocumentsAsync without IDs parameter to get all documents, then filter
            // This ensures we get the documents before they're marked for deletion
            _logger.LogDebug($"[ChromaDeleteDocumentsTool] Attempting to get documents for collection: {collectionName}");
            var originalDocs = await _chromaService.GetDocumentsAsync(collectionName);
            _logger.LogDebug($"[ChromaDeleteDocumentsTool] Retrieved documents, now extracting data");
            var originalDocData = ExtractOriginalDocumentData(originalDocs);
            
            // Filter to only include the documents we're deleting
            var filteredOriginalDocData = new Dictionary<string, (string ContentHash, Dictionary<string, object> Metadata)>();
            foreach (var docId in ids)
            {
                if (originalDocData.ContainsKey(docId))
                {
                    filteredOriginalDocData[docId] = originalDocData[docId];
                }
            }
            
            _logger.LogDebug($"[ChromaDeleteDocumentsTool] Filtered original doc data from {originalDocData.Count} to {filteredOriginalDocData.Count} entries for deletion");
            
            // Use the filtered data for the rest of the process
            originalDocData = filteredOriginalDocData;

            // Get current repository state for deletion tracking
            var repoPath = _doltConfig.RepositoryPath;
            var branchContext = await _doltCli.GetCurrentBranchAsync();
            var baseCommitHash = await _doltCli.GetHeadCommitHashAsync();

            // STEP 2: Track deletions in deletion tracking table
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Recording deletions in tracking database");
            _logger.LogDebug($"[ChromaDeleteDocumentsTool] Original document data contains {originalDocData.Count} entries: [{string.Join(", ", originalDocData.Keys)}]");
            _logger.LogDebug($"[ChromaDeleteDocumentsTool] Processing {ids.Count} document IDs: [{string.Join(", ", ids)}]");
            
            int trackedCount = 0;
            foreach (var docId in ids)
            {
                if (originalDocData.ContainsKey(docId))
                {
                    _logger.LogDebug($"[ChromaDeleteDocumentsTool] Tracking deletion for docId: {docId}");
                    await _deletionTracker.TrackDeletionAsync(
                        repoPath,
                        docId, 
                        collectionName, 
                        originalDocData[docId].ContentHash,
                        originalDocData[docId].Metadata,
                        branchContext,
                        baseCommitHash
                    );
                    trackedCount++;
                    _logger.LogDebug($"[ChromaDeleteDocumentsTool] Successfully tracked deletion for docId: {docId}");
                }
                else
                {
                    _logger.LogWarning($"[ChromaDeleteDocumentsTool] No original data found for docId: {docId}, cannot track deletion");
                }
            }
            
            _logger.LogInformation($"[ChromaDeleteDocumentsTool] Tracked {trackedCount} out of {ids.Count} deletions");

            // STEP 3: Mark documents with deletion flag temporarily (for immediate detection)
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Marking documents for deletion sync");
            var deleteMetadata = ids.Select(_ => new Dictionary<string, object> 
            { 
                ["is_local_change"] = true,
                ["_pending_deletion"] = true,
                ["_deletion_timestamp"] = DateTime.UtcNow.ToString("O"),
                ["_deletion_source"] = "mcp_tool"
            }).ToList();

            // Update metadata to mark for deletion sync (PP13-68-C2: this is user action, keep markAsLocalChange=true)
            await _chromaService.UpdateDocumentsAsync(collectionName, ids, metadatas: deleteMetadata, markAsLocalChange: true);

            // STEP 4: Actually delete the documents
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Proceeding with document deletion");
            var result = await _chromaService.DeleteDocumentsAsync(collectionName, ids);
            var duration = DateTime.UtcNow - startTime;

            // STEP 5: Update deletion tracking status
            if (result)
            {
                ToolLoggingUtility.LogToolInfo(_logger, toolName, "Updating deletion tracking status to pending");
                foreach (var docId in ids)
                {
                    // Documents are already tracked as "pending" by default in TrackDeletionAsync
                    ToolLoggingUtility.LogToolDebug(_logger, toolName, $"Deletion tracked for {docId}");
                }
            }

            var response = new
            {
                success = result,
                message = result ? $"Successfully deleted {ids.Count} documents from collection '{collectionName}'" : "Failed to delete documents"
            };

            if (result)
            {
                var resultMessage = $"Deleted {ids.Count} documents from '{collectionName}' in {duration.TotalMilliseconds:F1}ms";
                ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, resultMessage);
            }
            else
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Failed to delete documents");
            }
            
            return response;
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = $"Failed to delete documents: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Extracts original document data for deletion tracking
    /// </summary>
    private Dictionary<string, (string ContentHash, Dictionary<string, object> Metadata)> ExtractOriginalDocumentData(dynamic originalDocs)
    {
        var result = new Dictionary<string, (string ContentHash, Dictionary<string, object> Metadata)>();
        
        try
        {
            if (originalDocs == null)
            {
                _logger.LogWarning("ExtractOriginalDocumentData: originalDocs is null");
                return result;
            }

            // Log the actual type and structure we received
            _logger.LogDebug($"ExtractOriginalDocumentData: Received type {originalDocs.GetType().Name}");

            // Handle both dynamic objects and Dictionary<string, object>
            Dictionary<string, object> originalDocsDict;
            
            if (originalDocs is Dictionary<string, object> dict)
            {
                originalDocsDict = dict;
                _logger.LogDebug($"ExtractOriginalDocumentData: Using Dictionary with {originalDocsDict.Count} keys: [{string.Join(", ", originalDocsDict.Keys)}]");
            }
            else
            {
                _logger.LogWarning($"ExtractOriginalDocumentData: Unexpected type {originalDocs.GetType().Name}, attempting dynamic access");
                originalDocsDict = new Dictionary<string, object>();
                
                // Try to extract keys dynamically
                try
                {
                    var properties = originalDocs.GetType().GetProperties();
                    foreach (var prop in properties)
                    {
                        originalDocsDict[prop.Name] = prop.GetValue(originalDocs);
                    }
                    _logger.LogDebug($"ExtractOriginalDocumentData: Extracted {originalDocsDict.Count} properties: [{string.Join(", ", originalDocsDict.Keys)}]");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExtractOriginalDocumentData: Failed to extract properties dynamically");
                    return result;
                }
            }
            
            if (!originalDocsDict.ContainsKey("ids"))
            {
                _logger.LogWarning($"ExtractOriginalDocumentData: 'ids' key not found. Available keys: [{string.Join(", ", originalDocsDict.Keys)}]");
                return result;
            }

            // Try multiple type conversions for ids
            object idsValue = originalDocsDict["ids"];
            List<object>? ids = null;
            
            if (idsValue is List<object> listObj)
            {
                ids = listObj;
                _logger.LogDebug($"ExtractOriginalDocumentData: Found ids as List<object> with {ids.Count} items");
            }
            else if (idsValue is object[] arrayObj)
            {
                ids = arrayObj.ToList();
                _logger.LogDebug($"ExtractOriginalDocumentData: Found ids as object[] with {ids.Count} items, converted to List");
            }
            else if (idsValue is System.Collections.IEnumerable enumerable)
            {
                ids = enumerable.Cast<object>().ToList();
                _logger.LogDebug($"ExtractOriginalDocumentData: Found ids as IEnumerable ({idsValue.GetType().Name}) with {ids.Count} items, converted to List");
            }
            else
            {
                _logger.LogWarning($"ExtractOriginalDocumentData: 'ids' value has unexpected type {idsValue?.GetType().Name ?? "null"}");
                return result;
            }

            if (ids == null || ids.Count == 0)
            {
                _logger.LogWarning("ExtractOriginalDocumentData: ids list is null or empty");
                return result;
            }

            // Handle documents with similar type flexibility
            List<object>? documents = null;
            if (originalDocsDict.ContainsKey("documents"))
            {
                var documentsValue = originalDocsDict["documents"];
                if (documentsValue is List<object> docListObj)
                {
                    documents = docListObj;
                }
                else if (documentsValue is object[] docArrayObj)
                {
                    documents = docArrayObj.ToList();
                }
                else if (documentsValue is System.Collections.IEnumerable docEnumerable)
                {
                    documents = docEnumerable.Cast<object>().ToList();
                }
                
                _logger.LogDebug($"ExtractOriginalDocumentData: Found documents with {documents?.Count ?? 0} items");
            }
            else
            {
                _logger.LogWarning("ExtractOriginalDocumentData: 'documents' key not found");
            }

            // Handle metadatas with similar type flexibility
            List<object>? metadatas = null;
            if (originalDocsDict.ContainsKey("metadatas"))
            {
                var metadatasValue = originalDocsDict["metadatas"];
                if (metadatasValue is List<object> metaListObj)
                {
                    metadatas = metaListObj;
                }
                else if (metadatasValue is object[] metaArrayObj)
                {
                    metadatas = metaArrayObj.ToList();
                }
                else if (metadatasValue is System.Collections.IEnumerable metaEnumerable)
                {
                    metadatas = metaEnumerable.Cast<object>().ToList();
                }
                
                _logger.LogDebug($"ExtractOriginalDocumentData: Found metadatas with {metadatas?.Count ?? 0} items");
            }
            else
            {
                _logger.LogDebug("ExtractOriginalDocumentData: 'metadatas' key not found (this is optional)");
            }

            // Process each document
            for (int i = 0; i < ids.Count; i++)
            {
                var docId = ids[i]?.ToString() ?? "";
                var content = documents != null && i < documents.Count ? documents[i]?.ToString() ?? "" : "";
                var metadata = new Dictionary<string, object>();

                if (string.IsNullOrEmpty(docId))
                {
                    _logger.LogWarning($"ExtractOriginalDocumentData: Empty docId at index {i}, skipping");
                    continue;
                }

                // Extract metadata if available
                if (metadatas != null && i < metadatas.Count && metadatas[i] != null)
                {
                    if (metadatas[i] is Dictionary<string, object> metadataDict)
                    {
                        metadata = new Dictionary<string, object>(metadataDict);
                    }
                    else
                    {
                        _logger.LogDebug($"ExtractOriginalDocumentData: Metadata at index {i} is type {metadatas[i].GetType().Name}, not Dictionary<string,object>");
                    }
                }

                // Calculate content hash
                var contentHash = DocumentConverterUtilityV2.CalculateContentHash(content);
                result[docId] = (contentHash, metadata);
                
                _logger.LogDebug($"ExtractOriginalDocumentData: Successfully extracted data for docId '{docId}' with content hash {contentHash}");

                // IMPORTANT: Also create an entry using the content as the key for backward compatibility
                // This handles cases where ChromaDB returns content as ID
                if (!string.IsNullOrEmpty(content) && content != docId)
                {
                    result[content] = (contentHash, metadata);
                    _logger.LogDebug($"ExtractOriginalDocumentData: Also mapped content '{content}' to same data for compatibility");
                }
            }

            _logger.LogInformation($"ExtractOriginalDocumentData: Successfully extracted data for {result.Count} documents");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractOriginalDocumentData: Exception during document data extraction");
        }

        return result;
    }
}