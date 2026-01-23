namespace Embranch.Models
{
    /// <summary>
    /// Result of checking a ChromaDB database for legacy version compatibility issues.
    /// Contains information about whether migration is required and any detected errors.
    /// </summary>
    /// <param name="RequiresMigration">True if the database requires migration due to version incompatibility</param>
    /// <param name="ErrorType">Type of error detected (e.g., "missing_type", "schema_incompatible"), null if no migration needed</param>
    /// <param name="ErrorMessage">Detailed error message if migration is required, null otherwise</param>
    /// <param name="DbPath">Path to the database that was checked</param>
    public record LegacyDbCheckResult(
        bool RequiresMigration,
        string? ErrorType,
        string? ErrorMessage,
        string DbPath
    );

    /// <summary>
    /// Result of creating a migrated copy of a legacy ChromaDB database.
    /// Contains the path to the migrated copy and status information.
    /// </summary>
    /// <param name="Success">True if the migration was successful</param>
    /// <param name="MigratedDbPath">Path to the temporary migrated database copy, null if migration failed</param>
    /// <param name="OriginalDbPath">Path to the original database that was copied</param>
    /// <param name="ErrorMessage">Error message if migration failed, null otherwise</param>
    /// <param name="CreatedAt">Timestamp when the migrated copy was created</param>
    public record MigratedDbResult(
        bool Success,
        string? MigratedDbPath,
        string OriginalDbPath,
        string? ErrorMessage,
        DateTime CreatedAt
    );

    /// <summary>
    /// Information about a legacy database migration that was performed during an import operation.
    /// Included in tool responses when migration occurs.
    /// </summary>
    public class LegacyMigrationInfo
    {
        /// <summary>
        /// Original path to the legacy database
        /// </summary>
        public string OriginalPath { get; set; } = string.Empty;

        /// <summary>
        /// Whether migration was performed
        /// </summary>
        public bool WasMigrated { get; set; }

        /// <summary>
        /// Reason for the migration
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Type of legacy issue detected (e.g., "missing_type")
        /// </summary>
        public string? ErrorType { get; set; }
    }
}
