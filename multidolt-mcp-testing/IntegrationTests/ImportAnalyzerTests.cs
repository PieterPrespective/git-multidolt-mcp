using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Embranch.Models;
using Embranch.Services;
using Python.Runtime;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for the ImportAnalyzer service.
    /// Tests conflict detection, collection mapping, and import preview functionality
    /// using real ChromaDB databases (external and local).
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class ImportAnalyzerTests
    {
        private ILogger<ImportAnalyzerTests> _logger = null!;
        private ILogger<ImportAnalyzer> _analyzerLogger = null!;
        private ILogger<ExternalChromaDbReader> _readerLogger = null!;
        private IExternalChromaDbReader _externalReader = null!;
        private IChromaDbService _chromaService = null!;
        private IImportAnalyzer _importAnalyzer = null!;

        private string _tempDataPath = null!;
        private string _externalDbPath = null!;
        private string _localDbPath = null!;
        private List<string> _testCollections = new();

        private const string ExternalCollection1 = "import_test_external_1";
        private const string ExternalCollection2 = "import_test_external_2";
        private const string ExternalCollection3 = "import_test_project_alpha";
        private const string ExternalCollection4 = "import_test_project_beta";
        private const string LocalCollection = "import_test_local";

        [SetUp]
        public async Task Setup()
        {
            // PythonContext is managed by GlobalTestSetup - just verify it's available
            if (!PythonContext.IsInitialized)
            {
                throw new InvalidOperationException("PythonContext should be initialized by GlobalTestSetup");
            }

            // Create temp paths
            _tempDataPath = Path.Combine(Path.GetTempPath(), "ImportAnalyzerTests", Guid.NewGuid().ToString());
            _externalDbPath = Path.Combine(_tempDataPath, "external_chroma");
            _localDbPath = Path.Combine(_tempDataPath, "local_chroma");
            Directory.CreateDirectory(_externalDbPath);
            Directory.CreateDirectory(_localDbPath);

            // Create loggers
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _logger = loggerFactory.CreateLogger<ImportAnalyzerTests>();
            _analyzerLogger = loggerFactory.CreateLogger<ImportAnalyzer>();
            _readerLogger = loggerFactory.CreateLogger<ExternalChromaDbReader>();

            // Create external reader
            _externalReader = new ExternalChromaDbReader(_readerLogger);

            // Create local ChromaDB service
            var serverConfig = new ServerConfiguration
            {
                ChromaMode = "persistent",
                ChromaDataPath = _localDbPath,
                DataPath = _tempDataPath
            };
            _chromaService = CreateChromaService(serverConfig);

            // Create import analyzer
            _importAnalyzer = new ImportAnalyzer(_externalReader, _chromaService, _analyzerLogger);

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

        #region Validation Tests

        /// <summary>
        /// Verifies that AnalyzeImportAsync fails gracefully for invalid paths
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_InvalidPath_ReturnsFailureResult()
        {
            // Arrange
            var invalidPath = Path.Combine(_tempDataPath, "nonexistent_db");

            // Act
            var result = await _importAnalyzer.AnalyzeImportAsync(invalidPath);

            // Assert
            Assert.That(result.Success, Is.False, "Should fail for invalid path");
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty, "Should have error message");
            Assert.That(result.SourcePath, Is.EqualTo(invalidPath));
        }

        /// <summary>
        /// Verifies that AnalyzeImportAsync succeeds for valid empty external database
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_ValidEmptyDb_ReturnsSuccessWithNoChanges()
        {
            // Arrange - Create empty external database
            await CreateExternalDatabase();

            // Act
            var result = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);

            // Assert
            Assert.That(result.Success, Is.True, "Should succeed for valid empty database");
            Assert.That(result.TotalConflicts, Is.EqualTo(0));
            Assert.That(result.Preview?.DocumentsToAdd, Is.EqualTo(0));
            Assert.That(result.CanAutoImport, Is.True);
        }

        #endregion

        #region Conflict Detection Tests

        /// <summary>
        /// Verifies that no conflicts are detected when importing to new collection
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_NewCollection_NoConflicts()
        {
            // Arrange - Create external database with documents
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (ExternalCollection1, "doc1", "Content 1"),
                (ExternalCollection1, "doc2", "Content 2"),
                (ExternalCollection1, "doc3", "Content 3")
            });

            // Act
            var result = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.TotalConflicts, Is.EqualTo(0), "No conflicts for new collection");
            Assert.That(result.Preview?.DocumentsToAdd, Is.EqualTo(3), "Should add 3 documents");
            Assert.That(result.Preview?.CollectionsToCreate, Is.EqualTo(1), "Should create 1 collection");
            Assert.That(result.CanAutoImport, Is.True);
        }

        /// <summary>
        /// Verifies that content modification conflicts are detected
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_ContentModification_DetectsConflict()
        {
            // Arrange - Create external and local databases with conflicting documents
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "doc1", "External content version")
            });

            // Create local collection with different content
            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local content version" },
                new List<string> { "doc1" });

            // Act
            var result = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.TotalConflicts, Is.EqualTo(1), "Should detect 1 conflict");
            Assert.That(result.Conflicts.Count, Is.EqualTo(1));
            Assert.That(result.Conflicts[0].Type, Is.EqualTo(ImportConflictType.ContentModification));
            Assert.That(result.Conflicts[0].DocumentId, Is.EqualTo("doc1"));
            Assert.That(result.CanAutoImport, Is.False, "Content conflicts are not auto-resolvable");
        }

        /// <summary>
        /// Verifies that identical documents are skipped without conflict
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_IdenticalDocuments_NoConflict()
        {
            // Arrange - Create identical documents in both databases
            var content = "Identical content in both databases";
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "doc1", content)
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { content },
                new List<string> { "doc1" });

            // Act
            var result = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.TotalConflicts, Is.EqualTo(0), "Identical docs should not create conflict");
            Assert.That(result.Preview?.DocumentsToSkip, Is.EqualTo(1), "Should skip 1 identical document");
        }

        /// <summary>
        /// Verifies that conflict IDs are deterministic (PP13-73 prevention)
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_ConflictIds_AreDeterministic()
        {
            // Arrange - Create conflicting documents
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "doc1", "External version")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local version" },
                new List<string> { "doc1" });

            // Act - Run analysis multiple times
            var result1 = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);
            var result2 = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);
            var result3 = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath);

            // Assert - Conflict IDs should be identical
            Assert.That(result1.Success && result2.Success && result3.Success, Is.True);
            Assert.That(result1.Conflicts.Count, Is.EqualTo(1));
            Assert.That(result2.Conflicts.Count, Is.EqualTo(1));
            Assert.That(result3.Conflicts.Count, Is.EqualTo(1));

            var id1 = result1.Conflicts[0].ConflictId;
            var id2 = result2.Conflicts[0].ConflictId;
            var id3 = result3.Conflicts[0].ConflictId;

            Assert.That(id1, Is.EqualTo(id2), "Conflict IDs must be deterministic");
            Assert.That(id1, Is.EqualTo(id3), "Conflict IDs must be deterministic across multiple calls");
            Assert.That(id1, Does.StartWith("imp_"), "Conflict ID should have import prefix");
        }

        #endregion

        #region Collection Mapping Tests

        /// <summary>
        /// Verifies that collection filter correctly maps source to target
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_WithFilter_MapsCollections()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (ExternalCollection1, "doc1", "Content 1")
            });

            var filter = new ImportFilter
            {
                Collections = new List<CollectionImportSpec>
                {
                    new CollectionImportSpec
                    {
                        Name = ExternalCollection1,
                        ImportInto = "mapped_target_collection"
                    }
                }
            };

            // Act
            var result = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath, filter);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.Preview?.AffectedCollections, Contains.Item("mapped_target_collection"));
            Assert.That(result.Preview?.CollectionsToCreate, Is.EqualTo(1));
        }

        /// <summary>
        /// Verifies that wildcard collection patterns are expanded correctly
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_WildcardCollection_ExpandsPattern()
        {
            // Arrange - Create multiple project collections
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (ExternalCollection3, "doc1", "Alpha content"),  // import_test_project_alpha
                (ExternalCollection4, "doc2", "Beta content")    // import_test_project_beta
            });

            var filter = new ImportFilter
            {
                Collections = new List<CollectionImportSpec>
                {
                    new CollectionImportSpec
                    {
                        Name = "import_test_project_*",
                        ImportInto = "consolidated_projects"
                    }
                }
            };

            // Act
            var result = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath, filter);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.Preview?.DocumentsToAdd, Is.EqualTo(2), "Should import docs from both matching collections");
            Assert.That(result.Preview?.CollectionsToCreate, Is.EqualTo(1), "Should create 1 target collection");
            Assert.That(result.Preview?.AffectedCollections, Contains.Item("consolidated_projects"));
        }

        /// <summary>
        /// Verifies that multiple sources can consolidate into one target
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_MultiSourceConsolidation_PreviewsCorrectly()
        {
            // Arrange - Create multiple source collections
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (ExternalCollection1, "doc1", "Content from collection 1"),
                (ExternalCollection2, "doc2", "Content from collection 2")
            });

            var filter = new ImportFilter
            {
                Collections = new List<CollectionImportSpec>
                {
                    new CollectionImportSpec { Name = ExternalCollection1, ImportInto = "consolidated" },
                    new CollectionImportSpec { Name = ExternalCollection2, ImportInto = "consolidated" }
                }
            };

            // Act
            var result = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath, filter);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.Preview?.DocumentsToAdd, Is.EqualTo(2), "Should add docs from both sources");
            Assert.That(result.Preview?.CollectionsToCreate, Is.EqualTo(1), "Should create only 1 target collection");
            Assert.That(result.Preview?.AffectedCollections.Count, Is.EqualTo(1));
            Assert.That(result.Preview?.AffectedCollections, Contains.Item("consolidated"));
        }

        #endregion

        #region Auto-Resolution Tests

        /// <summary>
        /// Verifies that CanAutoResolveImportConflictAsync returns true for metadata conflicts
        /// </summary>
        [Test]
        public async Task CanAutoResolveImportConflictAsync_MetadataConflict_ReturnsTrue()
        {
            // Arrange
            var conflict = new ImportConflictInfo
            {
                ConflictId = "imp_test123456",
                Type = ImportConflictType.MetadataConflict,
                DocumentId = "doc1",
                SourceCollection = "external",
                TargetCollection = "local"
            };

            // Act
            var result = await _importAnalyzer.CanAutoResolveImportConflictAsync(conflict);

            // Assert
            Assert.That(result, Is.True, "Metadata conflicts should be auto-resolvable");
        }

        /// <summary>
        /// Verifies that CanAutoResolveImportConflictAsync returns false for content modification conflicts
        /// </summary>
        [Test]
        public async Task CanAutoResolveImportConflictAsync_ContentModification_ReturnsFalse()
        {
            // Arrange
            var conflict = new ImportConflictInfo
            {
                ConflictId = "imp_test123456",
                Type = ImportConflictType.ContentModification,
                DocumentId = "doc1",
                SourceCollection = "external",
                TargetCollection = "local",
                SourceContentHash = "hash1",
                TargetContentHash = "hash2" // Different hashes
            };

            // Act
            var result = await _importAnalyzer.CanAutoResolveImportConflictAsync(conflict);

            // Assert
            Assert.That(result, Is.False, "Content modification conflicts should not be auto-resolvable");
        }

        /// <summary>
        /// Verifies that content modifications with identical hashes are auto-resolvable
        /// </summary>
        [Test]
        public async Task CanAutoResolveImportConflictAsync_IdenticalContent_ReturnsTrue()
        {
            // Arrange
            var conflict = new ImportConflictInfo
            {
                ConflictId = "imp_test123456",
                Type = ImportConflictType.ContentModification,
                DocumentId = "doc1",
                SourceCollection = "external",
                TargetCollection = "local",
                SourceContentHash = "identical_hash",
                TargetContentHash = "identical_hash" // Same hash
            };

            // Act
            var result = await _importAnalyzer.CanAutoResolveImportConflictAsync(conflict);

            // Assert
            Assert.That(result, Is.True, "Identical content should be auto-resolvable");
        }

        #endregion

        #region Quick Preview Tests

        /// <summary>
        /// Verifies that GetQuickPreviewAsync returns basic statistics without full analysis
        /// </summary>
        [Test]
        public async Task GetQuickPreviewAsync_ReturnsBasicStats()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (ExternalCollection1, "doc1", "Content 1"),
                (ExternalCollection1, "doc2", "Content 2")
            });

            // Act
            var preview = await _importAnalyzer.GetQuickPreviewAsync(_externalDbPath);

            // Assert
            Assert.That(preview.AffectedCollections, Contains.Item(ExternalCollection1));
            Assert.That(preview.DocumentsToAdd, Is.GreaterThanOrEqualTo(0));
            Assert.That(preview.CollectionsToCreate, Is.GreaterThanOrEqualTo(0));
        }

        #endregion

        #region Content Preview Tests

        /// <summary>
        /// Verifies that content preview is included when requested
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_WithContentPreview_IncludesContent()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (LocalCollection, "doc1", "External content for preview")
            });

            await _chromaService.CreateCollectionAsync(LocalCollection);
            _testCollections.Add(LocalCollection);
            await _chromaService.AddDocumentsAsync(
                LocalCollection,
                new List<string> { "Local content for preview" },
                new List<string> { "doc1" });

            // Act
            var result = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath, includeContentPreview: true);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.Conflicts.Count, Is.EqualTo(1));
            Assert.That(result.Conflicts[0].SourceContent, Is.Not.Null.And.Not.Empty, "Should include source content");
            Assert.That(result.Conflicts[0].TargetContent, Is.Not.Null.And.Not.Empty, "Should include target content");
        }

        /// <summary>
        /// Verifies that content preview is omitted when not requested
        /// </summary>
        [Test]
        public async Task AnalyzeImportAsync_WithoutContentPreview_ExcludesContent()
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

            // Act
            var result = await _importAnalyzer.AnalyzeImportAsync(_externalDbPath, includeContentPreview: false);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.Conflicts.Count, Is.EqualTo(1));
            Assert.That(result.Conflicts[0].SourceContent, Is.Null, "Should not include source content");
            Assert.That(result.Conflicts[0].TargetContent, Is.Null, "Should not include target content");
            // But hashes should always be present
            Assert.That(result.Conflicts[0].SourceContentHash, Is.Not.Null.And.Not.Empty);
            Assert.That(result.Conflicts[0].TargetContentHash, Is.Not.Null.And.Not.Empty);
        }

        #endregion

        #region Helper Methods

        private string _externalClientId = string.Empty;

        /// <summary>
        /// Creates an empty external ChromaDB database
        /// </summary>
        private async Task CreateExternalDatabase()
        {
            await PythonContext.ExecuteAsync(() =>
            {
                _externalClientId = $"TestExternalDb_{Guid.NewGuid():N}";
                dynamic client = ChromaClientPool.GetOrCreateClient(_externalClientId, $"persistent:{_externalDbPath}");
                return true;
            }, timeoutMs: 30000, operationName: "CreateExternalDb");
        }

        /// <summary>
        /// Creates external database with specified documents.
        /// Uses proper Python list conversion to avoid deadlocks.
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

                    // Convert to proper Python lists to avoid deadlocks
                    PyObject pyIds = ConvertToPyList(group.Select(d => d.docId).ToList());
                    PyObject pyDocs = ConvertToPyList(group.Select(d => d.content).ToList());

                    collection.add(ids: pyIds, documents: pyDocs);
                }

                return true;
            }, timeoutMs: 60000, operationName: "CreateExternalDbWithDocs");
        }

        /// <summary>
        /// Converts a C# string list to a Python list.
        /// Must be called within PythonContext.ExecuteAsync.
        /// </summary>
        private static PyObject ConvertToPyList(List<string> items)
        {
            dynamic pyList = PythonEngine.Eval("[]");
            foreach (var item in items)
            {
                pyList.append(item);
            }
            return pyList;
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
