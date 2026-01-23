using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Embranch.Models;
using Embranch.Services;
using Embranch.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for PP13-80: Robust JSON Column Parsing for Dolt Query Results
    ///
    /// These tests verify that the application correctly handles JSON-type columns
    /// from Dolt query results, particularly the metadata column in the collections table.
    ///
    /// The root cause of the PP13-80 bug was that Dolt returns JSON-type columns as
    /// nested JSON objects (ValueKind.Object), not as escaped strings (ValueKind.String),
    /// causing GetString() to throw InvalidOperationException.
    ///
    /// NOTE: These tests avoid complex CollectionChangeDetector flows to prevent Python
    /// deadlock issues that have been documented in PP13-60 and PP13-69.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    [Category("PP13-80")]
    [CancelAfter(30000)]
    public class PP13_80_JsonColumnParsingTests
    {
        private DoltCli _doltCli = null!;
        private string _tempDir = null!;
        private ILogger<PP13_80_JsonColumnParsingTests> _logger = null!;
        private ILoggerFactory _loggerFactory = null!;

        [SetUp]
        public async Task Setup()
        {
            // Create unique temp directory for each test
            _tempDir = Path.Combine(Path.GetTempPath(), $"PP13_80_Tests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);

            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });
            _logger = _loggerFactory.CreateLogger<PP13_80_JsonColumnParsingTests>();

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

            // Initialize Dolt repository
            await _doltCli.InitAsync();

            // Create the collections table with JSON type column (matching production schema)
            await CreateProductionSchemaAsync();
        }

        [TearDown]
        public void TearDown()
        {
            // Dispose logger factory
            _loggerFactory?.Dispose();

            // Clean up temp directory
            if (Directory.Exists(_tempDir))
            {
                try
                {
                    Directory.Delete(_tempDir, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }

        /// <summary>
        /// PP13-80 Core Test: Verifies that JsonUtility correctly handles
        /// JsonElement with Object ValueKind - the exact scenario that caused the original bug.
        /// </summary>
        [Test]
        public void JsonUtility_DirectTest_ObjectValueKind_ReturnsRawText()
        {
            // Arrange - Simulate Dolt query result with JSON column
            var json = "{\"collection_name\":\"test\",\"metadata\":{\"key\":\"value\"}}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act - Use JsonUtility to extract metadata (this would throw before PP13-80 fix)
            var metadata = JsonUtility.GetPropertyAsString(element, "metadata", "{}");
            var collectionName = JsonUtility.GetPropertyAsString(element, "collection_name", "");

            // Assert - Should return raw JSON, not throw exception
            Assert.That(metadata, Does.Contain("\"key\":\"value\""));
            Assert.That(collectionName, Is.EqualTo("test"));
            TestContext.WriteLine($"JsonUtility correctly extracted metadata: {metadata}");
        }

        /// <summary>
        /// Verifies that querying Dolt with JSON columns works and returns parseable data.
        /// This test inserts data into Dolt and verifies the query results can be parsed.
        /// </summary>
        [Test]
        public async Task DoltQuery_WithJsonMetadata_ReturnsParseableResults()
        {
            // Arrange - Insert collection with JSON metadata
            var metadata = new Dictionary<string, object>
            {
                { "hnsw:space", "cosine" },
                { "custom_key", "custom_value" }
            };
            var metadataJson = JsonSerializer.Serialize(metadata);
            var escapedMetadata = SqlEscapeUtility.EscapeJsonForSql(metadataJson);

            var insertSql = $@"
                INSERT INTO collections (collection_name, metadata, created_at, updated_at)
                VALUES ('test_collection', '{escapedMetadata}', NOW(), NOW())";

            await _doltCli.ExecuteAsync(insertSql);
            TestContext.WriteLine("Inserted collection with JSON metadata");

            // Act - Query the data back
            var querySql = "SELECT collection_name, metadata FROM collections";
            var results = await _doltCli.QueryAsync<dynamic>(querySql);

            // Assert - Verify we can parse the results using JsonUtility
            foreach (var row in results)
            {
                if (row is JsonElement jsonElement)
                {
                    // This is the exact pattern used in CollectionChangeDetector (after PP13-80 fix)
                    var name = JsonUtility.GetPropertyAsString(jsonElement, "collection_name", "");
                    var metadataResult = JsonUtility.GetPropertyAsString(jsonElement, "metadata", "{}");

                    Assert.That(name, Is.EqualTo("test_collection"));
                    Assert.That(metadataResult, Does.Contain("hnsw:space"));
                    TestContext.WriteLine($"Successfully parsed: name={name}, metadata={metadataResult}");
                }
                else
                {
                    // Dynamic object case
                    Assert.That((string)row.collection_name, Is.EqualTo("test_collection"));
                    TestContext.WriteLine("Parsed as dynamic object");
                }
            }
        }

        /// <summary>
        /// Verifies that null metadata is handled correctly in Dolt query results.
        /// </summary>
        [Test]
        public async Task DoltQuery_WithNullMetadata_ReturnsDefault()
        {
            // Arrange - Insert collection with NULL metadata
            var insertSql = @"
                INSERT INTO collections (collection_name, metadata, created_at, updated_at)
                VALUES ('null_meta_collection', NULL, NOW(), NOW())";

            await _doltCli.ExecuteAsync(insertSql);

            // Act - Query the data back
            var querySql = "SELECT collection_name, metadata FROM collections WHERE collection_name = 'null_meta_collection'";
            var results = await _doltCli.QueryAsync<dynamic>(querySql);

            // Assert
            foreach (var row in results)
            {
                if (row is JsonElement jsonElement)
                {
                    var name = JsonUtility.GetPropertyAsString(jsonElement, "collection_name", "");
                    var metadataResult = JsonUtility.GetPropertyAsString(jsonElement, "metadata", "{}");

                    Assert.That(name, Is.EqualTo("null_meta_collection"));
                    Assert.That(metadataResult, Is.EqualTo("{}"));
                    TestContext.WriteLine($"Successfully parsed null metadata as default: {metadataResult}");
                }
            }
        }

        /// <summary>
        /// Verifies that empty JSON object metadata is handled correctly.
        /// </summary>
        [Test]
        public async Task DoltQuery_WithEmptyMetadata_ParsesCorrectly()
        {
            // Arrange - Insert collection with empty {} metadata
            var insertSql = @"
                INSERT INTO collections (collection_name, metadata, created_at, updated_at)
                VALUES ('empty_meta_collection', '{}', NOW(), NOW())";

            await _doltCli.ExecuteAsync(insertSql);

            // Act - Query the data back
            var querySql = "SELECT collection_name, metadata FROM collections WHERE collection_name = 'empty_meta_collection'";
            var results = await _doltCli.QueryAsync<dynamic>(querySql);

            // Assert
            foreach (var row in results)
            {
                if (row is JsonElement jsonElement)
                {
                    var name = JsonUtility.GetPropertyAsString(jsonElement, "collection_name", "");
                    var metadataResult = JsonUtility.GetPropertyAsString(jsonElement, "metadata", "{}");

                    Assert.That(name, Is.EqualTo("empty_meta_collection"));
                    Assert.That(metadataResult, Is.EqualTo("{}"));
                    TestContext.WriteLine($"Successfully parsed empty metadata: {metadataResult}");
                }
            }
        }

        /// <summary>
        /// Verifies that deeply nested JSON metadata is handled correctly.
        /// </summary>
        [Test]
        public async Task DoltQuery_WithNestedMetadata_ParsesCorrectly()
        {
            // Arrange - Insert collection with deeply nested JSON metadata
            var metadata = new Dictionary<string, object>
            {
                { "level1", new Dictionary<string, object>
                    {
                        { "level2", new Dictionary<string, object>
                            {
                                { "value", "deep_value" }
                            }
                        }
                    }
                }
            };
            var metadataJson = JsonSerializer.Serialize(metadata);
            var escapedMetadata = SqlEscapeUtility.EscapeJsonForSql(metadataJson);

            var insertSql = $@"
                INSERT INTO collections (collection_name, metadata, created_at, updated_at)
                VALUES ('nested_meta_collection', '{escapedMetadata}', NOW(), NOW())";

            await _doltCli.ExecuteAsync(insertSql);

            // Act - Query the data back
            var querySql = "SELECT collection_name, metadata FROM collections WHERE collection_name = 'nested_meta_collection'";
            var results = await _doltCli.QueryAsync<dynamic>(querySql);

            // Assert
            foreach (var row in results)
            {
                if (row is JsonElement jsonElement)
                {
                    var name = JsonUtility.GetPropertyAsString(jsonElement, "collection_name", "");
                    var metadataResult = JsonUtility.GetPropertyAsString(jsonElement, "metadata", "{}");

                    Assert.That(name, Is.EqualTo("nested_meta_collection"));
                    Assert.That(metadataResult, Does.Contain("level1"));
                    Assert.That(metadataResult, Does.Contain("deep_value"));
                    TestContext.WriteLine($"Successfully parsed nested metadata: {metadataResult}");
                }
            }
        }

        /// <summary>
        /// Simulates the exact bug scenario: multiple collections with various metadata types.
        /// This is the validation test that ensures the PP13-80 fix works end-to-end.
        /// </summary>
        [Test]
        public async Task DoltQuery_MultipleCollections_VaryingMetadata_AllParseCorrectly()
        {
            // Arrange - Insert multiple collections with different metadata patterns
            var collections = new (string name, string? metadata)[]
            {
                ("col_with_object", "{\"type\":\"primary\",\"tags\":[\"a\",\"b\"]}"),
                ("col_with_null", null),
                ("col_with_empty", "{}"),
                ("col_with_nested", "{\"outer\":{\"inner\":\"value\"}}")
            };

            foreach (var (name, metadata) in collections)
            {
                string insertSql;
                if (metadata != null)
                {
                    var escaped = SqlEscapeUtility.EscapeJsonForSql(metadata);
                    insertSql = $@"
                        INSERT INTO collections (collection_name, metadata, created_at, updated_at)
                        VALUES ('{name}', '{escaped}', NOW(), NOW())";
                }
                else
                {
                    insertSql = $@"
                        INSERT INTO collections (collection_name, metadata, created_at, updated_at)
                        VALUES ('{name}', NULL, NOW(), NOW())";
                }
                await _doltCli.ExecuteAsync(insertSql);
            }

            // Act - Query all collections back
            var querySql = "SELECT collection_name, metadata FROM collections ORDER BY collection_name";
            var results = await _doltCli.QueryAsync<dynamic>(querySql);

            // Assert - All should parse without exception using the PP13-80 fix pattern
            int count = 0;
            foreach (var row in results)
            {
                if (row is JsonElement jsonElement)
                {
                    // This exact pattern is used in CollectionChangeDetector.GetDoltCollectionsAsync()
                    var name = JsonUtility.GetPropertyAsString(jsonElement, "collection_name", "");
                    var metadataResult = JsonUtility.GetPropertyAsString(jsonElement, "metadata", "{}");

                    Assert.That(name, Is.Not.Empty, $"Collection name should not be empty for row {count}");
                    Assert.That(metadataResult, Is.Not.Null, $"Metadata should not be null for collection {name}");

                    TestContext.WriteLine($"[{count}] Parsed: {name} => {metadataResult}");
                    count++;
                }
            }

            Assert.That(count, Is.EqualTo(4), "Should have parsed all 4 collections");
            TestContext.WriteLine($"Successfully parsed all {count} collections without exception");
        }

        #region Helper Methods

        /// <summary>
        /// Creates the production schema with JSON type columns
        /// </summary>
        private async Task CreateProductionSchemaAsync()
        {
            // Create collections table with JSON type (matching SyncDatabaseSchemaV2.sql)
            var collectionsTableQuery = @"
                CREATE TABLE IF NOT EXISTS collections (
                    collection_name VARCHAR(255) PRIMARY KEY,
                    display_name VARCHAR(255),
                    description TEXT,
                    embedding_model VARCHAR(100) DEFAULT 'default',
                    chunk_size INT DEFAULT 512,
                    chunk_overlap INT DEFAULT 50,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                    document_count INT DEFAULT 0,
                    metadata JSON
                )";

            await _doltCli.ExecuteAsync(collectionsTableQuery);
            TestContext.WriteLine("Created collections table with JSON metadata column");
        }

        #endregion
    }
}
