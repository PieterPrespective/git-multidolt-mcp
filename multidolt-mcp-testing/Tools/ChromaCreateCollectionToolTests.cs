using Microsoft.Extensions.Logging;
using Moq;
using DMMS.Services;
using DMMS.Tools;

namespace DMMSTesting.Tools;

/// <summary>
/// Unit tests for ChromaCreateCollectionTool
/// </summary>
[TestFixture]
public class ChromaCreateCollectionToolTests
{
    private Mock<ILogger<ChromaCreateCollectionTool>> _mockLogger;
    private Mock<IChromaDbService> _mockChromaService;
    private ChromaCreateCollectionTool _tool;

    /// <summary>
    /// Sets up test environment before each test
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<ChromaCreateCollectionTool>>();
        _mockChromaService = new Mock<IChromaDbService>();
        _tool = new ChromaCreateCollectionTool(_mockLogger.Object, _mockChromaService.Object);
    }

    /// <summary>
    /// Tests successful creation of a collection
    /// </summary>
    [Test]
    public async Task CreateCollection_WithValidName_ReturnsSuccess()
    {
        // Arrange
        _mockChromaService.Setup(s => s.CreateCollectionAsync("test_collection", It.IsAny<Dictionary<string, object>?>()))
            .ReturnsAsync(true);

        // Act
        var result = await _tool.CreateCollection("test_collection");

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.True);
        Assert.That(resultObj?.message, Does.Contain("Successfully created collection 'test_collection'"));
    }

    /// <summary>
    /// Tests creation with empty collection name
    /// </summary>
    [Test]
    public async Task CreateCollection_WithEmptyName_ReturnsError()
    {
        // Act
        var result = await _tool.CreateCollection("");

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Is.EqualTo("Collection name is required"));
    }

    /// <summary>
    /// Tests creation with null collection name
    /// </summary>
    [Test]
    public async Task CreateCollection_WithNullName_ReturnsError()
    {
        // Act
        var result = await _tool.CreateCollection(null!);

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Is.EqualTo("Collection name is required"));
    }

    /// <summary>
    /// Tests creation with valid metadata JSON
    /// </summary>
    [Test]
    public async Task CreateCollection_WithValidMetadata_ParsesAndPassesMetadata()
    {
        // Arrange
        var metadataJson = @"{""description"": ""Test collection"", ""version"": 1}";
        Dictionary<string, object>? capturedMetadata = null;

        _mockChromaService.Setup(s => s.CreateCollectionAsync("test_collection", It.IsAny<Dictionary<string, object>?>()))
            .Callback<string, Dictionary<string, object>?>((name, metadata) => capturedMetadata = metadata)
            .ReturnsAsync(true);

        // Act
        var result = await _tool.CreateCollection("test_collection", "default", metadataJson);

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.True);
        Assert.That(capturedMetadata, Is.Not.Null);
        Assert.That(capturedMetadata!.ContainsKey("description"), Is.True);
        Assert.That(capturedMetadata["description"].ToString(), Is.EqualTo("Test collection"));
    }

    /// <summary>
    /// Tests creation with invalid metadata JSON
    /// </summary>
    [Test]
    public async Task CreateCollection_WithInvalidMetadata_ReturnsError()
    {
        // Arrange
        var invalidMetadataJson = @"{invalid json}";

        // Act
        var result = await _tool.CreateCollection("test_collection", "default", invalidMetadataJson);

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Does.Contain("Invalid metadata JSON"));
    }

    /// <summary>
    /// Tests creation when service fails
    /// </summary>
    [Test]
    public async Task CreateCollection_WhenServiceFails_ReturnsFailureMessage()
    {
        // Arrange
        _mockChromaService.Setup(s => s.CreateCollectionAsync("test_collection", It.IsAny<Dictionary<string, object>?>()))
            .ReturnsAsync(false);

        // Act
        var result = await _tool.CreateCollection("test_collection");

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.message, Is.EqualTo("Failed to create collection"));
    }

    /// <summary>
    /// Tests error handling when service throws exception
    /// </summary>
    [Test]
    public async Task CreateCollection_WhenServiceThrowsException_ReturnsError()
    {
        // Arrange
        var expectedException = new Exception("Service error");
        _mockChromaService.Setup(s => s.CreateCollectionAsync("test_collection", It.IsAny<Dictionary<string, object>?>()))
            .ThrowsAsync(expectedException);

        // Act
        var result = await _tool.CreateCollection("test_collection");

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Does.Contain("Service error"));
    }

    /// <summary>
    /// Tests that embedding function name is passed to service
    /// </summary>
    [Test]
    public async Task CreateCollection_WithEmbeddingFunction_LogsCorrectFunction()
    {
        // Arrange
        _mockChromaService.Setup(s => s.CreateCollectionAsync("test_collection", It.IsAny<Dictionary<string, object>?>()))
            .ReturnsAsync(true);

        // Act
        await _tool.CreateCollection("test_collection", "openai");

        // Assert
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("'openai'")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Tests that error logging occurs when exception is thrown
    /// </summary>
    [Test]
    public async Task CreateCollection_LogsErrorWhenExceptionThrown()
    {
        // Arrange
        var expectedException = new Exception("Test exception");
        _mockChromaService.Setup(s => s.CreateCollectionAsync("test_collection", It.IsAny<Dictionary<string, object>?>()))
            .ThrowsAsync(expectedException);

        // Act
        await _tool.CreateCollection("test_collection");

        // Assert
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error creating collection")),
                expectedException,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}