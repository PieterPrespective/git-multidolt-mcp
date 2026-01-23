using Microsoft.Extensions.Logging;
using Embranch.Models;

namespace Embranch.Services
{
    /// <summary>
    /// Disposable context manager for import operations that handles legacy database migration transparently.
    /// Creates a migrated copy if needed and ensures cleanup after operations complete.
    /// Provides the effective path to use for all import operations.
    /// </summary>
    public class LegacyDbImportContext : IAsyncDisposable
    {
        private readonly ILegacyDbMigrator _migrator;
        private readonly ILogger _logger;
        private readonly string? _migratedPath;
        private bool _disposed;

        /// <summary>
        /// The effective path to use for all import operations.
        /// This will be the migrated copy path if migration was required,
        /// or the original path if no migration was needed.
        /// </summary>
        public string EffectivePath { get; }

        /// <summary>
        /// Whether a migration was performed for this context.
        /// </summary>
        public bool WasMigrated { get; }

        /// <summary>
        /// The original database path that was requested.
        /// </summary>
        public string OriginalPath { get; }

        /// <summary>
        /// Information about the migration if one was performed, null otherwise.
        /// </summary>
        public LegacyMigrationInfo? MigrationInfo { get; }

        /// <summary>
        /// Private constructor - use CreateAsync to create instances
        /// </summary>
        private LegacyDbImportContext(
            ILegacyDbMigrator migrator,
            ILogger logger,
            string originalPath,
            string effectivePath,
            string? migratedPath,
            bool wasMigrated,
            LegacyMigrationInfo? migrationInfo)
        {
            _migrator = migrator;
            _logger = logger;
            OriginalPath = originalPath;
            EffectivePath = effectivePath;
            _migratedPath = migratedPath;
            WasMigrated = wasMigrated;
            MigrationInfo = migrationInfo;
        }

        /// <summary>
        /// Creates a new LegacyDbImportContext for the specified database path.
        /// Automatically detects if migration is needed and creates a migrated copy if required.
        /// </summary>
        /// <param name="migrator">The legacy database migrator service</param>
        /// <param name="dbPath">Path to the external database</param>
        /// <param name="logger">Logger for diagnostic information</param>
        /// <returns>A context containing the effective path to use for import operations</returns>
        public static async Task<LegacyDbImportContext> CreateAsync(
            ILegacyDbMigrator migrator,
            string dbPath,
            ILogger logger)
        {
            logger.LogInformation("Creating LegacyDbImportContext for path: {Path}", dbPath);

            // Check if migration is needed
            var compatCheck = await migrator.CheckCompatibilityAsync(dbPath);

            if (!compatCheck.RequiresMigration)
            {
                logger.LogDebug("Database at {Path} is compatible - no migration needed", dbPath);
                return new LegacyDbImportContext(
                    migrator,
                    logger,
                    originalPath: dbPath,
                    effectivePath: dbPath,
                    migratedPath: null,
                    wasMigrated: false,
                    migrationInfo: null
                );
            }

            // Migration is required
            logger.LogInformation("Database at {Path} requires migration: {ErrorType}", dbPath, compatCheck.ErrorType);

            var migrationResult = await migrator.CreateMigratedCopyAsync(dbPath);

            if (!migrationResult.Success || string.IsNullOrEmpty(migrationResult.MigratedDbPath))
            {
                // Migration failed - we'll return the original path and let the caller handle the error
                logger.LogWarning("Migration failed for {Path}: {Error}. Using original path.",
                    dbPath, migrationResult.ErrorMessage);

                return new LegacyDbImportContext(
                    migrator,
                    logger,
                    originalPath: dbPath,
                    effectivePath: dbPath,
                    migratedPath: null,
                    wasMigrated: false,
                    migrationInfo: new LegacyMigrationInfo
                    {
                        OriginalPath = dbPath,
                        WasMigrated = false,
                        Reason = $"Migration failed: {migrationResult.ErrorMessage}",
                        ErrorType = compatCheck.ErrorType
                    }
                );
            }

            logger.LogInformation("Successfully created migrated copy at {MigratedPath}", migrationResult.MigratedDbPath);

            return new LegacyDbImportContext(
                migrator,
                logger,
                originalPath: dbPath,
                effectivePath: migrationResult.MigratedDbPath,
                migratedPath: migrationResult.MigratedDbPath,
                wasMigrated: true,
                migrationInfo: new LegacyMigrationInfo
                {
                    OriginalPath = dbPath,
                    WasMigrated = true,
                    Reason = "Legacy ChromaDB version detected and migrated for compatibility",
                    ErrorType = compatCheck.ErrorType
                }
            );
        }

        /// <summary>
        /// Disposes of the context, cleaning up any temporary migrated database.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (!string.IsNullOrEmpty(_migratedPath))
            {
                _logger.LogInformation("Disposing LegacyDbImportContext - cleaning up migrated copy at {Path}", _migratedPath);
                await _migrator.DisposeMigratedCopyAsync(_migratedPath);
            }
            else
            {
                _logger.LogDebug("Disposing LegacyDbImportContext - no migrated copy to clean up");
            }
        }
    }
}
