using System.Text.Json;

namespace Embranch.Utilities
{
    /// <summary>
    /// Utility methods for JSON handling, particularly for Dolt query result parsing.
    /// </summary>
    /// <remarks>
    /// Dolt returns JSON-type columns as embedded JSON structures (objects/arrays),
    /// not as escaped strings. This causes issues when using GetString() on JsonElements
    /// that have ValueKind = Object or Array.
    ///
    /// This utility provides safe extraction methods that handle all JsonValueKind types,
    /// returning appropriate string representations for each.
    ///
    /// Example problem:
    ///   SELECT metadata FROM collections WHERE metadata IS NOT NULL
    ///   Returns: {"key": "value"} as a JsonElement with ValueKind.Object
    ///   Calling GetString() throws InvalidOperationException
    ///
    /// Solution:
    ///   Use GetElementAsString() which calls GetRawText() for Object/Array types
    /// </remarks>
    public static class JsonUtility
    {
        /// <summary>
        /// Safely extracts a string representation from a JsonElement.
        /// Handles JSON columns from Dolt which may return as nested objects rather than strings.
        /// </summary>
        /// <param name="element">The JsonElement to extract a value from</param>
        /// <param name="defaultValue">Default value if element is null/undefined</param>
        /// <returns>String representation of the element's value</returns>
        public static string GetElementAsString(JsonElement element, string defaultValue = "")
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? defaultValue,
                JsonValueKind.Null => defaultValue,
                JsonValueKind.Undefined => defaultValue,
                JsonValueKind.Object => element.GetRawText(),
                JsonValueKind.Array => element.GetRawText(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => defaultValue
            };
        }

        /// <summary>
        /// Safely extracts a string from a JsonElement property.
        /// Returns defaultValue if the property doesn't exist or is null/undefined.
        /// </summary>
        /// <param name="parent">The parent JsonElement containing the property</param>
        /// <param name="propertyName">Name of the property to extract</param>
        /// <param name="defaultValue">Default value if property is missing or null</param>
        /// <returns>String representation of the property value</returns>
        public static string GetPropertyAsString(
            JsonElement parent,
            string propertyName,
            string defaultValue = "")
        {
            if (parent.TryGetProperty(propertyName, out var prop))
            {
                return GetElementAsString(prop, defaultValue);
            }
            return defaultValue;
        }

        /// <summary>
        /// Safely extracts a nullable string from a JsonElement property.
        /// Returns null if property doesn't exist, is null, or is undefined.
        /// </summary>
        /// <param name="parent">The parent JsonElement containing the property</param>
        /// <param name="propertyName">Name of the property to extract</param>
        /// <returns>String representation of the property value, or null</returns>
        public static string? GetPropertyAsNullableString(
            JsonElement parent,
            string propertyName)
        {
            if (parent.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Null || prop.ValueKind == JsonValueKind.Undefined)
                    return null;
                return GetElementAsString(prop, "");
            }
            return null;
        }
    }
}
