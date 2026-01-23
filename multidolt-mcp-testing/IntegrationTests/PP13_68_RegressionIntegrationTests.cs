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
    /// CRITICAL regression integration tests for PP13-68 synchronization bug fixes
    /// These tests reproduce the exact PP13-68 scenario and validate the content-hash verification fix
    /// </summary>
    [TestFixture]
    public class PP13_68_RegressionIntegrationTests
    {
        private string _tempDir = null!;
        private DoltCli _doltCli = null!;
        private ChromaDbService _chromaService = null!;
        private SyncManagerV2 _syncManager = null!;
        private ChromaToDoltSyncer _chromaSyncer = null!;
        private ILogger<PP13_68_RegressionIntegrationTests> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            // Initialize Python context if not already done
            if (!PythonContext.IsInitialized)
            {
                PythonContext.Initialize();
            }

            _tempDir = Path.Combine(Path.GetTempPath(), $"PP13_68_RegressionTests_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _logger = loggerFactory.CreateLogger<PP13_68_RegressionIntegrationTests>();

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
            await deletionTracker.InitializeAsync(_tempDir);
                
            // Initialize services
            var chromaDetector = new ChromaToDoltDetector(_chromaService, _doltCli, deletionTracker, doltConfig, loggerFactory.CreateLogger<ChromaToDoltDetector>());
            _chromaSyncer = new ChromaToDoltSyncer(_chromaService, _doltCli, chromaDetector, loggerFactory.CreateLogger<ChromaToDoltSyncer>());
            
            // Create SyncManagerV2
            _syncManager = new SyncManagerV2(
                _doltCli,
                _chromaService,
                deletionTracker,        // IDeletionTracker
                deletionTracker,        // ISyncStateTracker (same object implements both interfaces)
                doltConfig,
                loggerFactory.CreateLogger<SyncManagerV2>()
            );
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

        [TearDown]
        public void TearDown()
        {
            try
            {
                // Cleanup Chroma collections created during tests
                var collections = _chromaService?.ListCollectionsAsync()?.GetAwaiter().GetResult();
                if (collections != null)
                {
                    foreach (var collection in collections.Where(c => c.StartsWith("pp13_68_")))
                    {
                        try
                        {
                            _chromaService.DeleteCollectionAsync(collection).GetAwaiter().GetResult();
                        }
                        catch { /* Ignore cleanup errors */ }
                    }
                }

                // Dispose ChromaService
                _chromaService?.Dispose();
                
                // Note: Directory cleanup will fail due to Python context locking files
                // This is expected behavior in the testing environment
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        /// <summary>
        /// CRITICAL TEST: PP13-68 Exact Reproduction - First checkout should update content despite same document count
        /// This is the primary bug scenario that PP13-68 was designed to fix
        /// 
        /// Scenario: 
        /// 1. Main branch has 2 documents with content A
        /// 2. Feature branch has 2 documents with same IDs but different content B  
        /// 3. First checkout from main → feature should update ChromaDB content to B
        /// 4. The bug was that count-based sync logic would skip sync because counts matched
        /// 5. The fix uses content-hash verification to detect content differences
        /// </summary>
        [Test]
        public async Task PP13_68_FirstCheckout_ShouldUpdateContent_ExactScenario()
        {
            _logger.LogInformation("=== CRITICAL PP13-68 REGRESSION TEST: First Checkout Content Update ===");
            
            const string collectionName = "pp13_68_exact_reproduction";
            
            // === PHASE 1: Setup Main Branch with Content A ===
            _logger.LogInformation("Phase 1: Setting up main branch with original content");
            
            await _chromaService.CreateCollectionAsync(collectionName);
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> 
                { 
                    "Original document 1 content - this is the main branch version",
                    "Original document 2 content - this is also the main branch version" 
                },
                new List<string> { "doc-1", "doc-2" });

            // Stage and commit on main
            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            await _syncManager.ProcessCommitAsync("PP13-68 Test: Initial content on main branch");
            
            var mainDocuments = await _chromaService.QueryDocumentsAsync(collectionName, new List<string> { "document" });
            var mainDocs = ExtractDocuments(mainDocuments);
            var mainContent1 = mainDocs[0];
            var mainContent2 = mainDocs[1];
            
            _logger.LogInformation($"Main branch content 1: '{mainContent1[..50]}...'");
            _logger.LogInformation($"Main branch content 2: '{mainContent2[..50]}...'");
            
            // === PHASE 2: Create Feature Branch with Same Count, Different Content ===
            _logger.LogInformation("Phase 2: Creating feature branch with different content (same count)");
            
            await _doltCli.CheckoutAsync("pp13-68-feature", createNew: true);
            
            // Sync to get main content first
            await _syncManager.FullSyncAsync(collectionName);
            
            // Replace content with different text but keep same IDs (this is the critical scenario)
            await _chromaService.DeleteDocumentsAsync(collectionName, new List<string> { "doc-1", "doc-2" });
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> 
                { 
                    "MODIFIED document 1 content - this is the FEATURE BRANCH version with different text",
                    "MODIFIED document 2 content - this is also the FEATURE BRANCH version with different text" 
                },
                new List<string> { "doc-1", "doc-2" });

            // Stage and commit on feature branch
            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            await _syncManager.ProcessCommitAsync("PP13-68 Test: Modified content on feature branch");
            
            var featureDocuments = await _chromaService.QueryDocumentsAsync(collectionName, new List<string> { "document" });
            var featureDocs = ExtractDocuments(featureDocuments);
            var featureContent1 = featureDocs[0];
            var featureContent2 = featureDocs[1];
            
            _logger.LogInformation($"Feature branch content 1: '{featureContent1[..50]}...'");
            _logger.LogInformation($"Feature branch content 2: '{featureContent2[..50]}...'");
            
            // === PHASE 3: Critical Test - First checkout main → feature ===
            _logger.LogInformation("Phase 3: CRITICAL TEST - First checkout from main to feature branch");
            
            // Go back to main first 
            var checkoutMainResult = await _syncManager.ProcessCheckoutAsync("main", false);
            Assert.That(checkoutMainResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "Checkout to main should succeed");
            
            // Verify we're on main with main content
            var documentsOnMain = await _chromaService.QueryDocumentsAsync(collectionName, new List<string> { "document" });
            var mainDocsAfterCheckout = ExtractDocuments(documentsOnMain);
            var currentContent1 = mainDocsAfterCheckout[0];
            var currentContent2 = mainDocsAfterCheckout[1];
            
            Assert.That(currentContent1, Does.Contain("Original document 1"), 
                "Should be on main branch with original content before critical test");
            Assert.That(currentContent2, Does.Contain("Original document 2"), 
                "Should be on main branch with original content before critical test");
            
            _logger.LogInformation("Verified: Currently on main with original content");
            
            // === THE CRITICAL MOMENT: First checkout to feature branch ===
            _logger.LogInformation("*** EXECUTING CRITICAL FIRST CHECKOUT: main → feature ***");
            
            var checkoutResult = await _syncManager.ProcessCheckoutAsync("pp13-68-feature", false);
            
            Assert.That(checkoutResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "PP13-68 Critical: First checkout should complete successfully");
            
            // === PHASE 4: Validate Fix - Content Should Be Updated ===
            _logger.LogInformation("Phase 4: Validating PP13-68 fix - content should be updated");
            
            var documentsAfterCheckout = await _chromaService.QueryDocumentsAsync(collectionName, new List<string> { "document" });
            var finalDocs = ExtractDocuments(documentsAfterCheckout);
            var finalContent1 = finalDocs[0];
            var finalContent2 = finalDocs[1];
            
            _logger.LogInformation($"After checkout - Content 1: '{finalContent1[..50]}...'");
            _logger.LogInformation($"After checkout - Content 2: '{finalContent2[..50]}...'");
            
            // CRITICAL ASSERTIONS: These validate the PP13-68 fix
            Assert.That(finalContent1, Does.Contain("MODIFIED document 1"), 
                "PP13-68 CRITICAL: First checkout should update content to feature branch version (content-hash fix should detect difference despite same count)");
            Assert.That(finalContent2, Does.Contain("MODIFIED document 2"), 
                "PP13-68 CRITICAL: First checkout should update content to feature branch version (content-hash fix should detect difference despite same count)");
            
            Assert.That(finalContent1, Does.Not.Contain("Original document 1"), 
                "PP13-68 CRITICAL: Content should no longer be main branch version after checkout");
            Assert.That(finalContent2, Does.Not.Contain("Original document 2"), 
                "PP13-68 CRITICAL: Content should no longer be main branch version after checkout");
            
            // Verify document count remains the same (this was the original false positive trigger)
            var finalCount = await _chromaService.GetDocumentCountAsync(collectionName);
            Assert.That(finalCount, Is.EqualTo(2), 
                "Document count should remain 2 (this identical count was causing the original PP13-68 bug)");
            
            _logger.LogInformation("=== PP13-68 REGRESSION TEST PASSED: Content-hash verification successfully detected content difference despite identical count ===");
        }

        /// <summary>
        /// Test that subsequent checkouts continue working correctly after the fix
        /// This ensures the fix doesn't break normal operation
        /// </summary>
        [Test]
        public async Task PP13_68_SubsequentCheckouts_ShouldContinueWorking()
        {
            _logger.LogInformation("=== PP13-68 TEST: Subsequent checkouts should continue working ===");
            
            const string collectionName = "pp13_68_subsequent_test";
            
            // Setup similar to the main test
            await _chromaService.CreateCollectionAsync(collectionName);
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> { "Main content A", "Main content B" },
                new List<string> { "doc-a", "doc-b" });

            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            await _syncManager.ProcessCommitAsync("Setup main");
            
            // Create feature branch with different content
            await _doltCli.CheckoutAsync("feature-subsequent", createNew: true);
            await _syncManager.FullSyncAsync(collectionName);
            
            await _chromaService.DeleteDocumentsAsync(collectionName, new List<string> { "doc-a", "doc-b" });
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> { "Feature content A", "Feature content B" },
                new List<string> { "doc-a", "doc-b" });

            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            await _syncManager.ProcessCommitAsync("Setup feature");
            
            // Test multiple checkout cycles
            for (int i = 0; i < 3; i++)
            {
                _logger.LogInformation($"Testing checkout cycle {i + 1}");
                
                // Checkout to main
                var mainResult = await _syncManager.ProcessCheckoutAsync("main", false);
                Assert.That(mainResult.Status, Is.EqualTo(SyncStatusV2.Completed));
                
                var mainDocsResult = await _chromaService.QueryDocumentsAsync(collectionName, new List<string> { "content" });
                var mainDocs = ExtractDocuments(mainDocsResult);
                Assert.That(mainDocs[0], Does.Contain("Main content"));
                
                // Checkout to feature
                var featureResult = await _syncManager.ProcessCheckoutAsync("feature-subsequent", false);
                Assert.That(featureResult.Status, Is.EqualTo(SyncStatusV2.Completed));
                
                var featureDocsResult = await _chromaService.QueryDocumentsAsync(collectionName, new List<string> { "content" });
                var featureDocs = ExtractDocuments(featureDocsResult);
                Assert.That(featureDocs[0], Does.Contain("Feature content"));
            }
            
            _logger.LogInformation("=== PP13-68 TEST PASSED: Subsequent checkouts working correctly ===");
        }

        /// <summary>
        /// Test that content-hash verification works with various document sizes and types
        /// This ensures the fix is robust across different content scenarios
        /// </summary>
        [Test]
        public async Task PP13_68_ContentHashVerification_VariousContentSizes()
        {
            _logger.LogInformation("=== PP13-68 TEST: Content-hash verification with various content sizes ===");
            
            const string collectionName = "pp13_68_content_sizes";
            
            await _chromaService.CreateCollectionAsync(collectionName);
            
            // Test with small, medium, and large content
            var smallContent = "Small content";
            var mediumContent = string.Join(" ", Enumerable.Repeat("Medium content with more text to make it longer.", 10));
            var largeContent = string.Join(" ", Enumerable.Repeat("Large content with much more text repeated many times to create a substantial document.", 50));
            
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> { smallContent, mediumContent, largeContent },
                new List<string> { "small", "medium", "large" });

            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            await _syncManager.ProcessCommitAsync("Setup various sizes on main");
            
            // Create feature branch with modified content (same sizes, different text)
            await _doltCli.CheckoutAsync("feature-sizes", createNew: true);
            await _syncManager.FullSyncAsync(collectionName);
            
            var modifiedSmall = "Small MODIFIED content";
            var modifiedMedium = string.Join(" ", Enumerable.Repeat("Modified medium content with different text to make it longer.", 10));
            var modifiedLarge = string.Join(" ", Enumerable.Repeat("Modified large content with completely different text repeated many times.", 50));
            
            await _chromaService.DeleteDocumentsAsync(collectionName, new List<string> { "small", "medium", "large" });
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> { modifiedSmall, modifiedMedium, modifiedLarge },
                new List<string> { "small", "medium", "large" });

            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            await _syncManager.ProcessCommitAsync("Setup modified sizes on feature");
            
            // Test checkout detects all content differences
            await _syncManager.ProcessCheckoutAsync("main", false);
            
            var beforeDocsResult = await _chromaService.QueryDocumentsAsync(collectionName, new List<string> { "content" });
            var beforeDocs = ExtractDocuments(beforeDocsResult);
            Assert.That(beforeDocs[0], Does.Not.Contain("MODIFIED"));
            
            await _syncManager.ProcessCheckoutAsync("feature-sizes", false);
            
            var afterDocsResult = await _chromaService.QueryDocumentsAsync(collectionName, new List<string> { "content" });
            var afterDocs = ExtractDocuments(afterDocsResult);
            Assert.That(afterDocs[0], Does.Contain("MODIFIED"), 
                "Content-hash verification should detect difference in small content");
            Assert.That(afterDocs[1], Does.Contain("Modified"), 
                "Content-hash verification should detect difference in medium content");
            Assert.That(afterDocs[2], Does.Contain("Modified"), 
                "Content-hash verification should detect difference in large content");
            
            _logger.LogInformation("=== PP13-68 TEST PASSED: Content-hash verification works with various content sizes ===");
        }
    }
}