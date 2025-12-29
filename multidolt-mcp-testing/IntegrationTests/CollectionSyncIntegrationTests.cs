using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using DMMS.Models;
using DMMS.Services;
using DMMS.Tools;

namespace DMMS.Testing.IntegrationTests;

[TestFixture]
public class CollectionSyncIntegrationTests
{
    private string _tempDir = null!;
    private ServiceProvider _serviceProvider = null!;
    private IDeletionTracker _deletionTracker = null!;
    private ICollectionChangeDetector _collectionChangeDetector = null!;
    private IChromaDbService _chromaService = null!;
    private ISyncManagerV2 _syncManager = null!;
    private ChromaDeleteCollectionTool _deleteCollectionTool = null!;
    private ChromaModifyCollectionTool _modifyCollectionTool = null!;
    private IDoltCli _doltCli = null!;
    private DoltConfiguration _doltConfig = null!;
    private readonly string _testCollectionName = "pp13-61-test-collection";

    [SetUp]
    public async Task SetUp()
    {
        // Initialize Python context if not already done
        if (!PythonContext.IsInitialized)
        {
            PythonContext.Initialize();
        }

        await InitializeTestEnvironmentAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private async Task InitializeTestEnvironmentAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PP13-61-CollectionSyncTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        TestContext.WriteLine($"Using temp directory: {_tempDir}");

        // Create and configure Dolt CLI FIRST (following the working pattern)
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        
        _doltConfig = new DoltConfiguration
        {
            DoltExecutablePath = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? @"C:\Program Files\Dolt\bin\dolt.exe" 
                : "dolt",
            RepositoryPath = _tempDir,
            CommandTimeoutMs = 45000,
            EnableDebugLogging = true
        };
        
        _doltCli = new DoltCli(Options.Create(_doltConfig), loggerFactory.CreateLogger<DoltCli>());
        
        // Critical: Initialize Dolt BEFORE any services that depend on it
        TestContext.WriteLine("Initializing Dolt repository...");
        var initResult = await _doltCli.InitAsync();
        if (!initResult.Success)
        {
            throw new InvalidOperationException($"Failed to initialize Dolt repository: {initResult.Error}");
        }
        TestContext.WriteLine("✓ Dolt repository initialized successfully");

        // Now create service provider with proper configuration
        _serviceProvider = CreateServiceProvider();
        
        _deletionTracker = _serviceProvider.GetRequiredService<IDeletionTracker>();
        _collectionChangeDetector = _serviceProvider.GetRequiredService<ICollectionChangeDetector>();
        _chromaService = _serviceProvider.GetRequiredService<IChromaDbService>();
        _syncManager = _serviceProvider.GetRequiredService<ISyncManagerV2>();
        _deleteCollectionTool = _serviceProvider.GetRequiredService<ChromaDeleteCollectionTool>();
        _modifyCollectionTool = _serviceProvider.GetRequiredService<ChromaModifyCollectionTool>();

        TestContext.WriteLine("✓ Service provider initialized");

        // Initialize services after Dolt is ready
        await _deletionTracker.InitializeAsync(_tempDir);
        await _collectionChangeDetector.InitializeAsync(_tempDir);
        
        // Create basic Dolt schema
        await CreateBasicDoltSchemaAsync();
        
        TestContext.WriteLine("✓ Initialized test environment with complete service stack");
    }

    private ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        
        // Add logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        
        // Configure Server
        var chromaDataPath = Path.Combine(_tempDir, "chroma_data");
        Directory.CreateDirectory(chromaDataPath);
        
        var serverConfig = new ServerConfiguration
        {
            ChromaDataPath = chromaDataPath,
            DataPath = _tempDir,
            ChromaMode = "persistent"
        };
        
        services.Configure<ServerConfiguration>(opts =>
        {
            opts.ChromaDataPath = serverConfig.ChromaDataPath;
            opts.DataPath = serverConfig.DataPath;
            opts.ChromaMode = serverConfig.ChromaMode;
        });
        
        services.Configure<DoltConfiguration>(opts =>
        {
            opts.RepositoryPath = _doltConfig.RepositoryPath;
            opts.DoltExecutablePath = _doltConfig.DoltExecutablePath;
            opts.CommandTimeoutMs = _doltConfig.CommandTimeoutMs;
            opts.EnableDebugLogging = _doltConfig.EnableDebugLogging;
        });
        
