using System.ComponentModel;

namespace DMMS.Models
{
    /// <summary>
    /// Detailed merge conflict information with GUID for tracking
    /// </summary>
    public class DetailedConflictInfo
    {
        /// <summary>
        /// Unique identifier for tracking this conflict across tool calls
        /// </summary>
        public string ConflictId { get; set; } = string.Empty;
        
        /// <summary>
        /// Name of the collection containing the conflicted document
        /// </summary>
        public string Collection { get; set; } = string.Empty;
        
        /// <summary>
        /// Unique identifier of the document in conflict
        /// </summary>
        public string DocumentId { get; set; } = string.Empty;
        
        /// <summary>
        /// Type of conflict detected
        /// </summary>
        public ConflictType Type { get; set; }
        
        /// <summary>
        /// Whether this conflict can be automatically resolved
        /// </summary>
        public bool AutoResolvable { get; set; }
        
        /// <summary>
        /// System-suggested resolution strategy
        /// </summary>
        public string SuggestedResolution { get; set; } = string.Empty;
        
        /// <summary>
        /// Field-level conflict details
        /// </summary>
        public List<FieldConflict> FieldConflicts { get; set; } = new();
        
        /// <summary>
        /// Available resolution options for this conflict
        /// </summary>
        public List<string> ResolutionOptions { get; set; } = new();
        
        /// <summary>
        /// Base values from the common ancestor
        /// </summary>
        public Dictionary<string, object> BaseValues { get; set; } = new();
        
        /// <summary>
        /// Values from our branch
        /// </summary>
        public Dictionary<string, object> OurValues { get; set; } = new();
        
        /// <summary>
        /// Values from their branch (source branch)
        /// </summary>
        public Dictionary<string, object> TheirValues { get; set; } = new();

        /// <summary>
        /// Document content from the merge base (common ancestor)
        /// </summary>
        public string? BaseContent { get; set; }

        /// <summary>
        /// Document content from our branch (target branch)
        /// </summary>
        public string? OursContent { get; set; }

        /// <summary>
        /// Document content from their branch (source branch)
        /// </summary>
        public string? TheirsContent { get; set; }

        /// <summary>
        /// Content hash from the merge base for change detection
        /// </summary>
        public string? BaseContentHash { get; set; }

        /// <summary>
        /// Content hash from our branch for change detection
        /// </summary>
        public string? OursContentHash { get; set; }

        /// <summary>
        /// Content hash from their branch for change detection
        /// </summary>
        public string? TheirsContentHash { get; set; }
    }

    /// <summary>
    /// Field-level conflict information for precise conflict resolution
    /// </summary>
    public class FieldConflict
    {
        /// <summary>
        /// Name of the conflicted field
        /// </summary>
        public string FieldName { get; set; } = string.Empty;
        
        /// <summary>
        /// Value in the base (common ancestor) version
        /// </summary>
        public object? BaseValue { get; set; }
        
        /// <summary>
        /// Value in our branch
        /// </summary>
        public object? OurValue { get; set; }
        
        /// <summary>
        /// Value in their branch (source branch)
        /// </summary>
        public object? TheirValue { get; set; }
        
        /// <summary>
        /// Hash of the base value for change detection
        /// </summary>
        public string BaseHash { get; set; } = string.Empty;
        
        /// <summary>
        /// Hash of our value for change detection
        /// </summary>
        public string OurHash { get; set; } = string.Empty;
        
        /// <summary>
        /// Hash of their value for change detection
        /// </summary>
        public string TheirHash { get; set; } = string.Empty;
        
        /// <summary>
        /// Whether this field conflict can be automatically merged
        /// </summary>
        public bool CanAutoMerge { get; set; }
    }

    /// <summary>
    /// Result of merge preview analysis
    /// </summary>
    public class MergePreviewResult
    {
        /// <summary>
        /// Whether the preview analysis succeeded
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// Whether the merge can be performed automatically without user intervention
        /// </summary>
        public bool CanAutoMerge { get; set; }
        
        /// <summary>
        /// Source branch being merged from
        /// </summary>
        public string SourceBranch { get; set; } = string.Empty;
        
        /// <summary>
        /// Target branch being merged into
        /// </summary>
        public string TargetBranch { get; set; } = string.Empty;
        
        /// <summary>
        /// Preview information about changes
        /// </summary>
        public MergePreviewInfo? Preview { get; set; }
        
