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
using Moq;

namespace Embranch.IntegrationTests
{
    /// <summary>
    /// Integration tests for PP13-69 Phase 2: Service Integration
    /// Tests that sync state is properly stored in SQLite and services work together correctly
    /// </summary>
    [TestFixture]
    [Category("PP13-69")]
    [Category("Phase2")]
    [Category("Integration")]
    public class PP13_69_Phase2_SyncStateServiceIntegrationTests
    {
        private DoltCli _dolt = null!;
        private SqliteDeletionTracker _syncStateTracker = null!;
        private DeltaDetectorV2 _deltaDetector = null!;
        private SyncManagerV2 _syncManager = null!;
        private IChromaDbService _chromaService = null!;
        private string _testRepoPath = null!;
        private string _testDbPath = null!;
        private ServerConfiguration _serverConfig = null!;

        [SetUp]
        public async Task Setup()
        {
            // Create temporary directory for test repository
            _testRepoPath = Path.Combine(Path.GetTempPath(), $"PP13_69_Phase2_Test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testRepoPath);

            // Create temporary SQLite database path
            _testDbPath = Path.Combine(_testRepoPath, "test_sync_state.db");

            // Setup server configuration
            _serverConfig = new ServerConfiguration { DataPath = Path.GetDirectoryName(_testDbPath)! };

            // Initialize Dolt repository
            var doltConfig = new DoltConfiguration 
            { 
                RepositoryPath = _testRepoPath,
                CommandTimeoutMs = 30000
            };

            _dolt = new DoltCli(Options.Create(doltConfig), Mock.Of<ILogger<DoltCli>>());
            
            // Initialize repository
            await _dolt.InitAsync();
            await _dolt.ExecuteAsync("CREATE TABLE collections (collection_name VARCHAR(255) PRIMARY KEY)");
            await _dolt.ExecuteAsync("CREATE TABLE documents (doc_id VARCHAR(64) NOT NULL, collection_name VARCHAR(255) NOT NULL, content LONGTEXT NOT NULL, content_hash CHAR(64) NOT NULL, title VARCHAR(500), metadata JSON NOT NULL, created_at DATETIME DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (doc_id, collection_name))");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Initial schema setup for PP13-69 Phase 2 tests");

            // Initialize sync state tracker (SQLite-based)
            _syncStateTracker = new SqliteDeletionTracker(
                Mock.Of<ILogger<SqliteDeletionTracker>>(), 
                _serverConfig
            );
            await _syncStateTracker.InitializeAsync(_testRepoPath);

            // Initialize delta detector with sync state tracker
            _deltaDetector = new DeltaDetectorV2(
                _dolt, 
                _syncStateTracker, 
                _testRepoPath,
                Mock.Of<ILogger<DeltaDetectorV2>>()
            );

            // Mock ChromaDB service with complete setup for SyncChromaToMatchBranch and FullSyncAsync
            var chromaMock = new Mock<IChromaDbService>();
            chromaMock.Setup(x => x.ListCollectionsAsync(It.IsAny<int?>(), It.IsAny<int?>()))
                .ReturnsAsync(new List<string>());
            chromaMock.Setup(x => x.GetDocumentsAsync(It.IsAny<string>(), It.IsAny<List<string>?>(), It.IsAny<Dictionary<string, object>?>(), It.IsAny<int?>(), It.IsAny<bool>()))
                .ReturnsAsync(new Dictionary<string, object> { ["ids"] = new List<object>() });
            chromaMock.Setup(x => x.DeleteCollectionAsync(It.IsAny<string>()))
                .ReturnsAsync(true);
            chromaMock.Setup(x => x.CreateCollectionAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object>?>()))
                .ReturnsAsync(true);
            chromaMock.Setup(x => x.AddDocumentsAsync(
                It.IsAny<string>(),
                It.IsAny<List<string>>(),
                It.IsAny<List<string>>(),
                It.IsAny<List<Dictionary<string, object>>?>(),
                It.IsAny<bool>(),
                It.IsAny<bool>()))
                .ReturnsAsync(true);
            chromaMock.Setup(x => x.DeleteDocumentsAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<bool>()))
                .ReturnsAsync(true);
            _chromaService = chromaMock.Object;

