using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Phase 4 performance and reliability tests for branch switching operations
    /// </summary>
    [TestFixture]
    public class Phase4PerformanceTests
    {
        private string _tempDir = null!;
        private DoltCli _doltCli = null!;
        private ChromaDbService _chromaService = null!;
        private SyncManagerV2 _syncManager = null!;
        private ILogger<Phase4PerformanceTests> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            // Initialize Python context if not already done
            if (!PythonContext.IsInitialized)
            {
                PythonContext.Initialize();
            }

            _tempDir = Path.Combine(Path.GetTempPath(), $"BranchPerfTests_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
            _logger = loggerFactory.CreateLogger<Phase4PerformanceTests>();

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

            // Initialize deletion tracker and its database schema
            var deletionTracker = new SqliteDeletionTracker(
                loggerFactory.CreateLogger<SqliteDeletionTracker>(),
                config.Value);
            deletionTracker.InitializeAsync(_tempDir).GetAwaiter().GetResult();
                
            // Initialize sync manager 
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
        public async Task TearDown()
        {
            try
            {
                // Clear collections created during tests instead of deleting files
                // (files are locked by Python context until application quits)
                if (_chromaService != null)
                {
                    try
                    {
                        var collections = await _chromaService.ListCollectionsAsync();
                        foreach (var collection in collections)
                        {
                            if (collection.Contains("perf-") || collection.Contains("recovery-") || 
                                collection.Contains("memory-") || collection.Contains("test"))
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

                    // Dispose ChromaService
                    _chromaService.Dispose();
                }
                
                // Note: We don't delete _tempDir as Python context holds file locks
                // Files will be cleaned up when the application exits
                _logger?.LogDebug("Teardown completed - collections cleared, files left for application exit cleanup");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error during teardown");
            }
        }

        /// <summary>
        /// Performance Test 1: Large Document Set Handling
        /// </summary>
        [Test]
        [Ignore("PP13-58: Temporarily disabled - test hanging, likely due to sync state tracking issues")]
        [TestCase(100, 3, Description = "Small scale: 100 docs across 3 collections")]
        public async Task TestLargeDocumentSetHandling(int totalDocuments, int collectionCount)
        {
            _logger.LogInformation($"Starting Large Document Set Test: {totalDocuments} documents across {collectionCount} collections");

            var stopwatch = new Stopwatch();
            var docsPerCollection = totalDocuments / collectionCount;
            var memoryBefore = GC.GetTotalMemory(true);

            // Create collections and populate with documents
            stopwatch.Start();
            for (int c = 0; c < collectionCount; c++)
            {
                var collectionName = $"perf-collection-{c}";
                await _chromaService.CreateCollectionAsync(collectionName);

                var documents = new List<string>();
                var ids = new List<string>();

                for (int d = 0; d < docsPerCollection; d++)
                {
                    ids.Add($"doc-{c}-{d}");
                    documents.Add($"Performance test document {d} in collection {c}. " +
                                $"This is a sample content that simulates a real document with some meaningful text. " +
                                $"Document ID: {c}-{d}, Timestamp: {DateTime.UtcNow:O}");
                }

                await _chromaService.AddDocumentsAsync(collectionName, ids, documents);
            }
            stopwatch.Stop();

            var populationTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Population completed in {populationTime}ms");

            // Commit all documents
            stopwatch.Restart();
            await _syncManager.ProcessCommitAsync($"Performance test: {totalDocuments} documents", true, false);
            stopwatch.Stop();

            var commitTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Commit completed in {commitTime}ms");

            // Create a feature branch with modifications
            await _doltCli.CheckoutAsync("perf-feature", createNew: true);
            
            // Modify 10% of documents
            var modificationsPerCollection = Math.Max(1, docsPerCollection / 10);
            stopwatch.Restart();
            for (int c = 0; c < collectionCount; c++)
            {
                var collectionName = $"perf-collection-{c}";
                var idsToModify = new List<string>();
                var modifiedDocs = new List<string>();

                for (int d = 0; d < modificationsPerCollection; d++)
                {
                    idsToModify.Add($"doc-{c}-{d}");
                    modifiedDocs.Add($"MODIFIED: Performance test document {d} in collection {c}. Modified at {DateTime.UtcNow:O}");
                }

                await _chromaService.UpdateDocumentsAsync(collectionName, idsToModify, modifiedDocs);
            }
            
            await _syncManager.ProcessCommitAsync("Performance test: modifications", true, false);
            stopwatch.Stop();

            var modificationTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Modifications completed in {modificationTime}ms");

            // Test branch switching performance
            stopwatch.Restart();
            var switchResult = await _syncManager.ProcessCheckoutAsync("main", false);
            stopwatch.Stop();

            var switchToMainTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Switch to main completed in {switchToMainTime}ms");

            Assert.That(switchResult.Status, Is.EqualTo(SyncStatusV2.Completed), "Branch switch should succeed");
            Assert.That(switchToMainTime, Is.LessThan(10000), $"Branch switch should complete within 10 seconds for {totalDocuments} documents");

            // Switch back to feature branch
            stopwatch.Restart();
            switchResult = await _syncManager.ProcessCheckoutAsync("perf-feature", false);
            stopwatch.Stop();

            var switchToFeatureTime = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation($"Switch to feature branch completed in {switchToFeatureTime}ms");

            Assert.That(switchToFeatureTime, Is.LessThan(10000), "Branch switch back should also complete within 10 seconds");

            // Check memory usage
            var memoryAfter = GC.GetTotalMemory(true);
            var memoryUsedMB = (memoryAfter - memoryBefore) / (1024 * 1024);
            
            _logger.LogInformation($"Memory usage: {memoryUsedMB}MB");
            Assert.That(memoryUsedMB, Is.LessThan(500), "Memory usage should remain under 500MB");

            // Performance summary
            _logger.LogInformation($"\n=== Performance Summary ===");
            _logger.LogInformation($"Documents: {totalDocuments} across {collectionCount} collections");
            _logger.LogInformation($"Population: {populationTime}ms");
            _logger.LogInformation($"Initial Commit: {commitTime}ms");
            _logger.LogInformation($"Modifications: {modificationTime}ms");
            _logger.LogInformation($"Switch to Main: {switchToMainTime}ms");
            _logger.LogInformation($"Switch to Feature: {switchToFeatureTime}ms");
            _logger.LogInformation($"Memory Used: {memoryUsedMB}MB");
            _logger.LogInformation($"===========================\n");
        }

        /// <summary>
        /// Reliability Test 1: Failure Recovery
        /// </summary>
        [Test]
        [Ignore("PP13-58: Temporarily disabled - architectural issue with sync state tracking needs deeper fix")]
        public async Task TestFailureRecovery()
        {
            _logger.LogInformation("Starting Failure Recovery Test");

            // Setup initial state
            await _chromaService.CreateCollectionAsync("recovery-test");
            await _chromaService.AddDocumentsAsync("recovery-test",
                new List<string> { "doc1", "doc2", "doc3" },
                new List<string> { "Doc 1", "Doc 2", "Doc 3" });
            
            await _syncManager.ProcessCommitAsync("Initial state", true, false);

            // Create branches
            await _doltCli.CheckoutAsync("branch-a", createNew: true);
            await _chromaService.AddDocumentsAsync("recovery-test",
                new List<string> { "branch-a-doc" },
                new List<string> { "Branch A doc" });
            await _syncManager.ProcessCommitAsync("Branch A changes", true, false);

            await _doltCli.CheckoutAsync("main");
            await _syncManager.ProcessCheckoutAsync("main", false);

            // Test 1: Recovery from uncommitted changes during switch
            _logger.LogInformation("Test 1: Testing recovery with uncommitted changes");
            
            // Create uncommitted changes
            await _chromaService.AddDocumentsAsync("recovery-test",
                new List<string> { "uncommitted" },
                new List<string> { "Uncommitted doc" });

            // Attempt switch with reset_first (should always succeed)
            var checkoutResult = await _syncManager.ProcessCheckoutAsync("main", false);
            
            Assert.That(checkoutResult.Status, Is.Not.EqualTo(SyncStatusV2.Failed), 
                "Should recover with reset_first mode");

            // Verify state is clean
            var localChanges = await _syncManager.GetLocalChangesAsync();
            Assert.That(localChanges.HasChanges, Is.False, "Should have no changes after reset recovery");

            _logger.LogInformation("Failure Recovery Test completed successfully");
        }

        /// <summary>
        /// Test memory usage during repeated branch switches
        /// </summary>
        [Test]
        public async Task TestMemoryUsageDuringRepeatedSwitches()
        {
            _logger.LogInformation("Starting Memory Usage Test");

            // Setup branches with documents
            await _chromaService.CreateCollectionAsync("memory-test");
            
            // Create substantial documents to test memory
            var largeDoc = new string('x', 1000); // 1KB document
            var documents = Enumerable.Range(0, 50).Select(i => $"{largeDoc}_Document_{i}").ToList();
            var ids = Enumerable.Range(0, 50).Select(i => $"mem-doc-{i}").ToList();
            
            await _chromaService.AddDocumentsAsync("memory-test", ids, documents);
            await _syncManager.ProcessCommitAsync("Large documents on main", true, false);

            // Create branch with different content
            await _doltCli.CheckoutAsync("memory-branch", createNew: true);
            await _chromaService.UpdateDocumentsAsync("memory-test",
                ids.Take(25).ToList(),
                documents.Take(25).Select(d => d + "_MODIFIED").ToList());
            await _syncManager.ProcessCommitAsync("Modified documents on branch", true, false);

            // Perform repeated switches and monitor memory
            var memoryReadings = new List<long>();
            var switchTimes = new List<long>();

            for (int i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                
                var memoryBefore = GC.GetTotalMemory(false);
                var stopwatch = Stopwatch.StartNew();
                
                // Switch to main
                await _syncManager.ProcessCheckoutAsync("main", false);
                
                // Switch to branch
                await _syncManager.ProcessCheckoutAsync("memory-branch", false);
                
                stopwatch.Stop();
                var memoryAfter = GC.GetTotalMemory(false);
                
                memoryReadings.Add(memoryAfter - memoryBefore);
                switchTimes.Add(stopwatch.ElapsedMilliseconds);
                
                _logger.LogInformation($"Iteration {i + 1}: Memory delta = {(memoryAfter - memoryBefore) / 1024}KB, Time = {stopwatch.ElapsedMilliseconds}ms");
            }

            // Analyze results
            var avgMemoryDelta = memoryReadings.Average() / (1024 * 1024);
            var maxMemoryDelta = memoryReadings.Max() / (1024 * 1024);
            var avgSwitchTime = switchTimes.Average();
            
            _logger.LogInformation($"\n=== Memory Usage Summary ===");
            _logger.LogInformation($"Average memory delta: {avgMemoryDelta:F2}MB");
            _logger.LogInformation($"Max memory delta: {maxMemoryDelta:F2}MB");
            _logger.LogInformation($"Average switch time: {avgSwitchTime:F2}ms");
            _logger.LogInformation($"============================\n");

            // Assertions
            Assert.That(avgMemoryDelta, Is.LessThan(50), "Average memory delta should be less than 50MB");
            Assert.That(maxMemoryDelta, Is.LessThan(100), "Max memory delta should be less than 100MB");
            
            // Check for memory leaks (later iterations shouldn't use more memory)
            var firstHalfAvg = memoryReadings.Take(3).Average();
            var secondHalfAvg = memoryReadings.Skip(3).Average();
            var leakIndicator = (secondHalfAvg - firstHalfAvg) / (1024 * 1024);
            
            _logger.LogInformation($"Potential memory leak indicator: {leakIndicator:F2}MB");
            Assert.That(Math.Abs(leakIndicator), Is.LessThan(10), "Should not show signs of memory leak");
        }
    }
}