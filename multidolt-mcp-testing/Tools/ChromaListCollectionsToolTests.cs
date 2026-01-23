using Microsoft.Extensions.Logging;
using Moq;
using Embranch.Services;
using Embranch.Tools;

namespace EmbranchTesting.Tools;

/// <summary>
/// Unit tests for ChromaListCollectionsTool
/// </summary>
[TestFixture]
public class ChromaListCollectionsToolTests
{
    private Mock<ILogger<ChromaListCollectionsTool>> _mockLogger;
    private Mock<IChromaDbService> _mockChromaService;
    private ChromaListCollectionsTool _tool;

    /// <summary>
    /// Sets up test environment before each test
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<ChromaListCollectionsTool>>();
        _mockChromaService = new Mock<IChromaDbService>();
        _tool = new ChromaListCollectionsTool(_mockLogger.Object, _mockChromaService.Object);
    }

    /// <summary>
    /// Tests successful listing of collections
    /// </summary>
    [Test]
    public async Task ListCollections_WithExistingCollections_ReturnsSuccess()
    {
        // Arrange
        var collections = new List<string> { "collection1", "collection2", "collection3" };
        _mockChromaService.Setup(s => s.ListCollectionsAsync(It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(collections);

        // Act
        var result = await _tool.ListCollections();

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.collections, Is.Not.Null);
        Assert.That(resultObj?.total_count, Is.EqualTo(3));
        var resultCollections = resultObj?.collections as string[];
        Assert.That(resultCollections, Is.EqualTo(new[] { "collection1", "collection2", "collection3" }));
    }

    /// <summary>
    /// Tests listing collections when no collections exist
    /// </summary>
    [Test]
    public async Task ListCollections_WithNoCollections_ReturnsNoCollectionsFound()
    {
        // Arrange
        var emptyCollections = new List<string>();
        _mockChromaService.Setup(s => s.ListCollectionsAsync(It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(emptyCollections);

        // Act
        var result = await _tool.ListCollections();

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.collections, Is.Not.Null);
        Assert.That(resultObj?.total_count, Is.EqualTo(0));
        var resultCollections = resultObj?.collections as string[];
        Assert.That(resultCollections, Is.Empty);
    }

    /// <summary>
    /// Tests listing collections with limit and offset parameters
    /// </summary>
    [Test]
    public async Task ListCollections_WithLimitAndOffset_PassesParametersCorrectly()
    {
        // Arrange
        var collections = new List<string> { "collection2" };
        _mockChromaService.Setup(s => s.ListCollectionsAsync(1, 1))
            .ReturnsAsync(collections);

        // Act
        var result = await _tool.ListCollections(limit: 1, offset: 1);

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.collections, Is.Not.Null);
        Assert.That(resultObj?.total_count, Is.EqualTo(1));
        var returnedCollections = resultObj?.collections as string[];
        Assert.That(returnedCollections, Is.EqualTo(new[] { "collection2" }));
        _mockChromaService.Verify(s => s.ListCollectionsAsync(1, 1), Times.Once);
    }

    /// <summary>
    /// Tests error handling when service throws exception
    /// </summary>
    [Test]
    public async Task ListCollections_WhenServiceThrowsException_ReturnsError()
    {
        // Arrange
        var expectedException = new Exception("Service error");
        _mockChromaService.Setup(s => s.ListCollectionsAsync(It.IsAny<int?>(), It.IsAny<int?>()))
            .ThrowsAsync(expectedException);

        // Act
        var result = await _tool.ListCollections();

        // Assert
        Assert.That(result, Is.Not.Null);
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Does.Contain("Service error"));
    }

    /// <summary>
    /// Tests that logging occurs during operation
    /// </summary>
    [Test]
    public async Task ListCollections_LogsInformationDuringOperation()
    {
        // Arrange
        var collections = new List<string> { "test_collection" };
        _mockChromaService.Setup(s => s.ListCollectionsAsync(It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(collections);

        // Act
        await _tool.ListCollections();

        // Assert
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Listing collections")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Tests that error logging occurs when exception is thrown
    /// </summary>
    [Test]
    public async Task ListCollections_LogsErrorWhenExceptionThrown()
    {
        // Arrange
        var expectedException = new Exception("Test exception");
        _mockChromaService.Setup(s => s.ListCollectionsAsync(It.IsAny<int?>(), It.IsAny<int?>()))
            .ThrowsAsync(expectedException);

        // Act
        await _tool.ListCollections();

        // Assert
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("TOOLCALL EXIT : ListCollections - EXCEPTION")),
                expectedException,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}