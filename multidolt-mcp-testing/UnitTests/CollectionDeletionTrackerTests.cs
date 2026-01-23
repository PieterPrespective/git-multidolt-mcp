using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Embranch.UnitTests
{
    /// <summary>
    /// Comprehensive unit tests for collection deletion tracking functionality
    /// Tests extension of SqliteDeletionTracker to handle collection-level operations
    /// Follows PP13-60 lessons for proper initialization patterns
    /// </summary>
    [TestFixture]
    public class CollectionDeletionTrackerTests
    {
        private string _tempDataPath;
        private string _testRepoPath;
        private SqliteDeletionTracker _tracker;
        private const string TestCollectionName = "test-collection";
        private const string TestCollectionNameRenamed = "test-collection-renamed";

        [SetUp]
        public async Task Setup()
        {
            _tempDataPath = Path.Combine(Path.GetTempPath(), $"collection_deletion_test_{Guid.NewGuid():N}");
            _testRepoPath = Path.Combine(Path.GetTempPath(), $"repo_test_{Guid.NewGuid():N}");
            
            Directory.CreateDirectory(_tempDataPath);
            Directory.CreateDirectory(_testRepoPath);
            
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<SqliteDeletionTracker>();
            
            var serverConfig = new ServerConfiguration { DataPath = _tempDataPath };
            
            _tracker = new SqliteDeletionTracker(logger, serverConfig);
            
            // Initialize tracker - following PP13-60 pattern
            await _tracker.InitializeAsync(_testRepoPath);
            
            TestContext.WriteLine($"✓ Initialized test environment with deletion tracker in {_tempDataPath}");
        }

        [TearDown]
        public void Cleanup()
        {
            _tracker?.Dispose();
            
            // Clean up temp directories
            try
            {
                if (Directory.Exists(_tempDataPath))
                    Directory.Delete(_tempDataPath, true);
                if (Directory.Exists(_testRepoPath))
                    Directory.Delete(_testRepoPath, true);
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Cleanup warning: {ex.Message}");
            }
        }

        [Test]
        public async Task CanTrackCollectionDeletion()
        {
            // Arrange
            var originalMetadata = new Dictionary<string, object>
            {
                { "description", "Test collection" },
                { "embedding_model", "default" },
                { "document_count", 42 }
            };
            var branchContext = "main";
            var baseCommitHash = "abc123";

            // Act
            await _tracker.TrackCollectionDeletionAsync(_testRepoPath, TestCollectionName, 
                originalMetadata, branchContext, baseCommitHash);

            // Assert
            var pendingDeletions = await _tracker.GetPendingCollectionDeletionsAsync(_testRepoPath);
            
            Assert.That(pendingDeletions, Is.Not.Null);
            Assert.That(pendingDeletions.Count, Is.EqualTo(1));
            
            var deletion = pendingDeletions.First();
            Assert.That(deletion.CollectionName, Is.EqualTo(TestCollectionName));
            Assert.That(deletion.OperationType, Is.EqualTo("deletion"));
            Assert.That(deletion.RepoPath, Is.EqualTo(_testRepoPath));
            Assert.That(deletion.DeletionSource, Is.EqualTo("mcp_tool"));
            Assert.That(deletion.BranchContext, Is.EqualTo(branchContext));
            Assert.That(deletion.BaseCommitHash, Is.EqualTo(baseCommitHash));
            Assert.That(deletion.SyncStatus, Is.EqualTo("pending"));
            Assert.That(deletion.OriginalMetadata, Is.Not.Null);
            Assert.That(deletion.OriginalMetadata, Does.Contain("Test collection"));

            TestContext.WriteLine($"✓ Successfully tracked collection deletion: {deletion.Id}");
        }

        [Test]
        public async Task CanTrackCollectionRename()
        {
            // Arrange
            var originalMetadata = new Dictionary<string, object>
            {
                { "description", "Original collection" },
                { "embedding_model", "default" }
            };
            var newMetadata = new Dictionary<string, object>
            {
                { "description", "Renamed collection" },
                { "embedding_model", "default" }
            };
            var branchContext = "feature-branch";
            var baseCommitHash = "def456";

            // Act
            await _tracker.TrackCollectionUpdateAsync(_testRepoPath, TestCollectionName, TestCollectionNameRenamed,
                originalMetadata, newMetadata, branchContext, baseCommitHash);

            // Assert
            var pendingDeletions = await _tracker.GetPendingCollectionDeletionsAsync(_testRepoPath);
            
            Assert.That(pendingDeletions, Is.Not.Null);
            Assert.That(pendingDeletions.Count, Is.EqualTo(1));
            
            var deletion = pendingDeletions.First();
            Assert.That(deletion.CollectionName, Is.EqualTo(TestCollectionName)); // CollectionName stores original name for tracking purposes
            Assert.That(deletion.OperationType, Is.EqualTo("rename"));
            Assert.That(deletion.OriginalName, Is.EqualTo(TestCollectionName));
            Assert.That(deletion.NewName, Is.EqualTo(TestCollectionNameRenamed));
            Assert.That(deletion.RepoPath, Is.EqualTo(_testRepoPath));
            Assert.That(deletion.DeletionSource, Is.EqualTo("mcp_tool"));
            Assert.That(deletion.BranchContext, Is.EqualTo(branchContext));
            Assert.That(deletion.BaseCommitHash, Is.EqualTo(baseCommitHash));
            Assert.That(deletion.SyncStatus, Is.EqualTo("pending"));

            TestContext.WriteLine($"✓ Successfully tracked collection rename: {TestCollectionName} -> {TestCollectionNameRenamed}");
        }

        [Test]
        public async Task CanTrackCollectionMetadataUpdate()
        {
            // Arrange
            var originalMetadata = new Dictionary<string, object>
            {
                { "description", "Original description" },
                { "embedding_model", "default" }
            };
            var newMetadata = new Dictionary<string, object>
            {
                { "description", "Updated description" },
                { "embedding_model", "cohere" }
            };
            var branchContext = "main";
            var baseCommitHash = "ghi789";

            // Act - same collection name = metadata update
            await _tracker.TrackCollectionUpdateAsync(_testRepoPath, TestCollectionName, TestCollectionName,
                originalMetadata, newMetadata, branchContext, baseCommitHash);

            // Assert
            var pendingDeletions = await _tracker.GetPendingCollectionDeletionsAsync(_testRepoPath);
            
            Assert.That(pendingDeletions, Is.Not.Null);
            Assert.That(pendingDeletions.Count, Is.EqualTo(1));
            
            var deletion = pendingDeletions.First();
            Assert.That(deletion.CollectionName, Is.EqualTo(TestCollectionName));
            Assert.That(deletion.OperationType, Is.EqualTo("metadata_update"));
            Assert.That(deletion.OriginalName, Is.EqualTo(TestCollectionName));
            Assert.That(deletion.NewName, Is.EqualTo(TestCollectionName));
            Assert.That(deletion.SyncStatus, Is.EqualTo("pending"));

            TestContext.WriteLine($"✓ Successfully tracked collection metadata update for: {TestCollectionName}");
        }

        [Test]
        public async Task CanHandleMultipleCollectionOperations()
        {
            // Arrange
            var metadata1 = new Dictionary<string, object> { { "description", "Collection 1" } };
            var metadata2 = new Dictionary<string, object> { { "description", "Collection 2" } };
            var metadata3 = new Dictionary<string, object> { { "description", "Collection 3" } };
            var branchContext = "main";
            var baseCommitHash = "multi123";

            // Act - Track multiple different operations
            await _tracker.TrackCollectionDeletionAsync(_testRepoPath, "collection-1", 
                metadata1, branchContext, baseCommitHash);
            
            await _tracker.TrackCollectionUpdateAsync(_testRepoPath, "collection-2", "collection-2-renamed",
                metadata2, metadata2, branchContext, baseCommitHash);
            
            await _tracker.TrackCollectionUpdateAsync(_testRepoPath, "collection-3", "collection-3",
                metadata3, metadata3, branchContext, baseCommitHash);

            // Assert
            var pendingDeletions = await _tracker.GetPendingCollectionDeletionsAsync(_testRepoPath);
            
            Assert.That(pendingDeletions, Is.Not.Null);
            Assert.That(pendingDeletions.Count, Is.EqualTo(3));
            
            // Verify all operations are tracked
            var operations = pendingDeletions.Select(d => new { d.CollectionName, d.OperationType }).ToList();
            
            Assert.That(operations, Does.Contain(new { CollectionName = "collection-1", OperationType = "deletion" }));
            Assert.That(operations, Does.Contain(new { CollectionName = "collection-2", OperationType = "rename" })); // CollectionName stores original name
            Assert.That(operations, Does.Contain(new { CollectionName = "collection-3", OperationType = "metadata_update" }));
            
            // Additionally verify the rename operation has correct new name
            var renameOperation = pendingDeletions.FirstOrDefault(d => d.OperationType == "rename");
            Assert.That(renameOperation.OperationType, Is.EqualTo("rename"), "Should have a rename operation");
            Assert.That(renameOperation.OriginalName, Is.EqualTo("collection-2"));
            Assert.That(renameOperation.NewName, Is.EqualTo("collection-2-renamed"));

            TestContext.WriteLine($"✓ Successfully tracked {pendingDeletions.Count} collection operations");
        }

        [Test]
        public async Task CanMarkCollectionDeletionAsCommitted()
        {
            // Arrange
            var originalMetadata = new Dictionary<string, object> { { "description", "To be committed" } };
            await _tracker.TrackCollectionDeletionAsync(_testRepoPath, TestCollectionName, 
                originalMetadata, "main", "commit123");

            // Verify it's pending
            var pendingBefore = await _tracker.GetPendingCollectionDeletionsAsync(_testRepoPath);
            Assert.That(pendingBefore.Count, Is.EqualTo(1));

            // Act
            await _tracker.MarkCollectionDeletionCommittedAsync(_testRepoPath, TestCollectionName, "deletion");

            // Assert
            var pendingAfter = await _tracker.GetPendingCollectionDeletionsAsync(_testRepoPath);
            Assert.That(pendingAfter.Count, Is.EqualTo(0), "Should have no pending deletions after marking as committed");

            TestContext.WriteLine($"✓ Successfully marked collection deletion as committed");
        }

        [Test]
        public async Task CanCleanupCommittedCollectionDeletions()
        {
            // Arrange - Track multiple operations and mark some as committed
            var metadata = new Dictionary<string, object> { { "description", "Test cleanup" } };
            
            await _tracker.TrackCollectionDeletionAsync(_testRepoPath, "collection-1", metadata, "main", "cleanup123");
            await _tracker.TrackCollectionDeletionAsync(_testRepoPath, "collection-2", metadata, "main", "cleanup123");
            await _tracker.TrackCollectionUpdateAsync(_testRepoPath, "collection-3", "collection-3-new", metadata, metadata, "main", "cleanup123");

            // Mark two as committed
            await _tracker.MarkCollectionDeletionCommittedAsync(_testRepoPath, "collection-1", "deletion");
            await _tracker.MarkCollectionDeletionCommittedAsync(_testRepoPath, "collection-3", "rename"); // Use original name, not new name

            // Verify before cleanup - should still have 1 pending
            var pendingBefore = await _tracker.GetPendingCollectionDeletionsAsync(_testRepoPath);
            Assert.That(pendingBefore.Count, Is.EqualTo(1));
            Assert.That(pendingBefore.First().CollectionName, Is.EqualTo("collection-2"));

            // Act
            await _tracker.CleanupCommittedCollectionDeletionsAsync(_testRepoPath);

            // Assert
            var pendingAfter = await _tracker.GetPendingCollectionDeletionsAsync(_testRepoPath);
            Assert.That(pendingAfter.Count, Is.EqualTo(1), "Should still have 1 pending after cleanup");
            Assert.That(pendingAfter.First().CollectionName, Is.EqualTo("collection-2"));

            TestContext.WriteLine($"✓ Successfully cleaned up committed collection deletions, {pendingAfter.Count} pending remain");
        }

        [Test]
        public async Task GetPendingCollectionDeletionsReturnsEmptyForNewRepo()
        {
            // Arrange
            var cleanRepoPath = Path.Combine(Path.GetTempPath(), $"clean_repo_{Guid.NewGuid():N}");

            // Act
            var pending = await _tracker.GetPendingCollectionDeletionsAsync(cleanRepoPath);

            // Assert
            Assert.That(pending, Is.Not.Null);
            Assert.That(pending.Count, Is.EqualTo(0));

            TestContext.WriteLine($"✓ Correctly returned empty list for new repository");
        }

        [Test]
        public async Task CollectionDeletionTrackingDoesNotAffectDocumentTracking()
        {
            // Arrange - Track both document and collection deletions
            var collectionMetadata = new Dictionary<string, object> { { "description", "Test collection" } };
            var docMetadata = new Dictionary<string, object> { { "title", "Test document" } };
            
            // Act - Track collection deletion
            await _tracker.TrackCollectionDeletionAsync(_testRepoPath, TestCollectionName, 
                collectionMetadata, "main", "mixed123");
            
            // Track document deletion
            await _tracker.TrackDeletionAsync(_testRepoPath, "doc-1", TestCollectionName, 
                "hash123", docMetadata, "main", "mixed123");

            // Assert - Both should be tracked independently
            var pendingCollectionDeletions = await _tracker.GetPendingCollectionDeletionsAsync(_testRepoPath);
            var pendingDocumentDeletions = await _tracker.GetPendingDeletionsAsync(_testRepoPath, TestCollectionName);
            
            Assert.That(pendingCollectionDeletions.Count, Is.EqualTo(1));
            Assert.That(pendingDocumentDeletions.Count, Is.EqualTo(1));
            
            Assert.That(pendingCollectionDeletions.First().CollectionName, Is.EqualTo(TestCollectionName));
            Assert.That(pendingDocumentDeletions.First().DocId, Is.EqualTo("doc-1"));

            TestContext.WriteLine($"✓ Collection and document deletion tracking work independently");
        }

        [Test]
        public async Task CanHandleNullOptionalFields()
        {
            // Arrange
            var metadata = new Dictionary<string, object> { { "description", "Minimal test" } };

            // Act - Track with minimal required fields only
            await _tracker.TrackCollectionDeletionAsync(_testRepoPath, TestCollectionName, 
                metadata, null, null);

            // Assert
            var pendingDeletions = await _tracker.GetPendingCollectionDeletionsAsync(_testRepoPath);
            
            Assert.That(pendingDeletions.Count, Is.EqualTo(1));
            
            var deletion = pendingDeletions.First();
            Assert.That(deletion.CollectionName, Is.EqualTo(TestCollectionName));
            Assert.That(deletion.BranchContext, Is.Null);
            Assert.That(deletion.BaseCommitHash, Is.Null);
            Assert.That(deletion.SyncStatus, Is.EqualTo("pending"));

            TestContext.WriteLine($"✓ Successfully handled null optional fields");
        }

        [Test]
        public void CollectionDeletionRecordConstructorSetsCorrectDefaults()
        {
            // Act
            var record = new CollectionDeletionRecord("test-collection", "/repo/path", "deletion", "mcp_tool");

            // Assert
            Assert.That(record.Id, Is.Not.Null.And.Not.Empty);
            Assert.That(record.CollectionName, Is.EqualTo("test-collection"));
            Assert.That(record.RepoPath, Is.EqualTo("/repo/path"));
            Assert.That(record.OperationType, Is.EqualTo("deletion"));
            Assert.That(record.DeletionSource, Is.EqualTo("mcp_tool"));
            Assert.That(record.SyncStatus, Is.EqualTo("pending"));
            Assert.That(record.DeletedAt, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
            Assert.That(record.CreatedAt, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));

            TestContext.WriteLine($"✓ CollectionDeletionRecord constructor sets correct defaults");
        }
    }
}