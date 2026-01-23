using Embranch.Models;

namespace Embranch.Services
{
    /// <summary>
    /// Service for analyzing potential merge conflicts before execution
    /// Provides detailed conflict detection and auto-resolution identification
    /// </summary>
    public interface IConflictAnalyzer
    {
        /// <summary>
        /// Analyze a potential merge operation to detect conflicts and provide preview information
        /// </summary>
        /// <param name="sourceBranch">Branch to merge from</param>
        /// <param name="targetBranch">Branch to merge into</param>
        /// <param name="includeAutoResolvable">Whether to include conflicts that can be auto-resolved</param>
        /// <param name="detailedDiff">Whether to include full document content in conflict details</param>
        /// <returns>Complete merge preview result with conflicts and recommendations</returns>
        Task<MergePreviewResult> AnalyzeMergeAsync(
            string sourceBranch, 
            string targetBranch,
            bool includeAutoResolvable,
            bool detailedDiff);
        
        /// <summary>
        /// Get detailed conflict information for a specific table
        /// Provides field-level conflict details with stable GUIDs for tracking
        /// </summary>
        /// <param name="tableName">Name of the table to analyze conflicts for</param>
        /// <returns>Collection of detailed conflict information</returns>
        Task<List<DetailedConflictInfo>> GetDetailedConflictsAsync(string tableName);
        
        /// <summary>
        /// Determine if a specific conflict can be automatically resolved
        /// Checks for non-overlapping field changes and identical content scenarios
        /// </summary>
        /// <param name="conflict">Conflict to analyze for auto-resolution potential</param>
        /// <returns>True if conflict can be automatically resolved</returns>
        Task<bool> CanAutoResolveConflictAsync(DetailedConflictInfo conflict);

        /// <summary>
        /// Get content comparison for a specific document across branches
        /// Shows base, source, and target content for detailed comparison
        /// </summary>
        /// <param name="tableName">Name of the table/collection containing the document</param>
        /// <param name="documentId">ID of the document to compare</param>
        /// <param name="sourceBranch">Source branch name</param>
        /// <param name="targetBranch">Target branch name</param>
        /// <returns>Content comparison data for the document</returns>
        Task<ContentComparison> GetContentComparisonAsync(
            string tableName,
            string documentId,
            string sourceBranch,
            string targetBranch);

        /// <summary>
        /// Generate resolution preview showing what each resolution option would produce
        /// </summary>
        /// <param name="conflict">The conflict to generate preview for</param>
        /// <param name="resolutionType">Type of resolution to preview</param>
        /// <returns>Preview of the resolution outcome</returns>
        Task<ResolutionPreview> GenerateResolutionPreviewAsync(
            DetailedConflictInfo conflict,
            ResolutionType resolutionType);
    }
}