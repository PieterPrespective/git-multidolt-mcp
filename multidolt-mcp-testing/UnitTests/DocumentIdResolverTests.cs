using Embranch.Services;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;

namespace EmbranchTesting.UnitTests
{
    /// <summary>
    /// Unit tests for DocumentIdResolver functionality
    /// </summary>
    [TestFixture]
    public class DocumentIdResolverTests
    {
        private Mock<IChromaDbService> _mockChromaService;
        private DocumentIdResolver _resolver;
        private Mock<ILogger<DocumentIdResolver>> _mockLogger;

        [SetUp]
        public void SetUp()
        {
            _mockChromaService = new Mock<IChromaDbService>();
            _mockLogger = new Mock<ILogger<DocumentIdResolver>>();
            _resolver = new DocumentIdResolver(_mockChromaService.Object, _mockLogger.Object);
        }

        /// <summary>
        /// Tests that chunk IDs are correctly identified
        /// </summary>
        [Test]
        public void IsChunkId_WithChunkId_ReturnsTrue()
        {
            // Arrange
            var chunkId = "doc1_chunk_0";
            
            // Act
            var result = _resolver.IsChunkId(chunkId);
            
            // Assert
            Assert.That(result, Is.True);
        }

        /// <summary>
        /// Tests that base IDs are correctly identified
        /// </summary>
        [Test]
        public void IsChunkId_WithBaseId_ReturnsFalse()
        {
            // Arrange
            var baseId = "doc1";
            
            // Act
            var result = _resolver.IsChunkId(baseId);
            
            // Assert
            Assert.That(result, Is.False);
        }

        /// <summary>
        /// Tests base ID extraction from chunk ID
        /// </summary>
        [Test]
        public void ExtractBaseDocumentId_WithChunkId_ReturnsBaseId()
        {
            // Arrange
            var chunkId = "doc1_chunk_0";
            
            // Act
            var result = _resolver.ExtractBaseDocumentId(chunkId);
            
            // Assert
            Assert.That(result, Is.EqualTo("doc1"));
        }

        /// <summary>
        /// Tests base ID extraction with base ID (should return as-is)
        /// </summary>
        [Test]
        public void ExtractBaseDocumentId_WithBaseId_ReturnsBaseId()
        {
            // Arrange
            var baseId = "doc1";
            
            // Act
            var result = _resolver.ExtractBaseDocumentId(baseId);
            
            // Assert
            Assert.That(result, Is.EqualTo("doc1"));
        }

        /// <summary>
        /// Tests expansion of base ID to chunk IDs via metadata query
        /// </summary>
        [Test]
        public async Task ExpandToChunkIdsAsync_WithBaseId_ReturnsChunkIds()
        {
            // Arrange
            var baseId = "doc1";
            var expectedChunkIds = new List<string> { "doc1_chunk_0", "doc1_chunk_1" };
            
            var mockResult = new Dictionary<string, object>
            {
                ["ids"] = expectedChunkIds.Cast<object>().ToList()
            };
            
            _mockChromaService.Setup(s => s.GetDocumentsAsync("test_collection", null, It.IsAny<Dictionary<string, object>>(), null, false))
                .ReturnsAsync(mockResult);
            
            // Act
            var result = await _resolver.ExpandToChunkIdsAsync("test_collection", baseId);
            
            // Assert
            Assert.That(result, Is.EquivalentTo(expectedChunkIds));
        }

        /// <summary>
        /// Tests expansion of chunk ID (should return as-is)
        /// </summary>
        [Test]
        public async Task ExpandToChunkIdsAsync_WithChunkId_ReturnsSame()
        {
            // Arrange
            var chunkId = "doc1_chunk_0";
            
            // Act
            var result = await _resolver.ExpandToChunkIdsAsync("test_collection", chunkId);
            
            // Assert
            Assert.That(result, Is.EquivalentTo(new List<string> { chunkId }));
        }

        /// <summary>
        /// Tests extraction of unique base IDs from mixed list
        /// </summary>
        [Test]
        public void ExtractUniqueBaseDocumentIds_WithMixedIds_ReturnsUniqueBaseIds()
        {
            // Arrange
            var mixedIds = new List<string> 
            { 
                "doc1", 
                "doc1_chunk_0", 
                "doc1_chunk_1", 
                "doc2", 
                "doc2_chunk_0" 
            };
            
            // Act
            var result = _resolver.ExtractUniqueBaseDocumentIds(mixedIds);
            
            // Assert
            Assert.That(result, Is.EquivalentTo(new List<string> { "doc1", "doc2" }));
        }
    }
}