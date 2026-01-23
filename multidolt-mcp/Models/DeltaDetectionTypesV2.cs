using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Embranch.Models
{
    /// <summary>
    /// V2 Model Types for Bidirectional Sync Architecture
    /// Supports generalized document schema with JSON metadata
    /// </summary>
    
    #region Core Document Types (Updated for V2)
    
    /// <summary>
    /// Represents a document from the generalized Dolt documents table.
    /// All domain-specific fields are stored in the Metadata dictionary.
    /// </summary>
    public record DoltDocumentV2(
        string DocId,                              // Document unique identifier
        string CollectionName,                     // Collection this document belongs to
        string Content,                            // Full text content to be chunked
        string ContentHash,                        // SHA-256 hash for change tracking
        string? Title = null,                      // Extracted title for common queries
        string? DocType = null,                    // Extracted type for categorization
        Dictionary<string, object>? Metadata = null // ALL user-defined fields preserved here
    )
    {
        /// <summary>
        /// Get a display name for this document
        /// </summary>
        public string GetDisplayName() => Title ?? DocId;
        
        /// <summary>
        /// Serialize metadata to JSON string for database storage
        /// </summary>
        public string GetMetadataJson() => 
            Metadata == null ? "{}" : JsonSerializer.Serialize(Metadata);
        
        /// <summary>
        /// Extract a specific metadata field value
        /// </summary>
        public T? GetMetadataValue<T>(string key)
        {
            if (Metadata?.TryGetValue(key, out var value) == true)
            {
                if (value is JsonElement element)
                    return JsonSerializer.Deserialize<T>(element.GetRawText());
                return (T?)value;
            }
            return default;
        }
    }
    
    /// <summary>
    /// Represents a document delta between Dolt and ChromaDB (updated for V2).
    /// Used for tracking changes that need synchronization.
    /// </summary>
    public class DocumentDeltaV2
    {
        public string DocId { get; set; } = "";
        public string CollectionName { get; set; } = "";
        public string Content { get; set; } = "";
        public string ContentHash { get; set; } = "";
        public string? Title { get; set; }
        public string? DocType { get; set; }
        public string Metadata { get; set; } = "{}";     // JSON string - preserved exactly
        public string ChangeType { get; set; } = "";     // "new", "modified", "deleted"
        
        public DocumentDeltaV2() { }
        
        public DocumentDeltaV2(string docId, string collectionName, string content, 
            string contentHash, string? title, string? docType, string metadata, string changeType)
        {
            DocId = docId;
            CollectionName = collectionName;
            Content = content;
            ContentHash = contentHash;
            Title = title;
            DocType = docType;
            Metadata = metadata;
            ChangeType = changeType;
        }
        
        public bool IsNew => ChangeType == "new";
        public bool IsModified => ChangeType == "modified";
        public bool IsDeleted => ChangeType == "deleted";
        
        /// <summary>
        /// Parse metadata JSON into dictionary
        /// </summary>
        public Dictionary<string, object>? GetMetadataDict()
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, object>>(Metadata);
            }
            catch
            {
                return null;
            }
        }
    }
    
    /// <summary>
    /// Represents a deleted document (updated for V2)
    /// </summary>
    public class DeletedDocumentV2
    {
        public string DocId { get; set; } = "";
        public string CollectionName { get; set; } = "";
        public string ChunkIds { get; set; } = "[]";     // JSON array of chunk IDs
        public string? OriginalContentHash { get; set; } // Content hash from deletion tracking
        
        public DeletedDocumentV2() { }
        
        public DeletedDocumentV2(string docId, string collectionName, string chunkIds, string? originalContentHash = null)
        {
            DocId = docId;
            CollectionName = collectionName;
            ChunkIds = chunkIds;
            OriginalContentHash = originalContentHash;
        }
        
        public List<string> GetChunkIdList()
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(ChunkIds) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
    
    #endregion
    
    #region Chroma-to-Dolt Sync Types (New for V2)
    
    /// <summary>
    /// Represents a document in ChromaDB that may need syncing to Dolt
    /// </summary>
    public record ChromaDocument(
        string DocId,                              // Document identifier
        string CollectionName,                     // ChromaDB collection name
        string Content,                            // Reassembled content from chunks
        string ContentHash,                        // Computed hash of content
        Dictionary<string, object> Metadata,       // All metadata from ChromaDB
        List<dynamic>? Chunks = null              // Original chunks if needed
    )
    {
        /// <summary>
        /// Check if this document has a local change flag in metadata
        /// </summary>
        public bool IsLocalChange => 
            Metadata.TryGetValue("is_local_change", out var value) && 
            (value?.ToString()?.ToLower() == "true" || value?.Equals(true) == true);
        
        /// <summary>
        /// Extract title from metadata
        /// </summary>
        public string? GetTitle() => 
            Metadata.TryGetValue("title", out var title) ? title?.ToString() : null;
        
        /// <summary>
        /// Extract document type from metadata
        /// </summary>
        public string? GetDocType() => 
            Metadata.TryGetValue("doc_type", out var type) ? type?.ToString() : null;
    }
    
    /// <summary>
    /// Represents local changes detected in ChromaDB that need staging to Dolt
    /// </summary>
    public record LocalChanges(
        List<ChromaDocument> NewDocuments,         // Documents that exist only in ChromaDB
        List<ChromaDocument> ModifiedDocuments,    // Documents modified in ChromaDB
        List<DeletedDocumentV2> DeletedDocuments  // Documents deleted from ChromaDB
    )
    {
        public bool HasChanges => NewDocuments.Count > 0 || ModifiedDocuments.Count > 0 || DeletedDocuments.Count > 0;
        public int TotalChanges => NewDocuments.Count + ModifiedDocuments.Count + DeletedDocuments.Count;
        
        /// <summary>
        /// Get a summary string of changes
        /// </summary>
        public string GetSummary() => 
            $"{NewDocuments.Count} new, {ModifiedDocuments.Count} modified, {DeletedDocuments.Count} deleted";
        
        /// <summary>
        /// Get all collection names affected by these changes
        /// </summary>
        public IEnumerable<string> GetAffectedCollectionNames()
        {
            var collections = new HashSet<string>();
            
            foreach (var doc in NewDocuments)
                collections.Add(doc.CollectionName);
            
            foreach (var doc in ModifiedDocuments)
                collections.Add(doc.CollectionName);
            
            foreach (var doc in DeletedDocuments)
                collections.Add(doc.CollectionName);
            
            return collections;
        }
    }
    
    #endregion
    
    #region Sync Operation Results (New for V2)
    
    /// <summary>
    /// Result of staging local changes from ChromaDB to Dolt
    /// </summary>
    public record StageResult(
        StageStatus Status,
        int Added,
        int Modified,
        int Deleted,
        string? ErrorMessage = null
    )
    {
        public bool Success => Status == StageStatus.Completed;
        public int TotalStaged => Added + Modified + Deleted;
        
        public string GetSummary() => Status switch
        {
            StageStatus.Completed => $"Staged {TotalStaged} changes ({Added} added, {Modified} modified, {Deleted} deleted)",
            StageStatus.NoChanges => "No changes to stage",
            StageStatus.Failed => $"Staging failed: {ErrorMessage}",
            _ => "Unknown status"
        };
    }
    
    /// <summary>
    /// Status of staging operation
    /// </summary>
    public enum StageStatus
    {
        Completed,
        NoChanges,
        Failed
    }
    
    /// <summary>
    /// Result of initializing version control from ChromaDB
    /// </summary>
    public record InitResult(
        InitStatus Status,
        int DocumentsImported,
        string? CommitHash = null,
        string? ErrorMessage = null
    )
    {
        public bool Success => Status == InitStatus.Completed;
        
        public string GetSummary() => Status switch
        {
            InitStatus.Completed => $"Initialized with {DocumentsImported} documents (commit: {CommitHash})",
            InitStatus.Failed => $"Initialization failed: {ErrorMessage}",
            _ => "Unknown status"
        };
    }
    
    /// <summary>
    /// Status of initialization operation
    /// </summary>
    public enum InitStatus
    {
        Completed,
        Failed
    }
    
    #endregion
    
    #region Status and State Types (New/Updated for V2)
    
    /// <summary>
    /// Overall status summary for sync operations
    /// </summary>
    public class StatusSummary
    {
        public string Branch { get; set; } = "";
        public string CurrentCommit { get; set; } = "";
        public string CollectionName { get; set; } = "";
        public LocalChanges? LocalChanges { get; set; }
        public bool HasUncommittedDoltChanges { get; set; }
        public bool HasUncommittedChromaChanges { get; set; }
        
        public bool IsClean => !HasUncommittedDoltChanges && !HasUncommittedChromaChanges;
        
        public string GetSummary()
        {
            var parts = new List<string>();
            
            parts.Add($"Branch: {Branch}");
            parts.Add($"Commit: {CurrentCommit[..Math.Min(8, CurrentCommit.Length)]}");
            
            if (HasUncommittedDoltChanges)
                parts.Add("Dolt has uncommitted changes");
            
            if (HasUncommittedChromaChanges && LocalChanges != null)
                parts.Add($"ChromaDB has {LocalChanges.TotalChanges} local changes");
            
            if (IsClean)
                parts.Add("Working directory clean");
            
            return string.Join(" | ", parts);
        }
    }
    
    /// <summary>
    /// Direction of sync operation
    /// </summary>
    public enum SyncDirection
    {
        DoltToChroma,      // Traditional sync from version control to working copy
        ChromaToDolt,      // Stage changes from working copy to version control
        Bidirectional      // Both directions in one operation
    }
    
    #endregion
    
    #region Sync Log and State (Updated for V2)
    
    /// <summary>
    /// Represents a row from the document_sync_log table (V2)
    /// </summary>
    public record SyncLogEntryV2(
        int Id,
        string DocId,
        string CollectionName,
        string ContentHash,
        string ChromaChunkIds,      // JSON array of chunk IDs
        DateTime SyncedAt,
        SyncDirection SyncDirection,
        string SyncAction,           // "added", "modified", "deleted", "staged"
        string? EmbeddingModel
    )
    {
        public bool IsCurrent(string currentHash) => ContentHash == currentHash;
        
        public List<string> GetChunkIdList()
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(ChromaChunkIds) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
    
    /// <summary>
    /// Represents the synchronization state for a ChromaDB collection (V2)
    /// </summary>
    public record ChromaSyncStateV2(
        string CollectionName,
        string? LastSyncCommit,
        DateTime? LastSyncAt,
        int DocumentCount,
        int ChunkCount,
        string? EmbeddingModel,
        string SyncStatus,           // "synced", "pending", "error", "in_progress", "local_changes"
        int LocalChangesCount,       // NEW: Number of uncommitted local changes
        string? ErrorMessage,
        string? Metadata
    )
    {
        public bool IsSynced => SyncStatus == "synced";
        public bool HasError => SyncStatus == "error";
        public bool HasLocalChanges => SyncStatus == "local_changes" || LocalChangesCount > 0;
        public bool NeedsFullSync => string.IsNullOrEmpty(LastSyncCommit);
    }
    
    #endregion
}