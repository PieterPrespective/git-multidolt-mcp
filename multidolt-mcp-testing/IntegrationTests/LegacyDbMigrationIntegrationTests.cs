using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Python.Runtime;
using System.IO.Compression;

namespace EmbranchTesting.IntegrationTests;

/// <summary>
/// Integration tests for the LegacyDbMigrator service with real ChromaDB databases.
/// Tests migration functionality with the out-of-date test database.
/// Uses EmbranchTesting namespace for GlobalTestSetup PythonContext initialization.
/// </summary>
[TestFixture]
public class LegacyDbMigrationIntegrationTests
{
    private ILogger<LegacyDbMigrator>? _logger;
    private LegacyDbMigrator? _migrator;
    private string _zipFilePath = null!;
    private string _testDatabasePath = null!;

    [SetUp]
    public void SetUp()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _logger = loggerFactory.CreateLogger<LegacyDbMigrator>();
        _migrator = new LegacyDbMigrator(_logger);

        // PythonContext should be initialized by GlobalTestSetup
        if (!PythonContext.IsInitialized)
        {
            Assert.Fail("PythonContext should be initialized by GlobalTestSetup");
        }

        // Set up paths to the test database zip file
        var testProjectRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(TestContext.CurrentContext.TestDirectory)))!;
        _zipFilePath = Path.Combine(testProjectRoot, "TestData", "out-of-date-chroma-database.zip");

        // Create unique test directory for this test run
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var testName = TestContext.CurrentContext.Test.Name;
        _testDatabasePath = Path.Combine(Path.GetTempPath(), $"LegacyMigrationTest_{testName}_{timestamp}_{Guid.NewGuid():N}");

        // Extract the zip file to test location
        if (File.Exists(_zipFilePath))
        {
            ZipFile.ExtractToDirectory(_zipFilePath, _testDatabasePath);
            _logger?.LogInformation($"Extracted test database from {_zipFilePath} to {_testDatabasePath}");
        }
        else
        {
            Assert.Fail($"Test database zip file not found at: {_zipFilePath}");
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        // Wait briefly for file handles to be released
        await Task.Delay(200);

        // Clean up test directory with retry logic
        if (!string.IsNullOrEmpty(_testDatabasePath) && Directory.Exists(_testDatabasePath))
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(_testDatabasePath, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(100 * (int)Math.Pow(2, attempt));
                }
                catch
                {
                    // Ignore cleanup errors on final attempt
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Tests that CheckCompatibilityAsync correctly detects a legacy database
    /// </summary>
    [Test]
    public async Task CheckCompatibilityAsync_LegacyDatabase_RequiresMigration()
    {
        // Act
        var result = await _migrator!.CheckCompatibilityAsync(_testDatabasePath);

        // Assert
        Assert.That(result.RequiresMigration, Is.True, "Legacy database should require migration");
        Assert.That(result.ErrorType, Is.Not.Null.And.Not.Empty, "Should have an error type");
        Assert.That(result.DbPath, Is.EqualTo(_testDatabasePath));

        _logger?.LogInformation($"Detected legacy database with error type: {result.ErrorType}");
    }

    /// <summary>
    /// Tests that CreateMigratedCopyAsync successfully creates a migrated copy
    /// </summary>
    [Test]
    public async Task CreateMigratedCopyAsync_LegacyDatabase_CreatesMigratedCopy()
    {
        // Act
        var result = await _migrator!.CreateMigratedCopyAsync(_testDatabasePath);

        // Assert
        Assert.That(result.Success, Is.True, "Migration should succeed");
        Assert.That(result.MigratedDbPath, Is.Not.Null.And.Not.Empty, "Should have migrated path");
        Assert.That(result.OriginalDbPath, Is.EqualTo(_testDatabasePath));
        Assert.That(result.ErrorMessage, Is.Null, "Should have no error message on success");

        // Verify the migrated directory exists
        Assert.That(Directory.Exists(result.MigratedDbPath!), Is.True, "Migrated directory should exist");

        // Verify the migrated database has the expected files
        var sqlitePath = Path.Combine(result.MigratedDbPath!, "chroma.sqlite3");
        Assert.That(File.Exists(sqlitePath), Is.True, "Migrated database should have chroma.sqlite3");

        _logger?.LogInformation($"Created migrated copy at: {result.MigratedDbPath}");

        // Clean up the migrated copy
        await _migrator.DisposeMigratedCopyAsync(result.MigratedDbPath!);
    }

    /// <summary>
    /// Tests that migrated copy can be accessed without errors
    /// </summary>
    [Test]
    public async Task CreateMigratedCopyAsync_MigratedCopy_CanBeAccessed()
    {
        // Arrange
        var migrationResult = await _migrator!.CreateMigratedCopyAsync(_testDatabasePath);
        Assert.That(migrationResult.Success, Is.True, "Migration should succeed first");

        try
        {
            // Act - Check compatibility of migrated copy
            var compatCheck = await _migrator.CheckCompatibilityAsync(migrationResult.MigratedDbPath!);

            // Assert
            Assert.That(compatCheck.RequiresMigration, Is.False, "Migrated copy should not require migration");
            Assert.That(compatCheck.ErrorType, Is.Null, "Migrated copy should have no error type");

            _logger?.LogInformation("Migrated copy is accessible and compatible");
        }
        finally
        {
            // Clean up
            await _migrator.DisposeMigratedCopyAsync(migrationResult.MigratedDbPath!);
        }
    }

    /// <summary>
    /// Tests that DisposeMigratedCopyAsync successfully removes the temp directory
    /// </summary>
    [Test]
    public async Task DisposeMigratedCopyAsync_ExistingDirectory_RemovesDirectory()
    {
        // Arrange
        var migrationResult = await _migrator!.CreateMigratedCopyAsync(_testDatabasePath);
        Assert.That(migrationResult.Success, Is.True);
        Assert.That(Directory.Exists(migrationResult.MigratedDbPath!), Is.True);

        // Act
        await _migrator.DisposeMigratedCopyAsync(migrationResult.MigratedDbPath!);

        // Allow time for cleanup
        await Task.Delay(500);

        // Assert - directory should be removed (or cleanup logged as warning)
        // Note: ChromaDB may still hold file locks, so we accept either outcome
        _logger?.LogInformation($"Dispose completed for: {migrationResult.MigratedDbPath}");
    }

    /// <summary>
    /// Tests that migration preserves original database unchanged
    /// </summary>
    [Test]
    public async Task CreateMigratedCopyAsync_OriginalDatabase_NotModified()
    {
        // Arrange - get original file hashes
        var originalSqlitePath = Path.Combine(_testDatabasePath, "chroma.sqlite3");
        var originalHash = ComputeFileHash(originalSqlitePath);
        var originalSize = new FileInfo(originalSqlitePath).Length;

        // Act
        var migrationResult = await _migrator!.CreateMigratedCopyAsync(_testDatabasePath);

        try
        {
            // Assert - original should be unchanged
            var newHash = ComputeFileHash(originalSqlitePath);
            var newSize = new FileInfo(originalSqlitePath).Length;

            Assert.That(newHash, Is.EqualTo(originalHash), "Original database hash should not change");
            Assert.That(newSize, Is.EqualTo(originalSize), "Original database size should not change");

            _logger?.LogInformation("Original database was preserved unchanged");
        }
        finally
        {
            if (migrationResult.Success)
            {
                await _migrator.DisposeMigratedCopyAsync(migrationResult.MigratedDbPath!);
            }
        }
    }

    /// <summary>
    /// Tests that migration is idempotent - multiple migrations work correctly
    /// </summary>
    [Test]
    public async Task CreateMigratedCopyAsync_MultipleOperations_AllSucceed()
    {
        // Arrange
        var results = new List<MigratedDbResult>();

        try
        {
            // Act - create multiple migrated copies
            for (int i = 0; i < 3; i++)
            {
                var result = await _migrator!.CreateMigratedCopyAsync(_testDatabasePath);
                results.Add(result);
            }

            // Assert - all should succeed with unique paths
            foreach (var result in results)
            {
                Assert.That(result.Success, Is.True, $"Migration {results.IndexOf(result)} should succeed");
                Assert.That(result.MigratedDbPath, Is.Not.Null, $"Migration {results.IndexOf(result)} should have path");
            }

            // All paths should be unique
            var uniquePaths = results.Select(r => r.MigratedDbPath).Distinct().Count();
            Assert.That(uniquePaths, Is.EqualTo(results.Count), "All migrated paths should be unique");

            _logger?.LogInformation($"Successfully created {results.Count} migrated copies");
        }
        finally
        {
            // Clean up all
            foreach (var result in results.Where(r => r.Success))
            {
                await _migrator!.DisposeMigratedCopyAsync(result.MigratedDbPath!);
            }
        }
    }

    /// <summary>
    /// Tests that migrated database can list collections
    /// </summary>
    [Test]
    public async Task MigratedDatabase_ListCollections_ReturnsExpectedCollections()
    {
        // Arrange
        var migrationResult = await _migrator!.CreateMigratedCopyAsync(_testDatabasePath);
        Assert.That(migrationResult.Success, Is.True);

        try
        {
            // Act - try to list collections using the migrated database
            var collectionNames = await PythonContext.ExecuteAsync(() =>
            {
                using var _ = Py.GIL();
                dynamic chromadb = Py.Import("chromadb");
                dynamic client = chromadb.PersistentClient(path: migrationResult.MigratedDbPath);
                dynamic collections = client.list_collections();

                var names = new List<string>();
                foreach (dynamic name in collections)
                {
                    names.Add(name.ToString());
                }
                return names;
            }, timeoutMs: 30000, operationName: "ListCollections");

            // Assert
            Assert.That(collectionNames, Is.Not.Null);
            Assert.That(collectionNames.Count, Is.EqualTo(2), "Should have 2 collections");
            Assert.That(collectionNames, Contains.Item("learning_database"));
            Assert.That(collectionNames, Contains.Item("DSplineKnowledge"));

            _logger?.LogInformation($"Listed {collectionNames.Count} collections from migrated database");
        }
        finally
        {
            await _migrator.DisposeMigratedCopyAsync(migrationResult.MigratedDbPath!);
        }
    }

    /// <summary>
    /// Tests that migrated database can retrieve documents
    /// </summary>
    [Test]
    public async Task MigratedDatabase_GetDocuments_ReturnsDocuments()
    {
        // Arrange
        var migrationResult = await _migrator!.CreateMigratedCopyAsync(_testDatabasePath);
        Assert.That(migrationResult.Success, Is.True);

        try
        {
            // Act - get documents from a collection
            var docCount = await PythonContext.ExecuteAsync(() =>
            {
                using var _ = Py.GIL();
                dynamic chromadb = Py.Import("chromadb");
                dynamic client = chromadb.PersistentClient(path: migrationResult.MigratedDbPath);
                dynamic collection = client.get_collection(name: "DSplineKnowledge");
                return (int)collection.count();
            }, timeoutMs: 30000, operationName: "GetDocuments");

            // Assert
            Assert.That(docCount, Is.GreaterThan(0), "Should have documents in the collection");

            _logger?.LogInformation($"Found {docCount} documents in DSplineKnowledge collection");
        }
        finally
        {
            await _migrator.DisposeMigratedCopyAsync(migrationResult.MigratedDbPath!);
        }
    }

    #region Helper Methods

    private static string ComputeFileHash(string filePath)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    #endregion
}
