using DMMS.Models;
using System.ComponentModel;

namespace DMMS.Services
{
    /// <summary>
    /// V2 Interface for bidirectional synchronization between Dolt version control and ChromaDB.
    /// Treats ChromaDB as working copy and Dolt as version control repository.
    /// </summary>
    public interface ISyncManagerV2
    {
        // ==================== Initialization ====================
        
        /// <summary>
        /// Initialize version control for an existing ChromaDB collection
        /// </summary>
        /// <param name="collectionName">ChromaDB collection to version control</param>
        /// <param name="initialCommitMessage">Message for initial commit</param>
        /// <returns>Result indicating success and documents imported</returns>
        Task<InitResult> InitializeVersionControlAsync(string collectionName, string initialCommitMessage = "Initial import from ChromaDB");
        
        // ==================== Status Operations ====================
        
        /// <summary>
        /// Get comprehensive status of sync state
        /// </summary>
        /// <returns>Status summary including local changes and branch info</returns>
        Task<StatusSummary> GetStatusAsync();
        
        /// <summary>
        /// Get local changes in ChromaDB that haven't been staged to Dolt
        /// </summary>
        /// <returns>Details of new, modified, and deleted documents in ChromaDB</returns>
        Task<LocalChanges> GetLocalChangesAsync();
        
        // ==================== Commit Processing (Updated) ====================
        
        /// <summary>
        /// Process a commit operation with optional auto-staging from ChromaDB
        /// </summary>
        /// <param name="message">Commit message describing the changes</param>
        /// <param name="autoStageFromChroma">Whether to automatically stage ChromaDB changes before commit</param>
        /// <param name="syncBackToChroma">Whether to sync committed changes back to ChromaDB</param>
        /// <returns>Result indicating success, staged count, and sync statistics</returns>
        Task<SyncResultV2> ProcessCommitAsync(string message, bool autoStageFromChroma = true, bool syncBackToChroma = false);
        
        // ==================== Pull Processing (Updated) ====================
        
        /// <summary>
        /// Process a pull operation from remote and sync changes to ChromaDB
        /// </summary>
        /// <param name="remote">Name of the remote to pull from</param>
        /// <param name="force">Force pull even if local changes exist (will overwrite)</param>
        /// <returns>Result indicating success or blocked by local changes</returns>
        Task<SyncResultV2> ProcessPullAsync(string remote = "origin", bool force = false);
        
        // ==================== Checkout Processing (Updated) ====================
        
        /// <summary>
        /// Process a branch checkout and manage ChromaDB collection state
        /// </summary>
        /// <param name="targetBranch">Name of the branch to checkout</param>
        /// <param name="createNew">Whether to create the branch if it doesn't exist</param>
        /// <param name="force">Force checkout even if local changes exist (will overwrite)</param>
        /// <returns>Result indicating success or blocked by local changes</returns>
        Task<SyncResultV2> ProcessCheckoutAsync(string targetBranch, bool createNew = false, bool force = false);
        
        // ==================== Merge Processing (Updated) ====================
        
        /// <summary>
        /// Process a merge operation and sync merged changes to ChromaDB
        /// </summary>
        /// <param name="sourceBranch">Name of the branch to merge from</param>
        /// <param name="force">Force merge even if local changes exist</param>
        /// <param name="resolutions">Optional conflict resolution preferences</param>
        /// <returns>Result including success status and conflict information</returns>
        Task<Services.MergeSyncResultV2> ProcessMergeAsync(string sourceBranch, bool force = false, List<ConflictResolutionRequest>? resolutions = null);
        
        // ==================== Push Processing ====================
        
        /// <summary>
        /// Process a push operation to remote repository
        /// </summary>
        /// <param name="remote">Name of the remote to push to</param>
        /// <param name="branch">Branch to push (null for current branch)</param>
        /// <returns>Result indicating success</returns>
        Task<SyncResultV2> ProcessPushAsync(string remote = "origin", string? branch = null);
        
        // ==================== Reset Processing ====================
        
        /// <summary>
        /// Process a reset operation and rebuild ChromaDB state
        /// </summary>
        /// <param name="targetCommit">Commit hash to reset to</param>
        /// <param name="hard">Whether to perform hard reset (discards local changes)</param>
        /// <returns>Result indicating success and sync regeneration</returns>
        Task<SyncResultV2> ProcessResetAsync(string targetCommit, bool hard = false);
        
        // ==================== Change Detection ====================
        
        /// <summary>
        /// Check if there are uncommitted changes in Dolt or ChromaDB
        /// </summary>
        /// <returns>True if changes need to be processed</returns>
        Task<bool> HasPendingChangesAsync();
        
        /// <summary>
        /// Get detailed information about pending changes in both systems
        /// </summary>
        /// <returns>Summary of changes in Dolt and ChromaDB</returns>
        Task<PendingChangesV2> GetPendingChangesAsync();
        