        // Add core services - use the already initialized DoltCli instance
        services.AddSingleton<IDoltCli>(_doltCli);
        services.AddSingleton(serverConfig);
        services.AddSingleton(Options.Create(serverConfig));
        services.AddSingleton(_doltConfig);
        services.AddSingleton(Options.Create(_doltConfig));
        
        // Register services
        services.AddSingleton<IDeletionTracker, SqliteDeletionTracker>();
        services.AddSingleton<ICollectionChangeDetector, CollectionChangeDetector>();
        services.AddSingleton<ISyncManagerV2, SyncManagerV2>();
        
        // Add Chroma service using the working pattern
        services.AddSingleton<IChromaDbService>(sp => 
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var serverConfigOptions = Options.Create(serverConfig);
            return new ChromaDbService(loggerFactory.CreateLogger<ChromaDbService>(), serverConfigOptions);
        });
        
        // Register tools
        services.AddTransient<ChromaDeleteCollectionTool>();
        services.AddTransient<ChromaModifyCollectionTool>();

        return services.BuildServiceProvider();
    }

    private async Task CreateBasicDoltSchemaAsync()
    {
        // Create collections table - using the schema pattern that matches CollectionChangeDetector expectations
        var collectionsTableQuery = @"
            CREATE TABLE IF NOT EXISTS collections (
                collection_name VARCHAR(255) PRIMARY KEY,
                display_name VARCHAR(255),
                metadata TEXT,
                created_at DATETIME DEFAULT NOW(),
                updated_at DATETIME DEFAULT NOW()
            )";
        
        await _doltCli.ExecuteAsync(collectionsTableQuery);
        
        // Create documents table for cascade deletion tests
        await _doltCli.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS documents (
                doc_id VARCHAR(255), 
                collection_name VARCHAR(255), 
                content_hash VARCHAR(255), 
                content TEXT, 
                metadata JSON, 
                PRIMARY KEY (doc_id, collection_name)
            )");
        
        // Commit initial schema
        await _doltCli.CommitAsync("Initial schema");
        
        TestContext.WriteLine("✓ Created basic Dolt schema");
    }

    [Test]
    public async Task EndToEnd_CollectionDeletionWorkflow_ShouldTrackAndSyncCorrectly()
    {
        TestContext.WriteLine("=== Starting End-to-End Collection Deletion Test ===");

        // Step 1: Create test collection in ChromaDB
        await _chromaService.CreateCollectionAsync(_testCollectionName, new Dictionary<string, object> { ["test"] = true });
        
        // Add the collection to Dolt collections table to simulate existing sync state
        var metadataJson = System.Text.Json.JsonSerializer.Serialize(new { test = true });
        await _doltCli.ExecuteAsync(
            $"INSERT INTO collections (collection_name, metadata) VALUES ('{_testCollectionName}', '{metadataJson.Replace("'", "''")}')");
        
        // Add some test documents to simulate content
        await _doltCli.ExecuteAsync(
            $"INSERT INTO documents (doc_id, collection_name, content_hash, content, metadata) VALUES ('doc1', '{_testCollectionName}', 'hash1', 'content1', '{{}}')");
        await _doltCli.ExecuteAsync(
            $"INSERT INTO documents (doc_id, collection_name, content_hash, content, metadata) VALUES ('doc2', '{_testCollectionName}', 'hash2', 'content2', '{{}}')");

        TestContext.WriteLine($"✓ Created test collection '{_testCollectionName}' with 2 documents");

        // Step 2: Use ChromaDeleteCollectionTool to delete the collection (this should track the deletion)
        var deleteResult = await _deleteCollectionTool.DeleteCollection(_testCollectionName);
        var deleteResultObj = deleteResult as dynamic;
        
        Assert.That(deleteResultObj?.success, Is.True);
        Assert.That(deleteResultObj?.tracked, Is.True);
        TestContext.WriteLine("✓ Collection deletion tracked successfully");

        // Step 3: Verify tracking record was created
        var pendingDeletions = await _deletionTracker.GetPendingCollectionDeletionsAsync(_tempDir);
        Assert.That(pendingDeletions.Count, Is.EqualTo(1));
        Assert.That(pendingDeletions[0].CollectionName, Is.EqualTo(_testCollectionName));
        Assert.That(pendingDeletions[0].OperationType, Is.EqualTo("deletion"));
        TestContext.WriteLine("✓ Deletion tracking record created");

        // Step 4: Use collection change detector to verify changes are detected
        var collectionChanges = await _collectionChangeDetector.DetectCollectionChangesAsync();
        var hasPendingChanges = await _collectionChangeDetector.HasPendingCollectionChangesAsync();
        
        Assert.That(hasPendingChanges, Is.True);
        Assert.That(collectionChanges.DeletedCollections.Count, Is.GreaterThan(0));
        TestContext.WriteLine("✓ Collection changes detected");

        // Step 5: Use SyncManagerV2 to sync the collection changes
        var syncResult = await _syncManager.SyncCollectionChangesAsync();
        
        Assert.That(syncResult.Success, Is.True);
        Assert.That(syncResult.CollectionsDeleted, Is.EqualTo(1));
        Assert.That(syncResult.DocumentsDeletedByCollectionDeletion, Is.EqualTo(2)); // Cascade deletion
        Assert.That(syncResult.DeletedCollectionNames, Contains.Item(_testCollectionName));
        TestContext.WriteLine($"✓ Collection sync completed: {syncResult.GetSummary()}");

        // Step 6: Verify collection and documents were removed from Dolt
        var collectionsAfterSync = await _doltCli.QueryAsync<Dictionary<string, object>>(
            $"SELECT * FROM collections WHERE collection_name = '{_testCollectionName}'");
        var documentsAfterSync = await _doltCli.QueryAsync<Dictionary<string, object>>(
            $"SELECT * FROM documents WHERE collection_name = '{_testCollectionName}'");
        
        Assert.That(collectionsAfterSync.Count(), Is.EqualTo(0));
        Assert.That(documentsAfterSync.Count(), Is.EqualTo(0));
        TestContext.WriteLine("✓ Collection and documents removed from Dolt");

        // Step 7: Verify tracking records were cleaned up
        var pendingDeletionsAfterSync = await _deletionTracker.GetPendingCollectionDeletionsAsync(_tempDir);
        Assert.That(pendingDeletionsAfterSync.Count, Is.EqualTo(0));
        TestContext.WriteLine("✓ Tracking records cleaned up");

        // Step 8: Verify no more pending changes
        var hasPendingChangesAfterSync = await _collectionChangeDetector.HasPendingCollectionChangesAsync();
        Assert.That(hasPendingChangesAfterSync, Is.False);
        TestContext.WriteLine("✓ No pending changes remaining");

        TestContext.WriteLine("=== End-to-End Collection Deletion Test PASSED ===");
    }

    [Test]
    public async Task EndToEnd_CollectionRenameWorkflow_ShouldTrackAndSyncCorrectly()
    {
        TestContext.WriteLine("=== Starting End-to-End Collection Rename Test ===");

        const string originalName = "original-collection";
        const string newName = "renamed-collection";

        // Step 1: Create test collection in ChromaDB
        await _chromaService.CreateCollectionAsync(originalName, new Dictionary<string, object> { ["version"] = 1 });
        
        // Add the collection to Dolt
        var metadataJson = System.Text.Json.JsonSerializer.Serialize(new { version = 1 });
        await _doltCli.ExecuteAsync(
            $"INSERT INTO collections (collection_name, metadata) VALUES ('{originalName}', '{metadataJson.Replace("'", "''")}')");
        
        // Add a test document
        await _doltCli.ExecuteAsync(
            $"INSERT INTO documents (doc_id, collection_name, content_hash, content, metadata) VALUES ('doc1', '{originalName}', 'hash1', 'content1', '{{}}')");

        TestContext.WriteLine($"✓ Created test collection '{originalName}' with 1 document");

        // Step 2: Use ChromaModifyCollectionTool to rename the collection (this should track the rename)
        var modifyResult = await _modifyCollectionTool.ModifyCollection(originalName, newName);
        var modifyResultObj = modifyResult as dynamic;
        
        Assert.That(modifyResultObj?.tracked, Is.True);
        Assert.That(modifyResultObj?.tracking_details?.operation_type, Is.EqualTo("rename"));
        TestContext.WriteLine("✓ Collection rename tracked successfully");

        // Step 3: Verify tracking record was created
        var pendingChanges = await _deletionTracker.GetPendingCollectionDeletionsAsync(_tempDir);
        Assert.That(pendingChanges.Count, Is.EqualTo(1));
        Assert.That(pendingChanges[0].CollectionName, Is.EqualTo(originalName));
        Assert.That(pendingChanges[0].OperationType, Is.EqualTo("rename"));
        Assert.That(pendingChanges[0].OriginalName, Is.EqualTo(originalName));
        Assert.That(pendingChanges[0].NewName, Is.EqualTo(newName));
        TestContext.WriteLine("✓ Rename tracking record created");

        // Step 4: Use SyncManagerV2 to sync the collection changes
        var syncResult = await _syncManager.SyncCollectionChangesAsync();
        
        Assert.That(syncResult.Success, Is.True);
        Assert.That(syncResult.CollectionsRenamed, Is.EqualTo(1));
        Assert.That(syncResult.RenamedCollectionNames, Contains.Item($"{originalName} -> {newName}"));
        TestContext.WriteLine($"✓ Collection rename sync completed: {syncResult.GetSummary()}");

        // Step 5: Verify collection was renamed in Dolt tables
        var collectionsAfterRename = await _doltCli.QueryAsync<Dictionary<string, object>>(
            $"SELECT * FROM collections WHERE collection_name = '{newName}'");
        var oldCollections = await _doltCli.QueryAsync<Dictionary<string, object>>(
            $"SELECT * FROM collections WHERE collection_name = '{originalName}'");
        var documentsAfterRename = await _doltCli.QueryAsync<Dictionary<string, object>>(
            $"SELECT * FROM documents WHERE collection_name = '{newName}'");
        
        Assert.That(collectionsAfterRename.Count(), Is.EqualTo(1));
        Assert.That(oldCollections.Count(), Is.EqualTo(0));
        Assert.That(documentsAfterRename.Count(), Is.EqualTo(1));
        TestContext.WriteLine("✓ Collection and documents renamed in Dolt");

        // Step 6: Verify tracking records were cleaned up
        var pendingChangesAfterSync = await _deletionTracker.GetPendingCollectionDeletionsAsync(_tempDir);
        Assert.That(pendingChangesAfterSync.Count, Is.EqualTo(0));
        TestContext.WriteLine("✓ Tracking records cleaned up");

        TestContext.WriteLine("=== End-to-End Collection Rename Test PASSED ===");
    }

    [Test]
    public async Task EndToEnd_ProcessCommitAsync_WithCollectionChanges_ShouldIncludeInCommit()
    {
        TestContext.WriteLine("=== Starting End-to-End ProcessCommit with Collection Changes Test ===");

        // Step 1: Create and delete a collection to generate collection changes
        await _chromaService.CreateCollectionAsync(_testCollectionName);
        
        // Add to Dolt to simulate sync state
        await _doltCli.ExecuteAsync(
            $"INSERT INTO collections (collection_name, metadata) VALUES ('{_testCollectionName}', '{{}}')");
        
        // Delete the collection using the tool (tracks deletion)
        await _deleteCollectionTool.DeleteCollection(_testCollectionName);
        TestContext.WriteLine($"✓ Created and deleted collection '{_testCollectionName}' to generate changes");

        // Step 2: Use ProcessCommitAsync which should include collection changes
        var commitResult = await _syncManager.ProcessCommitAsync("Test commit with collection changes", autoStageFromChroma: false);
        
        Assert.That(commitResult.Success, Is.True);
        Assert.That(commitResult.CommitHash, Is.Not.Null);
        
        // Check if collection changes were included in the result data
        var resultData = commitResult.Data as dynamic;
        if (resultData?.CollectionChanges != null)
        {
            TestContext.WriteLine("✓ Collection changes included in commit result");
        }
        
        TestContext.WriteLine($"✓ Commit completed with hash: {commitResult.CommitHash}");

        // Step 3: Verify collection was removed from Dolt
        var collectionsAfterCommit = await _doltCli.QueryAsync<Dictionary<string, object>>(
            $"SELECT * FROM collections WHERE collection_name = '{_testCollectionName}'");
        
        Assert.That(collectionsAfterCommit.Count(), Is.EqualTo(0));
        TestContext.WriteLine("✓ Collection removed from Dolt during commit");

        // Step 4: Verify tracking records were cleaned up
        var pendingChangesAfterCommit = await _deletionTracker.GetPendingCollectionDeletionsAsync(_tempDir);
        Assert.That(pendingChangesAfterCommit.Count, Is.EqualTo(0));
        TestContext.WriteLine("✓ Tracking records cleaned up during commit");

        TestContext.WriteLine("=== End-to-End ProcessCommit Test PASSED ===");
    }

    [Test]
    public async Task EndToEnd_MultipleCollectionOperations_ShouldProcessAllCorrectly()
    {
        TestContext.WriteLine("=== Starting End-to-End Multiple Collection Operations Test ===");

        const string deleteCollection = "delete-me";
        const string renameCollection = "rename-me"; 
        const string renamedCollection = "renamed-collection";

        // Step 1: Create multiple test collections
        await _chromaService.CreateCollectionAsync(deleteCollection);
        await _chromaService.CreateCollectionAsync(renameCollection);
        
        // Add to Dolt
        await _doltCli.ExecuteAsync(
            $"INSERT INTO collections (collection_name, metadata) VALUES ('{deleteCollection}', '{{}}')");
        await _doltCli.ExecuteAsync(
            $"INSERT INTO collections (collection_name, metadata) VALUES ('{renameCollection}', '{{}}')");
        
        TestContext.WriteLine("✓ Created multiple test collections");

        // Step 2: Perform different operations
        await _deleteCollectionTool.DeleteCollection(deleteCollection);
        await _modifyCollectionTool.ModifyCollection(renameCollection, renamedCollection);
        
        TestContext.WriteLine("✓ Performed deletion and rename operations");

        // Step 3: Verify tracking records
        var pendingChanges = await _deletionTracker.GetPendingCollectionDeletionsAsync(_tempDir);
        Assert.That(pendingChanges.Count, Is.EqualTo(2));
        
        var deletionOp = pendingChanges.FirstOrDefault(p => p.OperationType == "deletion");
        var renameOp = pendingChanges.FirstOrDefault(p => p.OperationType == "rename");
        
        Assert.That(deletionOp.CollectionName, Is.EqualTo(deleteCollection));
        Assert.That(renameOp.OriginalName, Is.EqualTo(renameCollection));
        Assert.That(renameOp.NewName, Is.EqualTo(renamedCollection));
        
        TestContext.WriteLine("✓ Multiple operations tracked correctly");

        // Step 4: Sync all changes
        var syncResult = await _syncManager.SyncCollectionChangesAsync();
        
        Assert.That(syncResult.Success, Is.True);
        Assert.That(syncResult.CollectionsDeleted, Is.EqualTo(1));
        Assert.That(syncResult.CollectionsRenamed, Is.EqualTo(1));
        Assert.That(syncResult.TotalCollectionChanges, Is.EqualTo(2));
        
        TestContext.WriteLine($"✓ All operations synced: {syncResult.GetSummary()}");

        // Step 5: Verify final state
        var finalCollections = await _doltCli.QueryAsync<Dictionary<string, object>>("SELECT collection_name FROM collections");
        var collectionNames = finalCollections.Select(c => c["collection_name"].ToString()).ToList();
        
        Assert.That(collectionNames, Does.Not.Contain(deleteCollection));
        Assert.That(collectionNames, Does.Not.Contain(renameCollection));
        Assert.That(collectionNames, Contains.Item(renamedCollection));
        
        TestContext.WriteLine("✓ Final state verified - deletion and rename completed correctly");

        TestContext.WriteLine("=== End-to-End Multiple Collection Operations Test PASSED ===");
    }

    [Test]
    public async Task CollectionChangeDetector_ShouldDetectOrphanedCollections()
    {
        TestContext.WriteLine("=== Starting Collection Change Detector Orphaned Collections Test ===");

        // Step 1: Create collection in Dolt but not in ChromaDB (simulates deletion without tracking)
        const string orphanedCollection = "orphaned-collection";
        
        var metadataJson = System.Text.Json.JsonSerializer.Serialize(new { orphaned = true });
        await _doltCli.ExecuteAsync(
            $"INSERT INTO collections (collection_name, metadata) VALUES ('{orphanedCollection}', '{metadataJson.Replace("'", "''")}')");
        
        TestContext.WriteLine($"✓ Created orphaned collection '{orphanedCollection}' in Dolt only");

        // Step 2: Use collection change detector to find orphaned collections
        var changes = await _collectionChangeDetector.DetectCollectionChangesAsync();
        
        Assert.That(changes.DeletedCollections, Contains.Item(orphanedCollection));
        TestContext.WriteLine("✓ Orphaned collection detected by change detector");

        // Step 3: Verify HasPendingChanges returns true
        var hasPendingChanges = await _collectionChangeDetector.HasPendingCollectionChangesAsync();
        Assert.That(hasPendingChanges, Is.True);
        TestContext.WriteLine("✓ HasPendingChanges correctly identified orphaned collection");

        TestContext.WriteLine("=== Collection Change Detector Test PASSED ===");
    }
}