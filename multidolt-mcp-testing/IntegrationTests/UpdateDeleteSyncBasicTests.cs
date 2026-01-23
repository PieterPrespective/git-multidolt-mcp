using System.Linq;
using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Embranch.IntegrationTests
{
    /// <summary>
    /// Basic integration tests for PP13-60-C1 Update/Delete sync functionality
    /// Tests that the enhanced tools properly flag changes for sync detection
    /// </summary>
    [TestFixture]
    [CancelAfter(30000)] // 30 second timeout for entire test fixture
    public class UpdateDeleteSyncBasicTests
    {
        private ServiceProvider _serviceProvider;
        private IChromaDbService _chromaService;
        private IDeletionTracker _deletionTracker;
        private string _testCollectionName;
        private string _tempDir = null!;
        private DoltCli _doltCli = null!;
        private ILogger<UpdateDeleteSyncBasicTests> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            // Initialize Python context if not already done
            if (!PythonContext.IsInitialized)
            {
                PythonContext.Initialize();
            }

            // Create test directories - use single temp directory pattern
            _testCollectionName = $"update_delete_test_{Guid.NewGuid():N}";
            _tempDir = Path.Combine(Path.GetTempPath(), $"UpdateDeleteSyncTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);


            // Initialize Dolt CLI FIRST (before creating service provider)
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _logger = loggerFactory.CreateLogger<UpdateDeleteSyncBasicTests>();
            
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
            
            // Critical: Initialize Dolt BEFORE any services that depend on it
            _logger.LogInformation("Initializing Dolt repository at {Path}", _tempDir);
            await _doltCli.InitAsync();
            _logger.LogInformation("Dolt repository initialized successfully");

            // Now create service provider with initialized Dolt
            _serviceProvider = CreateServiceProvider();
            _chromaService = _serviceProvider.GetRequiredService<IChromaDbService>();
            _deletionTracker = _serviceProvider.GetRequiredService<IDeletionTracker>();
            _logger.LogInformation("Service provider initialized");


            // Initialize test environment after Dolt is ready
            await InitializeTestEnvironmentAsync();
        }

        [Test]
        [CancelAfter(10000)] // 10 second timeout
        public async Task UpdateDocumentTool_SetsLocalChangeFlag()
        {
            // Arrange: Add initial document
            var docId = "update_test_doc";
            var originalContent = "Original content";
            var updatedContent = "Updated content that should sync";
            
            await AddTestDocumentAsync(docId, originalContent);
            
            // Act: Update the document through the tool
            var updateTool = _serviceProvider.GetRequiredService<Tools.ChromaUpdateDocumentsTool>();
            var updateResult = await updateTool.UpdateDocuments(
                _testCollectionName,
                new List<string> { docId }, // ids
                new List<string> { updatedContent }, // documents  
                null // metadatas
            );
            
            TestContext.WriteLine($"Update tool result: {updateResult}");
            
            // Assert: Verify the document was flagged for local change
            var results = await _chromaService.GetDocumentsAsync(_testCollectionName, new List<string> { docId });
            
            Assert.That(results, Is.Not.Null);
            
            // Extract metadata from dynamic results
            if (results is Dictionary<string, object> resultsDict)
            {
                var metadatas = resultsDict.GetValueOrDefault("metadatas") as List<object>;
                if (metadatas != null && metadatas.Count > 0)
                {
                    var metadata = metadatas[0] as Dictionary<string, object>;
                    Assert.That(metadata, Is.Not.Null);
                    Assert.That(metadata.ContainsKey("is_local_change"), Is.True, "Should have is_local_change flag");
                    Assert.That(metadata["is_local_change"], Is.EqualTo(true), "is_local_change should be true");
                    Assert.That(metadata.ContainsKey("content_hash"), Is.True, "Should have content_hash");
                    
                    TestContext.WriteLine("✓ Document properly flagged with is_local_change=true and content_hash");
                }
            }
        }

        [Test]
        [CancelAfter(10000)] // 10 second timeout
        public async Task DeleteDocumentTool_TracksInDeletionDatabase()
        {
            // Arrange: Add initial document
            var docId = "delete_test_doc";
            var originalContent = "Content to be deleted";

            await AddTestDocumentAsync(docId, originalContent);


            // Act: Delete the document through the tool
            var deleteTool = _serviceProvider.GetRequiredService<Tools.ChromaDeleteDocumentsTool>();
            var deleteResult = await deleteTool.DeleteDocuments(
                _testCollectionName,
                $"[\"{docId}\"]" // ids JSON
            );

            TestContext.WriteLine($"Delete tool result: {deleteResult}");

            // Assert: Verify deletion was tracked in external database (use _tempDir not _tempRepoPath)
            TestContext.WriteLine($"Querying for pending deletions at path: {_tempDir} for collection: {_testCollectionName}");
            var pendingDeletions = await _deletionTracker.GetPendingDeletionsAsync(_tempDir, _testCollectionName);
            
            TestContext.WriteLine($"Found {pendingDeletions.Count} pending deletions");
            if (pendingDeletions.Count == 0)
            {
                // Debug: Try getting ALL pending deletions (all collections)
                var allPendingDeletions = await _deletionTracker.GetPendingDeletionsAsync(_tempDir);
                TestContext.WriteLine($"Found {allPendingDeletions.Count} total pending deletions across all collections");
                foreach (var d in allPendingDeletions)
                {
                    TestContext.WriteLine($"  - DocId: {d.DocId}, Collection: {d.CollectionName}, Status: {d.SyncStatus}");
                }
            }

            Assert.That(pendingDeletions, Has.Count.GreaterThanOrEqualTo(1), "Should have at least one pending deletion");

            var deletion = pendingDeletions.FirstOrDefault(d => d.DocId == docId);
            Assert.That(deletion.DocId, Is.Not.Null.And.Not.Empty, $"Should track deletion for document {docId}");
            Assert.That(deletion.DocId, Is.EqualTo(docId));
            Assert.That(deletion.CollectionName, Is.EqualTo(_testCollectionName));
            Assert.That(deletion.SyncStatus, Is.EqualTo("pending"));
            Assert.That(deletion.DeletionSource, Is.EqualTo("mcp_tool"));

            TestContext.WriteLine($"✓ Deletion properly tracked for document {docId}");
        }

        [Test]
        [CancelAfter(10000)] // 10 second timeout
        public async Task DeletionDetector_FindsTrackedDeletions()
        {
            // Arrange: Add document to ChromaDB and sync it to Dolt first
            var docId = "detector_test_doc";
            var content = "Content for detector test";
            await AddTestDocumentAsync(docId, content);
            
            // CRITICAL: Create Dolt documents table and add baseline data
            // This simulates that the document was previously synced to Dolt
            var doltCli = _serviceProvider.GetRequiredService<IDoltCli>();
            await CreateDoltDocumentsTableAsync(doltCli);
            await AddDocumentToDoltAsync(doltCli, docId, content, _testCollectionName);
            
            // Now delete the document from ChromaDB (this should be detected as deletion)
            var deleteTool = _serviceProvider.GetRequiredService<Tools.ChromaDeleteDocumentsTool>();
            await deleteTool.DeleteDocuments(_testCollectionName, $"[\"{docId}\"]");
            
            // Act: Create detector and test detection
            var doltConfig = _serviceProvider.GetRequiredService<IOptions<DoltConfiguration>>();
            
            var detector = new ChromaToDoltDetector(
                _chromaService,
                doltCli,
                _deletionTracker,
                doltConfig,
                _serviceProvider.GetService<ILogger<ChromaToDoltDetector>>()
            );
            
            var localChanges = await detector.DetectLocalChangesAsync(_testCollectionName);
            
            // Assert: Verify deletion was detected
            Assert.That(localChanges, Is.Not.Null);
            Assert.That(localChanges.HasChanges, Is.True, "Should detect changes");
            Assert.That(localChanges.DeletedDocuments, Has.Count.GreaterThanOrEqualTo(1), "Should detect deleted documents");
            
            var deletedDoc = localChanges.DeletedDocuments.FirstOrDefault(d => d.DocId == docId);
            Assert.That(deletedDoc, Is.Not.Null, $"Should detect deletion for document {docId}");
            Assert.That(deletedDoc.DocId, Is.EqualTo(docId));
            Assert.That(deletedDoc.CollectionName, Is.EqualTo(_testCollectionName));
            
            TestContext.WriteLine($"✓ Detector found deletion for document {docId}");
        }

        private async Task InitializeTestEnvironmentAsync()
        {
            // Initialize deletion tracker with the correct path (where Dolt was initialized)
            await _deletionTracker.InitializeAsync(_tempDir);
            
            // Create test collection in ChromaDB
            await _chromaService.CreateCollectionAsync(_testCollectionName);
            
            TestContext.WriteLine($"✓ Initialized test environment with collection {_testCollectionName}");
        }

        private async Task AddTestDocumentAsync(string docId, string content)
        {
            await _chromaService.AddDocumentsAsync(
                _testCollectionName,
                new List<string> { docId },
                new List<string> { content },
                new List<Dictionary<string, object>>
                {
                    new() { ["title"] = $"Test Document {docId}" }
                }
            );
            TestContext.WriteLine($"✓ Added test document: {docId}");
        }

        private ServiceProvider CreateServiceProvider()
        {
            var services = new ServiceCollection();
            
            // Add logging
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Trace); // Reduce noise in tests
            });
            
            // Configure Dolt with proper Windows path handling
            var doltConfig = new DoltConfiguration
            {
                RepositoryPath = _tempDir,
                DoltExecutablePath = Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? @"C:\Program Files\Dolt\bin\dolt.exe"
                    : "dolt",
                CommandTimeoutMs = 30000,
                EnableDebugLogging = true
            };
            services.Configure<DoltConfiguration>(opts =>
            {
                opts.RepositoryPath = doltConfig.RepositoryPath;
                opts.DoltExecutablePath = doltConfig.DoltExecutablePath;
                opts.CommandTimeoutMs = doltConfig.CommandTimeoutMs;
                opts.EnableDebugLogging = doltConfig.EnableDebugLogging;
            });
            
            // Configure Server with single temp directory
            // Ensure dev directory exists for deletion tracking database
            var devPath = Path.Combine(_tempDir, "dev");
            Directory.CreateDirectory(devPath);
            
            var serverConfig = new ServerConfiguration
            {
                ChromaDataPath = Path.Combine(_tempDir, "chroma_data"),
                DataPath = _tempDir,
                ChromaMode = "persistent"
            };
            services.Configure<ServerConfiguration>(opts =>
            {
                opts.ChromaDataPath = serverConfig.ChromaDataPath;
                opts.DataPath = serverConfig.DataPath;
                opts.ChromaMode = serverConfig.ChromaMode;
            });
            
            // Add core services - use the already initialized DoltCli instance
            services.AddSingleton<IDoltCli>(_doltCli);
            // Register ServerConfiguration for SqliteDeletionTracker
            services.AddSingleton(serverConfig);
            services.AddSingleton(Options.Create(serverConfig));
            services.AddSingleton<IDeletionTracker, SqliteDeletionTracker>();
            
            // Add Chroma service - simplified direct instantiation
            services.AddSingleton<IChromaDbService>(sp => 
            {
                var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
                var serverConfigOptions = Options.Create(serverConfig);
                
                // Create ChromaDbService directly (matches working test pattern)
                return new ChromaDbService(
                    loggerFactory.CreateLogger<ChromaDbService>(), 
                    serverConfigOptions);
            });
                
            // Add MCP Tools
            services.AddTransient<Tools.ChromaUpdateDocumentsTool>();
            services.AddTransient<Tools.ChromaDeleteDocumentsTool>();
            
            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Creates the documents table in Dolt (simulating a previously synced state)
        /// </summary>
        private async Task CreateDoltDocumentsTableAsync(IDoltCli doltCli)
        {
            const string createTableSql = @"
                CREATE TABLE IF NOT EXISTS documents (
                    doc_id VARCHAR(255) NOT NULL,
                    collection_name VARCHAR(255) NOT NULL,
                    content_hash VARCHAR(64),
                    content TEXT,
                    metadata JSON,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY (doc_id, collection_name)
                )";
            
            var rowsAffected = await doltCli.ExecuteAsync(createTableSql);
            if (rowsAffected < 0)
            {
                throw new Exception("Failed to create documents table");
            }
            
            TestContext.WriteLine("✓ Created Dolt documents table");
        }

        /// <summary>
        /// Adds a document to Dolt (simulating previous sync operation)
        /// </summary>
        private async Task AddDocumentToDoltAsync(IDoltCli doltCli, string docId, string content, string collectionName)
        {
            // Calculate content hash (simple hash for testing)
            var contentHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
            var contentHashString = Convert.ToHexString(contentHash).ToLower();
            
            var insertSql = $@"
                INSERT INTO documents (doc_id, collection_name, content_hash, content, metadata) 
                VALUES ('{docId.Replace("'", "''")}', '{collectionName.Replace("'", "''")}', '{contentHashString}', '{content.Replace("'", "''")}', '{{}}')";
            
            var rowsAffected = await doltCli.ExecuteAsync(insertSql);
            if (rowsAffected <= 0)
            {
                throw new Exception("Failed to add document to Dolt");
            }
            
            TestContext.WriteLine($"✓ Added baseline document to Dolt: {docId}");
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider?.Dispose();
            
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
                TestContext.WriteLine("✓ Cleaned up test directories");
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Warning: Failed to clean up test directories: {ex.Message}");
            }
        }
    }
}