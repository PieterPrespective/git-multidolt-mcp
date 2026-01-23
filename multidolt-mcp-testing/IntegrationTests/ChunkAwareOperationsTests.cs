using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for chunk-aware document operations (PP13-69-C8)
    /// Tests the DocumentIdResolver and ChromaPythonService integration for handling
    /// documents that are stored as multiple chunks in ChromaDB.
    /// </summary>
    [TestFixture]
    public class ChunkAwareOperationsTests
    {
        private ChromaPersistentDbService _chromaService;
        private DocumentIdResolver _idResolver;
        private ILogger<ChromaPersistentDbService> _logger;
        private ILogger<DocumentIdResolver> _resolverLogger;
        private string _testDirectory;
        private string _collectionName;

        [SetUp]
        public async Task SetUp()
        {
            // Create unique test directory
            _testDirectory = Path.Combine(Path.GetTempPath(), $"ChunkAwareTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testDirectory);

            var chromaDataPath = Path.Combine(_testDirectory, "chroma_data");
            Directory.CreateDirectory(chromaDataPath);

            // Initialize Python context first
            PythonContext.Initialize();

            // Set up configuration
            var serverConfig = new ServerConfiguration
            {
                ChromaDataPath = chromaDataPath,
                ChromaMode = "persistent"
            };

            // Set up loggers
            var loggerFactory = LoggerFactory.Create(builder => 
                builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            
            _logger = loggerFactory.CreateLogger<ChromaPersistentDbService>();
            _resolverLogger = loggerFactory.CreateLogger<DocumentIdResolver>();

            // Create services - first create chromaService without resolver, then create resolver with chromaService
            _chromaService = new ChromaPersistentDbService(_logger, Options.Create(serverConfig), null);
            
            // Create resolver with actual chromaService
            _idResolver = new DocumentIdResolver(_chromaService, _resolverLogger);

            _collectionName = "chunk_test_collection";

            // Clean up any existing collection
            var existingCollections = await _chromaService.ListCollectionsAsync();
            if (existingCollections.Contains(_collectionName))
            {
                await _chromaService.DeleteCollectionAsync(_collectionName);
            }

            // Create test collection
            await _chromaService.CreateCollectionAsync(_collectionName);
        }

        [TearDown]
        public async Task TearDown()
        {
            try
            {
                // Clean up collection
                if (_chromaService != null)
                {
                    await _chromaService.DeleteCollectionAsync(_collectionName);
                    
                    // Dispose service
                    _chromaService.Dispose();
                }
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Cleanup warning: {ex.Message}");
            }

            // Python context cleanup is handled automatically

            try
            {
                // Clean up directory
                if (Directory.Exists(_testDirectory))
                {
                    Directory.Delete(_testDirectory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Directory cleanup warning: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a large document with the specified number of characters.
        /// </summary>
        private static string CreateLargeDocument(int characters)
        {
            const string baseText = "This is test content for chunk testing. ";
            var sb = new StringBuilder();
            while (sb.Length < characters)
            {
                sb.Append(baseText);
            }
            return sb.ToString().Substring(0, characters);
        }

        /// <summary>
        /// Tests that a large document (>1024 characters) creates multiple chunks when added to ChromaDB.
        /// Verifies that chunks follow the expected naming pattern and contain proper metadata.
        /// </summary>
        [Test]
        public async Task AddLargeDocument_CreatesMultipleChunks()
        {
            // Arrange - Create document larger than default chunk size (512 chars)
            var documentId = "large_doc_1";
            var largeContent = CreateLargeDocument(1200); // Should create 3 chunks
            
            TestContext.WriteLine($"Creating document with {largeContent.Length} characters");

            // Act - Add document to ChromaDB
            var result = await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { largeContent },
                new List<string> { documentId });

            // Assert - Document addition succeeds
            Assert.That(result, Is.True, "Document addition should succeed");

            // Verify chunks were created
            var allDocs = await _chromaService.GetDocumentsAsync(_collectionName);
            Assert.That(allDocs, Is.Not.Null, "Should be able to retrieve documents");

            var docsDict = (Dictionary<string, object>)allDocs;
            var ids = (List<object>)docsDict["ids"];
            var documents = (List<object>)docsDict["documents"];
            var metadatas = (List<object>)docsDict["metadatas"];

            TestContext.WriteLine($"Found {ids.Count} chunks in collection");

            for(int i = 0; i < ids.Count; i++)
            {
                var docContent = documents[i]?.ToString() ?? "";
                var docId = ids[i]?.ToString() ?? "";
                TestContext.WriteLine($"[DOC CONTENT]  Chunk {i} @ {docId} content length: {docContent.Length}");
            }


            // Should have multiple chunks
            Assert.That(ids.Count, Is.GreaterThan(1), "Large document should create multiple chunks");

            // Verify chunk ID pattern
            var chunkIds = ids.Select(id => id.ToString()).ToList();
            var expectedChunkIds = new List<string>();
            for (int i = 0; i < ids.Count; i++)
            {
                expectedChunkIds.Add($"{documentId}_chunk_{i}");
            }

            Assert.That(chunkIds, Is.EquivalentTo(expectedChunkIds), 
                "Chunk IDs should follow expected pattern");

            // Verify metadata contains source_id for expansion
            for (int i = 0; i < metadatas.Count; i++)
            {
                var metadata = (Dictionary<string, object>)metadatas[i];
                Assert.That(metadata.ContainsKey("source_id"), Is.True, 
                    $"Chunk {i} should have source_id in metadata");
                Assert.That(metadata["source_id"].ToString(), Is.EqualTo(documentId),
                    $"Chunk {i} source_id should match document ID");
            }

            TestContext.WriteLine("✅ Large document correctly created multiple chunks with proper metadata");
        }

        /// <summary>
        /// Tests that deleting a document by its base ID removes all associated chunks.
        /// </summary>
        [Test]
        public async Task DeleteDocumentByBaseId_RemovesAllChunks()
        {
            // Arrange - Create document with multiple chunks
            var documentId = "deletable_doc";
            var largeContent = CreateLargeDocument(1200);
            
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { largeContent },
                new List<string> { documentId });

            // Verify chunks exist
            var beforeDeletion = await _chromaService.GetDocumentsAsync(_collectionName);
            var beforeDict = (Dictionary<string, object>)beforeDeletion;
            var beforeIds = (List<object>)beforeDict["ids"];
            
            TestContext.WriteLine($"Before deletion: {beforeIds.Count} chunks exist");
            Assert.That(beforeIds.Count, Is.GreaterThan(1), "Should have multiple chunks before deletion");

            // Act - Delete document by base ID (should trigger chunk expansion)
            var deleteResult = await _chromaService.DeleteDocumentsAsync(_collectionName,
                new List<string> { documentId }, 
                expandChunks: true);

            // Assert - Deletion succeeds
            Assert.That(deleteResult, Is.True, "Deletion should succeed");

            // Verify all chunks are removed
            var afterDeletion = await _chromaService.GetDocumentsAsync(_collectionName);
            var afterDict = (Dictionary<string, object>)afterDeletion;
            var afterIds = (List<object>)afterDict["ids"];

            TestContext.WriteLine($"After deletion: {afterIds.Count} chunks remain");
            Assert.That(afterIds.Count, Is.EqualTo(0), "All chunks should be removed");

            TestContext.WriteLine("✅ Document deletion by base ID successfully removed all chunks");
        }

        /// <summary>
        /// Tests that updating a document by base ID affects all chunks appropriately.
        /// Note: This tests metadata updates as full content rechunking is complex.
        /// </summary>
        [Test]
        public async Task UpdateDocumentMetadataByBaseId_UpdatesAllChunks()
        {
            // Arrange - Create document with multiple chunks
            var documentId = "updatable_doc";
            var largeContent = CreateLargeDocument(1200);
            
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { largeContent },
                new List<string> { documentId });

            // Act - Update metadata by base ID
            var newMetadata = new Dictionary<string, object>
            {
                ["author"] = "Test Author",
                ["updated"] = "true",
                ["timestamp"] = DateTime.UtcNow.ToString()
            };

            var updateResult = await _chromaService.UpdateDocumentsAsync(_collectionName,
                new List<string> { documentId },
                documents: null,
                metadatas: new List<Dictionary<string, object>> { newMetadata },
                markAsLocalChange: true,
                expandChunks: true);

            // Assert - Update succeeds
            Assert.That(updateResult, Is.True, "Update should succeed");

            // Verify all chunks have updated metadata
            var updatedDocs = await _chromaService.GetDocumentsAsync(_collectionName);
            var updatedDict = (Dictionary<string, object>)updatedDocs;
            var updatedMetadatas = (List<object>)updatedDict["metadatas"];

            TestContext.WriteLine($"Verified {updatedMetadatas.Count} chunks have updated metadata");

            foreach (var metadataObj in updatedMetadatas)
            {
                var metadata = (Dictionary<string, object>)metadataObj;
                
                Assert.That(metadata.ContainsKey("author"), Is.True, 
                    "Each chunk should have updated author metadata");
                Assert.That(metadata["author"].ToString(), Is.EqualTo("Test Author"),
                    "Author metadata should be updated");
                Assert.That(metadata.ContainsKey("updated"), Is.True,
                    "Each chunk should have updated flag");
            }

            TestContext.WriteLine("✅ Document metadata update by base ID successfully updated all chunks");
        }

        /// <summary>
        /// Tests operations with a mix of base IDs and chunk IDs to ensure backward compatibility.
        /// </summary>
        [Test]
        public async Task MixedIdOperations_WorkCorrectly()
        {
            // Arrange - Create multiple documents
            var doc1Id = "mixed_doc_1";
            var doc2Id = "mixed_doc_2";
            var largeContent1 = CreateLargeDocument(800);  // 2 chunks
            var smallContent2 = CreateLargeDocument(300);  // 1 chunk

            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { largeContent1, smallContent2 },
                new List<string> { doc1Id, doc2Id });

            // Get all document IDs to understand the chunk structure
            var allDocs = await _chromaService.GetDocumentsAsync(_collectionName);
            var allDict = (Dictionary<string, object>)allDocs;
            var allIds = ((List<object>)allDict["ids"]).Select(id => id.ToString()).ToList();

            TestContext.WriteLine($"Created documents with IDs: {string.Join(", ", allIds)}");

            // Act - Delete using mixed ID types
            var chunkIdToDelete = allIds.FirstOrDefault(id => id.Contains("_chunk_0"));
            var deleteIds = new List<string> { doc1Id, chunkIdToDelete }; // Base ID + specific chunk ID

            var deleteResult = await _chromaService.DeleteDocumentsAsync(_collectionName,
                deleteIds,
                expandChunks: true);

            // Assert - Deletion succeeds
            Assert.That(deleteResult, Is.True, "Mixed ID deletion should succeed");

            // Verify expected documents remain
            var remainingDocs = await _chromaService.GetDocumentsAsync(_collectionName);
            var remainingDict = (Dictionary<string, object>)remainingDocs;
            var remainingIds = ((List<object>)remainingDict["ids"]).Select(id => id.ToString()).ToList();

            TestContext.WriteLine($"Remaining documents: {string.Join(", ", remainingIds)}");

            // Should only have chunks from doc2 (since doc1 was deleted by base ID)
            var doc2Chunks = remainingIds.Where(id => id.StartsWith(doc2Id)).ToList();
            Assert.That(remainingIds.Count, Is.EqualTo(doc2Chunks.Count),
                "Only doc2 chunks should remain");

            TestContext.WriteLine("✅ Mixed ID operations work correctly with chunk expansion");
        }

        /// <summary>
        /// Tests the DocumentIdResolver functionality directly.
        /// </summary>
        [Test]
        public async Task DocumentIdResolver_ExpandsIdsCorrectly()
        {
            // Arrange - Create document with multiple chunks
            var documentId = "resolver_test_doc";
            var largeContent = CreateLargeDocument(1000);
            
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { largeContent },
                new List<string> { documentId });

            // Act - Test ID resolver methods
            var isChunkId = _idResolver.IsChunkId(documentId);
            var isChunkIdForChunk = _idResolver.IsChunkId($"{documentId}_chunk_0");
            
            var baseId = _idResolver.ExtractBaseDocumentId($"{documentId}_chunk_1");
            var expandedIds = await _idResolver.ExpandToChunkIdsAsync(_collectionName, documentId);

            // Assert - Resolver works correctly
            Assert.That(isChunkId, Is.False, "Base ID should not be identified as chunk ID");
            Assert.That(isChunkIdForChunk, Is.True, "Chunk ID should be identified correctly");
            Assert.That(baseId, Is.EqualTo(documentId), "Base ID extraction should work");
            Assert.That(expandedIds.Count, Is.GreaterThan(1), "Base ID should expand to multiple chunks");

            TestContext.WriteLine($"Base ID '{documentId}' expanded to: {string.Join(", ", expandedIds)}");

            // Verify expanded IDs follow pattern
            for (int i = 0; i < expandedIds.Count; i++)
            {
                var expectedId = $"{documentId}_chunk_{i}";
                Assert.That(expandedIds[i], Is.EqualTo(expectedId),
                    $"Expanded ID {i} should follow pattern");
            }

            TestContext.WriteLine("✅ DocumentIdResolver correctly expands base IDs to chunk IDs");
        }

        /// <summary>
        /// Tests that chunk expansion is disabled when expandChunks=false.
        /// </summary>
        [Test]
        public async Task ChunkExpansionDisabled_WorksWithExactIds()
        {
            // Arrange - Create document with multiple chunks
            var documentId = "exact_match_doc";
            var largeContent = CreateLargeDocument(800);
            
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { largeContent },
                new List<string> { documentId });

            // Get actual chunk IDs
            var allDocs = await _chromaService.GetDocumentsAsync(_collectionName);
            var allDict = (Dictionary<string, object>)allDocs;
            var allIds = ((List<object>)allDict["ids"]).Select(id => id.ToString()).ToList();
            var firstChunkId = allIds.First();

            TestContext.WriteLine($"Attempting to delete specific chunk: {firstChunkId}");

            // Act - Try to delete base ID with expansion disabled (should fail/do nothing)
            var deleteBaseResult = await _chromaService.DeleteDocumentsAsync(_collectionName,
                new List<string> { documentId },
                expandChunks: false);

            // Get documents after attempted base ID deletion
            var afterBaseDelete = await _chromaService.GetDocumentsAsync(_collectionName);
            var afterBaseDict = (Dictionary<string, object>)afterBaseDelete;
            var afterBaseIds = ((List<object>)afterBaseDict["ids"]).Select(id => id.ToString()).ToList();

            // Should still have all chunks (base ID deletion without expansion should not work)
            Assert.That(afterBaseIds.Count, Is.EqualTo(allIds.Count),
                "Disabling chunk expansion should prevent base ID deletion");

            // Act - Delete specific chunk with expansion disabled (should work)
            var deleteChunkResult = await _chromaService.DeleteDocumentsAsync(_collectionName,
                new List<string> { firstChunkId },
                expandChunks: false);

            // Assert
            Assert.That(deleteChunkResult, Is.True, "Specific chunk deletion should succeed");

            var afterChunkDelete = await _chromaService.GetDocumentsAsync(_collectionName);
            var afterChunkDict = (Dictionary<string, object>)afterChunkDelete;
            var afterChunkIds = ((List<object>)afterChunkDict["ids"]).Select(id => id.ToString()).ToList();

            // Should have one less chunk
            Assert.That(afterChunkIds.Count, Is.EqualTo(allIds.Count - 1),
                "Specific chunk deletion should remove only that chunk");
            Assert.That(afterChunkIds.Contains(firstChunkId), Is.False,
                "Deleted chunk should not be present");

            TestContext.WriteLine("✅ Chunk expansion can be disabled for exact ID matching");
        }

        /// <summary>
        /// Tests error handling when attempting operations on non-existent documents.
        /// </summary>
        [Test]
        public async Task OperationsOnNonExistentDocument_HandleGracefully()
        {
            // Act & Assert - Delete non-existent document should succeed (no-op)
            var deleteResult = await _chromaService.DeleteDocumentsAsync(_collectionName,
                new List<string> { "non_existent_doc" },
                expandChunks: true);

            Assert.That(deleteResult, Is.True, "Deleting non-existent document should not fail");

            // Test ID resolver with non-existent document
            var expandedIds = await _idResolver.ExpandToChunkIdsAsync(_collectionName, "non_existent_doc");
            Assert.That(expandedIds.Count, Is.EqualTo(0), 
                "Expanding non-existent document should return empty list");

            TestContext.WriteLine("✅ Operations on non-existent documents handle gracefully");
        }
    }
}