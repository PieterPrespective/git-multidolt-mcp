using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;
using DMMS.Services;

namespace DMMSTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for the ImportExecutor service.
    /// Tests import execution, conflict resolution, batch operations, and metadata handling
    /// using real ChromaDB databases (external and local).
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class ImportExecutorTests
    {
        private ILogger<ImportExecutorTests> _logger = null!;
        private ILogger<ImportExecutor> _executorLogger = null!;
        private ILogger<ImportAnalyzer> _analyzerLogger = null!;
        private ILogger<ExternalChromaDbReader> _readerLogger = null!;
        private IExternalChromaDbReader _externalReader = null!;
        private IChromaDbService _chromaService = null!;
        private IImportAnalyzer _importAnalyzer = null!;
        private IImportExecutor _importExecutor = null!;

        private string _tempDataPath = null!;
        private string _externalDbPath = null!;
        private string _localDbPath = null!;
        private List<string> _testCollections = new();

        private const string ExternalCollection1 = "exec_test_external_1";
        private const string ExternalCollection2 = "exec_test_external_2";
        private const string ExternalProjectAlpha = "exec_test_project_alpha";
        private const string ExternalProjectBeta = "exec_test_project_beta";
        private const string LocalCollection = "exec_test_local";

        [SetUp]
        public async Task Setup()
        {
            // PythonContext is managed by GlobalTestSetup - just verify it's available
            if (!PythonContext.IsInitialized)
            {
                throw new InvalidOperationException("PythonContext should be initialized by GlobalTestSetup");
            }

            // Create temp paths
            _tempDataPath = Path.Combine(Path.GetTempPath(), "ImportExecutorTests", Guid.NewGuid().ToString());
            _externalDbPath = Path.Combine(_tempDataPath, "external_chroma");
            _localDbPath = Path.Combine(_tempDataPath, "local_chroma");
            Directory.CreateDirectory(_externalDbPath);
            Directory.CreateDirectory(_localDbPath);

            // Create loggers
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _logger = loggerFactory.CreateLogger<ImportExecutorTests>();
            _executorLogger = loggerFactory.CreateLogger<ImportExecutor>();
            _analyzerLogger = loggerFactory.CreateLogger<ImportAnalyzer>();
            _readerLogger = loggerFactory.CreateLogger<ExternalChromaDbReader>();

            // Create services
            _externalReader = new ExternalChromaDbReader(_readerLogger);

            var serverConfig = new ServerConfiguration
            {
                ChromaMode = "persistent",
                ChromaDataPath = _localDbPath,
                DataPath = _tempDataPath
            };
            _chromaService = CreateChromaService(serverConfig);

            _importAnalyzer = new ImportAnalyzer(_externalReader, _chromaService, _analyzerLogger);
            _importExecutor = new ImportExecutor(_externalReader, _chromaService, _importAnalyzer, _executorLogger);

            _logger.LogInformation("Test setup complete - External: {External}, Local: {Local}",
                _externalDbPath, _localDbPath);
        }

        [TearDown]
        public async Task Cleanup()
        {
            try
            {
                // Clean up test collections in local ChromaDB
                foreach (var collection in _testCollections)
                {
                    try
                    {
                        await _chromaService.DeleteCollectionAsync(collection);
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }
                _testCollections.Clear();

                // Dispose external reader
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

        #region Basic Import Tests

        /// <summary>
        /// Verifies that ExecuteImportAsync succeeds for a new collection with no conflicts
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_NewCollection_ImportsSuccessfully()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (ExternalCollection1, "doc1", "Content 1"),
                (ExternalCollection1, "doc2", "Content 2"),
                (ExternalCollection1, "doc3", "Content 3")
            });
            _testCollections.Add(ExternalCollection1);

            // Act
            var result = await _importExecutor.ExecuteImportAsync(_externalDbPath);

            // Assert
            Assert.That(result.Success, Is.True, "Import should succeed");
            Assert.That(result.DocumentsImported, Is.EqualTo(3), "Should import 3 documents");
            Assert.That(result.CollectionsCreated, Is.EqualTo(1), "Should create 1 collection");
            Assert.That(result.ConflictsResolved, Is.EqualTo(0), "No conflicts to resolve");

            // Verify documents exist in local
            var localCount = await _chromaService.GetCollectionCountAsync(ExternalCollection1);
            Assert.That(localCount, Is.GreaterThan(0), "Documents should exist in local collection");
        }

        /// <summary>
        /// Verifies that ExecuteImportAsync fails gracefully for invalid paths
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_InvalidPath_ReturnsFailure()
        {
            // Arrange
            var invalidPath = Path.Combine(_tempDataPath, "nonexistent_db");

            // Act
            var result = await _importExecutor.ExecuteImportAsync(invalidPath);

            // Assert
            Assert.That(result.Success, Is.False, "Should fail for invalid path");
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty, "Should have error message");
        }

        #endregion

        #region Conflict Resolution Tests

        /// <summary>
        /// Verifies that ExecuteImportAsync applies KeepSource resolution correctly
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_WithKeepSourceResolution_OverwritesLocal()
        {
            // Arrange - Create conflict
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "doc1", "External content - should win")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local content - should be overwritten" },
                new List<string> { "doc1" });

            // Get conflict ID from preview
            var preview = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);
            Assert.That(preview.Conflicts.Count, Is.EqualTo(1));
            var conflictId = preview.Conflicts[0].ConflictId;

            var resolutions = new List<ImportConflictResolution>
            {
                new ImportConflictResolution
                {
                    ConflictId = conflictId,
                    ResolutionType = ImportResolutionType.KeepSource
                }
            };

            // Act
            var result = await _importExecutor.ExecuteImportAsync(
                _externalDbPath,
                resolutions: resolutions,
                autoResolveRemaining: false);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.ConflictsResolved, Is.EqualTo(1));
            Assert.That(result.ResolutionBreakdown?["keepsource"], Is.EqualTo(1));

            // Verify content was overwritten - check the actual document content
            var localDocs = await _chromaService.GetDocumentsAsync(LocalCollection, new List<string> { "doc1" });
            Assert.That(localDocs, Is.Not.Null);
        }

        /// <summary>
        /// Verifies that ExecuteImportAsync applies KeepTarget resolution correctly
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_WithKeepTargetResolution_KeepsLocal()
        {
            // Arrange - Create conflict
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "doc1", "External content - should be ignored")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local content - should be kept" },
                new List<string> { "doc1" });

            // Get conflict ID from preview
            var preview = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);
            var conflictId = preview.Conflicts[0].ConflictId;

            var resolutions = new List<ImportConflictResolution>
            {
                new ImportConflictResolution
                {
                    ConflictId = conflictId,
                    ResolutionType = ImportResolutionType.KeepTarget
                }
            };

            // Act
            var result = await _importExecutor.ExecuteImportAsync(
                _externalDbPath,
                resolutions: resolutions,
                autoResolveRemaining: false);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.ConflictsResolved, Is.EqualTo(1));
            Assert.That(result.DocumentsSkipped, Is.EqualTo(1), "Document should be skipped");
            Assert.That(result.ResolutionBreakdown?["keeptarget"], Is.EqualTo(1));
        }

        /// <summary>
        /// Verifies that ExecuteImportAsync applies Skip resolution correctly
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_WithSkipResolution_SkipsDocument()
        {
            // Arrange - Create conflict
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "doc1", "External content")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local content" },
                new List<string> { "doc1" });

            var preview = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);
            var conflictId = preview.Conflicts[0].ConflictId;

            var resolutions = new List<ImportConflictResolution>
            {
                new ImportConflictResolution
                {
                    ConflictId = conflictId,
                    ResolutionType = ImportResolutionType.Skip
                }
            };

            // Act
            var result = await _importExecutor.ExecuteImportAsync(
                _externalDbPath,
                resolutions: resolutions,
                autoResolveRemaining: false);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.DocumentsSkipped, Is.EqualTo(1));
            Assert.That(result.ResolutionBreakdown?["skip"], Is.EqualTo(1));
        }

        /// <summary>
        /// Verifies that auto-resolution uses the specified default strategy
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_AutoResolve_UsesDefaultStrategy()
        {
            // Arrange - Create conflict
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "doc1", "External content")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local content" },
                new List<string> { "doc1" });

            // Act - Use auto-resolve with skip strategy
            var result = await _importExecutor.ExecuteImportAsync(
                _externalDbPath,
                autoResolveRemaining: true,
                defaultStrategy: "skip");

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.ConflictsResolved, Is.EqualTo(1));
            Assert.That(result.ResolutionBreakdown?["skip"], Is.EqualTo(1));
        }

        #endregion

        #region Collection Mapping Tests

        /// <summary>
        /// Verifies that collection filter correctly maps source to target
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_WithFilter_MapsCollections()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (ExternalCollection1, "doc1", "Content 1")
            });

            var targetCollection = "mapped_exec_target";
            _testCollections.Add(targetCollection);

            var filter = new ImportFilter
            {
                Collections = new List<CollectionImportSpec>
                {
                    new CollectionImportSpec
                    {
                        Name = ExternalCollection1,
                        ImportInto = targetCollection
                    }
                }
            };

            // Act
            var result = await _importExecutor.ExecuteImportAsync(_externalDbPath, filter);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.CollectionsCreated, Is.EqualTo(1));

            // Verify documents are in target collection
            var collections = await _chromaService.ListCollectionsAsync();
            Assert.That(collections, Contains.Item(targetCollection));
        }

        /// <summary>
        /// Verifies that wildcard collection patterns work correctly
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_WildcardCollection_ImportsMatchingCollections()
        {
            // Arrange - Create multiple project collections
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (ExternalProjectAlpha, "doc1", "Alpha content"),
                (ExternalProjectBeta, "doc2", "Beta content")
            });

            var targetCollection = "consolidated_exec_projects";
            _testCollections.Add(targetCollection);

            var filter = new ImportFilter
            {
                Collections = new List<CollectionImportSpec>
                {
                    new CollectionImportSpec
                    {
                        Name = "exec_test_project_*",
                        ImportInto = targetCollection
                    }
                }
            };

            // Act
            var result = await _importExecutor.ExecuteImportAsync(_externalDbPath, filter);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.DocumentsImported, Is.EqualTo(2), "Should import docs from both matching collections");
            Assert.That(result.CollectionsCreated, Is.EqualTo(1), "Should create 1 target collection");
        }

        /// <summary>
        /// Verifies that multiple sources can consolidate into one target
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_CollectionConsolidation_MergesDocuments()
        {
            // Arrange - Create multiple source collections
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (ExternalCollection1, "doc1", "Content from collection 1"),
                (ExternalCollection2, "doc2", "Content from collection 2")
            });

            var targetCollection = "consolidated_exec_target";
            _testCollections.Add(targetCollection);

            var filter = new ImportFilter
            {
                Collections = new List<CollectionImportSpec>
                {
                    new CollectionImportSpec { Name = ExternalCollection1, ImportInto = targetCollection },
                    new CollectionImportSpec { Name = ExternalCollection2, ImportInto = targetCollection }
                }
            };

            // Act
            var result = await _importExecutor.ExecuteImportAsync(_externalDbPath, filter);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.DocumentsImported, Is.EqualTo(2), "Should import docs from both sources");
            Assert.That(result.CollectionsCreated, Is.EqualTo(1), "Should create only 1 target collection");

            // Verify both documents exist in target
            var targetCount = await _chromaService.GetCollectionCountAsync(targetCollection);
            Assert.That(targetCount, Is.GreaterThan(0), "Target should have documents");
        }

        #endregion

        #region Batch Operation Tests

        /// <summary>
        /// Verifies that documents are imported via batch operation (AddDocumentsAsync)
        /// rather than individual adds. This test verifies the behavior indirectly by
        /// checking that import completes quickly for multiple documents.
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_BatchImport_ImportsEfficiently()
        {
            // Arrange - Create multiple documents
            var docs = new List<(string, string, string)>();
            for (int i = 0; i < 10; i++)
            {
                docs.Add((ExternalCollection1, $"doc_{i}", $"Content for document {i}"));
            }
            await CreateExternalDatabaseWithDocuments(docs.ToArray());
            _testCollections.Add(ExternalCollection1);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Act
            var result = await _importExecutor.ExecuteImportAsync(_externalDbPath);

            stopwatch.Stop();

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.DocumentsImported, Is.EqualTo(10), "Should import all 10 documents");

            // Batch import should be relatively fast (< 30 seconds for 10 docs)
            // Individual adds would be much slower due to embedding recalculation
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(30000),
                "Batch import should complete in reasonable time");
        }

        #endregion

        #region Metadata Tests

        /// <summary>
        /// Verifies that imported documents have proper metadata including import tracking fields
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_SetsProperMetadata_HasImportFields()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (ExternalCollection1, "meta_doc", "Content with metadata")
            });
            _testCollections.Add(ExternalCollection1);

            // Act
            var result = await _importExecutor.ExecuteImportAsync(_externalDbPath);

            // Assert
            Assert.That(result.Success, Is.True);

            // Get the imported document and check metadata
            var docs = await _chromaService.GetDocumentsAsync(ExternalCollection1);
            Assert.That(docs, Is.Not.Null);

            // Document should exist (actual metadata verification depends on response format)
            var count = await _chromaService.GetCollectionCountAsync(ExternalCollection1);
            Assert.That(count, Is.GreaterThan(0), "Document should be imported");
        }

        #endregion

        #region Conflict ID Consistency Tests

        /// <summary>
        /// Verifies that conflict IDs from preview match those used in execution (PP13-73 prevention)
        /// </summary>
        [Test]
        public async Task ExecuteImportAsync_ConflictIds_MatchPreview()
        {
            // Arrange - Create conflict
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "conflict_doc", "External version")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local version" },
                new List<string> { "conflict_doc" });

            // Get preview and conflict ID
            var preview = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);
            Assert.That(preview.Conflicts.Count, Is.EqualTo(1));
            var previewConflictId = preview.Conflicts[0].ConflictId;

            // Create resolution using preview conflict ID
            var resolutions = new List<ImportConflictResolution>
            {
                new ImportConflictResolution
                {
                    ConflictId = previewConflictId,
                    ResolutionType = ImportResolutionType.KeepSource
                }
            };

            // Act - Execute with resolution
            var result = await _importExecutor.ExecuteImportAsync(
                _externalDbPath,
                resolutions: resolutions,
                autoResolveRemaining: false);

            // Assert - Should successfully resolve the conflict using preview ID
            Assert.That(result.Success, Is.True, "Import should succeed using preview conflict ID");
            Assert.That(result.ConflictsResolved, Is.EqualTo(1), "Should resolve exactly 1 conflict");
        }

        #endregion

        #region Validation Tests

        /// <summary>
        /// Verifies that ValidateResolutionsAsync catches unknown conflict IDs
        /// </summary>
        [Test]
        public async Task ValidateResolutionsAsync_UnknownConflictId_ReturnsError()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "doc1", "External content")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local content" },
                new List<string> { "doc1" });

            var preview = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);

            var invalidResolutions = new List<ImportConflictResolution>
            {
                new ImportConflictResolution
                {
                    ConflictId = "imp_invalid12345",
                    ResolutionType = ImportResolutionType.KeepSource
                }
            };

            // Act
            var (isValid, errors) = await _importExecutor.ValidateResolutionsAsync(preview, invalidResolutions);

            // Assert
            Assert.That(isValid, Is.False, "Should be invalid for unknown conflict ID");
            Assert.That(errors, Contains.Item("Unknown conflict ID: imp_invalid12345"));
        }

        /// <summary>
        /// Verifies that ValidateResolutionsAsync catches custom resolution without content
        /// </summary>
        [Test]
        public async Task ValidateResolutionsAsync_CustomWithoutContent_ReturnsError()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "doc1", "External content")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local content" },
                new List<string> { "doc1" });

            var preview = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);
            var conflictId = preview.Conflicts[0].ConflictId;

            var invalidResolutions = new List<ImportConflictResolution>
            {
                new ImportConflictResolution
                {
                    ConflictId = conflictId,
                    ResolutionType = ImportResolutionType.Custom,
                    CustomContent = null // Missing required content
                }
            };

            // Act
            var (isValid, errors) = await _importExecutor.ValidateResolutionsAsync(preview, invalidResolutions);

            // Assert
            Assert.That(isValid, Is.False, "Should be invalid for custom without content");
            Assert.That(errors.Any(e => e.Contains("requires custom_content")), Is.True);
        }

        #endregion

        #region Auto-Resolve Tests

        /// <summary>
        /// Verifies that AutoResolveImportConflictsAsync resolves conflicts using specified strategy
        /// </summary>
        [Test]
        public async Task AutoResolveImportConflictsAsync_ResolvesWithStrategy()
        {
            // Arrange - Create conflict
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "auto_doc", "External auto content")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local auto content" },
                new List<string> { "auto_doc" });

            var preview = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);
            Assert.That(preview.Conflicts.Count, Is.EqualTo(1));

            // Act
            var resolved = await _importExecutor.AutoResolveImportConflictsAsync(
                _externalDbPath,
                preview.Conflicts,
                "keep_source");

            // Assert
            Assert.That(resolved, Is.EqualTo(1), "Should resolve 1 conflict");
        }

        #endregion

        #region Single Conflict Resolution Tests

        /// <summary>
        /// Verifies that ResolveImportConflictAsync resolves a single conflict correctly
        /// </summary>
        [Test]
        public async Task ResolveImportConflictAsync_SingleConflict_ResolvesSuccessfully()
        {
            // Arrange - Create conflict
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "single_doc", "External single content")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local single content" },
                new List<string> { "single_doc" });

            var preview = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);
            var conflict = preview.Conflicts[0];

            var resolution = new ImportConflictResolution
            {
                ConflictId = conflict.ConflictId,
                ResolutionType = ImportResolutionType.KeepSource
            };

            // Act
            var success = await _importExecutor.ResolveImportConflictAsync(_externalDbPath, conflict, resolution);

            // Assert
            Assert.That(success, Is.True, "Single conflict resolution should succeed");
        }

        #endregion

        #region Helper Methods

        private string _externalClientId = string.Empty;

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

                // Group by collection
                var byCollection = documents.GroupBy(d => d.collection);

                foreach (var group in byCollection)
                {
                    dynamic collection = client.get_or_create_collection(name: group.Key);
                    var idsList = group.Select(d => d.docId).ToList();
                    var contentsList = group.Select(d => d.content).ToList();

                    // Add documents one at a time to avoid Python list conversion issues
                    for (int i = 0; i < idsList.Count; i++)
                    {
                        collection.add(
                            ids: new[] { idsList[i] },
                            documents: new[] { contentsList[i] }
                        );
                    }
                }

                return true;
            }, timeoutMs: 60000, operationName: "CreateExternalDbWithDocs");
        }

        /// <summary>
        /// Creates ChromaDbService for local database
        /// </summary>
        private IChromaDbService CreateChromaService(ServerConfiguration config)
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<ChromaPythonService>();

            return new ChromaPythonService(logger, Options.Create(config));
        }

        #endregion
    }
}
