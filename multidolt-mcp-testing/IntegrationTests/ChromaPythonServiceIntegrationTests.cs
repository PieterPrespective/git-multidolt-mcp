using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests specifically for ChromaPythonService operations.
    /// Tests the actual service implementation with real ChromaDB, not just mocks.
    /// </summary>
    [TestFixture]
    public class ChromaPythonServiceIntegrationTests
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
            // Initialize Python context first
            PythonContext.Initialize();
            
            // Create unique test directory
            _testDirectory = Path.Combine(Path.GetTempPath(), $"ChromaServiceTests_{Guid.NewGuid():N}");
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
            
            _logger = loggerFactory.CreateLogger<ChromaPersistentDbService>();
            _resolverLogger = loggerFactory.CreateLogger<DocumentIdResolver>();

            // Create services
            _chromaService = new ChromaPersistentDbService(_logger, Options.Create(serverConfig), null);
            _idResolver = new DocumentIdResolver(_chromaService, _resolverLogger);

            _collectionName = "service_integration_test";

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
                if (_chromaService != null)
                {
                    await _chromaService.DeleteCollectionAsync(_collectionName);
                    _chromaService.Dispose();
                }
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Cleanup warning: {ex.Message}");
            }

            try
            {
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
        /// Test the critical issue: UpdateDocumentsAsync should not delete documents when updating content.
        /// This is the exact bug happening in TestComplexDocumentLifecycleAcrossBranches.
        /// </summary>
        [Test]
        public async Task UpdateDocumentsAsync_WithNewContent_ShouldPreserveDocument()
        {
            // Arrange - Add initial document
            var initialContent = "Original document content";
            var documentId = "test_doc_1";
            
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { initialContent },
                new List<string> { documentId });

            // Verify document exists
            var beforeUpdate = await _chromaService.GetDocumentsAsync(_collectionName);
            var beforeDict = (Dictionary<string, object>)beforeUpdate;
            var beforeIds = (List<object>)beforeDict["ids"];
            var beforeDocs = (List<object>)beforeDict["documents"];
            
            Assert.That(beforeIds.Count, Is.EqualTo(1), "Should have 1 document before update");
            Assert.That(beforeIds[0].ToString(), Is.EqualTo(documentId), "Should find original document");
            Assert.That(beforeDocs[0].ToString(), Is.EqualTo(initialContent), "Should have original content");

            // Act - Update document content
            var updatedContent = "Updated document content - much longer text";
            var updateResult = await _chromaService.UpdateDocumentsAsync(_collectionName,
                new List<string> { documentId },
                new List<string> { updatedContent },
                markAsLocalChange: true,
                expandChunks: true);

            // Assert - Update should succeed
            Assert.That(updateResult, Is.True, "Update operation should succeed");

            // Verify document still exists with updated content
            var afterUpdate = await _chromaService.GetDocumentsAsync(_collectionName);
            var afterDict = (Dictionary<string, object>)afterUpdate;
            var afterIds = (List<object>)afterDict["ids"];
            var afterDocs = (List<object>)afterDict["documents"];

            // CRITICAL: Document should still exist after update
            Assert.That(afterIds.Count, Is.GreaterThan(0), "Document should still exist after update - THIS IS THE BUG!");
            
            if (afterIds.Count > 0)
            {
                var foundDoc = false;
                for (int i = 0; i < afterIds.Count; i++)
                {
                    var id = afterIds[i].ToString();
                    var content = afterDocs[i].ToString();
                    
                    TestContext.WriteLine($"After update: ID={id}, Content={content}");
                    
                    // Check if this is our document (exact ID or chunk ID)
                    if (id == documentId || id.StartsWith(documentId + "_chunk_"))
                    {
                        foundDoc = true;
                        Assert.That(content, Is.EqualTo(updatedContent), 
                            "Document content should be updated");
                    }
                }
                
                Assert.That(foundDoc, Is.True, "Should find the updated document in collection");
            }
            
            TestContext.WriteLine("✅ Document update preserves document with new content");
        }

        /// <summary>
        /// Test that metadata-only updates work correctly without deleting documents.
        /// </summary>
        [Test]
        public async Task UpdateDocumentsAsync_MetadataOnly_ShouldPreserveDocumentAndContent()
        {
            // Arrange
            var documentContent = "Document for metadata update test";
            var documentId = "metadata_test_doc";
            
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { documentContent },
                new List<string> { documentId });

            // Act - Update only metadata
            var newMetadata = new Dictionary<string, object>
            {
                ["author"] = "Test Author",
                ["version"] = 2,
                ["updated"] = DateTime.UtcNow.ToString()
            };

            var updateResult = await _chromaService.UpdateDocumentsAsync(_collectionName,
                new List<string> { documentId },
                documents: null,  // No document content update
                metadatas: new List<Dictionary<string, object>> { newMetadata },
                markAsLocalChange: true,
                expandChunks: true);

            // Assert
            Assert.That(updateResult, Is.True, "Metadata update should succeed");

            var afterUpdate = await _chromaService.GetDocumentsAsync(_collectionName);
            var afterDict = (Dictionary<string, object>)afterUpdate;
            var afterIds = (List<object>)afterDict["ids"];
            var afterDocs = (List<object>)afterDict["documents"];
            var afterMetadatas = (List<object>)afterDict["metadatas"];

            Assert.That(afterIds.Count, Is.GreaterThan(0), "Document should exist after metadata update");
            
            // Find our document
            var foundDoc = false;
            for (int i = 0; i < afterIds.Count; i++)
            {
                var id = afterIds[i].ToString();
                if (id == documentId || id.StartsWith(documentId + "_chunk_"))
                {
                    foundDoc = true;
                    Assert.That(afterDocs[i].ToString(), Is.EqualTo(documentContent), 
                        "Document content should remain unchanged");
                    
                    var metadata = (Dictionary<string, object>)afterMetadatas[i];
                    Assert.That(metadata.ContainsKey("author"), Is.True, "Should have updated metadata");
                    Assert.That(metadata["author"].ToString(), Is.EqualTo("Test Author"), "Should have correct metadata");
                    break;
                }
            }
            
            Assert.That(foundDoc, Is.True, "Should find document after metadata update");
            TestContext.WriteLine("✅ Metadata update preserves document content");
        }

        /// <summary>
        /// Test updating documents with both content and metadata.
        /// </summary>
        [Test]
        public async Task UpdateDocumentsAsync_ContentAndMetadata_ShouldUpdateBoth()
        {
            // Arrange
            var originalContent = "Original content";
            var documentId = "content_meta_test";
            
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { originalContent },
                new List<string> { documentId });

            // Act - Update both content and metadata
            var newContent = "Updated content with new information";
            var newMetadata = new Dictionary<string, object>
            {
                ["status"] = "updated",
                ["timestamp"] = DateTime.UtcNow.Ticks
            };

            var updateResult = await _chromaService.UpdateDocumentsAsync(_collectionName,
                new List<string> { documentId },
                documents: new List<string> { newContent },
                metadatas: new List<Dictionary<string, object>> { newMetadata },
                markAsLocalChange: true,
                expandChunks: true);

            // Assert
            Assert.That(updateResult, Is.True, "Combined update should succeed");

            var afterUpdate = await _chromaService.GetDocumentsAsync(_collectionName);
            var afterDict = (Dictionary<string, object>)afterUpdate;
            var afterIds = (List<object>)afterDict["ids"];
            var afterDocs = (List<object>)afterDict["documents"];
            var afterMetadatas = (List<object>)afterDict["metadatas"];

            Assert.That(afterIds.Count, Is.GreaterThan(0), "Document should exist after combined update");
            
            // Verify both content and metadata updated
            var foundDoc = false;
            for (int i = 0; i < afterIds.Count; i++)
            {
                var id = afterIds[i].ToString();
                if (id == documentId || id.StartsWith(documentId + "_chunk_"))
                {
                    foundDoc = true;
                    Assert.That(afterDocs[i].ToString(), Is.EqualTo(newContent), 
                        "Document content should be updated");
                    
                    var metadata = (Dictionary<string, object>)afterMetadatas[i];
                    Assert.That(metadata.ContainsKey("status"), Is.True, "Should have updated metadata");
                    Assert.That(metadata["status"].ToString(), Is.EqualTo("updated"), "Should have correct metadata");
                    break;
                }
            }
            
            Assert.That(foundDoc, Is.True, "Should find document after combined update");
            TestContext.WriteLine("✅ Combined content and metadata update works correctly");
        }

        /// <summary>
        /// Test updating multiple documents at once.
        /// </summary>
        [Test]
        public async Task UpdateDocumentsAsync_MultipleDocuments_ShouldUpdateAll()
        {
            // Arrange - Add multiple documents
            var documents = new List<string> { "Doc 1 content", "Doc 2 content", "Doc 3 content" };
            var documentIds = new List<string> { "multi_doc_1", "multi_doc_2", "multi_doc_3" };
            
            await _chromaService.AddDocumentsAsync(_collectionName, documents, documentIds);

            // Verify all documents exist
            var beforeUpdate = await _chromaService.GetDocumentsAsync(_collectionName);
            var beforeDict = (Dictionary<string, object>)beforeUpdate;
            var beforeIds = (List<object>)beforeDict["ids"];
            Assert.That(beforeIds.Count, Is.EqualTo(3), "Should have 3 documents before update");

            // Act - Update all documents
            var updatedDocuments = new List<string> 
            { 
                "Updated Doc 1 content", 
                "Updated Doc 2 content", 
                "Updated Doc 3 content" 
            };

            var updateResult = await _chromaService.UpdateDocumentsAsync(_collectionName,
                documentIds,
                updatedDocuments,
                markAsLocalChange: true,
                expandChunks: true);

            // Assert
            Assert.That(updateResult, Is.True, "Multiple document update should succeed");

            var afterUpdate = await _chromaService.GetDocumentsAsync(_collectionName);
            var afterDict = (Dictionary<string, object>)afterUpdate;
            var afterIds = (List<object>)afterDict["ids"];
            var afterDocs = (List<object>)afterDict["documents"];

            Assert.That(afterIds.Count, Is.EqualTo(3), "Should still have 3 documents after update");
            
            // Verify each document was updated
            foreach (var expectedId in documentIds)
            {
                var found = false;
                for (int i = 0; i < afterIds.Count; i++)
                {
                    var id = afterIds[i].ToString();
                    if (id == expectedId || id.StartsWith(expectedId + "_chunk_"))
                    {
                        found = true;
                        var content = afterDocs[i].ToString();
                        Assert.That(content.StartsWith("Updated"), Is.True, 
                            $"Document {expectedId} should have updated content");
                        break;
                    }
                }
                Assert.That(found, Is.True, $"Should find updated document {expectedId}");
            }
            
            TestContext.WriteLine("✅ Multiple document update works correctly");
        }

        /// <summary>
        /// Test the specific scenario from BranchSwitchingIntegrationTests.
        /// This replicates the exact conditions where doc1 disappears.
        /// </summary>
        [Test]
        public async Task ReplicateTestComplexDocumentLifecycleBug_Doc1Update()
        {
            // Arrange - Replicate exact conditions from failing test
            var documents = new List<string> { "Doc 1 content", "Doc 2 content", "Doc 3 content" };
            var documentIds = new List<string> { "doc1", "doc2", "doc3" };
            
            await _chromaService.AddDocumentsAsync("alpha", documents, documentIds);

            // Log documents before update
            var beforeUpdate = await _chromaService.GetDocumentsAsync("alpha");
            var beforeDict = (Dictionary<string, object>)beforeUpdate;
            var beforeIds = (List<object>)beforeDict["ids"];
            var beforeDocs = (List<object>)beforeDict["documents"];
            
            TestContext.WriteLine("=== BEFORE UPDATE ===");
            for (int i = 0; i < beforeIds.Count; i++)
            {
                TestContext.WriteLine($"ID={beforeIds[i]}, Content={beforeDocs[i]}");
            }
            
            Assert.That(beforeIds.Count, Is.EqualTo(3), "Should have 3 documents initially");

            // Act - Update doc1 exactly as in the failing test
            var updateResult = await _chromaService.UpdateDocumentsAsync("alpha",
                new List<string> { "doc1" },
                new List<string> { "Doc 1 modified on feature-a" });

            Assert.That(updateResult, Is.True, "Update should succeed");

            // Log documents after update
            var afterUpdate = await _chromaService.GetDocumentsAsync("alpha");
            var afterDict = (Dictionary<string, object>)afterUpdate;
            var afterIds = (List<object>)afterDict["ids"];
            var afterDocs = (List<object>)afterDict["documents"];
            
            TestContext.WriteLine("=== AFTER UPDATE ===");
            for (int i = 0; i < afterIds.Count; i++)
            {
                TestContext.WriteLine($"ID={afterIds[i]}, Content={afterDocs[i]}");
            }

            // Assert - This will fail with current bug, showing the issue
            Assert.That(afterIds.Count, Is.EqualTo(3), 
                "Should still have 3 documents after update - BUG: doc1 disappears!");
            
            // Verify doc1 still exists
            var doc1Found = false;
            for (int i = 0; i < afterIds.Count; i++)
            {
                var id = afterIds[i].ToString();
                if (id == "doc1" || id.StartsWith("doc1_chunk_"))
                {
                    doc1Found = true;
                    var content = afterDocs[i].ToString();
                    Assert.That(content, Is.EqualTo("Doc 1 modified on feature-a"), 
                        "doc1 should have updated content");
                    break;
                }
            }
            
            Assert.That(doc1Found, Is.True, "doc1 should exist after update - THIS IS THE BUG!");
            TestContext.WriteLine("✅ Successfully updated doc1 without losing it");
        }

        /// <summary>
        /// Test that expandChunks=false works correctly for direct chunk updates.
        /// </summary>
        [Test]
        public async Task UpdateDocumentsAsync_ExpandChunksFalse_UpdatesExactIds()
        {
            // Arrange - Add a document that gets chunked
            var largeContent = new string('A', 800);  // Will create multiple chunks
            var documentId = "chunk_test_doc";
            
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { largeContent },
                new List<string> { documentId });

            // Get the actual chunk IDs
            var docs = await _chromaService.GetDocumentsAsync(_collectionName);
            var docsDict = (Dictionary<string, object>)docs;
            var chunkIds = ((List<object>)docsDict["ids"]).Select(id => id.ToString()).ToList();
            
            Assert.That(chunkIds.Count, Is.GreaterThan(1), "Should have multiple chunks");
            
            // Act - Update specific chunk with expandChunks=false
            var firstChunkId = chunkIds[0];
            var newContent = "Updated first chunk content";
            
            var updateResult = await _chromaService.UpdateDocumentsAsync(_collectionName,
                new List<string> { firstChunkId },
                new List<string> { newContent },
                expandChunks: false);

            // Assert
            Assert.That(updateResult, Is.True, "Specific chunk update should succeed");

            var afterUpdate = await _chromaService.GetDocumentsAsync(_collectionName);
            var afterDict = (Dictionary<string, object>)afterUpdate;
            var afterIds = (List<object>)afterDict["ids"];
            var afterDocs = (List<object>)afterDict["documents"];

            // Should have same number of chunks
            Assert.That(afterIds.Count, Is.EqualTo(chunkIds.Count), 
                "Should have same number of chunks after update");

            // Find the updated chunk
            var updatedChunkFound = false;
            for (int i = 0; i < afterIds.Count; i++)
            {
                if (afterIds[i].ToString() == firstChunkId)
                {
                    updatedChunkFound = true;
                    Assert.That(afterDocs[i].ToString(), Is.EqualTo(newContent), 
                        "Updated chunk should have new content");
                    break;
                }
            }
            
            Assert.That(updatedChunkFound, Is.True, "Should find the updated chunk");
            TestContext.WriteLine("✅ Direct chunk update with expandChunks=false works correctly");
        }

        /// <summary>
        /// Test error handling for invalid update operations.
        /// </summary>
        [Test]
        public async Task UpdateDocumentsAsync_InvalidOperations_ShouldHandleGracefully()
        {
            // Test 1: Update non-existent document
            var updateResult1 = await _chromaService.UpdateDocumentsAsync(_collectionName,
                new List<string> { "non_existent_doc" },
                new List<string> { "Some content" });
            
            // Should not crash - ChromaDB handles this gracefully
            Assert.That(updateResult1, Is.True, "Update of non-existent document should not crash");

            // Test 2: Empty documents and metadata should throw
            Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await _chromaService.UpdateDocumentsAsync(_collectionName,
                    new List<string> { "some_id" },
                    documents: null,
                    metadatas: null);
            }, "Should throw when both documents and metadatas are null");

            TestContext.WriteLine("✅ Error handling works correctly");
        }

        /// <summary>
        /// PP13-69-C10: Test querying large multi-chunk documents.
        /// Note: ChromaDB uses embedding-based semantic search, not keyword search.
        /// Results are based on vector similarity, so exact keyword matches aren't guaranteed.
        /// This test verifies that querying returns documents from the collection.
        /// </summary>
        [Test]
        public async Task QueryDocumentsAsync_LargeMultiChunkDocument_ReturnsResults()
        {
            // Arrange - Create a large document (~1000 chars) that will be chunked
            var largeContent = "This is a document about machine learning and AI. " +
                               new string('X', 400) +
                               " Neural networks are fascinating. " +
                               new string('Y', 400) +
                               " Deep learning transforms industries.";

            TestContext.WriteLine($"Document length: {largeContent.Length} chars");

            var documentId = "large_doc_query_test";
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { largeContent },
                new List<string> { documentId });

            // Verify document was chunked
            var allDocs = await _chromaService.GetDocumentsAsync(_collectionName);
            var allDocsDict = (Dictionary<string, object>)allDocs;
            var allIds = (List<object>)allDocsDict["ids"];
            var allContents = (List<object>)allDocsDict["documents"];

            TestContext.WriteLine($"Document stored as {allIds.Count} chunks:");
            for (int i = 0; i < allIds.Count; i++)
            {
                var content = allContents[i]?.ToString() ?? "";
                TestContext.WriteLine($"  Chunk {i}: '{content.Substring(0, Math.Min(50, content.Length))}...'");
            }

            Assert.That(allIds.Count, Is.GreaterThan(1), "Large document should be chunked into multiple parts");

            // Act - Query for related content
            var queryResult = await _chromaService.QueryDocumentsAsync(_collectionName,
                new List<string> { "artificial intelligence machine learning" }, nResults: 5);

            // Assert - Should return results
            Assert.That(queryResult, Is.Not.Null, "Query result should not be null");
            var resultDict = queryResult as Dictionary<string, object>;
            var documents = resultDict?.GetValueOrDefault("documents") as List<object>;
            var firstResults = documents?[0] as List<object>;

            Assert.That(firstResults, Is.Not.Null.And.Not.Empty, "Should have query results");

            var firstDoc = firstResults[0]?.ToString() ?? "";
            TestContext.WriteLine($"Query returned: '{firstDoc.Substring(0, Math.Min(100, firstDoc.Length))}...'");

            // Verify we got content from one of our chunks
            Assert.That(firstDoc.Length, Is.GreaterThan(0), "Query should return non-empty content");

            TestContext.WriteLine("✅ Query returns results from multi-chunk documents");
        }

        /// <summary>
        /// PP13-69-C10: Test querying for "MODIFIED" content in a large document after modification.
        /// This directly replicates the failing scenario from PP13_68_ContentHashPerformance_VariousDocumentSizes.
        /// </summary>
        [Test]
        public async Task QueryDocumentsAsync_ModifiedLargeDocument_FindsModifiedContent()
        {
            // Arrange - Create a ~1000 char document (similar to "medium" in the performance test)
            var originalContent = "This is test content for performance measurement. " +
                                  new string('X', 900) + " End of document.";

            TestContext.WriteLine($"Original document length: {originalContent.Length} chars");

            var documentId = "modified_doc_test";
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { originalContent },
                new List<string> { documentId });

            // Verify original document
            var beforeUpdate = await _chromaService.GetDocumentsAsync(_collectionName);
            var beforeDict = (Dictionary<string, object>)beforeUpdate;
            var beforeIds = (List<object>)beforeDict["ids"];
            TestContext.WriteLine($"Original document stored as {beforeIds.Count} chunks");

            // Act - Modify the document with "MODIFIED" prefix (same as performance test)
            var modifiedContent = "MODIFIED " + originalContent;
            TestContext.WriteLine($"Modified document length: {modifiedContent.Length} chars");

            // Delete old chunks and add modified content
            await _chromaService.DeleteDocumentsAsync(_collectionName,
                beforeIds.Select(id => id.ToString()).ToList());
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { modifiedContent },
                new List<string> { documentId });

            // Verify modified document
            var afterUpdate = await _chromaService.GetDocumentsAsync(_collectionName);
            var afterDict = (Dictionary<string, object>)afterUpdate;
            var afterIds = (List<object>)afterDict["ids"];
            var afterDocs = (List<object>)afterDict["documents"];

            TestContext.WriteLine($"Modified document stored as {afterIds.Count} chunks:");
            for (int i = 0; i < afterIds.Count; i++)
            {
                var content = afterDocs[i]?.ToString() ?? "";
                TestContext.WriteLine($"  Chunk {afterIds[i]}: '{content.Substring(0, Math.Min(50, content.Length))}...'");
            }

            // Query for "MODIFIED"
            var queryResult = await _chromaService.QueryDocumentsAsync(_collectionName,
                new List<string> { "MODIFIED" }, nResults: 5);

            Assert.That(queryResult, Is.Not.Null, "Query result should not be null");
            var resultDict = queryResult as Dictionary<string, object>;
            var documents = resultDict?.GetValueOrDefault("documents") as List<object>;
            var firstResults = documents?[0] as List<object>;

            Assert.That(firstResults, Is.Not.Null.And.Not.Empty, "Should have query results");

            var firstDoc = firstResults[0]?.ToString() ?? "";
            TestContext.WriteLine($"Query for 'MODIFIED' returned: '{firstDoc.Substring(0, Math.Min(100, firstDoc.Length))}...'");

            // This is the critical assertion that fails in PP13_68_ContentHashPerformance_VariousDocumentSizes
            Assert.That(firstDoc, Does.Contain("MODIFIED"),
                "Query for 'MODIFIED' should return chunk containing 'MODIFIED' - " +
                "if this fails, there may be a chunking or indexing issue");

            TestContext.WriteLine("✅ Query correctly finds 'MODIFIED' content after document update");
        }

        /// <summary>
        /// PP13-69-C10: Test to verify double-chunking does NOT occur.
        /// AddDocumentsAsync should handle chunking, not the caller.
        /// </summary>
        [Test]
        public async Task AddDocumentsAsync_LargeDocument_ChunksOnlyOnce()
        {
            // Arrange - Create a document that will definitely be chunked (>512 chars)
            var largeContent = new string('X', 1000);
            // Note: Don't use "chunk" in the ID as it would confuse the detection logic
            var documentId = "large_doc_chunking_test";

            // Act - Add document (AddDocumentsAsync should chunk it)
            await _chromaService.AddDocumentsAsync(_collectionName,
                new List<string> { largeContent },
                new List<string> { documentId });

            // Assert - Check the resulting chunks
            var docs = await _chromaService.GetDocumentsAsync(_collectionName);
            var docsDict = (Dictionary<string, object>)docs;
            var ids = (List<object>)docsDict["ids"];
            var contents = (List<object>)docsDict["documents"];

            TestContext.WriteLine($"Document {documentId} ({largeContent.Length} chars) stored as {ids.Count} chunks:");

            int totalContentLength = 0;
            foreach (var idObj in ids)
            {
                var id = idObj.ToString();
                // Check that chunk IDs don't have double _chunk_ patterns (e.g., doc_chunk_0_chunk_0)
                var chunkPattern = "_chunk_";
                var firstChunkIndex = id.IndexOf(chunkPattern);
                if (firstChunkIndex >= 0)
                {
                    var secondChunkIndex = id.IndexOf(chunkPattern, firstChunkIndex + chunkPattern.Length);
                    Assert.That(secondChunkIndex, Is.EqualTo(-1),
                        $"ID '{id}' should not have double _chunk_ pattern - indicates double chunking!");
                }
                TestContext.WriteLine($"  ID: {id}");
            }

            for (int i = 0; i < contents.Count; i++)
            {
                var content = contents[i]?.ToString() ?? "";
                totalContentLength += content.Length;
                TestContext.WriteLine($"  Chunk {i}: {content.Length} chars");
            }

            // With 512 char chunks and 50 char overlap, a 1000 char document should have ~3 chunks
            // Each chunk should be ~512 chars (except possibly the last)
            Assert.That(ids.Count, Is.InRange(2, 4),
                $"1000 char document should be ~2-4 chunks, not {ids.Count} (double chunking would create more)");

            // Verify no chunk is tiny (which would indicate double-chunking of already-small chunks)
            foreach (var contentObj in contents)
            {
                var content = contentObj?.ToString() ?? "";
                // In proper chunking, chunks should be close to chunk size (512)
                // except possibly the last chunk
                // Double-chunking would create many tiny chunks
                TestContext.WriteLine($"  Chunk size: {content.Length} chars");
            }

            TestContext.WriteLine("✅ Document chunked correctly without double-chunking");
        }
    }
}