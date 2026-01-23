using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;

namespace Embranch.UnitTests
{
    /// <summary>
    /// Unit tests for ISyncStateTracker functionality in SqliteDeletionTracker
    /// Tests sync state CRUD operations, branch isolation, and database operations
    /// </summary>
    [TestFixture]
    public class SyncStateTrackerTests
    {
        private string _tempDataPath;
        private string _testRepoPath;
        private SqliteDeletionTracker _tracker;
        private ILogger<SqliteDeletionTracker> _logger;

        [SetUp]
        public async Task Setup()
        {
            _tempDataPath = Path.Combine(Path.GetTempPath(), $"syncstate_test_{Guid.NewGuid():N}");
            _testRepoPath = Path.Combine(Path.GetTempPath(), $"repo_test_{Guid.NewGuid():N}");
            
            Directory.CreateDirectory(_tempDataPath);
            Directory.CreateDirectory(_testRepoPath);
            
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<SqliteDeletionTracker>();
            
            var serverConfig = new ServerConfiguration { DataPath = _tempDataPath };
            
            _tracker = new SqliteDeletionTracker(_logger, serverConfig);
            
            // Initialize the tracker database schema
            await _tracker.InitializeAsync(_testRepoPath);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                _tracker?.Dispose();
                
                // Wait a short time to ensure files are released
                System.Threading.Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Error disposing tracker: {ex.Message}");
            }
            
