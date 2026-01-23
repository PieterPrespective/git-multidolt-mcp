using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Embranch.Models;

namespace Embranch.Services
{
    /// <summary>
    /// Interface for tracking synchronization state between ChromaDB and Dolt
    /// Stores metadata locally in SQLite to avoid versioning conflicts in Dolt
    /// </summary>
    public interface ISyncStateTracker
    {
        /// <summary>
        /// Initializes the sync state tracker for a specific repository
        /// </summary>
        /// <param name="repoPath">Path to the Dolt repository</param>
        Task InitializeAsync(string repoPath);

        /// <summary>
        /// Gets the sync state for a specific collection
        /// </summary>
        /// <param name="repoPath">Path to the Dolt repository</param>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="branchContext">Optional branch context (defaults to current branch)</param>
        /// <returns>The sync state record if found, null otherwise</returns>
        Task<SyncStateRecord?> GetSyncStateAsync(string repoPath, string collectionName, string? branchContext = null);

        /// <summary>
        /// Updates or creates a sync state record for a collection
        /// </summary>
        /// <param name="repoPath">Path to the Dolt repository</param>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="state">The sync state record to save</param>
        Task UpdateSyncStateAsync(string repoPath, string collectionName, SyncStateRecord state);

        /// <summary>
        /// Gets all sync states for a repository
        /// </summary>
        /// <param name="repoPath">Path to the Dolt repository</param>
        /// <returns>List of all sync state records for the repository</returns>
        Task<List<SyncStateRecord>> GetAllSyncStatesAsync(string repoPath);

        /// <summary>
        /// Clears all sync states for a specific branch
        /// </summary>
        /// <param name="repoPath">Path to the Dolt repository</param>
        /// <param name="branchContext">The branch to clear sync states for</param>
        Task ClearBranchSyncStatesAsync(string repoPath, string branchContext);

        /// <summary>
        /// Reconstructs sync state after a branch checkout
        /// </summary>
        /// <param name="repoPath">Path to the Dolt repository</param>
        /// <param name="newBranch">The branch that was checked out</param>
        /// <returns>True if reconstruction was successful, false otherwise</returns>
        Task<bool> ReconstructSyncStateAsync(string repoPath, string newBranch);

        /// <summary>
        /// Gets sync states for a specific branch
        /// </summary>
        /// <param name="repoPath">Path to the Dolt repository</param>
        /// <param name="branchContext">The branch to get sync states for</param>
        /// <returns>List of sync state records for the branch</returns>
        Task<List<SyncStateRecord>> GetBranchSyncStatesAsync(string repoPath, string branchContext);

        /// <summary>
        /// Deletes a specific sync state record
        /// </summary>
        /// <param name="repoPath">Path to the Dolt repository</param>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="branchContext">Optional branch context</param>
        Task DeleteSyncStateAsync(string repoPath, string collectionName, string? branchContext = null);

        /// <summary>
        /// Updates the commit hash for a sync state
        /// </summary>
        /// <param name="repoPath">Path to the Dolt repository</param>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="commitHash">The new commit hash</param>
        /// <param name="branchContext">Optional branch context</param>
        Task UpdateCommitHashAsync(string repoPath, string collectionName, string commitHash, string? branchContext = null);

        /// <summary>
        /// Cleans up stale sync states (e.g., for deleted collections)
        /// </summary>
        /// <param name="repoPath">Path to the Dolt repository</param>
        Task CleanupStaleSyncStatesAsync(string repoPath);
    }
}