using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Embranch.Services;
using Microsoft.Extensions.Logging;

namespace EmbranchTesting.UnitTests
{
    /// <summary>
    /// Utility for ensuring clean test state isolation in branch switching tests.
    /// PP13-69-C6: Enhanced state isolation framework for reliable test execution.
    /// </summary>
    public static class TestStateIsolationUtility
    {
        /// <summary>
        /// Ensures clean branch state before test operations
        /// </summary>
        /// <param name="syncManager">Sync manager instance</param>
        /// <param name="chromaService">ChromaDB service instance</param>
        /// <param name="syncStateTracker">Sync state tracker instance</param>
        /// <param name="branchName">Branch name to validate</param>
        /// <param name="repoPath">Repository path</param>
        /// <param name="logger">Logger for debugging information</param>
        public static async Task EnsureCleanBranchStateAsync(
            ISyncManagerV2 syncManager, 
            IChromaDbService chromaService,
            ISyncStateTracker syncStateTracker,
            string branchName,
            string repoPath,
            ILogger? logger = null)
        {
            logger?.LogInformation("EnsureCleanBranchState: Validating state for branch '{Branch}'", branchName);

            // Force sync to ensure any pending changes are committed
            var localChanges = await syncManager.GetLocalChangesAsync();
            if (localChanges.HasChanges)
            {
                logger?.LogInformation("EnsureCleanBranchState: Found {Count} local changes, forcing sync", localChanges.TotalChanges);
                await syncManager.FullSyncAsync(forceSync: true);
            }
            
            // Verify expected collections and document counts
            var collections = await chromaService.ListCollectionsAsync();
            foreach (var collection in collections)
            {
                var docCount = await chromaService.GetDocumentCountAsync(collection);
                logger?.LogInformation("EnsureCleanBranchState: Branch '{Branch}' collection '{Collection}': {Count} documents", 
                    branchName, collection, docCount);
            }
            
            // Log sync state for debugging
            var syncStates = await syncStateTracker.GetAllSyncStatesAsync(repoPath);
            var branchSyncStates = syncStates.Where(s => s.BranchContext == branchName).ToList();
            logger?.LogInformation("EnsureCleanBranchState: Branch '{Branch}' has {Count} sync state records", 
                branchName, branchSyncStates.Count);

            foreach (var syncState in branchSyncStates)
            {
                logger?.LogDebug("EnsureCleanBranchState: Sync state - Collection: '{Collection}', Status: '{Status}', DocCount: {DocCount}",
                    syncState.CollectionName, syncState.SyncStatus, syncState.DocumentCount);
            }
        }
        
        /// <summary>
        /// Forces complete state reset and re-sync from Dolt
        /// </summary>
        /// <param name="syncManager">Sync manager instance</param>
        /// <param name="chromaService">ChromaDB service instance</param>
        /// <param name="syncStateTracker">Sync state tracker instance</param>
        /// <param name="branchName">Branch name to reset</param>
        /// <param name="repoPath">Repository path</param>
        /// <param name="logger">Logger for debugging information</param>
        public static async Task ForceCleanStateResyncAsync(
            ISyncManagerV2 syncManager,
            IChromaDbService chromaService, 
            ISyncStateTracker syncStateTracker,
            string branchName,
            string repoPath,
            ILogger? logger = null)
        {
            logger?.LogInformation("ForceCleanStateResync: Starting comprehensive reset for branch '{Branch}'", branchName);

            // Delete all ChromaDB collections
            var collections = await chromaService.ListCollectionsAsync();
            logger?.LogInformation("ForceCleanStateResync: Deleting {Count} ChromaDB collections", collections.Count);
            
            foreach (var collection in collections)
            {
                logger?.LogDebug("ForceCleanStateResync: Deleting collection '{Collection}'", collection);
                await chromaService.DeleteCollectionAsync(collection);
            }
            
            // Clear sync state for this branch
            logger?.LogInformation("ForceCleanStateResync: Clearing sync state for branch '{Branch}'", branchName);
            await syncStateTracker.ClearBranchSyncStatesAsync(repoPath, branchName);
            
            // Force full resync from Dolt
            logger?.LogInformation("ForceCleanStateResync: Performing full resync from Dolt");
            await syncManager.FullSyncAsync(forceSync: true);

            logger?.LogInformation("ForceCleanStateResync: Comprehensive reset completed for branch '{Branch}'", branchName);
        }

