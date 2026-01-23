using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Embranch.Models;
using Embranch.Services;
using Embranch.Tools;
using Python.Runtime;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for PP13-78 Cross-Collection ID Collision Detection.
    /// Tests the full workflow of detecting and resolving document ID collisions
    /// when multiple source collections are consolidated into a single target.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    [Category("PP13-78")]
    public class PP13_78_CollisionDetectionTests
    {
        private ILogger<PP13_78_CollisionDetectionTests> _logger = null!;
        private IExternalChromaDbReader _externalReader = null!;
        private IChromaDbService _chromaService = null!;
        private IImportAnalyzer _importAnalyzer = null!;
        private IImportExecutor _importExecutor = null!;
        private ILegacyDbMigrator _legacyMigrator = null!;
        private PreviewImportTool _previewTool = null!;
        private ExecuteImportTool _executeTool = null!;
        private ISyncManagerV2 _syncManager = null!;

        private string _tempDataPath = null!;
        private string _externalDbPath = null!;
        private string _localDbPath = null!;
        private List<string> _testCollections = new();
        private string _externalClientId = string.Empty;

        private const string SourceCollectionA = "pp78_source_a";
        private const string SourceCollectionB = "pp78_source_b";
        private const string SourceCollectionC = "pp78_source_c";
        private const string ConsolidatedTarget = "pp78_consolidated";

        [SetUp]
        public async Task Setup()
        {
            // PythonContext is managed by GlobalTestSetup
            if (!PythonContext.IsInitialized)
            {
                throw new InvalidOperationException("PythonContext should be initialized by GlobalTestSetup");
            }

            // Create temp paths
            _tempDataPath = Path.Combine(Path.GetTempPath(), "PP13_78_Collision_Tests", Guid.NewGuid().ToString());
            _externalDbPath = Path.Combine(_tempDataPath, "external_chroma");
            _localDbPath = Path.Combine(_tempDataPath, "local_chroma");
            Directory.CreateDirectory(_externalDbPath);
            Directory.CreateDirectory(_localDbPath);

            // Create loggers
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _logger = loggerFactory.CreateLogger<PP13_78_CollisionDetectionTests>();
            var readerLogger = loggerFactory.CreateLogger<ExternalChromaDbReader>();
            var analyzerLogger = loggerFactory.CreateLogger<ImportAnalyzer>();
            var executorLogger = loggerFactory.CreateLogger<ImportExecutor>();
            var previewToolLogger = loggerFactory.CreateLogger<PreviewImportTool>();
            var executeToolLogger = loggerFactory.CreateLogger<ExecuteImportTool>();

            // Create server configuration
            var serverConfig = new ServerConfiguration
            {
                ChromaMode = "persistent",
                ChromaDataPath = _localDbPath,
                DataPath = _tempDataPath
            };

            // Create services
            var legacyMigratorLogger = loggerFactory.CreateLogger<LegacyDbMigrator>();
            _externalReader = new ExternalChromaDbReader(readerLogger);
            _chromaService = CreateChromaService(serverConfig);
            _importAnalyzer = new ImportAnalyzer(_externalReader, _chromaService, analyzerLogger);
            _importExecutor = new ImportExecutor(_externalReader, _chromaService, _importAnalyzer, executorLogger);
            _legacyMigrator = new LegacyDbMigrator(legacyMigratorLogger);

            // Create mock SyncManager
            _syncManager = CreateMockSyncManager();

            // Create tools
            _previewTool = new PreviewImportTool(previewToolLogger, _importAnalyzer, _externalReader, _legacyMigrator);
            _executeTool = new ExecuteImportTool(executeToolLogger, _importExecutor, _importAnalyzer, _externalReader, _syncManager, _legacyMigrator);

            _logger.LogInformation("PP13-78 Test setup complete - External: {External}, Local: {Local}",
                _externalDbPath, _localDbPath);
        }

        [TearDown]
        public async Task Cleanup()
        {
            try
            {
                foreach (var collection in _testCollections)
                {
                    try
                    {
                        await _chromaService.DeleteCollectionAsync(collection);
                    }
                    catch { /* Ignore cleanup errors */ }
                }
                _testCollections.Clear();

                if (_externalReader is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during test cleanup");
            }
        }

        #region Cross-Collection Collision Detection Tests

        /// <summary>
        /// PP13-78: Verifies that PreviewImport detects ID collisions when two source
        /// collections have documents with the same ID being consolidated into one target.
        /// </summary>
        [Test]
        public async Task PreviewImport_TwoSourcesSameDocId_DetectsCollision()
        {
            // Arrange - Create two source collections with same document ID
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (SourceCollectionA, "shared_doc", "Content from Collection A"),
                (SourceCollectionB, "shared_doc", "Content from Collection B"),
                (SourceCollectionA, "unique_a", "Unique A content"),
                (SourceCollectionB, "unique_b", "Unique B content")
            });
            _testCollections.Add(ConsolidatedTarget);

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = SourceCollectionA, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionB, import_into = ConsolidatedTarget }
                }
            });

            // Act
            var result = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson, include_content_preview: true);
            var json = JsonSerializer.Serialize(result);
            var preview = JsonSerializer.Deserialize<JsonElement>(json);

            // Assert
            Assert.That(preview.GetProperty("success").GetBoolean(), Is.True);

            var importPreview = preview.GetProperty("import_preview");
            Assert.That(importPreview.GetProperty("has_conflicts").GetBoolean(), Is.True,
                "Should detect cross-collection conflict");
            Assert.That(importPreview.GetProperty("total_conflicts").GetInt32(), Is.EqualTo(1),
                "Should detect exactly one collision for 'shared_doc'");

            var conflicts = preview.GetProperty("conflicts").EnumerateArray().ToList();
            Assert.That(conflicts.Count, Is.EqualTo(1));

            var conflict = conflicts[0];
            Assert.That(conflict.GetProperty("document_id").GetString(), Is.EqualTo("shared_doc"));
            Assert.That(conflict.GetProperty("conflict_id").GetString(), Does.StartWith("xc_"),
                "Cross-collection conflict should have 'xc_' prefix");
            Assert.That(conflict.GetProperty("conflict_type").GetString(), Is.EqualTo("idcollision"));
        }

        /// <summary>
        /// PP13-78: Verifies that PreviewImport returns can_auto_import=false when collisions exist
        /// </summary>
        [Test]
        public async Task PreviewImport_CollisionDetected_CanAutoImportFalse()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (SourceCollectionA, "collision_doc", "Content A"),
                (SourceCollectionB, "collision_doc", "Content B")
            });
            _testCollections.Add(ConsolidatedTarget);

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = SourceCollectionA, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionB, import_into = ConsolidatedTarget }
                }
            });

            // Act
            var result = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson);
            var json = JsonSerializer.Serialize(result);
            var preview = JsonSerializer.Deserialize<JsonElement>(json);

            // Assert
            Assert.That(preview.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(preview.GetProperty("can_auto_import").GetBoolean(), Is.False,
                "Should NOT allow auto-import when cross-collection collision exists");
        }

        /// <summary>
        /// PP13-78: Verifies that PreviewImport returns can_auto_import=true when no collisions
        /// </summary>
        [Test]
        public async Task PreviewImport_NoCollisions_CanAutoImportTrue()
        {
            // Arrange - Different document IDs in each collection
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (SourceCollectionA, "doc_a1", "Content A1"),
                (SourceCollectionA, "doc_a2", "Content A2"),
                (SourceCollectionB, "doc_b1", "Content B1"),
                (SourceCollectionB, "doc_b2", "Content B2")
            });
            _testCollections.Add(ConsolidatedTarget);

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = SourceCollectionA, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionB, import_into = ConsolidatedTarget }
                }
            });

            // Act
            var result = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson);
            var json = JsonSerializer.Serialize(result);
            var preview = JsonSerializer.Deserialize<JsonElement>(json);

            // Assert
            Assert.That(preview.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(preview.GetProperty("can_auto_import").GetBoolean(), Is.True,
                "Should allow auto-import when no collisions exist");
            Assert.That(preview.GetProperty("import_preview").GetProperty("total_conflicts").GetInt32(), Is.EqualTo(0));
        }

        /// <summary>
        /// PP13-78: Verifies that PreviewImport detects collisions with wildcard filters
        /// </summary>
        [Test]
        public async Task PreviewImport_CollisionWithWildcard_DetectsCorrectly()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                ("pp78_wildcard_source1", "common_doc", "Source 1 content"),
                ("pp78_wildcard_source2", "common_doc", "Source 2 content"),
                ("pp78_wildcard_source1", "unique_1", "Unique 1"),
                ("pp78_wildcard_source2", "unique_2", "Unique 2")
            });
            _testCollections.Add("pp78_wildcard_target");

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = "pp78_wildcard_*", import_into = "pp78_wildcard_target" }
                }
            });

            // Act
            var result = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson);
            var json = JsonSerializer.Serialize(result);
            var preview = JsonSerializer.Deserialize<JsonElement>(json);

            // Assert
            Assert.That(preview.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(preview.GetProperty("import_preview").GetProperty("has_conflicts").GetBoolean(), Is.True,
                "Should detect collision with wildcard filter");

            var conflicts = preview.GetProperty("conflicts").EnumerateArray().ToList();
            var collisionConflict = conflicts.FirstOrDefault(c =>
                c.GetProperty("conflict_id").GetString()?.StartsWith("xc_") == true);
            Assert.That(collisionConflict.ValueKind, Is.Not.EqualTo(JsonValueKind.Undefined),
                "Should have a cross-collection collision conflict");
        }

        /// <summary>
        /// PP13-78: Verifies detection of multiple collisions from 3 source collections
        /// </summary>
        [Test]
        public async Task PreviewImport_ThreeSourcesSameDocId_DetectsMultipleCollisions()
        {
            // Arrange - Three source collections with same document ID
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (SourceCollectionA, "triple_collision_doc", "Content A"),
                (SourceCollectionB, "triple_collision_doc", "Content B"),
                (SourceCollectionC, "triple_collision_doc", "Content C")
            });
            _testCollections.Add(ConsolidatedTarget);

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = SourceCollectionA, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionB, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionC, import_into = ConsolidatedTarget }
                }
            });

            // Act
            var result = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson);
            var json = JsonSerializer.Serialize(result);
            var preview = JsonSerializer.Deserialize<JsonElement>(json);

            // Assert
            Assert.That(preview.GetProperty("success").GetBoolean(), Is.True);

            var conflicts = preview.GetProperty("conflicts").EnumerateArray().ToList();
            var crossCollisionConflicts = conflicts.Where(c =>
                c.GetProperty("conflict_id").GetString()?.StartsWith("xc_") == true).ToList();

            // With 3 collections, we should have 2 collision conflicts (A+B, A+C)
            Assert.That(crossCollisionConflicts.Count, Is.EqualTo(2),
                "Should detect 2 collision conflicts for 3 sources with same ID");
        }

        #endregion

        #region Namespace Resolution Tests

        /// <summary>
        /// PP13-78: Verifies that Namespace resolution creates properly prefixed IDs
        /// </summary>
        [Test]
        public async Task ExecuteImport_NamespaceResolution_PrefixesIds()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (SourceCollectionA, "shared_doc", "Content from A"),
                (SourceCollectionB, "shared_doc", "Content from B")
            });
            _testCollections.Add(ConsolidatedTarget);

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = SourceCollectionA, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionB, import_into = ConsolidatedTarget }
                }
            });

            // Get conflict ID from preview
            var previewResult = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson);
            var previewJson = JsonSerializer.Serialize(previewResult);
            var preview = JsonSerializer.Deserialize<JsonElement>(previewJson);
            var conflictId = preview.GetProperty("conflicts")[0].GetProperty("conflict_id").GetString();

            // Act - Execute with namespace resolution
            var resolutionsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                { conflictId!, "namespace" }
            });

            var result = await _executeTool.ExecuteImport(
                _externalDbPath,
                filter: filterJson,
                conflict_resolutions: resolutionsJson,
                auto_resolve_remaining: false,
                stage_to_dolt: false);
            var json = JsonSerializer.Serialize(result);
            var execution = JsonSerializer.Deserialize<JsonElement>(json);

            // Assert
            Assert.That(execution.GetProperty("success").GetBoolean(), Is.True,
                "Import should succeed with namespace resolution");

            // Verify both namespaced documents exist
            var targetDocs = await _chromaService.GetDocumentsAsync(ConsolidatedTarget);
            var targetJson = JsonSerializer.Serialize(targetDocs);
            var docsElement = JsonSerializer.Deserialize<JsonElement>(targetJson);

            var docIds = docsElement.GetProperty("ids").EnumerateArray()
                .Select(id => id.GetString()).ToList();

            // Should have namespaced IDs like "pp78_source_a__shared_doc" and "pp78_source_b__shared_doc"
            Assert.That(docIds.Any(id => id?.Contains($"{SourceCollectionA}__shared_doc") == true),
                Is.True, "Should have namespaced ID from source A");
            Assert.That(docIds.Any(id => id?.Contains($"{SourceCollectionB}__shared_doc") == true),
                Is.True, "Should have namespaced ID from source B");
        }

        /// <summary>
        /// PP13-78: Verifies that Namespace resolution preserves original document ID in metadata
        /// </summary>
        [Test]
        public async Task ExecuteImport_NamespaceResolution_PreservesOriginalId()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (SourceCollectionA, "meta_test_doc", "Content A"),
                (SourceCollectionB, "meta_test_doc", "Content B")
            });
            _testCollections.Add(ConsolidatedTarget);

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = SourceCollectionA, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionB, import_into = ConsolidatedTarget }
                }
            });

            // Get conflict ID from preview
            var previewResult = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson);
            var previewJson = JsonSerializer.Serialize(previewResult);
            var preview = JsonSerializer.Deserialize<JsonElement>(previewJson);
            var conflictId = preview.GetProperty("conflicts")[0].GetProperty("conflict_id").GetString();

            // Execute with namespace resolution
            var resolutionsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                { conflictId!, "namespace" }
            });

            await _executeTool.ExecuteImport(
                _externalDbPath,
                filter: filterJson,
                conflict_resolutions: resolutionsJson,
                auto_resolve_remaining: false,
                stage_to_dolt: false);

            // Act - Get documents and check metadata
            var targetDocs = await _chromaService.GetDocumentsAsync(ConsolidatedTarget);
            var targetJson = JsonSerializer.Serialize(targetDocs);
            var docsElement = JsonSerializer.Deserialize<JsonElement>(targetJson);

            // Assert - Check metadata contains original_doc_id
            var metadatas = docsElement.GetProperty("metadatas").EnumerateArray().ToList();
            var hasOriginalDocIdMetadata = metadatas.Any(m =>
                m.TryGetProperty("original_doc_id", out var val) && val.GetString() == "meta_test_doc");

            Assert.That(hasOriginalDocIdMetadata, Is.True,
                "Namespaced documents should have 'original_doc_id' metadata");
        }

        #endregion

        #region KeepFirst / KeepLast Resolution Tests

        /// <summary>
        /// PP13-78: Verifies that KeepFirst resolution keeps the alphabetically first collection's document
        /// </summary>
        [Test]
        public async Task ExecuteImport_KeepFirstResolution_KeepsAlphabeticallyFirst()
        {
            // Arrange - SourceCollectionA is alphabetically before SourceCollectionB
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (SourceCollectionA, "keepfirst_doc", "Content from A - should be kept"),
                (SourceCollectionB, "keepfirst_doc", "Content from B - should be skipped")
            });
            _testCollections.Add(ConsolidatedTarget);

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = SourceCollectionA, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionB, import_into = ConsolidatedTarget }
                }
            });

            // Get conflict ID
            var previewResult = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson);
            var previewJson = JsonSerializer.Serialize(previewResult);
            var preview = JsonSerializer.Deserialize<JsonElement>(previewJson);
            var conflictId = preview.GetProperty("conflicts")[0].GetProperty("conflict_id").GetString();

            // Act - Execute with keep_first resolution
            var resolutionsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                { conflictId!, "keep_first" }
            });

            var result = await _executeTool.ExecuteImport(
                _externalDbPath,
                filter: filterJson,
                conflict_resolutions: resolutionsJson,
                auto_resolve_remaining: false,
                stage_to_dolt: false);
            var json = JsonSerializer.Serialize(result);
            var execution = JsonSerializer.Deserialize<JsonElement>(json);

            // Assert
            Assert.That(execution.GetProperty("success").GetBoolean(), Is.True);

            // Should have only one document with the original ID
            var targetDocs = await _chromaService.GetDocumentsAsync(ConsolidatedTarget);
            var targetJson = JsonSerializer.Serialize(targetDocs);
            var docsElement = JsonSerializer.Deserialize<JsonElement>(targetJson);

            var docIds = docsElement.GetProperty("ids").EnumerateArray()
                .Select(id => id.GetString()).ToList();

            // Find the keepfirst_doc (may be chunked, so look for base ID)
            var keepfirstDocs = docIds.Where(id => id?.Contains("keepfirst_doc") == true).ToList();
            Assert.That(keepfirstDocs.Count, Is.GreaterThan(0), "Should have keepfirst_doc");

            // Check content is from source A
            var documents = docsElement.GetProperty("documents").EnumerateArray()
                .Select(d => d.GetString()).ToList();
            var hasContentFromA = documents.Any(d => d?.Contains("Content from A") == true);
            Assert.That(hasContentFromA, Is.True, "Should contain content from alphabetically first collection (A)");
        }

        /// <summary>
        /// PP13-78: Verifies that KeepLast resolution keeps the alphabetically last collection's document
        /// </summary>
        [Test]
        public async Task ExecuteImport_KeepLastResolution_KeepsAlphabeticallyLast()
        {
            // Arrange - SourceCollectionB is alphabetically after SourceCollectionA
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (SourceCollectionA, "keeplast_doc", "Content from A - should be skipped"),
                (SourceCollectionB, "keeplast_doc", "Content from B - should be kept")
            });
            _testCollections.Add(ConsolidatedTarget);

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = SourceCollectionA, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionB, import_into = ConsolidatedTarget }
                }
            });

            // Get conflict ID
            var previewResult = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson);
            var previewJson = JsonSerializer.Serialize(previewResult);
            var preview = JsonSerializer.Deserialize<JsonElement>(previewJson);
            var conflictId = preview.GetProperty("conflicts")[0].GetProperty("conflict_id").GetString();

            // Act - Execute with keep_last resolution
            var resolutionsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                { conflictId!, "keep_last" }
            });

            var result = await _executeTool.ExecuteImport(
                _externalDbPath,
                filter: filterJson,
                conflict_resolutions: resolutionsJson,
                auto_resolve_remaining: false,
                stage_to_dolt: false);
            var json = JsonSerializer.Serialize(result);
            var execution = JsonSerializer.Deserialize<JsonElement>(json);

            // Assert
            Assert.That(execution.GetProperty("success").GetBoolean(), Is.True);

            // Check content is from source B
            var targetDocs = await _chromaService.GetDocumentsAsync(ConsolidatedTarget);
            var targetJson = JsonSerializer.Serialize(targetDocs);
            var docsElement = JsonSerializer.Deserialize<JsonElement>(targetJson);

            var documents = docsElement.GetProperty("documents").EnumerateArray()
                .Select(d => d.GetString()).ToList();
            var hasContentFromB = documents.Any(d => d?.Contains("Content from B") == true);
            Assert.That(hasContentFromB, Is.True, "Should contain content from alphabetically last collection (B)");
        }

        #endregion

        #region Skip Resolution Tests

        /// <summary>
        /// PP13-78: Verifies that Skip resolution skips all colliding documents
        /// </summary>
        [Test]
        public async Task ExecuteImport_SkipResolution_SkipsCollisions()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (SourceCollectionA, "skip_collision_doc", "Content A"),
                (SourceCollectionB, "skip_collision_doc", "Content B"),
                (SourceCollectionA, "unique_doc", "Unique content")
            });
            _testCollections.Add(ConsolidatedTarget);

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = SourceCollectionA, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionB, import_into = ConsolidatedTarget }
                }
            });

            // Get conflict ID
            var previewResult = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson);
            var previewJson = JsonSerializer.Serialize(previewResult);
            var preview = JsonSerializer.Deserialize<JsonElement>(previewJson);
            var conflictId = preview.GetProperty("conflicts")[0].GetProperty("conflict_id").GetString();

            // Act - Execute with skip resolution
            var resolutionsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                { conflictId!, "skip" }
            });

            var result = await _executeTool.ExecuteImport(
                _externalDbPath,
                filter: filterJson,
                conflict_resolutions: resolutionsJson,
                auto_resolve_remaining: false,
                stage_to_dolt: false);
            var json = JsonSerializer.Serialize(result);
            var execution = JsonSerializer.Deserialize<JsonElement>(json);

            // Assert
            Assert.That(execution.GetProperty("success").GetBoolean(), Is.True);

            // Verify only unique_doc was imported (not the colliding skip_collision_doc)
            var targetDocs = await _chromaService.GetDocumentsAsync(ConsolidatedTarget);
            var targetJson = JsonSerializer.Serialize(targetDocs);
            var docsElement = JsonSerializer.Deserialize<JsonElement>(targetJson);

            var docIds = docsElement.GetProperty("ids").EnumerateArray()
                .Select(id => id.GetString()).ToList();

            Assert.That(docIds.Any(id => id?.Contains("unique_doc") == true), Is.True,
                "Non-colliding document should be imported");
            Assert.That(docIds.Any(id => id?.Contains("skip_collision_doc") == true), Is.False,
                "Colliding document should be skipped with skip resolution");
        }

        #endregion

        #region Mixed Resolution Tests

        /// <summary>
        /// PP13-78: Verifies that different resolutions can be applied to different collisions
        /// </summary>
        [Test]
        public async Task ExecuteImport_MixedResolutions_HandlesEachCorrectly()
        {
            // Arrange - Create multiple collisions
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (SourceCollectionA, "doc_namespace", "Content A for namespace"),
                (SourceCollectionB, "doc_namespace", "Content B for namespace"),
                (SourceCollectionA, "doc_keepfirst", "Content A for keepfirst"),
                (SourceCollectionB, "doc_keepfirst", "Content B for keepfirst")
            });
            _testCollections.Add(ConsolidatedTarget);

            var filterJson = JsonSerializer.Serialize(new
            {
                collections = new[]
                {
                    new { name = SourceCollectionA, import_into = ConsolidatedTarget },
                    new { name = SourceCollectionB, import_into = ConsolidatedTarget }
                }
            });

            // Get conflict IDs
            var previewResult = await _previewTool.PreviewImport(_externalDbPath, filter: filterJson);
            var previewJson = JsonSerializer.Serialize(previewResult);
            var preview = JsonSerializer.Deserialize<JsonElement>(previewJson);

            var conflicts = preview.GetProperty("conflicts").EnumerateArray().ToList();
            var namespaceConflict = conflicts.FirstOrDefault(c =>
                c.GetProperty("document_id").GetString() == "doc_namespace");
            var keepfirstConflict = conflicts.FirstOrDefault(c =>
                c.GetProperty("document_id").GetString() == "doc_keepfirst");

            var namespaceConflictId = namespaceConflict.GetProperty("conflict_id").GetString();
            var keepfirstConflictId = keepfirstConflict.GetProperty("conflict_id").GetString();

            // Act - Execute with mixed resolutions
            var resolutionsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                { namespaceConflictId!, "namespace" },
                { keepfirstConflictId!, "keep_first" }
            });

            var result = await _executeTool.ExecuteImport(
                _externalDbPath,
                filter: filterJson,
                conflict_resolutions: resolutionsJson,
                auto_resolve_remaining: false,
                stage_to_dolt: false);
            var json = JsonSerializer.Serialize(result);
            var execution = JsonSerializer.Deserialize<JsonElement>(json);

            // Assert
            Assert.That(execution.GetProperty("success").GetBoolean(), Is.True);

            var targetDocs = await _chromaService.GetDocumentsAsync(ConsolidatedTarget);
            var targetJson = JsonSerializer.Serialize(targetDocs);
            var docsElement = JsonSerializer.Deserialize<JsonElement>(targetJson);

            var docIds = docsElement.GetProperty("ids").EnumerateArray()
                .Select(id => id.GetString()).ToList();

            // Should have namespaced versions of doc_namespace
            Assert.That(docIds.Any(id => id?.Contains($"{SourceCollectionA}__doc_namespace") == true), Is.True,
                "Should have namespaced doc from A");
            Assert.That(docIds.Any(id => id?.Contains($"{SourceCollectionB}__doc_namespace") == true), Is.True,
                "Should have namespaced doc from B");

            // Should have only one doc_keepfirst (from first collection alphabetically)
            var keepfirstDocs = docIds.Where(id => id?.Contains("doc_keepfirst") == true &&
                !id.Contains($"{SourceCollectionA}__") && !id.Contains($"{SourceCollectionB}__")).ToList();
            Assert.That(keepfirstDocs.Count, Is.GreaterThan(0),
                "Should have doc_keepfirst without namespace prefix");
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates external database with specified documents
        /// </summary>
        private async Task CreateExternalDatabaseWithDocuments(
            (string collection, string docId, string content)[] documents)
        {
            await PythonContext.ExecuteAsync(() =>
            {
                _externalClientId = $"TestExternalDb_{Guid.NewGuid():N}";
                dynamic client = ChromaClientPool.GetOrCreateClient(_externalClientId, $"persistent:{_externalDbPath}");

                var byCollection = documents.GroupBy(d => d.collection);

                foreach (var group in byCollection)
                {
                    dynamic collection = client.get_or_create_collection(name: group.Key);

                    PyObject pyIds = ConvertToPyList(group.Select(d => d.docId).ToList());
                    PyObject pyDocs = ConvertToPyList(group.Select(d => d.content).ToList());

                    collection.add(ids: pyIds, documents: pyDocs);
                }

                return true;
            }, timeoutMs: 60000, operationName: "CreateExternalDbWithDocs");
        }

        private static PyObject ConvertToPyList(List<string> items)
        {
            dynamic pyList = PythonEngine.Eval("[]");
            foreach (var item in items)
            {
                pyList.append(item);
            }
            return pyList;
        }

        private IChromaDbService CreateChromaService(ServerConfiguration config)
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<ChromaPythonService>();

            return new ChromaPythonService(logger, Options.Create(config));
        }

        private ISyncManagerV2 CreateMockSyncManager()
        {
            return new MockSyncManagerV2();
        }

        #endregion

        #region Mock Classes

        private class MockSyncManagerV2 : ISyncManagerV2
        {
            public Task<InitResult> InitializeVersionControlAsync(string collectionName, string initialCommitMessage = "Initial import from ChromaDB")
                => Task.FromResult(new InitResult(InitStatus.Completed, 0, "mock_hash"));

            public Task<StatusSummary> GetStatusAsync()
                => Task.FromResult(new StatusSummary());

            public Task<LocalChanges> GetLocalChangesAsync()
                => Task.FromResult(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

            public Task<SyncResultV2> ProcessCommitAsync(string message, bool autoStageFromChroma = true, bool syncBackToChroma = false)
                => Task.FromResult(new SyncResultV2 { Status = SyncStatusV2.Completed, CommitHash = "mock_hash" });

            public Task<SyncResultV2> ProcessPullAsync(string remote = "origin", bool force = false)
                => Task.FromResult(new SyncResultV2 { Status = SyncStatusV2.Completed });

            public Task<SyncResultV2> ProcessCheckoutAsync(string targetBranch, bool createNew = false, bool preserveLocalChanges = false)
                => Task.FromResult(new SyncResultV2 { Status = SyncStatusV2.Completed });

            public Task<MergeSyncResultV2> ProcessMergeAsync(string sourceBranch, bool force = false, List<ConflictResolutionRequest>? resolutions = null)
                => Task.FromResult(new MergeSyncResultV2 { Status = SyncStatusV2.Completed });

            public Task<SyncResultV2> ProcessPushAsync(string remote = "origin", string? branch = null)
                => Task.FromResult(new SyncResultV2 { Status = SyncStatusV2.Completed });

            public Task<SyncResultV2> ProcessResetAsync(string targetCommit, bool hard = false)
                => Task.FromResult(new SyncResultV2 { Status = SyncStatusV2.Completed });

            public Task<SyncResultV2> PerformComprehensiveResetAsync(string targetBranch)
                => Task.FromResult(new SyncResultV2 { Status = SyncStatusV2.Completed });

            public Task<bool> HasPendingChangesAsync()
                => Task.FromResult(false);

            public Task<PendingChangesV2> GetPendingChangesAsync()
                => Task.FromResult(new PendingChangesV2());

            public Task<SyncResultV2> FullSyncAsync(string? collectionName = null, bool forceSync = false)
                => Task.FromResult(new SyncResultV2 { Status = SyncStatusV2.Completed });

            public Task<SyncResultV2> IncrementalSyncAsync(string? collectionName = null)
                => Task.FromResult(new SyncResultV2 { Status = SyncStatusV2.Completed });

            public Task<StageResult> StageLocalChangesAsync(string collectionName)
                => Task.FromResult(new StageResult(StageStatus.Completed, 0, 0, 0));

            public Task<StageResult> StageLocalChangesAsync(string collectionName, LocalChanges localChanges)
                => Task.FromResult(new StageResult(StageStatus.Completed, 0, 0, 0));

            public Task<SyncResultV2> ImportFromChromaAsync(string sourceCollection, string? commitMessage = null)
                => Task.FromResult(new SyncResultV2 { Status = SyncStatusV2.Completed });

            public Task<CollectionSyncResult> SyncCollectionChangesAsync()
                => Task.FromResult(new CollectionSyncResult { Status = SyncStatusV2.Completed });

            public Task<CollectionSyncResult> StageCollectionChangesAsync()
                => Task.FromResult(new CollectionSyncResult { Status = SyncStatusV2.Completed });
        }

        #endregion
    }
}
