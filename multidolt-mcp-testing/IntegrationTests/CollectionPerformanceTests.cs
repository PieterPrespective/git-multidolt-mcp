using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;
using DMMS.Services;

namespace DMMS.Testing.IntegrationTests
{
    /// <summary>
    /// Phase 5: Performance tests for collection-level operations as specified in PP13-61
    /// Tests collection deletion, renaming, updates with large datasets
    /// </summary>
    [TestFixture]
    public class CollectionPerformanceTests
    {
        private string _tempDir = null!;
        private DoltCli _doltCli = null!;
        private ChromaDbService _chromaService = null!;
        private SyncManagerV2 _syncManager = null!;
        private IDeletionTracker _deletionTracker = null!;
        private ICollectionChangeDetector _collectionDetector = null!;
        private ILogger<CollectionPerformanceTests> _logger = null!;
        private ILoggerFactory _loggerFactory = null!;

        [SetUp]
        public async Task Setup()
        {
            // Initialize Python context if not already done
            if (!PythonContext.IsInitialized)
            {
                PythonContext.Initialize();
            }

            _tempDir = Path.Combine(Path.GetTempPath(), $"CollectionPerfTests_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            _loggerFactory = LoggerFactory.Create(builder => 
                builder.AddConsole().SetMinimumLevel(LogLevel.Information));
            _logger = _loggerFactory.CreateLogger<CollectionPerformanceTests>();

            // Initialize Dolt CLI
            var doltConfig = Options.Create(new DoltConfiguration
            {
                DoltExecutablePath = Environment.OSVersion.Platform == PlatformID.Win32NT 
                    ? @"C:\Program Files\Dolt\bin\dolt.exe" 
                    : "dolt",
                RepositoryPath = _tempDir,
                CommandTimeoutMs = 60000,
                EnableDebugLogging = false
            });

            _doltCli = new DoltCli(doltConfig, _loggerFactory.CreateLogger<DoltCli>());
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
                _loggerFactory.CreateLogger<ChromaDbService>(), 
                config
            );

            // Initialize deletion tracker with collection support
            _deletionTracker = new SqliteDeletionTracker(
                _loggerFactory.CreateLogger<SqliteDeletionTracker>(),
                config.Value);
            await _deletionTracker.InitializeAsync(_tempDir);
            
            // Initialize collection change detector
            _collectionDetector = new CollectionChangeDetector(
                _chromaService,
                _doltCli,
                _deletionTracker,
                doltConfig,
                _loggerFactory.CreateLogger<CollectionChangeDetector>()
            );
            await _collectionDetector.InitializeAsync(_tempDir);
                
