using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Embranch.IntegrationTests
{
    /// <summary>
    /// Integration tests for PP13-61 Phase 2 Collection Change Detection functionality
    /// Tests the CollectionChangeDetector service in realistic scenarios with ChromaDB and Dolt
    /// Follows PP13-60 lessons for proper initialization patterns
    /// </summary>
    [TestFixture]
    [CancelAfter(60000)] // 60 second timeout for entire test fixture
    public class CollectionChangeDetectionIntegrationTests
    {
        private ServiceProvider _serviceProvider = null!;
        private IChromaDbService _chromaService = null!;
        private IDeletionTracker _deletionTracker = null!;
        private ICollectionChangeDetector _collectionChangeDetector = null!;
        private DoltCli _doltCli = null!;
        private string _tempDir = null!;
        private ILogger<CollectionChangeDetectionIntegrationTests> _logger = null!;

        private const string TestCollection1 = "collection-change-test-1";
        private const string TestCollection2 = "collection-change-test-2";
        private const string TestCollection3 = "collection-change-test-3";

        [SetUp]
        public async Task Setup()
        {
            // Initialize Python context if not already done
            if (!PythonContext.IsInitialized)
            {
                PythonContext.Initialize();
            }

            // Create test directories
            _tempDir = Path.Combine(Path.GetTempPath(), $"CollectionChangeDetectionTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);

            // Initialize Dolt CLI FIRST
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
            _logger = loggerFactory.CreateLogger<CollectionChangeDetectionIntegrationTests>();
            
            var doltConfig = Options.Create(new DoltConfiguration
            {
                DoltExecutablePath = Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? @"C:\Program Files\Dolt\bin\dolt.exe"
                    : "dolt",
                RepositoryPath = _tempDir,
                CommandTimeoutMs = 45000,
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
            _collectionChangeDetector = _serviceProvider.GetRequiredService<ICollectionChangeDetector>();
            _logger.LogInformation("Service provider initialized");

            // Initialize test environment after Dolt is ready
            await InitializeTestEnvironmentAsync();
        }

        [TearDown]
        public async Task TearDown()
        {
            try
            {
                // Clean up ChromaDB collections
                try
                {
                    var collections = await _chromaService.ListCollectionsAsync();
                    if (collections is List<string> collectionList)
                    {
                        foreach (var name in collectionList)
                        {
                            if (!string.IsNullOrEmpty(name) && (name.Contains("collection-change-test") || name.Contains("renamed-test")))
                            {
                                await _chromaService.DeleteCollectionAsync(name);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up collections");
                }

                _serviceProvider?.Dispose();
                
                // Clean up temp directory
                if (Directory.Exists(_tempDir))
                {
                    try
                    {
                        Directory.Delete(_tempDir, true);
                    }
                    catch (Exception ex)
                    {
                        TestContext.WriteLine($"Cleanup warning: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"TearDown error: {ex.Message}");
            }
        }

        [Test]
        [CancelAfter(15000)] // 15 second timeout
        public async Task DetectCollectionChanges_NoChanges_ReturnsEmptyChanges()
        {
            // Arrange - Create collections in both ChromaDB and Dolt
            await CreateTestCollection(TestCollection1, "Test collection 1");
            await CreateTestCollection(TestCollection2, "Test collection 2");
            
            // Create corresponding collections in Dolt
            await CreateDoltCollection(TestCollection1, new Dictionary<string, object> { { "description", "Test collection 1" } });
            await CreateDoltCollection(TestCollection2, new Dictionary<string, object> { { "description", "Test collection 2" } });

            // Act
            var changes = await _collectionChangeDetector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.False);
            Assert.That(changes.TotalChanges, Is.EqualTo(0));
            Assert.That(changes.DeletedCollections, Is.Empty);
            Assert.That(changes.RenamedCollections, Is.Empty);
            Assert.That(changes.UpdatedCollections, Is.Empty);

            TestContext.WriteLine("✓ Correctly detected no collection changes");
        }

        [Test]
        [CancelAfter(15000)] // 15 second timeout
        public async Task DetectCollectionChanges_CollectionDeleted_DetectsDelete()
        {
            // Arrange - Create collections in both systems
            await CreateTestCollection(TestCollection1, "Test collection 1");
            
            await CreateDoltCollection(TestCollection1, new Dictionary<string, object> { { "description", "Test collection 1" } });
            await CreateDoltCollection(TestCollection2, new Dictionary<string, object> { { "description", "Test collection 2" } });
            
            // TestCollection2 exists in Dolt but not in ChromaDB (simulating deletion)

            // Act
            var changes = await _collectionChangeDetector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.True);
            Assert.That(changes.TotalChanges, Is.EqualTo(1));
            Assert.That(changes.DeletedCollections, Has.Count.EqualTo(1));
            Assert.That(changes.DeletedCollections, Contains.Item(TestCollection2));
            Assert.That(changes.RenamedCollections, Is.Empty);
            Assert.That(changes.UpdatedCollections, Is.Empty);

            TestContext.WriteLine($"✓ Correctly detected collection deletion: {TestCollection2}");
        }

        [Test]
        [CancelAfter(15000)] // 15 second timeout
        public async Task DetectCollectionChanges_PendingDeletion_DetectsPendingDelete()
        {
            // Arrange - Create collections in both systems
            await CreateTestCollection(TestCollection1, "Test collection 1");
            
            await CreateDoltCollection(TestCollection1, new Dictionary<string, object> { { "description", "Test collection 1" } });
            await CreateDoltCollection(TestCollection2, new Dictionary<string, object> { { "description", "Test collection 2" } });
            
            // Track a pending deletion
            var originalMetadata = new Dictionary<string, object> { { "description", "Test collection 2" } };
            await _deletionTracker.TrackCollectionDeletionAsync(_tempDir, TestCollection2, originalMetadata, "main", "abc123");

            // Act
            var changes = await _collectionChangeDetector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.True);
            Assert.That(changes.DeletedCollections, Contains.Item(TestCollection2));

            TestContext.WriteLine($"✓ Correctly detected pending collection deletion: {TestCollection2}");
        }

        [Test]
        [CancelAfter(15000)] // 15 second timeout
        public async Task DetectCollectionChanges_PendingRename_DetectsPendingRename()
        {
            // Arrange - Create collections
            var originalName = TestCollection1;
            var newName = $"{TestCollection1}-renamed";
            
            await CreateTestCollection(newName, "Renamed test collection");
            
            await CreateDoltCollection(originalName, new Dictionary<string, object> { { "description", "Original test collection" } });
            
            // Track a pending rename
            var originalMetadata = new Dictionary<string, object> { { "description", "Original test collection" } };
            var newMetadata = new Dictionary<string, object> { { "description", "Renamed test collection" } };
            await _deletionTracker.TrackCollectionUpdateAsync(_tempDir, originalName, newName, originalMetadata, newMetadata, "main", "abc123");

            // Act
            var changes = await _collectionChangeDetector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.True);
            Assert.That(changes.RenamedCollections, Has.Count.EqualTo(1));
            
            var rename = changes.RenamedCollections[0];
            Assert.That(rename.OldName, Is.EqualTo(originalName));
            Assert.That(rename.NewName, Is.EqualTo(newName));

            TestContext.WriteLine($"✓ Correctly detected pending collection rename: {originalName} -> {newName}");
        }

        [Test]
        [CancelAfter(15000)] // 15 second timeout
        public async Task DetectCollectionChanges_MetadataChanged_DetectsMetadataUpdate()
        {
            // Arrange - Create collections with different metadata between ChromaDB and Dolt
            await CreateTestCollection(TestCollection1, "Updated description");
            
            // Original metadata in Dolt (different from ChromaDB)
            await CreateDoltCollection(TestCollection1, new Dictionary<string, object> { { "description", "Original description" } });

            // Act
            var changes = await _collectionChangeDetector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.True);
            Assert.That(changes.UpdatedCollections, Has.Count.EqualTo(1));
            
            var update = changes.UpdatedCollections[0];
            Assert.That(update.CollectionName, Is.EqualTo(TestCollection1));
            Assert.That(update.OldMetadata, Is.Not.Null);
            Assert.That(update.NewMetadata, Is.Not.Null);

            TestContext.WriteLine($"✓ Correctly detected metadata update for collection: {TestCollection1}");
        }

        [Test]
        [CancelAfter(20000)] // 20 second timeout
        public async Task DetectCollectionChanges_MultipleChanges_DetectsAllChanges()
        {
            // Arrange - Complex scenario with multiple types of changes
            
            // 1. Create collection that will have metadata change
            await CreateTestCollection(TestCollection1, "Updated description for test 1");
            await CreateDoltCollection(TestCollection1, new Dictionary<string, object> { { "description", "Original description for test 1" } });
            
            // 2. Create collection that exists only in Dolt (will be detected as deleted)
            await CreateDoltCollection(TestCollection2, new Dictionary<string, object> { { "description", "Will be deleted" } });
            
            // 3. Track a pending rename
            var renamedFrom = TestCollection3;
            var renamedTo = $"{TestCollection3}-renamed";
            await CreateTestCollection(renamedTo, "Renamed collection");
            await CreateDoltCollection(renamedFrom, new Dictionary<string, object> { { "description", "Original renamed collection" } });
            
            var originalMetadata = new Dictionary<string, object> { { "description", "Original renamed collection" } };
            var newMetadata = new Dictionary<string, object> { { "description", "Renamed collection" } };
            await _deletionTracker.TrackCollectionUpdateAsync(_tempDir, renamedFrom, renamedTo, originalMetadata, newMetadata, "main", "abc123");

            // Act
            var changes = await _collectionChangeDetector.DetectCollectionChangesAsync();

            // Assert
            Assert.That(changes, Is.Not.Null);
            Assert.That(changes.HasChanges, Is.True);
            Assert.That(changes.TotalChanges, Is.GreaterThanOrEqualTo(2)); // At least metadata update and deletion
            
            // Check for metadata update
            var metadataUpdate = changes.UpdatedCollections.FirstOrDefault(u => u.CollectionName == TestCollection1);
            Assert.That(metadataUpdate, Is.Not.Null, "Should detect metadata update");
            
            // Check for deletion
            Assert.That(changes.DeletedCollections, Contains.Item(TestCollection2), "Should detect deleted collection");
            
            // Check for rename
            var rename = changes.RenamedCollections.FirstOrDefault(r => r.OldName == renamedFrom);
            Assert.That(rename, Is.Not.Null, "Should detect renamed collection");

            TestContext.WriteLine($"✓ Correctly detected multiple changes: {changes.GetSummary()}");
        }

        [Test]
        [CancelAfter(10000)] // 10 second timeout
        public async Task HasPendingCollectionChanges_WithChanges_ReturnsTrue()
        {
            // Arrange - Create a change scenario
            await CreateTestCollection(TestCollection1, "Test collection");
            await CreateDoltCollection(TestCollection2, new Dictionary<string, object> { { "description", "To be deleted" } });

            // Act
            var hasChanges = await _collectionChangeDetector.HasPendingCollectionChangesAsync();

            // Assert
            Assert.That(hasChanges, Is.True);
            TestContext.WriteLine("✓ Correctly detected pending collection changes");
        }

        [Test]
        [CancelAfter(10000)] // 10 second timeout
        public async Task HasPendingCollectionChanges_NoChanges_ReturnsFalse()
        {
            // Arrange - Create matching collections
            await CreateTestCollection(TestCollection1, "Test collection");
            await CreateDoltCollection(TestCollection1, new Dictionary<string, object> { { "description", "Test collection" } });

            // Act
            var hasChanges = await _collectionChangeDetector.HasPendingCollectionChangesAsync();

            // Assert
            Assert.That(hasChanges, Is.False);
            TestContext.WriteLine("✓ Correctly detected no pending collection changes");
        }

        #region Helper Methods

        private ServiceProvider CreateServiceProvider()
        {
            var services = new ServiceCollection();
            
            // Add logging
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });
            
            // Configure Dolt
            var doltConfig = new DoltConfiguration
            {
                RepositoryPath = _tempDir,
                DoltExecutablePath = Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? @"C:\Program Files\Dolt\bin\dolt.exe"
                    : "dolt",
                CommandTimeoutMs = 45000,
                EnableDebugLogging = true
            };
            services.Configure<DoltConfiguration>(opts =>
            {
                opts.RepositoryPath = doltConfig.RepositoryPath;
                opts.DoltExecutablePath = doltConfig.DoltExecutablePath;
                opts.CommandTimeoutMs = doltConfig.CommandTimeoutMs;
                opts.EnableDebugLogging = doltConfig.EnableDebugLogging;
            });
            
            // Configure Server
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
            
            // Add core services
            services.AddSingleton<IDoltCli>(_doltCli);
            services.AddSingleton(serverConfig);
            services.AddSingleton(Options.Create(serverConfig));
            services.AddSingleton(doltConfig);
            services.AddSingleton(Options.Create(doltConfig));
            
            // Register deletion tracker and collection change detector
            services.AddSingleton<IDeletionTracker, SqliteDeletionTracker>();
            services.AddSingleton<ICollectionChangeDetector, CollectionChangeDetector>();
            
            // Add Chroma service
            services.AddSingleton<IChromaDbService>(sp => 
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var serverConfigOptions = Options.Create(serverConfig);
                return new ChromaDbService(loggerFactory.CreateLogger<ChromaDbService>(), serverConfigOptions);
            });
            
            return services.BuildServiceProvider();
        }

        private async Task InitializeTestEnvironmentAsync()
        {
            // Initialize deletion tracker (following PP13-60 pattern)
            await _deletionTracker.InitializeAsync(_tempDir);
            
            // Initialize collection change detector
            await _collectionChangeDetector.InitializeAsync(_tempDir);
            
            // Create collections table in Dolt - matches other integration test patterns
            var collectionsTableQuery = @"
                CREATE TABLE IF NOT EXISTS collections (
                    collection_name VARCHAR(255) PRIMARY KEY,
                    display_name VARCHAR(255),
                    metadata TEXT,
                    created_at DATETIME DEFAULT NOW(),
                    updated_at DATETIME DEFAULT NOW()
                )";
            
            try
            {
                await _doltCli.ExecuteAsync(collectionsTableQuery);
                TestContext.WriteLine("✓ Created collections table in Dolt");
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Warning: Failed to create collections table: {ex.Message}");
            }
            
            TestContext.WriteLine($"✓ Initialized test environment with complete service stack");
        }

        private async Task CreateTestCollection(string collectionName, string description)
        {
            try
            {
                await _chromaService.CreateCollectionAsync(collectionName, metadata: new Dictionary<string, object> { { "description", description } });
                TestContext.WriteLine($"✓ Created ChromaDB collection: {collectionName}");
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Warning: Failed to create collection {collectionName}: {ex.Message}");
            }
        }

        private async Task CreateDoltCollection(string collectionName, Dictionary<string, object> metadata)
        {
            try
            {
                var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
                var sql = $@"
                    INSERT INTO collections (collection_name, metadata, created_at, updated_at) 
                    VALUES ('{collectionName.Replace("'", "''")}', '{metadataJson.Replace("'", "''")}', NOW(), NOW())
                    ON DUPLICATE KEY UPDATE metadata = '{metadataJson.Replace("'", "''")}', updated_at = NOW()";
                
                await _doltCli.QueryAsync<dynamic>(sql);
                TestContext.WriteLine($"✓ Created Dolt collection: {collectionName}");
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Warning: Failed to create Dolt collection {collectionName}: {ex.Message}");
            }
        }

        #endregion
    }
}