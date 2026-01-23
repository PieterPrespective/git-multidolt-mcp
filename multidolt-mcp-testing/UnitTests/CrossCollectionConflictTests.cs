using NUnit.Framework;
using Embranch.Models;

namespace EmbranchTesting.UnitTests
{
    /// <summary>
    /// Unit tests for cross-collection ID collision detection utilities.
    /// Tests the new PP13-78 functionality for detecting and resolving
    /// document ID collisions across multiple source collections.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    [Category("PP13-78")]
    public class CrossCollectionConflictTests
    {
        #region Cross-Collection Conflict ID Generation Tests

        /// <summary>
        /// Verifies that GenerateCrossCollectionConflictId produces deterministic results
        /// </summary>
        [Test]
        public void GenerateCrossCollectionConflictId_SameInputs_ReturnsSameId()
        {
            // Arrange
            var source1 = "PP02-186";
            var source2 = "PP02-193";
            var target = "issueLogs";
            var docId = "planned_approach";

            // Act
            var id1 = ImportUtility.GenerateCrossCollectionConflictId(source1, source2, target, docId);
            var id2 = ImportUtility.GenerateCrossCollectionConflictId(source1, source2, target, docId);
            var id3 = ImportUtility.GenerateCrossCollectionConflictId(source1, source2, target, docId);

            // Assert
            Assert.That(id1, Is.Not.Null.And.Not.Empty);
            Assert.That(id1, Is.EqualTo(id2), "Same inputs should produce identical ID");
            Assert.That(id1, Is.EqualTo(id3), "Same inputs should produce identical ID on repeated calls");
        }

        /// <summary>
        /// Verifies that cross-collection conflict ID is order-independent
        /// (same ID regardless of which collection is passed first)
        /// </summary>
        [Test]
        public void GenerateCrossCollectionConflictId_OrderIndependent_ReturnsSameId()
        {
            // Arrange
            var source1 = "Collection_A";
            var source2 = "Collection_B";
            var target = "target_collection";
            var docId = "doc_001";

            // Act - Order 1: A, B
            var id1 = ImportUtility.GenerateCrossCollectionConflictId(source1, source2, target, docId);
            // Act - Order 2: B, A
            var id2 = ImportUtility.GenerateCrossCollectionConflictId(source2, source1, target, docId);

            // Assert
            Assert.That(id1, Is.EqualTo(id2),
                "Conflict ID should be the same regardless of source collection order");
        }

        /// <summary>
        /// Verifies that different document IDs produce different conflict IDs
        /// </summary>
        [Test]
        public void GenerateCrossCollectionConflictId_DifferentDocIds_ReturnsDifferentIds()
        {
            // Arrange
            var source1 = "PP02-186";
            var source2 = "PP02-193";
            var target = "issueLogs";

            // Act
            var id1 = ImportUtility.GenerateCrossCollectionConflictId(source1, source2, target, "doc1");
            var id2 = ImportUtility.GenerateCrossCollectionConflictId(source1, source2, target, "doc2");

            // Assert
            Assert.That(id1, Is.Not.EqualTo(id2),
                "Different document IDs should produce different conflict IDs");
        }

        /// <summary>
        /// Verifies that different target collections produce different conflict IDs
        /// </summary>
        [Test]
        public void GenerateCrossCollectionConflictId_DifferentTargets_ReturnsDifferentIds()
        {
            // Arrange
            var source1 = "PP02-186";
            var source2 = "PP02-193";
            var docId = "planned_approach";

            // Act
            var id1 = ImportUtility.GenerateCrossCollectionConflictId(source1, source2, "target1", docId);
            var id2 = ImportUtility.GenerateCrossCollectionConflictId(source1, source2, "target2", docId);

            // Assert
            Assert.That(id1, Is.Not.EqualTo(id2),
                "Different target collections should produce different conflict IDs");
        }

        /// <summary>
        /// Verifies that cross-collection conflict IDs have the correct format (xc_ prefix)
        /// </summary>
        [Test]
        public void GenerateCrossCollectionConflictId_Format_StartsWithXcPrefix()
        {
            // Arrange & Act
            var id = ImportUtility.GenerateCrossCollectionConflictId(
                "source1", "source2", "target", "doc");

            // Assert
            Assert.That(id, Does.StartWith("xc_"), "Cross-collection conflict ID should start with 'xc_' prefix");
            Assert.That(id.Length, Is.EqualTo(15), "Conflict ID should be 'xc_' + 12 hex chars = 15 chars");
        }

