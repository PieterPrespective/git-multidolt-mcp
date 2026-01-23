using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Embranch.Models
{
    /// <summary>
    /// Represents a collection rename operation
    /// </summary>
    public record CollectionRename(
        string OldName,
        string NewName,
        Dictionary<string, object>? OldMetadata = null,
        Dictionary<string, object>? NewMetadata = null
    );

    /// <summary>
    /// Represents a collection metadata change operation
    /// </summary>
    public record CollectionMetadataChange(
        string CollectionName,
        Dictionary<string, object>? OldMetadata,
        Dictionary<string, object>? NewMetadata
    );

    /// <summary>
    /// Represents all collection-level changes detected between ChromaDB and Dolt
    /// </summary>
    public class CollectionChanges
    {
        public List<string> DeletedCollections { get; set; } = new();
        public List<CollectionRename> RenamedCollections { get; set; } = new();
        public List<CollectionMetadataChange> UpdatedCollections { get; set; } = new();

        /// <summary>
        /// Check if there are any collection changes
        /// </summary>
        public bool HasChanges => DeletedCollections.Count > 0 || RenamedCollections.Count > 0 || UpdatedCollections.Count > 0;

        /// <summary>
        /// Get total number of collection changes
        /// </summary>
        public int TotalChanges => DeletedCollections.Count + RenamedCollections.Count + UpdatedCollections.Count;

        /// <summary>
        /// Get a summary string of collection changes
        /// </summary>
        public string GetSummary() => 
            $"{DeletedCollections.Count} deleted, {RenamedCollections.Count} renamed, {UpdatedCollections.Count} metadata updated";

        /// <summary>
        /// Get all collection names affected by these changes
        /// </summary>
        public IEnumerable<string> GetAffectedCollectionNames()
        {
            var collections = new HashSet<string>();
            
            foreach (var name in DeletedCollections)
                collections.Add(name);
            
            foreach (var rename in RenamedCollections)
            {
                collections.Add(rename.OldName);
                collections.Add(rename.NewName);
            }
            
            foreach (var update in UpdatedCollections)
                collections.Add(update.CollectionName);
            
            return collections;
        }
    }

    /// <summary>
    /// Represents a collection information from ChromaDB
    /// </summary>
    public record ChromaCollectionInfo(
        string Name,
        Dictionary<string, object>? Metadata = null
    )
    {
        /// <summary>
        /// Serialize metadata to JSON string for comparison
        /// </summary>
        public string GetMetadataJson() => 
            Metadata == null ? "{}" : JsonSerializer.Serialize(Metadata, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    /// <summary>
    /// Represents a collection information from Dolt
    /// </summary>
    public record DoltCollectionInfo(
        string Name,
        Dictionary<string, object>? Metadata = null
    )
    {
        /// <summary>
        /// Serialize metadata to JSON string for comparison
        /// </summary>
        public string GetMetadataJson() => 
            Metadata == null ? "{}" : JsonSerializer.Serialize(Metadata, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}