using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Embranch.Services;
using Python.Runtime;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for the ExternalChromaDbReader service.
    /// Tests database validation, collection listing, document retrieval,
    /// and wildcard pattern matching using real ChromaDB databases.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class ExternalChromaDbReaderTests
    {
        private ILogger<ExternalChromaDbReaderTests> _logger = null!;
        private ILogger<ExternalChromaDbReader> _readerLogger = null!;
        private IExternalChromaDbReader _externalReader = null!;

        private string _tempDataPath = null!;
        private string _externalDbPath = null!;

        private const string TestCollection1 = "reader_test_collection_1";
        private const string TestCollection2 = "reader_test_collection_2";
        private const string TestProjectAlpha = "reader_test_project_alpha";
        private const string TestProjectBeta = "reader_test_project_beta";
        private const string TestProjectGamma = "reader_test_project_gamma";

        [SetUp]
        public async Task Setup()
        {
            // PythonContext is managed by GlobalTestSetup - just verify it's available
            if (!PythonContext.IsInitialized)
            {
                throw new InvalidOperationException("PythonContext should be initialized by GlobalTestSetup");
            }

            // Create temp paths
            _tempDataPath = Path.Combine(Path.GetTempPath(), "ExternalChromaDbReaderTests", Guid.NewGuid().ToString());
            _externalDbPath = Path.Combine(_tempDataPath, "external_chroma");
            Directory.CreateDirectory(_externalDbPath);

            // Create loggers
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _logger = loggerFactory.CreateLogger<ExternalChromaDbReaderTests>();
            _readerLogger = loggerFactory.CreateLogger<ExternalChromaDbReader>();

            // Create external reader
            _externalReader = new ExternalChromaDbReader(_readerLogger);

            _logger.LogInformation("Test setup complete - External DB path: {Path}", _externalDbPath);
        }

        [TearDown]
        public async Task Cleanup()
        {
            try
            {
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

        #region Database Validation Tests

        /// <summary>
        /// Verifies that ValidateExternalDbAsync returns failure for non-existent path
        /// </summary>
        [Test]
        public async Task ValidateExternalDbAsync_NonExistentPath_ReturnsFailure()
        {
            // Arrange
            var invalidPath = Path.Combine(_tempDataPath, "nonexistent_db");

            // Act
            var result = await _externalReader.ValidateExternalDbAsync(invalidPath);

            // Assert
            Assert.That(result.IsValid, Is.False, "Should fail for non-existent path");
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty, "Should have error message");
            Assert.That(result.ErrorMessage, Does.Contain("does not exist"));
            Assert.That(result.DbPath, Is.EqualTo(invalidPath));
        }

        /// <summary>
        /// Verifies that ValidateExternalDbAsync returns failure for path without chroma.sqlite3
        /// </summary>
        [Test]
        public async Task ValidateExternalDbAsync_MissingChromaSqlite_ReturnsFailure()
        {
            // Arrange - Create empty directory
            var emptyDbPath = Path.Combine(_tempDataPath, "empty_db");
            Directory.CreateDirectory(emptyDbPath);

            // Act
            var result = await _externalReader.ValidateExternalDbAsync(emptyDbPath);

            // Assert
            Assert.That(result.IsValid, Is.False, "Should fail for missing chroma.sqlite3");
            Assert.That(result.ErrorMessage, Does.Contain("chroma.sqlite3"));
        }

        /// <summary>
        /// Verifies that ValidateExternalDbAsync returns success with statistics for valid database
        /// </summary>
        [Test]
        public async Task ValidateExternalDbAsync_ValidDatabase_ReturnsSuccessWithStats()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "doc1", "Content 1"),
                (TestCollection1, "doc2", "Content 2"),
                (TestCollection2, "doc3", "Content 3")
            });

            // Act
            var result = await _externalReader.ValidateExternalDbAsync(_externalDbPath);

            // Assert
            Assert.That(result.IsValid, Is.True, "Should succeed for valid database");
            Assert.That(result.ErrorMessage, Is.Null.Or.Empty);
            Assert.That(result.CollectionCount, Is.EqualTo(2), "Should have 2 collections");
            Assert.That(result.TotalDocuments, Is.EqualTo(3), "Should have 3 total documents");
            Assert.That(result.DbPath, Is.EqualTo(_externalDbPath));
        }

        /// <summary>
        /// Verifies that ValidateExternalDbAsync returns zero counts for empty database
        /// </summary>
        [Test]
        public async Task ValidateExternalDbAsync_EmptyDatabase_ReturnsSuccessWithZeroCounts()
        {
            // Arrange - Create empty database
            await CreateEmptyExternalDatabase();

            // Act
            var result = await _externalReader.ValidateExternalDbAsync(_externalDbPath);

            // Assert
            Assert.That(result.IsValid, Is.True, "Should succeed for empty database");
            Assert.That(result.CollectionCount, Is.EqualTo(0), "Should have 0 collections");
            Assert.That(result.TotalDocuments, Is.EqualTo(0), "Should have 0 documents");
        }

        #endregion

        #region Collection Listing Tests

        /// <summary>
        /// Verifies that ListExternalCollectionsAsync returns all collections with details
        /// </summary>
        [Test]
        public async Task ListExternalCollectionsAsync_ReturnsCollectionInfo()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "doc1", "Content 1"),
                (TestCollection1, "doc2", "Content 2"),
                (TestCollection2, "doc3", "Content 3")
            });

            // Act
            var collections = await _externalReader.ListExternalCollectionsAsync(_externalDbPath);

            // Assert
            Assert.That(collections.Count, Is.EqualTo(2), "Should have 2 collections");

            var col1 = collections.FirstOrDefault(c => c.Name == TestCollection1);
            Assert.That(col1, Is.Not.Null);
            Assert.That(col1!.DocumentCount, Is.EqualTo(2), "Collection 1 should have 2 documents");

            var col2 = collections.FirstOrDefault(c => c.Name == TestCollection2);
            Assert.That(col2, Is.Not.Null);
            Assert.That(col2!.DocumentCount, Is.EqualTo(1), "Collection 2 should have 1 document");
        }

        /// <summary>
        /// Verifies that ListExternalCollectionsAsync returns empty list for empty database
        /// </summary>
        [Test]
        public async Task ListExternalCollectionsAsync_EmptyDatabase_ReturnsEmptyList()
        {
            // Arrange
            await CreateEmptyExternalDatabase();

            // Act
            var collections = await _externalReader.ListExternalCollectionsAsync(_externalDbPath);

            // Assert
            Assert.That(collections, Is.Not.Null);
            Assert.That(collections.Count, Is.EqualTo(0));
        }

        #endregion

        #region Wildcard Collection Matching Tests

        /// <summary>
        /// Verifies that ListMatchingCollectionsAsync matches prefix wildcards
        /// </summary>
        [Test]
        public async Task ListMatchingCollectionsAsync_PrefixWildcard_MatchesCorrectly()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestProjectAlpha, "doc1", "Alpha content"),
                (TestProjectBeta, "doc2", "Beta content"),
                (TestProjectGamma, "doc3", "Gamma content"),
                (TestCollection1, "doc4", "Other content")
            });

            // Act
            var matches = await _externalReader.ListMatchingCollectionsAsync(_externalDbPath, "reader_test_project_*");

            // Assert
            Assert.That(matches.Count, Is.EqualTo(3), "Should match 3 project collections");
            Assert.That(matches, Contains.Item(TestProjectAlpha));
            Assert.That(matches, Contains.Item(TestProjectBeta));
            Assert.That(matches, Contains.Item(TestProjectGamma));
            Assert.That(matches, Does.Not.Contain(TestCollection1));
        }

        /// <summary>
        /// Verifies that ListMatchingCollectionsAsync matches suffix wildcards
        /// </summary>
        [Test]
        public async Task ListMatchingCollectionsAsync_SuffixWildcard_MatchesCorrectly()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestProjectAlpha, "doc1", "Alpha content"),
                (TestCollection1, "doc2", "Collection 1 content")
            });

            // Act
            var matches = await _externalReader.ListMatchingCollectionsAsync(_externalDbPath, "*_alpha");

            // Assert
            Assert.That(matches.Count, Is.EqualTo(1), "Should match 1 collection ending with _alpha");
            Assert.That(matches, Contains.Item(TestProjectAlpha));
        }

        /// <summary>
        /// Verifies that ListMatchingCollectionsAsync returns exact match for non-wildcard pattern
        /// </summary>
        [Test]
        public async Task ListMatchingCollectionsAsync_ExactMatch_ReturnsCorrectly()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "doc1", "Content 1"),
                (TestCollection2, "doc2", "Content 2")
            });

            // Act
            var matches = await _externalReader.ListMatchingCollectionsAsync(_externalDbPath, TestCollection1);

            // Assert
            Assert.That(matches.Count, Is.EqualTo(1), "Should return exactly 1 match");
            Assert.That(matches[0], Is.EqualTo(TestCollection1));
        }

        /// <summary>
        /// Verifies that ListMatchingCollectionsAsync returns empty for no matches
        /// </summary>
        [Test]
        public async Task ListMatchingCollectionsAsync_NoMatches_ReturnsEmptyList()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "doc1", "Content 1")
            });

            // Act
            var matches = await _externalReader.ListMatchingCollectionsAsync(_externalDbPath, "nonexistent_*");

            // Assert
            Assert.That(matches, Is.Not.Null);
            Assert.That(matches.Count, Is.EqualTo(0));
        }

        #endregion

        #region Document Retrieval Tests

        /// <summary>
        /// Verifies that GetExternalDocumentsAsync retrieves all documents from collection
        /// </summary>
        [Test]
        public async Task GetExternalDocumentsAsync_RetrievesAllDocuments()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "doc1", "Content 1"),
                (TestCollection1, "doc2", "Content 2"),
                (TestCollection1, "doc3", "Content 3")
            });

            // Act
            var documents = await _externalReader.GetExternalDocumentsAsync(_externalDbPath, TestCollection1);

            // Assert
            Assert.That(documents.Count, Is.EqualTo(3), "Should retrieve all 3 documents");

            var doc1 = documents.FirstOrDefault(d => d.DocId == "doc1");
            Assert.That(doc1, Is.Not.Null);
            Assert.That(doc1!.Content, Is.EqualTo("Content 1"));
            Assert.That(doc1.CollectionName, Is.EqualTo(TestCollection1));
            Assert.That(doc1.ContentHash, Is.Not.Null.And.Not.Empty, "Should have content hash");
        }

        /// <summary>
        /// Verifies that GetExternalDocumentsAsync returns empty list for non-existent collection
        /// </summary>
        [Test]
        public async Task GetExternalDocumentsAsync_NonExistentCollection_ReturnsEmptyList()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "doc1", "Content 1")
            });

            // Act
            var documents = await _externalReader.GetExternalDocumentsAsync(_externalDbPath, "nonexistent_collection");

            // Assert
            Assert.That(documents, Is.Not.Null);
            Assert.That(documents.Count, Is.EqualTo(0));
        }

        /// <summary>
        /// Verifies that GetExternalDocumentsAsync filters by document ID patterns
        /// </summary>
        [Test]
        public async Task GetExternalDocumentsAsync_WithDocumentPatterns_FiltersCorrectly()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "doc_summary", "Summary content"),
                (TestCollection1, "doc_report", "Report content"),
                (TestCollection1, "other_item", "Other content")
            });

            // Act
            var documents = await _externalReader.GetExternalDocumentsAsync(
                _externalDbPath,
                TestCollection1,
                documentIdPatterns: new List<string> { "doc_*" });

            // Assert
            Assert.That(documents.Count, Is.EqualTo(2), "Should retrieve 2 documents matching doc_*");
            Assert.That(documents.All(d => d.DocId.StartsWith("doc_")), Is.True);
        }

        /// <summary>
        /// Verifies that GetExternalDocumentsAsync supports multiple document ID patterns
        /// </summary>
        [Test]
        public async Task GetExternalDocumentsAsync_MultiplePatterns_MatchesAny()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "doc_summary", "Summary content"),
                (TestCollection1, "report_final", "Report content"),
                (TestCollection1, "other_item", "Other content")
            });

            // Act
            var documents = await _externalReader.GetExternalDocumentsAsync(
                _externalDbPath,
                TestCollection1,
                documentIdPatterns: new List<string> { "doc_*", "*_final" });

            // Assert
            Assert.That(documents.Count, Is.EqualTo(2), "Should retrieve documents matching either pattern");
            var docIds = documents.Select(d => d.DocId).ToList();
            Assert.That(docIds, Contains.Item("doc_summary"));
            Assert.That(docIds, Contains.Item("report_final"));
        }

        /// <summary>
        /// Verifies that content hash is computed correctly for documents
        /// </summary>
        [Test]
        public async Task GetExternalDocumentsAsync_ContentHash_IsDeterministic()
        {
            // Arrange
            var content = "Test content for hashing";
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "hash_test_doc", content)
            });

            // Act
            var docs1 = await _externalReader.GetExternalDocumentsAsync(_externalDbPath, TestCollection1);
            var docs2 = await _externalReader.GetExternalDocumentsAsync(_externalDbPath, TestCollection1);

            // Assert
            Assert.That(docs1.Count, Is.EqualTo(1));
            Assert.That(docs2.Count, Is.EqualTo(1));
            Assert.That(docs1[0].ContentHash, Is.EqualTo(docs2[0].ContentHash),
                "Content hash should be deterministic");
            Assert.That(docs1[0].ContentHash.Length, Is.EqualTo(64),
                "SHA-256 hash should be 64 hex characters");
        }

        #endregion

        #region Collection Metadata Tests

        /// <summary>
        /// Verifies that GetExternalCollectionMetadataAsync returns null for non-existent collection
        /// </summary>
        [Test]
        public async Task GetExternalCollectionMetadataAsync_NonExistentCollection_ReturnsNull()
        {
            // Arrange
            await CreateEmptyExternalDatabase();

            // Act
            var metadata = await _externalReader.GetExternalCollectionMetadataAsync(_externalDbPath, "nonexistent");

            // Assert
            Assert.That(metadata, Is.Null);
        }

        #endregion

        #region Collection Count Tests

        /// <summary>
        /// Verifies that GetExternalCollectionCountAsync returns correct count
        /// </summary>
        [Test]
        public async Task GetExternalCollectionCountAsync_ReturnsCorrectCount()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "doc1", "Content 1"),
                (TestCollection1, "doc2", "Content 2"),
                (TestCollection1, "doc3", "Content 3")
            });

            // Act
            var count = await _externalReader.GetExternalCollectionCountAsync(_externalDbPath, TestCollection1);

            // Assert
            Assert.That(count, Is.EqualTo(3), "Should return 3 documents");
        }

        /// <summary>
        /// Verifies that GetExternalCollectionCountAsync returns 0 for non-existent collection
        /// </summary>
        [Test]
        public async Task GetExternalCollectionCountAsync_NonExistentCollection_ReturnsZero()
        {
            // Arrange
            await CreateEmptyExternalDatabase();

            // Act
            var count = await _externalReader.GetExternalCollectionCountAsync(_externalDbPath, "nonexistent");

            // Assert
            Assert.That(count, Is.EqualTo(0));
        }

        #endregion

        #region Collection Exists Tests

        /// <summary>
        /// Verifies that CollectionExistsAsync returns true for existing collection
        /// </summary>
        [Test]
        public async Task CollectionExistsAsync_ExistingCollection_ReturnsTrue()
        {
            // Arrange
            await CreateExternalDatabaseWithDocuments(new[]
            {
                (TestCollection1, "doc1", "Content 1")
            });

            // Act
            var exists = await _externalReader.CollectionExistsAsync(_externalDbPath, TestCollection1);

            // Assert
            Assert.That(exists, Is.True);
        }

        /// <summary>
        /// Verifies that CollectionExistsAsync returns false for non-existent collection
        /// </summary>
        [Test]
        public async Task CollectionExistsAsync_NonExistentCollection_ReturnsFalse()
        {
            // Arrange
            await CreateEmptyExternalDatabase();

            // Act
            var exists = await _externalReader.CollectionExistsAsync(_externalDbPath, "nonexistent");

            // Assert
            Assert.That(exists, Is.False);
        }

        #endregion

        #region Helper Methods

        private string _externalClientId = string.Empty;

        /// <summary>
        /// Creates an empty external ChromaDB database.
        /// Uses unique client ID pattern matching ImportAnalyzerTests.
        /// </summary>
        private async Task CreateEmptyExternalDatabase()
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
        /// Uses unique client ID and proper Python list conversion.
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

                    // Convert to proper Python lists like ChromaPythonService does
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

        #endregion
    }
}