        /// <summary>
        /// Verifies that conflict ID hex portion is lowercase
        /// </summary>
        [Test]
        public void GenerateCrossCollectionConflictId_HexPortion_IsLowercase()
        {
            // Arrange & Act
            var id = ImportUtility.GenerateCrossCollectionConflictId(
                "SOURCE", "TARGET", "COLLECTION", "DOC");

            // Extract hex portion (after "xc_")
            var hexPortion = id.Substring(3);

            // Assert
            Assert.That(hexPortion, Is.EqualTo(hexPortion.ToLowerInvariant()),
                "Hex portion should be lowercase");
            Assert.That(hexPortion, Does.Match("^[0-9a-f]+$"),
                "Hex portion should contain only valid hex characters");
        }

        /// <summary>
        /// Verifies that cross-collection conflict IDs are different from regular import conflict IDs
        /// </summary>
        [Test]
        public void GenerateCrossCollectionConflictId_DifferentFromRegularConflictId()
        {
            // Arrange
            var source = "sourceCol";
            var target = "targetCol";
            var docId = "doc_001";

            // Act
            var crossCollectionId = ImportUtility.GenerateCrossCollectionConflictId(
                source, "otherSource", target, docId);
            var regularId = ImportUtility.GenerateImportConflictId(
                source, target, docId, ImportConflictType.IdCollision);

            // Assert
            Assert.That(crossCollectionId, Is.Not.EqualTo(regularId),
                "Cross-collection conflict ID should differ from regular import conflict ID");
            Assert.That(crossCollectionId, Does.StartWith("xc_"));
            Assert.That(regularId, Does.StartWith("imp_"));
        }

        #endregion

        #region New Resolution Type Parsing Tests

        /// <summary>
        /// Verifies that "namespace" resolution type is parsed correctly
        /// </summary>
        [Test]
        public void ParseResolutionType_Namespace_ReturnsNamespace()
        {
            // Test various formats
            Assert.That(ImportUtility.ParseResolutionType("namespace"), Is.EqualTo(ImportResolutionType.Namespace));
            Assert.That(ImportUtility.ParseResolutionType("Namespace"), Is.EqualTo(ImportResolutionType.Namespace));
            Assert.That(ImportUtility.ParseResolutionType("NAMESPACE"), Is.EqualTo(ImportResolutionType.Namespace));
        }

        /// <summary>
        /// Verifies that "keep_first" resolution type is parsed correctly
        /// </summary>
        [Test]
        public void ParseResolutionType_KeepFirst_ReturnsKeepFirst()
        {
            // Test various formats
            Assert.That(ImportUtility.ParseResolutionType("keep_first"), Is.EqualTo(ImportResolutionType.KeepFirst));
            Assert.That(ImportUtility.ParseResolutionType("keepfirst"), Is.EqualTo(ImportResolutionType.KeepFirst));
            Assert.That(ImportUtility.ParseResolutionType("KeepFirst"), Is.EqualTo(ImportResolutionType.KeepFirst));
            Assert.That(ImportUtility.ParseResolutionType("KEEP_FIRST"), Is.EqualTo(ImportResolutionType.KeepFirst));
            Assert.That(ImportUtility.ParseResolutionType("first"), Is.EqualTo(ImportResolutionType.KeepFirst));
            Assert.That(ImportUtility.ParseResolutionType("First"), Is.EqualTo(ImportResolutionType.KeepFirst));
        }

        /// <summary>
        /// Verifies that "keep_last" resolution type is parsed correctly
        /// </summary>
        [Test]
        public void ParseResolutionType_KeepLast_ReturnsKeepLast()
        {
            // Test various formats
            Assert.That(ImportUtility.ParseResolutionType("keep_last"), Is.EqualTo(ImportResolutionType.KeepLast));
            Assert.That(ImportUtility.ParseResolutionType("keeplast"), Is.EqualTo(ImportResolutionType.KeepLast));
            Assert.That(ImportUtility.ParseResolutionType("KeepLast"), Is.EqualTo(ImportResolutionType.KeepLast));
            Assert.That(ImportUtility.ParseResolutionType("KEEP_LAST"), Is.EqualTo(ImportResolutionType.KeepLast));
            Assert.That(ImportUtility.ParseResolutionType("last"), Is.EqualTo(ImportResolutionType.KeepLast));
            Assert.That(ImportUtility.ParseResolutionType("Last"), Is.EqualTo(ImportResolutionType.KeepLast));
        }

        #endregion

        #region Resolution Options for IdCollision Tests

        /// <summary>
        /// Verifies that IdCollision conflict type now includes namespace and keep_first/keep_last options
        /// </summary>
        [Test]
        public void GetResolutionOptions_IdCollision_IncludesNewOptions()
        {
            var options = ImportUtility.GetResolutionOptions(ImportConflictType.IdCollision);

            Assert.That(options, Contains.Item("namespace"), "Should include 'namespace' option");
            Assert.That(options, Contains.Item("keep_first"), "Should include 'keep_first' option");
            Assert.That(options, Contains.Item("keep_last"), "Should include 'keep_last' option");
            Assert.That(options, Contains.Item("skip"), "Should include 'skip' option");
        }

