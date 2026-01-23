using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Embranch.Models;
using Embranch.Services;

namespace EmbranchTesting.UnitTests;

[TestFixture]
public class CollectionSyncManagerTests
{
    private Mock<IDoltCli> _mockDoltCli = null!;
    private Mock<IChromaDbService> _mockChromaService = null!;
    private Mock<IDeletionTracker> _mockDeletionTracker = null!;
    private Mock<IOptions<DoltConfiguration>> _mockDoltOptions = null!;
    private Mock<ILogger<SyncManagerV2>> _mockLogger = null!;
    private DoltConfiguration _doltConfig = null!;
    private SyncManagerV2 _syncManager = null!;

    [SetUp]
    public void SetUp()
    {
        _mockDoltCli = new Mock<IDoltCli>();
        _mockChromaService = new Mock<IChromaDbService>();
        _mockDeletionTracker = new Mock<IDeletionTracker>();
        _mockDoltOptions = new Mock<IOptions<DoltConfiguration>>();
        _mockLogger = new Mock<ILogger<SyncManagerV2>>();
        
        _doltConfig = new DoltConfiguration
        {
            RepositoryPath = "/test/repo"
        };
        _mockDoltOptions.Setup(x => x.Value).Returns(_doltConfig);
        
        // CRITICAL: Setup Dolt initialization check - without this, many operations may fail
        _mockDoltCli.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        
        // Setup global mocks for all SQL operations that SyncManagerV2 may perform
        // Mock SHOW TABLES query
        var mockTables = new List<Dictionary<string, object>>
        {
            new() { ["Tables_in_database"] = "collections" },
            new() { ["Tables_in_database"] = "documents" }
        };
        _mockDoltCli.Setup(x => x.QueryAsync<Dictionary<string, object>>(
            It.Is<string>(sql => sql.Contains("SHOW TABLES"))))
            .ReturnsAsync(mockTables);
            
        // Mock DELETE operations (documents and collections)
        _mockDoltCli.Setup(x => x.QueryAsync<object>(
            It.Is<string>(sql => sql.Contains("DELETE FROM documents"))))
            .ReturnsAsync(new List<object>());
            
        _mockDoltCli.Setup(x => x.QueryAsync<object>(
            It.Is<string>(sql => sql.Contains("DELETE FROM collections"))))
            .ReturnsAsync(new List<object>());
            
        // Mock UPDATE operations (for renames and metadata updates)
        _mockDoltCli.Setup(x => x.QueryAsync<object>(
            It.Is<string>(sql => sql.Contains("UPDATE collections SET collection_name"))))
            .ReturnsAsync(new List<object>());
            
        _mockDoltCli.Setup(x => x.QueryAsync<object>(
            It.Is<string>(sql => sql.Contains("UPDATE documents SET collection_name"))))
            .ReturnsAsync(new List<object>());
            
        _mockDoltCli.Setup(x => x.QueryAsync<object>(
            It.Is<string>(sql => sql.Contains("UPDATE collections SET metadata"))))
            .ReturnsAsync(new List<object>());

        _syncManager = new SyncManagerV2(
            _mockDoltCli.Object,
            _mockChromaService.Object,
            _mockDeletionTracker.Object,
            Mock.Of<ISyncStateTracker>(), // ISyncStateTracker (separate mock for interface)
            _mockDoltOptions.Object,
            _mockLogger.Object
        );
    }

