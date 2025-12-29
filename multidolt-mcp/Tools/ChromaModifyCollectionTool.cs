using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Microsoft.Extensions.Options;
using DMMS.Models;
using DMMS.Services;
using DMMS.Utilities;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that modifies a ChromaDB collection's name or metadata
/// </summary>
[McpServerToolType]
public class ChromaModifyCollectionTool
{
    private readonly ILogger<ChromaModifyCollectionTool> _logger;
    private readonly IChromaDbService _chromaService;
    private readonly IDeletionTracker _deletionTracker;
    private readonly IDoltCli _doltCli;
    private readonly DoltConfiguration _doltConfig;

    /// <summary>
    /// Initializes a new instance of the ChromaModifyCollectionTool class
    /// </summary>
    public ChromaModifyCollectionTool(ILogger<ChromaModifyCollectionTool> logger, IChromaDbService chromaService,
        IDeletionTracker deletionTracker, IDoltCli doltCli, IOptions<DoltConfiguration> doltConfig)
    {
        _logger = logger;
        _chromaService = chromaService;
        _deletionTracker = deletionTracker;
        _doltCli = doltCli;
        _doltConfig = doltConfig.Value;
    }

    /// <summary>
    /// Update a collection's name or metadata. Note: Changing HNSW parameters after creation has no effect on existing data
    /// </summary>
    [McpServerTool]
    [Description("Update a collection's name or metadata. Note: Changing HNSW parameters after creation has no effect on existing data.")]
    public virtual async Task<object> ModifyCollection(string collection_name, string? new_name = null, Dictionary<string, object>? new_metadata = null)
    {
        const string toolName = nameof(ChromaModifyCollectionTool);
        const string methodName = nameof(ModifyCollection);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"collection_name: {collection_name}, new_name: {new_name}, new_metadata: {new_metadata != null}");

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

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Modifying collection: {collection_name}");

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

            // Extract original metadata for tracking
            var originalMetadata = ExtractCollectionMetadata(collection);
            
            // Determine the operation type and track the change
            bool isRename = !string.IsNullOrWhiteSpace(new_name) && new_name != collection_name;
            bool isMetadataUpdate = new_metadata != null;

            if (isRename || isMetadataUpdate)
            {
                // Get current repository state for tracking
                var repoPath = _doltConfig.RepositoryPath;
                var branchContext = await _doltCli.GetCurrentBranchAsync();
                var baseCommitHash = await _doltCli.GetHeadCommitHashAsync();

                // Track the collection update operation
                ToolLoggingUtility.LogToolInfo(_logger, toolName, "Recording collection update in tracking database");
                await _deletionTracker.TrackCollectionUpdateAsync(
                    repoPath,
                    collection_name,
                    new_name ?? collection_name,
                    originalMetadata,
                    new_metadata ?? new Dictionary<string, object>(),
                    branchContext,
                    baseCommitHash
                );
                _logger.LogDebug($"[ChromaModifyCollectionTool] Successfully tracked collection update for '{collection_name}'");
            }

            // STUB: Backend method not yet implemented
            // TODO: Implement ModifyCollectionAsync in IChromaDbService
            ToolLoggingUtility.LogToolWarning(_logger, toolName, "Backend method ModifyCollectionAsync not yet implemented");
            
            ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Collection modification is not yet implemented in the backend service");
            return new
            {
                success = false,
                error = "NOT_IMPLEMENTED",
                message = "Collection modification is not yet implemented in the backend service",
                stub = true,
                required_backend_method = "IChromaDbService.ModifyCollectionAsync",
                tracked = isRename || isMetadataUpdate,
                tracking_details = isRename || isMetadataUpdate ? new
                {
                    operation_type = isRename ? "rename" : "metadata_update",
                    original_name = collection_name,
                    new_name = new_name,
                    has_metadata_changes = isMetadataUpdate,
                    tracking_timestamp = DateTime.UtcNow
                } : null
            };

            // When backend is implemented, the code would be:
            /*
            var result = await _chromaService.ModifyCollectionAsync(collection_name, new_name, new_metadata);
            
            if (result)
            {
                ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, 
                    $"Successfully modified collection '{collection_name}' with tracking");
                
                return new
                {
                    success = true,
                    collection = new
                    {
                        original_name = collection_name,
                        new_name = new_name ?? collection_name,
                        metadata = new_metadata ?? originalMetadata
                    },
                    changes = new
                    {
                        name_changed = isRename,
                        metadata_changed = isMetadataUpdate
                    },
                    tracked = true,
                    message = $"Collection '{collection_name}' modified successfully with tracking"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    error = "MODIFICATION_FAILED",
                    message = "Collection modification failed in backend service",
                    tracked = true
                };
            }
            */
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to modify collection: {ex.Message}"
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
            _logger.LogWarning($"[ChromaModifyCollectionTool] Could not extract collection metadata: {ex.Message}");
            // Provide basic metadata if extraction fails
            metadata["extracted_at"] = DateTime.UtcNow;
            metadata["extraction_method"] = "fallback";
        }

        return metadata;
    }
}