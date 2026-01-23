using NUnit.Framework;
using Embranch.Utilities;
using System.Text.Json;

namespace EmbranchTesting.UnitTests
{
    /// <summary>
    /// Unit tests for JsonUtility class.
    /// Tests safe extraction of values from JsonElements, particularly for Dolt query result parsing.
    /// PP13-80: Robust JSON Column Parsing for Dolt Query Results
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public class JsonUtilityTests
    {
        #region GetElementAsString Tests

        /// <summary>
        /// Verifies that string values are returned directly
        /// </summary>
        [Test]
        public void GetElementAsString_StringValue_ReturnsString()
        {
            // Arrange
            var json = "\"hello world\"";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetElementAsString(element);

            // Assert
            Assert.That(result, Is.EqualTo("hello world"));
        }

        /// <summary>
        /// Verifies that null values return the default value
        /// </summary>
        [Test]
        public void GetElementAsString_NullValue_ReturnsDefault()
        {
            // Arrange
            var json = "null";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetElementAsString(element, "default");

            // Assert
            Assert.That(result, Is.EqualTo("default"));
        }

        /// <summary>
        /// Verifies that object values return raw JSON text
        /// This is the key fix for PP13-80 - Dolt returns JSON columns as objects
        /// </summary>
        [Test]
        public void GetElementAsString_ObjectValue_ReturnsRawText()
        {
            // Arrange - This is what Dolt returns for JSON columns
            var json = "{\"key\":\"value\",\"nested\":{\"a\":1}}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetElementAsString(element);

            // Assert - Should return the raw JSON text, not throw an exception
            Assert.That(result, Does.Contain("\"key\":\"value\""));
            Assert.That(result, Does.Contain("\"nested\""));
        }

        /// <summary>
        /// Verifies that array values return raw JSON text
        /// </summary>
        [Test]
        public void GetElementAsString_ArrayValue_ReturnsRawText()
        {
            // Arrange
            var json = "[1, 2, 3, \"four\"]";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetElementAsString(element);

            // Assert - GetRawText preserves original whitespace
            Assert.That(result, Does.Contain("1"));
            Assert.That(result, Does.Contain("2"));
            Assert.That(result, Does.Contain("3"));
            Assert.That(result, Does.Contain("\"four\""));
            Assert.That(result, Does.StartWith("["));
            Assert.That(result, Does.EndWith("]"));
        }

        /// <summary>
        /// Verifies that number values return raw text representation
        /// </summary>
        [Test]
        public void GetElementAsString_NumberValue_ReturnsRawText()
        {
            // Arrange
            var json = "42.5";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetElementAsString(element);

            // Assert
            Assert.That(result, Is.EqualTo("42.5"));
        }

        /// <summary>
        /// Verifies that true boolean returns "true"
        /// </summary>
        [Test]
        public void GetElementAsString_BooleanTrue_ReturnsTrue()
        {
            // Arrange
            var json = "true";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetElementAsString(element);

            // Assert
            Assert.That(result, Is.EqualTo("true"));
        }

        /// <summary>
        /// Verifies that false boolean returns "false"
        /// </summary>
        [Test]
        public void GetElementAsString_BooleanFalse_ReturnsFalse()
        {
            // Arrange
            var json = "false";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetElementAsString(element);

            // Assert
            Assert.That(result, Is.EqualTo("false"));
        }

        /// <summary>
        /// Verifies that empty string is returned when no default is specified
        /// </summary>
        [Test]
        public void GetElementAsString_NullValue_NoDefault_ReturnsEmptyString()
        {
            // Arrange
            var json = "null";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetElementAsString(element);

            // Assert
            Assert.That(result, Is.EqualTo(""));
        }

        #endregion

        #region GetPropertyAsString Tests

        /// <summary>
        /// Verifies that existing string properties are extracted correctly
        /// </summary>
        [Test]
        public void GetPropertyAsString_ExistingStringProperty_ReturnsValue()
        {
            // Arrange
            var json = "{\"name\":\"test\",\"value\":123}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetPropertyAsString(element, "name");

            // Assert
            Assert.That(result, Is.EqualTo("test"));
        }

        /// <summary>
        /// Verifies that missing properties return the default value
        /// </summary>
        [Test]
        public void GetPropertyAsString_MissingProperty_ReturnsDefault()
        {
            // Arrange
            var json = "{\"name\":\"test\"}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetPropertyAsString(element, "missing", "default_value");

            // Assert
            Assert.That(result, Is.EqualTo("default_value"));
        }

        /// <summary>
        /// Verifies that object properties return raw JSON text
        /// This is the key fix for PP13-80 - metadata column returns JSON object
        /// </summary>
        [Test]
        public void GetPropertyAsString_ObjectProperty_ReturnsRawText()
        {
            // Arrange - Simulating Dolt query result with JSON metadata column
            var json = "{\"collection_name\":\"test\",\"metadata\":{\"key\":\"value\"}}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetPropertyAsString(element, "metadata", "{}");

            // Assert - Should return raw JSON, not throw an exception
            Assert.That(result, Does.Contain("\"key\":\"value\""));
        }

        /// <summary>
        /// Verifies that null properties return the default value
        /// </summary>
        [Test]
        public void GetPropertyAsString_NullProperty_ReturnsDefault()
        {
            // Arrange
            var json = "{\"name\":null}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetPropertyAsString(element, "name", "default");

            // Assert
            Assert.That(result, Is.EqualTo("default"));
        }

        #endregion

        #region GetPropertyAsNullableString Tests

        /// <summary>
        /// Verifies that null properties return null (not default)
        /// </summary>
        [Test]
        public void GetPropertyAsNullableString_NullProperty_ReturnsNull()
        {
            // Arrange
            var json = "{\"name\":null}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetPropertyAsNullableString(element, "name");

            // Assert
            Assert.That(result, Is.Null);
        }

        /// <summary>
        /// Verifies that missing properties return null
        /// </summary>
        [Test]
        public void GetPropertyAsNullableString_MissingProperty_ReturnsNull()
        {
            // Arrange
            var json = "{\"other\":\"value\"}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetPropertyAsNullableString(element, "missing");

            // Assert
            Assert.That(result, Is.Null);
        }

        /// <summary>
        /// Verifies that existing string properties return the value
        /// </summary>
        [Test]
        public void GetPropertyAsNullableString_ExistingProperty_ReturnsValue()
        {
            // Arrange
            var json = "{\"name\":\"test_value\"}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetPropertyAsNullableString(element, "name");

            // Assert
            Assert.That(result, Is.EqualTo("test_value"));
        }

        /// <summary>
        /// Verifies that object properties return raw JSON text
        /// </summary>
        [Test]
        public void GetPropertyAsNullableString_ObjectProperty_ReturnsRawText()
        {
            // Arrange
            var json = "{\"metadata\":{\"created\":\"2024-01-01\"}}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var result = JsonUtility.GetPropertyAsNullableString(element, "metadata");

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("\"created\":\"2024-01-01\""));
        }

