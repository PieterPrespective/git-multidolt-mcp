using Microsoft.Extensions.Logging;
using Embranch.Models;
using Python.Runtime;

namespace Embranch.Services
{
    /// <summary>
    /// Service for handling legacy ChromaDB database version compatibility.
    /// Creates temporary migrated copies of legacy databases without modifying originals.
    /// Uses ChromaCompatibilityHelper for the actual migration logic.
    /// </summary>
    public class LegacyDbMigrator : ILegacyDbMigrator
    {
        private readonly ILogger<LegacyDbMigrator> _logger;
        private const string TempDirectoryPrefix = "Embranch_LegacyMigration";
        private const int MaxCleanupRetries = 5;
        private const int BaseRetryDelayMs = 100;

        /// <summary>
        /// Initializes a new instance of LegacyDbMigrator
        /// </summary>
        /// <param name="logger">Logger for diagnostic information</param>
        public LegacyDbMigrator(ILogger<LegacyDbMigrator> logger)
        {
            _logger = logger;
            _logger.LogInformation("LegacyDbMigrator initialized");
        }

        /// <inheritdoc />
        public async Task<LegacyDbCheckResult> CheckCompatibilityAsync(string dbPath)
        {
            _logger.LogInformation("Checking legacy compatibility for database at: {Path}", dbPath);

            // First check if the path exists
            if (!Directory.Exists(dbPath))
            {
                return new LegacyDbCheckResult(
                    RequiresMigration: false,
                    ErrorType: "path_not_found",
                    ErrorMessage: $"Directory does not exist: {dbPath}",
                    DbPath: dbPath
                );
            }

            // Check for chroma.sqlite3 file
            var sqlitePath = Path.Combine(dbPath, "chroma.sqlite3");
            if (!File.Exists(sqlitePath))
            {
                return new LegacyDbCheckResult(
                    RequiresMigration: false,
                    ErrorType: "not_chromadb",
                    ErrorMessage: $"Not a valid ChromaDB database (missing chroma.sqlite3): {dbPath}",
                    DbPath: dbPath
                );
            }

            // Try to connect and list collections - this is where legacy errors occur
            try
            {
                var isValid = await PythonContext.ExecuteAsync(() =>
                {
                    using var _ = Py.GIL();
                    dynamic chromadb = Py.Import("chromadb");
                    dynamic client = chromadb.PersistentClient(path: dbPath);

                    // Attempt to list collections - this triggers the _type error on legacy DBs
                    dynamic collections = client.list_collections();

                    _logger.LogDebug("Successfully connected to database and listed collections");
                    return true;
                }, timeoutMs: 30000, operationName: $"CheckCompatibility_{Path.GetFileName(dbPath)}");

                if (isValid)
                {
                    _logger.LogInformation("Database at {Path} is compatible - no migration needed", dbPath);
                    return new LegacyDbCheckResult(
                        RequiresMigration: false,
                        ErrorType: null,
                        ErrorMessage: null,
                        DbPath: dbPath
                    );
                }
            }
            catch (Exception ex)
            {
                if (IsLegacyVersionError(ex))
                {
                    var errorType = DetectErrorType(ex);
                    _logger.LogWarning("Legacy database detected at {Path}: {ErrorType} - {Message}",
                        dbPath, errorType, ex.Message);

                    return new LegacyDbCheckResult(
                        RequiresMigration: true,
                        ErrorType: errorType,
                        ErrorMessage: ex.Message,
                        DbPath: dbPath
                    );
                }

                // Re-throw non-legacy errors
                _logger.LogError(ex, "Unexpected error checking database compatibility at {Path}", dbPath);
                throw;
            }

            // Should not reach here, but return safe default
            return new LegacyDbCheckResult(
                RequiresMigration: false,
                ErrorType: null,
                ErrorMessage: null,
                DbPath: dbPath
            );
        }

