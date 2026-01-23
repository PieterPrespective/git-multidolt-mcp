using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Embranch.Models;
using Embranch.Services;
using Embranch.Utilities;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for PP13-77: SQL JSON Escaping for Metadata with Special Characters.
    /// Validates that documents with Windows file paths, single quotes, and other special
    /// characters in metadata can be successfully inserted, updated, and retrieved from Dolt.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class PP13_77_MetadataEscapingTests
    {
        private string _tempDir = null!;
        private DoltCli _doltCli = null!;
        private ChromaDbService _chromaService = null!;
        private ChromaToDoltSyncer _chromaSyncer = null!;
        private ILogger<PP13_77_MetadataEscapingTests> _logger = null!;
        private ILoggerFactory _loggerFactory = null!;

        [SetUp]
        public async Task Setup()
        {
            // Initialize Python context if not already done
            if (!PythonContext.IsInitialized)
            {
                PythonContext.Initialize();
            }

            _tempDir = Path.Combine(Path.GetTempPath(), $"PP13_77_Tests_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            _loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _logger = _loggerFactory.CreateLogger<PP13_77_MetadataEscapingTests>();

            // Initialize Dolt CLI
            var doltConfig = Options.Create(new DoltConfiguration
            {
                DoltExecutablePath = Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? @"C:\Program Files\Dolt\bin\dolt.exe"
                    : "dolt",
                RepositoryPath = _tempDir,
                CommandTimeoutMs = 30000,
                EnableDebugLogging = true
            });
            _doltCli = new DoltCli(doltConfig, _loggerFactory.CreateLogger<DoltCli>());
            await _doltCli.InitAsync();

            // Initialize ChromaDB service with local storage
            var chromaDataPath = Path.Combine(_tempDir, "chroma_data");
            Directory.CreateDirectory(chromaDataPath);
            var serverConfig = Options.Create(new ServerConfiguration
            {
                ChromaDataPath = chromaDataPath,
                DataPath = _tempDir
            });
            _chromaService = new ChromaDbService(
                _loggerFactory.CreateLogger<ChromaDbService>(),
                serverConfig
            );

            // Initialize deletion tracker
            var deletionTracker = new SqliteDeletionTracker(
                _loggerFactory.CreateLogger<SqliteDeletionTracker>(),
                serverConfig.Value);
            await deletionTracker.InitializeAsync(_tempDir);

            // Initialize ChromaToDoltSyncer
            var chromaDetector = new ChromaToDoltDetector(
                _chromaService,
                _doltCli,
                deletionTracker,
                doltConfig,
                _loggerFactory.CreateLogger<ChromaToDoltDetector>());

            _chromaSyncer = new ChromaToDoltSyncer(
                _chromaService,
                _doltCli,
                chromaDetector,
                _loggerFactory.CreateLogger<ChromaToDoltSyncer>());

            // Ensure schema tables exist
            await _chromaSyncer.CreateSchemaTablesAsync();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                // Cleanup Chroma collections created during tests
                var collections = _chromaService?.ListCollectionsAsync()?.GetAwaiter().GetResult();
                if (collections != null)
                {
                    foreach (var collection in collections.Where(c => c.StartsWith("pp13_77_")))
                    {
                        try
                        {
                            _chromaService.DeleteCollectionAsync(collection).GetAwaiter().GetResult();
                        }
                        catch { /* Ignore cleanup errors */ }
                    }
                }

                _chromaService?.Dispose();
                _loggerFactory?.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        /// <summary>
        /// Verifies that a document with Windows file path in metadata can be inserted into Dolt.
        /// This reproduces the exact scenario from the bug report where paths like C:\Users\...
        /// caused JSON parsing errors.
        /// </summary>
        [Test]
        public async Task InsertDocument_WindowsPathInMetadata_Succeeds()
        {
            _logger.LogInformation("=== PP13-77 TEST: Insert document with Windows path in metadata ===");

            const string collectionName = "pp13_77_insert_windows_path";

            // Create the collection in ChromaDB first
            await _chromaService.CreateCollectionAsync(collectionName);

            // Create a ChromaDocument with Windows path in metadata
            var metadata = new Dictionary<string, object>
            {
                ["import_source"] = @"C:\Users\piete\AppData\Local\Temp\Embranch_LegacyMigration\test_collection",
                ["import_timestamp"] = "2026-01-16T13:05:50.4134547Z",
                ["is_local_change"] = true
            };

            var doc = new ChromaDocument(
                DocId: "test-doc-001",
                CollectionName: collectionName,
                Content: "Test document content for PP13-77",
                ContentHash: "abc123hash",
                Metadata: metadata,
                Chunks: new List<dynamic>()
            );

            // Act - This should not throw a JSON parsing error
            await _chromaSyncer.InsertDocumentToDoltAsync(doc, collectionName);

            // Assert - Verify document exists in Dolt
            var result = await _doltCli.QueryAsync<Dictionary<string, object>>(
                $"SELECT doc_id, metadata FROM documents WHERE doc_id = 'test-doc-001'");

            Assert.That(result, Is.Not.Null, "Document should exist in Dolt");
            Assert.That(result.Count(), Is.GreaterThan(0), "Should have at least one result row");

            _logger.LogInformation("=== PP13-77 TEST PASSED: Windows path metadata inserted successfully ===");
        }

        /// <summary>
        /// Verifies that a document with Windows file path in metadata can be updated in Dolt.
        /// </summary>
        [Test]
        public async Task UpdateDocument_WindowsPathInMetadata_Succeeds()
        {
            _logger.LogInformation("=== PP13-77 TEST: Update document with Windows path in metadata ===");

            const string collectionName = "pp13_77_update_windows_path";

            // Create the collection in ChromaDB first
            await _chromaService.CreateCollectionAsync(collectionName);

            // First insert a document without special characters
            var initialMetadata = new Dictionary<string, object>
            {
                ["status"] = "initial"
            };

            var initialDoc = new ChromaDocument(
                DocId: "update-doc-001",
                CollectionName: collectionName,
                Content: "Initial content",
                ContentHash: "initial123",
                Metadata: initialMetadata,
                Chunks: new List<dynamic>()
            );

            await _chromaSyncer.InsertDocumentToDoltAsync(initialDoc, collectionName);

            // Now update with Windows path in metadata
            var updatedMetadata = new Dictionary<string, object>
            {
                ["import_source"] = @"C:\Users\testuser\Documents\ImportedData",
                ["import_timestamp"] = DateTime.UtcNow.ToString("O"),
                ["status"] = "updated"
            };

            var updatedDoc = new ChromaDocument(
                DocId: "update-doc-001",
                CollectionName: collectionName,
                Content: "Updated content with special path metadata",
                ContentHash: "updated456",
                Metadata: updatedMetadata,
                Chunks: new List<dynamic>()
            );

            // Act - This should not throw a JSON parsing error
            await _chromaSyncer.UpdateDocumentInDoltAsync(updatedDoc);

            // Assert - Verify document was updated
            var result = await _doltCli.QueryAsync<Dictionary<string, object>>(
                $"SELECT content_hash FROM documents WHERE doc_id = 'update-doc-001'");

            Assert.That(result, Is.Not.Null);
            var resultList = result.ToList();
            Assert.That(resultList.Count, Is.GreaterThan(0));

            _logger.LogInformation("=== PP13-77 TEST PASSED: Windows path metadata updated successfully ===");
        }

        /// <summary>
        /// Verifies that metadata with various special characters (paths, quotes, newlines)
        /// is preserved correctly through insert and retrieval round-trip.
        /// </summary>
        [Test]
        public async Task RoundTrip_MetadataWithSpecialChars_Preserved()
        {
            _logger.LogInformation("=== PP13-77 TEST: Round-trip metadata preservation with special characters ===");

            const string collectionName = "pp13_77_roundtrip";

            // Create the collection
            await _chromaService.CreateCollectionAsync(collectionName);

            // Create metadata with various special characters
            var originalMetadata = new Dictionary<string, object>
            {
                ["path"] = @"C:\Users\O'Brien\Documents\Test Files",
                ["description"] = "Document with 'single quotes' and special chars",
                ["timestamp"] = "2026-01-16T13:05:50.4134547Z"
            };

            var doc = new ChromaDocument(
                DocId: "roundtrip-doc-001",
                CollectionName: collectionName,
                Content: "Test content for round-trip validation",
                ContentHash: "roundtrip123",
                Metadata: originalMetadata,
                Chunks: new List<dynamic>()
            );

            // Act - Insert document
            await _chromaSyncer.InsertDocumentToDoltAsync(doc, collectionName);

            // Retrieve and verify metadata
            var result = await _doltCli.QueryAsync<Dictionary<string, object>>(
                $"SELECT metadata FROM documents WHERE doc_id = 'roundtrip-doc-001'");

            Assert.That(result, Is.Not.Null);
            var resultList = result.ToList();
            Assert.That(resultList.Count, Is.GreaterThan(0));

            // Parse the retrieved metadata JSON
            var retrievedMetadataJson = resultList[0]["metadata"]?.ToString();
            Assert.That(retrievedMetadataJson, Is.Not.Null.And.Not.Empty);

            var retrievedMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(retrievedMetadataJson!);
            Assert.That(retrievedMetadata, Is.Not.Null);

            // Verify special characters were preserved
            var pathValue = retrievedMetadata!["path"].ToString();
            Assert.That(pathValue, Does.Contain("O'Brien"), "Single quote in path should be preserved");
            Assert.That(pathValue, Does.Contain(@"\"), "Backslashes should be preserved");

            _logger.LogInformation("=== PP13-77 TEST PASSED: Special characters preserved in round-trip ===");
        }

        /// <summary>
        /// Simulates the exact import scenario from the bug report where importing from
        /// an external ChromaDB with Windows paths in import_source metadata caused commit failure.
        /// </summary>
        [Test]
        public async Task ImportedDocument_WithLegacyPath_CommitsSuccessfully()
        {
            _logger.LogInformation("=== PP13-77 TEST: Import scenario with legacy Windows path ===");

            const string collectionName = "pp13_77_import_scenario";

            // Create the collection
            await _chromaService.CreateCollectionAsync(collectionName);

            // Simulate the exact metadata from the bug report
            var importMetadata = new Dictionary<string, object>
            {
                ["import_source"] = @"C:\Users\piete\AppData\Local\Temp\Embranch_LegacyMigration\feat-design-planning_27e44c08b6534f57ad9f059c75ec4261",
                ["import_source_collection"] = "SE-409",
                ["import_timestamp"] = "2026-01-16T13:05:50.4134547Z"
            };

            var doc = new ChromaDocument(
                DocId: "planned-approach-001",
                CollectionName: collectionName,
                Content: "Imported design planning document content",
                ContentHash: "legacyhash789",
                Metadata: importMetadata,
                Chunks: new List<dynamic>()
            );

            // Act - Insert document (should not throw)
            await _chromaSyncer.InsertDocumentToDoltAsync(doc, collectionName);

            // Stage changes
            await _doltCli.AddAllAsync();

            // Commit should succeed without JSON parsing error
            var commitResult = await _doltCli.CommitAsync("Test commit for PP13-77 import scenario");

            // Assert
            Assert.That(commitResult, Is.Not.Null);
            Assert.That(commitResult.Success, Is.True, "Commit should succeed");
            Assert.That(commitResult.CommitHash, Is.Not.Null.And.Not.Empty, "Should have a commit hash");

            _logger.LogInformation("=== PP13-77 TEST PASSED: Import scenario committed successfully ===");
        }

        /// <summary>
        /// Verifies that the SqlEscapeUtility correctly escapes a real-world metadata JSON.
        /// This is a sanity check for the escaping logic.
        /// </summary>
        [Test]
        public void SqlEscapeUtility_RealWorldMetadata_EscapesCorrectly()
        {
            _logger.LogInformation("=== PP13-77 TEST: SqlEscapeUtility real-world escaping ===");

            // Arrange - create metadata like it would come from import
            var metadata = new Dictionary<string, object>
            {
                ["import_source"] = @"C:\Users\piete\AppData\Local\Temp\Embranch_LegacyMigration\test",
                ["import_timestamp"] = "2026-01-16T13:05:50.4134547Z"
            };

            var json = JsonSerializer.Serialize(metadata);
            _logger.LogInformation("Original JSON: {Json}", json);

            // Act
            var escapedJson = SqlEscapeUtility.EscapeJsonForSql(json);
            _logger.LogInformation("Escaped JSON: {EscapedJson}", escapedJson);

            // Assert - backslashes should be doubled
            Assert.That(escapedJson, Does.Contain(@"C:\\\\Users\\\\piete"));
            Assert.That(escapedJson, Does.Contain(@"\\\\AppData\\\\Local\\\\Temp"));

            // Verify the escaped JSON would be valid after SQL parsing
            // After SQL parsing, it should become valid JSON again
            var afterSqlParsing = escapedJson
                .Replace("\\\\", "\\")  // SQL parser consumes one level
                .Replace("''", "'");     // SQL parser unescapes quotes

            // This should be valid JSON
            var parsedMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(afterSqlParsing);
            Assert.That(parsedMetadata, Is.Not.Null);
            Assert.That(parsedMetadata!["import_source"].ToString(), Does.Contain(@"C:\Users\piete"));

            _logger.LogInformation("=== PP13-77 TEST PASSED: SqlEscapeUtility escaping verified ===");
        }
    }
}
