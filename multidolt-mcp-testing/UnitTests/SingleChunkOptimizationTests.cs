using Embranch.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Embranch.Models;

namespace EmbranchTesting.UnitTests
{
    /// <summary>
    /// Unit tests specifically for the single chunk optimization and double-chunking prevention.
    /// Tests the fixes implemented to prevent doc3_chunk_0_chunk_0 scenarios.
    /// </summary>
    [TestFixture]
    public class SingleChunkOptimizationTests
    {
        private ChromaPersistentDbService _chromaService;
        private DocumentIdResolver _idResolver;
        private Mock<ILogger<ChromaPersistentDbService>> _mockLogger;
        private Mock<ILogger<DocumentIdResolver>> _mockResolverLogger;
        private string _testDirectory;

        [SetUp]
        public void SetUp()
        {
            // Initialize Python context first
            PythonContext.Initialize();
            
            // Create temporary directory for test
            _testDirectory = Path.Combine(Path.GetTempPath(), $"SingleChunkTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testDirectory);

            var chromaDataPath = Path.Combine(_testDirectory, "chroma_data");
            Directory.CreateDirectory(chromaDataPath);

            // Set up configuration
            var serverConfig = new ServerConfiguration
            {
                ChromaDataPath = chromaDataPath,
                ChromaMode = "persistent"
            };

            // Set up loggers
            var loggerFactory = LoggerFactory.Create(builder => 
                builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            
            _mockLogger = new Mock<ILogger<ChromaPersistentDbService>>();
            _mockResolverLogger = new Mock<ILogger<DocumentIdResolver>>();

            // Create services
            _chromaService = new ChromaPersistentDbService(_mockLogger.Object, Options.Create(serverConfig), null);
            _idResolver = new DocumentIdResolver(_chromaService, _mockResolverLogger.Object);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                _chromaService?.Dispose();
                
                if (Directory.Exists(_testDirectory))
                {
                    Directory.Delete(_testDirectory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Cleanup warning: {ex.Message}");
            }
        }

        /// <summary>
        /// Test that small documents use original ID without _chunk_0 suffix.
        /// This is the key optimization to prevent double-chunking.
        /// </summary>
        [Test]
        public async Task SmallDocument_ShouldUseOriginalId_WithoutChunkSuffix()
        {
            // Arrange
            await _chromaService.CreateCollectionAsync("single_chunk_test");
            
            var smallDocument = "Small doc content";  // Under 512 characters
            var documentId = "small_doc_1";
            
            // Act - Add small document
            var result = await _chromaService.AddDocumentsAsync("single_chunk_test",
                new List<string> { smallDocument },
                new List<string> { documentId });

            // Assert - Addition succeeds
            Assert.That(result, Is.True, "Document addition should succeed");

            // Verify the document is stored with original ID (not small_doc_1_chunk_0)
            var retrievedDocs = await _chromaService.GetDocumentsAsync("single_chunk_test");
            Assert.That(retrievedDocs, Is.Not.Null);

            var docsDict = (Dictionary<string, object>)retrievedDocs;
            var ids = (List<object>)docsDict["ids"];
            var documents = (List<object>)docsDict["documents"];

            // Should have exactly one document with original ID
            Assert.That(ids.Count, Is.EqualTo(1), "Should have exactly one document");
            Assert.That(ids[0].ToString(), Is.EqualTo(documentId), "Should use original ID, not add _chunk_0 suffix");
            Assert.That(documents[0].ToString(), Is.EqualTo(smallDocument), "Content should match");

            TestContext.WriteLine($"✅ Small document stored with ID: {ids[0]}");
        }

        /// <summary>
        /// Test that already-chunked documents are not re-chunked.
        /// This prevents the double-chunking issue where doc3_chunk_0 becomes doc3_chunk_0_chunk_0.
        /// </summary>
        [Test]
        public async Task AlreadyChunkedDocument_ShouldNotBeReChunked()
        {
            // Arrange
            await _chromaService.CreateCollectionAsync("prevent_double_chunk");
            
            var chunkContent = "This is content from an existing chunk";
            var chunkId = "original_doc_chunk_0";  // Already a chunk ID
            
            // Act - Add document that's already a chunk
            var result = await _chromaService.AddDocumentsAsync("prevent_double_chunk",
                new List<string> { chunkContent },
                new List<string> { chunkId });

            // Assert
            Assert.That(result, Is.True, "Chunk addition should succeed");

            // Verify it kept the original chunk ID (not original_doc_chunk_0_chunk_0)
            var retrievedDocs = await _chromaService.GetDocumentsAsync("prevent_double_chunk");
            var docsDict = (Dictionary<string, object>)retrievedDocs;
            var ids = (List<object>)docsDict["ids"];

            Assert.That(ids.Count, Is.EqualTo(1), "Should have exactly one document");
            Assert.That(ids[0].ToString(), Is.EqualTo(chunkId), "Should preserve original chunk ID");

            // Most importantly: should NOT have double-chunk suffix (e.g., _chunk_0_chunk_0)
            Assert.That(ids[0].ToString(), Does.Not.Contain("_chunk_0_chunk_"), "Should not add double-chunk suffix");
            Assert.That(ids[0].ToString(), Does.Not.EndWith("_chunk_0_chunk_0"), "Should not have _chunk_0_chunk_0 pattern");

            TestContext.WriteLine($"✅ Chunk document preserved ID: {ids[0]}");
        }

        /// <summary>
        /// Test that large documents still get chunked properly with _chunk_# suffix.
        /// </summary>
        [Test]
        public async Task LargeDocument_ShouldCreateMultipleChunks_WithChunkSuffix()
        {
            // Arrange
            await _chromaService.CreateCollectionAsync("multi_chunk_test");
            
            var largeDocument = new string('A', 800);  // 800 characters, should create multiple chunks
            var documentId = "large_doc_1";
            
            // Act
            var result = await _chromaService.AddDocumentsAsync("multi_chunk_test",
                new List<string> { largeDocument },
                new List<string> { documentId });

            // Assert
            Assert.That(result, Is.True, "Large document addition should succeed");

            var retrievedDocs = await _chromaService.GetDocumentsAsync("multi_chunk_test");
            var docsDict = (Dictionary<string, object>)retrievedDocs;
            var ids = (List<object>)docsDict["ids"];

            // Should have multiple chunks with _chunk_# suffix
            Assert.That(ids.Count, Is.GreaterThan(1), "Large document should create multiple chunks");
            
            var chunkIds = ids.Select(id => id.ToString()).ToList();
            for (int i = 0; i < chunkIds.Count; i++)
            {
                var expectedId = $"{documentId}_chunk_{i}";
                Assert.That(chunkIds[i], Is.EqualTo(expectedId), $"Chunk {i} should follow naming pattern");
            }

            TestContext.WriteLine($"✅ Large document created {ids.Count} chunks: {string.Join(", ", chunkIds)}");
        }

        /// <summary>
        /// Test the specific scenario from the failing test: prevent doc3_chunk_0_chunk_0.
        /// This simulates what happens during sync operations.
        /// </summary>
        [Test]
        public async Task SyncScenario_PreventDoubleChunking_OfDoc3()
        {
            // Arrange
            await _chromaService.CreateCollectionAsync("alpha");
            
            // Step 1: Simulate initial document addition (user action)
            var doc3Content = "Doc 3 content";  // Small document
            await _chromaService.AddDocumentsAsync("alpha",
                new List<string> { doc3Content },
                new List<string> { "doc3" },
                markAsLocalChange: true);

            // Verify initial state
            var initialDocs = await _chromaService.GetDocumentsAsync("alpha");
            var initialDict = (Dictionary<string, object>)initialDocs;
            var initialIds = (List<object>)initialDict["ids"];
            
            TestContext.WriteLine($"Initial state: {string.Join(", ", initialIds.Select(id => id.ToString()))}");
            Assert.That(initialIds[0].ToString(), Is.EqualTo("doc3"), "Should use original ID for small doc");

            // Step 2: Simulate sync operation that might try to re-add chunks
            // This is where the double-chunking bug occurred
            await _chromaService.DeleteCollectionAsync("alpha");  // Reset
            await _chromaService.CreateCollectionAsync("alpha");

            // Simulate SyncManager trying to add pre-chunked data
            var preChunkedId = "doc3_chunk_0";
            var preChunkedContent = doc3Content;
            
            await _chromaService.AddDocumentsAsync("alpha",
                new List<string> { preChunkedContent },
                new List<string> { preChunkedId },  // Already has _chunk_0
                markAsLocalChange: false);

            // Assert - Should not create doc3_chunk_0_chunk_0
            var finalDocs = await _chromaService.GetDocumentsAsync("alpha");
            var finalDict = (Dictionary<string, object>)finalDocs;
            var finalIds = (List<object>)finalDict["ids"];

            TestContext.WriteLine($"Final state: {string.Join(", ", finalIds.Select(id => id.ToString()))}");
            Assert.That(finalIds.Count, Is.EqualTo(1), "Should have exactly one document");
            Assert.That(finalIds[0].ToString(), Is.EqualTo("doc3_chunk_0"), "Should preserve pre-chunked ID");
            Assert.That(finalIds[0].ToString(), Does.Not.Contain("_chunk_0_chunk_0"), "Should NOT create double-chunk suffix");

            TestContext.WriteLine("✅ Successfully prevented double-chunking in sync scenario");
        }

        /// <summary>
        /// Test that deletion works correctly with the new single-chunk optimization.
        /// Documents stored without _chunk_0 suffix should still be deletable by base ID.
        /// </summary>
        [Test]
        public async Task SingleChunkDocument_ShouldBeDeletedByBaseId()
        {
            // Arrange
            await _chromaService.CreateCollectionAsync("deletion_test");
            
            var smallDocument = "Small document for deletion test";
            var documentId = "deletable_small_doc";
            
            // Add small document (should use original ID without _chunk_0)
            await _chromaService.AddDocumentsAsync("deletion_test",
                new List<string> { smallDocument },
                new List<string> { documentId });

            // Verify it exists
            var beforeDeletion = await _chromaService.GetDocumentsAsync("deletion_test");
            var beforeDict = (Dictionary<string, object>)beforeDeletion;
            var beforeIds = (List<object>)beforeDict["ids"];
            Assert.That(beforeIds.Count, Is.EqualTo(1), "Document should exist before deletion");

            // Act - Delete by base ID
            var deleteResult = await _chromaService.DeleteDocumentsAsync("deletion_test",
                new List<string> { documentId },
                expandChunks: true);

            // Assert
            Assert.That(deleteResult, Is.True, "Deletion should succeed");

            var afterDeletion = await _chromaService.GetDocumentsAsync("deletion_test");
            var afterDict = (Dictionary<string, object>)afterDeletion;
            var afterIds = (List<object>)afterDict["ids"];
            Assert.That(afterIds.Count, Is.EqualTo(0), "Document should be deleted");

            TestContext.WriteLine("✅ Single chunk document successfully deleted by base ID");
        }
    }
}