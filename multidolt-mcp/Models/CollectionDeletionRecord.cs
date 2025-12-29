using System;
using System.Collections.Generic;

namespace DMMS.Models
{
    /// <summary>
    /// Represents a collection deletion record for external tracking
    /// </summary>
    public struct CollectionDeletionRecord
    {
        /// <summary>
        /// Unique identifier for the collection deletion record
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Path to the repository where the deletion occurred
        /// </summary>
        public string RepoPath { get; set; }

        /// <summary>
        /// The collection name that was deleted or modified
        /// </summary>
        public string CollectionName { get; set; }

        /// <summary>
        /// Type of operation: 'deletion', 'rename', 'metadata_update'
        /// </summary>
        public string OperationType { get; set; }

        /// <summary>
        /// When the deletion/modification occurred
        /// </summary>
        public DateTime DeletedAt { get; set; }

        /// <summary>
        /// Source of the deletion (e.g., 'mcp_tool', 'sync')
        /// </summary>
        public string DeletionSource { get; set; }

        /// <summary>
        /// JSON serialized original metadata of the collection
        /// </summary>
        public string? OriginalMetadata { get; set; }

        /// <summary>
        /// Original collection name (for rename operations)
        /// </summary>
        public string? OriginalName { get; set; }

        /// <summary>
        /// New collection name (for rename operations)
        /// </summary>
        public string? NewName { get; set; }

        /// <summary>
        /// Branch context where deletion/modification occurred
        /// </summary>
        public string? BranchContext { get; set; }

        /// <summary>
        /// Base commit hash when deletion/modification occurred
        /// </summary>
        public string? BaseCommitHash { get; set; }

        /// <summary>
        /// Sync status: 'pending', 'staged', 'committed'
        /// </summary>
        public string SyncStatus { get; set; }

        /// <summary>
        /// When this record was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Constructor for creating a new collection deletion record
        /// </summary>
        public CollectionDeletionRecord(string collectionName, string repoPath, string operationType,
            string deletionSource, string? originalMetadata = null, string? originalName = null,
            string? newName = null, string? branchContext = null, string? baseCommitHash = null)
        {
            Id = Guid.NewGuid().ToString();
            RepoPath = repoPath;
            CollectionName = collectionName;
            OperationType = operationType;
            DeletedAt = DateTime.UtcNow;
            DeletionSource = deletionSource;
            OriginalMetadata = originalMetadata;
            OriginalName = originalName;
            NewName = newName;
            BranchContext = branchContext;
            BaseCommitHash = baseCommitHash;
            SyncStatus = "pending";
            CreatedAt = DateTime.UtcNow;
        }
    }
}