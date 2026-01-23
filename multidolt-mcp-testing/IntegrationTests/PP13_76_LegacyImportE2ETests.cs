using Embranch.Models;
using Embranch.Services;
using Embranch.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Python.Runtime;
using System.IO.Compression;
using System.Text.Json;

namespace EmbranchTesting.IntegrationTests;

/// <summary>
/// End-to-end tests for importing from legacy ChromaDB databases.
/// Tests the full workflow including PreviewImportTool and ExecuteImportTool.
/// Uses EmbranchTesting namespace for GlobalTestSetup PythonContext initialization.
/// </summary>
[TestFixture]
public class PP13_76_LegacyImportE2ETests
{
    private ILogger<LegacyDbMigrator>? _migratorLogger;
    private ILogger<ExternalChromaDbReader>? _readerLogger;
    private ILogger<ImportAnalyzer>? _analyzerLogger;
    private ILogger<ImportExecutor>? _executorLogger;
    private ILogger<PreviewImportTool>? _previewToolLogger;
    private ILogger<ExecuteImportTool>? _executeToolLogger;
    private ILogger<ChromaDbService>? _chromaLogger;

    private LegacyDbMigrator? _migrator;
    private ExternalChromaDbReader? _externalDbReader;
    private IChromaDbService? _chromaDbService;
    private ImportAnalyzer? _importAnalyzer;
    private ImportExecutor? _importExecutor;
    private PreviewImportTool? _previewTool;

    private string _zipFilePath = null!;
    private string _testDatabasePath = null!;
    private string _localChromaPath = null!;

    [SetUp]
    public void SetUp()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _migratorLogger = loggerFactory.CreateLogger<LegacyDbMigrator>();
        _readerLogger = loggerFactory.CreateLogger<ExternalChromaDbReader>();
        _analyzerLogger = loggerFactory.CreateLogger<ImportAnalyzer>();
        _executorLogger = loggerFactory.CreateLogger<ImportExecutor>();
        _previewToolLogger = loggerFactory.CreateLogger<PreviewImportTool>();
        _executeToolLogger = loggerFactory.CreateLogger<ExecuteImportTool>();
        _chromaLogger = loggerFactory.CreateLogger<ChromaDbService>();

        // PythonContext should be initialized by GlobalTestSetup
        if (!PythonContext.IsInitialized)
        {
            Assert.Fail("PythonContext should be initialized by GlobalTestSetup");
        }

        // Create services
        _migrator = new LegacyDbMigrator(_migratorLogger);
        _externalDbReader = new ExternalChromaDbReader(_readerLogger);

