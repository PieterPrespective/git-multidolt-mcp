using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;
using DMMS.Services;

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
    public void TearDown()
    {
        // Clean up test directory
        if (Directory.Exists(_testDataPath))
        {
            Directory.Delete(_testDataPath, recursive: true);
        }
    }

    /// <summary>
    /// Tests creating and listing collections
    /// </summary>
    [Test]
    public async Task CreateAndListCollections_ShouldWork()
    {
        // Arrange & Act - Create collections
        await _service.CreateCollectionAsync("test_collection_1");
        await _service.CreateCollectionAsync("test_collection_2", new Dictionary<string, object>
        {
            { "description", "Test collection with metadata" },
            { "version", 1 }
        });

        var collections = await _service.ListCollectionsAsync();

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
        await _service.CreateCollectionAsync("docs_collection");
        
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
        await _service.AddDocumentsAsync("docs_collection", documents, ids, metadatas);

        // Act - Query documents
        var queryResult = await _service.QueryDocumentsAsync("docs_collection", new List<string> { "artificial intelligence" });

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
        await _service.CreateCollectionAsync("count_test");
        var documents = new List<string> { "Doc 1", "Doc 2", "Doc 3", "Doc 4", "Doc 5" };
        var ids = new List<string> { "1", "2", "3", "4", "5" };

        // Act
        await _service.AddDocumentsAsync("count_test", documents, ids);
        var count = await _service.GetCollectionCountAsync("count_test");

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
        await _service.CreateCollectionAsync("update_test");
        await _service.AddDocumentsAsync("update_test", 
            new List<string> { "Original document" }, 
            new List<string> { "doc1" });

        // Act - Update document
        await _service.UpdateDocumentsAsync("update_test", 
            new List<string> { "doc1" }, 
            new List<string> { "Updated document" });

        // Act - Get updated document
        var result = await _service.GetDocumentsAsync("update_test", new List<string> { "doc1" });

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
        await _service.CreateCollectionAsync("delete_test");
        await _service.AddDocumentsAsync("delete_test", 
            new List<string> { "Doc 1", "Doc 2", "Doc 3" }, 
            new List<string> { "1", "2", "3" });

        // Act - Delete one document
        await _service.DeleteDocumentsAsync("delete_test", new List<string> { "2" });
        var count = await _service.GetCollectionCountAsync("delete_test");

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
        await _service.CreateCollectionAsync("to_delete");
        var initialCollections = await _service.ListCollectionsAsync();

        // Act
        await _service.DeleteCollectionAsync("to_delete");
        var finalCollections = await _service.ListCollectionsAsync();

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
        Assert.ThrowsAsync<InvalidOperationException>(async () => 
            await _service.GetCollectionAsync("non_existent"));
    }

    /// <summary>
    /// Tests error handling for duplicate collection creation
    /// </summary>
    [Test]
    public async Task CreateDuplicateCollection_ShouldThrowException()
    {
        // Arrange
        await _service.CreateCollectionAsync("duplicate_test");

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(async () => 
            await _service.CreateCollectionAsync("duplicate_test"));
    }

    /// <summary>
    /// Tests adding documents with duplicate IDs
    /// </summary>
    [Test]
    public async Task AddDocumentsWithDuplicateIds_ShouldThrowException()
    {
        // Arrange
        await _service.CreateCollectionAsync("duplicate_ids");
        await _service.AddDocumentsAsync("duplicate_ids", 
            new List<string> { "First doc" }, 
            new List<string> { "duplicate_id" });

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(async () => 
            await _service.AddDocumentsAsync("duplicate_ids", 
                new List<string> { "Second doc" }, 
                new List<string> { "duplicate_id" }));
    }

    /// <summary>
    /// Tests persistence across service instances (data survives service recreation)
    /// </summary>
    [Test]
    public async Task DataPersistence_ShouldSurviveServiceRecreation()
    {
        // Arrange - Create data with first service instance
        await _service.CreateCollectionAsync("persistence_test");
        await _service.AddDocumentsAsync("persistence_test", 
            new List<string> { "Persistent document" }, 
            new List<string> { "persistent_id" });

        // Act - Create new service instance pointing to same data directory
        var newService = new ChromaPersistentDbService(_logger, _options);
        
        var collections = await newService.ListCollectionsAsync();
        var count = await newService.GetCollectionCountAsync("persistence_test");

        // Assert
        Assert.That(collections, Does.Contain("persistence_test"));
        Assert.That(count, Is.EqualTo(1));
    }
}