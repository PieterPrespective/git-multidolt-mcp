using Embranch.Models;

namespace Embranch.Services
{
    /// <summary>
    /// Service for resolving merge conflicts with various resolution strategies
    /// Supports field-level merging, custom values, and automatic resolution
    /// </summary>
    public interface IMergeConflictResolver
    {
        /// <summary>
        /// Resolve a specific conflict using the provided resolution strategy
        /// </summary>
        /// <param name="conflict">The conflict to resolve</param>
        /// <param name="resolution">User-specified resolution preferences</param>
        /// <returns>True if resolution was successful</returns>
        Task<bool> ResolveConflictAsync(
            DetailedConflictInfo conflict,
            ConflictResolutionRequest resolution);
        
        /// <summary>
        /// Automatically resolve all conflicts that can be safely auto-resolved
        /// Uses non-overlapping field change detection and identical content matching
        /// </summary>
        /// <param name="conflicts">List of conflicts to attempt auto-resolution on</param>
        /// <returns>Number of conflicts successfully auto-resolved</returns>
        Task<int> AutoResolveConflictsAsync(List<DetailedConflictInfo> conflicts);
        
        /// <summary>
        /// Apply field-level merge resolution where different fields are kept from different branches
        /// </summary>
        /// <param name="tableName">Name of the table containing the conflict</param>
        /// <param name="documentId">Document ID of the conflicted record</param>
        /// <param name="fieldResolutions">Dictionary mapping field names to resolution choices (ours/theirs)</param>
        /// <returns>True if field merge was successful</returns>
        Task<bool> ApplyFieldMergeAsync(
            string tableName,
            string documentId,
            Dictionary<string, string> fieldResolutions);
        
        /// <summary>
        /// Apply custom user-provided values to resolve a conflict
        /// </summary>
        /// <param name="tableName">Name of the table containing the conflict</param>
        /// <param name="documentId">Document ID of the conflicted record</param>
        /// <param name="customValues">Dictionary of field names to custom values</param>
        /// <returns>True if custom resolution was successful</returns>
        Task<bool> ApplyCustomResolutionAsync(
            string tableName,
            string documentId,
            Dictionary<string, object> customValues);

        /// <summary>
        /// PP13-73-C3: Resolve multiple conflicts in a single transaction.
        /// Dolt requires ALL conflicts to be resolved before COMMIT is allowed.
        /// This method collects all resolution SQL statements and executes them atomically.
        /// </summary>
        /// <param name="conflictResolutions">List of (conflict, resolution) tuples to resolve</param>
        /// <returns>BatchResolutionResult with per-conflict outcomes and overall success</returns>
        Task<BatchResolutionResult> ResolveBatchAsync(
            List<(DetailedConflictInfo Conflict, ConflictResolutionRequest Resolution)> conflictResolutions);
    }
}