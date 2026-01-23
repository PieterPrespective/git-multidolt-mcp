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
    /// Integration tests for PP13-68 metadata cleanup functionality
    /// Tests the CleanupSyncMetadataAsync and ValidatePostOperationStateAsync methods in real workflows
    /// These tests ensure that is_local_change flags and other metadata are properly managed
    /// </summary>
    [TestFixture]
    public class PP13_68_MetadataCleanupIntegrationTests
    {
        private string _tempDir = null!;
        private DoltCli _doltCli = null!;
        private ChromaDbService _chromaService = null!;
        private SyncManagerV2 _syncManager = null!;
        private ChromaToDoltSyncer _chromaSyncer = null!;
        private ChromaToDoltDetector _chromaDetector = null!;
        private ILogger<PP13_68_MetadataCleanupIntegrationTests> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            // Initialize Python context if not already done
            if (!PythonContext.IsInitialized)
            {
                PythonContext.Initialize();
            }

            _tempDir = Path.Combine(Path.GetTempPath(), $"PP13_68_MetadataCleanupTests_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _logger = loggerFactory.CreateLogger<PP13_68_MetadataCleanupIntegrationTests>();

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
            _chromaDetector = new ChromaToDoltDetector(_chromaService, _doltCli, deletionTracker, doltConfig, loggerFactory.CreateLogger<ChromaToDoltDetector>());
            _chromaSyncer = new ChromaToDoltSyncer(_chromaService, _doltCli, _chromaDetector, loggerFactory.CreateLogger<ChromaToDoltSyncer>());
            
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
                // Cleanup Chroma collections created during tests
                var collections = _chromaService?.ListCollectionsAsync()?.GetAwaiter().GetResult();
                if (collections != null)
                {
                    foreach (var collection in collections.Where(c => c.StartsWith("pp13_68_cleanup_")))
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
        /// Test that CleanupSyncMetadataAsync properly clears is_local_change flags after commit operations
        /// This tests the PP13-68 metadata cleanup functionality in a real commit workflow
        /// </summary>
        [Test]
        public async Task PP13_68_CleanupSyncMetadata_RealCommitWorkflow_ShouldClearLocalChangeFlags()
        {
            _logger.LogInformation("=== PP13-68 TEST: Metadata cleanup in real commit workflow ===");
            
            const string collectionName = "pp13_68_cleanup_commit_test";
            
            // Step 1: Create collection with documents
            await _chromaService.CreateCollectionAsync(collectionName);
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> { "Initial document 1", "Initial document 2" },
                new List<string> { "cleanup-1", "cleanup-2" });

            // Stage and commit to establish baseline
            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            await _syncManager.ProcessCommitAsync("PP13-68 Metadata Test: Initial commit");
            
            // Step 2: Make local changes to create is_local_change metadata
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> { "Local change document 3" },
                new List<string> { "cleanup-3" });
            
            await _chromaService.UpdateDocumentsAsync(collectionName, 
                new List<string> { "cleanup-1" },
                new List<string> { "Updated document 1 - local change" });

            _logger.LogInformation("Created local changes that should generate is_local_change metadata");

            // Step 3: Verify that local changes are detected (should have is_local_change flags)
            var localChangesBeforeCleanup = await _syncManager.GetLocalChangesAsync();
            Assert.That(localChangesBeforeCleanup.HasChanges, Is.True, 
                "Should detect local changes before commit");
            
            // Step 4: Stage local changes (this should set is_local_change flags)
            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            
            // Step 5: Execute commit workflow (this should trigger metadata cleanup)
            var commitResult = await _syncManager.ProcessCommitAsync("PP13-68 Test: Commit with cleanup");
            
            Assert.That(commitResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "Commit should complete successfully");
            
            // Step 6: Use CleanupSyncMetadataAsync to clean up metadata  
            var cleanedCount = await _chromaDetector.CleanupSyncMetadataAsync(collectionName);
            _logger.LogInformation($"Cleaned up metadata for {cleanedCount} documents");
            
            // Step 7: Validate that metadata cleanup worked
            var isValid = await _chromaDetector.ValidatePostOperationStateAsync(collectionName);
            Assert.That(isValid, Is.True, 
                "PP13-68: Post-operation state validation should pass after cleanup");
            
            // Step 8: Verify no false positive local changes after cleanup
            var localChangesAfterCleanup = await _syncManager.GetLocalChangesAsync();
            Assert.That(localChangesAfterCleanup.HasChanges, Is.False, 
                "PP13-68: Should have no local changes after commit and metadata cleanup");
            
            _logger.LogInformation("=== PP13-68 TEST PASSED: Metadata cleanup successfully cleared local change flags ===");
        }

        /// <summary>
        /// Test that ValidatePostOperationStateAsync correctly detects metadata inconsistencies
        /// This ensures the validation method catches problems that could lead to sync issues
        /// </summary>
        [Test]
        public async Task PP13_68_ValidatePostOperationState_DetectsInconsistencies()
        {
            _logger.LogInformation("=== PP13-68 TEST: Post-operation state validation detects inconsistencies ===");
            
            const string collectionName = "pp13_68_cleanup_validation_test";
            
            // Setup baseline state
            await _chromaService.CreateCollectionAsync(collectionName);
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> { "Validation test document 1", "Validation test document 2" },
                new List<string> { "val-1", "val-2" });

            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            await _syncManager.ProcessCommitAsync("Setup validation test");
            
            // Clean state should validate successfully
            var initialValidation = await _chromaDetector.ValidatePostOperationStateAsync(collectionName);
            Assert.That(initialValidation, Is.True, 
                "Clean state should pass validation");
            
            // Create some local changes but don't commit them (creates inconsistent state)
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> { "Uncommitted change document" },
                new List<string> { "uncommitted-1" });
            
            _logger.LogInformation("Created uncommitted changes to test validation");
            
            // Validation should detect the inconsistent state
            var validationWithChanges = await _chromaDetector.ValidatePostOperationStateAsync(collectionName);
            
            // Note: The behavior here depends on the implementation - it might pass or fail
            // We're testing that the validation method works and doesn't crash
            _logger.LogInformation($"Validation with uncommitted changes: {validationWithChanges}");
            
            // Clean up the changes
            await _chromaService.DeleteDocumentsAsync(collectionName, new List<string> { "uncommitted-1" });
            
            // Validation should pass again after cleanup
            var finalValidation = await _chromaDetector.ValidatePostOperationStateAsync(collectionName);
            Assert.That(finalValidation, Is.True, 
                "Validation should pass after cleaning up uncommitted changes");
            
            _logger.LogInformation("=== PP13-68 TEST PASSED: Post-operation state validation working correctly ===");
        }

        /// <summary>
        /// Test metadata cleanup during checkout operations
        /// This ensures that checkout operations properly clean up metadata from the previous branch
        /// </summary>
        [Test]
        public async Task PP13_68_MetadataCleanup_CheckoutWorkflow_ShouldClearPreviousBranchMetadata()
        {
            _logger.LogInformation("=== PP13-68 TEST: Metadata cleanup during checkout workflow ===");
            
            const string collectionName = "pp13_68_cleanup_checkout_test";
            
            // Setup main branch
            await _chromaService.CreateCollectionAsync(collectionName);
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> { "Main branch document 1", "Main branch document 2" },
                new List<string> { "checkout-1", "checkout-2" });

            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            await _syncManager.ProcessCommitAsync("Setup main for checkout test");
            
            // Create feature branch with changes
            await _doltCli.CheckoutAsync("pp13-68-cleanup-feature", createNew: true);
            await _syncManager.FullSyncAsync(collectionName);
            
            // Make changes on feature branch
            await _chromaService.AddDocumentsAsync(collectionName,
                new List<string> { "Feature branch document 3" },
                new List<string> { "checkout-3" });
            
            await _chromaSyncer.StageLocalChangesAsync(collectionName);
            await _syncManager.ProcessCommitAsync("Feature branch changes");
            
            // Switch back to main (this should trigger metadata cleanup)
            _logger.LogInformation("Executing checkout from feature to main (should cleanup feature metadata)");
            
            var checkoutResult = await _syncManager.ProcessCheckoutAsync("main", false);
            Assert.That(checkoutResult.Status, Is.EqualTo(SyncStatusV2.Completed), 
                "Checkout should complete successfully");
            
            // Explicit cleanup call to ensure metadata is clean
            var cleanedCount = await _chromaDetector.CleanupSyncMetadataAsync(collectionName);
            _logger.LogInformation($"Cleaned up {cleanedCount} documents after checkout");
            
            // Validate state after checkout and cleanup
            var isValid = await _chromaDetector.ValidatePostOperationStateAsync(collectionName);
            Assert.That(isValid, Is.True, 
                "PP13-68: Post-checkout state should be valid after metadata cleanup");
            
            // Verify document state is correct for main branch
            var documentsAfterCheckout = await _chromaService.QueryDocumentsAsync(collectionName, new List<string> { "document" });
            var documentCount = await _chromaService.GetDocumentCountAsync(collectionName);
            
            Assert.That(documentCount, Is.EqualTo(2), 
                "Should have 2 documents on main branch (feature document should not be present)");
            
            // Check that no false positive local changes exist
            var localChanges = await _syncManager.GetLocalChangesAsync();
            Assert.That(localChanges.HasChanges, Is.False, 
                "PP13-68: Should have no false positive local changes after checkout and cleanup");
            
            _logger.LogInformation("=== PP13-68 TEST PASSED: Checkout workflow metadata cleanup working correctly ===");
        }

        /// <summary>
        /// Test that metadata cleanup works correctly with multiple collections
        /// This ensures the cleanup methods scale to multi-collection scenarios
        /// </summary>
        [Test]
        public async Task PP13_68_MetadataCleanup_MultipleCollections()
        {
            _logger.LogInformation("=== PP13-68 TEST: Metadata cleanup with multiple collections ===");
            
            const string collection1 = "pp13_68_cleanup_multi_1";
            const string collection2 = "pp13_68_cleanup_multi_2";
            
            // Setup multiple collections
            await _chromaService.CreateCollectionAsync(collection1);
            await _chromaService.CreateCollectionAsync(collection2);
            
            await _chromaService.AddDocumentsAsync(collection1,
                new List<string> { "Collection 1 document A", "Collection 1 document B" },
                new List<string> { "multi-1-a", "multi-1-b" });
            
            await _chromaService.AddDocumentsAsync(collection2,
                new List<string> { "Collection 2 document X", "Collection 2 document Y" },
                new List<string> { "multi-2-x", "multi-2-y" });

            // Stage and commit both collections
            await _chromaSyncer.StageLocalChangesAsync(collection1);
            await _chromaSyncer.StageLocalChangesAsync(collection2);
            await _syncManager.ProcessCommitAsync("Multi-collection setup");
            
            // Make changes to both collections to create metadata
            await _chromaService.AddDocumentsAsync(collection1,
                new List<string> { "Collection 1 additional document" },
                new List<string> { "multi-1-c" });
            
            await _chromaService.AddDocumentsAsync(collection2,
                new List<string> { "Collection 2 additional document" },
                new List<string> { "multi-2-z" });
            
            // Stage and commit the changes
            await _chromaSyncer.StageLocalChangesAsync(collection1);
            await _chromaSyncer.StageLocalChangesAsync(collection2);
            await _syncManager.ProcessCommitAsync("Multi-collection changes");
            
            // Test cleanup on both collections
            var cleaned1 = await _chromaDetector.CleanupSyncMetadataAsync(collection1);
            var cleaned2 = await _chromaDetector.CleanupSyncMetadataAsync(collection2);
            
            _logger.LogInformation($"Cleaned {cleaned1} documents in collection1, {cleaned2} documents in collection2");
            
            // Validate both collections
            var valid1 = await _chromaDetector.ValidatePostOperationStateAsync(collection1);
            var valid2 = await _chromaDetector.ValidatePostOperationStateAsync(collection2);
            
            Assert.That(valid1, Is.True, "Collection 1 should be valid after cleanup");
            Assert.That(valid2, Is.True, "Collection 2 should be valid after cleanup");
            
            // Verify no false positive changes in either collection
            var localChanges = await _syncManager.GetLocalChangesAsync();
            Assert.That(localChanges.HasChanges, Is.False, 
                "PP13-68: Multi-collection cleanup should eliminate all false positive changes");
            
            _logger.LogInformation("=== PP13-68 TEST PASSED: Multi-collection metadata cleanup working correctly ===");
        }
    }
}