    [Test]
    public async Task SyncCollectionChangesAsync_WithNoChanges_ShouldReturnNoChanges()
    {
        // Arrange
        _mockDeletionTracker.Setup(x => x.GetPendingCollectionDeletionsAsync(_doltConfig.RepositoryPath))
            .ReturnsAsync(new List<CollectionDeletionRecord>());

        // Act
        var result = await _syncManager.SyncCollectionChangesAsync();

        // Assert
        Assert.That(result.Status, Is.EqualTo(SyncStatusV2.NoChanges));
        Assert.That(result.TotalCollectionChanges, Is.EqualTo(0));
        Assert.That(result.Success, Is.True);

        // Verify no Dolt operations were performed
        _mockDoltCli.Verify(x => x.CommitAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task SyncCollectionChangesAsync_WithCollectionDeletion_ShouldProcessDeletionAndCommit()
    {
        // Arrange
        var deletionRecord = new CollectionDeletionRecord
        {
            Id = "test-id",
            CollectionName = "test-collection",
            OperationType = "deletion",
            OriginalMetadata = "{}",
            RepoPath = _doltConfig.RepositoryPath
        };

        _mockDeletionTracker.Setup(x => x.GetPendingCollectionDeletionsAsync(_doltConfig.RepositoryPath))
            .ReturnsAsync(new List<CollectionDeletionRecord> { deletionRecord });

        // Mock test-specific document query for cascade deletion
        var mockDocuments = new List<Dictionary<string, object>>
        {
            new() { ["doc_id"] = "doc1" },
            new() { ["doc_id"] = "doc2" }
        };
        _mockDoltCli.Setup(x => x.QueryAsync<Dictionary<string, object>>(
            It.Is<string>(sql => sql.Contains("SELECT doc_id FROM documents"))))
            .ReturnsAsync(mockDocuments);

        var commitResult = new CommitResult(true, "abc123", "Commit successful");
        _mockDoltCli.Setup(x => x.CommitAsync(It.IsAny<string>()))
            .ReturnsAsync(commitResult);

        // Act
        var result = await _syncManager.SyncCollectionChangesAsync();

        // Assert
        Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed));
        Assert.That(result.Success, Is.True);
        Assert.That(result.CollectionsDeleted, Is.EqualTo(1));
        Assert.That(result.DocumentsDeletedByCollectionDeletion, Is.EqualTo(2));
        Assert.That(result.DeletedCollectionNames, Contains.Item("test-collection"));
        Assert.That(result.CommitHash, Is.EqualTo("abc123"));

        // Verify cascade document deletion
        _mockDoltCli.Verify(x => x.QueryAsync<object>(
            "DELETE FROM documents WHERE collection_name = 'test-collection'"), Times.Once);
        
        // Verify collection deletion
        _mockDoltCli.Verify(x => x.QueryAsync<object>(
            "DELETE FROM collections WHERE collection_name = 'test-collection'"), Times.Once);
        
        // Verify commit
        _mockDoltCli.Verify(x => x.CommitAsync(
            It.Is<string>(msg => msg.Contains("Collection sync"))), Times.Once);
        
        // Verify tracking cleanup
        _mockDeletionTracker.Verify(x => x.MarkCollectionDeletionCommittedAsync(
            _doltConfig.RepositoryPath, "test-collection", "deletion"), Times.Once);
        _mockDeletionTracker.Verify(x => x.CleanupCommittedCollectionDeletionsAsync(
            _doltConfig.RepositoryPath), Times.Once);
    }

