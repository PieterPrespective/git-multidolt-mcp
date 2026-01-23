using NUnit.Framework;
using Embranch.Utilities;

namespace EmbranchTesting.UnitTests
{
    /// <summary>
    /// Unit tests for WildcardMatcher utility class.
    /// Tests prefix, suffix, middle, and exact pattern matching.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public class WildcardMatcherTests
    {
        #region Prefix Wildcard Tests (pattern: *suffix)

        /// <summary>
        /// Verifies that prefix wildcards match values ending with the suffix
        /// </summary>
        [Test]
        public void WildcardMatcher_AsteriskPrefix_MatchesSuffix()
        {
            // Arrange
            var pattern = "*_summary";

            // Act & Assert - should match
            Assert.That(WildcardMatcher.IsMatch(pattern, "doc_summary"), Is.True);
            Assert.That(WildcardMatcher.IsMatch(pattern, "report_summary"), Is.True);
            Assert.That(WildcardMatcher.IsMatch(pattern, "annual_report_summary"), Is.True);
            Assert.That(WildcardMatcher.IsMatch(pattern, "_summary"), Is.True); // * matches empty string

            // Should not match
            Assert.That(WildcardMatcher.IsMatch(pattern, "summary"), Is.False);
            Assert.That(WildcardMatcher.IsMatch(pattern, "doc_summary_final"), Is.False);
            Assert.That(WildcardMatcher.IsMatch(pattern, "summary_doc"), Is.False);
        }

        /// <summary>
        /// Verifies prefix wildcard with various suffixes
        /// </summary>
        [Test]
        [TestCase("*_docs", "project_docs", true)]
        [TestCase("*_docs", "archive_2024_docs", true)]
        [TestCase("*_docs", "_docs", true)]
        [TestCase("*_docs", "docs", false)]
        [TestCase("*_docs", "project_documents", false)]
        public void WildcardMatcher_PrefixWildcard_VariousSuffixes(string pattern, string value, bool expected)
        {
            Assert.That(WildcardMatcher.IsMatch(pattern, value), Is.EqualTo(expected),
                $"Pattern '{pattern}' should {(expected ? "" : "NOT ")}match '{value}'");
        }

        #endregion

        #region Suffix Wildcard Tests (pattern: prefix*)

        /// <summary>
        /// Verifies that suffix wildcards match values starting with the prefix
        /// </summary>
        [Test]
        public void WildcardMatcher_AsteriskSuffix_MatchesPrefix()
        {
            // Arrange
            var pattern = "doc_*";

            // Act & Assert - should match
            Assert.That(WildcardMatcher.IsMatch(pattern, "doc_123"), Is.True);
            Assert.That(WildcardMatcher.IsMatch(pattern, "doc_summary"), Is.True);
            Assert.That(WildcardMatcher.IsMatch(pattern, "doc_"), Is.True); // * matches empty string

            // Should not match
            Assert.That(WildcardMatcher.IsMatch(pattern, "document_123"), Is.False);
            Assert.That(WildcardMatcher.IsMatch(pattern, "my_doc_123"), Is.False);
            Assert.That(WildcardMatcher.IsMatch(pattern, "doc"), Is.False);
        }

        /// <summary>
        /// Verifies suffix wildcard with various prefixes
        /// </summary>
        [Test]
        [TestCase("project_*", "project_alpha", true)]
        [TestCase("project_*", "project_beta_v2", true)]
        [TestCase("project_*", "project_", true)]
        [TestCase("project_*", "my_project_alpha", false)]
        [TestCase("project_*", "project", false)]
        public void WildcardMatcher_SuffixWildcard_VariousPrefixes(string pattern, string value, bool expected)
        {
            Assert.That(WildcardMatcher.IsMatch(pattern, value), Is.EqualTo(expected),
                $"Pattern '{pattern}' should {(expected ? "" : "NOT ")}match '{value}'");
        }

        #endregion

        #region Middle Wildcard Tests (pattern: prefix*suffix)

        /// <summary>
        /// Verifies that middle wildcards match values with correct prefix and suffix
        /// </summary>
        [Test]
        public void WildcardMatcher_MiddleWildcard_MatchesPattern()
        {
            // Arrange
            var pattern = "project_*_docs";

            // Act & Assert - should match
            Assert.That(WildcardMatcher.IsMatch(pattern, "project_alpha_docs"), Is.True);
            Assert.That(WildcardMatcher.IsMatch(pattern, "project_beta_v2_docs"), Is.True);
            Assert.That(WildcardMatcher.IsMatch(pattern, "project__docs"), Is.True); // * matches empty string

            // Should not match
            Assert.That(WildcardMatcher.IsMatch(pattern, "project_alpha_documents"), Is.False);
            Assert.That(WildcardMatcher.IsMatch(pattern, "my_project_alpha_docs"), Is.False);
            Assert.That(WildcardMatcher.IsMatch(pattern, "projectalphadocs"), Is.False);
        }

        /// <summary>
        /// Verifies middle wildcard with various patterns
        /// </summary>
        [Test]
        [TestCase("archive_*_2024", "archive_q1_2024", true)]
        [TestCase("archive_*_2024", "archive_annual_report_2024", true)]
        [TestCase("archive_*_2024", "archive__2024", true)]
        [TestCase("archive_*_2024", "archive_2024", false)]
        [TestCase("archive_*_2024", "archive_q1_2025", false)]
        public void WildcardMatcher_MiddleWildcard_VariousPatterns(string pattern, string value, bool expected)
        {
            Assert.That(WildcardMatcher.IsMatch(pattern, value), Is.EqualTo(expected),
                $"Pattern '{pattern}' should {(expected ? "" : "NOT ")}match '{value}'");
        }

        #endregion

        #region Exact Match Tests

        /// <summary>
        /// Verifies that patterns without wildcards only match exactly
        /// </summary>
        [Test]
        public void WildcardMatcher_ExactMatch_MatchesExactly()
        {
            // Arrange
            var pattern = "doc_123";

            // Act & Assert
            Assert.That(WildcardMatcher.IsMatch(pattern, "doc_123"), Is.True);

            // Should not match anything else
            Assert.That(WildcardMatcher.IsMatch(pattern, "doc_1234"), Is.False);
            Assert.That(WildcardMatcher.IsMatch(pattern, "doc_12"), Is.False);
            Assert.That(WildcardMatcher.IsMatch(pattern, "DOC_123"), Is.False); // Case sensitive
            Assert.That(WildcardMatcher.IsMatch(pattern, " doc_123"), Is.False);
            Assert.That(WildcardMatcher.IsMatch(pattern, "doc_123 "), Is.False);
        }

        #endregion

        #region Multiple Wildcards Tests

        /// <summary>
        /// Verifies that multiple wildcards in a pattern work correctly
        /// </summary>
        [Test]
        public void WildcardMatcher_MultipleWildcards_MatchesCorrectly()
        {
            // Arrange
            var pattern = "*_report_*_2024";

            // Act & Assert
            Assert.That(WildcardMatcher.IsMatch(pattern, "annual_report_q1_2024"), Is.True);
            Assert.That(WildcardMatcher.IsMatch(pattern, "monthly_report_summary_2024"), Is.True);
            Assert.That(WildcardMatcher.IsMatch(pattern, "_report__2024"), Is.True); // * matches empty

            Assert.That(WildcardMatcher.IsMatch(pattern, "report_q1_2024"), Is.False); // missing leading
            Assert.That(WildcardMatcher.IsMatch(pattern, "annual_report_2024"), Is.False); // missing middle
        }

        /// <summary>
        /// Verifies that pattern with only wildcard matches non-empty strings
        /// Note: Empty strings are considered invalid values and don't match
        /// </summary>
        [Test]
        public void WildcardMatcher_WildcardOnly_MatchesNonEmptyStrings()
        {
            // Arrange
            var pattern = "*";

            // Act & Assert
            Assert.That(WildcardMatcher.IsMatch(pattern, "anything"), Is.True);
            Assert.That(WildcardMatcher.IsMatch(pattern, ""), Is.False); // Empty strings are invalid values
            Assert.That(WildcardMatcher.IsMatch(pattern, "complex_long_name_with_many_parts"), Is.True);
        }

        #endregion

        #region Collection Pattern Matching Tests

        /// <summary>
        /// Verifies collection pattern matching with multiple collection names
        /// </summary>
        [Test]
        public void WildcardMatcher_CollectionPattern_MatchesMultiple()
        {
            // Arrange
            var pattern = "archive_*";
            var collections = new List<string>
            {
                "archive_2024",
                "archive_2025",
                "archive_2024_q1",
                "documents",
                "main",
                "old_archive"
            };

            // Act
            var matches = WildcardMatcher.GetMatches(pattern, collections);

            // Assert
            Assert.That(matches.Count, Is.EqualTo(3));
            Assert.That(matches, Contains.Item("archive_2024"));
            Assert.That(matches, Contains.Item("archive_2025"));
            Assert.That(matches, Contains.Item("archive_2024_q1"));
            Assert.That(matches, Does.Not.Contain("documents"));
            Assert.That(matches, Does.Not.Contain("old_archive"));
        }

        /// <summary>
        /// Verifies FilterByPattern returns all matching items
        /// </summary>
        [Test]
        public void WildcardMatcher_FilterByPattern_ReturnsMatches()
        {
            // Arrange
            var pattern = "*_docs";
            var values = new[] { "project_docs", "archive_docs", "main", "my_documents" };

            // Act
            var matches = WildcardMatcher.FilterByPattern(pattern, values).ToList();

            // Assert
            Assert.That(matches.Count, Is.EqualTo(2));
            Assert.That(matches, Contains.Item("project_docs"));
            Assert.That(matches, Contains.Item("archive_docs"));
        }

        /// <summary>
        /// Verifies FilterByPatterns returns matches from any pattern
        /// </summary>
        [Test]
        public void WildcardMatcher_FilterByPatterns_ReturnsAllMatches()
        {
            // Arrange
            var patterns = new[] { "doc_*", "*_summary" };
            var values = new[] { "doc_123", "doc_456", "report_summary", "main", "other" };

            // Act
            var matches = WildcardMatcher.FilterByPatterns(patterns, values).ToList();

            // Assert
            Assert.That(matches.Count, Is.EqualTo(3));
            Assert.That(matches, Contains.Item("doc_123"));
            Assert.That(matches, Contains.Item("doc_456"));
            Assert.That(matches, Contains.Item("report_summary"));
        }

        #endregion

        #region Edge Cases

        /// <summary>
        /// Verifies null and empty handling
        /// </summary>
        [Test]
        public void WildcardMatcher_NullOrEmpty_ReturnsFalse()
        {
            Assert.That(WildcardMatcher.IsMatch(null!, "value"), Is.False);
            Assert.That(WildcardMatcher.IsMatch("", "value"), Is.False);
            Assert.That(WildcardMatcher.IsMatch("pattern", null!), Is.False);
            Assert.That(WildcardMatcher.IsMatch("pattern", ""), Is.False);
            Assert.That(WildcardMatcher.IsMatch(null!, null!), Is.False);
        }

        /// <summary>
        /// Verifies HasWildcard detection
        /// </summary>
        [Test]
        public void WildcardMatcher_HasWildcard_DetectsCorrectly()
        {
            Assert.That(WildcardMatcher.HasWildcard("pattern_*"), Is.True);
            Assert.That(WildcardMatcher.HasWildcard("*_pattern"), Is.True);
            Assert.That(WildcardMatcher.HasWildcard("pattern_*_end"), Is.True);
            Assert.That(WildcardMatcher.HasWildcard("*"), Is.True);

            Assert.That(WildcardMatcher.HasWildcard("no_wildcard"), Is.False);
            Assert.That(WildcardMatcher.HasWildcard(""), Is.False);
            Assert.That(WildcardMatcher.HasWildcard(null!), Is.False);
        }

        /// <summary>
        /// Verifies AnyMatch returns true when at least one value matches
        /// </summary>
        [Test]
        public void WildcardMatcher_AnyMatch_ReturnsTrueWhenAnyMatches()
        {
            var values = new[] { "doc_1", "doc_2", "other" };

            Assert.That(WildcardMatcher.AnyMatch("doc_*", values), Is.True);
            Assert.That(WildcardMatcher.AnyMatch("other", values), Is.True);
            Assert.That(WildcardMatcher.AnyMatch("notfound_*", values), Is.False);
        }

        /// <summary>
        /// Verifies IsValidPattern validates patterns correctly
        /// </summary>
        [Test]
        public void WildcardMatcher_IsValidPattern_ValidatesCorrectly()
        {
            Assert.That(WildcardMatcher.IsValidPattern("valid_pattern"), Is.True);
            Assert.That(WildcardMatcher.IsValidPattern("valid_*_pattern"), Is.True);
            Assert.That(WildcardMatcher.IsValidPattern("*"), Is.True);

            Assert.That(WildcardMatcher.IsValidPattern(""), Is.False);
            Assert.That(WildcardMatcher.IsValidPattern(null!), Is.False);
        }

        /// <summary>
        /// Verifies GetPatternType categorizes patterns correctly
        /// </summary>
        [Test]
        public void WildcardMatcher_GetPatternType_CategorizesCorrectly()
        {
            Assert.That(WildcardMatcher.GetPatternType("exact"), Is.EqualTo("exact"));
            Assert.That(WildcardMatcher.GetPatternType("prefix_*"), Is.EqualTo("prefix"));
            Assert.That(WildcardMatcher.GetPatternType("*_suffix"), Is.EqualTo("suffix"));
            Assert.That(WildcardMatcher.GetPatternType("*contains*"), Is.EqualTo("contains"));
            Assert.That(WildcardMatcher.GetPatternType("pre_*_suf"), Is.EqualTo("complex"));
            Assert.That(WildcardMatcher.GetPatternType(""), Is.EqualTo("empty"));
        }

        #endregion

        #region Special Characters Tests

        /// <summary>
        /// Verifies that regex special characters in patterns are properly escaped
        /// </summary>
        [Test]
        public void WildcardMatcher_SpecialCharacters_EscapedCorrectly()
        {
            // These characters have special meaning in regex and should be escaped
            Assert.That(WildcardMatcher.IsMatch("doc.txt", "doc.txt"), Is.True);
            Assert.That(WildcardMatcher.IsMatch("doc.txt", "docXtxt"), Is.False); // . should not match any char

            Assert.That(WildcardMatcher.IsMatch("file[1]", "file[1]"), Is.True);
            Assert.That(WildcardMatcher.IsMatch("file[1]", "file1"), Is.False);

            Assert.That(WildcardMatcher.IsMatch("test$value", "test$value"), Is.True);
            Assert.That(WildcardMatcher.IsMatch("test^value", "test^value"), Is.True);
        }

        /// <summary>
        /// Verifies wildcard with special characters works correctly
        /// </summary>
        [Test]
        public void WildcardMatcher_WildcardWithSpecialChars_MatchesCorrectly()
        {
            Assert.That(WildcardMatcher.IsMatch("*.txt", "document.txt"), Is.True);
            Assert.That(WildcardMatcher.IsMatch("file[*].log", "file[123].log"), Is.True);
            Assert.That(WildcardMatcher.IsMatch("path/to/*", "path/to/file"), Is.True);
        }

        #endregion
    }
}
