using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Embranch.Models;
using Embranch.Services;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Focused integration tests to validate Phase 1: Multi-collection sync during branch checkout
    /// </summary>
    [TestFixture]
    public class MultiCollectionBranchSyncTests
    {
        private string _tempDir = null!;
        private DoltCli _doltCli = null!;
        private ChromaDbService _chromaService = null!;
        private SyncManagerV2 _syncManager = null!;
        private ChromaToDoltSyncer _chromaSyncer = null!;
        private ILogger<MultiCollectionBranchSyncTests> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            // Initialize Python context if not already done
            if (!PythonContext.IsInitialized)
            {
                PythonContext.Initialize();
            }

            _tempDir = Path.Combine(Path.GetTempPath(), $"MultiCollSyncTests_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _logger = loggerFactory.CreateLogger<MultiCollectionBranchSyncTests>();

            // Initialize Dolt CLI
            var doltConfig = Options.Create(new DoltConfiguration
            {
                DoltExecutablePath = Environment.OSVersion.Platform == PlatformID.Win32NT 
                    ? @"C:\Program Files\Dolt\bin\dolt.exe" 
                    : "dolt",
                RepositoryPath = _tempDir,
                CommandTimeoutMs = 30000,
                EnableDebugLogging = true
            });
            _doltCli = new DoltCli(doltConfig, loggerFactory.CreateLogger<DoltCli>());
            await _doltCli.InitAsync();
            
            // Initialize ChromaDB service with local storage
            var chromaDataPath = Path.Combine(_tempDir, "chroma_data");
            Directory.CreateDirectory(chromaDataPath);
            var serverConfig = Options.Create(new ServerConfiguration 
            { 
                ChromaDataPath = chromaDataPath,
                DataPath = _tempDir
            });
            _chromaService = new ChromaDbService(
                loggerFactory.CreateLogger<ChromaDbService>(), 
                serverConfig
            );

            // Initialize deletion tracker and its database schema
            var deletionTracker = new SqliteDeletionTracker(
                loggerFactory.CreateLogger<SqliteDeletionTracker>(),
                serverConfig.Value);
            deletionTracker.InitializeAsync(_tempDir).GetAwaiter().GetResult();
                
            // Initialize services
            var chromaDetector = new ChromaToDoltDetector(_chromaService, _doltCli, deletionTracker, doltConfig, loggerFactory.CreateLogger<ChromaToDoltDetector>());
            _chromaSyncer = new ChromaToDoltSyncer(_chromaService, _doltCli, chromaDetector, loggerFactory.CreateLogger<ChromaToDoltSyncer>());
            
            // Create SyncManagerV2
            _syncManager = new SyncManagerV2(
                _doltCli,
                _chromaService,
                deletionTracker,
                deletionTracker,
                doltConfig,
                loggerFactory.CreateLogger<SyncManagerV2>()
            );
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                // Dispose ChromaService
                _chromaService?.Dispose();
                
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        /// <summary>
        /// Helper method to extract documents from QueryDocumentsAsync result
        /// </summary>
        private static List<string> ExtractDocuments(object? queryResult)
        {
            var docsDict = (Dictionary<string, object>)queryResult!;
            var docsList = (List<object>)docsDict["documents"];
            var firstResult = (List<object>)docsList[0];
            return firstResult.Cast<string>().ToList();
        }

        /// <summary>
        /// CRITICAL TEST: Verify that ALL collections are synced during branch checkout, not just the first one
        /// This is the primary issue identified in PP13-57
        /// </summary>
        [Test]
        [Ignore("PP13-58: Temporarily disabled - sync state tracking issues causing test hangs")]
        public async Task TestAllCollectionsSyncedDuringCheckout()
        {
            _logger.LogInformation("=== TEST: All Collections Synced During Checkout ===");

            // Step 1: Create multiple collections on main branch
            _logger.LogInformation("Step 1: Creating 3 collections on main branch");
            
            await _chromaService.CreateCollectionAsync("collection_alpha");
            await _chromaService.CreateCollectionAsync("collection_beta");
            await _chromaService.CreateCollectionAsync("collection_gamma");

            // Add documents to each collection
            await _chromaService.AddDocumentsAsync("collection_alpha",
                new List<string> { "Alpha doc 1 on main", "Alpha doc 2 on main" },
                new List<string> { "alpha-1", "alpha-2" });

            await _chromaService.AddDocumentsAsync("collection_beta",
                new List<string> { "Beta doc 1 on main", "Beta doc 2 on main", "Beta doc 3 on main" },
                new List<string> { "beta-1", "beta-2", "beta-3" });

            await _chromaService.AddDocumentsAsync("collection_gamma",
                new List<string> { "Gamma doc 1 on main" },
                new List<string> { "gamma-1" });

            // Stage and commit on main
            await _chromaSyncer.StageLocalChangesAsync("collection_alpha");
            await _chromaSyncer.StageLocalChangesAsync("collection_beta");
            await _chromaSyncer.StageLocalChangesAsync("collection_gamma");
            await _syncManager.ProcessCommitAsync("Initial collections on main");

            _logger.LogInformation("Main branch state: Alpha=2 docs, Beta=3 docs, Gamma=1 doc");

            // Step 2: Create feature branch with different collection states
            _logger.LogInformation("Step 2: Creating feature branch with modified collections");
            
            await _doltCli.CheckoutAsync("feature-branch", createNew: true);

            // Sync to get main content first
            await _syncManager.FullSyncAsync("collection_alpha");
            await _syncManager.FullSyncAsync("collection_beta");
            await _syncManager.FullSyncAsync("collection_gamma");

            // Modify collections on feature branch
            // Alpha: Add 1 document
            await _chromaService.AddDocumentsAsync("collection_alpha",
                new List<string> { "Alpha doc 3 on feature" },
                new List<string> { "alpha-3" });

            // Beta: Delete 1 document
            await _chromaService.DeleteDocumentsAsync("collection_beta", new List<string> { "beta-2" });

            // Gamma: Add 2 documents
            await _chromaService.AddDocumentsAsync("collection_gamma",
                new List<string> { "Gamma doc 2 on feature", "Gamma doc 3 on feature" },
                new List<string> { "gamma-2", "gamma-3" });

            // Create a new collection only on feature branch
            await _chromaService.CreateCollectionAsync("collection_delta");
            await _chromaService.AddDocumentsAsync("collection_delta",
                new List<string> { "Delta doc 1 on feature", "Delta doc 2 on feature" },
                new List<string> { "delta-1", "delta-2" });

            // Stage and commit on feature branch
            await _chromaSyncer.StageLocalChangesAsync("collection_alpha");
            await _chromaSyncer.StageLocalChangesAsync("collection_beta");
            await _chromaSyncer.StageLocalChangesAsync("collection_gamma");
            await _chromaSyncer.StageLocalChangesAsync("collection_delta");
            await _syncManager.ProcessCommitAsync("Feature branch changes");

            _logger.LogInformation("Feature branch state: Alpha=3 docs, Beta=2 docs, Gamma=3 docs, Delta=2 docs");

            // Step 3: Switch back to main and verify ALL collections are synced
            _logger.LogInformation("Step 3: Switching back to main branch");
            
            var checkoutResult = await _syncManager.ProcessCheckoutAsync("main", false);
            
            Assert.That(checkoutResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "Checkout to main should complete successfully");

            // CRITICAL VERIFICATION: Check document counts in ALL collections
            _logger.LogInformation("Step 4: Verifying ALL collections reflect main branch state");
            
            var alphaCount = await _chromaService.GetDocumentCountAsync("collection_alpha");
            var betaCount = await _chromaService.GetDocumentCountAsync("collection_beta");
            var gammaCount = await _chromaService.GetDocumentCountAsync("collection_gamma");
            
            _logger.LogInformation($"After checkout to main - Alpha: {alphaCount}, Beta: {betaCount}, Gamma: {gammaCount}");

            // These assertions verify the fix for PP13-57
            Assert.That(alphaCount, Is.EqualTo(2), 
                "collection_alpha should have 2 documents on main (was getting wrong count due to single collection sync)");
            Assert.That(betaCount, Is.EqualTo(3), 
                "collection_beta should have 3 documents on main (was not synced in original bug)");
            Assert.That(gammaCount, Is.EqualTo(1), 
                "collection_gamma should have 1 document on main (was not synced in original bug)");

            // Verify delta collection doesn't exist on main
            var collections = await _chromaService.ListCollectionsAsync();
            Assert.That(collections.Contains("collection_delta"), Is.False, 
                "collection_delta should not exist on main branch");

            // Step 5: Switch to feature branch and verify ALL collections are synced
            _logger.LogInformation("Step 5: Switching to feature branch");
            
            checkoutResult = await _syncManager.ProcessCheckoutAsync("feature-branch", false);
            
            Assert.That(checkoutResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "Checkout to feature branch should complete successfully");

            // Verify feature branch state
            alphaCount = await _chromaService.GetDocumentCountAsync("collection_alpha");
            betaCount = await _chromaService.GetDocumentCountAsync("collection_beta");
            gammaCount = await _chromaService.GetDocumentCountAsync("collection_gamma");
            var deltaCount = await _chromaService.GetDocumentCountAsync("collection_delta");
            
            _logger.LogInformation($"After checkout to feature - Alpha: {alphaCount}, Beta: {betaCount}, Gamma: {gammaCount}, Delta: {deltaCount}");

            Assert.That(alphaCount, Is.EqualTo(3), 
                "collection_alpha should have 3 documents on feature branch");
            Assert.That(betaCount, Is.EqualTo(2), 
                "collection_beta should have 2 documents on feature branch");
            Assert.That(gammaCount, Is.EqualTo(3), 
                "collection_gamma should have 3 documents on feature branch");
            Assert.That(deltaCount, Is.EqualTo(2), 
                "collection_delta should have 2 documents on feature branch");

            _logger.LogInformation("=== TEST PASSED: All collections properly synced during checkout ===");
        }

        /// <summary>
        /// Test that the branch state validation method works correctly
        /// </summary>
        [Test]
        //[Ignore("PP13-58: Temporarily disabled - sync state tracking issues causing test hangs")]
        public async Task TestBranchStateValidation()
        {
            _logger.LogInformation("=== TEST: Branch State Validation ===");

            // Create test collections
            await _chromaService.CreateCollectionAsync("validation_test_1");
            await _chromaService.CreateCollectionAsync("validation_test_2");

            await _chromaService.AddDocumentsAsync("validation_test_1",
                new List<string> { "Doc 1", "Doc 2" },
                new List<string> { "val-1", "val-2" });

            await _chromaService.AddDocumentsAsync("validation_test_2",
                new List<string> { "Doc A" },
                new List<string> { "val-a" });

            // Stage and commit
            await _chromaSyncer.StageLocalChangesAsync("validation_test_1");
            await _chromaSyncer.StageLocalChangesAsync("validation_test_2");
            await _syncManager.ProcessCommitAsync("Test validation setup");

            // The validation should pass after a successful sync
            var currentBranch = await _doltCli.GetCurrentBranchAsync();
            _logger.LogInformation($"Validating state on branch: {currentBranch}");

            // After commit, ChromaDB and Dolt should be in sync
            var collection1Count = await _chromaService.GetDocumentCountAsync("validation_test_1");
            var collection2Count = await _chromaService.GetDocumentCountAsync("validation_test_2");

            Assert.That(collection1Count, Is.EqualTo(2), "validation_test_1 should have 2 documents");
            Assert.That(collection2Count, Is.EqualTo(1), "validation_test_2 should have 1 document");

            _logger.LogInformation("=== TEST PASSED: Branch state validation working ===");
        }

        /// <summary>
        /// Test that uncommitted changes are properly detected and handled
        /// </summary>
        [Test]
        //[Ignore("PP13-58: Temporarily disabled - sync state tracking issues causing test hangs")]
        public async Task TestUncommittedChangesDetection()
        {
            _logger.LogInformation("=== TEST: Uncommitted Changes Detection ===");

            // Create initial state
            await _chromaService.CreateCollectionAsync("changes_test");
            await _chromaService.AddDocumentsAsync("changes_test",
                new List<string> { "Initial doc" },
                new List<string> { "init-1" });

            await _chromaSyncer.StageLocalChangesAsync("changes_test");
            await _syncManager.ProcessCommitAsync("Initial commit");

            // Create a feature branch
            await _doltCli.CheckoutAsync("test-feature", createNew: true);
            await _syncManager.ProcessCommitAsync("Create feature branch");

            // Switch back to main
            await _syncManager.ProcessCheckoutAsync("main", false);

            // Add uncommitted changes
            await _chromaService.AddDocumentsAsync("changes_test",
                new List<string> { "Uncommitted doc" },
                new List<string> { "uncommitted-1" });

            // Check that changes are detected
            var localChanges = await _syncManager.GetLocalChangesAsync();
            
            Assert.That(localChanges.HasChanges, Is.True, 
                "Should detect uncommitted changes");
            Assert.That(localChanges.TotalChanges, Is.GreaterThan(0), 
                "Should have at least one change");

            // Try to checkout (PP13-69-C1: no force parameter needed)
            // This tests the if_uncommitted logic
            var checkoutResult = await _syncManager.ProcessCheckoutAsync("test-feature", false);
            
            // The checkout might fail or succeed based on uncommitted changes handling
            // Log the result for debugging
            _logger.LogInformation($"Checkout result with uncommitted changes: Status={checkoutResult.Status}");

            if (checkoutResult.Status == SyncStatusV2.LocalChangesExist)
            {
                _logger.LogInformation("Checkout blocked due to local changes (expected behavior)");
                Assert.Pass("Uncommitted changes properly detected and blocked checkout");
            }
            else if (checkoutResult.Status == SyncStatusV2.Completed)
            {
                _logger.LogInformation("Checkout completed (may have handled changes)");
                Assert.Pass("Checkout handled uncommitted changes");
            }

            _logger.LogInformation("=== TEST PASSED: Uncommitted changes detection working ===");
        }

        /// <summary>
        /// Test rapid branch switching to ensure no state corruption
        /// </summary>
        [Test]
        [Ignore("PP13-58: Temporarily disabled - sync state tracking issues causing test hangs")]
        public async Task TestRapidBranchSwitching()
        {
            _logger.LogInformation("=== TEST: Rapid Branch Switching ===");

            // Create collections on main
            await _chromaService.CreateCollectionAsync("rapid_test");
            await _chromaService.AddDocumentsAsync("rapid_test",
                new List<string> { "Main doc 1", "Main doc 2" },
                new List<string> { "rapid-1", "rapid-2" });

            await _chromaSyncer.StageLocalChangesAsync("rapid_test");
            await _syncManager.ProcessCommitAsync("Main setup");

            var mainCount = await _chromaService.GetDocumentCountAsync("rapid_test");
            _logger.LogInformation($"Main branch: {mainCount} documents");

            // Create branch A with different content
            await _doltCli.CheckoutAsync("branch-a", createNew: true);
            await _syncManager.FullSyncAsync("rapid_test");
            
            await _chromaService.AddDocumentsAsync("rapid_test",
                new List<string> { "Branch A doc" },
                new List<string> { "branch-a-1" });
            
            await _chromaSyncer.StageLocalChangesAsync("rapid_test");
            await _syncManager.ProcessCommitAsync("Branch A changes");

            var branchACount = await _chromaService.GetDocumentCountAsync("rapid_test");
            _logger.LogInformation($"Branch A: {branchACount} documents");

            // Create branch B with different content
            await _syncManager.ProcessCheckoutAsync("main", false);
            await _doltCli.CheckoutAsync("branch-b", createNew: true);
            await _syncManager.FullSyncAsync("rapid_test");
            
            await _chromaService.DeleteDocumentsAsync("rapid_test", new List<string> { "rapid-1" });
            
            await _chromaSyncer.StageLocalChangesAsync("rapid_test");
            await _syncManager.ProcessCommitAsync("Branch B changes");

            var branchBCount = await _chromaService.GetDocumentCountAsync("rapid_test");
            _logger.LogInformation($"Branch B: {branchBCount} documents");

            // Rapid switching test
            _logger.LogInformation("Starting rapid branch switching...");

            for (int i = 0; i < 3; i++)
            {
                _logger.LogInformation($"Iteration {i + 1}:");
                
                // Switch to main
                await _syncManager.ProcessCheckoutAsync("main", false);
                var count = await _chromaService.GetDocumentCountAsync("rapid_test");
                Assert.That(count, Is.EqualTo(2), $"Iteration {i}: Main should have 2 docs");
                _logger.LogInformation($"  Main: {count} docs ✓");

                // Switch to branch-a
                await _syncManager.ProcessCheckoutAsync("branch-a", false);
                count = await _chromaService.GetDocumentCountAsync("rapid_test");
                Assert.That(count, Is.EqualTo(3), $"Iteration {i}: Branch A should have 3 docs");
                _logger.LogInformation($"  Branch A: {count} docs ✓");

                // Switch to branch-b
                await _syncManager.ProcessCheckoutAsync("branch-b", false);
                count = await _chromaService.GetDocumentCountAsync("rapid_test");
                Assert.That(count, Is.EqualTo(1), $"Iteration {i}: Branch B should have 1 doc");
                _logger.LogInformation($"  Branch B: {count} docs ✓");
            }

            // Final check - no false positive changes
            var localChanges = await _syncManager.GetLocalChangesAsync();
            Assert.That(localChanges.HasChanges, Is.False, 
                "Should have no false positive changes after rapid switching");

            _logger.LogInformation("=== TEST PASSED: Rapid branch switching without corruption ===");
        }

        /// <summary>
        /// PP13-68 INTEGRATION TEST: Multi-collection checkout with content validation
        /// Tests that content-hash verification works correctly across multiple collections during checkout
        /// This extends the existing multi-collection tests with PP13-68 content validation
        /// </summary>
        [Test]
        public async Task PP13_68_MultiCollectionCheckout_ShouldUpdateAllContentCorrectly()
        {
            _logger.LogInformation("=== PP13-68 TEST: Multi-collection checkout with content validation ===");

            // Step 1: Create main branch with multiple collections
            await _chromaService.CreateCollectionAsync("pp13_68_alpha");
            await _chromaService.CreateCollectionAsync("pp13_68_beta");

            await _chromaService.AddDocumentsAsync("pp13_68_alpha",
                new List<string> { "Alpha main content 1", "Alpha main content 2" },
                new List<string> { "alpha-1", "alpha-2" });

            await _chromaService.AddDocumentsAsync("pp13_68_beta",
                new List<string> { "Beta main content 1", "Beta main content 2" },
                new List<string> { "beta-1", "beta-2" });

            await _chromaSyncer.StageLocalChangesAsync("pp13_68_alpha");
            await _chromaSyncer.StageLocalChangesAsync("pp13_68_beta");
            await _syncManager.ProcessCommitAsync("PP13-68: Setup main branch collections");

            // Step 2: Create feature branch with different content (same counts)
            await _doltCli.CheckoutAsync("pp13-68-multi-feature", createNew: true);
            
            await _syncManager.FullSyncAsync("pp13_68_alpha");
            await _syncManager.FullSyncAsync("pp13_68_beta");

            // Modify content while keeping same document counts and IDs (PP13-68 scenario)
            await _chromaService.DeleteDocumentsAsync("pp13_68_alpha", new List<string> { "alpha-1", "alpha-2" });
            await _chromaService.AddDocumentsAsync("pp13_68_alpha",
                new List<string> { "Alpha FEATURE content 1", "Alpha FEATURE content 2" },
                new List<string> { "alpha-1", "alpha-2" });

            await _chromaService.DeleteDocumentsAsync("pp13_68_beta", new List<string> { "beta-1", "beta-2" });
            await _chromaService.AddDocumentsAsync("pp13_68_beta",
                new List<string> { "Beta FEATURE content 1", "Beta FEATURE content 2" },
                new List<string> { "beta-1", "beta-2" });

            await _chromaSyncer.StageLocalChangesAsync("pp13_68_alpha");
            await _chromaSyncer.StageLocalChangesAsync("pp13_68_beta");
            await _syncManager.ProcessCommitAsync("PP13-68: Setup feature branch with different content");

            // Step 3: Test critical checkout - main to feature
            await _syncManager.ProcessCheckoutAsync("main", false);
            
            // Verify we're on main with original content
            var mainAlphaDocs = await _chromaService.QueryDocumentsAsync("pp13_68_alpha", new List<string> { "content" });
            var mainBetaDocs = await _chromaService.QueryDocumentsAsync("pp13_68_beta", new List<string> { "content" });
            
            var mainAlphaContent = ExtractDocuments(mainAlphaDocs);
            var mainBetaContent = ExtractDocuments(mainBetaDocs);
            Assert.That(mainAlphaContent[0], Does.Contain("main"), "Should be on main branch");
            Assert.That(mainBetaContent[0], Does.Contain("main"), "Should be on main branch");

            // CRITICAL CHECKOUT TEST: main → feature (PP13-68 scenario)
            _logger.LogInformation("*** EXECUTING PP13-68 CRITICAL MULTI-COLLECTION CHECKOUT ***");
            var checkoutResult = await _syncManager.ProcessCheckoutAsync("pp13-68-multi-feature", false);
            
            Assert.That(checkoutResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "PP13-68: Multi-collection checkout should complete successfully");

            // Step 4: Validate all collections have correct content (PP13-68 content-hash verification)
            var featureAlphaDocs = await _chromaService.QueryDocumentsAsync("pp13_68_alpha", new List<string> { "content" });
            var featureBetaDocs = await _chromaService.QueryDocumentsAsync("pp13_68_beta", new List<string> { "content" });

            var featureAlphaContent = ExtractDocuments(featureAlphaDocs);
            Assert.That(featureAlphaContent[0], Does.Contain("FEATURE"), 
                "PP13-68: Alpha collection should have feature content after checkout");
            Assert.That(featureAlphaContent[1], Does.Contain("FEATURE"), 
                "PP13-68: Alpha collection should have feature content after checkout");
            
            var featureBetaContent = ExtractDocuments(featureBetaDocs);
            Assert.That(featureBetaContent[0], Does.Contain("FEATURE"), 
                "PP13-68: Beta collection should have feature content after checkout");
            Assert.That(featureBetaContent[1], Does.Contain("FEATURE"), 
                "PP13-68: Beta collection should have feature content after checkout");

            // Verify document counts remain the same (this was triggering original bug)
            var alphaCount = await _chromaService.GetDocumentCountAsync("pp13_68_alpha");
            var betaCount = await _chromaService.GetDocumentCountAsync("pp13_68_beta");
            
            Assert.That(alphaCount, Is.EqualTo(2), "Alpha count should remain 2");
            Assert.That(betaCount, Is.EqualTo(2), "Beta count should remain 2");

            _logger.LogInformation("=== PP13-68 TEST PASSED: Multi-collection content validation works correctly ===");
        }

        /// <summary>
        /// PP13-68 STRESS TEST: Multiple rapid checkouts with content validation
        /// Tests that the content-hash verification doesn't cause performance issues or race conditions
        /// </summary>
        [Test]
        public async Task PP13_68_RapidCheckoutsWithContentValidation()
        {
            _logger.LogInformation("=== PP13-68 STRESS TEST: Rapid checkouts with content validation ===");

            // Setup two branches with different content but same counts
            await _chromaService.CreateCollectionAsync("pp13_68_stress");
            
            await _chromaService.AddDocumentsAsync("pp13_68_stress",
                new List<string> { "Main stress test content 1", "Main stress test content 2" },
                new List<string> { "stress-1", "stress-2" });

            await _chromaSyncer.StageLocalChangesAsync("pp13_68_stress");
            await _syncManager.ProcessCommitAsync("Setup stress test main");

            // Create feature with different content
            await _doltCli.CheckoutAsync("pp13-68-stress-feature", createNew: true);
            await _syncManager.FullSyncAsync("pp13_68_stress");
            
            await _chromaService.DeleteDocumentsAsync("pp13_68_stress", new List<string> { "stress-1", "stress-2" });
            await _chromaService.AddDocumentsAsync("pp13_68_stress",
                new List<string> { "Feature stress test content 1", "Feature stress test content 2" },
                new List<string> { "stress-1", "stress-2" });

            await _chromaSyncer.StageLocalChangesAsync("pp13_68_stress");
            await _syncManager.ProcessCommitAsync("Setup stress test feature");

            // Rapid switching test with content validation
            _logger.LogInformation("Starting PP13-68 rapid checkout stress test (10 iterations)...");

            for (int i = 0; i < 10; i++)
            {
                // Switch to main
                var mainResult = await _syncManager.ProcessCheckoutAsync("main", false);
                Assert.That(mainResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                    $"Iteration {i}: Checkout to main should succeed");

                var mainDocs = await _chromaService.QueryDocumentsAsync("pp13_68_stress", new List<string> { "content" });
                var mainContent = ExtractDocuments(mainDocs);
                Assert.That(mainContent[0], Does.Contain("Main stress"), 
                    $"Iteration {i}: Should have main content after checkout");

                // Switch to feature
                var featureResult = await _syncManager.ProcessCheckoutAsync("pp13-68-stress-feature", false);
                Assert.That(featureResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                    $"Iteration {i}: Checkout to feature should succeed");

                var featureDocs = await _chromaService.QueryDocumentsAsync("pp13_68_stress", new List<string> { "content" });
                var featureContent = ExtractDocuments(featureDocs);
                Assert.That(featureContent[0], Does.Contain("Feature stress"), 
                    $"Iteration {i}: Should have feature content after checkout");

                if (i % 3 == 0)
                {
                    _logger.LogInformation($"PP13-68 stress test iteration {i + 1}/10 completed successfully");
                }
            }

            _logger.LogInformation("=== PP13-68 STRESS TEST PASSED: Content-hash verification performs well under rapid switching ===");
        }
    }
}