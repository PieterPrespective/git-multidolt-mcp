using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Embranch.UnitTests
{
    /// <summary>
    /// Basic unit tests for SqliteDeletionTracker service
    /// Tests core deletion tracking functionality
    /// </summary>
    [TestFixture]
    public class DeletionTrackerBasicTests
    {
        private string _tempDataPath;
        private string _testRepoPath;
        private SqliteDeletionTracker _tracker;

        [SetUp]
        public async Task Setup()
        {
            _tempDataPath = Path.Combine(Path.GetTempPath(), $"deletion_test_{Guid.NewGuid():N}");
            _testRepoPath = Path.Combine(Path.GetTempPath(), $"repo_test_{Guid.NewGuid():N}");
            
            Directory.CreateDirectory(_tempDataPath);
            Directory.CreateDirectory(_testRepoPath);
            
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<SqliteDeletionTracker>();
            
            var serverConfig = new ServerConfiguration { DataPath = _tempDataPath };
            
            _tracker = new SqliteDeletionTracker(logger, serverConfig);
            
            // Initialize the deletion tracker database schema - CRITICAL for proper SqliteDeletionTracker operation
            await _tracker.InitializeAsync(_testRepoPath);
        }

        [Test]
        public async Task CanInitializeDeletionTracker()
        {
            // Act
            await _tracker.InitializeAsync(_testRepoPath);
            
            // Assert
            var dbPath = Path.Combine(_tempDataPath, "dev", "deletion_tracking.db");
            Assert.That(File.Exists(dbPath), Is.True, "Database file should be created");
        }

        [Test]
        public async Task CanTrackAndRetrieveDeletion()
        {
            // Arrange
            await _tracker.InitializeAsync(_testRepoPath);
            
            var docId = "test_doc_123";
            var collectionName = "test_collection";
            var contentHash = "abc123hash";
            var originalMetadata = new Dictionary<string, object>
            {
                ["title"] = "Test Document"
            };
            var branchContext = "main";
            var baseCommitHash = "commit_abc123";
            
            // Act - Track deletion
            await _tracker.TrackDeletionAsync(
                _testRepoPath, 
                docId, 
                collectionName, 
                contentHash, 
                originalMetadata, 
                branchContext, 
                baseCommitHash
            );
            
            // Assert - Retrieve and verify
            var pendingDeletions = await _tracker.GetPendingDeletionsAsync(_testRepoPath, collectionName);
            
            Assert.That(pendingDeletions, Has.Count.EqualTo(1));
            
            var deletion = pendingDeletions[0];
            Assert.That(deletion.DocId, Is.EqualTo(docId));
            Assert.That(deletion.CollectionName, Is.EqualTo(collectionName));
            Assert.That(deletion.RepoPath, Is.EqualTo(_testRepoPath));
            Assert.That(deletion.OriginalContentHash, Is.EqualTo(contentHash));
            Assert.That(deletion.BranchContext, Is.EqualTo(branchContext));
            Assert.That(deletion.BaseCommitHash, Is.EqualTo(baseCommitHash));
            Assert.That(deletion.DeletionSource, Is.EqualTo("mcp_tool"));
            Assert.That(deletion.SyncStatus, Is.EqualTo("pending"));
        }

        [Test]
        public async Task CanMarkDeletionAsStaged()
        {
            // Arrange
            await _tracker.InitializeAsync(_testRepoPath);
            
            var docId = "test_doc";
            var collectionName = "test_collection";
            await _tracker.TrackDeletionAsync(_testRepoPath, docId, collectionName, "hash", 
                new Dictionary<string, object>(), "main", "commit");
            
            // Verify initially pending
            var pendingBefore = await _tracker.GetPendingDeletionsAsync(_testRepoPath, collectionName);
            Assert.That(pendingBefore, Has.Count.EqualTo(1));
            Assert.That(pendingBefore[0].SyncStatus, Is.EqualTo("pending"));
            
            // Act
            await _tracker.MarkDeletionStagedAsync(_testRepoPath, docId, collectionName);
            
            // Assert
            var pendingAfter = await _tracker.GetPendingDeletionsAsync(_testRepoPath, collectionName);
            Assert.That(pendingAfter, Has.Count.EqualTo(0), "Should no longer be in pending list");
        }

        [Test]
        public async Task CanCheckIfDeletionExists()
        {
            // Arrange
            await _tracker.InitializeAsync(_testRepoPath);
            
            var docId = "test_doc";
            var collectionName = "test_collection";
            
            // Initially should not exist
            var existsBefore = await _tracker.HasPendingDeletionAsync(_testRepoPath, docId, collectionName);
            Assert.That(existsBefore, Is.False);
            
            // Track deletion
            await _tracker.TrackDeletionAsync(_testRepoPath, docId, collectionName, "hash", 
                new Dictionary<string, object>(), "main", "commit");
            
            // Should now exist
            var existsAfter = await _tracker.HasPendingDeletionAsync(_testRepoPath, docId, collectionName);
            Assert.That(existsAfter, Is.True);
        }

        [Test]
        public async Task CanRemoveDeletionTracking()
        {
            // Arrange
            await _tracker.InitializeAsync(_testRepoPath);
            
            await _tracker.TrackDeletionAsync(_testRepoPath, "doc1", "test_collection", "hash1", 
                new Dictionary<string, object>(), "main", "commit");
            await _tracker.TrackDeletionAsync(_testRepoPath, "doc2", "test_collection", "hash2", 
                new Dictionary<string, object>(), "main", "commit");
            
            // Verify both deletions exist
            var deletionsBefore = await _tracker.GetPendingDeletionsAsync(_testRepoPath, "test_collection");
            Assert.That(deletionsBefore, Has.Count.EqualTo(2));
            
            // Act
            await _tracker.RemoveDeletionTrackingAsync(_testRepoPath, "doc1", "test_collection");
            
            // Assert
            var deletionsAfter = await _tracker.GetPendingDeletionsAsync(_testRepoPath, "test_collection");
            Assert.That(deletionsAfter, Has.Count.EqualTo(1));
            Assert.That(deletionsAfter[0].DocId, Is.EqualTo("doc2"));
        }

        [TearDown]
        public void TearDown()
        {
            _tracker?.Dispose();
            
            try
            {
                if (Directory.Exists(_tempDataPath))
                    Directory.Delete(_tempDataPath, recursive: true);
                if (Directory.Exists(_testRepoPath))
                    Directory.Delete(_testRepoPath, recursive: true);
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Warning: Failed to clean up test directories: {ex.Message}");
            }
        }
    }
}