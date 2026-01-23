using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Embranch.Models;
using Embranch.Services;
using Embranch.Tools;

namespace EmbranchTesting.UnitTests;

[TestFixture]
public class ChromaModifyCollectionToolTests
{
    private Mock<ILogger<ChromaModifyCollectionTool>> _mockLogger = null!;
    private Mock<IChromaDbService> _mockChromaService = null!;
    private Mock<IDeletionTracker> _mockDeletionTracker = null!;
    private Mock<IDoltCli> _mockDoltCli = null!;
    private Mock<IOptions<DoltConfiguration>> _mockDoltOptions = null!;
    private DoltConfiguration _doltConfig = null!;
    private ChromaModifyCollectionTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<ChromaModifyCollectionTool>>();
        _mockChromaService = new Mock<IChromaDbService>();
        _mockDeletionTracker = new Mock<IDeletionTracker>();
        _mockDoltCli = new Mock<IDoltCli>();
        _mockDoltOptions = new Mock<IOptions<DoltConfiguration>>();
        
        _doltConfig = new DoltConfiguration
        {
            RepositoryPath = "/test/repo"
        };
        _mockDoltOptions.Setup(x => x.Value).Returns(_doltConfig);

        _tool = new ChromaModifyCollectionTool(
            _mockLogger.Object,
            _mockChromaService.Object,
            _mockDeletionTracker.Object,
            _mockDoltCli.Object,
            _mockDoltOptions.Object
        );
    }

    [Test]
    public async Task ModifyCollection_WithRename_ShouldTrackRenameOperation()
    {
        // Arrange
        const string originalName = "test-collection";
        const string newName = "renamed-collection";
        var collectionData = new Dictionary<string, object>
        {
            ["id"] = "12345",
            ["name"] = originalName,
            ["metadata"] = new Dictionary<string, object> { ["key1"] = "value1" }
        };

        _mockChromaService.Setup(x => x.GetCollectionAsync(originalName))
            .ReturnsAsync(collectionData);
        
        _mockDoltCli.Setup(x => x.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        
        _mockDoltCli.Setup(x => x.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");

        // Act
        var result = await _tool.ModifyCollection(originalName, newName);

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False); // Expected since backend is not implemented
        Assert.That(resultObj?.stub, Is.True);
        Assert.That(resultObj?.tracked, Is.True);
        Assert.That(resultObj?.tracking_details?.operation_type, Is.EqualTo("rename"));
        Assert.That(resultObj?.tracking_details?.original_name, Is.EqualTo(originalName));
        Assert.That(resultObj?.tracking_details?.new_name, Is.EqualTo(newName));

        // Verify tracking was called for rename
        _mockDeletionTracker.Verify(x => x.TrackCollectionUpdateAsync(
            _doltConfig.RepositoryPath,
            originalName,
            newName,
            It.IsAny<Dictionary<string, object>>(),
            It.IsAny<Dictionary<string, object>>(),
            "main",
            "abc123"
        ), Times.Once);
    }

    [Test]
    public async Task ModifyCollection_WithMetadataUpdate_ShouldTrackMetadataOperation()
    {
        // Arrange
        const string collectionName = "test-collection";
        var newMetadata = new Dictionary<string, object> { ["key2"] = "value2", ["key3"] = 123 };
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

        // Act
        var result = await _tool.ModifyCollection(collectionName, null, newMetadata);

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False); // Expected since backend is not implemented
        Assert.That(resultObj?.tracked, Is.True);
        Assert.That(resultObj?.tracking_details?.operation_type, Is.EqualTo("metadata_update"));
        Assert.That(resultObj?.tracking_details?.has_metadata_changes, Is.True);

        // Verify tracking was called for metadata update
        _mockDeletionTracker.Verify(x => x.TrackCollectionUpdateAsync(
            _doltConfig.RepositoryPath,
            collectionName,
            collectionName, // New name same as old for metadata-only update
            It.IsAny<Dictionary<string, object>>(),
            newMetadata,
            "main",
            "abc123"
        ), Times.Once);
    }

    [Test]
    public async Task ModifyCollection_WithBothRenameAndMetadata_ShouldTrackRenameOperation()
    {
        // Arrange
        const string originalName = "test-collection";
        const string newName = "renamed-collection";
        var newMetadata = new Dictionary<string, object> { ["key2"] = "value2" };
        var collectionData = new Dictionary<string, object>
        {
            ["metadata"] = new Dictionary<string, object> { ["key1"] = "value1" }
        };

        _mockChromaService.Setup(x => x.GetCollectionAsync(originalName))
            .ReturnsAsync(collectionData);
        
        _mockDoltCli.Setup(x => x.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        
        _mockDoltCli.Setup(x => x.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");

        // Act
        var result = await _tool.ModifyCollection(originalName, newName, newMetadata);

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.tracked, Is.True);
        Assert.That(resultObj?.tracking_details?.operation_type, Is.EqualTo("rename")); // Rename takes precedence

        // Verify tracking was called
        _mockDeletionTracker.Verify(x => x.TrackCollectionUpdateAsync(
            _doltConfig.RepositoryPath,
            originalName,
            newName,
            It.IsAny<Dictionary<string, object>>(),
            newMetadata,
            "main",
            "abc123"
        ), Times.Once);
    }

    [Test]
    public async Task ModifyCollection_WithNoChanges_ShouldNotTrack()
    {
        // Arrange
        const string collectionName = "test-collection";
        var collectionData = new Dictionary<string, object>
        {
            ["metadata"] = new Dictionary<string, object> { ["key1"] = "value1" }
        };

        _mockChromaService.Setup(x => x.GetCollectionAsync(collectionName))
            .ReturnsAsync(collectionData);

        // Act - no new name and no new metadata
        var result = await _tool.ModifyCollection(collectionName);

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False); // Expected since backend is not implemented
        Assert.That(resultObj?.tracked, Is.False); // Should not track when no changes

        // Verify tracking was not called
        _mockDeletionTracker.Verify(x => x.TrackCollectionUpdateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, object>>(), It.IsAny<Dictionary<string, object>>(),
            It.IsAny<string>(), It.IsAny<string>()
        ), Times.Never);
    }

    [Test]
    public async Task ModifyCollection_WithNonExistentCollection_ShouldReturnNotFoundError()
    {
        // Arrange
        const string collectionName = "nonexistent-collection";
        
        _mockChromaService.Setup(x => x.GetCollectionAsync(collectionName))
            .ReturnsAsync((object?)null);

        // Act
        var result = await _tool.ModifyCollection(collectionName, "new-name");

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Is.EqualTo("COLLECTION_NOT_FOUND"));
        Assert.That(resultObj?.message, Does.Contain("does not exist"));

        // Verify tracking was not called
        _mockDeletionTracker.Verify(x => x.TrackCollectionUpdateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, object>>(), It.IsAny<Dictionary<string, object>>(),
            It.IsAny<string>(), It.IsAny<string>()
        ), Times.Never);
    }

    [Test]
    public async Task ModifyCollection_WithEmptyCollectionName_ShouldReturnError()
    {
        // Arrange & Act
        var result = await _tool.ModifyCollection("");

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Is.EqualTo("COLLECTION_NAME_REQUIRED"));

        // Verify no service calls were made
        _mockChromaService.Verify(x => x.GetCollectionAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ModifyCollection_WhenTrackingThrowsException_ShouldReturnError()
    {
        // Arrange
        const string collectionName = "test-collection";
        var newMetadata = new Dictionary<string, object> { ["key2"] = "value2" };
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
        
        _mockDeletionTracker.Setup(x => x.TrackCollectionUpdateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, object>>(), It.IsAny<Dictionary<string, object>>(),
            It.IsAny<string>(), It.IsAny<string>()
        )).ThrowsAsync(new Exception("Tracking failed"));

        // Act
        var result = await _tool.ModifyCollection(collectionName, null, newMetadata);

        // Assert
        var resultObj = result as dynamic;
        Assert.That(resultObj?.success, Is.False);
        Assert.That(resultObj?.error, Is.EqualTo("OPERATION_FAILED"));
        Assert.That(resultObj?.message, Does.Contain("Failed to modify collection"));
    }

    [Test]
    public async Task ExtractCollectionMetadata_WithComplexData_ShouldExtractCorrectly()
    {
        // Arrange
        const string collectionName = "test-collection";
        var collectionData = new Dictionary<string, object>
        {
            ["id"] = "12345",
            ["name"] = collectionName,
            ["metadata"] = new Dictionary<string, object> { ["key1"] = "value1" },
            ["custom_field"] = "custom_value",
            ["numeric_field"] = 42
        };
        var newName = "new-name";

        _mockChromaService.Setup(x => x.GetCollectionAsync(collectionName))
            .ReturnsAsync(collectionData);
        
        _mockDoltCli.Setup(x => x.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        
        _mockDoltCli.Setup(x => x.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");

        // Act
        await _tool.ModifyCollection(collectionName, newName);

        // Assert - Verify tracking was called with correctly extracted metadata (excluding id and name)
        _mockDeletionTracker.Verify(x => x.TrackCollectionUpdateAsync(
            _doltConfig.RepositoryPath,
            collectionName,
            newName,
            It.Is<Dictionary<string, object>>(m => 
                m.ContainsKey("metadata") && 
                m.ContainsKey("custom_field") && 
                m.ContainsKey("numeric_field") && 
                !m.ContainsKey("id") && 
                !m.ContainsKey("name")),
            It.IsAny<Dictionary<string, object>>(),
            "main",
            "abc123"
        ), Times.Once);
    }
}