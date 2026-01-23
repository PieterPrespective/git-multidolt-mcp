using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EmbranchTesting.IntegrationTests;

/// <summary>
/// Minimal reproducible test for Python.NET ChromaDB deadlock issue
/// This test isolates the exact conditions causing the deadlock
/// </summary>
[TestFixture]
public class PythonNetDeadlockTest
{
    private ILogger<PythonNetDeadlockTest>? _logger;
    private string _testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        using var loggerFactory = LoggerFactory.Create(builder => 
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        _logger = loggerFactory.CreateLogger<PythonNetDeadlockTest>();

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        _testDirectory = Path.Combine(Path.GetTempPath(), $"DeadlockTest_{timestamp}");
        Directory.CreateDirectory(_testDirectory);
        
        _logger.LogInformation("Test directory: {TestDirectory}", _testDirectory);
    }

    [Test]
    [Timeout(30000)] // 30 second timeout to detect deadlock
    public async Task MultipleChromaServices_CreateCollection_ShouldNotDeadlock()
    {
        _logger!.LogInformation("=== Starting Python.NET Deadlock Reproduction Test ===");
        
        // Initialize PythonContext once
        if (!PythonContext.IsInitialized)
        {
            _logger.LogInformation("Initializing PythonContext...");
            PythonContext.Initialize();
            _logger.LogInformation("✅ PythonContext initialized");
        }

        try
        {
            // Test Case 1: Single service, multiple operations
            _logger.LogInformation("\n--- Test Case 1: Single Service Multiple Operations ---");
            await TestSingleServiceMultipleOperations();
            
            // Test Case 2: Multiple services, same path
            _logger.LogInformation("\n--- Test Case 2: Multiple Services Same Path ---");
            await TestMultipleServicesSamePath();
            
            // Test Case 3: Multiple services, different paths
            _logger.LogInformation("\n--- Test Case 3: Multiple Services Different Paths ---");
            await TestMultipleServicesDifferentPaths();
            
            // Test Case 4: Service disposal and recreation
            _logger.LogInformation("\n--- Test Case 4: Service Disposal and Recreation ---");
            await TestServiceDisposalAndRecreation();
            
            _logger.LogInformation("\n✅ All test cases completed without deadlock!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Test failed with exception");
            throw;
        }
    }

    private async Task TestSingleServiceMultipleOperations()
    {
        _logger!.LogInformation("Creating single ChromaPythonService...");
        
        var config = Options.Create(new ServerConfiguration
        {
            ChromaDataPath = Path.Combine(_testDirectory, "test1")
        });
        
        var serviceLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChromaPythonService>();
        using var service = new ChromaPythonService(serviceLogger, config);
        
        // Multiple operations on same service
        _logger.LogInformation("Listing collections...");
        var collections = await service.ListCollectionsAsync();
        _logger.LogInformation("Collections found: {Count}", collections.Count());
        
        _logger.LogInformation("Creating collection 'test_collection_1'...");
        await service.CreateCollectionAsync("test_collection_1");
        _logger.LogInformation("✅ Collection created");
        
        _logger.LogInformation("Creating collection 'test_collection_2'...");
        await service.CreateCollectionAsync("test_collection_2");
        _logger.LogInformation("✅ Second collection created");
        
        _logger.LogInformation("✅ Single service test passed");
    }

