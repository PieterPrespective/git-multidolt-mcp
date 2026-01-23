using System;

namespace Embranch.Utilities
{
    /// <summary>
    /// Provides methods for safely escaping strings for SQL embedding.
    /// </summary>
    /// <remarks>
    /// This utility is essential when building SQL queries with string interpolation.
    /// While parameterized queries would be ideal, Dolt CLI has limited support for them.
    ///
    /// JSON Escaping Context:
    /// When JSON is embedded in a SQL string literal, backslashes need double-escaping.
    /// The SQL parser consumes one level of escaping, then the JSON parser needs the rest.
    ///
    /// Example:
    ///   Original: {"path":"C:\Users"}
    ///   After JsonSerializer: {"path":"C:\\Users"}
    ///   After EscapeJsonForSql: {"path":"C:\\\\Users"}
    ///   In SQL: '{"path":"C:\\\\Users"}'
    ///   After SQL parsing: {"path":"C:\\Users"}  -- JSON parser sees valid escape
    ///   SUCCESS: \\ becomes single backslash
    ///
    /// Without proper escaping:
    ///   After SQL parsing: {"path":"C:\Users"}  -- This is what JSON parser sees
    ///   ERROR: \U is not a valid JSON escape sequence!
    /// </remarks>
    public static class SqlEscapeUtility
    {
        /// <summary>
        /// Escapes a JSON string for embedding in a SQL string literal.
        /// This is necessary because JSON strings are first parsed by SQL,
        /// then by the JSON parser. Backslashes need to be double-escaped.
        /// </summary>
        /// <param name="json">The JSON string from JsonSerializer.Serialize()</param>
        /// <returns>SQL-safe JSON string suitable for embedding in '...'</returns>
        /// <example>
        /// var metadata = new Dictionary&lt;string, object&gt; { ["path"] = @"C:\Users" };
        /// var json = JsonSerializer.Serialize(metadata);  // {"path":"C:\\Users"}
        /// var sqlSafe = SqlEscapeUtility.EscapeJsonForSql(json);  // {"path":"C:\\\\Users"}
        /// var sql = $"INSERT INTO tbl (meta) VALUES ('{sqlSafe}')";
        /// </example>
        public static string EscapeJsonForSql(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return json;
            }

            // Order matters: escape backslashes first, then single quotes
            //
            // JSON serialization produces: {"path":"C:\\Users"}  (one backslash as \\)
            // We need SQL to receive: {"path":"C:\\Users"}  (for JSON parser)
            // So we must send: {"path":"C:\\\\Users"}  (double backslash)
            // After SQL parsing, JSON sees: {"path":"C:\\Users"}  (correct!)

            return json
                .Replace("\\", "\\\\")   // Escape backslashes for SQL string embedding
                .Replace("'", "''");      // Escape single quotes for SQL string literals
        }

        /// <summary>
        /// Escapes a plain string value for use in SQL string literals.
        /// Only escapes single quotes (not backslashes).
        /// Use this for non-JSON string values like content, titles, etc.
        /// </summary>
        /// <param name="value">The string value to escape</param>
        /// <returns>SQL-safe string with single quotes doubled</returns>
        public static string EscapeStringForSql(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            // For non-JSON strings, we only need to escape single quotes
            // Backslashes in content are literal and should remain as-is
            return value.Replace("'", "''");
        }
    }
}
