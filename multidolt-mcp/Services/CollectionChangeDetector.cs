using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Embranch.Models;
using Embranch.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Embranch.Services
{
    /// <summary>
    /// Detects changes in collection-level operations that need to be synchronized between ChromaDB and Dolt.
    /// Implements collection deletion, rename, and metadata update detection.
    /// </summary>
    public class CollectionChangeDetector : ICollectionChangeDetector
    {
        private readonly IChromaDbService _chroma;
        private readonly IDoltCli _dolt;
        private readonly IDeletionTracker _deletionTracker;
        private readonly DoltConfiguration _doltConfig;
        private readonly ILogger<CollectionChangeDetector>? _logger;
        private string _repositoryPath = "";

        public CollectionChangeDetector(
            IChromaDbService chroma,
            IDoltCli dolt,
            IDeletionTracker deletionTracker,
            IOptions<DoltConfiguration> doltConfig,
            ILogger<CollectionChangeDetector>? logger = null)
        {
            _chroma = chroma;
            _dolt = dolt;
            _deletionTracker = deletionTracker;
            _doltConfig = doltConfig.Value;
            _logger = logger;
            _repositoryPath = _doltConfig.RepositoryPath;
        }

        /// <summary>
        /// Initialize the collection change detector
        /// </summary>
        public async Task InitializeAsync(string repositoryPath)
        {
            _repositoryPath = repositoryPath;
            _logger?.LogInformation("Collection change detector initialized for repository: {RepositoryPath}", repositoryPath);
            
            // Initialize deletion tracker to ensure collection deletion schema exists
            await _deletionTracker.InitializeAsync(repositoryPath);
        }

        /// <summary>
        /// Validate schema and initialization
        /// </summary>
        public async Task ValidateSchemaAsync(string repositoryPath)
        {
            _logger?.LogInformation("Validating collection change detector schema for repository: {RepositoryPath}", repositoryPath);
            
            // Validate deletion tracker is initialized (basic connectivity test)
            // Note: ValidateSchemaAsync doesn't exist on IDeletionTracker, so we test basic functionality
            try
            {
                await _deletionTracker.GetPendingCollectionDeletionsAsync(repositoryPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Collection change detector schema validation failed - deletion tracker not accessible", ex);
            }
        }

        /// <summary>
        /// Validate initialization
        /// </summary>
        public async Task ValidateInitializationAsync()
        {
            _logger?.LogInformation("Validating collection change detector initialization");
            
            if (string.IsNullOrEmpty(_repositoryPath))
            {
                throw new InvalidOperationException("Collection change detector not properly initialized - repository path is empty");
            }
            
            // Test basic connectivity
            try
            {
                // Always test ChromaDB connectivity
                await _chroma.ListCollectionsAsync();
                
                // Only test Dolt connectivity if the repository is initialized
                // This prevents failures when starting fresh without an existing Dolt repo
                var isInitialized = await _dolt.IsInitializedAsync();
                if (isInitialized)
                {
                    _logger?.LogInformation("Dolt repository is initialized, validating database connectivity");
                    await GetDoltCollectionsAsync();
                }
                else
                {
                    _logger?.LogInformation("Dolt repository not yet initialized, skipping database validation");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Collection change detector validation failed - service connectivity error");
                throw new InvalidOperationException("Collection change detector validation failed", ex);
            }
        }

        /// <summary>
        /// Detect all collection-level changes between ChromaDB and Dolt
        /// </summary>
        public async Task<CollectionChanges> DetectCollectionChangesAsync()
        {
            _logger?.LogInformation("===== DetectCollectionChangesAsync STARTING =====");

            try
            {
                var changes = new CollectionChanges();

                // STEP 1: Get collections from both sources
                _logger?.LogInformation("STEP 1: Getting collections from ChromaDB and Dolt");
                var chromaCollections = await GetChromaCollectionsAsync();
                var doltCollections = await GetDoltCollectionsAsync();
                
                _logger?.LogInformation("Found {ChromaCount} collections in ChromaDB and {DoltCount} collections in Dolt", 
                    chromaCollections.Count, doltCollections.Count);

                // STEP 2: Check deletion tracker for pending collection deletions
                _logger?.LogInformation("STEP 2: Checking deletion tracker for pending collection operations");
                var pendingDeletions = await _deletionTracker.GetPendingCollectionDeletionsAsync(_repositoryPath);
                
                foreach (var deletion in pendingDeletions)
                {
                    if (deletion.OperationType == "deletion")
                    {
                        changes.DeletedCollections.Add(deletion.CollectionName);
                        _logger?.LogInformation("Found pending collection deletion: {CollectionName}", deletion.CollectionName);
                    }
                    else if (deletion.OperationType == "rename" && !string.IsNullOrEmpty(deletion.NewName))
                    {
                        var oldMetadata = ParseMetadata(deletion.OriginalMetadata);
                        var newMetadata = await GetCollectionMetadata(deletion.NewName);
                        changes.RenamedCollections.Add(new CollectionRename(deletion.OriginalName ?? deletion.CollectionName, deletion.NewName, oldMetadata, newMetadata));
                        _logger?.LogInformation("Found pending collection rename: {OldName} -> {NewName}", deletion.OriginalName ?? deletion.CollectionName, deletion.NewName);
                    }
                    else if (deletion.OperationType == "metadata_update")
                    {
                        var oldMetadata = ParseMetadata(deletion.OriginalMetadata);
                        var newMetadata = await GetCollectionMetadata(deletion.CollectionName);
                        changes.UpdatedCollections.Add(new CollectionMetadataChange(deletion.CollectionName, oldMetadata, newMetadata));
                        _logger?.LogInformation("Found pending collection metadata update: {CollectionName}", deletion.CollectionName);
                    }
                }

                // STEP 3: Find collections that exist in Dolt but not in ChromaDB (fallback deletion detection)
                _logger?.LogInformation("STEP 3: Finding collections deleted from ChromaDB (fallback detection)");
                var chromaCollectionNames = chromaCollections.Select(c => c.Name).ToHashSet();
                var doltCollectionNames = doltCollections.Select(c => c.Name).ToHashSet();
                
                foreach (var doltCollection in doltCollections)
                {
                    if (!chromaCollectionNames.Contains(doltCollection.Name))
                    {
                        // Only add if not already tracked by deletion tracking
                        if (!changes.DeletedCollections.Contains(doltCollection.Name))
                        {
                            changes.DeletedCollections.Add(doltCollection.Name);
                            _logger?.LogInformation("Found deleted collection via fallback detection: {CollectionName}", doltCollection.Name);
                        }
                    }
                }

                // STEP 4: Compare metadata for collections that exist in both systems
                _logger?.LogInformation("STEP 4: Comparing metadata for existing collections");
                var chromaCollectionMap = chromaCollections.ToDictionary(c => c.Name);
                var doltCollectionMap = doltCollections.ToDictionary(c => c.Name);

                foreach (var chromaCollection in chromaCollections)
                {
                    if (doltCollectionMap.TryGetValue(chromaCollection.Name, out var doltCollection))
                    {
                        // Compare metadata
                        if (!MetadataEquals(chromaCollection.Metadata, doltCollection.Metadata))
                        {
                            // Only add if not already tracked
                            if (!changes.UpdatedCollections.Any(u => u.CollectionName == chromaCollection.Name))
                            {
                                changes.UpdatedCollections.Add(new CollectionMetadataChange(
                                    chromaCollection.Name, 
                                    doltCollection.Metadata, 
                                    chromaCollection.Metadata));
                                _logger?.LogInformation("Found collection with metadata differences: {CollectionName}", chromaCollection.Name);
                            }
                        }
                    }
                }

                var result = changes;
                _logger?.LogInformation("Detected {Total} collection changes: {Summary}",
                    result.TotalChanges, result.GetSummary());

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to detect collection changes");
                throw new Exception($"Failed to detect collection changes: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Check if there are any pending collection changes
        /// </summary>
        public async Task<bool> HasPendingCollectionChangesAsync()
        {
            try
            {
                var changes = await DetectCollectionChangesAsync();
                return changes.HasChanges;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to check for pending collection changes");
                return false;
            }
        }

        #region Helper Methods

        /// <summary>
        /// Get all collections from ChromaDB with their metadata
        /// </summary>
        private async Task<List<ChromaCollectionInfo>> GetChromaCollectionsAsync()
        {
            try
            {
                var collections = new List<ChromaCollectionInfo>();
                var collectionNames = await _chroma.ListCollectionsAsync();

                // Handle List<string> return type
                if (collectionNames is List<string> stringList)
                {
                    foreach (var name in stringList)
                    {
                        if (!string.IsNullOrEmpty(name))
                        {
                            var metadata = await GetCollectionMetadata(name);
                            collections.Add(new ChromaCollectionInfo(name, metadata));
                        }
                    }
                }
                // Fallback for any other dynamic types (like List<object>)
                else if (collectionNames is System.Collections.IEnumerable enumerable)
                {
                    foreach (var nameObj in enumerable)
                    {
                        var name = nameObj?.ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            var metadata = await GetCollectionMetadata(name);
                            collections.Add(new ChromaCollectionInfo(name, metadata));
                        }
                    }
                }

                return collections;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get ChromaDB collections");
                return new List<ChromaCollectionInfo>();
            }
        }

        /// <summary>
        /// Get collection metadata from ChromaDB
        /// </summary>
        private async Task<Dictionary<string, object>?> GetCollectionMetadata(string collectionName)
        {
            try
            {
                var collectionInfo = await _chroma.GetCollectionAsync(collectionName);
                
                if (collectionInfo is Dictionary<string, object> infoDict)
                {
                    if (infoDict.TryGetValue("metadata", out var metadataObj))
                    {
                        if (metadataObj is Dictionary<string, object> metadata)
                        {
                            return metadata;
                        }
                        else if (metadataObj != null)
                        {
                            // Try to parse as JSON if it's a string
                            var metadataStr = metadataObj.ToString();
                            if (!string.IsNullOrEmpty(metadataStr) && metadataStr.StartsWith("{"))
                            {
                                try
                                {
                                    return JsonSerializer.Deserialize<Dictionary<string, object>>(metadataStr);
                                }
                                catch (JsonException)
                                {
                                    // Ignore JSON parse errors
                                }
                            }
                        }
                    }
                }

                return new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get metadata for collection {CollectionName}", collectionName);
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Get all collections from Dolt with their metadata
        /// </summary>
        private async Task<List<DoltCollectionInfo>> GetDoltCollectionsAsync()
        {
            // Check if Dolt is initialized first
            var isInitialized = await _dolt.IsInitializedAsync();
            if (!isInitialized)
            {
                _logger?.LogDebug("Dolt repository not initialized, returning empty collection list");
                return new List<DoltCollectionInfo>();
            }

            var sql = @"
                SELECT collection_name, metadata
                FROM collections";

            try
            {
                var results = await _dolt.QueryAsync<dynamic>(sql);
                var collections = new List<DoltCollectionInfo>();
                
                foreach (var row in results)
                {
                    string name, metadataJson;
                    
                    if (row is System.Text.Json.JsonElement jsonElement)
                    {
                        name = JsonUtility.GetPropertyAsString(jsonElement, "collection_name", "");
                        metadataJson = JsonUtility.GetPropertyAsString(jsonElement, "metadata", "{}");
                    }
                    else
                    {
                        name = (string)row.collection_name;
                        metadataJson = (string)row.metadata ?? "{}";
                    }
                    
                    if (!string.IsNullOrEmpty(name))
                    {
                        var metadata = ParseMetadata(metadataJson);
                        collections.Add(new DoltCollectionInfo(name, metadata));
                    }
                }
                
                return collections;
            }
            catch (DoltException ex) when (ex.Message.Contains("table not found"))
            {
                // Fresh/empty Dolt database - collections table doesn't exist yet
                _logger?.LogDebug("Collections table not found in Dolt - returning empty collections list (empty database)");
                return new List<DoltCollectionInfo>();
            }
        }

        /// <summary>
        /// Parse metadata JSON string to dictionary
        /// </summary>
        private Dictionary<string, object>? ParseMetadata(string? metadataJson)
        {
            if (string.IsNullOrEmpty(metadataJson) || metadataJson == "{}")
            {
                return new Dictionary<string, object>();
            }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "Failed to parse metadata JSON: {MetadataJson}", metadataJson);
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Compare two metadata dictionaries for equality
        /// </summary>
        private bool MetadataEquals(Dictionary<string, object>? metadata1, Dictionary<string, object>? metadata2)
        {
            // Handle null cases
            if (metadata1 == null && metadata2 == null) return true;
            if (metadata1 == null || metadata2 == null) return false;

            // Handle empty dictionaries
            if (metadata1.Count == 0 && metadata2.Count == 0) return true;
            if (metadata1.Count != metadata2.Count) return false;

            // Compare JSON representations for deep equality
            try
            {
                var json1 = JsonSerializer.Serialize(metadata1, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var json2 = JsonSerializer.Serialize(metadata2, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                return json1 == json2;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to compare metadata, treating as different");
                return false;
            }
        }

        #endregion
    }
}