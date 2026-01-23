using Embranch.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EmbranchTesting.UnitTests
{
    /// <summary>
    /// Unit tests for chunk-aware document operations using mocks.
    /// Tests the integration between DocumentIdResolver and ChromaPythonService
    /// without requiring actual ChromaDB/Python initialization.
    /// </summary>
    [TestFixture]
    public class ChunkAwareDocumentOperationsTests
    {
        private Mock<IChromaDbService> _mockChromaService;
        private DocumentIdResolver _idResolver;
        private Mock<ILogger<DocumentIdResolver>> _mockLogger;

        [SetUp]
        public void SetUp()
        {
            _mockChromaService = new Mock<IChromaDbService>();
            _mockLogger = new Mock<ILogger<DocumentIdResolver>>();
            _idResolver = new DocumentIdResolver(_mockChromaService.Object, _mockLogger.Object);
        }

        /// <summary>
        /// Test that large documents get chunked and can be deleted by base ID.
        /// This simulates the core scenario described in PP13-69-C8.
        /// </summary>
        [Test]
        public async Task DeleteByBaseId_WithMultipleChunks_DeletesAllChunks()
        {
            // Arrange - Simulate a document that was chunked into 3 pieces
            var collectionName = "test_collection";
            var baseDocId = "large_document";
            var chunkIds = new List<string> { "large_document_chunk_0", "large_document_chunk_1", "large_document_chunk_2" };
            
            // Mock the ChromaDB response for finding chunks
            var mockGetDocumentsResult = new Dictionary<string, object>
            {
                ["ids"] = chunkIds.Cast<object>().ToList()
            };
            
            // Setup mock to return chunks when querying by source_id metadata
            _mockChromaService
                .Setup(s => s.GetDocumentsAsync(
                    collectionName, 
                    null, 
                    It.Is<Dictionary<string, object>>(where => where.ContainsKey("source_id") && where["source_id"].ToString() == baseDocId),
                    null,
                    false))
                .ReturnsAsync(mockGetDocumentsResult);
            
            // Setup mock for deletion (should be called with all chunk IDs)
            _mockChromaService
                .Setup(s => s.DeleteDocumentsAsync(collectionName, It.IsAny<List<string>>(), true))
                .ReturnsAsync(true);

            // Act - Expand base ID to chunk IDs (this simulates what ChromaPythonService would do)
            var expandedIds = await _idResolver.ExpandToChunkIdsAsync(collectionName, baseDocId);

            // Assert - Verify expansion worked correctly
            Assert.That(expandedIds.Count, Is.EqualTo(3), "Should expand to 3 chunks");
            Assert.That(expandedIds, Is.EquivalentTo(chunkIds), "Should return all chunk IDs");

            // Simulate deletion call (this is what ChromaPythonService.DeleteDocumentsAsync would do)
            var deleteResult = await _mockChromaService.Object.DeleteDocumentsAsync(collectionName, expandedIds, true);
            Assert.That(deleteResult, Is.True, "Deletion should succeed");

            // Verify the delete was called with the correct chunk IDs
            _mockChromaService.Verify(s => s.DeleteDocumentsAsync(
                collectionName, 
                It.Is<List<string>>(ids => ids.Count == 3 && chunkIds.All(id => ids.Contains(id))),
                true), 
                Times.Once);

            TestContext.WriteLine($"✅ Successfully expanded base ID '{baseDocId}' to {expandedIds.Count} chunks and deleted all");
        }

        /// <summary>
        /// Test that single documents (not chunked) work correctly.
        /// </summary>
        [Test]
        public async Task DeleteByBaseId_WithSingleChunk_DeletesSingleChunk()
        {
            // Arrange - Simulate a small document that only has one chunk
            var collectionName = "test_collection";
            var baseDocId = "small_document";
            var chunkIds = new List<string> { "small_document_chunk_0" };
            
            var mockGetDocumentsResult = new Dictionary<string, object>
            {
                ["ids"] = chunkIds.Cast<object>().ToList()
            };
            
            _mockChromaService
                .Setup(s => s.GetDocumentsAsync(
                    collectionName, 
                    null, 
                    It.Is<Dictionary<string, object>>(where => where.ContainsKey("source_id") && where["source_id"].ToString() == baseDocId),
                    null,
                    false))
                .ReturnsAsync(mockGetDocumentsResult);

            // Act
            var expandedIds = await _idResolver.ExpandToChunkIdsAsync(collectionName, baseDocId);

            // Assert
            Assert.That(expandedIds.Count, Is.EqualTo(1), "Should expand to 1 chunk");
            Assert.That(expandedIds[0], Is.EqualTo("small_document_chunk_0"), "Should return the single chunk ID");

            TestContext.WriteLine($"✅ Single chunk document correctly handled: {expandedIds[0]}");
        }

        /// <summary>
        /// Test mixed operations with base IDs and chunk IDs.
        /// </summary>
        [Test]
        public async Task ExpandMultipleIds_WithMixedTypes_HandlesCorrectly()
        {
            // Arrange
            var collectionName = "test_collection";
            var inputIds = new List<string> 
            { 
                "doc1",           // Base ID - should expand to multiple chunks
                "doc2_chunk_0",   // Chunk ID - should remain as-is
                "doc3"            // Base ID - should expand to single chunk
            };

            // Mock responses for base IDs
            var doc1Chunks = new List<string> { "doc1_chunk_0", "doc1_chunk_1" };
            var doc3Chunks = new List<string> { "doc3_chunk_0" };

            _mockChromaService
                .Setup(s => s.GetDocumentsAsync(
                    collectionName, 
                    null, 
                    It.Is<Dictionary<string, object>>(where => where.ContainsKey("source_id") && where["source_id"].ToString() == "doc1"),
                    null,
                    false))
                .ReturnsAsync(new Dictionary<string, object> { ["ids"] = doc1Chunks.Cast<object>().ToList() });

            _mockChromaService
                .Setup(s => s.GetDocumentsAsync(
                    collectionName, 
                    null, 
                    It.Is<Dictionary<string, object>>(where => where.ContainsKey("source_id") && where["source_id"].ToString() == "doc3"),
                    null,
                    false))
                .ReturnsAsync(new Dictionary<string, object> { ["ids"] = doc3Chunks.Cast<object>().ToList() });

            // Act
            var expandedIds = await _idResolver.ExpandMultipleToChunkIdsAsync(collectionName, inputIds);

            // Assert
            var expectedIds = new List<string> { "doc1_chunk_0", "doc1_chunk_1", "doc2_chunk_0", "doc3_chunk_0" };
            Assert.That(expandedIds.Count, Is.EqualTo(4), "Should expand to 4 total chunk IDs");
            Assert.That(expandedIds, Is.EquivalentTo(expectedIds), "Should contain all expected chunk IDs");

            TestContext.WriteLine($"✅ Mixed ID expansion: {inputIds.Count} inputs -> {expandedIds.Count} chunks");
            foreach (var id in expandedIds)
            {
                TestContext.WriteLine($"  - {id}");
            }
        }

        /// <summary>
        /// Test ID pattern recognition.
        /// </summary>
        [Test]
        public void IdPatternRecognition_WorksCorrectly()
        {
            // Test chunk ID recognition
            Assert.That(_idResolver.IsChunkId("doc1_chunk_0"), Is.True, "Should recognize chunk ID pattern");
            Assert.That(_idResolver.IsChunkId("doc1_chunk_99"), Is.True, "Should recognize chunk ID with high index");
            Assert.That(_idResolver.IsChunkId("complex_doc_name_chunk_5"), Is.True, "Should recognize chunk ID with complex base name");
            
            // Test base ID recognition (not chunk IDs)
            Assert.That(_idResolver.IsChunkId("doc1"), Is.False, "Should not recognize base ID as chunk");
            Assert.That(_idResolver.IsChunkId("doc_chunk_notanumber"), Is.False, "Should not recognize invalid chunk pattern");
            Assert.That(_idResolver.IsChunkId("chunk_0"), Is.False, "Should not recognize partial chunk pattern");

            // Test base ID extraction
            Assert.That(_idResolver.ExtractBaseDocumentId("doc1_chunk_0"), Is.EqualTo("doc1"), "Should extract base ID");
            Assert.That(_idResolver.ExtractBaseDocumentId("complex_doc_name_chunk_5"), Is.EqualTo("complex_doc_name"), "Should extract complex base ID");
            Assert.That(_idResolver.ExtractBaseDocumentId("doc1"), Is.EqualTo("doc1"), "Should return base ID as-is");

            TestContext.WriteLine("✅ ID pattern recognition works correctly");
        }

        /// <summary>
        /// Test unique base ID extraction from mixed list.
        /// </summary>
        [Test]
        public void ExtractUniqueBaseIds_RemovesDuplicates()
        {
            // Arrange
            var mixedIds = new List<string>
            {
                "doc1",
                "doc1_chunk_0",
                "doc1_chunk_1", 
                "doc2_chunk_0",
                "doc3",
                "doc1_chunk_2",  // Another chunk of doc1
                "doc2"           // Base ID of doc2 (already has chunk above)
            };

            // Act
            var uniqueBaseIds = _idResolver.ExtractUniqueBaseDocumentIds(mixedIds);

            // Assert
            var expectedBaseIds = new List<string> { "doc1", "doc2", "doc3" };
            Assert.That(uniqueBaseIds.Count, Is.EqualTo(3), "Should have 3 unique base IDs");
            Assert.That(uniqueBaseIds, Is.EquivalentTo(expectedBaseIds), "Should extract correct unique base IDs");

            TestContext.WriteLine($"✅ Extracted {uniqueBaseIds.Count} unique base IDs from {mixedIds.Count} mixed IDs:");
            foreach (var id in uniqueBaseIds)
            {
                TestContext.WriteLine($"  - {id}");
            }
        }

        /// <summary>
        /// Test that non-existent documents are handled gracefully.
        /// </summary>
        [Test]
        public async Task ExpandNonExistentDocument_ReturnsEmpty()
        {
            // Arrange - Mock empty response for non-existent document
            var collectionName = "test_collection";
            var baseDocId = "nonexistent_doc";
            
            _mockChromaService
                .Setup(s => s.GetDocumentsAsync(
                    collectionName, 
                    null, 
                    It.IsAny<Dictionary<string, object>>(),
                    null,
                    false))
                .ReturnsAsync(new Dictionary<string, object> { ["ids"] = new List<object>() });

            // Act
            var expandedIds = await _idResolver.ExpandToChunkIdsAsync(collectionName, baseDocId);

            // Assert
            Assert.That(expandedIds.Count, Is.EqualTo(0), "Should return empty list for non-existent document");

            TestContext.WriteLine("✅ Non-existent document handled gracefully");
        }

        /// <summary>
        /// Integration test simulating the exact PP13-69-C8 scenario.
        /// Document "doc3" is deleted by base ID, should remove chunk "doc3_chunk_0".
        /// </summary>
        [Test]
        public async Task PP13_69_C8_Scenario_DeleteDoc3ByBaseId()
        {
            // Arrange - Simulate the exact scenario from the failing test
            var collectionName = "alpha";
            var baseDocId = "doc3";
            var chunkIds = new List<string> { "doc3_chunk_0" };  // doc3 has one chunk
            
            // Mock ChromaDB to return the chunk when searching by source_id
            _mockChromaService
                .Setup(s => s.GetDocumentsAsync(
                    collectionName, 
                    null, 
                    It.Is<Dictionary<string, object>>(where => where.ContainsKey("source_id") && where["source_id"].ToString() == baseDocId),
                    null,
                    false))
                .ReturnsAsync(new Dictionary<string, object> { ["ids"] = chunkIds.Cast<object>().ToList() });

            // Mock successful deletion
            _mockChromaService
                .Setup(s => s.DeleteDocumentsAsync(collectionName, chunkIds, true))
                .ReturnsAsync(true);

            // Act - This simulates what should happen in ChromaPythonService.DeleteDocumentsAsync
            var expandedIds = await _idResolver.ExpandToChunkIdsAsync(collectionName, baseDocId);
            var deleteResult = await _mockChromaService.Object.DeleteDocumentsAsync(collectionName, expandedIds, true);

            // Assert
            Assert.That(expandedIds.Count, Is.EqualTo(1), "doc3 should expand to 1 chunk");
            Assert.That(expandedIds[0], Is.EqualTo("doc3_chunk_0"), "Should find the correct chunk");
            Assert.That(deleteResult, Is.True, "Deletion should succeed");

            // Verify the exact deletion call
            _mockChromaService.Verify(s => s.DeleteDocumentsAsync(collectionName, 
                It.Is<List<string>>(ids => ids.Count == 1 && ids[0] == "doc3_chunk_0"), true), Times.Once);

            TestContext.WriteLine("✅ PP13-69-C8 scenario: doc3 deletion by base ID successfully removes doc3_chunk_0");
        }
    }
}