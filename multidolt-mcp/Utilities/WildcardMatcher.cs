using System.Text.RegularExpressions;

namespace DMMS.Utilities
{
    /// <summary>
    /// Utility class for wildcard pattern matching.
    /// Supports patterns with '*' wildcards for matching collection and document names.
    /// </summary>
    public static class WildcardMatcher
    {
        /// <summary>
        /// Determines if a value matches a wildcard pattern.
        /// Supports '*' as a wildcard that matches zero or more characters.
        /// </summary>
        /// <param name="pattern">The pattern to match against (may contain '*' wildcards)</param>
        /// <param name="value">The value to test</param>
        /// <returns>True if the value matches the pattern</returns>
        /// <example>
        /// IsMatch("*_summary", "doc_summary") => true
        /// IsMatch("doc_*", "doc_123") => true
        /// IsMatch("project_*_docs", "project_alpha_docs") => true
        /// IsMatch("exact_match", "exact_match") => true
        /// IsMatch("exact_match", "other_value") => false
        /// </example>
        public static bool IsMatch(string pattern, string value)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(value))
                return false;

            // If no wildcard, do exact match
            if (!pattern.Contains('*'))
                return string.Equals(pattern, value, StringComparison.Ordinal);

            // Convert wildcard pattern to regex
            var regexPattern = WildcardToRegex(pattern);
            return Regex.IsMatch(value, regexPattern, RegexOptions.Singleline);
        }

        /// <summary>
        /// Determines if a pattern contains wildcard characters.
        /// </summary>
        /// <param name="pattern">The pattern to check</param>
        /// <returns>True if the pattern contains '*'</returns>
        public static bool HasWildcard(string pattern)
        {
            return !string.IsNullOrEmpty(pattern) && pattern.Contains('*');
        }

        /// <summary>
        /// Filters a collection of values by a wildcard pattern.
        /// Returns all values that match the pattern.
        /// </summary>
        /// <param name="pattern">The pattern to match against</param>
        /// <param name="values">The values to filter</param>
        /// <returns>Enumerable of values that match the pattern</returns>
        public static IEnumerable<string> FilterByPattern(string pattern, IEnumerable<string> values)
        {
            if (string.IsNullOrEmpty(pattern) || values == null)
                return Enumerable.Empty<string>();

            return values.Where(v => IsMatch(pattern, v));
        }

        /// <summary>
        /// Filters a collection of values by multiple wildcard patterns.
        /// Returns all values that match any of the patterns.
        /// </summary>
        /// <param name="patterns">The patterns to match against</param>
        /// <param name="values">The values to filter</param>
        /// <returns>Enumerable of values that match any pattern</returns>
        public static IEnumerable<string> FilterByPatterns(IEnumerable<string> patterns, IEnumerable<string> values)
        {
            if (patterns == null || values == null)
                return Enumerable.Empty<string>();

            var valueList = values.ToList();
            var result = new HashSet<string>();

            foreach (var pattern in patterns)
            {
                foreach (var match in FilterByPattern(pattern, valueList))
                {
                    result.Add(match);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets all matching values for a pattern from a collection.
        /// Returns a list of all values that match the pattern.
        /// </summary>
        /// <param name="pattern">The pattern to match against</param>
        /// <param name="values">The values to search</param>
        /// <returns>List of matching values</returns>
        public static List<string> GetMatches(string pattern, IEnumerable<string> values)
        {
            return FilterByPattern(pattern, values).ToList();
        }

        /// <summary>
        /// Checks if any value in a collection matches the pattern.
        /// </summary>
        /// <param name="pattern">The pattern to match against</param>
        /// <param name="values">The values to check</param>
        /// <returns>True if at least one value matches</returns>
        public static bool AnyMatch(string pattern, IEnumerable<string> values)
        {
            if (string.IsNullOrEmpty(pattern) || values == null)
                return false;

            return values.Any(v => IsMatch(pattern, v));
        }

        /// <summary>
        /// Converts a wildcard pattern to a regex pattern.
        /// Escapes all regex special characters except '*' which is converted to '.*'.
        /// </summary>
        /// <param name="wildcardPattern">The wildcard pattern to convert</param>
        /// <returns>Regex pattern string</returns>
        internal static string WildcardToRegex(string wildcardPattern)
        {
            // Escape all regex special characters except '*'
            var escaped = Regex.Escape(wildcardPattern);

            // Convert escaped \* back to .* for wildcard matching
            var regexPattern = escaped.Replace("\\*", ".*");

            // Anchor to match the entire string
            return $"^{regexPattern}$";
        }

        /// <summary>
        /// Validates that a pattern is well-formed.
        /// A well-formed pattern is non-empty and if it contains wildcards,
        /// they can be converted to valid regex.
        /// </summary>
        /// <param name="pattern">The pattern to validate</param>
        /// <returns>True if the pattern is valid</returns>
        public static bool IsValidPattern(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return false;

            try
            {
                if (HasWildcard(pattern))
                {
                    var regex = WildcardToRegex(pattern);
                    // Try to compile the regex to validate it
                    _ = new Regex(regex);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the type of wildcard pattern.
        /// </summary>
        /// <param name="pattern">The pattern to analyze</param>
        /// <returns>String describing the pattern type</returns>
        public static string GetPatternType(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return "empty";

            if (!HasWildcard(pattern))
                return "exact";

            if (pattern.StartsWith("*") && pattern.EndsWith("*"))
                return "contains";

            if (pattern.StartsWith("*"))
                return "suffix";

            if (pattern.EndsWith("*"))
                return "prefix";

            return "complex";
        }
    }
}
