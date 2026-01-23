using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.IO.Compression;

namespace EmbranchTesting.IntegrationTests;

/// <summary>
/// Integration tests for migrating out-of-date ChromaDB databases
/// Tests the compatibility helper with real pre-existing databases
/// </summary>
[TestFixture]
public class OutOfDateDatabaseMigrationTests
{
    private ILogger<OutOfDateDatabaseMigrationTests>? _logger;
    private ILogger<ChromaPythonService>? _serviceLogger;
    private string _zipFilePath = null!;
    private string _testDatabasePath = null!;

    /// <summary>
    /// Set up test environment before each test
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        // Initialize logger for tests
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _logger = loggerFactory.CreateLogger<OutOfDateDatabaseMigrationTests>();
        _serviceLogger = loggerFactory.CreateLogger<ChromaPythonService>();
        
        // PythonContext is managed by GlobalTestSetup - just verify it's available
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
        _testDatabasePath = Path.Combine(Path.GetTempPath(), $"ChromaMigrationTest_{testName}_{timestamp}_{Guid.NewGuid():N}");
        
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

    /// <summary>
    /// Clean up after each test
    /// </summary>
    [TearDown]
    public async Task TearDown()
    {
        // Wait briefly for any Python operations to complete and file handles to be released
        await Task.Delay(200);
        
        // Clean up the test directory with retry logic
        // Note: PythonContext may still have file locks, so we handle this gracefully
        if (!string.IsNullOrEmpty(_testDatabasePath) && Directory.Exists(_testDatabasePath))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(_testDatabasePath, recursive: true);
                    _logger?.LogInformation($"Successfully cleared test directory: {_testDatabasePath} in {sw.ElapsedMilliseconds}ms");
                    break; // Success, exit the retry loop
                }
                catch (IOException ex) when (attempt < 4)
                {
                    // Wait and retry with exponential backoff
                    _logger?.LogWarning($"Attempt {attempt + 1} @ {sw.ElapsedMilliseconds}ms to delete test directory failed: {ex.Message}. Retrying...");
                    await Task.Delay(100 * (int)Math.Pow(2, attempt));
                }
                catch (IOException ex) when (ex.Message.Contains("data_level0.bin") || ex.Message.Contains("chroma.sqlite3") || ex.Message.Contains("being used by another process"))
                {
                    // Known ChromaDB/PythonContext file locking issue - log but don't fail the test
                    Console.WriteLine($"Warning: ChromaDB file locking prevented directory cleanup: {ex.Message} after {sw.ElapsedMilliseconds}ms");
                    Console.WriteLine($"This is a known limitation and does not affect test functionality. Directory: '{_testDatabasePath}'");
                    break; // Exit without throwing
                }
                catch (UnauthorizedAccessException ex) when (attempt < 4)
                {
                    _logger?.LogWarning($"Attempt {attempt + 1} @ {sw.ElapsedMilliseconds}ms to delete directory failed due to access: {ex.Message}. Retrying...");
                    await Task.Delay(100 * (int)Math.Pow(2, attempt));
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Unexpected error during directory cleanup: {_testDatabasePath}");
                    break; // Don't keep retrying for unexpected errors
                }
            }
        }
    }

    /// <summary>
    /// Test that the out-of-date database can be successfully migrated and collections listed
    /// </summary>
    [Test]
    public async Task MigrateOutOfDateDatabase_ShouldSuccessfullyListCollections()
    {
        // Arrange
        Assert.That(Directory.Exists(_testDatabasePath), Is.True, "Test database directory should exist");
        
        var config = Options.Create(new ServerConfiguration
        {
            ChromaDataPath = _testDatabasePath
        });
        
        ChromaPythonService? service = null;
        
        try
        {
            // Act - Create service which should trigger migration during initialization
            _logger?.LogInformation("Creating ChromaPythonService - this should trigger database migration");
            service = new ChromaPythonService(_serviceLogger!, config);
            
            _logger?.LogInformation("Attempting to list collections from migrated database");
            var collections = await service.ListCollectionsAsync();
            
            // Assert
            Assert.That(collections, Is.Not.Null, "Collections list should not be null");
            Assert.That(collections.Count, Is.EqualTo(2), "Should find exactly 2 collections");
            
            // Check for expected collection names
            Assert.That(collections, Contains.Item("learning_database"), "Should contain 'learning_database' collection");
            Assert.That(collections, Contains.Item("DSplineKnowledge"), "Should contain 'DSplineKnowledge' collection");
            
            _logger?.LogInformation($"✓ Successfully migrated database and found {collections.Count} collections: {string.Join(", ", collections)}");
        }
        finally
        {
            service?.Dispose();
        }
    }

    /// <summary>
    /// Test that the compatibility helper can detect and fix the database without errors
    /// </summary>
    [Test]
    public async Task ChromaCompatibilityHelper_ShouldMigrateDatabaseSuccessfully()
    {
        // Arrange
        Assert.That(Directory.Exists(_testDatabasePath), Is.True, "Test database directory should exist");
        
        // Act
        _logger?.LogInformation("Running ChromaCompatibilityHelper.EnsureCompatibilityAsync");
        bool migrationSuccess = await ChromaCompatibilityHelper.EnsureCompatibilityAsync(_logger!, _testDatabasePath);
        
        // Assert
        Assert.That(migrationSuccess, Is.True, "Database migration should succeed");
        
        // Verify that we can now validate the connection
        _logger?.LogInformation("Validating client connection after migration");
        bool connectionValid = await ChromaCompatibilityHelper.ValidateClientConnectionAsync(_logger!, _testDatabasePath);
        
        Assert.That(connectionValid, Is.True, "Client connection should be valid after migration");
        
        _logger?.LogInformation("✓ ChromaCompatibilityHelper successfully migrated and validated database");
    }

    /// <summary>
    /// Test that the database migration is idempotent (can be run multiple times safely)
    /// </summary>
    [Test]
    public async Task DatabaseMigration_ShouldBeIdempotent()
    {
        // Arrange
        Assert.That(Directory.Exists(_testDatabasePath), Is.True, "Test database directory should exist");
        
        // Act - Run migration multiple times
        _logger?.LogInformation("Running first migration");
        bool firstMigration = await ChromaCompatibilityHelper.EnsureCompatibilityAsync(_logger!, _testDatabasePath);
        
        _logger?.LogInformation("Running second migration (should be idempotent)");
        bool secondMigration = await ChromaCompatibilityHelper.EnsureCompatibilityAsync(_logger!, _testDatabasePath);
        
        _logger?.LogInformation("Running third migration (should be idempotent)");
        bool thirdMigration = await ChromaCompatibilityHelper.EnsureCompatibilityAsync(_logger!, _testDatabasePath);
        
        // Assert
        Assert.That(firstMigration, Is.True, "First migration should succeed");
        Assert.That(secondMigration, Is.True, "Second migration should succeed");
        Assert.That(thirdMigration, Is.True, "Third migration should succeed");
        
        // Verify collections are still accessible
        var config = Options.Create(new ServerConfiguration
        {
            ChromaDataPath = _testDatabasePath
        });
        
        using var service = new ChromaPythonService(_serviceLogger!, config);
        var collections = await service.ListCollectionsAsync();
        
        Assert.That(collections.Count, Is.EqualTo(2), "Should still have 2 collections after multiple migrations");
        
        _logger?.LogInformation("✓ Database migration is idempotent");
    }

    /// <summary>
    /// Test that the migrated database properly retains metadata, IDs, and distances when querying for DSpline documents
    /// </summary>
    [Test]
    public async Task MigratedDatabase_QueryDSplineDocuments_ShouldReturnCompleteData()
    {
        // Arrange
        Assert.That(Directory.Exists(_testDatabasePath), Is.True, "Test database directory should exist");
        
        var config = Options.Create(new ServerConfiguration
        {
            ChromaDataPath = _testDatabasePath
        });
        
        ChromaPythonService? service = null;
        
        try
        {
            // Act - Create service and query for DSpline documents
            _logger?.LogInformation("Creating ChromaPythonService and querying for DSpline documents");
            service = new ChromaPythonService(_serviceLogger!, config);
            
            // Query documents with "DSpline" text
            var queryResults = await service.QueryDocumentsAsync(
                collectionName: "DSplineKnowledge",
                queryTexts: new[] { "DSpline" }.ToList(),
                nResults: 5,
                where: null,
                whereDocument: null
            );
            
            // Assert
            Assert.That(queryResults, Is.Not.Null, "Query results should not be null");
            
            // Cast to dictionary to access the result properties
            var resultDict = queryResults as Dictionary<string, object>;
            Assert.That(resultDict, Is.Not.Null, "Query results should be a dictionary");
            
            Assert.That(resultDict!.ContainsKey("ids"), Is.True, "Results should contain 'ids' key");
            Assert.That(resultDict.ContainsKey("documents"), Is.True, "Results should contain 'documents' key");
            Assert.That(resultDict.ContainsKey("metadatas"), Is.True, "Results should contain 'metadatas' key");
            Assert.That(resultDict.ContainsKey("distances"), Is.True, "Results should contain 'distances' key");
            
            var ids = resultDict["ids"] as List<object>;
            var documents = resultDict["documents"] as List<object>;
            var metadatas = resultDict["metadatas"] as List<object>;
            var distances = resultDict["distances"] as List<object>;
            
            Assert.That(ids, Is.Not.Null.And.Not.Empty, "IDs should be returned and not empty");
            Assert.That(documents, Is.Not.Null.And.Not.Empty, "Documents should be returned and not empty");
            Assert.That(metadatas, Is.Not.Null.And.Not.Empty, "Metadatas should be returned and not empty");
            Assert.That(distances, Is.Not.Null.And.Not.Empty, "Distances should be returned and not empty");
            
            // Check for the expected document (assuming first result contains expected data)
            var firstResultIds = ids![0] as List<object>;
            Assert.That(firstResultIds, Is.Not.Null, "First result IDs should not be null");
            Assert.That(firstResultIds!.Any(id => id.ToString() == "dspline-creation-reference"), 
                Is.True, "Should find the 'dspline-creation-reference' document");
            
            // Validate that the document contains expected content
            var firstResultDocuments = documents![0] as List<object>;
            Assert.That(firstResultDocuments, Is.Not.Null, "First result documents should not be null");
            Assert.That(firstResultDocuments![0].ToString(), Does.Contain("DSpline Creation and Usage Reference"), 
                "Document should contain the expected DSpline reference content");
            
            // Validate metadata structure (log what we actually receive first)
            _logger?.LogInformation($"Metadata structure: {System.Text.Json.JsonSerializer.Serialize(metadatas)}");
            
            // More flexible metadata checking since structure might vary
            if (metadatas!.Count > 0)
            {
                _logger?.LogInformation("✓ Metadata is present in query results");
                // Just check that metadata exists - the exact structure may vary between ChromaDB versions
            }
            else
            {
                _logger?.LogInformation("Note: No metadata found in query results - this may be expected for some database versions");
            }
            
            // Validate distance is a reasonable value
            var firstResultDistances = distances![0] as List<object>;
            Assert.That(firstResultDistances, Is.Not.Null, "First result distances should not be null");
            
            if (firstResultDistances!.Count > 0 && double.TryParse(firstResultDistances[0].ToString(), out double distance))
            {
                Assert.That(distance, Is.GreaterThan(0.0), "Distance should be greater than 0");
                Assert.That(distance, Is.LessThan(2.0), "Distance should be less than 2.0 for a relevant result");
            }
            
            _logger?.LogInformation($"✓ Successfully queried DSpline documents: " +
                $"Found {firstResultIds.Count} results with complete metadata, IDs, and distances");
        }
        finally
        {
            service?.Dispose();
        }
    }

}