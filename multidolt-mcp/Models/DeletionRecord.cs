using System;
using System.Collections.Generic;

namespace Embranch.Models
{
    /// <summary>
    /// Represents a document deletion record for external tracking
    /// </summary>
    public struct DeletionRecord
    {
        /// <summary>
        /// Unique identifier for the deletion record
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Path to the repository where the deletion occurred
        /// </summary>
        public string RepoPath { get; set; }

        /// <summary>
        /// The document ID that was deleted
        /// </summary>
        public string DocId { get; set; }

        /// <summary>
        /// The collection name where the document was deleted
        /// </summary>
        public string CollectionName { get; set; }

        /// <summary>
        /// When the deletion occurred
        /// </summary>
        public DateTime DeletedAt { get; set; }

        /// <summary>
        /// Source of the deletion (e.g., 'mcp_tool', 'sync')
        /// </summary>
        public string DeletionSource { get; set; }

        /// <summary>
        /// Original content hash of the deleted document
        /// </summary>
        public string? OriginalContentHash { get; set; }

        /// <summary>
        /// JSON serialized original metadata of the deleted document
        /// </summary>
        public string? OriginalMetadata { get; set; }

        /// <summary>
        /// Branch context where deletion occurred
        /// </summary>
        public string? BranchContext { get; set; }

        /// <summary>
        /// Base commit hash when deletion occurred
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
        /// Constructor for creating a new deletion record
        /// </summary>
        public DeletionRecord(string docId, string collectionName, string repoPath, 
            string deletionSource, string? originalContentHash = null, 
            string? originalMetadata = null, string? branchContext = null, 
            string? baseCommitHash = null)
        {
            Id = Guid.NewGuid().ToString();
            RepoPath = repoPath;
            DocId = docId;
            CollectionName = collectionName;
            DeletedAt = DateTime.UtcNow;
            DeletionSource = deletionSource;
            OriginalContentHash = originalContentHash;
            OriginalMetadata = originalMetadata;
            BranchContext = branchContext;
            BaseCommitHash = baseCommitHash;
            SyncStatus = "pending";
            CreatedAt = DateTime.UtcNow;
        }
    }
}