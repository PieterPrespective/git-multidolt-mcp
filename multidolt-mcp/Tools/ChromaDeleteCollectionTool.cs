using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Microsoft.Extensions.Options;
using DMMS.Models;
using DMMS.Services;
using DMMS.Utilities;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that deletes a collection from ChromaDB
/// </summary>
[McpServerToolType]
public class ChromaDeleteCollectionTool
{
    private readonly ILogger<ChromaDeleteCollectionTool> _logger;
    private readonly IChromaDbService _chromaService;
    private readonly IDeletionTracker _deletionTracker;
    private readonly IDoltCli _doltCli;
    private readonly DoltConfiguration _doltConfig;

    /// <summary>
    /// Initializes a new instance of the ChromaDeleteCollectionTool class
    /// </summary>
    public ChromaDeleteCollectionTool(ILogger<ChromaDeleteCollectionTool> logger, IChromaDbService chromaService,
        IDeletionTracker deletionTracker, IDoltCli doltCli, IOptions<DoltConfiguration> doltConfig)
    {
        _logger = logger;
        _chromaService = chromaService;
        _deletionTracker = deletionTracker;
        _doltCli = doltCli;
        _doltConfig = doltConfig.Value;
    }

    /// <summary>
    /// Deletes a collection from ChromaDB with deletion tracking integration
    /// </summary>
    [McpServerTool]
    [Description("Delete a collection from ChromaDB.")]
    public virtual async Task<object> DeleteCollection(string collectionName)
    {
        const string toolName = nameof(ChromaDeleteCollectionTool);
        const string methodName = nameof(DeleteCollection);
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

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Starting collection deletion process for '{collectionName}'");

            // STEP 1: Get original collection metadata before deletion
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Retrieving original collection metadata before deletion");
            var originalCollectionData = await _chromaService.GetCollectionAsync(collectionName);
            
            if (originalCollectionData == null)
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"Collection '{collectionName}' not found");
                return new
                {
                    success = false,
                    error = $"Collection '{collectionName}' not found"
                };
            }

            // Extract metadata from collection data
            var originalMetadata = ExtractCollectionMetadata(originalCollectionData);
            _logger.LogDebug($"[ChromaDeleteCollectionTool] Retrieved metadata for collection '{collectionName}': {originalMetadata.Count} entries");

            // STEP 2: Get current repository state for deletion tracking
            var repoPath = _doltConfig.RepositoryPath;
            var branchContext = await _doltCli.GetCurrentBranchAsync();
            var baseCommitHash = await _doltCli.GetHeadCommitHashAsync();

            // STEP 3: Track collection deletion in deletion tracking table
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Recording collection deletion in tracking database");
            await _deletionTracker.TrackCollectionDeletionAsync(
                repoPath,
                collectionName,
                originalMetadata,
                branchContext,
                baseCommitHash
            );
            _logger.LogDebug($"[ChromaDeleteCollectionTool] Successfully tracked collection deletion for '{collectionName}'");

            // STEP 4: Actually delete the collection from ChromaDB
            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Deleting collection '{collectionName}' from ChromaDB");
            var result = await _chromaService.DeleteCollectionAsync(collectionName);

            if (result)
            {
                ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, 
                    $"Successfully deleted collection '{collectionName}' with deletion tracking");
                
                return new
                {
                    success = true,
                    message = $"Successfully deleted collection '{collectionName}' with deletion tracking",
                    tracked = true,
                    deletionDetails = new
                    {
                        collectionName,
                        originalMetadata = originalMetadata.Count,
                        branchContext,
                        baseCommitHash,
                        deletionTimestamp = DateTime.UtcNow
                    }
                };
            }
            else
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Failed to delete collection from ChromaDB");
                return new
                {
                    success = false,
                    error = "Failed to delete collection from ChromaDB",
                    tracked = true,
                    message = "Collection deletion was tracked but ChromaDB deletion failed"
                };
            }
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = $"Failed to delete collection: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Extracts metadata from ChromaDB collection data object
    /// </summary>
    private Dictionary<string, object> ExtractCollectionMetadata(object collectionData)
    {
        var metadata = new Dictionary<string, object>();
        
        try
        {
            // Handle different possible response formats from ChromaDB
            if (collectionData is Dictionary<string, object> dict)
            {
                // Standard dictionary format
                foreach (var kvp in dict)
                {
                    if (kvp.Key != "id" && kvp.Key != "name") // Skip standard collection identifiers
                    {
                        metadata[kvp.Key] = kvp.Value;
                    }
                }
            }
            else
            {
                // For other formats, try to extract using reflection or convert to string
                var type = collectionData.GetType();
                var properties = type.GetProperties();
                
                foreach (var property in properties)
                {
                    if (property.Name != "Id" && property.Name != "Name" && property.Name != "id" && property.Name != "name")
                    {
                        try
                        {
                            var value = property.GetValue(collectionData);
                            if (value != null)
                            {
                                metadata[property.Name] = value;
                            }
                        }
                        catch
                        {
                            // Skip properties that can't be accessed
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[ChromaDeleteCollectionTool] Could not extract collection metadata: {ex.Message}");
            // Provide basic metadata if extraction fails
            metadata["extracted_at"] = DateTime.UtcNow;
            metadata["extraction_method"] = "fallback";
        }

        return metadata;
    }
}