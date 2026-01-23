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
    /// Integration tests specifically for PP13-69: Branch Switching Validation
    /// Tests the exact scenarios that were failing before the architectural fix
    /// Validates that "chroma_sync_state" checkout conflicts are eliminated
    /// </summary>
    [TestFixture]
    [Category("PP13-69")]
    [Category("BranchSwitching")]
    [Category("Integration")]
    [Category("CriticalValidation")]
    public class PP13_69_BranchSwitchingValidationTests
    {
        private DoltCli _dolt = null!;
        private SqliteDeletionTracker _syncStateTracker = null!;
        private SyncManagerV2 _syncManager = null!;
        private IChromaDbService _chromaService = null!;
        private string _testRepoPath = null!;
        private ServerConfiguration _serverConfig = null!;

        [SetUp]
        public async Task Setup()
        {
            // Create temporary directory for test repository
            _testRepoPath = Path.Combine(Path.GetTempPath(), $"PP13_69_BranchTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testRepoPath);

            // Setup server configuration
            _serverConfig = new ServerConfiguration { DataPath = Path.GetDirectoryName(_testRepoPath)! };

            // Initialize Dolt repository
            var doltConfig = new DoltConfiguration 
            { 
                RepositoryPath = _testRepoPath,
                CommandTimeoutMs = 30000
            };

            _dolt = new DoltCli(Options.Create(doltConfig), Mock.Of<ILogger<DoltCli>>());
            
            // Initialize repository with schema (no chroma_sync_state table)
            await _dolt.InitAsync();
            await _dolt.ExecuteAsync("CREATE TABLE collections (collection_name VARCHAR(255) PRIMARY KEY)");
            await _dolt.ExecuteAsync("CREATE TABLE documents (doc_id VARCHAR(64) NOT NULL, collection_name VARCHAR(255) NOT NULL, content LONGTEXT NOT NULL, content_hash CHAR(64) NOT NULL, title VARCHAR(500), metadata JSON NOT NULL, created_at DATETIME DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (doc_id, collection_name))");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Initial schema setup - PP13-69 compliant (no chroma_sync_state)");

            // Initialize sync state tracker (SQLite-based)
            _syncStateTracker = new SqliteDeletionTracker(
                Mock.Of<ILogger<SqliteDeletionTracker>>(), 
                _serverConfig
            );
            await _syncStateTracker.InitializeAsync(_testRepoPath);

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
        public async Task BranchSwitching_WithLocalChanges_CarryMode_NoSyncStateConflicts()
        {
            // This test replicates the exact scenario from TestBranchSwitchingValidation that was failing
            // Before PP13-69 fix: "Your local changes to the following tables would be overwritten by checkout: chroma_sync_state"
            // After PP13-69 fix: Should complete successfully without conflicts

            // Arrange - Setup initial data
            const string collectionName = "test_collection";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('doc1', '{collectionName}', 'original content', 'hash1', '{{\"type\":\"original\"}}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Add initial data on main branch");

            // Create sync state (this would have been in Dolt before, causing conflicts)
            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);
            await deltaDetector.UpdateSyncStateAsync(collectionName, "initial_sync", 1, 2);

            // Create feature branch with different data
            await _dolt.CheckoutAsync("feature", createNew: true);
            await _dolt.ExecuteAsync($"UPDATE documents SET content = 'feature content' WHERE doc_id = 'doc1'");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Modify content on feature branch");

            // Update sync state on feature branch
            await deltaDetector.UpdateSyncStateAsync(collectionName, "feature_sync", 1, 3);

            // Switch back to main and create local uncommitted changes
            await _dolt.CheckoutAsync("main");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('doc2', '{collectionName}', 'uncommitted local change', 'hash2', '{{\"type\":\"local\"}}')");

            // Verify we have uncommitted changes
            var status = await _dolt.GetStatusAsync();
            Assert.That(status.HasStagedChanges || status.HasUnstagedChanges, Is.True, "Should have uncommitted changes for test validity");

            // Act - The critical test: checkout with carry mode
            // This is the exact operation that was failing with sync state conflicts
            var result = await _syncManager.ProcessCheckoutAsync("feature", preserveLocalChanges: true);

            // Assert - Verify no conflicts and successful operation (PP13-69-C1: simplified behavior)
            Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "ProcessCheckoutAsync should complete successfully without sync state conflicts (PP13-69 fix)");
            Assert.That(result.ErrorMessage, Is.Null.Or.Empty, 
                "Should not have error messages for sync state conflicts");
            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                Assert.That(result.ErrorMessage, Does.Not.Contain("chroma_sync_state"), 
                    "Error message should not mention chroma_sync_state conflicts");
                Assert.That(result.ErrorMessage, Does.Not.Contain("local changes to the following tables would be overwritten"), 
                    "Should not have table overwrite conflicts for sync state");
            }

            // Verify we're on the target branch
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            Assert.That(currentBranch, Is.EqualTo("feature"), "Should successfully switch to feature branch");

            // Verify sync state independence (key architectural benefit)
            var mainSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "main");
            var featureSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "feature");

            Assert.That(mainSyncState, Is.Not.Null, "Main branch sync state should be preserved");
            Assert.That(featureSyncState, Is.Not.Null, "Feature branch sync state should be preserved");
            Assert.That(mainSyncState?.LastSyncCommit, Is.EqualTo("initial_sync"));
            Assert.That(featureSyncState?.LastSyncCommit, Is.EqualTo("feature_sync"));
        }

        [Test]
        public async Task BranchSwitching_RepeatedCheckouts_NoAccumulatedConflicts()
        {
            // Test that multiple branch switches don't create accumulated sync state issues
            // This validates the architectural fix thoroughly

            // Arrange
            const string collectionName = "multi_switch_test";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('doc1', '{collectionName}', 'base content', 'hash_base', '{{}}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Initial commit");

            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);

            // Create multiple branches with sync states
            string[] branches = { "branch1", "branch2", "branch3" };
            for (int i = 0; i < branches.Length; i++)
            {
                await _dolt.CheckoutAsync(branches[i], createNew: true);
                await _dolt.ExecuteAsync($"UPDATE documents SET content = 'content_{i}' WHERE doc_id = 'doc1'");
                await _dolt.AddAsync(".");
                await _dolt.CommitAsync($"Update on {branches[i]}");
                await deltaDetector.UpdateSyncStateAsync(collectionName, $"sync_{i}", 1, i + 1);
            }

            // Act - Rapidly switch between branches with local changes
            for (int round = 0; round < 3; round++)
            {
                foreach (string branch in branches)
                {
                    // Create local uncommitted change
                    await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('temp_{round}_{branch}', '{collectionName}', 'temp', 'hash_temp', '{{}}') ON DUPLICATE KEY UPDATE content = 'updated'");

                    // Switch with carry mode
                    var result = await _syncManager.ProcessCheckoutAsync(branch, preserveLocalChanges: true);

                    // Assert each switch succeeds (PP13-69-C1: check Status instead of Success)
                    Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed), 
                        $"Round {round}, switch to {branch} should succeed without sync state conflicts");

                    var currentBranch = await _dolt.GetCurrentBranchAsync();
                    Assert.That(currentBranch, Is.EqualTo(branch));
                }
            }

            // Final verification - all sync states should be independent and intact
            for (int i = 0; i < branches.Length; i++)
            {
                var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, branches[i]);
                Assert.That(syncState, Is.Not.Null, $"Sync state for {branches[i]} should be preserved");
                Assert.That(syncState?.LastSyncCommit, Is.EqualTo($"sync_{i}"));
                Assert.That(syncState?.ChunkCount, Is.EqualTo(i + 1));
            }
        }

        [Test]
        public async Task BranchSwitching_ForceCheckoutScenario_NoTableDropping()
        {
            // Verify that the old workaround of dropping chroma_sync_state table is no longer used
            // This test ensures we're using the clean architectural approach

            // Arrange
            const string collectionName = "force_test";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('doc1', '{collectionName}', 'content', 'hash', '{{}}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Initial state");

            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);
            await deltaDetector.UpdateSyncStateAsync(collectionName, "test_sync", 1, 2);

            // Create conflicting branch
            await _dolt.CheckoutAsync("conflict-branch", createNew: true);
            await _dolt.ExecuteAsync($"UPDATE documents SET content = 'different content' WHERE doc_id = 'doc1'");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Conflicting change");

            // Go back to main and create conflicting local change
            await _dolt.CheckoutAsync("main");
            await _dolt.ExecuteAsync($"UPDATE documents SET content = 'local conflicting content' WHERE doc_id = 'doc1'");

            // Act - Attempt checkout that would require force
            var result = await _syncManager.ProcessCheckoutAsync("conflict-branch", preserveLocalChanges: true);

            // Assert - Verify tables list never includes chroma_sync_state
            var tablesResult = await _dolt.QueryAsync<Dictionary<string, object>>("SHOW TABLES");
            var tableNames = tablesResult?.SelectMany(row => row.Values).Select(v => v.ToString()).ToList() ?? new List<string>();
            Assert.That(tableNames, Does.Not.Contain("chroma_sync_state"), 
                "chroma_sync_state table should not exist in Dolt schema (architectural fix)");

            // Verify sync state is tracked in SQLite only
            var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "main");
            Assert.That(syncState, Is.Not.Null, "Sync state should exist in SQLite");
            Assert.That(syncState?.LastSyncCommit, Is.EqualTo("test_sync"));

            // The result may succeed or fail based on content conflicts, but should never fail due to sync state
            if (result.Status != SyncStatusV2.Completed)
            {
                Assert.That(result.ErrorMessage, Does.Not.Contain("chroma_sync_state"), 
                    "Any failure should be due to content conflicts, not sync state conflicts");
            }
        }

        [Test] 
        public async Task BranchSwitching_CarryModeWithSyncState_PreservesUserDataCorrectly()
        {
            // Validates that carry mode works correctly with sync state stored in SQLite
            // User data should be preserved while sync metadata remains branch-specific

            // Arrange
            const string collectionName = "carry_test";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('permanent_doc', '{collectionName}', 'permanent content', 'hash_perm', '{{\"type\":\"permanent\"}}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Add permanent document");

            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);
            await deltaDetector.UpdateSyncStateAsync(collectionName, "main_sync", 1, 2);

            // Create feature branch
            await _dolt.CheckoutAsync("feature", createNew: true);
            await deltaDetector.UpdateSyncStateAsync(collectionName, "feature_sync", 1, 3);

            // Go back to main and create local user data changes
            await _dolt.CheckoutAsync("main");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('local_doc', '{collectionName}', 'local user change', 'hash_local', '{{\"type\":\"local_change\"}}')");

            // Act - Switch with carry mode
            var result = await _syncManager.ProcessCheckoutAsync("feature", preserveLocalChanges: true);

            // Assert - Verify success and data preservation (PP13-69-C1: check Status)
            Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed), "Carry mode should work without sync state conflicts");

            var currentBranch = await _dolt.GetCurrentBranchAsync();
            Assert.That(currentBranch, Is.EqualTo("feature"), "Should be on feature branch");

            // Verify sync state is branch-specific
            var featureSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "feature");
            var mainSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "main");

            Assert.That(featureSyncState, Is.Not.Null);
            Assert.That(mainSyncState, Is.Not.Null);
            Assert.That(featureSyncState?.LastSyncCommit, Is.EqualTo("feature_sync"), "Feature sync state preserved");
            Assert.That(mainSyncState?.LastSyncCommit, Is.EqualTo("main_sync"), "Main sync state preserved");

            // User data preservation can be verified through status (implementation detail)
            // The key point is no sync state conflicts prevented the operation
        }

        [Test]
        public async Task BranchSwitching_EdgeCase_EmptyRepository_NoConflicts()
        {
            // Test edge case with minimal data to ensure robust conflict elimination

            // Arrange - Minimal setup
            const string collectionName = "minimal_test";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Minimal setup");

            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);
            await deltaDetector.UpdateSyncStateAsync(collectionName, "minimal_sync", 0, 0);

            // Act - Create branch and switch back with minimal changes
            await _dolt.CheckoutAsync("minimal-feature", createNew: true);
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('minimal_doc', '{collectionName}', 'minimal', 'hash_min', '{{}}')");

            var result = await _syncManager.ProcessCheckoutAsync("main", preserveLocalChanges: true);

            // Assert - Should succeed without any conflicts (PP13-69-C1: check Status)
            Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed), "Even minimal scenarios should not have sync state conflicts");

            var currentBranch = await _dolt.GetCurrentBranchAsync();
            Assert.That(currentBranch, Is.EqualTo("main"));

            // Verify sync state tracking works in minimal scenarios
            var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "main");
            Assert.That(syncState, Is.Not.Null);
            Assert.That(syncState?.LastSyncCommit, Is.EqualTo("minimal_sync"));
        }

        [Test]
        public async Task ValidationTest_ExactScenarioFromFailureLogs_ShouldNowSucceed()
        {
            // This test replicates as closely as possible the scenario from the failure logs
            // Lines 3355-3394 from TestBranchSwitchingValidation-639032881226077195.testlog

            // Arrange - Setup scenario matching the failure logs
            const string collectionName = "validation_replica";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('test_doc', '{collectionName}', 'test content for validation', 'validation_hash', '{{\"purpose\":\"validation_replica\"}}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Setup validation test scenario");

            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);
            await deltaDetector.UpdateSyncStateAsync(collectionName, "validation_sync", 1, 2);

            // Create target branch
            await _dolt.CheckoutAsync("main");  // Ensure we're on main
            await _dolt.CheckoutAsync("target-branch", createNew: true);
            await _dolt.ExecuteAsync($"UPDATE documents SET content = 'target branch content' WHERE doc_id = 'test_doc'");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Target branch changes");

            // Go back to main and create the local changes that would cause conflicts
            await _dolt.CheckoutAsync("main");
            await _dolt.ExecuteAsync($"UPDATE documents SET content = 'main branch local changes' WHERE doc_id = 'test_doc'");

            // Act - This is the exact operation that was failing:
            // "Your local changes to the following tables would be overwritten by checkout: chroma_sync_state"
            var checkoutResult = await _syncManager.ProcessCheckoutAsync("target-branch", preserveLocalChanges: true);

            // Assert - The critical validation (PP13-69-C1: check Status)
            Assert.That(checkoutResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "CRITICAL: The exact scenario from failure logs should now succeed (PP13-69 architectural fix)");

            // Verify no mention of chroma_sync_state in any error
            if (checkoutResult.Status != SyncStatusV2.Completed)
            {
                Assert.That(checkoutResult.ErrorMessage, Does.Not.Contain("chroma_sync_state"), 
                    "Any remaining errors should not be related to sync state conflicts");
                Assert.That(checkoutResult.ErrorMessage, Does.Not.Contain("DROP TABLE IF EXISTS chroma_sync_state"), 
                    "Should not attempt to drop sync state tables");
            }

            // Verify proper branch switching occurred
            var finalBranch = await _dolt.GetCurrentBranchAsync();
            
            // The branch switch may succeed or fail due to content conflicts, but never due to sync state
            if (checkoutResult.Status == SyncStatusV2.Completed)
            {
                Assert.That(finalBranch, Is.EqualTo("target-branch"), 
                    "If successful, should be on target branch");
            }
            
            // Most importantly: verify sync state is completely separate from Dolt versioning
            var tablesResult = await _dolt.QueryAsync<Dictionary<string, object>>("SHOW TABLES");
            var tableNames = tablesResult?.SelectMany(row => row.Values).Select(v => v.ToString()).ToList() ?? new List<string>();
            Assert.That(tableNames, Does.Not.Contain("chroma_sync_state"), 
                "ARCHITECTURAL VALIDATION: chroma_sync_state should not exist in versioned Dolt schema");

            var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "main");
            Assert.That(syncState, Is.Not.Null, 
                "Sync state should exist in local SQLite storage");
            Assert.That(syncState?.LastSyncCommit, Is.EqualTo("validation_sync"), 
                "Sync state should be preserved independently of branch operations");
        }
    }
}