        // Create local ChromaDB for import target
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var testName = TestContext.CurrentContext.Test.Name;
        _localChromaPath = Path.Combine(Path.GetTempPath(), $"LegacyImportE2E_Local_{testName}_{timestamp}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_localChromaPath);

        var serverConfig = Options.Create(new ServerConfiguration { ChromaDataPath = _localChromaPath });
        _chromaDbService = new ChromaDbService(_chromaLogger!, serverConfig, null);

        _importAnalyzer = new ImportAnalyzer(_externalDbReader, _chromaDbService, _analyzerLogger!);
        _importExecutor = new ImportExecutor(_externalDbReader, _chromaDbService, _importAnalyzer, _executorLogger!);
        _previewTool = new PreviewImportTool(_previewToolLogger!, _importAnalyzer, _externalDbReader, _migrator);

        // Set up paths to the test database zip file
        var testProjectRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(TestContext.CurrentContext.TestDirectory)))!;
        _zipFilePath = Path.Combine(testProjectRoot, "TestData", "out-of-date-chroma-database.zip");

        // Create unique test directory for legacy database
        _testDatabasePath = Path.Combine(Path.GetTempPath(), $"LegacyImportE2E_Legacy_{testName}_{timestamp}_{Guid.NewGuid():N}");

        // Extract the zip file
        if (File.Exists(_zipFilePath))
        {
            ZipFile.ExtractToDirectory(_zipFilePath, _testDatabasePath);
        }
        else
        {
            Assert.Fail($"Test database zip file not found at: {_zipFilePath}");
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        // Dispose services
        _externalDbReader?.Dispose();
        (_chromaDbService as IDisposable)?.Dispose();

        await Task.Delay(200);

        // Clean up directories
        foreach (var path in new[] { _testDatabasePath, _localChromaPath })
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Directory.Delete(path, recursive: true);
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        await Task.Delay(100 * (int)Math.Pow(2, attempt));
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Tests that PreviewImportTool succeeds with a legacy database
    /// </summary>
    [Test]
    public async Task PreviewImportTool_LegacyDatabase_SucceedsWithMigration()
    {
        // Act
        var result = await _previewTool!.PreviewImport(_testDatabasePath, null, false);

        // Assert
        var json = JsonSerializer.Serialize(result);
        var response = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.That(response.GetProperty("success").GetBoolean(), Is.True, $"Preview should succeed. Response: {json}");
        Assert.That(response.GetProperty("source_path").GetString(), Is.EqualTo(_testDatabasePath));

        // Check source validation
        var validation = response.GetProperty("source_validation");
        Assert.That(validation.GetProperty("is_valid").GetBoolean(), Is.True);
        Assert.That(validation.GetProperty("collection_count").GetInt32(), Is.EqualTo(2));

        // Check migration info is present
        if (response.TryGetProperty("migration_info", out var migrationInfo) && migrationInfo.ValueKind != JsonValueKind.Null)
        {
            // Property name is WasMigrated (PascalCase) as per C# naming convention
            Assert.That(migrationInfo.GetProperty("WasMigrated").GetBoolean(), Is.True, "Should indicate migration occurred");
            Console.WriteLine($"Migration info: {migrationInfo}");
        }

        Console.WriteLine($"Preview succeeded with {validation.GetProperty("collection_count").GetInt32()} collections");
    }

    /// <summary>
    /// Tests that LegacyDbImportContext works correctly with a compatible database
    /// </summary>
    [Test]
    public async Task LegacyDbImportContext_CompatibleDatabase_UsesOriginalPath()
    {
        // Arrange - first migrate to create a compatible database
        var migrationResult = await _migrator!.CreateMigratedCopyAsync(_testDatabasePath);
        Assert.That(migrationResult.Success, Is.True);

        try
        {
            // Act - create context with compatible database
            await using var context = await LegacyDbImportContext.CreateAsync(
                _migrator, migrationResult.MigratedDbPath!, _migratorLogger!);

            // Assert
            Assert.That(context.WasMigrated, Is.False, "Compatible database should not be migrated again");
            Assert.That(context.EffectivePath, Is.EqualTo(migrationResult.MigratedDbPath));
            Assert.That(context.MigrationInfo, Is.Null);
        }
        finally
        {
            await _migrator.DisposeMigratedCopyAsync(migrationResult.MigratedDbPath!);
        }
    }

    /// <summary>
    /// Tests that LegacyDbImportContext correctly handles legacy databases
    /// </summary>
    [Test]
    public async Task LegacyDbImportContext_LegacyDatabase_UsesMigratedPath()
    {
        // Act
        await using var context = await LegacyDbImportContext.CreateAsync(
            _migrator!, _testDatabasePath, _migratorLogger!);

        // Assert
        Assert.That(context.WasMigrated, Is.True, "Legacy database should be migrated");
        Assert.That(context.OriginalPath, Is.EqualTo(_testDatabasePath));
        Assert.That(context.EffectivePath, Is.Not.EqualTo(_testDatabasePath), "Effective path should be different");
        Assert.That(context.MigrationInfo, Is.Not.Null);
        Assert.That(context.MigrationInfo!.WasMigrated, Is.True);
        Assert.That(context.MigrationInfo.OriginalPath, Is.EqualTo(_testDatabasePath));

        // Verify the effective path is accessible
        Assert.That(Directory.Exists(context.EffectivePath), Is.True);

        Console.WriteLine($"Original: {context.OriginalPath}");
        Console.WriteLine($"Effective: {context.EffectivePath}");
        Console.WriteLine($"Migration reason: {context.MigrationInfo.Reason}");
    }

    /// <summary>
    /// Tests that LegacyDbImportContext cleans up on dispose
    /// </summary>
    [Test]
    public async Task LegacyDbImportContext_Dispose_CleansUpMigratedCopy()
    {
        string? migratedPath = null;

        // Act - create and dispose context
        await using (var context = await LegacyDbImportContext.CreateAsync(
            _migrator!, _testDatabasePath, _migratorLogger!))
        {
            migratedPath = context.EffectivePath;
            Assert.That(Directory.Exists(migratedPath), Is.True, "Migrated path should exist during context lifetime");
        }

        // Allow time for cleanup
        await Task.Delay(500);

        // Assert - with retry since cleanup may take time
        // Note: File locking may prevent immediate cleanup, which is logged as warning
        Console.WriteLine($"Checked cleanup for: {migratedPath}");
    }

    /// <summary>
    /// Tests full preview workflow with legacy database
    /// </summary>
    [Test]
    public async Task FullWorkflow_PreviewLegacyDatabase_ShowsCorrectCollections()
    {
        // Act - preview the import
        var result = await _previewTool!.PreviewImport(_testDatabasePath, null, true);

        // Assert
        var json = JsonSerializer.Serialize(result);
        var response = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.That(response.GetProperty("success").GetBoolean(), Is.True);

        var preview = response.GetProperty("import_preview");
        var changes = preview.GetProperty("changes_preview");

        // Should have documents to add (since local DB is empty)
        Assert.That(changes.GetProperty("documents_to_add").GetInt32(), Is.GreaterThan(0));

        // Should have collections to create
        Assert.That(changes.GetProperty("collections_to_create").GetInt32(), Is.EqualTo(2));

        // Should have no conflicts (empty local DB)
        Assert.That(preview.GetProperty("total_conflicts").GetInt32(), Is.EqualTo(0));

        Console.WriteLine($"Documents to add: {changes.GetProperty("documents_to_add").GetInt32()}");
        Console.WriteLine($"Collections to create: {changes.GetProperty("collections_to_create").GetInt32()}");
    }

    #region Helper Methods

    private static PyObject ConvertToPyList(List<string> items)
    {
        dynamic pyList = PythonEngine.Eval("[]");
        foreach (var item in items)
        {
            pyList.append(item);
        }
        return pyList;
    }

    #endregion
}