        /// <summary>
        /// List of detected conflicts (filtered based on request)
        /// </summary>
        public List<DetailedConflictInfo> Conflicts { get; set; } = new();
        
        /// <summary>
        /// Total number of conflicts detected before any filtering
        /// </summary>
        public int TotalConflictsDetected { get; set; }
        
        /// <summary>
        /// Status of auxiliary tables
        /// </summary>
        public AuxiliaryTableStatus? AuxiliaryStatus { get; set; }
        
        /// <summary>
        /// Recommended action for the user
        /// </summary>
        public string RecommendedAction { get; set; } = string.Empty;
        
        /// <summary>
        /// Human-readable message about the preview results
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Preview information about merge changes
    /// </summary>
    public class MergePreviewInfo
    {
        /// <summary>
        /// Number of documents that would be added
        /// </summary>
        public int DocumentsAdded { get; set; }
        
        /// <summary>
        /// Number of documents that would be modified
        /// </summary>
        public int DocumentsModified { get; set; }
        
        /// <summary>
        /// Number of documents that would be deleted
        /// </summary>
        public int DocumentsDeleted { get; set; }
        
        /// <summary>
        /// Number of collections that would be affected
        /// </summary>
        public int CollectionsAffected { get; set; }
    }

    /// <summary>
    /// Status information about auxiliary tables
    /// </summary>
    public class AuxiliaryTableStatus
    {
        /// <summary>
        /// Whether sync state table has conflicts
        /// </summary>
        public bool SyncStateConflict { get; set; }
        
        /// <summary>
        /// Whether local changes table has conflicts
        /// </summary>
        public bool LocalChangesConflict { get; set; }
        
        /// <summary>
        /// Whether sync operations table has conflicts
        /// </summary>
        public bool SyncOperationsConflict { get; set; }
    }

    /// <summary>
    /// User-specified conflict resolution request
    /// </summary>
    public class ConflictResolutionRequest
    {
        /// <summary>
        /// The conflict ID being resolved
        /// </summary>
        public string ConflictId { get; set; } = string.Empty;
        
        /// <summary>
        /// Type of resolution to apply
        /// </summary>
        public ResolutionType ResolutionType { get; set; }
        
        /// <summary>
        /// Field-level resolution choices (for field merge)
        /// </summary>
        public Dictionary<string, string> FieldResolutions { get; set; } = new();
        
        /// <summary>
        /// Custom values to use (for custom resolution)
        /// </summary>
        public Dictionary<string, object> CustomValues { get; set; } = new();
    }

    /// <summary>
    /// Container for multiple conflict resolutions with default strategy
    /// </summary>
    public class ConflictResolutionData
    {
        /// <summary>
        /// List of specific conflict resolutions
        /// </summary>
        public List<ConflictResolutionRequest> Resolutions { get; set; } = new();
        
        /// <summary>
        /// Default strategy for unspecified conflicts
        /// </summary>
        public string DefaultStrategy { get; set; } = "ours";
    }

    /// <summary>
    /// Types of resolution strategies available
    /// </summary>
    public enum ResolutionType
    {
        /// <summary>
        /// Keep our version (target branch)
        /// </summary>
        KeepOurs,
        
        /// <summary>
        /// Keep their version (source branch)
        /// </summary>
        KeepTheirs,
        
        /// <summary>
        /// Merge at field level with specified preferences
        /// </summary>
        FieldMerge,
        
        /// <summary>
        /// Use custom provided values
        /// </summary>
        Custom,
        
        /// <summary>
        /// Let system automatically resolve
        /// </summary>
        AutoResolve
    }

    /// <summary>
    /// Content comparison data for a document across branches
    /// </summary>
    public class ContentComparison
    {
        /// <summary>
        /// Table/collection name containing the document
        /// </summary>
        public string TableName { get; set; } = string.Empty;
        
        /// <summary>
        /// Document identifier
        /// </summary>
        public string DocumentId { get; set; } = string.Empty;
        
        /// <summary>
        /// Content from the base/common ancestor
        /// </summary>
        public DocumentContent? BaseContent { get; set; }
        
        /// <summary>
        /// Content from the source branch
        /// </summary>
        public DocumentContent? SourceContent { get; set; }
        
        /// <summary>
        /// Content from the target branch
        /// </summary>
        public DocumentContent? TargetContent { get; set; }
        