        /// <summary>
        /// Verifies that IdCollision does NOT include old incompatible options
        /// </summary>
        [Test]
        public void GetResolutionOptions_IdCollision_ExcludesIncompatibleOptions()
        {
            var options = ImportUtility.GetResolutionOptions(ImportConflictType.IdCollision);

            Assert.That(options, Does.Not.Contain("keep_source"), "Should not include 'keep_source' for cross-collection collision");
            Assert.That(options, Does.Not.Contain("keep_target"), "Should not include 'keep_target' for cross-collection collision");
            Assert.That(options, Does.Not.Contain("merge"), "Should not include 'merge' for cross-collection collision");
            Assert.That(options, Does.Not.Contain("custom"), "Should not include 'custom' for cross-collection collision");
        }

        #endregion

        #region Suggested Resolution Tests

        /// <summary>
        /// Verifies that IdCollision now suggests "namespace" as the resolution
        /// </summary>
        [Test]
        public void GetSuggestedResolution_IdCollision_ReturnsNamespace()
        {
            var suggestion = ImportUtility.GetSuggestedResolution(ImportConflictType.IdCollision);
            Assert.That(suggestion, Is.EqualTo("namespace"),
                "IdCollision should suggest 'namespace' resolution for cross-collection conflicts");
        }

        #endregion

        #region Auto-Resolvable Tests

        /// <summary>
        /// Verifies that IdCollision conflicts are NOT auto-resolvable
        /// </summary>
        [Test]
        public void IsAutoResolvable_IdCollision_ReturnsFalse()
        {
            Assert.That(ImportUtility.IsAutoResolvable(ImportConflictType.IdCollision), Is.False,
                "IdCollision (cross-collection conflict) should not be auto-resolvable");
        }

        #endregion

        #region Determinism / Consistency Tests

        /// <summary>
        /// PP13-78: Verifies that cross-collection conflict ID generation is consistent
        /// regardless of the order or timing of calls.
        /// </summary>
        [Test]
        public void CrossCollectionConflictIdConsistency_DeterministicAcrossCalls()
        {
            // Simulate multiple calls that would happen during preview and execute
            var testCases = new[]
            {
                ("PP02-186", "PP02-193", "issueLogs", "planned_approach"),
                ("SE-405", "SE-406", "issueLogs", "e2e_test_location"),
                ("Collection_A", "Collection_B", "target", "shared_doc")
            };

            // Generate IDs as would happen in preview
            var previewIds = testCases.Select(tc =>
                ImportUtility.GenerateCrossCollectionConflictId(tc.Item1, tc.Item2, tc.Item3, tc.Item4)).ToList();

            // Simulate time passing
            Thread.Sleep(10);

            // Generate IDs as would happen in execute
            var executeIds = testCases.Select(tc =>
                ImportUtility.GenerateCrossCollectionConflictId(tc.Item1, tc.Item2, tc.Item3, tc.Item4)).ToList();

            // Assert - IDs must match exactly
            for (int i = 0; i < previewIds.Count; i++)
            {
                Assert.That(executeIds[i], Is.EqualTo(previewIds[i]),
                    $"Test case {i}: Cross-collection conflict ID must be identical between preview and execute");
            }
        }

        /// <summary>
        /// PP13-78: Verifies that cross-collection conflict IDs are order-independent
        /// across multiple calls with swapped source collection order
        /// </summary>
        [Test]
        public void CrossCollectionConflictIdConsistency_OrderIndependentAcrossCalls()
        {
            var source1 = "Collection_Alpha";
            var source2 = "Collection_Beta";
            var target = "merged_collection";
            var docId = "shared_document";

            // Generate 50 IDs with order 1 (Alpha, Beta)
            var ids1 = Enumerable.Range(0, 50)
                .Select(_ => ImportUtility.GenerateCrossCollectionConflictId(source1, source2, target, docId))
                .ToList();

            // Generate 50 IDs with order 2 (Beta, Alpha)
            var ids2 = Enumerable.Range(0, 50)
                .Select(_ => ImportUtility.GenerateCrossCollectionConflictId(source2, source1, target, docId))
                .ToList();

            // All should be identical
            var allIds = ids1.Concat(ids2).ToList();
            Assert.That(allIds.Distinct().Count(), Is.EqualTo(1),
                "All generated IDs should be identical regardless of source collection order");
        }

        #endregion
    }
}