        /// <summary>
        /// Validates branch state consistency and logs detailed information for debugging
        /// </summary>
        /// <param name="syncManager">Sync manager instance</param>
        /// <param name="chromaService">ChromaDB service instance</param>
        /// <param name="doltCli">Dolt CLI instance</param>
        /// <param name="branchName">Branch name to validate</param>
        /// <param name="logger">Logger for debugging information</param>
        public static async Task ValidateBranchStateConsistencyAsync(
            ISyncManagerV2 syncManager,
            IChromaDbService chromaService,
            IDoltCli doltCli,
            string branchName,
            ILogger? logger = null)
        {
            var currentBranch = await doltCli.GetCurrentBranchAsync();
            var commitHash = await doltCli.GetHeadCommitHashAsync();
            
            logger?.LogInformation("=== State Validation for Branch '{Branch}' (Current: '{Current}', Commit: '{Commit}') ===", 
                branchName, currentBranch, commitHash);
            
            // Validate ChromaDB state
            var chromaCollections = await chromaService.ListCollectionsAsync();
            var totalChromaDocuments = 0;
            
            foreach (var collection in chromaCollections)
            {
                var chromaCount = await chromaService.GetDocumentCountAsync(collection);
                totalChromaDocuments += chromaCount;
                logger?.LogInformation("ChromaDB '{Collection}': {Count} documents", collection, chromaCount);
            }

            // Validate local changes
            var localChanges = await syncManager.GetLocalChangesAsync();
            logger?.LogInformation("Local Changes: HasChanges={HasChanges}, Total={Total}", 
                localChanges.HasChanges, localChanges.TotalChanges);

            if (localChanges.HasChanges)
            {
                logger?.LogWarning("Branch '{Branch}' has uncommitted changes - this may indicate state isolation issues", branchName);
                var affectedCollections = localChanges.GetAffectedCollectionNames()?.ToList() ?? new List<string>();
                logger?.LogInformation("Affected collections: {Collections}", string.Join(", ", affectedCollections));
            }

            logger?.LogInformation("=== Validation Complete: Branch '{Branch}', Total ChromaDB docs: {Count} ===", 
                branchName, totalChromaDocuments);
        }

        /// <summary>
        /// Ensures branch has exactly the expected number of documents in specified collection
        /// </summary>
        /// <param name="chromaService">ChromaDB service instance</param>
        /// <param name="collectionName">Collection to validate</param>
        /// <param name="expectedCount">Expected document count</param>
        /// <param name="branchName">Branch name for error messages</param>
        /// <param name="logger">Logger for debugging information</param>
        /// <returns>True if count matches, false otherwise</returns>
        public static async Task<bool> ValidateCollectionDocumentCountAsync(
            IChromaDbService chromaService,
            string collectionName,
            int expectedCount,
            string branchName,
            ILogger? logger = null)
        {
            try
            {
                var actualCount = await chromaService.GetDocumentCountAsync(collectionName);
                var matches = actualCount == expectedCount;

                if (matches)
                {
                    logger?.LogInformation("Collection count validation PASSED: Branch '{Branch}', Collection '{Collection}': {Count} docs (expected {Expected})",
                        branchName, collectionName, actualCount, expectedCount);
                }
                else
                {
                    logger?.LogWarning("Collection count validation FAILED: Branch '{Branch}', Collection '{Collection}': {Count} docs (expected {Expected})",
                        branchName, collectionName, actualCount, expectedCount);
                }

                return matches;
            }
            catch (System.Exception ex)
            {
                logger?.LogError(ex, "Failed to validate collection '{Collection}' on branch '{Branch}'", collectionName, branchName);
                return false;
            }
        }
    }
}