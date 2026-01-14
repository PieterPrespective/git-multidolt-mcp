using NUnit.Framework;
using DMMS.Models;
using System.Text.Json;

namespace DMMS.Testing.UnitTests
{
    /// <summary>
    /// Unit tests for ImportFilter and related model parsing.
    /// Tests filter deserialization, collection spec handling, and target extraction.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public class ImportFilterTests
    {
        #region Empty Filter Tests

        /// <summary>
        /// Verifies that an empty JSON object is parsed as ImportAll filter
        /// </summary>
        [Test]
        public void ImportFilter_ParseEmptyFilter_ReturnsImportAll()
        {
            // Arrange
            var json = "{}";

            // Act
            var filter = JsonSerializer.Deserialize<ImportFilter>(json);

            // Assert
            Assert.That(filter, Is.Not.Null);
            Assert.That(filter.IsImportAll, Is.True, "Empty filter should be ImportAll");
            Assert.That(filter.Collections, Is.Null);
        }

        /// <summary>
        /// Verifies that null collections array results in ImportAll
        /// </summary>
        [Test]
        public void ImportFilter_NullCollections_ReturnsImportAll()
        {
            // Arrange
            var filter = new ImportFilter { Collections = null };

            // Act & Assert
            Assert.That(filter.IsImportAll, Is.True, "Null collections should be ImportAll");
        }

        /// <summary>
        /// Verifies that empty collections array results in ImportAll
        /// </summary>
        [Test]
        public void ImportFilter_EmptyCollections_ReturnsImportAll()
        {
            // Arrange
            var filter = new ImportFilter { Collections = new List<CollectionImportSpec>() };

            // Act & Assert
            Assert.That(filter.IsImportAll, Is.True, "Empty collections should be ImportAll");
        }

        #endregion

        #region Collection Array Parsing Tests

        /// <summary>
        /// Verifies that collection array is parsed correctly with proper property names
        /// </summary>
        [Test]
        public void ImportFilter_ParseCollectionArray_ExtractsCorrectly()
        {
            // Arrange
            var json = """
                {
                    "collections": [
                        { "name": "remote_collection_1", "import_into": "local_collection_1" },
                        { "name": "remote_collection_2", "import_into": "local_collection_2" }
                    ]
                }
                """;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // Act
            var filter = JsonSerializer.Deserialize<ImportFilter>(json, options);

            // Assert
            Assert.That(filter, Is.Not.Null);
            Assert.That(filter.IsImportAll, Is.False, "Should not be ImportAll with collections specified");
            Assert.That(filter.Collections, Is.Not.Null);
            Assert.That(filter.Collections.Count, Is.EqualTo(2));
            Assert.That(filter.Collections[0].Name, Is.EqualTo("remote_collection_1"));
            Assert.That(filter.Collections[0].ImportInto, Is.EqualTo("local_collection_1"));
            Assert.That(filter.Collections[1].Name, Is.EqualTo("remote_collection_2"));
            Assert.That(filter.Collections[1].ImportInto, Is.EqualTo("local_collection_2"));
        }

        /// <summary>
        /// Verifies that multiple sources mapping to the same target are parsed correctly
        /// </summary>
        [Test]
        public void ImportFilter_MultipleSourcesSameTarget_ParsesCorrectly()
        {
            // Arrange
            var json = """
                {
                    "collections": [
                        { "name": "archive_2024_*", "import_into": "consolidated_archive" },
                        { "name": "archive_2025_*", "import_into": "consolidated_archive" }
                    ]
                }
                """;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // Act
            var filter = JsonSerializer.Deserialize<ImportFilter>(json, options);

            // Assert
            Assert.That(filter, Is.Not.Null);
            Assert.That(filter.Collections, Is.Not.Null);
            Assert.That(filter.Collections.Count, Is.EqualTo(2));
            Assert.That(filter.Collections[0].ImportInto, Is.EqualTo("consolidated_archive"));
            Assert.That(filter.Collections[1].ImportInto, Is.EqualTo("consolidated_archive"));
        }

        /// <summary>
        /// Verifies that GetTargetCollections returns unique set when multiple sources map to same target
        /// </summary>
        [Test]
        public void ImportFilter_GetTargetCollections_ReturnsUniqueSet()
        {
            // Arrange
            var filter = new ImportFilter
            {
                Collections = new List<CollectionImportSpec>
                {
                    new() { Name = "archive_2024_*", ImportInto = "consolidated_archive" },
                    new() { Name = "archive_2025_*", ImportInto = "consolidated_archive" },
                    new() { Name = "current_docs", ImportInto = "active_collection" }
                }
            };

            // Act
            var targets = filter.GetTargetCollections();

            // Assert
            Assert.That(targets, Is.Not.Null);
            Assert.That(targets.Count, Is.EqualTo(2), "Should have 2 unique target collections");
            Assert.That(targets, Contains.Item("consolidated_archive"));
            Assert.That(targets, Contains.Item("active_collection"));
        }

        #endregion

        #region Document Pattern Tests

        /// <summary>
        /// Verifies that document patterns with wildcards are parsed correctly
        /// </summary>
        [Test]
        public void ImportFilter_ParseDocumentPatterns_SupportsWildcards()
        {
            // Arrange
            var json = """
                {
                    "collections": [
                        {
                            "name": "remote_collection",
                            "import_into": "local_collection",
                            "documents": ["*_summary", "doc_*", "specific_doc_id"]
                        }
                    ]
                }
                """;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // Act
            var filter = JsonSerializer.Deserialize<ImportFilter>(json, options);

            // Assert
            Assert.That(filter, Is.Not.Null);
            Assert.That(filter.Collections, Is.Not.Null);
            Assert.That(filter.Collections.Count, Is.EqualTo(1));

            var spec = filter.Collections[0];
            Assert.That(spec.Documents, Is.Not.Null);
            Assert.That(spec.Documents.Count, Is.EqualTo(3));
            Assert.That(spec.Documents, Contains.Item("*_summary"));
            Assert.That(spec.Documents, Contains.Item("doc_*"));
            Assert.That(spec.Documents, Contains.Item("specific_doc_id"));
            Assert.That(spec.HasDocumentFilter, Is.True);
        }

        /// <summary>
        /// Verifies that null documents means import all documents
        /// </summary>
        [Test]
        public void ImportFilter_NullDocuments_MeansImportAll()
        {
            // Arrange
            var spec = new CollectionImportSpec
            {
                Name = "source",
                ImportInto = "target",
                Documents = null
            };

            // Act & Assert
            Assert.That(spec.HasDocumentFilter, Is.False, "Null documents should mean no filter");
        }

        /// <summary>
        /// Verifies that empty documents array means import all documents
        /// </summary>
        [Test]
        public void ImportFilter_EmptyDocuments_MeansImportAll()
        {
            // Arrange
            var spec = new CollectionImportSpec
            {
                Name = "source",
                ImportInto = "target",
                Documents = new List<string>()
            };

            // Act & Assert
            Assert.That(spec.HasDocumentFilter, Is.False, "Empty documents should mean no filter");
        }

        #endregion

        #region Wildcard Detection Tests

        /// <summary>
        /// Verifies that collection names with wildcards are correctly detected
        /// </summary>
        [Test]
        public void CollectionImportSpec_HasCollectionWildcard_DetectsWildcards()
        {
            // Test cases with wildcards
            var wildcardCases = new[]
            {
                new CollectionImportSpec { Name = "project_*", ImportInto = "all" },
                new CollectionImportSpec { Name = "*_docs", ImportInto = "all" },
                new CollectionImportSpec { Name = "archive_*_2024", ImportInto = "all" },
                new CollectionImportSpec { Name = "*", ImportInto = "all" }
            };

            foreach (var spec in wildcardCases)
            {
                Assert.That(spec.HasCollectionWildcard, Is.True,
                    $"'{spec.Name}' should be detected as having wildcard");
            }

            // Test cases without wildcards
            var noWildcardCases = new[]
            {
                new CollectionImportSpec { Name = "exact_match", ImportInto = "all" },
                new CollectionImportSpec { Name = "project_alpha", ImportInto = "all" },
                new CollectionImportSpec { Name = "docs_2024", ImportInto = "all" }
            };

            foreach (var spec in noWildcardCases)
            {
                Assert.That(spec.HasCollectionWildcard, Is.False,
                    $"'{spec.Name}' should NOT be detected as having wildcard");
            }
        }

        #endregion

        #region Complex Filter Tests

        /// <summary>
        /// Verifies that complex filters with multiple mappings and document patterns are parsed correctly
        /// </summary>
        [Test]
        public void ImportFilter_ComplexFilter_ParsesCorrectly()
        {
            // Arrange
            var json = """
                {
                    "collections": [
                        { "name": "archive_2024_*", "import_into": "consolidated_archive" },
                        { "name": "archive_2025_*", "import_into": "consolidated_archive" },
                        {
                            "name": "current_project",
                            "import_into": "active_docs",
                            "documents": ["*_final", "*_approved"]
                        }
                    ]
                }
                """;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // Act
            var filter = JsonSerializer.Deserialize<ImportFilter>(json, options);

            // Assert
            Assert.That(filter, Is.Not.Null);
            Assert.That(filter.Collections, Is.Not.Null);
            Assert.That(filter.Collections.Count, Is.EqualTo(3));

            // First two specs: collection wildcards, no document filter
            Assert.That(filter.Collections[0].HasCollectionWildcard, Is.True);
            Assert.That(filter.Collections[0].HasDocumentFilter, Is.False);
            Assert.That(filter.Collections[1].HasCollectionWildcard, Is.True);
            Assert.That(filter.Collections[1].HasDocumentFilter, Is.False);

            // Third spec: no collection wildcard, has document filter
            Assert.That(filter.Collections[2].HasCollectionWildcard, Is.False);
            Assert.That(filter.Collections[2].HasDocumentFilter, Is.True);
            Assert.That(filter.Collections[2].Documents.Count, Is.EqualTo(2));

            // Verify unique targets
            var targets = filter.GetTargetCollections();
            Assert.That(targets.Count, Is.EqualTo(2));
            Assert.That(targets, Contains.Item("consolidated_archive"));
            Assert.That(targets, Contains.Item("active_docs"));
        }

        #endregion
    }
}
