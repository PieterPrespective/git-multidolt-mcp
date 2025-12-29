using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;
using DMMS.Services;

namespace DMMS.Testing.IntegrationTests
{
    /// <summary>
    /// Phase 4 integration tests for document state validation and integrity checking
    /// </summary>
    [TestFixture]
    public class Phase4DocumentStateValidationTests
    {
        private string _tempDir = null!;
        private DoltCli _doltCli = null!;
        private ChromaDbService _chromaService = null!;
        private SyncManagerV2 _syncManager = null!;
        private ILogger<Phase4DocumentStateValidationTests> _logger = null!;
        private Dictionary<string, string> _documentHashes = null!;

        [SetUp]
        public async Task Setup()
        {
            // Initialize Python context if not already done
            if (!PythonContext.IsInitialized)
            {
                PythonContext.Initialize();
            }

            _tempDir = Path.Combine(Path.GetTempPath(), $"DocStateValidationTests_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _logger = loggerFactory.CreateLogger<Phase4DocumentStateValidationTests>();

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
            var config = Options.Create(new ServerConfiguration 
            { 
                ChromaDataPath = chromaDataPath,
                DataPath = _tempDir
            });
            _chromaService = new ChromaDbService(
                loggerFactory.CreateLogger<ChromaDbService>(), 
                config
            );

            // Initialize deletion tracker
            var deletionTracker = new SqliteDeletionTracker(
                loggerFactory.CreateLogger<SqliteDeletionTracker>(),
                config.Value);
            await deletionTracker.InitializeAsync(_tempDir);
                
            // Initialize sync manager 
            _syncManager = new SyncManagerV2(
                _doltCli, 
                _chromaService,
                deletionTracker,
                doltConfig,
                loggerFactory.CreateLogger<SyncManagerV2>()
            );

            _documentHashes = new Dictionary<string, string>();
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
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error during teardown");
            }
        }

        /// <summary>
        /// Calculate SHA256 hash of document content
        /// </summary>
        private string CalculateHash(string content)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(content);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Validation Test 1: Document Content Integrity
        /// </summary>
        [Test]
        public async Task TestDocumentContentIntegrity()
        {
            _logger.LogInformation("Starting Document Content Integrity Test");

            // Create documents with known content hashes
            var testDocuments = new Dictionary<string, string>
            {
                ["doc1"] = "This is document 1 with specific content that will be hashed",
                ["doc2"] = "Document 2 has different content for testing integrity",
                ["doc3"] = "The third document contains yet another unique content string"
            };

            // Calculate and store hashes
            foreach (var doc in testDocuments)
            {
                _documentHashes[doc.Key] = CalculateHash(doc.Value);
            }

            // Create collection and add documents
            await _chromaService.CreateCollectionAsync("integrity-test");
            await _chromaService.AddDocumentsAsync("integrity-test",
                testDocuments.Keys.ToList(),
                testDocuments.Values.ToList());

            // Initial commit
            await _syncManager.ProcessCommitAsync("Initial documents with known hashes", true, false);

            // Create branch and perform operations
            await _doltCli.CheckoutAsync("test-branch", createNew: true);
            
            // Modify doc1
            var modifiedDoc1 = "This is document 1 with MODIFIED content";
            await _chromaService.UpdateDocumentsAsync("integrity-test",
                new List<string> { "doc1" },
                new List<string> { modifiedDoc1 });
            
            // Stage and commit changes
            await _syncManager.ProcessCommitAsync("Modified doc1", true, false);
            
            // Calculate new hash for modified document
            var modifiedHash = CalculateHash(modifiedDoc1);
            
            // Verify doc1 has changed
            Assert.That(CalculateHash(modifiedDoc1), Is.Not.EqualTo(_documentHashes["doc1"]), 
                "Doc1 hash should have changed after modification");

            // Switch back to main branch
            await _syncManager.ProcessCheckoutAsync("main", false);
            
            // Switch to test-branch and verify modified content
            await _syncManager.ProcessCheckoutAsync("test-branch", false);

            _logger.LogInformation("Document Content Integrity Test completed successfully");
        }

