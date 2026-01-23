using System.Collections.Generic;

namespace Embranch.Models
{
    /// <summary>
    /// Represents a document that has changed and needs synchronization between Dolt and ChromaDB.
    /// Contains all information needed to update ChromaDB with the latest content.
    /// </summary>
    public class DocumentDelta
    {
        public string SourceTable { get; set; } = "";    // "issue_logs" or "knowledge_docs"
        public string SourceId { get; set; } = "";       // Primary key from source table
        public string Content { get; set; } = "";        // Full text content of the document
        public string ContentHash { get; set; } = "";    // SHA-256 hash for change detection
        public string Identifier { get; set; } = "";     // Human-readable identifier (project_id or tool_name)
        public string Metadata { get; set; } = "";       // JSON metadata string with additional fields
        public string ChangeType { get; set; } = "";     // "new" or "modified"
        
        public DocumentDelta() { }
        
        public DocumentDelta(string sourceTable, string sourceId, string content, string contentHash, 
            string identifier, string metadata, string changeType)
        {
            SourceTable = sourceTable;
            SourceId = sourceId;
            Content = content;
            ContentHash = contentHash;
            Identifier = identifier;
            Metadata = metadata;
            ChangeType = changeType;
        }
        
        /// <summary>
        /// Check if this is a new document (not previously synced)
        /// </summary>
        public bool IsNew => ChangeType == "new";
        
        /// <summary>
        /// Check if this is a modified document (previously synced but content changed)
        /// </summary>
        public bool IsModified => ChangeType == "modified";
    }

    /// <summary>
    /// Represents a document that has been deleted from Dolt but still exists in ChromaDB.
    /// Used to identify chunks that need to be removed from the vector database.
    /// </summary>
    public class DeletedDocument
    {
        public string SourceTable { get; set; } = "";        // "issue_logs" or "knowledge_docs"  
        public string SourceId { get; set; } = "";           // Primary key that no longer exists
        public string ChromaCollection { get; set; } = "";   // Collection containing the orphaned chunks
        public string ChunkIds { get; set; } = "";           // JSON array of chunk IDs to delete
        
        public DeletedDocument() { }
        
        public DeletedDocument(string sourceTable, string sourceId, string chromaCollection, string chunkIds)
        {
            SourceTable = sourceTable;
            SourceId = sourceId;
            ChromaCollection = chromaCollection;
            ChunkIds = chunkIds;
        }
        
        /// <summary>
        /// Parse the JSON chunk IDs into a list
        /// </summary>
        public List<string> GetChunkIdList()
        {
            if (string.IsNullOrEmpty(ChunkIds))
                return new List<string>();
                
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<string>>(ChunkIds) 
                    ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }
    }

    /// <summary>
    /// Represents a document from Dolt database with all fields needed for ChromaDB conversion.
    /// This is the complete document structure before chunking and embedding.
    /// </summary>
    public record DoltDocument(
        string SourceTable,         // Table this document comes from
        string SourceId,            // Primary key in the source table
        string Content,             // Full text content to be chunked
        string ContentHash,         // SHA-256 hash for change tracking
        string? ProjectId = null,   // For issue_logs: associated project
        int IssueNumber = 0,        // For issue_logs: issue number
        string? LogType = null,     // For issue_logs: type of log entry
        string? Title = null,       // Document title for both tables
        string? Category = null,    // For knowledge_docs: category
        string? ToolName = null,    // For knowledge_docs: tool name
        string? ToolVersion = null, // For knowledge_docs: tool version
        Dictionary<string, object>? CustomMetadata = null  // Additional metadata fields
    )
    {
        /// <summary>
        /// Check if this document is from the issue_logs table
        /// </summary>
        public bool IsIssueLog => SourceTable == "issue_logs";
        
        /// <summary>
        /// Check if this document is from the knowledge_docs table
        /// </summary>
        public bool IsKnowledgeDoc => SourceTable == "knowledge_docs";
        
        /// <summary>
        /// Get a display name for this document
        /// </summary>
        public string GetDisplayName()
        {
            if (IsIssueLog)
                return $"Issue #{IssueNumber} - {Title ?? "Untitled"}";
            else if (IsKnowledgeDoc)
                return $"{ToolName} - {Title ?? "Untitled"}";
            else
                return Title ?? SourceId;
        }
    }

    /// <summary>
    /// Represents a row from the document_sync_log table.
    /// Tracks which documents have been synced to which ChromaDB collections.
    /// </summary>
    public record SyncLogEntry(
        int Id,                     // Auto-increment primary key
        string SourceTable,         // Source table name
        string SourceId,            // Document ID in source table
        string ContentHash,         // Content hash at time of sync
        string ChromaCollection,    // Target ChromaDB collection
        string ChunkIds,           // JSON array of chunk IDs created
        DateTime SyncedAt,          // When the sync occurred
        string? EmbeddingModel,     // Model used for embeddings
        string SyncAction          // "added", "modified", or "deleted"
    )
    {
        /// <summary>
        /// Check if this sync entry is current (content hash matches)
        /// </summary>
        public bool IsCurrent(string currentHash) => ContentHash == currentHash;
        
        /// <summary>
        /// Parse the chunk IDs JSON into a list
        /// </summary>
        public List<string> GetChunkIdList() => 
            string.IsNullOrEmpty(ChunkIds) 
                ? new List<string>()
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(ChunkIds) ?? new List<string>();
    }

    /// <summary>
    /// Represents the synchronization state for a ChromaDB collection.
    /// Tracks overall sync status and statistics.
    /// </summary>
    public record ChromaSyncState(
        string CollectionName,      // ChromaDB collection name
        string? LastSyncCommit,     // Last Dolt commit that was synced
        DateTime? LastSyncAt,       // When the last sync occurred
        int DocumentCount,          // Total documents in collection
        int ChunkCount,            // Total chunks in collection
        string? EmbeddingModel,     // Embedding model used
        string SyncStatus,          // "synced", "pending", "error", "in_progress"
        string? ErrorMessage,       // Error details if status is "error"
        string? Metadata           // Additional JSON metadata
    )
    {
        /// <summary>
        /// Check if this collection is fully synced
        /// </summary>
        public bool IsSynced => SyncStatus == "synced";
        
        /// <summary>
        /// Check if this collection has an error
        /// </summary>
        public bool HasError => SyncStatus == "error";
        
        /// <summary>
        /// Check if this collection needs a full sync (no previous sync)
        /// </summary>
        public bool NeedsFullSync => string.IsNullOrEmpty(LastSyncCommit);
    }
}