    [Test]
    public async Task SyncCollectionChangesAsync_WithCollectionRename_ShouldProcessRename()
    {
        // Arrange
        var renameRecord = new CollectionDeletionRecord
        {
            Id = "test-id",
            CollectionName = "old-collection",
            OperationType = "rename",
            OriginalName = "old-collection",
            NewName = "new-collection",
            RepoPath = _doltConfig.RepositoryPath
        };

        _mockDeletionTracker.Setup(x => x.GetPendingCollectionDeletionsAsync(_doltConfig.RepositoryPath))
            .ReturnsAsync(new List<CollectionDeletionRecord> { renameRecord });

        var commitResult = new CommitResult(true, "abc123", "Commit successful");
        _mockDoltCli.Setup(x => x.CommitAsync(It.IsAny<string>()))
            .ReturnsAsync(commitResult);

        // Act
        var result = await _syncManager.SyncCollectionChangesAsync();

        // Assert
        Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed));
        Assert.That(result.CollectionsRenamed, Is.EqualTo(1));
        Assert.That(result.RenamedCollectionNames, Contains.Item("old-collection -> new-collection"));

        // Verify collection table update
        _mockDoltCli.Verify(x => x.QueryAsync<object>(
            "UPDATE collections SET collection_name = 'new-collection' WHERE collection_name = 'old-collection'"), Times.Once);
        
        // Verify documents table update
        _mockDoltCli.Verify(x => x.QueryAsync<object>(
            "UPDATE documents SET collection_name = 'new-collection' WHERE collection_name = 'old-collection'"), Times.Once);
        
        // Verify tracking cleanup
        _mockDeletionTracker.Verify(x => x.MarkCollectionDeletionCommittedAsync(
            _doltConfig.RepositoryPath, "old-collection", "rename"), Times.Once);
    }

    [Test]
    public async Task SyncCollectionChangesAsync_WithMetadataUpdate_ShouldProcessUpdate()
    {
        // Arrange
        var updateRecord = new CollectionDeletionRecord
        {
            Id = "test-id",
            CollectionName = "test-collection",
            OperationType = "metadata_update",
            NewName = "{\"key1\": \"value1\", \"key2\": 42}",
            RepoPath = _doltConfig.RepositoryPath
        };

        _mockDeletionTracker.Setup(x => x.GetPendingCollectionDeletionsAsync(_doltConfig.RepositoryPath))
            .ReturnsAsync(new List<CollectionDeletionRecord> { updateRecord });

        var commitResult = new CommitResult(true, "abc123", "Commit successful");
        _mockDoltCli.Setup(x => x.CommitAsync(It.IsAny<string>()))
            .ReturnsAsync(commitResult);

        // Act
        var result = await _syncManager.SyncCollectionChangesAsync();

        // Assert
        Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed));
        Assert.That(result.CollectionsUpdated, Is.EqualTo(1));
        Assert.That(result.UpdatedCollectionNames, Contains.Item("test-collection"));

        // Verify metadata update
        _mockDoltCli.Verify(x => x.QueryAsync<object>(
            It.Is<string>(sql => sql.Contains("UPDATE collections SET metadata") && 
                                sql.Contains("test-collection"))), Times.Once);
        
        // Verify tracking cleanup
        _mockDeletionTracker.Verify(x => x.MarkCollectionDeletionCommittedAsync(
            _doltConfig.RepositoryPath, "test-collection", "metadata_update"), Times.Once);
    }

    [Test]
    public async Task SyncCollectionChangesAsync_WithMixedOperations_ShouldProcessAllCorrectly()
    {
        // Arrange
        var operations = new List<CollectionDeletionRecord>
        {
            new()
            {
                Id = "del-id",
                CollectionName = "delete-me",
                OperationType = "deletion",
                RepoPath = _doltConfig.RepositoryPath
            },
            new()
            {
                Id = "ren-id", 
                CollectionName = "rename-me",
                OperationType = "rename",
                OriginalName = "rename-me",
                NewName = "renamed",
                RepoPath = _doltConfig.RepositoryPath
            },
            new()
            {
                Id = "upd-id",
                CollectionName = "update-me",
                OperationType = "metadata_update",
                NewName = "{\"updated\": true}",
                RepoPath = _doltConfig.RepositoryPath
            }
        };

        _mockDeletionTracker.Setup(x => x.GetPendingCollectionDeletionsAsync(_doltConfig.RepositoryPath))
            .ReturnsAsync(operations);

        // Mock empty documents for deletion
        _mockDoltCli.Setup(x => x.QueryAsync<Dictionary<string, object>>(
            It.Is<string>(sql => sql.Contains("SELECT doc_id FROM documents"))))
            .ReturnsAsync(new List<Dictionary<string, object>>());

        var commitResult = new CommitResult(true, "abc123", "Commit successful");
        _mockDoltCli.Setup(x => x.CommitAsync(It.IsAny<string>()))
            .ReturnsAsync(commitResult);

        // Act
        var result = await _syncManager.SyncCollectionChangesAsync();

        // Assert
        Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed));
        Assert.That(result.CollectionsDeleted, Is.EqualTo(1));
        Assert.That(result.CollectionsRenamed, Is.EqualTo(1));
        Assert.That(result.CollectionsUpdated, Is.EqualTo(1));
        Assert.That(result.TotalCollectionChanges, Is.EqualTo(3));

        // Verify all operations were called
        _mockDoltCli.Verify(x => x.QueryAsync<object>(
            "DELETE FROM collections WHERE collection_name = 'delete-me'"), Times.Once);
        _mockDoltCli.Verify(x => x.QueryAsync<object>(
            "UPDATE collections SET collection_name = 'renamed' WHERE collection_name = 'rename-me'"), Times.Once);
        _mockDoltCli.Verify(x => x.QueryAsync<object>(
            It.Is<string>(sql => sql.Contains("UPDATE collections SET metadata") && 
                                sql.Contains("update-me"))), Times.Once);
    }

    [Test]
    public async Task StageCollectionChangesAsync_WithPendingChanges_ShouldStageCorrectly()
    {
        // Arrange
        var operations = new List<CollectionDeletionRecord>
        {
            new() { OperationType = "deletion", CollectionName = "del-collection" },
            new() { OperationType = "rename", CollectionName = "ren-collection", OriginalName = "old", NewName = "new" },
            new() { OperationType = "metadata_update", CollectionName = "upd-collection" }
        };

        _mockDeletionTracker.Setup(x => x.GetPendingCollectionDeletionsAsync(_doltConfig.RepositoryPath))
            .ReturnsAsync(operations);

        // Act
        var result = await _syncManager.StageCollectionChangesAsync();

        // Assert
        Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed));
        Assert.That(result.CollectionsDeleted, Is.EqualTo(1));
        Assert.That(result.CollectionsRenamed, Is.EqualTo(1));
        Assert.That(result.CollectionsUpdated, Is.EqualTo(1));

        // Verify staging operations
        _mockDoltCli.Verify(x => x.AddAsync("collections"), Times.Once);
        _mockDoltCli.Verify(x => x.AddAsync("documents"), Times.Once);

        // Verify collection names are populated correctly
        Assert.That(result.DeletedCollectionNames, Contains.Item("del-collection"));
        Assert.That(result.RenamedCollectionNames, Contains.Item("old -> new"));
        Assert.That(result.UpdatedCollectionNames, Contains.Item("upd-collection"));
    }

    [Test]
    public async Task SyncCollectionChangesAsync_WhenDeletionFails_ShouldReturnError()
    {
        // Arrange
        var deletionRecord = new CollectionDeletionRecord
        {
            CollectionName = "test-collection",
            OperationType = "deletion",
            RepoPath = _doltConfig.RepositoryPath
        };

        _mockDeletionTracker.Setup(x => x.GetPendingCollectionDeletionsAsync(_doltConfig.RepositoryPath))
            .ReturnsAsync(new List<CollectionDeletionRecord> { deletionRecord });

        _mockDoltCli.Setup(x => x.QueryAsync<Dictionary<string, object>>(It.IsAny<string>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _syncManager.SyncCollectionChangesAsync();

        // Assert
        Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Failed));
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Failed to process collection deletion"));

        // Verify no commit was attempted
        _mockDoltCli.Verify(x => x.CommitAsync(It.IsAny<string>()), Times.Never);
        
        // Verify no cleanup was attempted
        _mockDeletionTracker.Verify(x => x.CleanupCommittedCollectionDeletionsAsync(It.IsAny<string>()), Times.Never);
    }
}