        // ==================== Manual Sync Operations ====================
        
        /// <summary>
        /// Perform a full synchronization from Dolt to ChromaDB
        /// </summary>
        /// <param name="collectionName">Target ChromaDB collection name</param>
        /// <param name="forceSync">Force sync by deleting and recreating collections, bypassing count optimization</param>
        /// <returns>Result indicating success and number of documents processed</returns>
        Task<SyncResultV2> FullSyncAsync(string? collectionName = null, bool forceSync = false);
        
        /// <summary>
        /// Perform an incremental sync of pending changes
        /// </summary>
        /// <param name="collectionName">Target ChromaDB collection name</param>
        /// <returns>Result indicating success and changes processed</returns>
        Task<SyncResultV2> IncrementalSyncAsync(string? collectionName = null);
        
        /// <summary>
        /// Stage local ChromaDB changes to Dolt (like git add)
        /// </summary>
        /// <param name="collectionName">Collection with local changes to stage</param>
        /// <returns>Result indicating staged changes</returns>
        Task<StageResult> StageLocalChangesAsync(string collectionName);
        
        /// <summary>
        /// Stage pre-detected local changes from ChromaDB to Dolt (like git add .)
        /// This overload eliminates redundant change detection and ensures consistency.
        /// </summary>
        /// <param name="collectionName">Collection with local changes to stage</param>
        /// <param name="localChanges">Pre-detected changes to stage</param>
        /// <returns>Result indicating staged changes</returns>
        Task<StageResult> StageLocalChangesAsync(string collectionName, LocalChanges localChanges);
        
        // ==================== Import/Export ====================
        
        /// <summary>
        /// Import documents from a ChromaDB collection into Dolt
        /// </summary>
        /// <param name="sourceCollection">Source ChromaDB collection name</param>
        /// <param name="commitMessage">Optional commit message for the import</param>
        /// <returns>Result indicating success and number of documents imported</returns>
        Task<SyncResultV2> ImportFromChromaAsync(string sourceCollection, string? commitMessage = null);
        
        // ==================== Collection-Level Sync Operations (PP13-61) ====================
        
        /// <summary>
        /// Synchronize collection-level changes (deletion, rename, metadata updates) from ChromaDB to Dolt
        /// </summary>
        /// <returns>Result indicating success and collection changes processed</returns>
        Task<CollectionSyncResult> SyncCollectionChangesAsync();
        
        /// <summary>
        /// Stage collection-level changes (deletion, rename, metadata updates) to Dolt
        /// </summary>
        /// <returns>Result indicating collection changes staged</returns>
        Task<CollectionSyncResult> StageCollectionChangesAsync();
    }
    
    // ==================== Supporting Types (V2) ====================
    
    /// <summary>
    /// Enhanced result of a sync operation with bidirectional support
    /// </summary>
    public class SyncResultV2
    {
        public SyncStatusV2 Status { get; set; } = SyncStatusV2.Pending;
        public string? ErrorMessage { get; set; }
        public string? CommitHash { get; set; }
        public bool WasFastForward { get; set; }
        
        // Bidirectional sync statistics
        public int Added { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }
        public int StagedFromChroma { get; set; }    // NEW: Documents staged from ChromaDB
        public int ChunksProcessed { get; set; }
        
        // Additional context
        public LocalChanges? LocalChanges { get; set; }           // NEW: Local changes detected
        public SyncDirection Direction { get; set; }              // NEW: Direction of sync
        public object? Data { get; set; }                         // NEW: Additional operation-specific data
        
        public bool Success => Status == SyncStatusV2.Completed || Status == SyncStatusV2.NoChanges;
        public int TotalChanges => Added + Modified + Deleted;
        public int TotalStaged => StagedFromChroma;
        
        public string GetSummary()
        {
            if (Status == SyncStatusV2.LocalChangesExist)
                return $"Operation blocked: {LocalChanges?.TotalChanges ?? 0} local changes exist. Use --force to override.";
            
            if (!Success)
                return $"Operation failed: {ErrorMessage}";
            
            var parts = new List<string>();
            
            if (StagedFromChroma > 0)
                parts.Add($"Staged {StagedFromChroma} from ChromaDB");
            
            if (TotalChanges > 0)
                parts.Add($"Synced {TotalChanges} changes ({Added} added, {Modified} modified, {Deleted} deleted)");
            
            if (CommitHash != null)
                parts.Add($"Commit: {CommitHash[..Math.Min(8, CommitHash.Length)]}");
            
            return parts.Count > 0 ? string.Join(". ", parts) : "No changes";
        }
    }
    
    /// <summary>
    /// Result of a merge operation with bidirectional sync
    /// </summary>
    public class MergeSyncResultV2 : SyncResultV2
    {
        public bool HasConflicts { get; set; }
        public List<ConflictInfoV2> Conflicts { get; set; } = new();
        public string? MergeCommitHash { get; set; }
        public int? CollectionsSynced { get; set; }
        
