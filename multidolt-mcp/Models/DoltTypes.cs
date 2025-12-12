namespace DMMS.Models
{
    // ==================== Result Types ====================

    /// <summary>
    /// Result from executing a Dolt CLI command
    /// </summary>
    public record DoltCommandResult(bool Success, string Output, string Error, int ExitCode);

    /// <summary>
    /// Result from committing changes
    /// </summary>
    public record CommitResult(bool Success, string CommitHash, string Message);

    /// <summary>
    /// Result from pulling changes from remote
    /// </summary>
    public record PullResult(bool Success, bool WasFastForward, bool HasConflicts, string Message);

    /// <summary>
    /// Result from merging branches
    /// </summary>
    public record MergeResult(bool Success, bool HasConflicts, string? MergeCommitHash, string Message);

    // ==================== Information Types ====================

    /// <summary>
    /// Information about a Dolt branch
    /// </summary>
    public record BranchInfo(string Name, bool IsCurrent, string LastCommitHash);

    /// <summary>
    /// Information about a Dolt commit
    /// </summary>
    public record CommitInfo(string Hash, string Message, string Author, DateTime Date);

    /// <summary>
    /// Information about a remote repository
    /// </summary>
    public record RemoteInfo(string Name, string Url);

    /// <summary>
    /// A row from a Dolt diff operation
    /// </summary>
    public class DiffRow
    {
        public string DiffType { get; set; } = "";           // "added", "modified", "removed"
        public string SourceId { get; set; } = "";
        public string FromContentHash { get; set; } = "";
        public string ToContentHash { get; set; } = "";
        public string ToContent { get; set; } = "";
        public string ToMetadata { get; set; } = "";         // JSON metadata
        public Dictionary<string, object> Metadata { get; set; } = new();
        
        public DiffRow() { }
        
        public DiffRow(string diffType, string sourceId, string fromContentHash, 
            string toContentHash, string toContent, Dictionary<string, object> metadata)
        {
            DiffType = diffType;
            SourceId = sourceId;
            FromContentHash = fromContentHash;
            ToContentHash = toContentHash;
            ToContent = toContent;
            Metadata = metadata;
        }
    }

    /// <summary>
    /// Information about a merge conflict
    /// </summary>
    public record ConflictInfo(
        string TableName,
        string RowId,
        Dictionary<string, object> OurValues,
        Dictionary<string, object> TheirValues,
        Dictionary<string, object> BaseValues
    );

    /// <summary>
    /// Strategy for resolving conflicts
    /// </summary>
    public enum ConflictResolution 
    { 
        /// <summary>Use our version</summary>
        Ours, 
        /// <summary>Use their version</summary>
        Theirs 
    }

    /// <summary>
    /// Current status of the repository
    /// </summary>
    public record RepositoryStatus(
        string Branch,
        bool HasStagedChanges,
        bool HasUnstagedChanges,
        IEnumerable<string> StagedTables,
        IEnumerable<string> ModifiedTables
    );

    /// <summary>
    /// Summary of differences between commits
    /// </summary>
    public record DiffSummary(
        int TablesChanged,
        int RowsAdded,
        int RowsModified,
        int RowsDeleted
    );
}