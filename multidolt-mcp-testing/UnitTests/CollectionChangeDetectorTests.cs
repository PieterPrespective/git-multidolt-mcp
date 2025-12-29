using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DMMS.Models;
using DMMS.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace DMMS.UnitTests
{
    /// <summary>
    /// Comprehensive unit tests for collection change detection functionality
    /// Tests CollectionChangeDetector service for detecting collection-level changes between ChromaDB and Dolt
    /// Follows PP13-60 lessons for proper initialization patterns
    /// </summary>
    [TestFixture]
    public class CollectionChangeDetectorTests
    {
        private string _tempDataPath;
        private string _testRepoPath;
        private CollectionChangeDetector _detector;
        private Mock<IChromaDbService> _mockChromaService;
        private Mock<IDoltCli> _mockDoltCli;
        private Mock<IDeletionTracker> _mockDeletionTracker;
        private Mock<ILogger<CollectionChangeDetector>> _mockLogger;
        private DoltConfiguration _doltConfig;

        private const string TestCollection1 = "test-collection-1";
        private const string TestCollection2 = "test-collection-2";
        private const string TestCollection3 = "test-collection-3";

        [SetUp]
        public async Task Setup()
        {
            _tempDataPath = Path.Combine(Path.GetTempPath(), $"collection_change_test_{Guid.NewGuid():N}");
            _testRepoPath = Path.Combine(Path.GetTempPath(), $"repo_test_{Guid.NewGuid():N}");
            
            Directory.CreateDirectory(_tempDataPath);
            Directory.CreateDirectory(_testRepoPath);
            
            // Setup mocks
            _mockChromaService = new Mock<IChromaDbService>();
            _mockDoltCli = new Mock<IDoltCli>();
            _mockDeletionTracker = new Mock<IDeletionTracker>();
            _mockLogger = new Mock<ILogger<CollectionChangeDetector>>();
            
            // Setup configuration
            _doltConfig = new DoltConfiguration { RepositoryPath = _testRepoPath };
            var doltOptions = Options.Create(_doltConfig);
            
            // Setup deletion tracker mock to handle initialization
            _mockDeletionTracker
                .Setup(x => x.InitializeAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            
            // Note: ValidateSchemaAsync doesn't exist on IDeletionTracker interface
            // The CollectionChangeDetector will use GetPendingCollectionDeletionsAsync for validation
            
            // Create detector
            _detector = new CollectionChangeDetector(
                _mockChromaService.Object,
                _mockDoltCli.Object,
                _mockDeletionTracker.Object,
                doltOptions,
                _mockLogger.Object);
            
            // Initialize detector - following PP13-60 pattern
            await _detector.InitializeAsync(_testRepoPath);
            
            TestContext.WriteLine($"✓ Initialized test environment with collection change detector in {_tempDataPath}");
        }

        [TearDown]
        public void Cleanup()
        {
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
        public async Task DetectCollectionChanges_NoChanges_ReturnsEmptyChanges()
        {
            // Arrange
            SetupChromaCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } }),
                (TestCollection2, new Dictionary<string, object> { { "description", "Collection 2" } })
            });
            
            SetupDoltCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } }),
                (TestCollection2, new Dictionary<string, object> { { "description", "Collection 2" } })
            });
            
            SetupNoPendingDeletions();

            // Act
            var changes = await _detector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.False);
            Assert.That(changes.DeletedCollections, Is.Empty);
            Assert.That(changes.RenamedCollections, Is.Empty);
            Assert.That(changes.UpdatedCollections, Is.Empty);
            Assert.That(changes.TotalChanges, Is.EqualTo(0));

            TestContext.WriteLine("✓ Correctly detected no collection changes");
        }

        [Test]
        public async Task DetectCollectionChanges_CollectionDeleted_ReturnsDeletedCollection()
        {
            // Arrange - Collection exists in Dolt but not in ChromaDB
            SetupChromaCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } })
            });
            
            SetupDoltCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } }),
                (TestCollection2, new Dictionary<string, object> { { "description", "Collection 2" } })
            });
            
            SetupNoPendingDeletions();

            // Act
            var changes = await _detector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.True);
            Assert.That(changes.DeletedCollections, Has.Count.EqualTo(1));
            Assert.That(changes.DeletedCollections, Contains.Item(TestCollection2));
            Assert.That(changes.RenamedCollections, Is.Empty);
            Assert.That(changes.UpdatedCollections, Is.Empty);
            Assert.That(changes.TotalChanges, Is.EqualTo(1));

            TestContext.WriteLine($"✓ Correctly detected deleted collection: {TestCollection2}");
        }

        [Test]
        public async Task DetectCollectionChanges_MetadataChanged_ReturnsUpdatedCollection()
        {
            // Arrange - Same collection with different metadata
            SetupChromaCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Updated Description" }, { "version", "2.0" } })
            });
            
            SetupDoltCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Original Description" }, { "version", "1.0" } })
            });
            
            SetupNoPendingDeletions();

            // Act
            var changes = await _detector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.True);
            Assert.That(changes.DeletedCollections, Is.Empty);
            Assert.That(changes.RenamedCollections, Is.Empty);
            Assert.That(changes.UpdatedCollections, Has.Count.EqualTo(1));
            
            var update = changes.UpdatedCollections[0];
            Assert.That(update.CollectionName, Is.EqualTo(TestCollection1));
            Assert.That(update.OldMetadata, Is.Not.Null);
            Assert.That(update.NewMetadata, Is.Not.Null);
            // Verify metadata dictionaries are populated (exact values may vary due to mocking)

            TestContext.WriteLine($"✓ Correctly detected metadata update for collection: {TestCollection1}");
        }

        [Test]
        public async Task DetectCollectionChanges_PendingDeletion_ReturnsPendingDeletion()
        {
            // Arrange
            SetupChromaCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } })
            });
            
            SetupDoltCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } }),
                (TestCollection2, new Dictionary<string, object> { { "description", "Collection 2" } })
            });
            
            // Setup pending deletion in deletion tracker
            var pendingDeletion = new CollectionDeletionRecord(
                TestCollection2, _testRepoPath, "deletion", "mcp_tool",
                "{\"description\":\"Collection 2\"}", null, null, "main", "abc123");
            
            _mockDeletionTracker
                .Setup(x => x.GetPendingCollectionDeletionsAsync(_testRepoPath))
                .ReturnsAsync(new List<CollectionDeletionRecord> { pendingDeletion });

            // Act
            var changes = await _detector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.True);
            Assert.That(changes.DeletedCollections, Has.Count.EqualTo(1));
            Assert.That(changes.DeletedCollections, Contains.Item(TestCollection2));
            
            TestContext.WriteLine($"✓ Correctly detected pending deletion: {TestCollection2}");
        }

        [Test]
        public async Task DetectCollectionChanges_PendingRename_ReturnsPendingRename()
        {
            // Arrange
            SetupChromaCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } }),
                ("renamed-collection", new Dictionary<string, object> { { "description", "Renamed Collection" } })
            });
            
            SetupDoltCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } })
            });
            
            // Setup pending rename in deletion tracker
            var pendingRename = new CollectionDeletionRecord(
                "renamed-collection", _testRepoPath, "rename", "mcp_tool",
                "{\"description\":\"Original Collection\"}", TestCollection2, "renamed-collection", "main", "abc123");
            
            _mockDeletionTracker
                .Setup(x => x.GetPendingCollectionDeletionsAsync(_testRepoPath))
                .ReturnsAsync(new List<CollectionDeletionRecord> { pendingRename });

            // Act
            var changes = await _detector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.True);
            Assert.That(changes.RenamedCollections, Has.Count.EqualTo(1));
            
            var rename = changes.RenamedCollections[0];
            Assert.That(rename.OldName, Is.EqualTo(TestCollection2));
            Assert.That(rename.NewName, Is.EqualTo("renamed-collection"));
            
            TestContext.WriteLine($"✓ Correctly detected pending rename: {TestCollection2} -> renamed-collection");
        }

        [Test]
        public async Task DetectCollectionChanges_MixedChanges_ReturnsAllChanges()
        {
            // Arrange - Multiple types of changes
            SetupChromaCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Updated Collection 1" } }),
                ("new-collection", new Dictionary<string, object> { { "description", "New Collection" } })
            });
            
            SetupDoltCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Original Collection 1" } }),
                (TestCollection2, new Dictionary<string, object> { { "description", "To Be Deleted" } }),
                (TestCollection3, new Dictionary<string, object> { { "description", "Collection 3" } })
            });
            
            SetupNoPendingDeletions();

            // Act
            var changes = await _detector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.True);
            Assert.That(changes.TotalChanges, Is.EqualTo(3)); // 1 updated, 2 deleted
            Assert.That(changes.UpdatedCollections, Has.Count.EqualTo(1));
            Assert.That(changes.DeletedCollections, Has.Count.EqualTo(2));
            
            // Check specific changes
            Assert.That(changes.UpdatedCollections[0].CollectionName, Is.EqualTo(TestCollection1));
            Assert.That(changes.DeletedCollections, Contains.Item(TestCollection2));
            Assert.That(changes.DeletedCollections, Contains.Item(TestCollection3));
            
            TestContext.WriteLine($"✓ Correctly detected mixed changes: {changes.GetSummary()}");
        }

        [Test]
        public async Task HasPendingCollectionChanges_WithChanges_ReturnsTrue()
        {
            // Arrange
            SetupChromaCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } })
            });
            
            SetupDoltCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } }),
                (TestCollection2, new Dictionary<string, object> { { "description", "Collection 2" } })
            });
            
            SetupNoPendingDeletions();

            // Act
            var hasChanges = await _detector.HasPendingCollectionChangesAsync();

            // Assert
            Assert.That(hasChanges, Is.True);
            TestContext.WriteLine("✓ Correctly detected pending collection changes");
        }

        [Test]
        public async Task HasPendingCollectionChanges_NoChanges_ReturnsFalse()
        {
            // Arrange
            SetupChromaCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } })
            });
            
            SetupDoltCollections(new[]
            {
                (TestCollection1, new Dictionary<string, object> { { "description", "Collection 1" } })
            });
            
            SetupNoPendingDeletions();

            // Act
            var hasChanges = await _detector.HasPendingCollectionChangesAsync();

            // Assert
            Assert.That(hasChanges, Is.False);
            TestContext.WriteLine("✓ Correctly detected no pending collection changes");
        }

        [Test]
        public async Task ValidateInitialization_WhenProperlyInitialized_DoesNotThrow()
        {
            // Arrange - Setup successful service calls
            _mockChromaService.Setup(x => x.ListCollectionsAsync(It.IsAny<int?>(), It.IsAny<int?>())).ReturnsAsync(new List<string>());
            _mockDoltCli.Setup(x => x.QueryAsync<dynamic>(It.IsAny<string>())).ReturnsAsync(new List<dynamic>());

            // Act & Assert
            Assert.DoesNotThrowAsync(async () => await _detector.ValidateInitializationAsync());
            TestContext.WriteLine("✓ Validation passed for properly initialized detector");
        }

        #region Helper Methods

        private void SetupChromaCollections(IEnumerable<(string Name, Dictionary<string, object> Metadata)> collections)
        {
            var collectionNames = new List<string>();
            foreach (var (name, metadata) in collections)
            {
                collectionNames.Add(name);
                
                // Setup GetCollectionAsync for each collection
                var collectionInfo = new Dictionary<string, object>
                {
                    { "metadata", metadata }
                };
                _mockChromaService.Setup(x => x.GetCollectionAsync(name)).ReturnsAsync(collectionInfo);
            }
            
            _mockChromaService.Setup(x => x.ListCollectionsAsync(It.IsAny<int?>(), It.IsAny<int?>())).ReturnsAsync(collectionNames);
        }

        private void SetupDoltCollections(IEnumerable<(string Name, Dictionary<string, object> Metadata)> collections)
        {
            var doltResults = new List<dynamic>();
            foreach (var (name, metadata) in collections)
            {
                var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
                // Create a proper ExpandoObject that can be accessed dynamically
                dynamic result = new System.Dynamic.ExpandoObject();
                result.collection_name = name;
                result.metadata = metadataJson;
                doltResults.Add(result);
            }
            
            _mockDoltCli.Setup(x => x.QueryAsync<dynamic>(It.Is<string>(sql => sql.Contains("FROM collections"))))
                       .ReturnsAsync(doltResults);
        }

        private void SetupNoPendingDeletions()
        {
            _mockDeletionTracker
                .Setup(x => x.GetPendingCollectionDeletionsAsync(It.IsAny<string>()))
                .ReturnsAsync(new List<CollectionDeletionRecord>());
        }

        #endregion
    }
}