using DMMS.Models;

namespace DMMS.Services
{
    /// <summary>
    /// Interface for executing import operations with conflict resolution.
    /// Provides methods to execute imports from external ChromaDB databases
    /// into the local DMMS-managed database with full conflict handling.
    /// </summary>
    public interface IImportExecutor
    {
        /// <summary>
        /// Executes an import operation with the specified resolutions.
        /// Imports documents from an external ChromaDB database into the local database,
        /// applying conflict resolutions and auto-resolving remaining conflicts based on strategy.
        /// Uses IChromaDbService.AddDocumentsAsync for proper chunking and metadata.
        /// </summary>
        /// <param name="sourcePath">Path to the external ChromaDB database folder</param>
        /// <param name="filter">Optional filter specifying which collections/documents to import</param>
        /// <param name="resolutions">Optional list of specific conflict resolutions</param>
        /// <param name="autoResolveRemaining">Whether to auto-resolve conflicts not explicitly specified</param>
        /// <param name="defaultStrategy">Default strategy for auto-resolution (keep_source, keep_target, skip)</param>
        /// <returns>Result with statistics about imported documents and resolved conflicts</returns>
        Task<ImportExecutionResult> ExecuteImportAsync(
            string sourcePath,
            ImportFilter? filter = null,
            List<ImportConflictResolution>? resolutions = null,
            bool autoResolveRemaining = true,
            string defaultStrategy = "keep_source");

        /// <summary>
        /// Resolves a single import conflict by applying the specified resolution.
        /// Can be used for incremental conflict resolution during interactive imports.
        /// </summary>
        /// <param name="sourcePath">Path to the external ChromaDB database folder</param>
        /// <param name="conflict">The conflict to resolve</param>
        /// <param name="resolution">The resolution to apply</param>
        /// <returns>True if the resolution was successfully applied</returns>
        Task<bool> ResolveImportConflictAsync(
            string sourcePath,
            ImportConflictInfo conflict,
            ImportConflictResolution resolution);

        /// <summary>
        /// Auto-resolves a list of conflicts based on the specified strategy.
        /// Useful for batch auto-resolution of auto-resolvable conflicts.
        /// </summary>
        /// <param name="sourcePath">Path to the external ChromaDB database folder</param>
        /// <param name="conflicts">List of conflicts to auto-resolve</param>
        /// <param name="strategy">Resolution strategy to apply (keep_source, keep_target, skip)</param>
        /// <returns>Number of conflicts successfully resolved</returns>
        Task<int> AutoResolveImportConflictsAsync(
            string sourcePath,
            List<ImportConflictInfo> conflicts,
            string strategy = "keep_source");

        /// <summary>
        /// Validates that a set of resolutions matches the conflicts from a preview.
        /// Ensures all conflict IDs are valid and resolution types are appropriate.
        /// </summary>
        /// <param name="preview">The import preview containing conflicts</param>
        /// <param name="resolutions">The resolutions to validate</param>
        /// <returns>Validation result with any error messages</returns>
        Task<(bool IsValid, List<string> Errors)> ValidateResolutionsAsync(
            ImportPreviewResult preview,
            List<ImportConflictResolution> resolutions);
    }
}