        #endregion

        #region Realistic Dolt Query Scenario Tests

        /// <summary>
        /// PP13-80: Simulates the exact scenario that caused the bug -
        /// parsing a collection row with JSON metadata from a Dolt query result
        /// </summary>
        [Test]
        public void GetPropertyAsString_DoltCollectionRow_WithMetadata_ParsesCorrectly()
        {
            // Arrange - Exact structure returned by Dolt for collections query
            var json = "{\"collection_name\":\"my_collection\",\"metadata\":{\"hnsw:space\":\"cosine\",\"custom_key\":123}}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var collectionName = JsonUtility.GetPropertyAsString(element, "collection_name", "");
            var metadata = JsonUtility.GetPropertyAsString(element, "metadata", "{}");

            // Assert
            Assert.That(collectionName, Is.EqualTo("my_collection"));
            Assert.That(metadata, Does.Contain("\"hnsw:space\":\"cosine\""));
            Assert.That(metadata, Does.Contain("\"custom_key\":123"));
        }

        /// <summary>
        /// Simulates parsing a collection row with null metadata
        /// </summary>
        [Test]
        public void GetPropertyAsString_DoltCollectionRow_WithNullMetadata_ReturnsDefault()
        {
            // Arrange
            var json = "{\"collection_name\":\"my_collection\",\"metadata\":null}";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var collectionName = JsonUtility.GetPropertyAsString(element, "collection_name", "");
            var metadata = JsonUtility.GetPropertyAsString(element, "metadata", "{}");

            // Assert
            Assert.That(collectionName, Is.EqualTo("my_collection"));
            Assert.That(metadata, Is.EqualTo("{}"));
        }

        /// <summary>
        /// Simulates parsing a document row with nested metadata
        /// </summary>
        [Test]
        public void GetPropertyAsString_DoltDocumentRow_WithNestedMetadata_ParsesCorrectly()
        {
            // Arrange - Document with complex nested metadata
            var json = @"{
                ""doc_id"":""doc-123"",
                ""content_hash"":""abc123"",
                ""metadata"":{
                    ""source"":""import"",
                    ""tags"":[""tag1"",""tag2""],
                    ""nested"":{""deep"":""value""}
                }
            }";
            var element = JsonDocument.Parse(json).RootElement;

            // Act
            var docId = JsonUtility.GetPropertyAsString(element, "doc_id", "");
            var contentHash = JsonUtility.GetPropertyAsString(element, "content_hash", "");
            var metadata = JsonUtility.GetPropertyAsString(element, "metadata", "{}");

            // Assert
            Assert.That(docId, Is.EqualTo("doc-123"));
            Assert.That(contentHash, Is.EqualTo("abc123"));
            Assert.That(metadata, Does.Contain("\"source\":\"import\""));
            Assert.That(metadata, Does.Contain("\"tags\":[\"tag1\",\"tag2\"]"));
        }

        #endregion
    }
}
