using Embranch.Models;

namespace Embranch.Services
{
    /// <summary>
    /// Interface for handling legacy ChromaDB database version compatibility.
    /// Provides methods to detect incompatible databases and create migrated copies
    /// without modifying the original external database.
    /// </summary>
    public interface ILegacyDbMigrator
    {
        /// <summary>
        /// Checks if a database requires migration due to version incompatibility.
        /// Detects issues like missing '_type' fields in collection configurations.
        /// </summary>
        /// <param name="dbPath">Path to the ChromaDB database to check</param>
        /// <returns>Check result indicating if migration is required and any error details</returns>
        Task<LegacyDbCheckResult> CheckCompatibilityAsync(string dbPath);

        /// <summary>
        /// Creates a migrated copy of a legacy database in a temporary location.
        /// The original database is never modified - all changes are made to the copy.
        /// </summary>
        /// <param name="sourceDbPath">Path to the original legacy database</param>
        /// <returns>Result containing the path to the migrated copy if successful</returns>
        Task<MigratedDbResult> CreateMigratedCopyAsync(string sourceDbPath);

        /// <summary>
        /// Disposes of a migrated temporary database copy.
        /// Handles ChromaDB file locking with retry logic.
        /// </summary>
        /// <param name="migratedDbPath">Path to the temporary migrated database to delete</param>
        Task DisposeMigratedCopyAsync(string migratedDbPath);

        /// <summary>
        /// Checks if the given exception indicates a legacy database version error.
        /// </summary>
        /// <param name="exception">Exception to analyze</param>
        /// <returns>True if the exception indicates a legacy version compatibility issue</returns>
        bool IsLegacyVersionError(Exception exception);
    }
}
