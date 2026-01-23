using Embranch.Models;

namespace Embranch.Services
{
    /// <summary>
    /// Interface for analyzing import operations and detecting potential conflicts.
    /// Provides methods to preview imports, identify conflicts, and determine auto-resolution capability.
    /// </summary>
    public interface IImportAnalyzer
    {
        /// <summary>
        /// Analyzes an import operation and returns preview information including conflicts.
        /// Compares documents from the external database with the local database to identify:
        /// - Documents to add (new documents not in local)
        /// - Documents to update (existing documents with changes)
        /// - Documents to skip (identical in both)
        /// - Conflicts requiring resolution
        /// </summary>
        /// <param name="sourcePath">Path to the external ChromaDB database</param>
        /// <param name="filter">Optional filter specifying which collections/documents to import</param>
        /// <param name="includeContentPreview">Include content snippets in conflict details (may impact performance)</param>
        /// <returns>Comprehensive preview result with conflict information</returns>
        Task<ImportPreviewResult> AnalyzeImportAsync(
            string sourcePath,
            ImportFilter? filter = null,
            bool includeContentPreview = false);

        /// <summary>
        /// Gets detailed conflict information for a specific source-target collection pair.
        /// Provides document-level conflict details for targeted analysis.
        /// </summary>
        /// <param name="sourcePath">Path to the external ChromaDB database</param>
        /// <param name="sourceCollection">Name of the source collection in external database</param>
        /// <param name="targetCollection">Name of the target collection in local database</param>
        /// <param name="documentIdPatterns">Optional document ID patterns to filter</param>
        /// <returns>List of detailed conflict information for the collection pair</returns>
        Task<List<ImportConflictInfo>> GetDetailedImportConflictsAsync(
            string sourcePath,
            string sourceCollection,
            string targetCollection,
            List<string>? documentIdPatterns = null);

        /// <summary>
        /// Determines if a specific conflict can be automatically resolved.
        /// Auto-resolvable conflicts include:
        /// - Metadata-only differences
        /// - Identical content with different metadata
        /// </summary>
        /// <param name="conflict">The conflict to evaluate</param>
        /// <returns>True if the conflict can be auto-resolved</returns>
        Task<bool> CanAutoResolveImportConflictAsync(ImportConflictInfo conflict);

        /// <summary>
        /// Gets import statistics for a source database without full conflict analysis.
        /// Faster than full analysis, suitable for quick preview.
        /// </summary>
        /// <param name="sourcePath">Path to the external ChromaDB database</param>
        /// <param name="filter">Optional filter specifying which collections/documents to import</param>
        /// <returns>Quick preview with basic statistics</returns>
        Task<ImportChangesPreview> GetQuickPreviewAsync(
            string sourcePath,
            ImportFilter? filter = null);
    }
}
