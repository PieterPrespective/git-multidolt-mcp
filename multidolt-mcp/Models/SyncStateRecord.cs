using System;

namespace Embranch.Models
{
    /// <summary>
    /// Represents a synchronization state record between ChromaDB and Dolt for external tracking
    /// Stored in SQLite to avoid versioning conflicts in Dolt
    /// </summary>
    public struct SyncStateRecord
    {
        /// <summary>
        /// Unique identifier for the sync state record
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Repository path for isolation
        /// </summary>
        public string RepoPath { get; set; }

        /// <summary>
        /// Collection name
        /// </summary>
        public string CollectionName { get; set; }

        /// <summary>
        /// Current branch name context
        /// </summary>
        public string? BranchContext { get; set; }

        /// <summary>
        /// Last synced Dolt commit hash
        /// </summary>
        public string? LastSyncCommit { get; set; }

        /// <summary>
        /// Timestamp of last sync
        /// </summary>
        public DateTime? LastSyncAt { get; set; }

        /// <summary>
        /// Number of documents synced
        /// </summary>
        public int DocumentCount { get; set; }

        /// <summary>
        /// Number of chunks created
        /// </summary>
        public int ChunkCount { get; set; }

        /// <summary>
        /// Embedding model used
        /// </summary>
        public string? EmbeddingModel { get; set; }

        /// <summary>
        /// Status: synced, pending, error, in_progress, local_changes
        /// </summary>
        public string SyncStatus { get; set; }

        /// <summary>
        /// Count of uncommitted local changes
        /// </summary>
        public int LocalChangesCount { get; set; }

        /// <summary>
        /// Any error messages
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// JSON metadata
        /// </summary>
        public string? Metadata { get; set; }

        /// <summary>
        /// When this record was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When this record was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Constructor for creating a new sync state record
        /// </summary>
        public SyncStateRecord(string repoPath, string collectionName, string? branchContext = null)
        {
            Id = Guid.NewGuid().ToString();
            RepoPath = repoPath;
            CollectionName = collectionName;
            BranchContext = branchContext;
            LastSyncCommit = null;
            LastSyncAt = null;
            DocumentCount = 0;
            ChunkCount = 0;
            EmbeddingModel = null;
            SyncStatus = "pending";
            LocalChangesCount = 0;
            ErrorMessage = null;
            Metadata = null;
            CreatedAt = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Constructor for creating a sync state record with specific values
        /// </summary>
        public SyncStateRecord(string id, string repoPath, string collectionName, string? branchContext, 
            string? lastSyncCommit, DateTime? lastSyncAt, int documentCount, int chunkCount, 
            string? embeddingModel, string syncStatus, int localChangesCount, string? errorMessage, 
            string? metadata, DateTime createdAt, DateTime updatedAt)
        {
            Id = id;
            RepoPath = repoPath;
            CollectionName = collectionName;
            BranchContext = branchContext;
            LastSyncCommit = lastSyncCommit;
            LastSyncAt = lastSyncAt;
            DocumentCount = documentCount;
            ChunkCount = chunkCount;
            EmbeddingModel = embeddingModel;
            SyncStatus = syncStatus;
            LocalChangesCount = localChangesCount;
            ErrorMessage = errorMessage;
            Metadata = metadata;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
        }

        /// <summary>
        /// Creates an updated copy with a new status
        /// </summary>
        public SyncStateRecord WithStatus(string newStatus, string? errorMessage = null)
        {
            return new SyncStateRecord(Id, RepoPath, CollectionName, BranchContext, LastSyncCommit, LastSyncAt,
                DocumentCount, ChunkCount, EmbeddingModel, newStatus, LocalChangesCount, errorMessage,
                Metadata, CreatedAt, DateTime.UtcNow);
        }

        /// <summary>
        /// Creates an updated copy with new sync information
        /// </summary>
        public SyncStateRecord WithSyncUpdate(string commitHash, int docCount, int chunkCount, string? embeddingModel = null)
        {
            return new SyncStateRecord(Id, RepoPath, CollectionName, BranchContext, commitHash, DateTime.UtcNow,
                docCount, chunkCount, embeddingModel ?? EmbeddingModel, "synced", 0, null,
                Metadata, CreatedAt, DateTime.UtcNow);
        }

        /// <summary>
        /// Creates an updated copy with new local changes count
        /// </summary>
        public SyncStateRecord WithLocalChanges(int localChangesCount)
        {
            return new SyncStateRecord(Id, RepoPath, CollectionName, BranchContext, LastSyncCommit, LastSyncAt,
                DocumentCount, ChunkCount, EmbeddingModel, localChangesCount > 0 ? "local_changes" : "synced", 
                localChangesCount, ErrorMessage, Metadata, CreatedAt, DateTime.UtcNow);
        }
    }
}