            // Initialize sync manager with all dependencies
            var doltConfigOptions = Options.Create(doltConfig);
            _syncManager = new SyncManagerV2(
                _dolt,
                _chromaService,
                _syncStateTracker,
                _syncStateTracker,
                doltConfigOptions,
                Mock.Of<ILogger<SyncManagerV2>>()
            );
        }

        [TearDown]
        public async Task Cleanup()
        {
            try
            {
                _syncStateTracker?.Dispose();
                await Task.Delay(100); // Allow cleanup
                
                if (Directory.Exists(_testRepoPath))
                {
                    Directory.Delete(_testRepoPath, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        [Test]
        public async Task DeltaDetector_UpdateSyncState_StoresInSQLite()
        {
            // Arrange
            const string collectionName = "test_collection";
            const string commitHash = "abc123def456";
            const int documentCount = 5;
            const int chunkCount = 10;

            // Ensure collection exists in Dolt
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");

            // Act
            await _deltaDetector.UpdateSyncStateAsync(collectionName, commitHash, documentCount, chunkCount);

            // Assert
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, currentBranch);

            Assert.That(syncState, Is.Not.Null, "Sync state should be stored in SQLite");
            Assert.That(syncState?.CollectionName, Is.EqualTo(collectionName));
            Assert.That(syncState?.LastSyncCommit, Is.EqualTo(commitHash));
            Assert.That(syncState?.DocumentCount, Is.EqualTo(documentCount));
            Assert.That(syncState?.ChunkCount, Is.EqualTo(chunkCount));
            Assert.That(syncState?.SyncStatus, Is.EqualTo("synced"));
            Assert.That(syncState?.BranchContext, Is.EqualTo(currentBranch));
            Assert.That(syncState?.RepoPath, Is.EqualTo(_testRepoPath));
        }

        [Test]
        public async Task DeltaDetector_UpdateSyncState_UpdatesExistingRecord()
        {
            // Arrange
            const string collectionName = "test_collection";
            const string initialCommit = "initial123";
            const string updatedCommit = "updated456";

            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");

            // Act - First update
            await _deltaDetector.UpdateSyncStateAsync(collectionName, initialCommit, 3, 6);

            // Act - Second update
            await _deltaDetector.UpdateSyncStateAsync(collectionName, updatedCommit, 7, 14);

            // Assert
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, currentBranch);

            Assert.That(syncState, Is.Not.Null);
            Assert.That(syncState?.LastSyncCommit, Is.EqualTo(updatedCommit), "Should update to latest commit");
            Assert.That(syncState?.DocumentCount, Is.EqualTo(7), "Should update document count");
            Assert.That(syncState?.ChunkCount, Is.EqualTo(14), "Should update chunk count");
        }

        [Test]
        public async Task SyncState_BranchIsolation_DifferentBranchesHaveIndependentSyncState()
        {
            // Arrange
            const string collectionName = "test_collection";
            const string mainBranchCommit = "main123";
            const string featureBranchCommit = "feature456";

            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Add test collection");

            // Act - Update sync state on main branch
            await _deltaDetector.UpdateSyncStateAsync(collectionName, mainBranchCommit, 5, 10);

            // Create and switch to feature branch
            await _dolt.CheckoutAsync("feature-branch", createNew: true);

            // Update sync state on feature branch  
            await _deltaDetector.UpdateSyncStateAsync(collectionName, featureBranchCommit, 8, 16);

            // Assert - Check feature branch sync state
            var featureSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "feature-branch");
            Assert.That(featureSyncState, Is.Not.Null);
            Assert.That(featureSyncState?.LastSyncCommit, Is.EqualTo(featureBranchCommit));
            Assert.That(featureSyncState?.DocumentCount, Is.EqualTo(8));
            Assert.That(featureSyncState?.BranchContext, Is.EqualTo("feature-branch"));

            // Switch back to main and verify independent state
            await _dolt.CheckoutAsync("main");
            var mainSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "main");
            
            Assert.That(mainSyncState, Is.Not.Null);
            Assert.That(mainSyncState?.LastSyncCommit, Is.EqualTo(mainBranchCommit));
            Assert.That(mainSyncState?.DocumentCount, Is.EqualTo(5));
            Assert.That(mainSyncState?.BranchContext, Is.EqualTo("main"));
        }

