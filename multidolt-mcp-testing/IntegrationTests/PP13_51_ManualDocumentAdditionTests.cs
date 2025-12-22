using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DMMS.Models;
using DMMS.Services;
using DMMS.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace DMMSTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for PP13-51: Manual document additions via ChromaAddDocumentsTool 
    /// should be detected as local changes during commit operations
    /// </summary>
    public class PP13_51_ManualDocumentAdditionTests
    {
        private IServiceProvider? _serviceProvider;
        private ILogger<PP13_51_ManualDocumentAdditionTests>? _logger;
        private string _testDirectory = null!;
        private string _chromaDbPath = null!;
        private string _doltPath = null!;
        
        private IChromaDbService? _chromaService;
        private IDoltCli? _doltCli;
        private ChromaToDoltDetector? _detector;
        private ChromaAddDocumentsTool? _addDocsTool;

        [SetUp]
        public void Setup()
        {
            // Initialize PythonContext for ChromaDB operations
            if (!PythonContext.IsInitialized)
            {
                PythonContext.Initialize();
            }

            // Create unique test directories with enhanced isolation
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var guid = Guid.NewGuid().ToString("N")[..8];
            var processId = Environment.ProcessId;
            _testDirectory = Path.Combine(Path.GetTempPath(), $"PP13_51_Test_{timestamp}_{guid}_{processId}");
            Directory.CreateDirectory(_testDirectory);
            
            _chromaDbPath = Path.Combine(_testDirectory, "chroma-data");
            _doltPath = Path.Combine(_testDirectory, "dolt-repo");
            
            // Setup services with test-specific configuration
            var services = new ServiceCollection();
            
            // Configure Dolt with test working directory
            services.Configure<DoltConfiguration>(options =>
            {
                options.DoltExecutablePath = "C:\\Program Files\\Dolt\\bin\\dolt.exe";
                options.RepositoryPath = _doltPath;
                options.CommandTimeoutMs = 30000;
                options.EnableDebugLogging = true;
            });

            // Configure unique ChromaDB path to prevent conflicts
            services.Configure<ServerConfiguration>(options =>
            {
                options.ChromaDataPath = _chromaDbPath;
            });

            // Add required services
            services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug)); // Changed to Debug to see more logs
            services.AddSingleton<IDoltCli, DoltCli>();
            services.AddSingleton<IChromaDbService, ChromaPersistentDbService>();
            services.AddSingleton<ChromaAddDocumentsTool>();

            _serviceProvider = services.BuildServiceProvider();
            _doltCli = _serviceProvider.GetRequiredService<IDoltCli>();
            _chromaService = _serviceProvider.GetRequiredService<IChromaDbService>();
            _addDocsTool = _serviceProvider.GetRequiredService<ChromaAddDocumentsTool>();
            
            // Create detector using the same logger factory from DI container
            var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
            _detector = new ChromaToDoltDetector(_chromaService, _doltCli, loggerFactory.CreateLogger<ChromaToDoltDetector>());
            
            // Create standalone logger using the same logger factory
            _logger = loggerFactory.CreateLogger<PP13_51_ManualDocumentAdditionTests>();
            
            _logger!.LogInformation("PP13-51 Test setup complete. ChromaDB: {ChromaPath}, Dolt: {DoltPath}", 
                _chromaDbPath, _doltPath);
        }

        [TearDown]
        public async Task TearDown()
        {
            try
            {
                // Step 1: Clean collections FIRST (which includes immediate disposal for Python services)
                await CleanupCollectionsAsync();
                
                // Step 2: Dispose services (most important resources already cleaned up in Step 1)
                if (_serviceProvider is IDisposable disposableProvider)
                {
                    disposableProvider.Dispose();
                }
                
                // Step 3: Shorter wait since immediate disposal should have released most handles
                await Task.Delay(500);
                
                // Step 4: Attempt directory cleanup with retry logic
                await CleanupDirectoryWithRetryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error during test cleanup");
            }
        }

        /// <summary>
        /// Cleans up ChromaDB collections before service disposal to prevent collection name conflicts and file locking
        /// </summary>
        private async Task CleanupCollectionsAsync()
        {
            if (_chromaService != null)
            {
                try
                {
                    var collections = await _chromaService.ListCollectionsAsync();
                    _logger?.LogInformation("Found {Count} collections to clean up", collections.Count);
                    
                    foreach (var collection in collections)
                    {
                        if (collection != "default") // Preserve default collection if needed
                        {
                            _logger?.LogInformation("Deleting collection: {Collection}", collection);
                            await _chromaService.DeleteCollectionAsync(collection);
                        }
                    }
                    _logger?.LogInformation("Collection cleanup completed successfully");

                    // PP13-51 FIX: Use immediate disposal to force file handle release for tests
                    if (_chromaService is ChromaPythonService pythonService)
                    {
                        _logger?.LogInformation("Performing immediate disposal to release file handles");
                        await pythonService.DisposeImmediatelyAsync();
                        _logger?.LogInformation("Immediate disposal completed");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to cleanup collections - continuing with disposal");
                }
            }
        }

        /// <summary>
        /// Attempts to cleanup the test directory with retry logic to handle file locking
        /// NOTE: ChromaDB's data_level0.bin may remain locked by Python.NET even after immediate disposal.
        /// This is a known limitation of Python.NET + ChromaDB integration and doesn't affect test functionality.
        /// </summary>
        private async Task CleanupDirectoryWithRetryAsync()
        {
            if (!Directory.Exists(_testDirectory))
                return;

            const int maxAttempts = 5;
            const int baseDelayMs = 500;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(_testDirectory, true);
                    _logger?.LogInformation("Successfully cleaned up test directory");
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    // Already deleted
                    return;
                }
                catch (UnauthorizedAccessException ex) when (attempt < maxAttempts - 1)
                {
                    int delayMs = baseDelayMs * (int)Math.Pow(2, attempt); // Exponential backoff
                    _logger?.LogWarning("Directory cleanup attempt {Attempt} failed, retrying in {Delay}ms: {Error}", 
                        attempt + 1, delayMs, ex.Message);
                    await Task.Delay(delayMs);
                }
                catch (IOException ex) when (attempt < maxAttempts - 1)
                {
                    int delayMs = baseDelayMs * (int)Math.Pow(2, attempt);
                    _logger?.LogWarning("Directory cleanup attempt {Attempt} failed, retrying in {Delay}ms: {Error}", 
                        attempt + 1, delayMs, ex.Message);
                    await Task.Delay(delayMs);
                }
                catch (IOException ex) when (ex.Message.Contains("data_level0.bin") || ex.Message.Contains("chroma.sqlite3"))
                {
                    // Known ChromaDB file locking issue - this is expected and not a test failure
                    _logger?.LogInformation("ChromaDB file locking prevented directory cleanup (expected behavior): {Error}", ex.Message);
                    _logger?.LogInformation("Test completed successfully despite directory cleanup limitation");
                    return;
                }
            }
            
            _logger?.LogError("Failed to cleanup test directory after {Attempts} attempts: {Directory}", 
                maxAttempts, _testDirectory);
        }

        [Test]
        [CancelAfter(30000)] // 30 seconds timeout
        public async Task ManualDocumentAddition_ShouldBeDetectedAsLocalChange()
        {
            _logger!.LogInformation("===== STARTING PP13-51 Test: Manual Document Addition Detection =====");

            await SetupTestRepositoryAsync();
            
            const string collectionName = "test-collection";
            const string docId = "manual-test-doc-1";
            const string docContent = "This document was manually added via ChromaAddDocumentsTool for PP13-51 testing";

            // Step 1: Create a collection and add document via MCP tool
            _logger!.LogInformation("STEP 1: Adding document via ChromaAddDocumentsTool (should set is_local_change=true)");
            
            await _chromaService!.CreateCollectionAsync(collectionName);
            
            var addResult = await _addDocsTool!.AddDocuments(
                collectionName, 
                $"[\"{docContent}\"]", 
                $"[\"{docId}\"]", 
                null); // No metadata - tool should add is_local_change=true automatically
            
            _logger!.LogInformation("AddDocuments result: {Result}", addResult);
            
            // Verify the document was added
            Assert.That(addResult.GetType().GetProperty("success")?.GetValue(addResult), Is.EqualTo(true), 
                "Document should be added successfully");

            // Step 2: Detect local changes using ChromaToDoltDetector
            _logger!.LogInformation("STEP 2: Detecting local changes...");
            
            var localChanges = await _detector!.DetectLocalChangesAsync(collectionName);
            
            _logger!.LogInformation("Detection result: {NewCount} new, {ModifiedCount} modified, {DeletedCount} deleted", 
                localChanges.NewDocuments.Count, localChanges.ModifiedDocuments.Count, localChanges.DeletedDocuments.Count);

            // Step 3: Verify the document is detected as a local change
            Assert.That(localChanges.HasChanges, Is.True, 
                "PP13-51 FIX: Manual document addition should be detected as local change");
            
            Assert.That(localChanges.NewDocuments.Count, Is.EqualTo(1), 
                "Should detect exactly 1 new document");
            
            Assert.That(localChanges.NewDocuments[0].DocId, Is.EqualTo(docId), 
                "Should detect the correct document ID");
                
            Assert.That(localChanges.NewDocuments[0].Content, Contains.Substring(docContent), 
                "Should detect the correct document content");

            _logger!.LogInformation("✅ SUCCESS: Manual document addition was correctly detected as local change!");
            _logger!.LogInformation("===== PP13-51 Test PASSED =====");
        }

        [Test]
        [CancelAfter(45000)] // 45 seconds timeout
        public async Task FallbackMechanism_ShouldDetectUnflaggedDocuments()
        {
            _logger!.LogInformation("===== STARTING PP13-51 Test: Fallback Mechanism Detection =====");

            await SetupTestRepositoryAsync();
            
            const string collectionName = "fallback-test-collection";
            const string docId = "fallback-test-doc-1";
            const string docContent = "This document tests the fallback mechanism for PP13-51";

            // Step 1: Create collection and add document WITHOUT is_local_change flag
            _logger!.LogInformation("----------------------------------------");
            _logger!.LogInformation("STEP 1A: Setting up collection");
            _logger!.LogInformation("----------------------------------------");

            bool createdCollection = await _chromaService!.CreateCollectionAsync(collectionName);
            Assert.That(createdCollection, Is.True, "Collection should be created successfully");

            _logger!.LogInformation("----------------------------------------");
            _logger!.LogInformation("STEP 1B: Adding document ");
            _logger!.LogInformation("----------------------------------------");

            // Add document directly without the MCP tool (simulates old behavior)
            bool addedDoc = await _chromaService.AddDocumentsAsync(collectionName, 
                new List<string> { docContent }, 
                new List<string> { docId }, 
                null); // No metadata - this will test the fallback detection mechanism

            Assert.That(addedDoc, Is.True, "document should be added successfully");


            _logger!.LogInformation("Document added without is_local_change flag");
            
            // Step 2: Verify fallback mechanism detects the unflagged document
            _logger!.LogInformation("STEP 2: Testing fallback mechanism...");
            
            
            var localChanges = await _detector!.DetectLocalChangesAsync(collectionName);
            
            _logger!.LogInformation("Fallback detection result: {NewCount} new, {ModifiedCount} modified, {DeletedCount} deleted", 
                localChanges.NewDocuments.Count, localChanges.ModifiedDocuments.Count, localChanges.DeletedDocuments.Count);

            // Step 3: Verify fallback mechanism works
            Assert.That(localChanges.HasChanges, Is.True, 
                "PP13-51 FALLBACK: Unflagged document should be detected by fallback mechanism");
            
            Assert.That(localChanges.NewDocuments.Count, Is.EqualTo(1), 
                "Fallback should detect exactly 1 new document");
            
            Assert.That(localChanges.NewDocuments[0].DocId, Is.EqualTo(docId), 
                "Fallback should detect the correct document ID");

            _logger!.LogInformation("✅ SUCCESS: Fallback mechanism correctly detected unflagged document!");
            _logger!.LogInformation("===== PP13-51 Fallback Test PASSED =====");
            

            _logger!.LogInformation("----------------------------------------");
            _logger!.LogInformation("STEP N: Teardown ");
            _logger!.LogInformation("----------------------------------------");
        }

        private async Task SetupTestRepositoryAsync()
        {
            _logger!.LogInformation("Setting up test Dolt repository...");

            // Initialize Dolt repository
            var initResult = await _doltCli!.InitAsync();
            Assert.That(initResult.Success, Is.True, $"Dolt init should succeed: {initResult.Error}");
            
            // Create basic schema for documents table
            var createTableSql = @"
                CREATE TABLE IF NOT EXISTS documents (
                    doc_id VARCHAR(255) PRIMARY KEY,
                    collection_name VARCHAR(255) NOT NULL,
                    content LONGTEXT NOT NULL,
                    content_hash VARCHAR(64) NOT NULL,
                    metadata JSON,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
                )";
                
            var createResult = await _doltCli!.ExecuteAsync(createTableSql);
            Assert.That(createResult, Is.GreaterThanOrEqualTo(0), "Document table creation should succeed");
            
            _logger!.LogInformation("Test repository setup complete");
        }
    }
}