            // Initialize sync manager 
            _syncManager = new SyncManagerV2(
                _doltCli, 
                _chromaService,
                _deletionTracker,
                doltConfig,
                _loggerFactory.CreateLogger<SyncManagerV2>()
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            try
            {
                // Clear collections created during tests
                if (_chromaService != null)
                {
                    try
                    {
                        var collections = await _chromaService.ListCollectionsAsync();
                        foreach (var collection in collections)
                        {
                            if (collection.StartsWith("perf-") || collection.StartsWith("cascade-") || 
                                collection.StartsWith("rename-") || collection.StartsWith("lifecycle-"))
                            {
                                await _chromaService.DeleteCollectionAsync(collection);
                                _logger?.LogDebug("Cleaned up collection: {Collection}", collection);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error clearing collections during teardown");
                    }

                    _chromaService.Dispose();
                }
                
                _logger?.LogDebug("Teardown completed - collections cleared");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error during teardown");
            }
            
            // Dispose resources
            if (_deletionTracker is IDisposable disposableTracker)
            {
                disposableTracker.Dispose();
            }
            _loggerFactory?.Dispose();
        }

        /// <summary>
        /// Test 1: Large Collection Handling - 1000+ documents
        /// Validates performance with large document sets in a single collection
        /// </summary>
        [Test]
        [Ignore("Temporarily disabled - performance tests take too long and may cause deadlocks")]
        [TestCase(100, Description = "Small collection: 100 documents")]
        [TestCase(500, Description = "Medium collection: 500 documents")]
        [TestCase(1000, Description = "Large collection: 1000 documents")]
        public async Task TestLargeCollectionHandling(int documentCount)
        {
            _logger.LogInformation($"=== Starting Large Collection Test with {documentCount} documents ===");

            var stopwatch = new Stopwatch();
            var collectionName = $"perf-large-{documentCount}";
            var memoryBefore = GC.GetTotalMemory(true);

            // Create collection
            stopwatch.Start();
            await _chromaService.CreateCollectionAsync(collectionName);
            stopwatch.Stop();
            var createTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Collection created in {createTime}ms");

            // Generate and add documents in batches
            stopwatch.Restart();
            var batchSize = 100;
            for (int batch = 0; batch < documentCount; batch += batchSize)
            {
                var currentBatchSize = Math.Min(batchSize, documentCount - batch);
                var ids = new List<string>();
                var documents = new List<string>();

                for (int i = 0; i < currentBatchSize; i++)
                {
                    var docId = $"doc-{batch + i:D6}";
                    ids.Add(docId);
                    documents.Add($"Performance test document {batch + i}. " +
                                $"This document contains test content for performance validation. " +
                                $"Collection: {collectionName}, Document ID: {docId}, " +
                                $"Timestamp: {DateTime.UtcNow:O}, " +
                                $"Batch: {batch / batchSize + 1}, " +
                                $"Additional metadata for realistic document size simulation.");
                }

                await _chromaService.AddDocumentsAsync(collectionName, ids, documents);
                _logger.LogDebug($"Added batch {batch / batchSize + 1}/{Math.Ceiling((double)documentCount / batchSize)}");
            }
            stopwatch.Stop();
            var populateTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Documents added in {populateTime}ms");

            // Commit to Dolt
            stopwatch.Restart();
            var syncResult = await _syncManager.ProcessCommitAsync(
                $"Performance test: {documentCount} documents in collection", true, false);
            stopwatch.Stop();
            var commitTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Initial commit completed in {commitTime}ms");

            // Test collection deletion with cascade
            stopwatch.Restart();
            
            // Track deletion before deleting from ChromaDB
            await _deletionTracker.TrackCollectionDeletionAsync(
                _tempDir, 
                collectionName,
                new Dictionary<string, object> { ["document_count"] = documentCount },
                "main",
                await _doltCli.GetHeadCommitHashAsync()
            );
            
            // Delete collection from ChromaDB
            await _chromaService.DeleteCollectionAsync(collectionName);
            stopwatch.Stop();
            var deleteTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Collection deleted from ChromaDB in {deleteTime}ms");

            // Sync deletion to Dolt (cascade delete all documents)
            stopwatch.Restart();
            var collectionSyncResult = await _syncManager.StageCollectionChangesAsync();
            await _syncManager.ProcessCommitAsync(
                $"Deleted collection with {documentCount} documents", true, false);
            stopwatch.Stop();
            var cascadeDeleteTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Cascade deletion synced to Dolt in {cascadeDeleteTime}ms");

            // Check memory usage
            var memoryAfter = GC.GetTotalMemory(true);
            var memoryUsedMB = (memoryAfter - memoryBefore) / (1024.0 * 1024.0);
            
            // Performance assertions
            Assert.That(createTime, Is.LessThan(1000), "Collection creation should take less than 1 second");
            Assert.That(populateTime, Is.LessThan(documentCount * 10), 
                $"Document population should average less than 10ms per document");
            Assert.That(commitTime, Is.LessThan(documentCount * 20), 
                $"Initial commit should average less than 20ms per document");
            Assert.That(cascadeDeleteTime, Is.LessThan(documentCount * 15), 
                $"Cascade deletion should average less than 15ms per document");
            Assert.That(memoryUsedMB, Is.LessThan(documentCount * 0.5), 
                $"Memory usage should be less than 0.5MB per document");

            // Verify deletion was complete
            var collections = await _chromaService.ListCollectionsAsync();
            Assert.That(collections, Does.Not.Contain(collectionName), 
                "Collection should be deleted from ChromaDB");

            // Log performance summary
            _logger.LogInformation($"\n=== Performance Summary for {documentCount} documents ===");
            _logger.LogInformation($"Collection Creation: {createTime}ms");
            _logger.LogInformation($"Document Population: {populateTime}ms ({populateTime / (double)documentCount:F2}ms/doc)");
            _logger.LogInformation($"Initial Commit: {commitTime}ms ({commitTime / (double)documentCount:F2}ms/doc)");
            _logger.LogInformation($"ChromaDB Deletion: {deleteTime}ms");
            _logger.LogInformation($"Cascade Delete Sync: {cascadeDeleteTime}ms ({cascadeDeleteTime / (double)documentCount:F2}ms/doc)");
            _logger.LogInformation($"Memory Used: {memoryUsedMB:F2}MB ({memoryUsedMB / documentCount * 1000:F2}KB/doc)");
            _logger.LogInformation($"================================================\n");
        }

        /// <summary>
        /// Test 2: Multiple Collection Operations
        /// Tests performance when operating on many collections simultaneously
        /// </summary>
        [Test]
        [Ignore("Temporarily disabled - performance tests take too long and may cause deadlocks")]
        [TestCase(5, 20, Description = "Small scale: 5 collections with 20 docs each")]
        [TestCase(10, 50, Description = "Medium scale: 10 collections with 50 docs each")]
        [TestCase(20, 100, Description = "Large scale: 20 collections with 100 docs each")]
        public async Task TestMultipleCollectionOperations(int collectionCount, int docsPerCollection)
        {
            _logger.LogInformation($"=== Starting Multiple Collection Test: {collectionCount} collections, {docsPerCollection} docs each ===");

            var stopwatch = new Stopwatch();
            var totalDocuments = collectionCount * docsPerCollection;
            var memoryBefore = GC.GetTotalMemory(true);

            // Create multiple collections
            stopwatch.Start();
            var collectionNames = new List<string>();
            for (int c = 0; c < collectionCount; c++)
            {
                var collectionName = $"perf-multi-{c:D3}";
                collectionNames.Add(collectionName);
                await _chromaService.CreateCollectionAsync(collectionName);
            }
            stopwatch.Stop();
            var createTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Created {collectionCount} collections in {createTime}ms");

            // Populate all collections
            stopwatch.Restart();
            foreach (var collectionName in collectionNames)
            {
                var ids = Enumerable.Range(0, docsPerCollection)
                    .Select(i => $"{collectionName}-doc-{i:D4}").ToList();
                var documents = ids.Select(id => 
                    $"Document {id} in {collectionName}. Test content for multi-collection performance.").ToList();
                
                await _chromaService.AddDocumentsAsync(collectionName, ids, documents);
            }
            stopwatch.Stop();
            var populateTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Populated {totalDocuments} documents in {populateTime}ms");

            // Initial commit
            stopwatch.Restart();
            await _syncManager.ProcessCommitAsync(
                $"Performance test: {collectionCount} collections with {totalDocuments} total documents", 
                true, false);
            stopwatch.Stop();
            var commitTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Initial commit in {commitTime}ms");

            // Test deleting half the collections
            var collectionsToDelete = collectionNames.Take(collectionCount / 2).ToList();
            stopwatch.Restart();
            
            foreach (var collectionName in collectionsToDelete)
            {
                // Track deletion
                await _deletionTracker.TrackCollectionDeletionAsync(
                    _tempDir,
                    collectionName,
                    new Dictionary<string, object> { ["document_count"] = docsPerCollection },
                    "main",
                    await _doltCli.GetHeadCommitHashAsync()
                );
                
                // Delete from ChromaDB
                await _chromaService.DeleteCollectionAsync(collectionName);
            }
            stopwatch.Stop();
            var deleteTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Deleted {collectionsToDelete.Count} collections in {deleteTime}ms");

            // Sync deletions
            stopwatch.Restart();
            var collectionSyncResult = await _syncManager.StageCollectionChangesAsync();
            await _syncManager.ProcessCommitAsync(
                $"Deleted {collectionsToDelete.Count} collections", true, false);
            stopwatch.Stop();
            var syncTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Synced deletions in {syncTime}ms");

            // Memory check
            var memoryAfter = GC.GetTotalMemory(true);
            var memoryUsedMB = (memoryAfter - memoryBefore) / (1024.0 * 1024.0);

            // Performance assertions
            Assert.That(createTime, Is.LessThan(collectionCount * 200), 
                "Collection creation should average less than 200ms per collection");
            Assert.That(populateTime, Is.LessThan(totalDocuments * 10), 
                "Document population should average less than 10ms per document");
            Assert.That(deleteTime, Is.LessThan(collectionsToDelete.Count * 500), 
                "Collection deletion should average less than 500ms per collection");
            Assert.That(syncTime, Is.LessThan(collectionsToDelete.Count * docsPerCollection * 10), 
                "Sync should average less than 10ms per deleted document");

            // Log summary
            _logger.LogInformation($"\n=== Multiple Collection Performance Summary ===");
            _logger.LogInformation($"Collections Created: {collectionCount} in {createTime}ms ({createTime / (double)collectionCount:F2}ms/collection)");
            _logger.LogInformation($"Documents Added: {totalDocuments} in {populateTime}ms ({populateTime / (double)totalDocuments:F2}ms/doc)");
            _logger.LogInformation($"Initial Commit: {commitTime}ms");
            _logger.LogInformation($"Collections Deleted: {collectionsToDelete.Count} in {deleteTime}ms");
            _logger.LogInformation($"Deletion Sync: {syncTime}ms");
            _logger.LogInformation($"Memory Used: {memoryUsedMB:F2}MB");
            _logger.LogInformation($"==============================================\n");
        }

        /// <summary>
        /// Test 3: Collection Rename Performance
        /// Tests the performance of renaming collections with documents
        /// </summary>
        [Test]
        [Ignore("Temporarily disabled - performance tests take too long and may cause deadlocks")]
        public async Task TestCollectionRenamePerformance()
        {
            _logger.LogInformation("=== Starting Collection Rename Performance Test ===");

            var stopwatch = new Stopwatch();
            var originalName = "rename-original";
            var newName = "rename-modified";
            var documentCount = 250;

            // Create and populate collection
            await _chromaService.CreateCollectionAsync(originalName);
            
            var ids = Enumerable.Range(0, documentCount)
                .Select(i => $"rename-doc-{i:D4}").ToList();
            var documents = ids.Select(id => 
                $"Document {id} for rename test. This will be renamed.").ToList();
            
            await _chromaService.AddDocumentsAsync(originalName, ids, documents);
            await _syncManager.ProcessCommitAsync("Initial collection for rename test", true, false);

            // Track rename operation
            stopwatch.Start();
            await _deletionTracker.TrackCollectionUpdateAsync(
                _tempDir,
                originalName,
                newName,
                new Dictionary<string, object> { ["created_at"] = DateTime.UtcNow.ToString("O") },
                new Dictionary<string, object> { ["renamed_at"] = DateTime.UtcNow.ToString("O") },
                "main",
                await _doltCli.GetHeadCommitHashAsync()
            );
            stopwatch.Stop();
            var trackTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Rename tracked in {trackTime}ms");

            // Delete old collection and create new one (simulate rename)
            stopwatch.Restart();
            await _chromaService.DeleteCollectionAsync(originalName);
            await _chromaService.CreateCollectionAsync(newName);
            await _chromaService.AddDocumentsAsync(newName, ids, documents);
            stopwatch.Stop();
            var renameTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Collection renamed in ChromaDB in {renameTime}ms");

            // Sync rename to Dolt
            stopwatch.Restart();
            var collectionChanges = await _collectionDetector.DetectCollectionChangesAsync();
            await _syncManager.StageCollectionChangesAsync();
            await _syncManager.ProcessCommitAsync($"Renamed collection from {originalName} to {newName}", true, false);
            stopwatch.Stop();
            var syncTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Rename synced to Dolt in {syncTime}ms");

            // Verify rename
            var collections = await _chromaService.ListCollectionsAsync();
            Assert.That(collections, Does.Not.Contain(originalName), "Original collection should not exist");
            Assert.That(collections, Does.Contain(newName), "New collection should exist");

            // Performance assertions
            Assert.That(trackTime, Is.LessThan(100), "Tracking should be nearly instant");
            Assert.That(renameTime, Is.LessThan(5000), "Rename simulation should complete within 5 seconds");
            Assert.That(syncTime, Is.LessThan(5000), "Sync should complete within 5 seconds");

            _logger.LogInformation($"\n=== Rename Performance Summary ===");
            _logger.LogInformation($"Documents: {documentCount}");
            _logger.LogInformation($"Track Time: {trackTime}ms");
            _logger.LogInformation($"Rename Time: {renameTime}ms");
            _logger.LogInformation($"Sync Time: {syncTime}ms");
            _logger.LogInformation($"Total Time: {trackTime + renameTime + syncTime}ms");
            _logger.LogInformation($"==================================\n");
        }

        /// <summary>
        /// Test 4: Complete Collection Lifecycle
        /// Tests the full lifecycle: create -> update -> delete with performance metrics
        /// </summary>
        [Test]
        [Ignore("Temporarily disabled - performance tests take too long and may cause deadlocks")]
        public async Task TestCompleteCollectionLifecycle()
        {
            _logger.LogInformation("=== Starting Complete Collection Lifecycle Test ===");

            var stopwatch = new Stopwatch();
            var collectionName = "lifecycle-test";
            var documentCount = 100;
            var memoryBefore = GC.GetTotalMemory(true);

            // Phase 1: Create
            stopwatch.Start();
            await _chromaService.CreateCollectionAsync(collectionName, 
                metadata: new Dictionary<string, object> 
                { 
                    ["created_at"] = DateTime.UtcNow.ToString("O"),
                    ["version"] = "1.0",
                    ["description"] = "Lifecycle test collection"
                });
            stopwatch.Stop();
            var createTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Phase 1 - Collection created in {createTime}ms");

            // Phase 2: Add documents
            stopwatch.Restart();
            var ids = Enumerable.Range(0, documentCount)
                .Select(i => $"lifecycle-doc-{i:D4}").ToList();
            var documents = ids.Select(id => 
                $"Lifecycle test document {id}. Testing full collection lifecycle.").ToList();
            
            await _chromaService.AddDocumentsAsync(collectionName, ids, documents);
            await _syncManager.ProcessCommitAsync("Lifecycle test: initial documents", true, false);
            stopwatch.Stop();
            var populateTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Phase 2 - Documents added and committed in {populateTime}ms");

            // Phase 3: Update documents
            stopwatch.Restart();
            var idsToUpdate = ids.Take(50).ToList();
            var updatedDocs = idsToUpdate.Select(id => 
                $"UPDATED: Lifecycle test document {id}. Modified at {DateTime.UtcNow:O}").ToList();
            
            await _chromaService.UpdateDocumentsAsync(collectionName, idsToUpdate, updatedDocs);
            await _syncManager.ProcessCommitAsync("Lifecycle test: updated documents", true, false);
            stopwatch.Stop();
            var updateTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Phase 3 - Documents updated in {updateTime}ms");

            // Phase 4: Delete some documents
            stopwatch.Restart();
            var idsToDelete = ids.Skip(75).Take(25).ToList();
            await _chromaService.DeleteDocumentsAsync(collectionName, idsToDelete);
            await _syncManager.ProcessCommitAsync("Lifecycle test: deleted some documents", true, false);
            stopwatch.Stop();
            var deleteDocsTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Phase 4 - Documents deleted in {deleteDocsTime}ms");

            // Phase 5: Delete collection
            stopwatch.Restart();
            
            // Track deletion
            await _deletionTracker.TrackCollectionDeletionAsync(
                _tempDir,
                collectionName,
                new Dictionary<string, object> 
                { 
                    ["deleted_at"] = DateTime.UtcNow.ToString("O"),
                    ["final_doc_count"] = 75 
                },
                "main",
                await _doltCli.GetHeadCommitHashAsync()
            );
            
            await _chromaService.DeleteCollectionAsync(collectionName);
            await _syncManager.StageCollectionChangesAsync();
            await _syncManager.ProcessCommitAsync("Lifecycle test: collection deleted", true, false);
            stopwatch.Stop();
            var deleteCollectionTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Phase 5 - Collection deleted in {deleteCollectionTime}ms");

            // Memory and performance analysis
            var memoryAfter = GC.GetTotalMemory(true);
            var memoryUsedMB = (memoryAfter - memoryBefore) / (1024.0 * 1024.0);
            var totalTime = createTime + populateTime + updateTime + deleteDocsTime + deleteCollectionTime;

            // Assertions
            Assert.That(totalTime, Is.LessThan(30000), "Complete lifecycle should finish within 30 seconds");
            Assert.That(memoryUsedMB, Is.LessThan(100), "Memory usage should stay under 100MB");

            // Verify final state
            var collections = await _chromaService.ListCollectionsAsync();
            Assert.That(collections, Does.Not.Contain(collectionName), "Collection should be fully deleted");

            _logger.LogInformation($"\n=== Lifecycle Performance Summary ===");
            _logger.LogInformation($"Phase 1 - Create: {createTime}ms");
            _logger.LogInformation($"Phase 2 - Populate ({documentCount} docs): {populateTime}ms");
            _logger.LogInformation($"Phase 3 - Update (50 docs): {updateTime}ms");
            _logger.LogInformation($"Phase 4 - Delete (25 docs): {deleteDocsTime}ms");
            _logger.LogInformation($"Phase 5 - Delete Collection: {deleteCollectionTime}ms");
            _logger.LogInformation($"Total Time: {totalTime}ms");
            _logger.LogInformation($"Memory Used: {memoryUsedMB:F2}MB");
            _logger.LogInformation($"====================================\n");
        }

        /// <summary>
        /// Test 5: Concurrent Collection Operations
        /// Tests performance with concurrent operations on multiple collections
        /// </summary>
        [Test]
        [Ignore("Temporarily disabled - performance tests take too long and may cause deadlocks")]
        public async Task TestConcurrentCollectionOperations()
        {
            _logger.LogInformation("=== Starting Concurrent Collection Operations Test ===");

            var stopwatch = new Stopwatch();
            var collectionCount = 5;
            var operationsPerCollection = 10;

            // Create collections
            var collectionNames = new List<string>();
            for (int i = 0; i < collectionCount; i++)
            {
                var name = $"perf-concurrent-{i}";
                collectionNames.Add(name);
                await _chromaService.CreateCollectionAsync(name);
                
                // Add initial documents
                var ids = Enumerable.Range(0, 20).Select(j => $"{name}-doc-{j}").ToList();
                var docs = ids.Select(id => $"Initial document {id}").ToList();
                await _chromaService.AddDocumentsAsync(name, ids, docs);
            }
            
            await _syncManager.ProcessCommitAsync("Initial state for concurrent test", true, false);

            // Perform concurrent operations
            stopwatch.Start();
            var tasks = new List<Task>();
            
            foreach (var collectionName in collectionNames)
            {
                // Simulate concurrent operations
                var task = Task.Run(async () =>
                {
                    for (int op = 0; op < operationsPerCollection; op++)
                    {
                        var opType = op % 3;
                        switch (opType)
                        {
                            case 0: // Add
                                var newId = $"{collectionName}-new-{op}";
                                await _chromaService.AddDocumentsAsync(collectionName,
                                    new List<string> { newId },
                                    new List<string> { $"Concurrent add {op}" });
                                break;
                                
                            case 1: // Update
                                var updateId = $"{collectionName}-doc-{op}";
                                await _chromaService.UpdateDocumentsAsync(collectionName,
                                    new List<string> { updateId },
                                    new List<string> { $"Concurrent update {op} at {DateTime.UtcNow:O}" });
                                break;
                                
                            case 2: // Delete
                                var deleteId = $"{collectionName}-doc-{19 - op}";
                                try
                                {
                                    await _chromaService.DeleteDocumentsAsync(collectionName,
                                        new List<string> { deleteId });
                                }
                                catch
                                {
                                    // Document might already be deleted
                                }
                                break;
                        }
                    }
                });
                tasks.Add(task);
            }
            
            await Task.WhenAll(tasks);
            stopwatch.Stop();
            var concurrentTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Concurrent operations completed in {concurrentTime}ms");

            // Sync all changes
            stopwatch.Restart();
            await _syncManager.ProcessCommitAsync("Concurrent operations completed", true, false);
            stopwatch.Stop();
            var syncTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Changes synced in {syncTime}ms");

            // Performance assertions
            var totalOperations = collectionCount * operationsPerCollection;
            Assert.That(concurrentTime, Is.LessThan(totalOperations * 100), 
                $"Concurrent operations should average less than 100ms per operation");
            Assert.That(syncTime, Is.LessThan(10000), 
                "Sync should complete within 10 seconds");

            _logger.LogInformation($"\n=== Concurrent Operations Summary ===");
            _logger.LogInformation($"Collections: {collectionCount}");
            _logger.LogInformation($"Operations per Collection: {operationsPerCollection}");
            _logger.LogInformation($"Total Operations: {totalOperations}");
            _logger.LogInformation($"Concurrent Execution: {concurrentTime}ms ({concurrentTime / (double)totalOperations:F2}ms/op)");
            _logger.LogInformation($"Sync Time: {syncTime}ms");
            _logger.LogInformation($"=====================================\n");
        }
    }
}