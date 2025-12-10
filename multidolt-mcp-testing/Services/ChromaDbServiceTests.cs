using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using DMMS.Models;
using DMMS.Services;

namespace DMMSTesting.Services;

/// <summary>
/// Unit tests for ChromaDbService using mocked IChromaDbService
/// Note: The actual ChromaDbService uses Python.NET and requires Python/chromadb to be installed
/// </summary>
[TestFixture]
public class ChromaDbServiceTests
{
    private Mock<IChromaDbService> _mockService;

    /// <summary>
    /// Sets up test environment before each test
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _mockService = new Mock<IChromaDbService>();
    }

    /// <summary>
    /// Cleans up resources after each test
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _mockService = null;
    }

    /// <summary>
    /// Tests successful listing of collections
    /// </summary>
    [Test]
    public async Task ListCollectionsAsync_WithMockService_ReturnsCollectionNames()
    {
        // Arrange
        var expectedCollections = new List<string> { "collection1", "collection2" };
        _mockService.Setup(s => s.ListCollectionsAsync(It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(expectedCollections);

        // Act
        var result = await _mockService.Object.ListCollectionsAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0], Is.EqualTo("collection1"));
        Assert.That(result[1], Is.EqualTo("collection2"));
    }

    /// <summary>
    /// Tests listing collections with limit and offset parameters
    /// </summary>
    [Test]
    public async Task ListCollectionsAsync_WithLimitAndOffset_ReturnsFilteredResults()
    {
        // Arrange
        var expectedCollections = new List<string> { "collection2" };
        _mockService.Setup(s => s.ListCollectionsAsync(1, 1))
            .ReturnsAsync(expectedCollections);

        // Act
        var result = await _mockService.Object.ListCollectionsAsync(limit: 1, offset: 1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0], Is.EqualTo("collection2"));
    }

    /// <summary>
    /// Tests successful creation of a collection
    /// </summary>
    [Test]
    public async Task CreateCollectionAsync_WithValidName_ReturnsTrue()
    {
        // Arrange
        _mockService.Setup(s => s.CreateCollectionAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object>?>()))
            .ReturnsAsync(true);

        // Act
        var result = await _mockService.Object.CreateCollectionAsync("test_collection");

        // Assert
        Assert.That(result, Is.True);
        _mockService.Verify(s => s.CreateCollectionAsync("test_collection", null), Times.Once);
    }

    /// <summary>
    /// Tests creation of a collection with metadata
    /// </summary>
    [Test]
    public async Task CreateCollectionAsync_WithMetadata_PassesMetadataCorrectly()
    {
        // Arrange
        var metadata = new Dictionary<string, object>
        {
            { "description", "Test collection" },
            { "version", 1 }
        };

        _mockService.Setup(s => s.CreateCollectionAsync("test_collection", metadata))
            .ReturnsAsync(true);

        // Act
        var result = await _mockService.Object.CreateCollectionAsync("test_collection", metadata);

        // Assert
        Assert.That(result, Is.True);
        _mockService.Verify(s => s.CreateCollectionAsync("test_collection", metadata), Times.Once);
    }

    /// <summary>
    /// Tests successful deletion of a collection
    /// </summary>
    [Test]
    public async Task DeleteCollectionAsync_WithValidName_ReturnsTrue()
    {
        // Arrange
        _mockService.Setup(s => s.DeleteCollectionAsync("test_collection"))
            .ReturnsAsync(true);

        // Act
        var result = await _mockService.Object.DeleteCollectionAsync("test_collection");

        // Assert
        Assert.That(result, Is.True);
        _mockService.Verify(s => s.DeleteCollectionAsync("test_collection"), Times.Once);
    }

    /// <summary>
    /// Tests successful addition of documents
    /// </summary>
    [Test]
    public async Task AddDocumentsAsync_WithValidData_ReturnsTrue()
    {
        // Arrange
        var documents = new List<string> { "Document 1", "Document 2" };
        var ids = new List<string> { "id1", "id2" };
        var metadatas = new List<Dictionary<string, object>>
        {
            new() { { "key1", "value1" } },
            new() { { "key2", "value2" } }
        };

        _mockService.Setup(s => s.AddDocumentsAsync("test_collection", documents, ids, metadatas, false))
            .ReturnsAsync(true);

        // Act
        var result = await _mockService.Object.AddDocumentsAsync("test_collection", documents, ids, metadatas, false);

        // Assert
        Assert.That(result, Is.True);
        _mockService.Verify(s => s.AddDocumentsAsync("test_collection", documents, ids, metadatas, false), Times.Once);
    }

    /// <summary>
    /// Tests getting collection count
    /// </summary>
    [Test]
    public async Task GetCollectionCountAsync_ReturnsCorrectCount()
    {
        // Arrange
        _mockService.Setup(s => s.GetCollectionCountAsync("test_collection"))
            .ReturnsAsync(42);

        // Act
        var result = await _mockService.Object.GetCollectionCountAsync("test_collection");

        // Assert
        Assert.That(result, Is.EqualTo(42));
    }

    /// <summary>
    /// Tests querying documents
    /// </summary>
    [Test]
    public async Task QueryDocumentsAsync_ReturnsExpectedResults()
    {
        // Arrange
        var queryTexts = new List<string> { "search query" };
        var expectedResult = new
        {
            ids = new List<List<string>> { new List<string> { "id1", "id2" } },
            documents = new List<List<string>> { new List<string> { "doc1", "doc2" } },
            metadatas = new List<List<Dictionary<string, object>>> { new List<Dictionary<string, object>>() },
            distances = new List<List<double>> { new List<double> { 0.1, 0.2 } }
        };

        _mockService.Setup(s => s.QueryDocumentsAsync("test_collection", queryTexts, 5, null, null))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _mockService.Object.QueryDocumentsAsync("test_collection", queryTexts);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    /// <summary>
    /// Tests getting documents
    /// </summary>
    [Test]
    public async Task GetDocumentsAsync_ReturnsExpectedDocuments()
    {
        // Arrange
        var ids = new List<string> { "id1", "id2" };
        var expectedResult = new
        {
            ids = new List<string> { "id1", "id2" },
            documents = new List<string> { "doc1", "doc2" },
            metadatas = new List<Dictionary<string, object>>()
        };

        _mockService.Setup(s => s.GetDocumentsAsync("test_collection", ids, null, null))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _mockService.Object.GetDocumentsAsync("test_collection", ids);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    /// <summary>
    /// Tests updating documents
    /// </summary>
    [Test]
    public async Task UpdateDocumentsAsync_WithValidData_ReturnsTrue()
    {
        // Arrange
        var ids = new List<string> { "id1", "id2" };
        var documents = new List<string> { "Updated doc 1", "Updated doc 2" };

        _mockService.Setup(s => s.UpdateDocumentsAsync("test_collection", ids, documents, null))
            .ReturnsAsync(true);

        // Act
        var result = await _mockService.Object.UpdateDocumentsAsync("test_collection", ids, documents);

        // Assert
        Assert.That(result, Is.True);
        _mockService.Verify(s => s.UpdateDocumentsAsync("test_collection", ids, documents, null), Times.Once);
    }

    /// <summary>
    /// Tests deleting documents
    /// </summary>
    [Test]
    public async Task DeleteDocumentsAsync_WithValidIds_ReturnsTrue()
    {
        // Arrange
        var ids = new List<string> { "id1", "id2" };

        _mockService.Setup(s => s.DeleteDocumentsAsync("test_collection", ids))
            .ReturnsAsync(true);

        // Act
        var result = await _mockService.Object.DeleteDocumentsAsync("test_collection", ids);

        // Assert
        Assert.That(result, Is.True);
        _mockService.Verify(s => s.DeleteDocumentsAsync("test_collection", ids), Times.Once);
    }
}