    private async Task TestMultipleServicesSamePath()
    {
        _logger!.LogInformation("Testing multiple services with same path...");
        
        var sharedPath = Path.Combine(_testDirectory, "shared");
        var config = Options.Create(new ServerConfiguration
        {
            ChromaDataPath = sharedPath
        });
        
        var serviceLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChromaPythonService>();
        
        // First service
        _logger.LogInformation("Creating first service...");
        using (var service1 = new ChromaPythonService(serviceLogger, config))
        {
            _logger.LogInformation("Service 1: Creating collection...");
            await service1.CreateCollectionAsync("shared_collection_1");
            _logger.LogInformation("✅ Service 1: Collection created");
        }
        
        // Allow cleanup
        await Task.Delay(500);
        GC.Collect();
        
        // Second service - THIS IS WHERE DEADLOCK TYPICALLY OCCURS
        _logger.LogInformation("Creating second service (potential deadlock point)...");
        using (var service2 = new ChromaPythonService(serviceLogger, config))
        {
            _logger.LogInformation("Service 2: Creating collection...");
            await service2.CreateCollectionAsync("shared_collection_2");
            _logger.LogInformation("✅ Service 2: Collection created - NO DEADLOCK!");
        }
        
        _logger.LogInformation("✅ Multiple services same path test passed");
    }

    private async Task TestMultipleServicesDifferentPaths()
    {
        _logger!.LogInformation("Testing multiple services with different paths...");
        
        var path1 = Path.Combine(_testDirectory, "path1");
        var path2 = Path.Combine(_testDirectory, "path2");
        
        var config1 = Options.Create(new ServerConfiguration { ChromaDataPath = path1 });
        var config2 = Options.Create(new ServerConfiguration { ChromaDataPath = path2 });
        
        var serviceLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChromaPythonService>();
        
        // First service
        _logger.LogInformation("Creating first service with path1...");
        using (var service1 = new ChromaPythonService(serviceLogger, config1))
        {
            await service1.CreateCollectionAsync("path1_collection");
            _logger.LogInformation("✅ Service 1: Collection created");
        }
        
        // Allow cleanup
        await Task.Delay(500);
        GC.Collect();
        
        // Second service with different path
        _logger.LogInformation("Creating second service with path2...");
        using (var service2 = new ChromaPythonService(serviceLogger, config2))
        {
            await service2.CreateCollectionAsync("path2_collection");
            _logger.LogInformation("✅ Service 2: Collection created");
        }
        
        _logger.LogInformation("✅ Multiple services different paths test passed");
    }

    private async Task TestServiceDisposalAndRecreation()
    {
        _logger!.LogInformation("Testing service disposal and recreation pattern...");
        
        var testPath = Path.Combine(_testDirectory, "disposal_test");
        var config = Options.Create(new ServerConfiguration { ChromaDataPath = testPath });
        var serviceLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChromaPythonService>();
        
        // Create, use, and dispose service multiple times
        for (int i = 1; i <= 3; i++)
        {
            _logger.LogInformation($"Iteration {i}: Creating service...");
            
            using (var service = new ChromaPythonService(serviceLogger, config))
            {
                var collectionName = $"disposal_test_{i}";
                _logger.LogInformation($"Creating collection '{collectionName}'...");
                await service.CreateCollectionAsync(collectionName);
                _logger.LogInformation($"✅ Collection '{collectionName}' created");
                
                // Perform additional operation
                var collections = await service.ListCollectionsAsync();
                _logger.LogInformation($"Total collections: {collections.Count()}");
            }
            
            _logger.LogInformation($"Service {i} disposed");
            
            // Small delay between iterations
            await Task.Delay(200);
        }
        
        _logger.LogInformation("✅ Service disposal and recreation test passed");
    }

