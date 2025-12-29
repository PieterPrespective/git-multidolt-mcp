using System.Threading.Tasks;
using DMMS.Models;

namespace DMMS.Services
{
    /// <summary>
    /// Interface for detecting collection-level changes between ChromaDB and Dolt.
    /// Identifies collection deletions, renames, and metadata updates that need synchronization.
    /// </summary>
    public interface ICollectionChangeDetector
    {
        /// <summary>
        /// Detect all collection-level changes that need to be synchronized between ChromaDB and Dolt
        /// </summary>
        /// <returns>Summary of collection changes (deletions, renames, metadata updates)</returns>
        Task<CollectionChanges> DetectCollectionChangesAsync();

        /// <summary>
        /// Check if there are any pending collection changes
        /// </summary>
        /// <returns>True if collection changes exist, false otherwise</returns>
        Task<bool> HasPendingCollectionChangesAsync();

        /// <summary>
        /// Initialize the collection change detector with required database connections
        /// </summary>
        /// <param name="repositoryPath">Path to the Dolt repository</param>
        /// <returns>Task representing the initialization operation</returns>
        Task InitializeAsync(string repositoryPath);

        /// <summary>
        /// Validate that all required schemas and dependencies are properly initialized
        /// </summary>
        /// <param name="repositoryPath">Path to the Dolt repository</param>
        /// <returns>Task representing the validation operation</returns>
        Task ValidateSchemaAsync(string repositoryPath);

        /// <summary>
        /// Validate that the collection change detector is properly initialized
        /// </summary>
        /// <returns>Task representing the validation operation</returns>
        Task ValidateInitializationAsync();
    }
}