            try
            {
                if (Directory.Exists(_tempDataPath))
                {
                    Directory.Delete(_tempDataPath, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not clean up temp data path: {ex.Message}");
            }
            
            try
            {
                if (Directory.Exists(_testRepoPath))
                {
                    Directory.Delete(_testRepoPath, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not clean up test repo path: {ex.Message}");
            }
        }

        /// <summary>
        /// Tests that sync state tracker can be initialized without errors
        /// </summary>
        [Test]
        public async Task CanInitializeSyncStateTracker()
        {
            // Act & Assert - Should not throw
            await _tracker.InitializeAsync(_testRepoPath);
            
            // Verify we can perform basic operations
            var allStates = await _tracker.GetAllSyncStatesAsync(_testRepoPath);
            Assert.That(allStates, Is.Not.Null);
            Assert.That(allStates.Count, Is.EqualTo(0));
        }

        /// <summary>
        /// Tests creating and retrieving a sync state record
        /// </summary>
        [Test]
        public async Task CanCreateAndRetrieveSyncState()
        {
            // Arrange
            var collectionName = "test-collection";
            var branchContext = "main";
            var syncState = new SyncStateRecord(_testRepoPath, collectionName, branchContext)
            {
                LastSyncCommit = "abc123",
                DocumentCount = 5,
                ChunkCount = 10,
                EmbeddingModel = "default",
                SyncStatus = "synced"
            };

            // Act
            await _tracker.UpdateSyncStateAsync(_testRepoPath, collectionName, syncState);
            var retrieved = await _tracker.GetSyncStateAsync(_testRepoPath, collectionName, branchContext);

            // Assert
            Assert.That(retrieved, Is.Not.Null);
            Assert.That(retrieved.Value.CollectionName, Is.EqualTo(collectionName));
            Assert.That(retrieved.Value.BranchContext, Is.EqualTo(branchContext));
            Assert.That(retrieved.Value.LastSyncCommit, Is.EqualTo("abc123"));
            Assert.That(retrieved.Value.DocumentCount, Is.EqualTo(5));
            Assert.That(retrieved.Value.ChunkCount, Is.EqualTo(10));
            Assert.That(retrieved.Value.EmbeddingModel, Is.EqualTo("default"));
            Assert.That(retrieved.Value.SyncStatus, Is.EqualTo("synced"));
        }

        /// <summary>
        /// Tests updating an existing sync state record
        /// </summary>
        [Test]
        public async Task CanUpdateExistingSyncState()
        {
            // Arrange
            var collectionName = "test-collection";
            var branchContext = "main";
            var originalState = new SyncStateRecord(_testRepoPath, collectionName, branchContext)
            {
                LastSyncCommit = "abc123",
                DocumentCount = 5,
                SyncStatus = "synced"
            };

            await _tracker.UpdateSyncStateAsync(_testRepoPath, collectionName, originalState);

            // Act
            var updatedState = originalState.WithSyncUpdate("def456", 10, 20, "openai");
            await _tracker.UpdateSyncStateAsync(_testRepoPath, collectionName, updatedState);
            var retrieved = await _tracker.GetSyncStateAsync(_testRepoPath, collectionName, branchContext);

            // Assert
            Assert.That(retrieved, Is.Not.Null);
            Assert.That(retrieved.Value.LastSyncCommit, Is.EqualTo("def456"));
            Assert.That(retrieved.Value.DocumentCount, Is.EqualTo(10));
            Assert.That(retrieved.Value.ChunkCount, Is.EqualTo(20));
            Assert.That(retrieved.Value.EmbeddingModel, Is.EqualTo("openai"));
            Assert.That(retrieved.Value.SyncStatus, Is.EqualTo("synced"));
        }

        /// <summary>
        /// Tests branch isolation - sync states should be isolated per branch
        /// </summary>
        [Test]
        public async Task SyncStateIsolatedByBranch()
        {
            // Arrange
            var collectionName = "test-collection";
            var mainBranch = "main";
            var featureBranch = "feature-branch";

            var mainState = new SyncStateRecord(_testRepoPath, collectionName, mainBranch)
            {
                LastSyncCommit = "main123",
                DocumentCount = 5,
                SyncStatus = "synced"
            };

            var featureState = new SyncStateRecord(_testRepoPath, collectionName, featureBranch)
            {
                LastSyncCommit = "feature456",
                DocumentCount = 3,
                SyncStatus = "pending"
            };

            // Act
            await _tracker.UpdateSyncStateAsync(_testRepoPath, collectionName, mainState);
            await _tracker.UpdateSyncStateAsync(_testRepoPath, collectionName, featureState);

            var retrievedMain = await _tracker.GetSyncStateAsync(_testRepoPath, collectionName, mainBranch);
            var retrievedFeature = await _tracker.GetSyncStateAsync(_testRepoPath, collectionName, featureBranch);

            // Assert
            Assert.That(retrievedMain, Is.Not.Null);
            Assert.That(retrievedFeature, Is.Not.Null);
            
            Assert.That(retrievedMain.Value.LastSyncCommit, Is.EqualTo("main123"));
            Assert.That(retrievedMain.Value.DocumentCount, Is.EqualTo(5));
            Assert.That(retrievedMain.Value.SyncStatus, Is.EqualTo("synced"));
            
            Assert.That(retrievedFeature.Value.LastSyncCommit, Is.EqualTo("feature456"));
            Assert.That(retrievedFeature.Value.DocumentCount, Is.EqualTo(3));
            Assert.That(retrievedFeature.Value.SyncStatus, Is.EqualTo("pending"));
        }

        /// <summary>
        /// Tests retrieving all sync states for a repository
        /// </summary>
        [Test]
        public async Task CanGetAllSyncStatesForRepository()
        {
            // Arrange
            var states = new[]
            {
                new SyncStateRecord(_testRepoPath, "collection1", "main") { DocumentCount = 5 },
                new SyncStateRecord(_testRepoPath, "collection2", "main") { DocumentCount = 3 },
                new SyncStateRecord(_testRepoPath, "collection1", "feature") { DocumentCount = 7 }
            };

            foreach (var state in states)
            {
                await _tracker.UpdateSyncStateAsync(_testRepoPath, state.CollectionName, state);
            }

            // Act
            var allStates = await _tracker.GetAllSyncStatesAsync(_testRepoPath);

            // Assert
            Assert.That(allStates, Is.Not.Null);
            Assert.That(allStates.Count, Is.EqualTo(3));
            
            var collection1Main = allStates.Find(s => s.CollectionName == "collection1" && s.BranchContext == "main");
            var collection2Main = allStates.Find(s => s.CollectionName == "collection2" && s.BranchContext == "main");
            var collection1Feature = allStates.Find(s => s.CollectionName == "collection1" && s.BranchContext == "feature");

            Assert.That(collection1Main.DocumentCount, Is.EqualTo(5));
            Assert.That(collection2Main.DocumentCount, Is.EqualTo(3));
            Assert.That(collection1Feature.DocumentCount, Is.EqualTo(7));
        }

        /// <summary>
        /// Tests getting sync states for a specific branch
        /// </summary>
        [Test]
        public async Task CanGetSyncStatesForSpecificBranch()
        {
            // Arrange
            var states = new[]
            {
                new SyncStateRecord(_testRepoPath, "collection1", "main") { DocumentCount = 5 },
                new SyncStateRecord(_testRepoPath, "collection2", "main") { DocumentCount = 3 },
                new SyncStateRecord(_testRepoPath, "collection1", "feature") { DocumentCount = 7 }
            };

            foreach (var state in states)
            {
                await _tracker.UpdateSyncStateAsync(_testRepoPath, state.CollectionName, state);
            }

            // Act
            var mainStates = await _tracker.GetBranchSyncStatesAsync(_testRepoPath, "main");
            var featureStates = await _tracker.GetBranchSyncStatesAsync(_testRepoPath, "feature");

            // Assert
            Assert.That(mainStates, Is.Not.Null);
            Assert.That(mainStates.Count, Is.EqualTo(2));
            Assert.That(featureStates, Is.Not.Null);
            Assert.That(featureStates.Count, Is.EqualTo(1));

            Assert.That(mainStates.TrueForAll(s => s.BranchContext == "main"), Is.True);
            Assert.That(featureStates[0].BranchContext, Is.EqualTo("feature"));
        }

        /// <summary>
        /// Tests clearing sync states for a specific branch
        /// </summary>
        [Test]
        public async Task CanClearBranchSyncStates()
        {
            // Arrange
            var states = new[]
            {
                new SyncStateRecord(_testRepoPath, "collection1", "main") { DocumentCount = 5 },
                new SyncStateRecord(_testRepoPath, "collection2", "main") { DocumentCount = 3 },
                new SyncStateRecord(_testRepoPath, "collection1", "feature") { DocumentCount = 7 }
            };

            foreach (var state in states)
            {
                await _tracker.UpdateSyncStateAsync(_testRepoPath, state.CollectionName, state);
            }

            // Act
            await _tracker.ClearBranchSyncStatesAsync(_testRepoPath, "main");

            // Assert
            var mainStates = await _tracker.GetBranchSyncStatesAsync(_testRepoPath, "main");
            var featureStates = await _tracker.GetBranchSyncStatesAsync(_testRepoPath, "feature");

            Assert.That(mainStates.Count, Is.EqualTo(0));
            Assert.That(featureStates.Count, Is.EqualTo(1));
        }

        /// <summary>
        /// Tests deleting a specific sync state record
        /// </summary>
        [Test]
        public async Task CanDeleteSpecificSyncState()
        {
            // Arrange
            var collectionName = "test-collection";
            var branchContext = "main";
            var syncState = new SyncStateRecord(_testRepoPath, collectionName, branchContext);

            await _tracker.UpdateSyncStateAsync(_testRepoPath, collectionName, syncState);

            // Act
            await _tracker.DeleteSyncStateAsync(_testRepoPath, collectionName, branchContext);

            // Assert
            var retrieved = await _tracker.GetSyncStateAsync(_testRepoPath, collectionName, branchContext);
            Assert.That(retrieved, Is.Null);
        }

        /// <summary>
        /// Tests updating commit hash for a sync state
        /// </summary>
        [Test]
        public async Task CanUpdateCommitHash()
        {
            // Arrange
            var collectionName = "test-collection";
            var branchContext = "main";
            var syncState = new SyncStateRecord(_testRepoPath, collectionName, branchContext)
            {
                LastSyncCommit = "abc123"
            };

            await _tracker.UpdateSyncStateAsync(_testRepoPath, collectionName, syncState);

            // Act
            await _tracker.UpdateCommitHashAsync(_testRepoPath, collectionName, "def456", branchContext);

            // Assert
            var retrieved = await _tracker.GetSyncStateAsync(_testRepoPath, collectionName, branchContext);
            Assert.That(retrieved, Is.Not.Null);
            Assert.That(retrieved.Value.LastSyncCommit, Is.EqualTo("def456"));
        }

        /// <summary>
        /// Tests handling null branch context (default branch)
        /// </summary>
        [Test]
        public async Task CanHandleNullBranchContext()
        {
            // Arrange
            var collectionName = "test-collection";
            var syncState = new SyncStateRecord(_testRepoPath, collectionName, null)
            {
                LastSyncCommit = "abc123",
                DocumentCount = 5
            };

            // Act
            await _tracker.UpdateSyncStateAsync(_testRepoPath, collectionName, syncState);
            var retrieved = await _tracker.GetSyncStateAsync(_testRepoPath, collectionName, null);

            // Assert
            Assert.That(retrieved, Is.Not.Null);
            Assert.That(retrieved.Value.BranchContext, Is.Null);
            Assert.That(retrieved.Value.LastSyncCommit, Is.EqualTo("abc123"));
            Assert.That(retrieved.Value.DocumentCount, Is.EqualTo(5));
        }

        /// <summary>
        /// Tests sync state reconstruction placeholder functionality
        /// </summary>
        [Test]
        public async Task ReconstructSyncStateReturnsTrue()
        {
            // Act
            var result = await _tracker.ReconstructSyncStateAsync(_testRepoPath, "main");

            // Assert
            Assert.That(result, Is.True);
        }

        /// <summary>
        /// Tests cleanup placeholder functionality
        /// </summary>
        [Test]
        public async Task CleanupStaleSyncStatesCompletes()
        {
            // Act & Assert - Should complete without throwing
            await _tracker.CleanupStaleSyncStatesAsync(_testRepoPath);
        }

        /// <summary>
        /// Tests SyncStateRecord factory methods
        /// </summary>
        [Test]
        public void SyncStateRecordFactoryMethods()
        {
            // Arrange
            var original = new SyncStateRecord("test-repo", "test-collection", "main")
            {
                LastSyncCommit = "abc123",
                DocumentCount = 5,
                SyncStatus = "synced"
            };

            // Test WithStatus
            var withStatus = original.WithStatus("error", "Something went wrong");
            Assert.That(withStatus.SyncStatus, Is.EqualTo("error"));
            Assert.That(withStatus.ErrorMessage, Is.EqualTo("Something went wrong"));

            // Test WithSyncUpdate
            var withUpdate = original.WithSyncUpdate("def456", 10, 20, "openai");
            Assert.That(withUpdate.LastSyncCommit, Is.EqualTo("def456"));
            Assert.That(withUpdate.DocumentCount, Is.EqualTo(10));
            Assert.That(withUpdate.ChunkCount, Is.EqualTo(20));
            Assert.That(withUpdate.EmbeddingModel, Is.EqualTo("openai"));
            Assert.That(withUpdate.SyncStatus, Is.EqualTo("synced"));

            // Test WithLocalChanges
            var withChanges = original.WithLocalChanges(3);
            Assert.That(withChanges.LocalChangesCount, Is.EqualTo(3));
            Assert.That(withChanges.SyncStatus, Is.EqualTo("local_changes"));

            var withoutChanges = original.WithLocalChanges(0);
            Assert.That(withoutChanges.LocalChangesCount, Is.EqualTo(0));
            Assert.That(withoutChanges.SyncStatus, Is.EqualTo("synced"));
        }

        /// <summary>
        /// Tests repository isolation - sync states should be isolated per repository
        /// </summary>
        [Test]
        public async Task SyncStateIsolatedByRepository()
        {
            // Arrange
            var repo2Path = Path.Combine(Path.GetTempPath(), $"repo2_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(repo2Path);

            var collectionName = "test-collection";
            var branchContext = "main";

            var repo1State = new SyncStateRecord(_testRepoPath, collectionName, branchContext)
            {
                DocumentCount = 5
            };

            var repo2State = new SyncStateRecord(repo2Path, collectionName, branchContext)
            {
                DocumentCount = 10
            };

            // Act
            await _tracker.InitializeAsync(repo2Path);
            await _tracker.UpdateSyncStateAsync(_testRepoPath, collectionName, repo1State);
            await _tracker.UpdateSyncStateAsync(repo2Path, collectionName, repo2State);

            var retrievedRepo1 = await _tracker.GetSyncStateAsync(_testRepoPath, collectionName, branchContext);
            var retrievedRepo2 = await _tracker.GetSyncStateAsync(repo2Path, collectionName, branchContext);

            // Assert
            Assert.That(retrievedRepo1, Is.Not.Null);
            Assert.That(retrievedRepo2, Is.Not.Null);
            
            Assert.That(retrievedRepo1.Value.DocumentCount, Is.EqualTo(5));
            Assert.That(retrievedRepo2.Value.DocumentCount, Is.EqualTo(10));

            // Cleanup
            if (Directory.Exists(repo2Path))
            {
                Directory.Delete(repo2Path, true);
            }
        }
    }
}