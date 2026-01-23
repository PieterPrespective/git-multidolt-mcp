using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for DeltaDetector service with both Dolt and ChromaDB.
    /// Tests change detection, document conversion, and synchronization scenarios.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class DeltaDetectorIntegrationTests
    {
        private DoltCli? _doltCli;
        private DeltaDetector? _deltaDetector;
        private IChromaDbService? _chromaService;
        private string _testRepoPath = null!;
        private string _chromaDbPath = null!;
        private readonly string _testCollectionName = "test_delta_collection";
        private ILogger<DeltaDetector>? _logger;

        [OneTimeSetUp]
        public async Task OneTimeSetup()
        {
            // Create test directories
            var testDir = Path.Combine(Path.GetTempPath(), $"delta_test_{Guid.NewGuid():N}");
            _testRepoPath = Path.Combine(testDir, "dolt_repo");
            _chromaDbPath = Path.Combine(testDir, "chroma_db");
            
            Directory.CreateDirectory(_testRepoPath);
            Directory.CreateDirectory(_chromaDbPath);

            // Initialize Dolt CLI
            var doltConfig = Options.Create(new DoltConfiguration
            {
                DoltExecutablePath = "dolt",
                RepositoryPath = _testRepoPath,
                CommandTimeoutMs = 30000,
                EnableDebugLogging = true
            });

            var doltLogger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<DoltCli>();
            _doltCli = new DoltCli(doltConfig, doltLogger);

            // Initialize Dolt repository
            var initResult = await _doltCli.InitAsync();
            Assert.That(initResult.Success, Is.True, $"Failed to init Dolt repo: {initResult.Error}");

            // Create schema for testing
            await CreateTestSchema();

            // Initialize DeltaDetector
            _logger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<DeltaDetector>();
            _deltaDetector = new DeltaDetector(_doltCli, _logger);

            // Initialize ChromaDB service
            var chromaConfig = Options.Create(new ServerConfiguration
            {
                ChromaDataPath = _chromaDbPath,
                ChromaMode = "persistent"
            });
            var chromaLogger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<ChromaPersistentDbService>();
            _chromaService = new ChromaPersistentDbService(chromaLogger, chromaConfig);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            // Dispose ChromaDB service
            if (_chromaService is IDisposable disposable)
            {
                disposable.Dispose();
            }
            
            // Clean up test directories
            try
            {
                if (Directory.Exists(_testRepoPath))
                {
                    Directory.Delete(Path.GetDirectoryName(_testRepoPath)!, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        private async Task CreateTestSchema()
        {
            // Create issue_logs table
            var createIssueLogsTable = @"
                CREATE TABLE issue_logs (
                    log_id VARCHAR(36) PRIMARY KEY,
                    project_id VARCHAR(36) NOT NULL,
                    issue_number INT NOT NULL,
                    title VARCHAR(500),
                    content LONGTEXT NOT NULL,
                    content_hash CHAR(64) NOT NULL,
                    log_type VARCHAR(50) DEFAULT 'implementation',
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    metadata JSON
                )";
            
            await _doltCli!.ExecuteAsync(createIssueLogsTable);

            // Create knowledge_docs table  
            var createKnowledgeDocsTable = @"
                CREATE TABLE knowledge_docs (
                    doc_id VARCHAR(36) PRIMARY KEY,
                    category VARCHAR(100) NOT NULL,
                    tool_name VARCHAR(255) NOT NULL,
                    tool_version VARCHAR(50),
                    title VARCHAR(500) NOT NULL,
                    content LONGTEXT NOT NULL,
                    content_hash CHAR(64) NOT NULL,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    metadata JSON
                )";
            
            await _doltCli.ExecuteAsync(createKnowledgeDocsTable);

            // Create document_sync_log table
            var createSyncLogTable = @"
                CREATE TABLE document_sync_log (
                    id INT AUTO_INCREMENT PRIMARY KEY,
                    source_table VARCHAR(50) NOT NULL,
                    source_id VARCHAR(36) NOT NULL,
                    content_hash CHAR(64) NOT NULL,
                    chroma_collection VARCHAR(255) NOT NULL,
                    chunk_ids JSON,
                    synced_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    embedding_model VARCHAR(100),
                    sync_action VARCHAR(20) NOT NULL,
                    UNIQUE KEY uk_source_collection (source_table, source_id, chroma_collection)
                )";
            
            await _doltCli.ExecuteAsync(createSyncLogTable);

            // Create chroma_sync_state table
            var createSyncStateTable = @"
                CREATE TABLE chroma_sync_state (
                    collection_name VARCHAR(255) PRIMARY KEY,
                    last_sync_commit VARCHAR(40),
                    last_sync_at DATETIME,
                    document_count INT DEFAULT 0,
                    chunk_count INT DEFAULT 0,
                    embedding_model VARCHAR(100),
                    sync_status VARCHAR(20) DEFAULT 'pending',
                    error_message TEXT,
                    metadata JSON
                )";
            
            await _doltCli.ExecuteAsync(createSyncStateTable);

            // Commit schema
            await _doltCli.AddAllAsync();
            await _doltCli.CommitAsync("Initial schema creation");
        }

        [Test]
        [Order(1)]
        public async Task GetPendingSyncDocuments_WithNewDocuments_ReturnsAllAsNew()
        {
            // Arrange - Add test documents
            var content1 = "This is a test issue log about authentication bugs that need to be fixed.";
            var hash1 = DocumentConverterUtility.CalculateContentHash(content1);
            
            var insertIssueLog = $@"
                INSERT INTO issue_logs (log_id, project_id, issue_number, title, content, content_hash, log_type)
                VALUES ('log-001', 'proj-001', 101, 'Auth Bug Fix', '{content1}', '{hash1}', 'implementation')";
            
            await _doltCli!.ExecuteAsync(insertIssueLog);

            var content2 = "Documentation about Entity Framework Core migrations and best practices.";
            var hash2 = DocumentConverterUtility.CalculateContentHash(content2);
            
            var insertKnowledgeDoc = $@"
                INSERT INTO knowledge_docs (doc_id, category, tool_name, tool_version, title, content, content_hash)
                VALUES ('doc-001', 'database', 'EntityFramework', '7.0', 'EF Core Migrations', '{content2}', '{hash2}')";
            
            await _doltCli.ExecuteAsync(insertKnowledgeDoc);

            // Act
            var pendingDocs = await _deltaDetector!.GetPendingSyncDocumentsAsync(_testCollectionName);
            
            // Assert
            Assert.That(pendingDocs, Is.Not.Null);
            var docList = pendingDocs.ToList();
            Assert.That(docList.Count, Is.EqualTo(2), "Should find 2 new documents");
            
            // Check that both are marked as new
            Assert.That(docList.All(d => d.ChangeType == "new"), Is.True, "All documents should be marked as new");
            
            // Verify document details
            var issueLog = docList.FirstOrDefault(d => d.SourceId == "log-001");
            Assert.That(issueLog, Is.Not.Null, "Should find issue log");
            Assert.That(issueLog!.SourceTable, Is.EqualTo("issue_logs"));
            Assert.That(issueLog.Content, Is.EqualTo(content1));
            Assert.That(issueLog.ContentHash, Is.EqualTo(hash1));
            
            var knowledgeDoc = docList.FirstOrDefault(d => d.SourceId == "doc-001");
            Assert.That(knowledgeDoc, Is.Not.Null, "Should find knowledge doc");
            Assert.That(knowledgeDoc!.SourceTable, Is.EqualTo("knowledge_docs"));
            Assert.That(knowledgeDoc.Content, Is.EqualTo(content2));
            Assert.That(knowledgeDoc.ContentHash, Is.EqualTo(hash2));
        }

        [Test]
        [Order(2)]
        public async Task GetPendingSyncDocuments_WithSyncedDocuments_ReturnsOnlyModified()
        {
            // Arrange - Mark one document as synced
            var syncLogInsert = $@"
                INSERT INTO document_sync_log (source_table, source_id, content_hash, chroma_collection, chunk_ids, sync_action)
                VALUES ('issue_logs', 'log-001', 
                        '{DocumentConverterUtility.CalculateContentHash("This is a test issue log about authentication bugs that need to be fixed.")}',
                        '{_testCollectionName}', '[""log-001_chunk_0""]', 'added')";
            
            await _doltCli!.ExecuteAsync(syncLogInsert);

            // Modify the content of the synced document
            var newContent = "Updated: This issue log has been modified with new information about the auth bug.";
            var newHash = DocumentConverterUtility.CalculateContentHash(newContent);
            
            var updateIssueLog = $@"
                UPDATE issue_logs 
                SET content = '{newContent}', content_hash = '{newHash}'
                WHERE log_id = 'log-001'";
            
            await _doltCli.ExecuteAsync(updateIssueLog);

            // Act
            var pendingDocs = await _deltaDetector!.GetPendingSyncDocumentsAsync(_testCollectionName);
            
            // Assert
            var docList = pendingDocs.ToList();
            Assert.That(docList.Count, Is.EqualTo(2), "Should find 2 documents needing sync");
            
            // The modified document should be marked as modified
            var modifiedDoc = docList.FirstOrDefault(d => d.SourceId == "log-001");
            Assert.That(modifiedDoc, Is.Not.Null);
            Assert.That(modifiedDoc!.ChangeType, Is.EqualTo("modified"), "Updated document should be marked as modified");
            Assert.That(modifiedDoc.Content, Is.EqualTo(newContent));
            
            // The unsynced document should still be marked as new
            var newDoc = docList.FirstOrDefault(d => d.SourceId == "doc-001");
            Assert.That(newDoc, Is.Not.Null);
            Assert.That(newDoc!.ChangeType, Is.EqualTo("new"), "Unsynced document should still be marked as new");
        }

        [Test]
        [Order(3)]
        public async Task GetDeletedDocuments_WithDeletedRows_ReturnsDeletedDocuments()
        {
            // Arrange - Add another sync log entry for a document that will be deleted
            var syncLogInsert = $@"
                INSERT INTO document_sync_log (source_table, source_id, content_hash, chroma_collection, chunk_ids, sync_action)
                VALUES ('knowledge_docs', 'doc-002', 'somehash123', '{_testCollectionName}', 
                        '[""doc-002_chunk_0"", ""doc-002_chunk_1""]', 'added')";
            
            await _doltCli!.ExecuteAsync(syncLogInsert);

            // Note: doc-002 doesn't exist in knowledge_docs table, simulating deletion

            // Act
            var deletedDocs = await _deltaDetector!.GetDeletedDocumentsAsync(_testCollectionName);
            
            // Assert
            var deletedList = deletedDocs.ToList();
            Assert.That(deletedList.Count, Is.EqualTo(1), "Should find 1 deleted document");
            
            var deletedDoc = deletedList.First();
            Assert.That(deletedDoc.SourceTable, Is.EqualTo("knowledge_docs"));
            Assert.That(deletedDoc.SourceId, Is.EqualTo("doc-002"));
            Assert.That(deletedDoc.ChromaCollection, Is.EqualTo(_testCollectionName));
            
            // Verify chunk IDs can be parsed
            var chunkIds = deletedDoc.GetChunkIdList();
            Assert.That(chunkIds.Count, Is.EqualTo(2));
            Assert.That(chunkIds, Contains.Item("doc-002_chunk_0"));
            Assert.That(chunkIds, Contains.Item("doc-002_chunk_1"));
        }

        [Test]
        [Order(4)]
        public async Task GetCommitDiff_BetweenCommits_ReturnsChanges()
        {
            // Arrange - Commit current state
            await _doltCli!.AddAllAsync();
            var commit1 = await _doltCli.CommitAsync("Test commit 1");
            Assert.That(commit1.Success, Is.True);

            // Make changes
            var newContent = "Another test document for diff testing.";
            var newHash = DocumentConverterUtility.CalculateContentHash(newContent);
            
            var insertNewLog = $@"
                INSERT INTO issue_logs (log_id, project_id, issue_number, title, content, content_hash, log_type)
                VALUES ('log-002', 'proj-001', 102, 'New Feature', '{newContent}', '{newHash}', 'investigation')";
            
            await _doltCli.ExecuteAsync(insertNewLog);

            await _doltCli.AddAllAsync();
            var commit2 = await _doltCli.CommitAsync("Test commit 2");
            Assert.That(commit2.Success, Is.True);

            // Act
            var diffs = await _deltaDetector!.GetCommitDiffAsync(
                commit1.CommitHash, 
                commit2.CommitHash, 
                "issue_logs");
            
            // Assert
            var diffList = diffs.ToList();
            Assert.That(diffList.Count, Is.GreaterThan(0), "Should find differences between commits");
            
            var addedRow = diffList.FirstOrDefault(d => d.DiffType == "added" && d.SourceId == "log-002");
            Assert.That(addedRow, Is.Not.Null, "Should find the newly added row");
            Assert.That(addedRow!.ToContent, Does.Contain(newContent));
            Assert.That(addedRow.ToContentHash, Is.EqualTo(newHash));
        }

        [Test]
        [Order(5)]
        public async Task DocumentConverter_ChunkContent_CreatesCorrectChunks()
        {
            // Arrange
            var longContent = string.Concat(Enumerable.Repeat("This is a test sentence. ", 50)); // ~1250 chars
            
            // Act
            var chunks = DocumentConverterUtility.ChunkContent(longContent, chunkSize: 200, chunkOverlap: 50);
            
            // Assert
            Assert.That(chunks.Count, Is.GreaterThan(1), "Should create multiple chunks");
            
            // Verify chunk sizes
            foreach (var chunk in chunks.Take(chunks.Count - 1)) // All but last chunk
            {
                Assert.That(chunk.Length, Is.EqualTo(200), "Non-final chunks should be exactly chunk size");
            }
            
            // Verify overlap
            for (int i = 1; i < chunks.Count; i++)
            {
                var previousEnd = chunks[i - 1].Substring(chunks[i - 1].Length - 50);
                var currentStart = chunks[i].Substring(0, Math.Min(50, chunks[i].Length));
                Assert.That(previousEnd, Is.EqualTo(currentStart), $"Chunks {i-1} and {i} should overlap");
            }
        }

        [Test]
        [Order(6)]
        public async Task DocumentConverter_ConvertDeltaToChroma_CreatesValidEntries()
        {
            // Arrange
            var delta = new DocumentDelta(
                sourceTable: "issue_logs",
                sourceId: "log-test-123",
                content: "This is test content that will be converted to ChromaDB format with proper chunking.",
                contentHash: DocumentConverterUtility.CalculateContentHash("This is test content..."),
                identifier: "proj-001",
                metadata: @"{""issue_number"": 999, ""log_type"": ""resolution"", ""title"": ""Test Issue""}",
                changeType: "new"
            );

            var currentCommit = await _doltCli!.GetHeadCommitHashAsync();

            // Act
            var chromaEntries = DocumentConverterUtility.ConvertDeltaToChroma(delta, currentCommit, chunkSize: 50, chunkOverlap: 10);
            
            // Assert
            Assert.That(chromaEntries, Is.Not.Null);
            Assert.That(chromaEntries.IsValid, Is.True, "Entries should be valid (all lists same length)");
            Assert.That(chromaEntries.Count, Is.GreaterThan(0), "Should have at least one chunk");
            
            // Verify IDs are deterministic
            Assert.That(chromaEntries.Ids[0], Is.EqualTo("log-test-123_chunk_0"));
            
            // Verify metadata contains required fields
            var metadata = chromaEntries.Metadatas[0];
            Assert.That(metadata["source_table"], Is.EqualTo("issue_logs"));
            Assert.That(metadata["source_id"], Is.EqualTo("log-test-123"));
            Assert.That(metadata["content_hash"], Is.EqualTo(delta.ContentHash));
            Assert.That(metadata["dolt_commit"], Is.EqualTo(currentCommit));
            Assert.That(metadata["chunk_index"], Is.EqualTo(0));
            Assert.That(metadata["issue_number"], Is.EqualTo(999));
            Assert.That(metadata["log_type"], Is.EqualTo("resolution"));
        }

        [Test]
        [Order(7)]
        public async Task GetChangesSinceCommit_WithChanges_ReturnsSummary()
        {
            // Arrange - Get initial commit
            var initialCommit = await _doltCli!.GetHeadCommitHashAsync();

            // Make some changes
            var content3 = "New document added after initial commit.";
            var hash3 = DocumentConverterUtility.CalculateContentHash(content3);
            
            var insertNewDoc = $@"
                INSERT INTO knowledge_docs (doc_id, category, tool_name, tool_version, title, content, content_hash)
                VALUES ('doc-003', 'testing', 'NUnit', '3.14', 'Unit Testing Guide', '{content3}', '{hash3}')";
            
            await _doltCli.ExecuteAsync(insertNewDoc);

            // Delete an existing document
            await _doltCli.ExecuteAsync("DELETE FROM issue_logs WHERE log_id = 'log-001'");

            // Commit changes
            await _doltCli.AddAllAsync();
            await _doltCli.CommitAsync("Changes for summary test");

            // Act
            var summary = await _deltaDetector!.GetChangesSinceCommitAsync(initialCommit, _testCollectionName);
            
            // Assert
            Assert.That(summary, Is.Not.Null);
            Assert.That(summary.HasChanges, Is.True, "Should detect changes");
            Assert.That(summary.TotalChangeCount, Is.GreaterThan(0), "Should have counted changes");
            
            // Verify specific change types
            var pendingList = summary.PendingDocuments.ToList();
            Assert.That(pendingList.Any(d => d.SourceId == "doc-003" && d.ChangeType == "new"), 
                Is.True, "Should detect new document");
        }

        [Test]
        [Order(8)]
        public async Task Integration_FullSyncWorkflow_WithChromaDB()
        {
            // This test demonstrates a complete sync workflow with ChromaDB
            
            // Arrange - Ensure ChromaDB collection exists
            await _chromaService!.CreateCollectionAsync(
                _testCollectionName, 
                new Dictionary<string, object> { ["source"] = "test" });

            // Get pending documents
            var pendingDocs = await _deltaDetector!.GetPendingSyncDocumentsAsync(_testCollectionName);
            var pendingList = pendingDocs.ToList();
            
            Assert.That(pendingList.Count, Is.GreaterThan(0), "Should have documents to sync");

            // Convert and add to ChromaDB
            var currentCommit = await _doltCli!.GetHeadCommitHashAsync();
            var documentsToAdd = new List<string>();
            var idsToAdd = new List<string>();
            var metadatasToAdd = new List<Dictionary<string, object>>();

            foreach (var doc in pendingList)
            {
                var chromaEntries = DocumentConverterUtility.ConvertDeltaToChroma(
                    doc, currentCommit, chunkSize: 100, chunkOverlap: 20);
                
                documentsToAdd.AddRange(chromaEntries.Documents);
                idsToAdd.AddRange(chromaEntries.Ids);
                metadatasToAdd.AddRange(chromaEntries.Metadatas);
            }

            // Act - Add to ChromaDB
            await _chromaService.AddDocumentsAsync(
                _testCollectionName,
                documentsToAdd,
                idsToAdd,
                metadatas: metadatasToAdd);

            // Query ChromaDB to verify
            var queryResults = await _chromaService.QueryDocumentsAsync(
                _testCollectionName,
                queryTexts: new List<string> { "authentication" },
                nResults: 5);

            // Assert
            Assert.That(queryResults, Is.Not.Null);
            
            // Parse query results (they come as object from the interface)
            var resultsJson = JsonSerializer.Serialize(queryResults);
            var results = JsonSerializer.Deserialize<Dictionary<string, object>>(resultsJson);
            
            Assert.That(results, Is.Not.Null);
            Assert.That(results!.ContainsKey("ids"), Is.True);
            
            // The query results structure is: {"ids": [[...]], "documents": [[...]], ...}
            var idsElement = JsonSerializer.Deserialize<JsonElement>(results["ids"].ToString()!);
            Assert.That(idsElement.GetArrayLength(), Is.GreaterThan(0));
            
            var firstResults = idsElement[0];
            Assert.That(firstResults.GetArrayLength(), Is.GreaterThan(0), "Should find relevant documents");

            // Verify chunk IDs follow expected pattern
            foreach (var idElement in firstResults.EnumerateArray())
            {
                var id = idElement.GetString();
                Assert.That(id, Does.Match(@"^[a-z]+-\d+_chunk_\d+$"), 
                    $"Chunk ID '{id}' should follow expected pattern");
            }
        }

        [Test]
        public void DocumentConverterUtility_CalculateContentHash_ProducesDeterministicHash()
        {
            // Arrange
            var content = "Test content for hashing";
            
            // Act
            var hash1 = DocumentConverterUtility.CalculateContentHash(content);
            var hash2 = DocumentConverterUtility.CalculateContentHash(content);
            
            // Assert
            Assert.That(hash1, Is.EqualTo(hash2), "Same content should produce same hash");
            Assert.That(hash1.Length, Is.EqualTo(64), "SHA-256 hash should be 64 hex characters");
            Assert.That(hash1, Does.Match("^[a-f0-9]+$"), "Hash should be lowercase hexadecimal");
        }

        [Test]
        public void DocumentConverterUtility_GetChunkIds_GeneratesDeterministicIds()
        {
            // Arrange
            var sourceId = "test-doc-456";
            var totalChunks = 5;
            
            // Act
            var chunkIds = DocumentConverterUtility.GetChunkIds(sourceId, totalChunks);
            
            // Assert
            Assert.That(chunkIds.Count, Is.EqualTo(totalChunks));
            for (int i = 0; i < totalChunks; i++)
            {
                Assert.That(chunkIds[i], Is.EqualTo($"{sourceId}_chunk_{i}"));
            }
        }
    }
}