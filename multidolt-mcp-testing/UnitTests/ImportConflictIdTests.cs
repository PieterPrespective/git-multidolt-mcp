using NUnit.Framework;
using DMMS.Models;

namespace DMMS.Testing.UnitTests
{
    /// <summary>
    /// Unit tests for import conflict ID generation and utility functions.
    /// Tests deterministic ID generation and content hash computation.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public class ImportConflictIdTests
    {
        #region Conflict ID Generation Tests

        /// <summary>
        /// Verifies that the same inputs always produce the same conflict ID
        /// </summary>
        [Test]
        public void ConflictIdGeneration_SameInput_ReturnsSameId()
        {
            // Arrange
            var sourceCol = "external_docs";
            var targetCol = "local_docs";
            var docId = "doc_001";
            var type = ImportConflictType.ContentModification;

            // Act
            var id1 = ImportUtility.GenerateImportConflictId(sourceCol, targetCol, docId, type);
            var id2 = ImportUtility.GenerateImportConflictId(sourceCol, targetCol, docId, type);
            var id3 = ImportUtility.GenerateImportConflictId(sourceCol, targetCol, docId, type);

            // Assert
            Assert.That(id1, Is.Not.Null.And.Not.Empty);
            Assert.That(id1, Is.EqualTo(id2), "Same inputs should produce identical ID");
            Assert.That(id1, Is.EqualTo(id3), "Same inputs should produce identical ID on repeated calls");
        }

        /// <summary>
        /// Verifies that different inputs produce different conflict IDs
        /// </summary>
        [Test]
        public void ConflictIdGeneration_DifferentInput_ReturnsDifferentId()
        {
            // Arrange - Base case
            var id1 = ImportUtility.GenerateImportConflictId(
                "source1", "target1", "doc1", ImportConflictType.ContentModification);

            // Different source collection
            var id2 = ImportUtility.GenerateImportConflictId(
                "source2", "target1", "doc1", ImportConflictType.ContentModification);

            // Different target collection
            var id3 = ImportUtility.GenerateImportConflictId(
                "source1", "target2", "doc1", ImportConflictType.ContentModification);

            // Different document ID
            var id4 = ImportUtility.GenerateImportConflictId(
                "source1", "target1", "doc2", ImportConflictType.ContentModification);

            // Different conflict type
            var id5 = ImportUtility.GenerateImportConflictId(
                "source1", "target1", "doc1", ImportConflictType.MetadataConflict);

            // Assert - All IDs should be unique
            var allIds = new[] { id1, id2, id3, id4, id5 };
            Assert.That(allIds.Distinct().Count(), Is.EqualTo(5),
                "All different inputs should produce unique IDs");
        }

        /// <summary>
        /// Verifies that conflict IDs have the correct format
        /// </summary>
        [Test]
        public void ConflictIdGeneration_Format_StartsWithImpPrefix()
        {
            // Arrange & Act
            var id = ImportUtility.GenerateImportConflictId(
                "source", "target", "doc", ImportConflictType.ContentModification);

            // Assert
            Assert.That(id, Does.StartWith("imp_"), "Conflict ID should start with 'imp_' prefix");
            Assert.That(id.Length, Is.EqualTo(16), "Conflict ID should be 'imp_' + 12 hex chars = 16 chars");
        }

        /// <summary>
        /// Verifies that conflict ID hex portion is lowercase
        /// </summary>
        [Test]
        public void ConflictIdGeneration_HexPortion_IsLowercase()
        {
            // Arrange & Act
            var id = ImportUtility.GenerateImportConflictId(
                "SOURCE", "TARGET", "DOC", ImportConflictType.ContentModification);

            // Extract hex portion (after "imp_")
            var hexPortion = id.Substring(4);

            // Assert
            Assert.That(hexPortion, Is.EqualTo(hexPortion.ToLowerInvariant()),
                "Hex portion should be lowercase");
            Assert.That(hexPortion, Does.Match("^[0-9a-f]+$"),
                "Hex portion should contain only valid hex characters");
        }

        /// <summary>
        /// Verifies determinism across all conflict types
        /// </summary>
        [Test]
        public void ConflictIdGeneration_AllConflictTypes_ProduceDeterministicIds()
        {
            var conflictTypes = Enum.GetValues<ImportConflictType>();

            foreach (var type in conflictTypes)
            {
                // Generate ID twice with same inputs
                var id1 = ImportUtility.GenerateImportConflictId("source", "target", "doc", type);
                var id2 = ImportUtility.GenerateImportConflictId("source", "target", "doc", type);

                Assert.That(id1, Is.EqualTo(id2),
                    $"Conflict type {type} should produce deterministic ID");
                Assert.That(id1, Does.StartWith("imp_"),
                    $"Conflict type {type} ID should have correct prefix");
            }
        }

        #endregion

        #region Content Hash Tests

        /// <summary>
        /// Verifies that content hash is deterministic
        /// </summary>
        [Test]
        public void ContentHash_SameContent_ReturnsSameHash()
        {
            // Arrange
            var content = "This is test document content.";

            // Act
            var hash1 = ImportUtility.ComputeContentHash(content);
            var hash2 = ImportUtility.ComputeContentHash(content);

            // Assert
            Assert.That(hash1, Is.Not.Null.And.Not.Empty);
            Assert.That(hash1, Is.EqualTo(hash2), "Same content should produce same hash");
        }

        /// <summary>
        /// Verifies that different content produces different hashes
        /// </summary>
        [Test]
        public void ContentHash_DifferentContent_ReturnsDifferentHash()
        {
            // Arrange
            var content1 = "Content version 1";
            var content2 = "Content version 2";
            var content3 = "content version 1"; // Lowercase change

            // Act
            var hash1 = ImportUtility.ComputeContentHash(content1);
            var hash2 = ImportUtility.ComputeContentHash(content2);
            var hash3 = ImportUtility.ComputeContentHash(content3);

            // Assert
            Assert.That(hash1, Is.Not.EqualTo(hash2));
            Assert.That(hash1, Is.Not.EqualTo(hash3), "Hash should be case-sensitive");
        }

        /// <summary>
        /// Verifies that empty/null content returns empty hash
        /// </summary>
        [Test]
        public void ContentHash_EmptyOrNull_ReturnsEmpty()
        {
            Assert.That(ImportUtility.ComputeContentHash(null!), Is.Empty);
            Assert.That(ImportUtility.ComputeContentHash(""), Is.Empty);
        }

        /// <summary>
        /// Verifies that hash is valid SHA-256 hex format
        /// </summary>
        [Test]
        public void ContentHash_Format_IsValidSha256Hex()
        {
            // Arrange & Act
            var hash = ImportUtility.ComputeContentHash("test content");

            // Assert
            Assert.That(hash.Length, Is.EqualTo(64), "SHA-256 hex should be 64 characters");
            Assert.That(hash, Does.Match("^[0-9a-f]+$"), "Hash should be lowercase hex");
        }

        #endregion

        #region Resolution Type Parsing Tests

        /// <summary>
        /// Verifies that resolution type strings are parsed correctly
        /// </summary>
        [Test]
        [TestCase("keep_source", ImportResolutionType.KeepSource)]
        [TestCase("keepsource", ImportResolutionType.KeepSource)]
        [TestCase("KeepSource", ImportResolutionType.KeepSource)]
        [TestCase("KEEP_SOURCE", ImportResolutionType.KeepSource)]
        [TestCase("source", ImportResolutionType.KeepSource)]
        [TestCase("keep_target", ImportResolutionType.KeepTarget)]
        [TestCase("keeptarget", ImportResolutionType.KeepTarget)]
        [TestCase("target", ImportResolutionType.KeepTarget)]
        [TestCase("merge", ImportResolutionType.Merge)]
        [TestCase("skip", ImportResolutionType.Skip)]
        [TestCase("custom", ImportResolutionType.Custom)]
        public void ParseResolutionType_ValidStrings_ParsesCorrectly(string input, ImportResolutionType expected)
        {
            var result = ImportUtility.ParseResolutionType(input);
            Assert.That(result, Is.EqualTo(expected),
                $"'{input}' should parse to {expected}");
        }

        /// <summary>
        /// Verifies that invalid/unknown strings default to KeepSource
        /// </summary>
        [Test]
        public void ParseResolutionType_InvalidString_DefaultsToKeepSource()
        {
            Assert.That(ImportUtility.ParseResolutionType("invalid"), Is.EqualTo(ImportResolutionType.KeepSource));
            Assert.That(ImportUtility.ParseResolutionType("unknown"), Is.EqualTo(ImportResolutionType.KeepSource));
            Assert.That(ImportUtility.ParseResolutionType(""), Is.EqualTo(ImportResolutionType.KeepSource));
            Assert.That(ImportUtility.ParseResolutionType(null!), Is.EqualTo(ImportResolutionType.KeepSource));
        }

        #endregion

        #region Resolution Options Tests

        /// <summary>
        /// Verifies that appropriate resolution options are returned for each conflict type
        /// </summary>
        [Test]
        public void GetResolutionOptions_ContentModification_ReturnsAllOptions()
        {
            var options = ImportUtility.GetResolutionOptions(ImportConflictType.ContentModification);

            Assert.That(options, Contains.Item("keep_source"));
            Assert.That(options, Contains.Item("keep_target"));
            Assert.That(options, Contains.Item("merge"));
            Assert.That(options, Contains.Item("skip"));
            Assert.That(options, Contains.Item("custom"));
        }

        /// <summary>
        /// Verifies that metadata conflicts don't include custom option
        /// </summary>
        [Test]
        public void GetResolutionOptions_MetadataConflict_NoCustomOption()
        {
            var options = ImportUtility.GetResolutionOptions(ImportConflictType.MetadataConflict);

            Assert.That(options, Contains.Item("keep_source"));
            Assert.That(options, Contains.Item("keep_target"));
            Assert.That(options, Contains.Item("merge"));
            Assert.That(options, Contains.Item("skip"));
            Assert.That(options, Does.Not.Contain("custom"));
        }

        /// <summary>
        /// Verifies that collection and ID conflicts don't include merge/custom
        /// </summary>
        [Test]
        [TestCase(ImportConflictType.CollectionMismatch)]
        [TestCase(ImportConflictType.IdCollision)]
        public void GetResolutionOptions_StructuralConflicts_LimitedOptions(ImportConflictType type)
        {
            var options = ImportUtility.GetResolutionOptions(type);

            Assert.That(options, Contains.Item("keep_source"));
            Assert.That(options, Contains.Item("keep_target"));
            Assert.That(options, Contains.Item("skip"));
            Assert.That(options, Does.Not.Contain("custom"));
            Assert.That(options, Does.Not.Contain("merge"));
        }

        #endregion

        #region Auto-Resolvable Tests

        /// <summary>
        /// Verifies that metadata conflicts are auto-resolvable
        /// </summary>
        [Test]
        public void IsAutoResolvable_MetadataConflict_ReturnsTrue()
        {
            Assert.That(ImportUtility.IsAutoResolvable(ImportConflictType.MetadataConflict), Is.True);
        }

        /// <summary>
        /// Verifies that other conflict types are not auto-resolvable by default
        /// </summary>
        [Test]
        [TestCase(ImportConflictType.ContentModification)]
        [TestCase(ImportConflictType.CollectionMismatch)]
        [TestCase(ImportConflictType.IdCollision)]
        public void IsAutoResolvable_OtherConflicts_ReturnsFalse(ImportConflictType type)
        {
            Assert.That(ImportUtility.IsAutoResolvable(type), Is.False);
        }

        #endregion

        #region Suggested Resolution Tests

        /// <summary>
        /// Verifies that appropriate suggestions are made for each conflict type
        /// </summary>
        [Test]
        [TestCase(ImportConflictType.ContentModification, "keep_source")]
        [TestCase(ImportConflictType.MetadataConflict, "keep_source")]
        [TestCase(ImportConflictType.CollectionMismatch, "keep_target")]
        [TestCase(ImportConflictType.IdCollision, "skip")]
        public void GetSuggestedResolution_ReturnsAppropriate(ImportConflictType type, string expected)
        {
            var suggestion = ImportUtility.GetSuggestedResolution(type);
            Assert.That(suggestion, Is.EqualTo(expected),
                $"Conflict type {type} should suggest '{expected}'");
        }

        #endregion

        #region Consistency Tests (PP13-73 Prevention)

        /// <summary>
        /// PP13-73 Prevention: Verifies that conflict ID generation is consistent
        /// regardless of the order or timing of calls.
        /// This test ensures we don't repeat the issues from PP13-73 where
        /// conflict IDs differed between preview and execute operations.
        /// </summary>
        [Test]
        public void ConflictIdConsistency_PP13_73_Prevention_DeterministicAcrossCalls()
        {
            // Simulate multiple calls that would happen during preview and execute
            var testCases = new[]
            {
                ("col1", "col1", "doc1", ImportConflictType.ContentModification),
                ("col2", "col2", "doc2", ImportConflictType.MetadataConflict),
                ("src_*", "target", "doc3", ImportConflictType.IdCollision)
            };

            // Generate IDs as would happen in preview
            var previewIds = testCases.Select(tc =>
                ImportUtility.GenerateImportConflictId(tc.Item1, tc.Item2, tc.Item3, tc.Item4)).ToList();

            // Simulate time passing or other operations...
            Thread.Sleep(10);

            // Generate IDs as would happen in execute (after user provides resolutions)
            var executeIds = testCases.Select(tc =>
                ImportUtility.GenerateImportConflictId(tc.Item1, tc.Item2, tc.Item3, tc.Item4)).ToList();

            // Assert - IDs must match exactly
            for (int i = 0; i < previewIds.Count; i++)
            {
                Assert.That(executeIds[i], Is.EqualTo(previewIds[i]),
                    $"Test case {i}: Conflict ID must be identical between preview and execute");
            }
        }

        /// <summary>
        /// PP13-73 Prevention: Verifies that conflict IDs don't contain any random components
        /// </summary>
        [Test]
        public void ConflictIdConsistency_PP13_73_Prevention_NoRandomComponents()
        {
            // Generate 100 IDs with same inputs
            var ids = Enumerable.Range(0, 100)
                .Select(_ => ImportUtility.GenerateImportConflictId("src", "tgt", "doc", ImportConflictType.ContentModification))
                .ToList();

            // All should be identical
            Assert.That(ids.Distinct().Count(), Is.EqualTo(1),
                "All generated IDs should be identical when inputs are the same");
        }

        #endregion
    }
}