        /// <summary>
        /// Whether there are conflicts between source and target
        /// </summary>
        public bool HasConflicts { get; set; }
        
        /// <summary>
        /// List of fields that have conflicts
        /// </summary>
        public List<string> ConflictingFields { get; set; } = new();
        
        /// <summary>
        /// Suggested resolution based on content analysis
        /// </summary>
        public string SuggestedResolution { get; set; } = string.Empty;
    }

    /// <summary>
    /// Document content with metadata
    /// </summary>
    public class DocumentContent
    {
        /// <summary>
        /// The main content/body of the document
        /// </summary>
        public string? Content { get; set; }
        
        /// <summary>
        /// Document metadata as key-value pairs
        /// </summary>
        public Dictionary<string, object> Metadata { get; set; } = new();
        
        /// <summary>
        /// Last modified timestamp if available
        /// </summary>
        public DateTime? LastModified { get; set; }
        
        /// <summary>
        /// Commit hash where this version exists
        /// </summary>
        public string CommitHash { get; set; } = string.Empty;
        
        /// <summary>
        /// Whether this document exists in this branch
        /// </summary>
        public bool Exists { get; set; } = true;
    }

    /// <summary>
    /// Resolution preview showing what would happen with each resolution option
    /// </summary>
    public class ResolutionPreview
    {
        /// <summary>
        /// The conflict ID this preview is for
        /// </summary>
        public string ConflictId { get; set; } = string.Empty;
        
        /// <summary>
        /// Resolution type being previewed
        /// </summary>
        public ResolutionType ResolutionType { get; set; }
        
        /// <summary>
        /// The resulting document content after resolution
        /// </summary>
        public DocumentContent ResultingContent { get; set; } = new();
        
        /// <summary>
        /// Fields that would be lost/overwritten
        /// </summary>
        public List<string> DataLossWarnings { get; set; } = new();
        
        /// <summary>
        /// Confidence level for auto-merge (0-100)
        /// </summary>
        public int ConfidenceLevel { get; set; }
        
        /// <summary>
        /// Human-readable description of what this resolution does
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// PP13-73-C3: Result of batch conflict resolution.
    /// Contains overall success status and per-conflict resolution details.
    /// </summary>
    public class BatchResolutionResult
    {
        /// <summary>
        /// Whether all conflicts were successfully resolved
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Total number of conflicts that were attempted
        /// </summary>
        public int TotalAttempted { get; set; }

        /// <summary>
        /// Number of conflicts successfully resolved
        /// </summary>
        public int SuccessfullyResolved { get; set; }

        /// <summary>
        /// Number of conflicts that failed to resolve
        /// </summary>
        public int FailedCount { get; set; }

        /// <summary>
        /// Error message if batch resolution failed
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Per-conflict resolution results
        /// </summary>
        public List<ConflictResolutionOutcome> ResolutionOutcomes { get; set; } = new();
    }

    /// <summary>
    /// PP13-73-C3: Outcome of resolving a single conflict within a batch.
    /// </summary>
    public class ConflictResolutionOutcome
    {
        /// <summary>
        /// The conflict ID that was resolved
        /// </summary>
        public string ConflictId { get; set; } = string.Empty;

        /// <summary>
        /// Document ID of the resolved conflict
        /// </summary>
        public string DocumentId { get; set; } = string.Empty;

        /// <summary>
        /// Collection name of the resolved conflict
        /// </summary>
        public string CollectionName { get; set; } = string.Empty;

        /// <summary>
        /// Resolution strategy that was applied
        /// </summary>
        public ResolutionType ResolutionType { get; set; }

        /// <summary>
        /// Whether this individual resolution succeeded
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Error message if this resolution failed (only set on failure)
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Types of conflicts that can occur during merge
    /// </summary>
    public enum ConflictType
    {
        /// <summary>
        /// Same document modified in both branches
        /// </summary>
        ContentModification,
        
        /// <summary>
        /// Metadata conflicts (timestamps, etc.)
        /// </summary>
        MetadataConflict,
        
        /// <summary>
        /// Both branches added same document ID
        /// </summary>
        AddAdd,
        
        /// <summary>
        /// One branch deleted, other modified
        /// </summary>
        DeleteModify,
        
        /// <summary>
        /// Structural schema changes conflict
        /// </summary>
        SchemaConflict
    }

}