        public MergeSyncStatusV2 MergeStatus => 
            Status == SyncStatusV2.LocalChangesExist ? MergeSyncStatusV2.LocalChangesExist :
            HasConflicts ? MergeSyncStatusV2.ConflictsDetected : 
            Success ? MergeSyncStatusV2.Completed : 
            MergeSyncStatusV2.Failed;
    }
    
    /// <summary>
    /// Information about pending changes in both Dolt and ChromaDB
    /// </summary>
    public class PendingChangesV2
    {
        // Dolt changes (traditional)
        public List<DocumentDeltaV2> DoltNewDocuments { get; set; } = new();
        public List<DocumentDeltaV2> DoltModifiedDocuments { get; set; } = new();
        public List<DeletedDocumentV2> DoltDeletedDocuments { get; set; } = new();
        
        // ChromaDB local changes (new)
        public LocalChanges? ChromaLocalChanges { get; set; }
        
        public int TotalDoltChanges => DoltNewDocuments.Count + DoltModifiedDocuments.Count + DoltDeletedDocuments.Count;
        public int TotalChromaChanges => ChromaLocalChanges?.TotalChanges ?? 0;
        public bool HasChanges => TotalDoltChanges > 0 || TotalChromaChanges > 0;
        
        public string GetSummary()
        {
            var parts = new List<string>();
            
            if (TotalDoltChanges > 0)
                parts.Add($"Dolt: {TotalDoltChanges} changes");
            
            if (TotalChromaChanges > 0)
                parts.Add($"ChromaDB: {TotalChromaChanges} local changes");
            
            return parts.Count > 0 ? string.Join(", ", parts) : "No pending changes";
        }
    }
    
    /// <summary>
    /// Information about a merge conflict (V2)
    /// </summary>
    public class ConflictInfoV2
    {
        public string DocId { get; set; } = "";
        public string CollectionName { get; set; } = "";
        public string ConflictType { get; set; } = "";  // "content", "metadata", "both"
        public string OurVersion { get; set; } = "";
        public string TheirVersion { get; set; } = "";
        public string? Resolution { get; set; }
    }
    
    /// <summary>
    /// Status of a sync operation (V2)
    /// </summary>
    public enum SyncStatusV2
    {
        Pending,
        InProgress,
        Completed,
        NoChanges,
        Failed,
        Conflicts,
        LocalChangesExist      // NEW: Operation blocked due to local changes
    }
    
    /// <summary>
    /// Status of a merge operation with sync (V2)
    /// </summary>
    public enum MergeSyncStatusV2
    {
        Pending,
        InProgress,
        Completed,
        ConflictsDetected,
        Failed,
        LocalChangesExist      // NEW: Merge blocked due to local changes
    }
    
    /// <summary>
    /// Result of collection-level synchronization operations (PP13-61)
    /// </summary>
    public class CollectionSyncResult
    {
        public SyncStatusV2 Status { get; set; } = SyncStatusV2.Pending;
        public string? ErrorMessage { get; set; }
        public string? CommitHash { get; set; }
        
        // Collection-level sync statistics
        public int CollectionsDeleted { get; set; }
        public int CollectionsRenamed { get; set; }
        public int CollectionsUpdated { get; set; }
        public int DocumentsDeletedByCollectionDeletion { get; set; } // Cascade deletion count
        
        // Collection operation details
        public List<string> DeletedCollectionNames { get; set; } = new();
        public List<string> RenamedCollectionNames { get; set; } = new();
        public List<string> UpdatedCollectionNames { get; set; } = new();
        
        public bool Success => Status == SyncStatusV2.Completed || Status == SyncStatusV2.NoChanges;
        public int TotalCollectionChanges => CollectionsDeleted + CollectionsRenamed + CollectionsUpdated;
        
        public string GetSummary()
        {
            if (!Success)
                return $"Collection sync failed: {ErrorMessage}";
            
            var parts = new List<string>();
            
            if (CollectionsDeleted > 0)
                parts.Add($"Deleted {CollectionsDeleted} collections");
            
            if (CollectionsRenamed > 0)
                parts.Add($"Renamed {CollectionsRenamed} collections");
            
            if (CollectionsUpdated > 0)
                parts.Add($"Updated {CollectionsUpdated} collections");
            
            if (DocumentsDeletedByCollectionDeletion > 0)
                parts.Add($"Cascade deleted {DocumentsDeletedByCollectionDeletion} documents");
            
            if (CommitHash != null)
                parts.Add($"Commit: {CommitHash[..Math.Min(8, CommitHash.Length)]}");
            
            return parts.Count > 0 ? string.Join(". ", parts) : "No collection changes";
        }
    }
}