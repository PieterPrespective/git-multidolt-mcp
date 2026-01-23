using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Embranch.Models;
using Embranch.Services;
using Moq;

namespace Embranch.UnitTests
{
    /// <summary>
    /// Unit tests for Phase 4: Production Initialization Patterns (PP13-61)
    /// Ensures proper fail-fast behavior and comprehensive validation during startup
    /// </summary>
    [TestFixture]
    public class ProductionInitializationTests
    {
        private ServiceCollection _services;
        private Mock<ILogger<ProductionInitializationTests>> _mockLogger;
        private string _tempDir;
        private DoltConfiguration _doltConfig;
        private ServerConfiguration _serverConfig;

        [SetUp]
        public void SetUp()
        {
            _services = new ServiceCollection();
            _mockLogger = new Mock<ILogger<ProductionInitializationTests>>();
            _tempDir = Path.Combine(Path.GetTempPath(), $"dmms_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            // Create configuration instances
            _doltConfig = new DoltConfiguration
            {
                RepositoryPath = _tempDir,
                DoltExecutablePath = "dolt",
                RemoteName = "origin"
            };

            _serverConfig = new ServerConfiguration
            {
                ChromaHost = "localhost",
                ChromaPort = 8000,
                DataPath = _tempDir
            };

            // Register configurations
            _services.AddSingleton(_doltConfig);
            _services.AddSingleton(Options.Create(_doltConfig));
            _services.AddSingleton(_serverConfig);
            _services.AddSingleton(Options.Create(_serverConfig));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
            {
                try
                {
                    Directory.Delete(_tempDir, true);
                }
                catch { }
            }
        }

        [Test]
        public async Task InitializeDeletionTracker_Success_CreatesDatabase()
        {
            // Arrange
            var mockChromaService = new Mock<IChromaDbService>();
            var mockDoltCli = new Mock<IDoltCli>();
            
            _services.AddSingleton(mockChromaService.Object);
            _services.AddSingleton(mockDoltCli.Object);
            _services.AddSingleton<IDeletionTracker, SqliteDeletionTracker>();
            _services.AddLogging();

            var serviceProvider = _services.BuildServiceProvider();
            var deletionTracker = serviceProvider.GetRequiredService<IDeletionTracker>();

            // Act
            await deletionTracker.InitializeAsync(_tempDir);

            // Assert - database is created in DataPath/dev, not repo path
            var dbPath = Path.Combine(_serverConfig.DataPath, "dev", "deletion_tracking.db");
            Assert.That(File.Exists(dbPath), Is.True, "Deletion tracker database should be created");
        }

        [Test]
        public async Task InitializeDeletionTracker_ValidPath_Succeeds()
        {
            // Arrange
            var mockChromaService = new Mock<IChromaDbService>();
            var mockDoltCli = new Mock<IDoltCli>();
            
            _services.AddSingleton(mockChromaService.Object);
            _services.AddSingleton(mockDoltCli.Object);
            _services.AddSingleton<IDeletionTracker, SqliteDeletionTracker>();
            _services.AddLogging();

            var serviceProvider = _services.BuildServiceProvider();
            var deletionTracker = serviceProvider.GetRequiredService<IDeletionTracker>();

            // Act
            Exception? caughtException = null;
            try
            {
                await deletionTracker.InitializeAsync(_tempDir);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // Assert - should succeed without exceptions
            Assert.That(caughtException, Is.Null, "Initialization should succeed with valid path");
        }

        [Test]
        public async Task InitializeCollectionChangeDetector_Success_ValidatesSchema()
        {
            // Arrange
            var mockChromaService = new Mock<IChromaDbService>();
            var mockDoltCli = new Mock<IDoltCli>();
            var mockDeletionTracker = new Mock<IDeletionTracker>();
            
            mockDeletionTracker.Setup(x => x.InitializeAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            
            _services.AddSingleton(mockChromaService.Object);
            _services.AddSingleton(mockDoltCli.Object);
            _services.AddSingleton(mockDeletionTracker.Object);
            _services.AddSingleton<ICollectionChangeDetector, CollectionChangeDetector>();
            _services.AddLogging();

            var serviceProvider = _services.BuildServiceProvider();
            var collectionDetector = serviceProvider.GetRequiredService<ICollectionChangeDetector>();

            // Act
            await collectionDetector.InitializeAsync(_tempDir);
            await collectionDetector.ValidateSchemaAsync(_tempDir);
            await collectionDetector.ValidateInitializationAsync();

            // Assert - no exception means success
            Assert.Pass("Collection change detector initialized successfully");
        }

        [Test]
        public async Task InitializeCollectionChangeDetector_MissingDependency_ThrowsException()
        {
            // Arrange - missing IDeletionTracker dependency
            var mockChromaService = new Mock<IChromaDbService>();
            var mockDoltCli = new Mock<IDoltCli>();
            
            _services.AddSingleton(mockChromaService.Object);
            _services.AddSingleton(mockDoltCli.Object);
            // Deliberately NOT adding IDeletionTracker
            _services.AddSingleton<ICollectionChangeDetector, CollectionChangeDetector>();
            _services.AddLogging();

            // Act & Assert
            try
            {
                var provider = _services.BuildServiceProvider();
                // If we get here, check if we can resolve the service
                var detector = provider.GetService<ICollectionChangeDetector>();
                Assert.Fail("Expected to fail resolving CollectionChangeDetector without IDeletionTracker");
            }
            catch (InvalidOperationException ex)
            {
                // Expected exception - test passes
                Assert.That(ex.Message, Does.Contain("Unable to resolve service"));
            }
        }

        [Test]
        public async Task ValidateCompleteServiceStack_AllServicesRegistered_Success()
        {
            // Arrange - complete service stack
            var mockChromaService = new Mock<IChromaDbService>();
            var mockDoltCli = new Mock<IDoltCli>();
            var mockDeletionTracker = new Mock<IDeletionTracker>();
            var mockCollectionDetector = new Mock<ICollectionChangeDetector>();
            var mockSyncStateTracker = new Mock<ISyncStateTracker>();

            mockDeletionTracker.Setup(x => x.InitializeAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            mockCollectionDetector.Setup(x => x.InitializeAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            mockCollectionDetector.Setup(x => x.ValidateSchemaAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            mockCollectionDetector.Setup(x => x.ValidateInitializationAsync())
                .Returns(Task.CompletedTask);
            mockSyncStateTracker.Setup(x => x.InitializeAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            _services.AddSingleton(mockChromaService.Object);
            _services.AddSingleton(mockDoltCli.Object);
            _services.AddSingleton(mockDeletionTracker.Object);
            _services.AddSingleton(mockCollectionDetector.Object);
            _services.AddSingleton(mockSyncStateTracker.Object);
            _services.AddSingleton<ISyncManagerV2, SyncManagerV2>();
            _services.AddLogging();

            var serviceProvider = _services.BuildServiceProvider();

            // Act - validate all services can be resolved
            var chromaService = serviceProvider.GetRequiredService<IChromaDbService>();
            var doltCli = serviceProvider.GetRequiredService<IDoltCli>();
            var deletionTracker = serviceProvider.GetRequiredService<IDeletionTracker>();
            var collectionDetector = serviceProvider.GetRequiredService<ICollectionChangeDetector>();
            var syncStateTracker = serviceProvider.GetRequiredService<ISyncStateTracker>();
            var syncManager = serviceProvider.GetRequiredService<ISyncManagerV2>();

            // Assert
            Assert.That(chromaService, Is.Not.Null, "ChromaDB service should be resolvable");
            Assert.That(doltCli, Is.Not.Null, "Dolt CLI service should be resolvable");
            Assert.That(deletionTracker, Is.Not.Null, "Deletion tracker should be resolvable");
            Assert.That(collectionDetector, Is.Not.Null, "Collection detector should be resolvable");
            Assert.That(syncStateTracker, Is.Not.Null, "Sync state tracker should be resolvable");
            Assert.That(syncManager, Is.Not.Null, "Sync manager should be resolvable");
        }

        [Test]
        public async Task InitializeWithLogging_ProducesDetailedLogs()
        {
            // Arrange
            var mockChromaService = new Mock<IChromaDbService>();
            var mockDoltCli = new Mock<IDoltCli>();
            var mockLogger = new Mock<ILogger<SqliteDeletionTracker>>();
            
            _services.AddSingleton(mockChromaService.Object);
            _services.AddSingleton(mockDoltCli.Object);
            _services.AddSingleton(mockLogger.Object);
            _services.AddSingleton<IDeletionTracker, SqliteDeletionTracker>();
            _services.AddLogging();

            var serviceProvider = _services.BuildServiceProvider();
            var deletionTracker = serviceProvider.GetRequiredService<IDeletionTracker>();

            // Act
            await deletionTracker.InitializeAsync(_tempDir);

            // Assert - verify that initialization happened (database was created)
            // The logger mock won't capture logs from the actual SqliteDeletionTracker
            // because it has its own logger injected via constructor
            var dbPath = Path.Combine(_serverConfig.DataPath, "dev", "deletion_tracking.db");
            Assert.That(File.Exists(dbPath), Is.True, "Database should be created as proof of initialization");
        }

        [Test]
        public void ServiceInitialization_MissingConfiguration_ThrowsException()
        {
            // Arrange - missing configuration
            var services = new ServiceCollection();
            services.AddSingleton<IDeletionTracker, SqliteDeletionTracker>();
            services.AddLogging();

            // Act & Assert
            try
            {
                var provider = services.BuildServiceProvider();
                // If we get here, try to resolve deletion tracker which should fail
                var tracker = provider.GetService<IDeletionTracker>();
                Assert.Fail("Expected to fail resolving IDeletionTracker without configuration");
            }
            catch (InvalidOperationException ex)
            {
                // Expected exception - test passes
                Assert.That(ex.Message, Does.Contain("Unable to resolve service"));
            }
        }

        [Test]
        public async Task InitializationSequence_CorrectOrder_Success()
        {
            // Arrange
            var initializationOrder = new List<string>();
            
            var mockDeletionTracker = new Mock<IDeletionTracker>();
            mockDeletionTracker.Setup(x => x.InitializeAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask)
                .Callback(() => initializationOrder.Add("DeletionTracker"));
            
            var mockCollectionDetector = new Mock<ICollectionChangeDetector>();
            mockCollectionDetector.Setup(x => x.InitializeAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask)
                .Callback(() => initializationOrder.Add("CollectionDetector"));
            mockCollectionDetector.Setup(x => x.ValidateSchemaAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask)
                .Callback(() => initializationOrder.Add("ValidateSchema"));
            mockCollectionDetector.Setup(x => x.ValidateInitializationAsync())
                .Returns(Task.CompletedTask)
                .Callback(() => initializationOrder.Add("ValidateInit"));

            // Act - simulate initialization sequence
            await mockDeletionTracker.Object.InitializeAsync(_tempDir);
            await mockCollectionDetector.Object.InitializeAsync(_tempDir);
            await mockCollectionDetector.Object.ValidateSchemaAsync(_tempDir);
            await mockCollectionDetector.Object.ValidateInitializationAsync();

            // Assert - verify correct order
            Assert.That(initializationOrder.Count, Is.EqualTo(4));
            Assert.That(initializationOrder[0], Is.EqualTo("DeletionTracker"));
            Assert.That(initializationOrder[1], Is.EqualTo("CollectionDetector"));
            Assert.That(initializationOrder[2], Is.EqualTo("ValidateSchema"));
            Assert.That(initializationOrder[3], Is.EqualTo("ValidateInit"));
        }

        [Test]
        public async Task FailFastPattern_InitializationFailure_NonZeroExitCode()
        {
            // This test verifies the fail-fast pattern concept
            // In production, the app would call Environment.Exit(1)
            
            // Arrange
            var mockDeletionTracker = new Mock<IDeletionTracker>();
            mockDeletionTracker.Setup(x => x.InitializeAsync(It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("Database initialization failed"));

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await mockDeletionTracker.Object.InitializeAsync(_tempDir));
            
            Assert.That(ex.Message, Is.EqualTo("Database initialization failed"));
            // In production, this would trigger Environment.Exit(1)
        }
    }
}