using NUnit.Framework;
using Embranch.Utilities;
using System.Collections.Generic;
using System.Text.Json;

namespace EmbranchTesting.UnitTests
{
    /// <summary>
    /// Unit tests for SqlEscapeUtility class.
    /// Tests JSON escaping for SQL embedding and plain string escaping.
    /// Verifies proper handling of Windows paths, special characters, Unicode, and edge cases.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public class SqlEscapeUtilityTests
    {
        #region EscapeJsonForSql - Backslash Tests

        /// <summary>
        /// Verifies that Windows file paths with backslashes are properly double-escaped.
        /// JSON serializer produces \\ for backslash, we need \\\\ for SQL embedding.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_WindowsPath_EscapesBackslashes()
        {
            // Arrange - JSON with escaped backslashes (as produced by JsonSerializer)
            var json = @"{""path"":""C:\\Users\\piete""}";

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert - backslashes should be doubled
            Assert.That(result, Is.EqualTo(@"{""path"":""C:\\\\Users\\\\piete""}"));
        }

        /// <summary>
        /// Verifies that a full Windows path with multiple directories is properly escaped.
        /// This simulates the real-world import scenario from the bug report.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_FullWindowsPath_EscapesAllBackslashes()
        {
            // Arrange - realistic import metadata path
            var json = @"{""import_source"":""C:\\Users\\piete\\AppData\\Local\\Temp\\DMMS_LegacyMigration""}";

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert - all backslashes should be doubled
            Assert.That(result, Does.Contain(@"C:\\\\Users\\\\piete\\\\AppData\\\\Local\\\\Temp\\\\DMMS_LegacyMigration"));
        }

        #endregion

        #region EscapeJsonForSql - Single Quote Tests

        /// <summary>
        /// Verifies that single quotes in JSON are doubled for SQL string literals.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_SingleQuote_Doubled()
        {
            // Arrange
            var json = @"{""name"":""O'Brien""}";

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert - single quotes should be doubled
            Assert.That(result, Is.EqualTo(@"{""name"":""O''Brien""}"));
        }

        /// <summary>
        /// Verifies that multiple single quotes are all doubled.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_MultipleSingleQuotes_AllDoubled()
        {
            // Arrange
            var json = @"{""text"":""It's John's book""}";

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert
            Assert.That(result, Is.EqualTo(@"{""text"":""It''s John''s book""}"));
        }

        #endregion

        #region EscapeJsonForSql - Combined Escaping Tests

        /// <summary>
        /// Verifies that both backslashes and single quotes are properly escaped.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_BackslashAndQuote_BothEscaped()
        {
            // Arrange - path with user name containing apostrophe
            var json = @"{""path"":""C:\\Users\\O'Brien""}";

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert - backslashes doubled AND single quotes doubled
            Assert.That(result, Is.EqualTo(@"{""path"":""C:\\\\Users\\\\O''Brien""}"));
        }

        #endregion

        #region EscapeJsonForSql - Unicode Escape Tests

        /// <summary>
        /// Verifies that Unicode escape sequences in JSON are properly escaped.
        /// The \u in \u0041 becomes \\u so SQL passes \u0041 to JSON parser.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_UnicodeEscape_PreservedButEscaped()
        {
            // Arrange - JSON with Unicode escape sequence for letter 'A'
            var json = @"{""char"":""\u0041""}";

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert - the backslash before u should be doubled
            Assert.That(result, Is.EqualTo(@"{""char"":""\\u0041""}"));
        }

        #endregion

        #region EscapeJsonForSql - Control Character Escape Tests

        /// <summary>
        /// Verifies that newline and tab escape sequences are properly escaped.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_NewlineAndTab_Escaped()
        {
            // Arrange - JSON with escaped newline and tab
            var json = @"{""text"":""line1\nline2\ttab""}";

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert - backslashes should be doubled
            Assert.That(result, Is.EqualTo(@"{""text"":""line1\\nline2\\ttab""}"));
        }

        /// <summary>
        /// Verifies that carriage return escape sequences are properly escaped.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_CarriageReturn_Escaped()
        {
            // Arrange - JSON with Windows line ending
            var json = @"{""text"":""line1\r\nline2""}";

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert
            Assert.That(result, Is.EqualTo(@"{""text"":""line1\\r\\nline2""}"));
        }

        #endregion

        #region EscapeJsonForSql - Edge Cases

        /// <summary>
        /// Verifies that empty string input returns empty string.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_EmptyString_ReturnsEmpty()
        {
            Assert.That(SqlEscapeUtility.EscapeJsonForSql(""), Is.EqualTo(""));
        }

        /// <summary>
        /// Verifies that null input returns null.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_NullString_ReturnsNull()
        {
            Assert.That(SqlEscapeUtility.EscapeJsonForSql(null), Is.Null);
        }

        /// <summary>
        /// Verifies that JSON without special characters is returned unchanged.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_NoSpecialChars_UnchangedExceptBackslash()
        {
            // Arrange - simple JSON without single quotes or backslashes
            var json = @"{""name"":""John"",""age"":30}";

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert - should be unchanged
            Assert.That(result, Is.EqualTo(json));
        }

        #endregion

        #region EscapeJsonForSql - Complex Metadata Tests

        /// <summary>
        /// Verifies that realistic import metadata with Windows paths is properly escaped.
        /// This test simulates the exact scenario from the bug report.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_ComplexMetadata_FullEscape()
        {
            // Arrange - realistic import metadata (as it would come from JsonSerializer)
            var metadata = new Dictionary<string, object>
            {
                ["import_source"] = @"C:\Users\piete\AppData\Local\Temp\DMMS",
                ["import_timestamp"] = "2026-01-16T13:05:50.4134547Z",
                ["is_local_change"] = true
            };
            var json = JsonSerializer.Serialize(metadata);

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert - should contain properly escaped backslashes
            Assert.That(result, Does.Contain(@"C:\\\\Users\\\\piete"));
            Assert.That(result, Does.Contain(@"\\\\Temp\\\\DMMS"));
        }

        /// <summary>
        /// Verifies that metadata with paths escapes backslashes correctly.
        /// This is the primary fix for PP13-77 - Windows paths with backslashes.
        /// </summary>
        [Test]
        public void EscapeJsonForSql_MetadataWithWindowsPath_BackslashesEscaped()
        {
            // Arrange - metadata with Windows path
            var metadata = new Dictionary<string, object>
            {
                ["path"] = @"C:\Users\TestUser\Documents",
                ["description"] = "Test document"
            };
            var json = JsonSerializer.Serialize(metadata);

            // Act
            var result = SqlEscapeUtility.EscapeJsonForSql(json);

            // Assert - backslashes should be doubled for SQL embedding
            Assert.That(result, Does.Contain(@"C:\\\\Users\\\\TestUser\\\\Documents"));
        }

        #endregion

        #region EscapeStringForSql Tests

        /// <summary>
        /// Verifies that single quotes are doubled for plain SQL strings.
        /// </summary>
        [Test]
        public void EscapeStringForSql_SingleQuote_Doubled()
        {
            Assert.That(SqlEscapeUtility.EscapeStringForSql("It's working"), Is.EqualTo("It''s working"));
        }

        /// <summary>
        /// Verifies that backslashes are NOT escaped for plain strings (only single quotes).
        /// Plain strings don't go through JSON parser, so backslashes should remain as-is.
        /// </summary>
        [Test]
        public void EscapeStringForSql_BackslashNotEscaped()
        {
            // For non-JSON content, backslashes should remain as-is
            Assert.That(SqlEscapeUtility.EscapeStringForSql(@"C:\Users"), Is.EqualTo(@"C:\Users"));
        }

        /// <summary>
        /// Verifies that empty string returns empty for plain string escaping.
        /// </summary>
        [Test]
        public void EscapeStringForSql_EmptyString_ReturnsEmpty()
        {
            Assert.That(SqlEscapeUtility.EscapeStringForSql(""), Is.EqualTo(""));
        }

        /// <summary>
        /// Verifies that null returns null for plain string escaping.
        /// </summary>
        [Test]
        public void EscapeStringForSql_NullString_ReturnsNull()
        {
            Assert.That(SqlEscapeUtility.EscapeStringForSql(null), Is.Null);
        }

        /// <summary>
        /// Verifies that strings without single quotes are unchanged.
        /// </summary>
        [Test]
        public void EscapeStringForSql_NoQuotes_Unchanged()
        {
            var input = "Hello World";
            Assert.That(SqlEscapeUtility.EscapeStringForSql(input), Is.EqualTo(input));
        }

        /// <summary>
        /// Verifies that multiple single quotes are all doubled.
        /// </summary>
        [Test]
        public void EscapeStringForSql_MultipleSingleQuotes_AllDoubled()
        {
            Assert.That(SqlEscapeUtility.EscapeStringForSql("'test' and 'more'"), Is.EqualTo("''test'' and ''more''"));
        }

        #endregion

        #region Test Cases with TestCase Attributes

        /// <summary>
        /// Parameterized test for various JSON escape scenarios.
        /// </summary>
        [Test]
        [TestCase(@"{""a"":1}", @"{""a"":1}", Description = "No escaping needed")]
        [TestCase(@"{""a"":""b\\c""}", @"{""a"":""b\\\\c""}", Description = "Single backslash")]
        [TestCase(@"{""a"":""it's""}", @"{""a"":""it''s""}", Description = "Single quote")]
        [TestCase("", "", Description = "Empty string")]
        public void EscapeJsonForSql_VariousInputs(string input, string expected)
        {
            Assert.That(SqlEscapeUtility.EscapeJsonForSql(input), Is.EqualTo(expected));
        }

        /// <summary>
        /// Parameterized test for various plain string escape scenarios.
        /// </summary>
        [Test]
        [TestCase("hello", "hello", Description = "No escaping needed")]
        [TestCase("it's", "it''s", Description = "Single quote")]
        [TestCase("can't won't", "can''t won''t", Description = "Multiple quotes")]
        [TestCase(@"C:\path", @"C:\path", Description = "Backslash not escaped")]
        [TestCase("", "", Description = "Empty string")]
        public void EscapeStringForSql_VariousInputs(string input, string expected)
        {
            Assert.That(SqlEscapeUtility.EscapeStringForSql(input), Is.EqualTo(expected));
        }

        #endregion
    }
}