    [Test]
    [Timeout(30000)]
    public async Task ReproduceExactSyncManagerScenario()
    {
        _logger!.LogInformation("=== Reproducing Exact SyncManager Scenario ===");
        
        if (!PythonContext.IsInitialized)
        {
            PythonContext.Initialize();
        }

        try
        {
            var inputPath = Path.Combine(_testDirectory, "input_chroma");
            var outputPath = Path.Combine(_testDirectory, "output_chroma");
            
            Directory.CreateDirectory(inputPath);
            Directory.CreateDirectory(outputPath);
            
            var inputConfig = Options.Create(new ServerConfiguration { ChromaDataPath = inputPath });
            var outputConfig = Options.Create(new ServerConfiguration { ChromaDataPath = outputPath });
            var serviceLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChromaPythonService>();
            
            // Step 1: Input service operations (simulating test setup)
            _logger.LogInformation("Step 1: Input service operations...");
            using (var inputService = new ChromaPythonService(serviceLogger, inputConfig))
            {
                await inputService.ListCollectionsAsync();
                _logger.LogInformation("✅ Input service list operation completed");
            }
            
            // Step 2: Cleanup delay (as in the actual test)
            _logger.LogInformation("Step 2: Cleanup delay...");
            await Task.Delay(1000);
            GC.Collect();
            
            // Step 3: Output service with collection creation (THIS IS THE DEADLOCK POINT)
            _logger.LogInformation("Step 3: Output service collection creation (deadlock point)...");
            using (var outputService = new ChromaPythonService(serviceLogger, outputConfig))
            {
                _logger.LogInformation("Attempting to create 'dolt_sync' collection...");
                await outputService.CreateCollectionAsync("dolt_sync");
                _logger.LogInformation("✅ Collection 'dolt_sync' created successfully - NO DEADLOCK!");
            }
            
            _logger.LogInformation("✅ Exact SyncManager scenario completed without deadlock!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Exact scenario reproduction failed");
            throw;
        }
    }

