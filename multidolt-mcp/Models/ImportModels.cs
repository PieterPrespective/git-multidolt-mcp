using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DMMS.Models
{
    #region Filter Models

    /// <summary>
    /// Represents an import filter configuration parsed from JSON.
    /// Used to specify which collections and documents to import from an external ChromaDB database.
    /// </summary>
    public record ImportFilter
    {
        /// <summary>
        /// Collection import specifications as an array.
        /// Array format enables:
        /// - Wildcard matching on collection names
        /// - Multiple source collections mapping to the same target (consolidation)
        /// - Ordered processing of collection mappings
        /// </summary>
        [JsonPropertyName("collections")]
        public List<CollectionImportSpec>? Collections { get; init; }

        /// <summary>
        /// Returns true if this is an empty filter (import all collections)
        /// </summary>
        [JsonIgnore]
        public bool IsImportAll => Collections == null || Collections.Count == 0;

        /// <summary>
        /// Gets all unique target collection names from the filter
        /// </summary>
        public HashSet<string> GetTargetCollections()
        {
            if (Collections == null) return new HashSet<string>();
            return Collections.Select(c => c.ImportInto).ToHashSet();
        }
    }

    /// <summary>
    /// Specification for importing collection(s) - supports wildcards in name.
    /// Defines mapping from source collection(s) in external database to target collection in local database.
    /// </summary>
    public record CollectionImportSpec
    {
        /// <summary>
        /// Source collection name pattern in external database.
        /// Supports wildcards: * matches zero or more characters.
        /// Examples: "project_*", "*_docs", "archive_2024_*"
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Target collection name in local database.
        /// Multiple source collections can map to the same target,
        /// enabling collection consolidation during import.
        /// </summary>
        [JsonPropertyName("import_into")]
        public string ImportInto { get; init; } = string.Empty;

        /// <summary>
        /// Document ID patterns to import (null = all documents).
        /// Supports wildcards: * matches zero or more characters.
        /// Examples: ["*_summary", "doc_*", "specific_doc_id"]
        /// </summary>
        [JsonPropertyName("documents")]
        public List<string>? Documents { get; init; }

        /// <summary>
        /// Returns true if the Name field contains wildcards
        /// </summary>
        [JsonIgnore]
        public bool HasCollectionWildcard => Name.Contains('*');

        /// <summary>
        /// Returns true if document filtering is specified
        /// </summary>
        [JsonIgnore]
        public bool HasDocumentFilter => Documents != null && Documents.Count > 0;
    }

    #endregion

    #region External Database Models

    /// <summary>
    /// Result of validating an external ChromaDB database.
    /// Contains information about whether the database is valid and accessible.
    /// </summary>
    public record ExternalDbValidationResult
    {
        /// <summary>
        /// Whether the external database is valid and accessible
        /// </summary>
        public bool IsValid { get; init; }

        /// <summary>
        /// Error message if validation failed
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Full path to the external database
        /// </summary>
        public string DbPath { get; init; } = string.Empty;

        /// <summary>
        /// Number of collections in the external database
        /// </summary>
        public int CollectionCount { get; init; }

        /// <summary>
        /// Total number of documents across all collections
        /// </summary>
        public long TotalDocuments { get; init; }
    }

    /// <summary>
    /// Information about a collection in an external ChromaDB database.
    /// Used when listing collections from the source database.
    /// </summary>
    public record ExternalCollectionInfo
    {
        /// <summary>
        /// Name of the collection
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Number of documents in the collection
        /// </summary>
        public int DocumentCount { get; init; }

        /// <summary>
        /// Collection metadata (if any)
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// A document from an external ChromaDB database.
    /// Contains all document data needed for import and conflict detection.
    /// </summary>
    public record ExternalDocument
    {
        /// <summary>
        /// Unique identifier of the document
        /// </summary>
        public string DocId { get; init; } = string.Empty;

        /// <summary>
        /// Name of the collection this document belongs to
        /// </summary>
        public string CollectionName { get; init; } = string.Empty;

        /// <summary>
        /// Document content/body text
        /// </summary>
        public string Content { get; init; } = string.Empty;

        /// <summary>
        /// SHA-256 hash of the content for comparison
        /// </summary>
        public string ContentHash { get; init; } = string.Empty;

        /// <summary>
        /// Document metadata
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }

    #endregion

    #region Conflict Models

    /// <summary>
    /// Types of import conflicts that can occur when importing documents
    /// </summary>
    public enum ImportConflictType
    {
        /// <summary>
        /// Document exists in both source and target with different content
        /// </summary>
        ContentModification,

        /// <summary>
        /// Document exists in both with different metadata only
        /// </summary>
        MetadataConflict,

        /// <summary>
        /// Collection exists in target with different structure/metadata
        /// </summary>
        CollectionMismatch,

        /// <summary>
        /// Document ID collision with different base documents
        /// </summary>
        IdCollision
    }

    /// <summary>
    /// Detailed information about an import conflict.
    /// Contains all data needed for conflict resolution.
    /// </summary>
    public record ImportConflictInfo
    {
        /// <summary>
        /// Unique identifier for this conflict (for resolution reference).
        /// Generated using SHA-256 hash of sourceCol + targetCol + docId + type.
        /// Format: imp_[12-char-hex]
        /// </summary>
        public string ConflictId { get; init; } = string.Empty;

        /// <summary>
        /// Source collection name in external database
        /// </summary>
        public string SourceCollection { get; init; } = string.Empty;

        /// <summary>
        /// Target collection name in local database
        /// </summary>
        public string TargetCollection { get; init; } = string.Empty;

        /// <summary>
        /// Document ID involved in conflict
        /// </summary>
        public string DocumentId { get; init; } = string.Empty;

        /// <summary>
        /// Type of conflict detected
        /// </summary>
        public ImportConflictType Type { get; init; }

        /// <summary>
        /// Whether this conflict can be auto-resolved
        /// </summary>
        public bool AutoResolvable { get; init; }

        /// <summary>
        /// Content from source (external) database
        /// </summary>
        public string? SourceContent { get; init; }

        /// <summary>
        /// Content from target (local) database
        /// </summary>
        public string? TargetContent { get; init; }

        /// <summary>
        /// Content hash from source for comparison
        /// </summary>
        public string? SourceContentHash { get; init; }

        /// <summary>
        /// Content hash from target for comparison
        /// </summary>
        public string? TargetContentHash { get; init; }

        /// <summary>
        /// Suggested resolution strategy
        /// </summary>
        public string? SuggestedResolution { get; init; }

        /// <summary>
        /// Available resolution options for this conflict
        /// </summary>
        public List<string> ResolutionOptions { get; init; } = new();

        /// <summary>
        /// Metadata from source document
        /// </summary>
        public Dictionary<string, object>? SourceMetadata { get; init; }

        /// <summary>
        /// Metadata from target document
        /// </summary>
        public Dictionary<string, object>? TargetMetadata { get; init; }
    }

    /// <summary>
    /// Resolution types for import conflicts
    /// </summary>
    public enum ImportResolutionType
    {
        /// <summary>
        /// Keep the source (external) version - overwrite local
        /// </summary>
        KeepSource,

        /// <summary>
        /// Keep the target (local) version - ignore external
        /// </summary>
        KeepTarget,

        /// <summary>
        /// Merge fields from both versions
        /// </summary>
        Merge,

        /// <summary>
        /// Skip this document entirely - don't import
        /// </summary>
        Skip,

        /// <summary>
        /// Apply custom content provided by user
        /// </summary>
        Custom
    }

    /// <summary>
    /// Resolution specification for an import conflict.
    /// Used to tell the executor how to handle a specific conflict.
    /// </summary>
    public record ImportConflictResolution
    {
        /// <summary>
        /// ID of the conflict to resolve (from preview)
        /// </summary>
        [JsonPropertyName("conflict_id")]
        public string ConflictId { get; init; } = string.Empty;

        /// <summary>
        /// Type of resolution to apply
        /// </summary>
        [JsonPropertyName("resolution_type")]
        public ImportResolutionType ResolutionType { get; init; }

        /// <summary>
        /// Custom content (when ResolutionType is Custom)
        /// </summary>
        [JsonPropertyName("custom_content")]
        public string? CustomContent { get; init; }

        /// <summary>
        /// Custom metadata (when ResolutionType is Custom)
        /// </summary>
        [JsonPropertyName("custom_metadata")]
        public Dictionary<string, object>? CustomMetadata { get; init; }
    }

    /// <summary>
    /// Container for conflict resolutions parsed from JSON input
    /// </summary>
    public record ImportResolutionData
    {
        /// <summary>
        /// List of specific conflict resolutions
        /// </summary>
        [JsonPropertyName("resolutions")]
        public List<ImportConflictResolution>? Resolutions { get; init; }

        /// <summary>
        /// Default strategy for unspecified conflicts
        /// </summary>
        [JsonPropertyName("default_strategy")]
        public string DefaultStrategy { get; init; } = "keep_source";
    }

    #endregion

    #region Result Models

    /// <summary>
    /// Result of analyzing an import operation (preview).
    /// Contains all information needed to decide how to proceed with import.
    /// </summary>
    public record ImportPreviewResult
    {
        /// <summary>
        /// Whether the preview analysis succeeded
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Error message if preview failed
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Path to the source external database
        /// </summary>
        public string SourcePath { get; init; } = string.Empty;

        /// <summary>
        /// Whether the import can proceed without conflicts (auto-import)
        /// </summary>
        public bool CanAutoImport { get; init; }

        /// <summary>
        /// Total number of conflicts detected
        /// </summary>
        public int TotalConflicts { get; init; }

        /// <summary>
        /// Number of conflicts that can be auto-resolved
        /// </summary>
        public int AutoResolvableConflicts { get; init; }

        /// <summary>
        /// Number of conflicts requiring manual resolution
        /// </summary>
        public int ManualConflicts { get; init; }

        /// <summary>
        /// Detailed conflict information
        /// </summary>
        public List<ImportConflictInfo> Conflicts { get; init; } = new();

        /// <summary>
        /// Preview of changes that will be made
        /// </summary>
        public ImportChangesPreview? Preview { get; init; }

        /// <summary>
        /// Recommended action based on analysis
        /// </summary>
        public string? RecommendedAction { get; init; }

        /// <summary>
        /// Human-readable message about the preview
        /// </summary>
        public string? Message { get; init; }
    }

    /// <summary>
    /// Preview of changes from an import operation.
    /// Shows counts of what will be added, updated, or skipped.
    /// </summary>
    public record ImportChangesPreview
    {
        /// <summary>
        /// Number of new documents to add
        /// </summary>
        public int DocumentsToAdd { get; init; }

        /// <summary>
        /// Number of existing documents to update
        /// </summary>
        public int DocumentsToUpdate { get; init; }

        /// <summary>
        /// Number of documents that will be skipped
        /// </summary>
        public int DocumentsToSkip { get; init; }

        /// <summary>
        /// Number of new collections to create
        /// </summary>
        public int CollectionsToCreate { get; init; }

        /// <summary>
        /// Number of existing collections that will be updated
        /// </summary>
        public int CollectionsToUpdate { get; init; }

        /// <summary>
        /// List of collection names that will be affected
        /// </summary>
        public List<string> AffectedCollections { get; init; } = new();
    }

    /// <summary>
    /// Result of executing an import operation.
    /// Contains statistics about what was imported and any issues encountered.
    /// </summary>
    public record ImportExecutionResult
    {
        /// <summary>
        /// Whether the import execution succeeded
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Error message if execution failed
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Path to the source external database
        /// </summary>
        public string SourcePath { get; init; } = string.Empty;

        /// <summary>
        /// Number of documents successfully imported
        /// </summary>
        public int DocumentsImported { get; init; }

        /// <summary>
        /// Number of documents updated (due to conflict resolution)
        /// </summary>
        public int DocumentsUpdated { get; init; }

        /// <summary>
        /// Number of documents skipped
        /// </summary>
        public int DocumentsSkipped { get; init; }

        /// <summary>
        /// Number of collections created
        /// </summary>
        public int CollectionsCreated { get; init; }

        /// <summary>
        /// Number of conflicts that were resolved
        /// </summary>
        public int ConflictsResolved { get; init; }

        /// <summary>
        /// Breakdown of resolution types used
        /// </summary>
        public Dictionary<string, int>? ResolutionBreakdown { get; init; }

        /// <summary>
        /// Commit hash after import (if staged to Dolt)
        /// </summary>
        public string? CommitHash { get; init; }

        /// <summary>
        /// Human-readable summary of the import
        /// </summary>
        public string? Message { get; init; }
    }

    #endregion

    #region Utility Class

    /// <summary>
    /// Utility class for import-related operations.
    /// Provides static methods for conflict ID generation and other common operations.
    /// </summary>
    public static class ImportUtility
    {
        /// <summary>
        /// Generates a deterministic conflict ID for import conflicts.
        /// Uses SHA-256 hash to ensure consistency between preview and execute operations.
        /// Format: imp_[12-char-hex]
        /// </summary>
        /// <param name="sourceCollection">Source collection name in external database</param>
        /// <param name="targetCollection">Target collection name in local database</param>
        /// <param name="documentId">Document ID involved in conflict</param>
        /// <param name="type">Type of conflict</param>
        /// <returns>Deterministic conflict ID string</returns>
        public static string GenerateImportConflictId(
            string sourceCollection,
            string targetCollection,
            string documentId,
            ImportConflictType type)
        {
            var input = $"{sourceCollection}_{targetCollection}_{documentId}_{type}";
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            var hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            return $"imp_{hashHex[..12]}";
        }

        /// <summary>
        /// Computes SHA-256 hash of content for comparison.
        /// Used for detecting content modifications between source and target.
        /// </summary>
        /// <param name="content">Content to hash</param>
        /// <returns>Hex string representation of SHA-256 hash</returns>
        public static string ComputeContentHash(string content)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Parses a resolution type string to ImportResolutionType enum.
        /// Handles various string formats (keep_source, keepsource, KeepSource, etc.)
        /// </summary>
        /// <param name="resolutionString">String representation of resolution type</param>
        /// <returns>Parsed ImportResolutionType</returns>
        public static ImportResolutionType ParseResolutionType(string resolutionString)
        {
            if (string.IsNullOrWhiteSpace(resolutionString))
                return ImportResolutionType.KeepSource;

            var normalized = resolutionString.Replace("_", "").ToLowerInvariant();
            return normalized switch
            {
                "keepsource" => ImportResolutionType.KeepSource,
                "source" => ImportResolutionType.KeepSource,
                "keeptarget" => ImportResolutionType.KeepTarget,
                "target" => ImportResolutionType.KeepTarget,
                "merge" => ImportResolutionType.Merge,
                "skip" => ImportResolutionType.Skip,
                "custom" => ImportResolutionType.Custom,
                _ => ImportResolutionType.KeepSource
            };
        }

        /// <summary>
        /// Gets the default resolution options for a conflict type
        /// </summary>
        /// <param name="type">Type of conflict</param>
        /// <returns>List of available resolution options as strings</returns>
        public static List<string> GetResolutionOptions(ImportConflictType type)
        {
            return type switch
            {
                ImportConflictType.ContentModification => new List<string>
                    { "keep_source", "keep_target", "merge", "skip", "custom" },
                ImportConflictType.MetadataConflict => new List<string>
                    { "keep_source", "keep_target", "merge", "skip" },
                ImportConflictType.CollectionMismatch => new List<string>
                    { "keep_source", "keep_target", "skip" },
                ImportConflictType.IdCollision => new List<string>
                    { "keep_source", "keep_target", "skip" },
                _ => new List<string> { "keep_source", "keep_target", "skip" }
            };
        }

        /// <summary>
        /// Determines if a conflict type is auto-resolvable by default
        /// </summary>
        /// <param name="type">Type of conflict</param>
        /// <returns>True if auto-resolvable</returns>
        public static bool IsAutoResolvable(ImportConflictType type)
        {
            return type switch
            {
                ImportConflictType.MetadataConflict => true,
                _ => false
            };
        }

        /// <summary>
        /// Gets the suggested resolution for a conflict type
        /// </summary>
        /// <param name="type">Type of conflict</param>
        /// <returns>Suggested resolution string</returns>
        public static string GetSuggestedResolution(ImportConflictType type)
        {
            return type switch
            {
                ImportConflictType.ContentModification => "keep_source",
                ImportConflictType.MetadataConflict => "keep_source",
                ImportConflictType.CollectionMismatch => "keep_target",
                ImportConflictType.IdCollision => "skip",
                _ => "keep_source"
            };
        }
    }

    #endregion
}
