using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using DMMS.Models;
using DMMS.Services;
using DMMS.Tools;

namespace DMMS.Testing.UnitTests;

[TestFixture]
public class ChromaDeleteCollectionToolTests
{
    private Mock<ILogger<ChromaDeleteCollectionTool>> _mockLogger = null!;
    private Mock<IChromaDbService> _mockChromaService = null!;
    private Mock<IDeletionTracker> _mockDeletionTracker = null!;
    private Mock<IDoltCli> _mockDoltCli = null!;
    private Mock<IOptions<DoltConfiguration>> _mockDoltOptions = null!;
    private DoltConfiguration _doltConfig = null!;
    private ChromaDeleteCollectionTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<ChromaDeleteCollectionTool>>();
        _mockChromaService = new Mock<IChromaDbService>();
        _mockDeletionTracker = new Mock<IDeletionTracker>();
        _mockDoltCli = new Mock<IDoltCli>();
        _mockDoltOptions = new Mock<IOptions<DoltConfiguration>>();
        
        _doltConfig = new DoltConfiguration
        {
            RepositoryPath = "/test/repo"
        };
        _mockDoltOptions.Setup(x => x.Value).Returns(_doltConfig);

        _tool = new ChromaDeleteCollectionTool(
            _mockLogger.Object,
            _mockChromaService.Object,
            _mockDeletionTracker.Object,
            _mockDoltCli.Object,
            _mockDoltOptions.Object
        );
    }

    [Test]
    public async Task DeleteCollection_WithValidCollection_ShouldTrackDeletionAndDeleteCollection()
    {
        // Arrange
        const string collectionName = "test-collection";
        var collectionData = new Dictionary<string, object>
        {
            ["id"] = "12345",
            ["name"] = collectionName,
            ["metadata"] = new Dictionary<string, object> { ["key1"] = "value1" }
        };

        _mockChromaService.Setup(x => x.GetCollectionAsync(collectionName))
            .ReturnsAsync(collectionData);
        
        _mockDoltCli.Setup(x => x.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        
        _mockDoltCli.Setup(x => x.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");
        
        _mockChromaService.Setup(x => x.DeleteCollectionAsync(collectionName))
            .ReturnsAsync(true);

        // Act
        var result = await _tool.DeleteCollection(collectionName);

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.True);
        Assert.That(resultObj?.tracked, Is.True);
        Assert.That(resultObj?.deletionDetails?.collectionName, Is.EqualTo(collectionName));

        // Verify tracking was called
        _mockDeletionTracker.Verify(x => x.TrackCollectionDeletionAsync(
            _doltConfig.RepositoryPath,
            collectionName,
            It.Is<Dictionary<string, object>>(m => m.ContainsKey("metadata")),
            "main",
            "abc123"
        ), Times.Once);

        // Verify deletion was called
        _mockChromaService.Verify(x => x.DeleteCollectionAsync(collectionName), Times.Once);
    }

    [Test]
    public async Task DeleteCollection_WithNonExistentCollection_ShouldReturnNotFoundError()
    {
        // Arrange
        const string collectionName = "nonexistent-collection";
        
        _mockChromaService.Setup(x => x.GetCollectionAsync(collectionName))
            .ReturnsAsync((object?)null);

        // Act
        var result = await _tool.DeleteCollection(collectionName);

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Does.Contain("not found"));

        // Verify tracking was not called
        _mockDeletionTracker.Verify(x => x.TrackCollectionDeletionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(),
            It.IsAny<string>(), It.IsAny<string>()
        ), Times.Never);
    }

    [Test]
    public async Task DeleteCollection_WithEmptyCollectionName_ShouldReturnError()
    {
        // Arrange & Act
        var result = await _tool.DeleteCollection("");

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Does.Contain("required"));

        // Verify no service calls were made
        _mockChromaService.Verify(x => x.GetCollectionAsync(It.IsAny<string>()), Times.Never);
        _mockDeletionTracker.Verify(x => x.TrackCollectionDeletionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(),
            It.IsAny<string>(), It.IsAny<string>()
        ), Times.Never);
    }

    [Test]
    public async Task DeleteCollection_WhenChromaDeleteFails_ShouldReturnErrorButStillTracked()
    {
        // Arrange
        const string collectionName = "test-collection";
        var collectionData = new Dictionary<string, object>
        {
            ["metadata"] = new Dictionary<string, object> { ["key1"] = "value1" }
        };

        _mockChromaService.Setup(x => x.GetCollectionAsync(collectionName))
            .ReturnsAsync(collectionData);
        
        _mockDoltCli.Setup(x => x.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        
        _mockDoltCli.Setup(x => x.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");
        
        _mockChromaService.Setup(x => x.DeleteCollectionAsync(collectionName))
            .ReturnsAsync(false);

        // Act
        var result = await _tool.DeleteCollection(collectionName);

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.tracked, Is.True);
        Assert.That(resultObj?.message, Does.Contain("deletion was tracked"));

        // Verify tracking was still called
        _mockDeletionTracker.Verify(x => x.TrackCollectionDeletionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(),
            It.IsAny<string>(), It.IsAny<string>()
        ), Times.Once);
    }

    [Test]
    public async Task DeleteCollection_WhenTrackingThrowsException_ShouldReturnError()
    {
        // Arrange
        const string collectionName = "test-collection";
        var collectionData = new Dictionary<string, object>
        {
            ["metadata"] = new Dictionary<string, object> { ["key1"] = "value1" }
        };

        _mockChromaService.Setup(x => x.GetCollectionAsync(collectionName))
            .ReturnsAsync(collectionData);
        
        _mockDoltCli.Setup(x => x.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        
        _mockDoltCli.Setup(x => x.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");
        
        _mockDeletionTracker.Setup(x => x.TrackCollectionDeletionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(),
            It.IsAny<string>(), It.IsAny<string>()
        )).ThrowsAsync(new Exception("Tracking failed"));

        // Act
        var result = await _tool.DeleteCollection(collectionName);

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Does.Contain("Failed to delete collection"));

        // Verify ChromaDB deletion was not called due to tracking failure
        _mockChromaService.Verify(x => x.DeleteCollectionAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ExtractCollectionMetadata_WithDictionaryFormat_ShouldExtractCorrectly()
    {
        // Arrange
        const string collectionName = "test-collection";
        var collectionData = new Dictionary<string, object>
        {
            ["id"] = "12345",
            ["name"] = collectionName,
            ["metadata"] = new Dictionary<string, object> { ["key1"] = "value1", ["key2"] = 42 },
            ["custom_field"] = "custom_value"
        };

        _mockChromaService.Setup(x => x.GetCollectionAsync(collectionName))
            .ReturnsAsync(collectionData);
        
        _mockDoltCli.Setup(x => x.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        
        _mockDoltCli.Setup(x => x.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");
        
        _mockChromaService.Setup(x => x.DeleteCollectionAsync(collectionName))
            .ReturnsAsync(true);

        // Act
        var result = await _tool.DeleteCollection(collectionName);

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.True);

        // Verify tracking was called with correct metadata (excluding id and name)
        _mockDeletionTracker.Verify(x => x.TrackCollectionDeletionAsync(
            _doltConfig.RepositoryPath,
            collectionName,
            It.Is<Dictionary<string, object>>(m => 
                m.ContainsKey("metadata") && 
                m.ContainsKey("custom_field") && 
                !m.ContainsKey("id") && 
                !m.ContainsKey("name")),
            "main",
            "abc123"
        ), Times.Once);
    }
}