        [Test]
        public async Task SyncState_RepositoryIsolation_DifferentReposHaveIndependentSyncState()
        {
            // Arrange
            const string collectionName = "test_collection";
            const string commit1 = "repo1_commit";
            const string commit2 = "repo2_commit";

            // Setup second repository path
            var secondRepoPath = Path.Combine(Path.GetTempPath(), $"PP13_69_Phase2_Test_Repo2_{Guid.NewGuid():N}");

            try
            {
                // Act - Update sync state for first repo
                await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
                await _deltaDetector.UpdateSyncStateAsync(collectionName, commit1, 3, 6);

                // Initialize second repo tracking
                await _syncStateTracker.InitializeAsync(secondRepoPath);

                // Update sync state for second repo
                await _syncStateTracker.UpdateSyncStateAsync(secondRepoPath, collectionName, 
                    new SyncStateRecord(
                        $"test_id_{Guid.NewGuid():N}"[..32],
                        secondRepoPath,
                        collectionName,
                        "main",
                        commit2,
                        DateTime.UtcNow,
                        7,
                        14,
                        null,
                        "synced",
                        0,
                        null,
                        null,
                        DateTime.UtcNow,
                        DateTime.UtcNow
                    ));

                // Assert - Verify isolation
                var currentBranch = await _dolt.GetCurrentBranchAsync();
                var repo1State = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, currentBranch);
                var repo2State = await _syncStateTracker.GetSyncStateAsync(secondRepoPath, collectionName, "main");

                Assert.That(repo1State, Is.Not.Null);
                Assert.That(repo2State, Is.Not.Null);
                Assert.That(repo1State?.LastSyncCommit, Is.EqualTo(commit1));
                Assert.That(repo2State?.LastSyncCommit, Is.EqualTo(commit2));
                Assert.That(repo1State?.DocumentCount, Is.EqualTo(3));
                Assert.That(repo2State?.DocumentCount, Is.EqualTo(7));
                Assert.That(repo1State?.RepoPath, Is.EqualTo(_testRepoPath));
                Assert.That(repo2State?.RepoPath, Is.EqualTo(secondRepoPath));
            }
            finally
            {
                if (Directory.Exists(secondRepoPath))
                {
                    Directory.Delete(secondRepoPath, true);
                }
            }
        }

        [Test]
        public async Task SyncManager_ProcessCheckout_NoSyncStateConflicts()
        {
            // Arrange
            const string collectionName = "test_collection";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('doc1', '{collectionName}', 'test content', 'hash123', '{{}}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Add test data");

            // Create sync state in SQLite
            await _deltaDetector.UpdateSyncStateAsync(collectionName, "test_commit", 1, 2);

            // Create feature branch with different content
            await _dolt.CheckoutAsync("feature", createNew: true);
            await _dolt.ExecuteAsync($"UPDATE documents SET content = 'modified content' WHERE doc_id = 'doc1'");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Modify content on feature branch");

            // Switch back to main and create local uncommitted changes
            await _dolt.CheckoutAsync("main");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('doc2', '{collectionName}', 'local change', 'hash456', '{{}}')");

            // Act - Attempt checkout with local changes (this would fail in old architecture)
            var result = await _syncManager.ProcessCheckoutAsync("feature", preserveLocalChanges: true);

            // Assert
            Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed), "Checkout should succeed without sync state conflicts");
            
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            Assert.That(currentBranch, Is.EqualTo("feature"), "Should be on feature branch");

            // Verify sync state is maintained independently per branch
            var mainSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "main");
            var featureSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "feature");

            Assert.That(mainSyncState, Is.Not.Null, "Main branch sync state should be preserved");
            Assert.That(featureSyncState, Is.Null, "Feature branch should not have sync state initially");
        }

        [Test]
        public async Task SyncState_SQLiteStorage_NoDoltVersioningInterference()
        {
            // Arrange
            const string collectionName = "test_collection";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");

            // Act - Create sync state and verify it's not in Dolt
            await _deltaDetector.UpdateSyncStateAsync(collectionName, "test_commit", 5, 10);

            // Check Dolt tables to ensure chroma_sync_state doesn't exist
            var doltTablesResult = await _dolt.QueryAsync<Dictionary<string, object>>("SHOW TABLES");
            var tableNames = doltTablesResult.Select(row => row.Values.FirstOrDefault()?.ToString() ?? "").ToList();
            
            // Check that sync state exists in SQLite but not in Dolt
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, currentBranch);

            // Assert
            Assert.That(tableNames, Does.Not.Contain("chroma_sync_state"), 
                "chroma_sync_state table should NOT exist in Dolt (PP13-69 fix)");
            Assert.That(syncState, Is.Not.Null, 
                "Sync state should exist in SQLite storage");
            Assert.That(syncState?.CollectionName, Is.EqualTo(collectionName));
        }

        [Test]
        public async Task SyncState_MultipleCollections_IndependentTracking()
        {
            // Arrange
            const string collection1 = "collection_1";
            const string collection2 = "collection_2";
            const string commit1 = "commit_col1";
            const string commit2 = "commit_col2";

            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collection1}'), ('{collection2}')");

            // Act
            await _deltaDetector.UpdateSyncStateAsync(collection1, commit1, 3, 6);
            await _deltaDetector.UpdateSyncStateAsync(collection2, commit2, 7, 14);

            // Assert
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            
            var syncState1 = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collection1, currentBranch);
            var syncState2 = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collection2, currentBranch);

            Assert.That(syncState1, Is.Not.Null);
            Assert.That(syncState2, Is.Not.Null);
            Assert.That(syncState1?.LastSyncCommit, Is.EqualTo(commit1));
            Assert.That(syncState2?.LastSyncCommit, Is.EqualTo(commit2));
            Assert.That(syncState1?.DocumentCount, Is.EqualTo(3));
            Assert.That(syncState2?.DocumentCount, Is.EqualTo(7));

            // Verify collections are tracked independently
            Assert.That(syncState1?.CollectionName, Is.EqualTo(collection1));
            Assert.That(syncState2?.CollectionName, Is.EqualTo(collection2));
        }

        [Test]
        public async Task SyncState_ReconstructAfterCheckout_FunctionalityPreserved()
        {
            // Arrange
            const string collectionName = "test_collection";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Add collection");

            // Create sync state on main
            await _deltaDetector.UpdateSyncStateAsync(collectionName, "main_commit", 5, 10);

            // Create feature branch
            await _dolt.CheckoutAsync("feature", createNew: true);

            // Act - Test reconstruction capability (placeholder for future implementation)
            var reconstructed = await _syncStateTracker.ReconstructSyncStateAsync(_testRepoPath, "feature");

            // Assert
            Assert.That(reconstructed, Is.True, "Reconstruction should be available for future implementation");

            // Verify sync state independence is maintained
            var mainSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "main");
            var featureSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "feature");

            Assert.That(mainSyncState, Is.Not.Null, "Main branch state should be preserved");
            Assert.That(featureSyncState, Is.Null, "Feature branch should start with clean state");
        }
    }
}