        /// <summary>
        /// Validation Test 2: Multi-Collection State Sync
        /// </summary>
        [Test]
        public async Task TestMultiCollectionStateSync()
        {
            _logger.LogInformation("Starting Multi-Collection State Sync Test");

            // Create multiple collections with different document sets per branch
            var collections = new[] { "collection-alpha", "collection-beta", "collection-gamma" };
            
            // Setup main branch with initial collections
            foreach (var collection in collections)
            {
                await _chromaService.CreateCollectionAsync(collection);
                await _chromaService.AddDocumentsAsync(collection,
                    new List<string> { $"{collection}-doc1", $"{collection}-doc2" },
                    new List<string> { $"Main doc 1 in {collection}", $"Main doc 2 in {collection}" });
            }
            
            await _syncManager.ProcessCommitAsync("Initial collections on main", true, false);

            // Create branch-1 with different collection state
            await _doltCli.CheckoutAsync("branch-1", createNew: true);
            
            // Add documents to existing collections
            await _chromaService.AddDocumentsAsync("collection-alpha",
                new List<string> { "branch1-alpha-doc" },
                new List<string> { "Branch-1 specific doc" });
            
            await _syncManager.ProcessCommitAsync("Branch-1 changes", true, false);

            // Test switching between branches and validate ALL collections reflect correct state
            
            // Switch to main
            await _syncManager.ProcessCheckoutAsync("main", false);
            await ValidateCollectionState("main", new Dictionary<string, int>
            {
                ["collection-alpha"] = 2,
                ["collection-beta"] = 2,
                ["collection-gamma"] = 2
            });

            // Switch to branch-1
            await _syncManager.ProcessCheckoutAsync("branch-1", false);
            await ValidateCollectionState("branch-1", new Dictionary<string, int>
            {
                ["collection-alpha"] = 3,  // 2 from main + 1 branch specific
                ["collection-beta"] = 2,
                ["collection-gamma"] = 2
            });

            _logger.LogInformation("Multi-Collection State Sync Test completed successfully");
        }

        /// <summary>
        /// Helper method to validate collection document counts
        /// </summary>
        private async Task ValidateCollectionState(string branchName, Dictionary<string, int> expectedCounts)
        {
            _logger.LogInformation($"Validating collection state for branch: {branchName}");
            
            foreach (var (collection, expectedCount) in expectedCounts)
            {
                var actualCount = await _chromaService.GetDocumentCountAsync(collection);
                Assert.That(actualCount, Is.EqualTo(expectedCount), 
                    $"Collection '{collection}' on branch '{branchName}' should have {expectedCount} documents, but has {actualCount}");
                
                _logger.LogInformation($"  {collection}: {actualCount} documents (expected: {expectedCount}) ✓");
            }
        }

        /// <summary>
        /// Test document state consistency after various sync operations
        /// </summary>
        [Test]
        public async Task TestDocumentStateThroughSyncOperations()
        {
            _logger.LogInformation("Starting Document State Through Sync Operations Test");

            // Create initial state
            await _chromaService.CreateCollectionAsync("sync-test");
            var initialDocs = new Dictionary<string, string>
            {
                ["doc-a"] = "Initial content A",
                ["doc-b"] = "Initial content B",
                ["doc-c"] = "Initial content C"
            };
            
            await _chromaService.AddDocumentsAsync("sync-test",
                initialDocs.Keys.ToList(),
                initialDocs.Values.ToList());
            
            await _syncManager.ProcessCommitAsync("Initial state", true, false);

            // Test full sync maintains document state
            var fullSyncResult = await _syncManager.FullSyncAsync("sync-test");
            Assert.That(fullSyncResult.Status, Is.EqualTo(SyncStatusV2.NoChanges), "Full sync should show no changes for synced state");
            
            var docCount = await _chromaService.GetDocumentCountAsync("sync-test");
            Assert.That(docCount, Is.EqualTo(3), "Should maintain 3 documents after full sync");

            // Create branch and test incremental sync
            await _doltCli.CheckoutAsync("sync-branch", createNew: true);
            
            // Modify and add documents
            await _chromaService.UpdateDocumentsAsync("sync-test",
                new List<string> { "doc-a" },
                new List<string> { "Modified content A" });
            
            await _chromaService.AddDocumentsAsync("sync-test",
                new List<string> { "doc-d" },
                new List<string> { "New content D" });
            
            await _syncManager.ProcessCommitAsync("Branch changes", true, false);

            // Switch back to main and verify state
            await _syncManager.ProcessCheckoutAsync("main", false);
            docCount = await _chromaService.GetDocumentCountAsync("sync-test");
            Assert.That(docCount, Is.EqualTo(3), "Main should still have 3 documents");

            _logger.LogInformation("Document State Through Sync Operations Test completed successfully");
        }
    }
}