        /// <inheritdoc />
        public async Task<MigratedDbResult> CreateMigratedCopyAsync(string sourceDbPath)
        {
            _logger.LogInformation("Creating migrated copy of legacy database: {Path}", sourceDbPath);

            try
            {
                // Create unique temp directory
                var tempDir = Path.Combine(
                    Path.GetTempPath(),
                    TempDirectoryPrefix,
                    $"{Path.GetFileName(sourceDbPath)}_{Guid.NewGuid():N}"
                );

                _logger.LogDebug("Creating temp directory for migration: {TempDir}", tempDir);
                Directory.CreateDirectory(tempDir);

                // Copy all database files
                await CopyDirectoryAsync(sourceDbPath, tempDir);
                _logger.LogDebug("Copied database files to temp directory");

                // Apply migration to the copy
                _logger.LogInformation("Applying migration to temporary copy at {TempDir}", tempDir);
                var migrationSuccess = await ChromaCompatibilityHelper.MigrateDatabaseAsync(_logger, tempDir);

                if (!migrationSuccess)
                {
                    _logger.LogError("Migration failed for database copy at {TempDir}", tempDir);

                    // Clean up failed migration
                    await DisposeMigratedCopyAsync(tempDir);

                    return new MigratedDbResult(
                        Success: false,
                        MigratedDbPath: null,
                        OriginalDbPath: sourceDbPath,
                        ErrorMessage: "Migration failed - ChromaCompatibilityHelper returned false",
                        CreatedAt: DateTime.UtcNow
                    );
                }

                // Validate the migrated copy works
                var validationSuccess = await ChromaCompatibilityHelper.ValidateClientConnectionAsync(_logger, tempDir);
                if (!validationSuccess)
                {
                    _logger.LogError("Post-migration validation failed for {TempDir}", tempDir);

                    await DisposeMigratedCopyAsync(tempDir);

                    return new MigratedDbResult(
                        Success: false,
                        MigratedDbPath: null,
                        OriginalDbPath: sourceDbPath,
                        ErrorMessage: "Migration completed but validation failed",
                        CreatedAt: DateTime.UtcNow
                    );
                }

                _logger.LogInformation("Successfully created migrated copy at {TempDir}", tempDir);

                return new MigratedDbResult(
                    Success: true,
                    MigratedDbPath: tempDir,
                    OriginalDbPath: sourceDbPath,
                    ErrorMessage: null,
                    CreatedAt: DateTime.UtcNow
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create migrated copy of {SourcePath}", sourceDbPath);

                return new MigratedDbResult(
                    Success: false,
                    MigratedDbPath: null,
                    OriginalDbPath: sourceDbPath,
                    ErrorMessage: $"Failed to create migrated copy: {ex.Message}",
                    CreatedAt: DateTime.UtcNow
                );
            }
        }

        /// <inheritdoc />
        public async Task DisposeMigratedCopyAsync(string migratedDbPath)
        {
            _logger.LogInformation("Disposing migrated copy at: {Path}", migratedDbPath);

            if (string.IsNullOrEmpty(migratedDbPath) || !Directory.Exists(migratedDbPath))
            {
                _logger.LogDebug("Directory does not exist, nothing to dispose: {Path}", migratedDbPath);
                return;
            }

            // Retry logic for ChromaDB file locking
            for (int attempt = 0; attempt < MaxCleanupRetries; attempt++)
            {
                try
                {
                    Directory.Delete(migratedDbPath, recursive: true);
                    _logger.LogInformation("Successfully cleaned up migrated copy at {Path}", migratedDbPath);
                    return;
                }
                catch (IOException ex)
                {
                    if (attempt < MaxCleanupRetries - 1)
                    {
                        var delay = BaseRetryDelayMs * (int)Math.Pow(2, attempt);
                        _logger.LogDebug("Cleanup attempt {Attempt} failed, retrying in {Delay}ms: {Message}",
                            attempt + 1, delay, ex.Message);
                        await Task.Delay(delay);
                    }
                    else
                    {
                        // Log warning on final failure - not critical, file will be cleaned up later
                        _logger.LogWarning("Failed to cleanup temp migration directory after {MaxRetries} attempts: {Path}. Error: {Error}",
                            MaxCleanupRetries, migratedDbPath, ex.Message);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (attempt < MaxCleanupRetries - 1)
                    {
                        var delay = BaseRetryDelayMs * (int)Math.Pow(2, attempt);
                        _logger.LogDebug("Cleanup attempt {Attempt} failed due to access, retrying in {Delay}ms: {Message}",
                            attempt + 1, delay, ex.Message);
                        await Task.Delay(delay);
                    }
                    else
                    {
                        // Log warning on final failure - not critical, file will be cleaned up later
                        _logger.LogWarning("Failed to cleanup temp migration directory after {MaxRetries} attempts (access denied): {Path}. Error: {Error}",
                            MaxCleanupRetries, migratedDbPath, ex.Message);
                    }
                }
            }
        }

        /// <inheritdoc />
        public bool IsLegacyVersionError(Exception exception)
        {
            if (exception == null) return false;

            var message = exception.Message.ToLowerInvariant();

            // Check for known legacy database error patterns
            return message.Contains("_type") ||
                   message.Contains("could not find") && message.Contains("type") ||
                   message.Contains("keyerror") && message.Contains("type") ||
                   message.Contains("configuration") && message.Contains("type") ||
                   (exception is PythonException && message.Contains("type"));
        }

        #region Private Helper Methods

        /// <summary>
        /// Detects the specific type of legacy error from an exception
        /// </summary>
        private string DetectErrorType(Exception ex)
        {
            var message = ex.Message.ToLowerInvariant();

            if (message.Contains("_type"))
                return "missing_type";
            if (message.Contains("configuration"))
                return "schema_incompatible";
            if (message.Contains("keyerror"))
                return "missing_key";

            return "unknown_legacy_error";
        }

        /// <summary>
        /// Copies a directory recursively including all files and subdirectories
        /// </summary>
        private async Task CopyDirectoryAsync(string sourceDir, string destDir)
        {
            // Create destination directory if it doesn't exist
            Directory.CreateDirectory(destDir);

            // Copy all files
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                await Task.Run(() => File.Copy(file, destFile, overwrite: true));
            }

            // Recursively copy subdirectories
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                await CopyDirectoryAsync(dir, destSubDir);
            }
        }

        #endregion
    }
}
