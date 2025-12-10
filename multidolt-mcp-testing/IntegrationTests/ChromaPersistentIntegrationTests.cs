using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;
using DMMS.Services;
using DMMSTesting.Utilities;

namespace DMMSTesting.IntegrationTests;

/// <summary>
/// Integration tests for ChromaPersistentDbService with actual file system
/// </summary>
[TestFixture]
public class ChromaPersistentIntegrationTests
{
    private ChromaPersistentDbService _service;
    private string _testDataPath;
    private ILogger<ChromaPersistentDbService> _logger;
    private IOptions<ServerConfiguration> _options;

    /// <summary>
    /// Sets up test environment before each test
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        // Create temporary directory for test data
        _testDataPath = Path.Combine(Path.GetTempPath(), $"chroma_test_{Guid.NewGuid():N}");
        
        // Set up logger and configuration
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = loggerFactory.CreateLogger<ChromaPersistentDbService>();
        
        var configuration = new ServerConfiguration
        {
            ChromaDataPath = _testDataPath,
            ChromaMode = "persistent"
        };
        _options = Options.Create(configuration);
        
        _service = new ChromaPersistentDbService(_logger, _options);
    }

    /// <summary>
    /// Cleans up test environment after each test
    /// </summary>
    [TearDown]
    public async Task TearDown()
    {
        // Explicitly clean up test data before disposal
        if (_service != null)
        {
            try
            {
                // Delete any collections created during tests
                var collections = _service.ListCollectionsAsync().Result;
                foreach (var collectionName in collections)
                {
                    try
                    {
                        _service.DeleteCollectionAsync(collectionName).Wait();
                        Console.WriteLine($"Deleted test collection '{collectionName}'");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error deleting collection '{collectionName}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during test collection cleanup: {ex.Message}");
            }
        }
        
        // Dispose service (handles connections only)
        _service?.Dispose();

        // Wait briefly for file handles to be released after disposal
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(100);


        //Thread.Sleep(100);

        // Clean up test directory with retry logic
        
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_testDataPath))
                {
                    Directory.Delete(_testDataPath, recursive: true);
                }

                _logger?.LogInformation($"Succesfully cleared test chroma data in test directory: {_testDataPath}");

                break; // Success
            }
            catch (IOException ex) when (attempt < 4)
            {
                // Wait and retry with exponential backoff
                _logger?.LogWarning($"Attempt {attempt + 1} @ t={sw.ElapsedMilliseconds}ms to delete test directory failed: {ex.Message}. Retrying...");
                await Task.Delay(100 * (int)Math.Pow(2, attempt));
            }
            catch (IOException ex) when (ex.Message.Contains("data_level0.bin") || ex.Message.Contains("chroma.sqlite3"))
            {
                // Known ChromaDB file locking issue - log but don't fail the test
                Console.WriteLine($"Warning: ChromaDB file locking prevented directory cleanup: {ex.Message} after t={sw.ElapsedMilliseconds}ms");
                Console.WriteLine($"This is a known limitation and does not affect test functionality. test directory: '{_testDataPath}'");
                break; // Exit without throwing
            }
        }
    }

    /// <summary>
    /// Tests creating and listing collections
    /// </summary>
    [Test]
    public async Task CreateAndListCollections_ShouldWork()
    {
        // Arrange & Act - Create collections
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.CreateCollectionAsync("test_collection_1"), 
            operationName: "Create collection 1");
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.CreateCollectionAsync("test_collection_2", new Dictionary<string, object>
            {
                { "description", "Test collection with metadata" },
                { "version", 1 }
            }),
            operationName: "Create collection 2");

        var collections = await TestUtilities.ExecuteWithTimeoutAsync(
            _service.ListCollectionsAsync(),
            operationName: "List collections");

        // Assert
        Assert.That(collections, Is.Not.Null);
        Assert.That(collections.Count, Is.EqualTo(2));
        Assert.That(collections, Does.Contain("test_collection_1"));
        Assert.That(collections, Does.Contain("test_collection_2"));
    }

    /// <summary>
    /// Tests adding and querying documents
    /// </summary>
    [Test]
    public async Task AddAndQueryDocuments_ShouldWork()
    {
        // Arrange
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.CreateCollectionAsync("docs_collection"),
            operationName: "Create docs collection");
        
        var documents = new List<string>
        {
            "This is the first document about artificial intelligence",
            "This is the second document about machine learning",
            "This is the third document about natural language processing"
        };
        var ids = new List<string> { "doc1", "doc2", "doc3" };
        var metadatas = new List<Dictionary<string, object>>
        {
            new() { { "category", "AI" }, { "author", "Alice" } },
            new() { { "category", "ML" }, { "author", "Bob" } },
            new() { { "category", "NLP" }, { "author", "Charlie" } }
        };

        // Act - Add documents
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.AddDocumentsAsync("docs_collection", documents, ids, metadatas),
            operationName: "Add documents");

        // Act - Query documents
        var queryResult = await TestUtilities.ExecuteWithTimeoutAsync(
            _service.QueryDocumentsAsync("docs_collection", new List<string> { "artificial intelligence" }),
            operationName: "Query documents");

        // Assert
        Assert.That(queryResult, Is.Not.Null);
        var resultObj = queryResult as dynamic;
        Assert.That(resultObj, Is.Not.Null);
    }

    /// <summary>
    /// Tests getting document count
    /// </summary>
    [Test]
    public async Task GetCollectionCount_ShouldReturnCorrectCount()
    {
        // Arrange
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.CreateCollectionAsync("count_test"),
            operationName: "Create count test collection");
        var documents = new List<string> { "Doc 1", "Doc 2", "Doc 3", "Doc 4", "Doc 5" };
        var ids = new List<string> { "1", "2", "3", "4", "5" };

        // Act
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.AddDocumentsAsync("count_test", documents, ids),
            operationName: "Add documents for count test");
        var count = await TestUtilities.ExecuteWithTimeoutAsync(
            _service.GetCollectionCountAsync("count_test"),
            operationName: "Get collection count");

        // Assert
        Assert.That(count, Is.EqualTo(5));
    }

    /// <summary>
    /// Tests updating documents
    /// </summary>
    [Test]
    public async Task UpdateDocuments_ShouldModifyExistingDocuments()
    {
        // Arrange
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.CreateCollectionAsync("update_test"),
            operationName: "Create update test collection");
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.AddDocumentsAsync("update_test", 
                new List<string> { "Original document" }, 
                new List<string> { "doc1" }),
            operationName: "Add original document");

        // Act - Update document
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.UpdateDocumentsAsync("update_test", 
                new List<string> { "doc1" }, 
                new List<string> { "Updated document" }),
            operationName: "Update document");

        // Act - Get updated document
        var result = await TestUtilities.ExecuteWithTimeoutAsync(
            _service.GetDocumentsAsync("update_test", new List<string> { "doc1" }),
            operationName: "Get updated document");

        // Assert
        Assert.That(result, Is.Not.Null);
        // Note: Would need to properly deserialize and check the document content
        // For now, just verify the operation completed without error
    }

    /// <summary>
    /// Tests deleting documents
    /// </summary>
    [Test]
    public async Task DeleteDocuments_ShouldRemoveDocuments()
    {
        // Arrange
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.CreateCollectionAsync("delete_test"),
            operationName: "Create delete test collection");
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.AddDocumentsAsync("delete_test", 
                new List<string> { "Doc 1", "Doc 2", "Doc 3" }, 
                new List<string> { "1", "2", "3" }),
            operationName: "Add documents for delete test");

        // Act - Delete one document
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.DeleteDocumentsAsync("delete_test", new List<string> { "2" }),
            operationName: "Delete document");
        var count = await TestUtilities.ExecuteWithTimeoutAsync(
            _service.GetCollectionCountAsync("delete_test"),
            operationName: "Get count after deletion");

        // Assert
        Assert.That(count, Is.EqualTo(2));
    }

    /// <summary>
    /// Tests deleting collections
    /// </summary>
    [Test]
    public async Task DeleteCollection_ShouldRemoveCollection()
    {
        // Arrange
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.CreateCollectionAsync("to_delete"),
            operationName: "Create collection to delete");
        var initialCollections = await TestUtilities.ExecuteWithTimeoutAsync(
            _service.ListCollectionsAsync(),
            operationName: "List initial collections");

        // Act
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.DeleteCollectionAsync("to_delete"),
            operationName: "Delete collection");
        var finalCollections = await TestUtilities.ExecuteWithTimeoutAsync(
            _service.ListCollectionsAsync(),
            operationName: "List collections after deletion");

        // Assert
        Assert.That(initialCollections, Does.Contain("to_delete"));
        Assert.That(finalCollections, Does.Not.Contain("to_delete"));
        Assert.That(finalCollections.Count, Is.EqualTo(initialCollections.Count - 1));
    }

    /// <summary>
    /// Tests error handling for non-existent collections
    /// </summary>
    [Test]
    public void GetNonExistentCollection_ShouldThrowException()
    {
        // Act & Assert
        Assert.ThrowsAsync<Python.Runtime.PythonException>(async () => 
            await TestUtilities.ExecuteWithTimeoutAsync(
                _service.GetCollectionAsync("non_existent"),
                operationName: "Get non-existent collection"));
    }

    /// <summary>
    /// Tests error handling for duplicate collection creation
    /// </summary>
    [Test]
    public async Task CreateDuplicateCollection_ShouldThrowException()
    {
        // Arrange
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.CreateCollectionAsync("duplicate_test"),
            operationName: "Create original collection");

        // Act & Assert
        Assert.ThrowsAsync<Python.Runtime.PythonException>(async () => 
            await TestUtilities.ExecuteWithTimeoutAsync(
                _service.CreateCollectionAsync("duplicate_test"),
                operationName: "Create duplicate collection"));
    }

    /// <summary>
    /// Tests adding documents with duplicate IDs
    /// </summary>
    [Test]
    public async Task AddDocumentsWithDuplicateIds_ShouldThrowException()
    {
        // Arrange
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.CreateCollectionAsync("duplicate_ids"),
            operationName: "Create collection for duplicate ID test");
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.AddDocumentsAsync("duplicate_ids", 
                new List<string> { "First doc" }, 
                new List<string> { "duplicate_id" }),
            operationName: "Add first document");

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(async () => 
            await TestUtilities.ExecuteWithTimeoutAsync(
                _service.AddDocumentsAsync("duplicate_ids", 
                    new List<string> { "Second doc" }, 
                    new List<string> { "duplicate_id" }),
                operationName: "Add duplicate document"));
    }

    /// <summary>
    /// Tests persistence across service instances (data survives service recreation)
    /// </summary>
    [Test]
    public async Task DataPersistence_ShouldSurviveServiceRecreation()
    {
        // Arrange - Create data with first service instance
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.CreateCollectionAsync("persistence_test"),
            operationName: "Create persistence test collection");
        await TestUtilities.ExecuteWithTimeoutAsync(
            _service.AddDocumentsAsync("persistence_test", 
                new List<string> { "Persistent document" }, 
                new List<string> { "persistent_id" }),
            operationName: "Add persistent document");

        // Act - Create new service instance pointing to same data directory
        var newService = new ChromaPersistentDbService(_logger, _options);
        
        var collections = await TestUtilities.ExecuteWithTimeoutAsync(
            newService.ListCollectionsAsync(),
            operationName: "List collections with new service");
        var count = await TestUtilities.ExecuteWithTimeoutAsync(
            newService.GetCollectionCountAsync("persistence_test"),
            operationName: "Get count with new service");

        // Assert
        Assert.That(collections, Does.Contain("persistence_test"));
        Assert.That(count, Is.EqualTo(1));
    }
}