    /// <summary>
    /// Test CreateCollection deadlock scenario specifically - rapid collection creation/deletion cycles
    /// Added for PP13-53 to ensure CreateCollection operations complete reliably
    /// </summary>
    [Test]
    [Timeout(30000)]
    public async Task CreateCollectionDeadlockTest_ShouldCompleteWithinTimeout()
    {
        _logger!.LogInformation("=== Testing CreateCollection Deadlock Prevention (PP13-53) ===");
        
        if (!PythonContext.IsInitialized)
        {
            PythonContext.Initialize();
        }

        var testPath = Path.Combine(_testDirectory, "createcollection_deadlock_test");
        var config = Options.Create(new ServerConfiguration { ChromaDataPath = testPath });
        var serviceLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChromaPythonService>();

        try
        {
            using var service = new ChromaPythonService(serviceLogger, config);
            
            _logger.LogInformation("Testing rapid collection creation/deletion cycles...");
            
            // Test 1: Sequential collection creation
            var sequentialStart = DateTime.UtcNow;
            for (int i = 0; i < 5; i++)
            {
                var collectionName = $"seq_collection_{i}";
                _logger.LogInformation($"Creating collection {collectionName}...");
                
                var createStart = DateTime.UtcNow;
                await service.CreateCollectionAsync(collectionName);
                var createTime = (DateTime.UtcNow - createStart).TotalMilliseconds;
                
                _logger.LogInformation($"✅ Collection {collectionName} created in {createTime:F0}ms");
                Assert.That(createTime, Is.LessThan(5000), $"Collection creation took too long: {createTime:F0}ms");
            }
            var sequentialTime = (DateTime.UtcNow - sequentialStart).TotalMilliseconds;
            _logger.LogInformation($"Sequential creation of 5 collections completed in {sequentialTime:F0}ms");
            
            // Test 2: Concurrent collection creation
            _logger.LogInformation("Testing concurrent collection creation...");
            var concurrentStart = DateTime.UtcNow;
            var tasks = new List<Task>();
            
            for (int i = 0; i < 3; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var collectionName = $"concurrent_collection_{index}";
                    _logger.LogInformation($"[Thread {Thread.CurrentThread.ManagedThreadId}] Creating {collectionName}...");
                    
                    var createStart = DateTime.UtcNow;
                    await service.CreateCollectionAsync(collectionName);
                    var createTime = (DateTime.UtcNow - createStart).TotalMilliseconds;
                    
                    _logger.LogInformation($"[Thread {Thread.CurrentThread.ManagedThreadId}] ✅ {collectionName} created in {createTime:F0}ms");
                    Assert.That(createTime, Is.LessThan(15000), $"Concurrent creation took too long: {createTime:F0}ms");
                }));
            }
            
            await Task.WhenAll(tasks);
            var concurrentTime = (DateTime.UtcNow - concurrentStart).TotalMilliseconds;
            _logger.LogInformation($"Concurrent creation of 3 collections completed in {concurrentTime:F0}ms");
            
            // Test 3: Create-Delete-Recreate cycle (tests state cleanup)
            _logger.LogInformation("Testing create-delete-recreate cycle...");
            var cycleName = "cycle_collection";
            
            for (int i = 0; i < 3; i++)
            {
                _logger.LogInformation($"Cycle {i + 1}: Creating {cycleName}...");
                var cycleStart = DateTime.UtcNow;
                
                await service.CreateCollectionAsync(cycleName);
                _logger.LogInformation($"Cycle {i + 1}: Deleting {cycleName}...");
                await service.DeleteCollectionAsync(cycleName);
                
                var cycleTime = (DateTime.UtcNow - cycleStart).TotalMilliseconds;
                _logger.LogInformation($"Cycle {i + 1} completed in {cycleTime:F0}ms");
                Assert.That(cycleTime, Is.LessThan(10000), $"Create-delete cycle took too long: {cycleTime:F0}ms");
            }
            
            _logger.LogInformation("✅ All CreateCollection deadlock tests passed!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ CreateCollection deadlock test failed");
            throw;
        }
    }

    /// <summary>
    /// Test rapid sequential operations similar to DetectLocalChangesAsync workflow to prevent queue saturation
    /// </summary>
    [Test]
    [Timeout(30000)]
    public async Task MultipleSequentialOperations_ShouldNotSaturateQueue()
    {
        _logger!.LogInformation("=== Testing Multiple Sequential Operations (Queue Saturation Prevention) ===");
        
        if (!PythonContext.IsInitialized)
        {
            PythonContext.Initialize();
        }

        var testPath = Path.Combine(_testDirectory, "queue_saturation_test");
        var config = Options.Create(new ServerConfiguration { ChromaDataPath = testPath });
        var serviceLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChromaPythonService>();

        try
        {
            using var service = new ChromaPythonService(serviceLogger, config);
            
            _logger.LogInformation("Creating test collection with documents...");
            await service.CreateCollectionAsync("test_collection");
            
            // Add some test documents to make operations more realistic
            await service.AddDocumentsAsync("test_collection", 
                new List<string> { "doc1_chunk_0", "doc2_chunk_0", "doc3_chunk_0" },
                new List<string> { "Document 1 content", "Document 2 content", "Document 3 content" },
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["doc_id"] = "doc1", ["chunk_index"] = 0, ["is_local_change"] = true },
                    new Dictionary<string, object> { ["doc_id"] = "doc2", ["chunk_index"] = 0, ["is_local_change"] = false },
                    new Dictionary<string, object> { ["doc_id"] = "doc3", ["chunk_index"] = 0, ["is_local_change"] = true }
                });

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting 15 rapid sequential operations...");
            
            // Simulate DetectLocalChangesAsync operation pattern - this was causing the queue saturation
            var operationTasks = new List<Task>();
            
            for (int i = 0; i < 15; i++)
            {
                var operationIndex = i;
                operationTasks.Add(Task.Run(async () =>
                {
                    // Simulate the operations that DetectLocalChangesAsync performs
                    _logger.LogDebug($"Operation {operationIndex}: Starting");
                    
                    // 1. GetDocumentsAsync (similar to GetFlaggedLocalChangesAsync)
                    await service.GetDocumentsAsync("test_collection", 
                        where: new Dictionary<string, object> { ["is_local_change"] = true });
                    
                    // 2. GetDocumentsAsync (similar to GetAllChromaDocumentsAsync)  
                    await service.GetDocumentsAsync("test_collection");
                    
                    _logger.LogDebug($"Operation {operationIndex}: Completed");
                }));
            }
            
            // Wait for all operations to complete
            await Task.WhenAll(operationTasks);
            
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogInformation($"✅ All 15 operations completed in {elapsed:F0}ms without saturation");
            
            // Verify queue is not saturated
            var queueStats = PythonContext.GetQueueStats();
            _logger.LogInformation($"Final queue size: {queueStats.QueueSize}, Over threshold: {queueStats.IsOverThreshold}");
            
            Assert.That(elapsed, Is.LessThan(10000), "Operations should complete within 10 seconds");
            Assert.That(queueStats.IsOverThreshold, Is.False, "Queue should not be over threshold after operations");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Multiple sequential operations test failed");
            throw;
        }
    }

    /// <summary>
    /// Test operation flooding stress scenario with 20-30 operations queued rapidly
    /// </summary>
    [Test]
    [Timeout(45000)]
    public async Task OperationFloodingStressTest_ShouldNotCauseDeadlock()
    {
        _logger!.LogInformation("=== Operation Flooding Stress Test (20-30 Operations) ===");
        
        if (!PythonContext.IsInitialized)
        {
            PythonContext.Initialize();
        }

        var testPath = Path.Combine(_testDirectory, "flooding_stress_test");
        var config = Options.Create(new ServerConfiguration { ChromaDataPath = testPath });
        var serviceLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChromaPythonService>();

        try
        {
            using var service = new ChromaPythonService(serviceLogger, config);
            
            await service.CreateCollectionAsync("stress_test_collection");
            
            var startTime = DateTime.UtcNow;
            var operationCount = 25; // 25 operations to stress test the queue
            _logger.LogInformation($"Starting {operationCount} rapid operations flooding test...");
            
            // Queue all operations as fast as possible (simulating the worst-case scenario)
            var tasks = new List<Task>();
            for (int i = 0; i < operationCount; i++)
            {
                var opId = i;
                tasks.Add(service.ListCollectionsAsync().ContinueWith(_ => 
                {
                    _logger.LogDebug($"Flood operation {opId} completed");
                }));
            }
            
            // Monitor queue size during flooding
            var monitoringTask = Task.Run(async () =>
            {
                var maxQueueSize = 0;
                while (!Task.WhenAll(tasks).IsCompleted)
                {
                    var stats = PythonContext.GetQueueStats();
                    maxQueueSize = Math.Max(maxQueueSize, stats.QueueSize);
                    await Task.Delay(50);
                }
                _logger.LogInformation($"Maximum queue size during flooding: {maxQueueSize}");
                return maxQueueSize;
            });
            
            // Wait for all operations to complete
            await Task.WhenAll(tasks);
            var maxQueueSize = await monitoringTask;
            
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogInformation($"✅ All {operationCount} flood operations completed in {elapsed:F0}ms");
            _logger.LogInformation($"Maximum queue size reached: {maxQueueSize}");
            
            Assert.That(elapsed, Is.LessThan(15000), $"Flooding stress test should complete within 15 seconds, took {elapsed:F0}ms");
            Assert.That(maxQueueSize, Is.LessThan(30), $"Queue should not grow excessively, max size was {maxQueueSize}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Operation flooding stress test failed");
            throw;
        }
    }

    /// <summary>
    /// Test complex ChromaDB operation patterns that simulate DetectLocalChangesAsync workflow 
    /// to ensure queue saturation doesn't occur with the optimizations
    /// </summary>
    [Test]
    [Timeout(30000)]
    public async Task ComplexChromaOperationPatterns_ShouldNotCauseQueueSaturation()
    {
        _logger!.LogInformation("=== Complex ChromaDB Operation Patterns Test ===");
        
        if (!PythonContext.IsInitialized)
        {
            PythonContext.Initialize();
        }

        var testPath = Path.Combine(_testDirectory, "complex_operations_test");
        var config = Options.Create(new ServerConfiguration { ChromaDataPath = testPath });
        var serviceLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChromaPythonService>();

        try
        {
            using var service = new ChromaPythonService(serviceLogger, config);
            
            // Set up test collection with documents
            await service.CreateCollectionAsync("complex_test_collection");
            await service.AddDocumentsAsync("complex_test_collection",
                new List<string> { "doc1_chunk_0", "doc2_chunk_0", "doc3_chunk_0", "doc4_chunk_0", "doc5_chunk_0" },
                new List<string> { "Content 1", "Content 2", "Content 3", "Content 4", "Content 5" },
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["doc_id"] = "doc1", ["chunk_index"] = 0, ["is_local_change"] = true },
                    new Dictionary<string, object> { ["doc_id"] = "doc2", ["chunk_index"] = 0, ["is_local_change"] = true },
                    new Dictionary<string, object> { ["doc_id"] = "doc3", ["chunk_index"] = 0, ["is_local_change"] = false },
                    new Dictionary<string, object> { ["doc_id"] = "doc4", ["chunk_index"] = 0, ["is_local_change"] = true },
                    new Dictionary<string, object> { ["doc_id"] = "doc5", ["chunk_index"] = 0, ["is_local_change"] = false }
                });

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting complex ChromaDB operation patterns...");
            
            // Simulate the operation pattern from DetectLocalChangesAsync (the actual problem source):
            // 1. GetDocumentsAsync with filter (GetFlaggedLocalChangesAsync)
            // 2. GetDocumentsAsync all (GetAllChromaDocumentsAsync) 
            // 3. GetDocumentsAsync with filter again (was the duplicate call issue)
            // 4. Multiple individual GetDocuments calls (was the DocumentExistsInDoltAsync issue)
            
            var queueStatsBefore = PythonContext.GetQueueStats();
            _logger.LogInformation($"Queue before complex operations: Size={queueStatsBefore.QueueSize}, OverThreshold={queueStatsBefore.IsOverThreshold}");
            
            // Operation 1: Get flagged documents (simulates GetFlaggedLocalChangesAsync)
            var flaggedDocs = await service.GetDocumentsAsync("complex_test_collection", 
                where: new Dictionary<string, object> { ["is_local_change"] = true });
            
            // Operation 2: Get all documents (simulates GetAllChromaDocumentsAsync)  
            var allDocs = await service.GetDocumentsAsync("complex_test_collection");
            
            // Operation 3: Simulate the processing that was happening in the foreach loop
            // This was causing the N individual operations that flooded the queue
            for (int i = 0; i < 5; i++)
            {
                // Each iteration simulates what DocumentExistsInDoltAsync was doing
                await service.GetDocumentsAsync("complex_test_collection", 
                    where: new Dictionary<string, object> { ["doc_id"] = $"doc{i + 1}" });
            }
            
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var queueStatsAfter = PythonContext.GetQueueStats();
            
            _logger.LogInformation($"✅ Complex operations completed in {elapsed:F0}ms");
            _logger.LogInformation($"Queue after operations: Size={queueStatsAfter.QueueSize}, OverThreshold={queueStatsAfter.IsOverThreshold}");
            _logger.LogInformation($"Operations processed: flagged docs found={flaggedDocs != null}, all docs found={allDocs != null}");
            
            Assert.That(elapsed, Is.LessThan(10000), $"Complex operations should complete in <10 seconds, took {elapsed:F0}ms");
            Assert.That(queueStatsAfter.IsOverThreshold, Is.False, "Queue should not be over threshold after complex operations");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Complex operation patterns test failed");
            throw;
        }
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Thread.Sleep(1000);
                Directory.Delete(_testDirectory, recursive: true);
                _logger?.LogInformation("Test environment cleaned up");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not clean up test environment");
        }
    }
}