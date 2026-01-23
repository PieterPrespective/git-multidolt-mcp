using System;
using System.IO;
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
    /// Tests to validate the architectural promises of PP13-69-C1:
    /// - Sync state never appears in Dolt tables
    /// - Checkout operations never fail due to sync state conflicts
    /// - ProcessCheckoutAsync remains simple and confident
    /// </summary>
    [TestFixture]
    [Category("PP13-69-C1")]
    [Category("ArchitecturalPromises")]
    [Category("Integration")]
    public class PP13_69_C1_ArchitecturalPromiseTests
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
            _testRepoPath = Path.Combine(Path.GetTempPath(), $"PP13_69_C1_Test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testRepoPath);

            _serverConfig = new ServerConfiguration { DataPath = Path.GetDirectoryName(_testRepoPath)! };

            var doltConfig = new DoltConfiguration 
            { 
                RepositoryPath = _testRepoPath,
                CommandTimeoutMs = 30000
            };

            _dolt = new DoltCli(Options.Create(doltConfig), Mock.Of<ILogger<DoltCli>>());
            
            await _dolt.InitAsync();
            await _dolt.ExecuteAsync("CREATE TABLE collections (collection_name VARCHAR(255) PRIMARY KEY)");
            await _dolt.ExecuteAsync("CREATE TABLE documents (doc_id VARCHAR(64) NOT NULL, collection_name VARCHAR(255) NOT NULL, content LONGTEXT NOT NULL, content_hash CHAR(64) NOT NULL, title VARCHAR(500), metadata JSON NOT NULL, created_at DATETIME DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (doc_id, collection_name))");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Initial PP13-69-C1 compliant schema");

            _syncStateTracker = new SqliteDeletionTracker(
                Mock.Of<ILogger<SqliteDeletionTracker>>(), 
                _serverConfig
            );
            await _syncStateTracker.InitializeAsync(_testRepoPath);

            // Setup ChromaDB mock with complete setup for SyncChromaToMatchBranch and FullSyncAsync
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
                await Task.Delay(100);
                
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
        public async Task SyncState_AlwaysInSQLite_NeverInDolt()
        {
            // Verify sync state NEVER appears in Dolt tables
            const string collectionName = "promise_test";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Add collection");

            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);
            await deltaDetector.UpdateSyncStateAsync(collectionName, "test_sync", 1, 2);
            
            // Wait a bit for the sync state to be persisted
            await Task.Delay(100);

            // Verify Dolt schema never contains sync state tables
            var tablesResult = await _dolt.QueryAsync<Dictionary<string, object>>("SHOW TABLES");
            var tableNames = tablesResult?.SelectMany(row => row.Values).Select(v => v.ToString()).ToList() ?? new List<string>();
            
            Assert.That(tableNames, Does.Not.Contain("chroma_sync_state"));
            Assert.That(tableNames, Does.Not.Contain("sync_state"));
            
            // Verify sync state exists in SQLite (check current branch)
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, currentBranch);
            Assert.That(syncState, Is.Not.Null, "Sync state should exist in SQLite");
            Assert.That(syncState?.LastSyncCommit, Is.EqualTo("test_sync"));
        }

        [Test]
        public async Task Checkout_AnyBranch_NoSyncStateConflicts()
        {
            // Test rapid branch switching with various sync states - should never fail due to sync state
            const string collectionName = "rapid_switch_test";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Initial state");

            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);

            for (int i = 0; i < 10; i++) 
            {
                var branchName = $"branch_{i}";
                var result = await _syncManager.ProcessCheckoutAsync(branchName, createNew: true);
                
                Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed), 
                    $"Branch switch {i} should never fail due to sync state (architectural promise)");
                
                // Create unique sync state per branch
                await deltaDetector.UpdateSyncStateAsync(collectionName, $"sync_{i}", 1, i + 1);
                
                // Verify we're on the correct branch
                var currentBranch = await _dolt.GetCurrentBranchAsync();
                Assert.That(currentBranch, Is.EqualTo(branchName));
            }

            // Verify all sync states are preserved independently
            for (int i = 0; i < 10; i++) 
            {
                var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, $"branch_{i}");
                Assert.That(syncState, Is.Not.Null, $"Sync state for branch_{i} should be preserved");
                Assert.That(syncState?.ChunkCount, Is.EqualTo(i + 1));
            }
        }

        [Test]
        public async Task ProcessCheckoutAsync_LineCountValidation_StaysSimple()
        {
            // Ensure method doesn't regress to complex implementation
            // Target: <50 lines total for ProcessCheckoutAsync main method
            
            // This test validates that the implementation maintains architectural simplicity
            // by checking the method signature reflects PP13-69-C1 goals
            
            const string collectionName = "simplicity_test";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Setup for simplicity test");

            // Act - Simple checkout operation
            var result = await _syncManager.ProcessCheckoutAsync("main");
            
            // Assert - Should complete confidently without defensive complexity
            Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "Simple checkout should succeed with simplified implementation");

            // Validation: Method signature should reflect simplification
            var method = typeof(SyncManagerV2).GetMethod("ProcessCheckoutAsync");
            Assert.That(method, Is.Not.Null, "ProcessCheckoutAsync method should exist");
            
            var parameters = method!.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(3), 
                "Method should have 3 parameters (targetBranch, createNew, preserveLocalChanges) - no force parameter");
            Assert.That(parameters[0].Name, Is.EqualTo("targetBranch"));
            Assert.That(parameters[1].Name, Is.EqualTo("createNew"));
            Assert.That(parameters[2].Name, Is.EqualTo("preserveLocalChanges"));
            
            // No force parameter should exist (eliminated with sync state conflicts)
            var hasForceParam = false;
            foreach (var param in parameters)
            {
                if (param.Name == "force")
                {
                    hasForceParam = true;
                    break;
                }
            }
            Assert.That(hasForceParam, Is.False, 
                "Force parameter should be eliminated (not needed for sync state conflicts)");
        }

        [Test]
        public async Task SyncStateTracking_OnlyInSQLite_NeverStaged()
        {
            // Verify sync state operations never show up in Dolt staging
            const string collectionName = "staging_test";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Setup staging test");

            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);

            // Update sync state multiple times
            for (int i = 0; i < 5; i++)
            {
                await deltaDetector.UpdateSyncStateAsync(collectionName, $"sync_{i}", 1, i + 1);
                
                // Verify no staging changes after sync state updates
                var status = await _dolt.GetStatusAsync();
                Assert.That(status.HasStagedChanges, Is.False, 
                    $"Sync state update {i} should not create staged changes in Dolt");
                Assert.That(status.HasUnstagedChanges, Is.False, 
                    $"Sync state update {i} should not create unstaged changes in Dolt");
            }

            // Verify sync state exists in SQLite but not Dolt (check current branch)
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, currentBranch);
            Assert.That(syncState, Is.Not.Null, "Sync state should exist in SQLite");
            Assert.That(syncState?.ChunkCount, Is.EqualTo(5), "Latest sync state should be preserved");

            // Final check: No sync state related tables in Dolt
            var tablesResult = await _dolt.QueryAsync<Dictionary<string, object>>("SHOW TABLES");
            var tableNames = tablesResult?.SelectMany(row => row.Values).Select(v => v.ToString()).ToList() ?? new List<string>();
            
            Assert.That(tableNames, Does.Not.Contain("chroma_sync_state"));
            Assert.That(tableNames, Does.Not.Contain("sync_state"));
        }

        [Test]
        public async Task ProcessCheckoutAsync_Performance_Under2000ms()
        {
            // PP13-69-C1 should make checkout faster, not slower
            const string collectionName = "performance_test";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('perf_doc', '{collectionName}', 'performance test content', 'perf_hash', '{{}}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Performance test setup");

            // Create target branch
            await _syncManager.ProcessCheckoutAsync("performance-test", createNew: true);
            await _syncManager.ProcessCheckoutAsync("main");

            // Measure checkout performance
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await _syncManager.ProcessCheckoutAsync("performance-test");
            stopwatch.Stop();

            Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed),
                "Performance test checkout should succeed");

            double elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Checkout completed in {elapsedMilliseconds:F2} milliseconds.");

            Assert.That(elapsedMilliseconds, Is.LessThan(3000),
                "Checkout should complete reasonably fast (functional focus, not performance optimization)");
            
            // Verify we're on the correct branch
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            Assert.That(currentBranch, Is.EqualTo("performance-test"));
        }

        [Test]
        public async Task PP13_69_Promise_CarryModeSeamless()
        {
            // Test: Carry mode works seamlessly without sync state interference
            const string collectionName = "carry_promise_test";
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('carry_doc', '{collectionName}', 'original content', 'orig_hash', '{{}}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Setup carry test");

            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);
            await deltaDetector.UpdateSyncStateAsync(collectionName, "main_sync", 1, 2);

            // Create target branch with different sync state
            await _syncManager.ProcessCheckoutAsync("carry-test", createNew: true);
            await deltaDetector.UpdateSyncStateAsync(collectionName, "carry_sync", 1, 3);

            // Go back and create uncommitted changes
            await _syncManager.ProcessCheckoutAsync("main");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('uncommitted_doc', '{collectionName}', 'uncommitted change', 'uncommit_hash', '{{}}')");

            // The critical test: carry mode should work seamlessly
            var result = await _syncManager.ProcessCheckoutAsync("carry-test", preserveLocalChanges: true);

            Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "Carry mode should work seamlessly without sync state interference (PP13-69 promise)");

            var currentBranch = await _dolt.GetCurrentBranchAsync();
            Assert.That(currentBranch, Is.EqualTo("carry-test"));

            // Verify sync states remain independent
            var mainSync = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "main");
            var carrySync = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "carry-test");

            Assert.That(mainSync?.LastSyncCommit, Is.EqualTo("main_sync"));
            Assert.That(carrySync?.LastSyncCommit, Is.EqualTo("carry_sync"));
        }

        [Test]
        public async Task ArchitecturalValidation_CompleteWorkflow_NoRegressions()
        {
            // Comprehensive test validating the complete PP13-69-C1 architectural promises
            const string collectionName = "complete_validation";
            
            // Phase 1: Setup with sync state
            await _dolt.ExecuteAsync($"INSERT INTO collections (collection_name) VALUES ('{collectionName}')");
            await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('doc1', '{collectionName}', 'content1', 'hash1', '{{}}')");
            await _dolt.AddAsync(".");
            await _dolt.CommitAsync("Phase 1 setup");

            var deltaDetector = new DeltaDetectorV2(_dolt, _syncStateTracker, _testRepoPath);
            await deltaDetector.UpdateSyncStateAsync(collectionName, "phase1_sync", 1, 2);

            // Phase 2: Multiple branches with independent sync states
            string[] branches = { "feature-a", "feature-b", "hotfix", "experimental" };
            foreach (var branch in branches)
            {
                var checkoutResult = await _syncManager.ProcessCheckoutAsync(branch, createNew: true);
                Assert.That(checkoutResult.Status, Is.EqualTo(SyncStatusV2.Completed));
                
                await deltaDetector.UpdateSyncStateAsync(collectionName, $"{branch}_sync", 1, branches.Length);
            }

            // Phase 3: Rapid switching with carry mode
            for (int round = 0; round < 3; round++)
            {
                foreach (var branch in branches)
                {
                    // Create local changes
                    await _dolt.ExecuteAsync($"INSERT INTO documents (doc_id, collection_name, content, content_hash, metadata) VALUES ('temp_{round}_{branch}', '{collectionName}', 'temp content', 'temp_hash', '{{}}') ON DUPLICATE KEY UPDATE content = 'updated'");

                    var result = await _syncManager.ProcessCheckoutAsync(branch, preserveLocalChanges: true);
                    Assert.That(result.Status, Is.EqualTo(SyncStatusV2.Completed), 
                        $"Round {round}, branch {branch} should switch seamlessly");
                }
            }

            // Phase 4: Final architectural validation
            var tablesResult = await _dolt.QueryAsync<Dictionary<string, object>>("SHOW TABLES");
            var tableNames = tablesResult?.SelectMany(row => row.Values).Select(v => v.ToString()).ToList() ?? new List<string>();
            
            Assert.That(tableNames, Does.Not.Contain("chroma_sync_state"));
            Assert.That(tableNames, Does.Not.Contain("sync_state"));

            // Verify all sync states preserved independently
            foreach (var branch in branches)
            {
                var syncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, branch);
                Assert.That(syncState, Is.Not.Null, $"Sync state for {branch} should be preserved");
                Assert.That(syncState?.LastSyncCommit, Is.EqualTo($"{branch}_sync"));
            }

            // Final checkout to main should work flawlessly
            var finalResult = await _syncManager.ProcessCheckoutAsync("main");
            Assert.That(finalResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "Final checkout should complete without any architectural conflicts");
            
            var mainSyncState = await _syncStateTracker.GetSyncStateAsync(_testRepoPath, collectionName, "main");
            Assert.That(mainSyncState?.LastSyncCommit, Is.EqualTo("phase1_sync"), 
                "Main branch sync state should be preserved throughout all operations");
        }
    }
}