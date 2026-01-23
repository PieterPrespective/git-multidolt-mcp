using Embranch.Models;

namespace Embranch.Services
{
    /// <summary>
    /// Interface for synchronizing data between Dolt version control and ChromaDB vector database.
    /// Coordinates operations to maintain consistency between the two systems.
    /// </summary>
    public interface ISyncManager
    {
        // ==================== Commit Processing ====================
        
        /// <summary>
        /// Process a commit operation and optionally sync changes to ChromaDB
        /// </summary>
        /// <param name="message">Commit message describing the changes</param>
        /// <param name="syncAfter">Whether to automatically sync changes to ChromaDB after commit</param>
        /// <returns>Result indicating success and sync statistics</returns>
        Task<SyncResult> ProcessCommitAsync(string message, bool syncAfter = true);
        
        // ==================== Pull Processing ====================
        
        /// <summary>
        /// Process a pull operation from remote and sync changes to ChromaDB
        /// </summary>
        /// <param name="remote">Name of the remote to pull from (default: "origin")</param>
        /// <returns>Result indicating success, fast-forward status, and sync statistics</returns>
        Task<SyncResult> ProcessPullAsync(string remote = "origin");
        
        // ==================== Checkout Processing ====================
        
        /// <summary>
        /// Process a branch checkout and manage ChromaDB collection state
        /// </summary>
        /// <param name="targetBranch">Name of the branch to checkout</param>
        /// <param name="createNew">Whether to create the branch if it doesn't exist</param>
        /// <returns>Result indicating success and sync operations performed</returns>
        Task<SyncResult> ProcessCheckoutAsync(string targetBranch, bool createNew = false);
        
        // ==================== Merge Processing ====================
        
        /// <summary>
        /// Process a merge operation and sync merged changes to ChromaDB
        /// </summary>
        /// <param name="sourceBranch">Name of the branch to merge from</param>
        /// <returns>Result including success status and conflict information</returns>
        Task<MergeSyncResult> ProcessMergeAsync(string sourceBranch);
        
        // ==================== Reset Processing ====================
        
        /// <summary>
        /// Process a reset operation and rebuild ChromaDB state
        /// </summary>
        /// <param name="targetCommit">Commit hash to reset to</param>
        /// <returns>Result indicating success and sync regeneration</returns>
        Task<SyncResult> ProcessResetAsync(string targetCommit);
        
        // ==================== Change Detection ====================
        
        /// <summary>
        /// Check if there are uncommitted changes or pending sync operations
        /// </summary>
        /// <returns>True if changes need to be processed</returns>
        Task<bool> HasPendingChangesAsync();
        
        /// <summary>
        /// Get detailed information about pending changes
        /// </summary>
        /// <returns>Summary of new, modified, and deleted documents</returns>
        Task<PendingChanges> GetPendingChangesAsync();
        
        // ==================== Manual Sync Operations ====================
        
        /// <summary>
        /// Perform a full synchronization of the current branch to ChromaDB
        /// </summary>
        /// <param name="collectionName">Target ChromaDB collection name. If null, uses branch-based naming</param>
        /// <returns>Result indicating success and number of documents processed</returns>
        Task<SyncResult> FullSyncAsync(string? collectionName = null);
        
        /// <summary>
        /// Perform an incremental sync of pending changes
        /// </summary>
        /// <param name="collectionName">Target ChromaDB collection name. If null, uses branch-based naming</param>
        /// <returns>Result indicating success and changes processed</returns>
        Task<SyncResult> IncrementalSyncAsync(string? collectionName = null);
        
        // ==================== ChromaDB to Dolt Import ====================
        
        /// <summary>
        /// Import all documents from a ChromaDB collection into Dolt tables
        /// </summary>
        /// <param name="sourceCollection">Source ChromaDB collection name</param>
        /// <param name="commitMessage">Optional commit message for the import</param>
        /// <returns>Result indicating success and number of documents imported</returns>
        Task<SyncResult> ImportFromChromaAsync(string sourceCollection, string? commitMessage = null);
    }
    
    // ==================== Supporting Types ====================

    /// <summary>
    /// Result of a sync operation
    /// </summary>
    public class SyncResult
    {
        public SyncStatus Status { get; set; } = SyncStatus.Pending;
        public string? ErrorMessage { get; set; }
        public string? CommitHash { get; set; }
        public bool WasFastForward { get; set; }
        
        // Statistics
        public int Added { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }
        public int ChunksProcessed { get; set; }
        
        public bool Success => Status == SyncStatus.Completed || Status == SyncStatus.NoChanges;
        public int TotalChanges => Added + Modified + Deleted;
    }
    
    /// <summary>
    /// Result of a merge operation with sync
    /// </summary>
    public class MergeSyncResult : SyncResult
    {
        public bool HasConflicts { get; set; }
        public List<ConflictInfo> Conflicts { get; set; } = new();
        public MergeSyncStatus MergeStatus => HasConflicts ? MergeSyncStatus.ConflictsDetected : 
                                             Success ? MergeSyncStatus.Completed : MergeSyncStatus.Failed;
    }
    
    /// <summary>
    /// Information about pending changes that need sync
    /// </summary>
    public class PendingChanges
    {
        public List<DocumentDelta> NewDocuments { get; set; } = new();
        public List<DocumentDelta> ModifiedDocuments { get; set; } = new();
        public List<DeletedDocument> DeletedDocuments { get; set; } = new();
        
        public int TotalChanges => NewDocuments.Count + ModifiedDocuments.Count + DeletedDocuments.Count;
        public bool HasChanges => TotalChanges > 0;
    }
    
    /// <summary>
    /// Status of a sync operation
    /// </summary>
    public enum SyncStatus
    {
        Pending,
        InProgress,
        Completed,
        NoChanges,
        Failed,
        Conflicts
    }
    
    /// <summary>
    /// Status of a merge operation with sync
    /// </summary>
    public enum MergeSyncStatus
    {
        Pending,
        InProgress,
        Completed,
        ConflictsDetected,
        Failed
    }
}