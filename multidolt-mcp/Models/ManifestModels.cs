using System.Text.Json.Serialization;

namespace DMMS.Models;

/// <summary>
/// PP13-79: Root manifest model for .dmms/state.json
/// Tracks Dolt repository state and Git-Dolt commit mappings
/// </summary>
public record DmmsManifest
{
    /// <summary>
    /// Schema version for forward compatibility
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0";

    /// <summary>
    /// Dolt repository configuration and state
    /// </summary>
    [JsonPropertyName("dolt")]
    public DoltManifestConfig Dolt { get; init; } = new();

    /// <summary>
    /// Git-Dolt commit mapping configuration
    /// </summary>
    [JsonPropertyName("git_mapping")]
    public GitMappingConfig GitMapping { get; init; } = new();

    /// <summary>
    /// Initialization behavior configuration
    /// </summary>
    [JsonPropertyName("initialization")]
    public InitializationConfig Initialization { get; init; } = new();

    /// <summary>
    /// Collection tracking configuration
    /// </summary>
    [JsonPropertyName("collections")]
    public CollectionTrackingConfig Collections { get; init; } = new();

    /// <summary>
    /// Last update timestamp
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// User or system that last updated the manifest
    /// </summary>
    [JsonPropertyName("updated_by")]
    public string? UpdatedBy { get; init; }
}

/// <summary>
/// PP13-79: Dolt repository configuration in manifest
/// </summary>
public record DoltManifestConfig
{
    /// <summary>
    /// Remote Dolt repository URL (e.g., "dolthub.com/org/repo")
    /// </summary>
    [JsonPropertyName("remote_url")]
    public string? RemoteUrl { get; init; }

    /// <summary>
    /// Default branch for the repository
    /// </summary>
    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; init; } = "main";

    /// <summary>
    /// Current Dolt commit hash
    /// </summary>
    [JsonPropertyName("current_commit")]
    public string? CurrentCommit { get; init; }

    /// <summary>
    /// Current Dolt branch name
    /// </summary>
    [JsonPropertyName("current_branch")]
    public string? CurrentBranch { get; init; }
}

/// <summary>
/// PP13-79: Git-Dolt commit mapping configuration
/// </summary>
public record GitMappingConfig
{
    /// <summary>
    /// Whether Git-Dolt mapping is enabled
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Last Git commit hash that was mapped
    /// </summary>
    [JsonPropertyName("last_git_commit")]
    public string? LastGitCommit { get; init; }

    /// <summary>
    /// Dolt commit hash at the last Git commit
    /// </summary>
    [JsonPropertyName("dolt_commit_at_git_commit")]
    public string? DoltCommitAtGitCommit { get; init; }
}

/// <summary>
/// PP13-79: Initialization behavior configuration
/// </summary>
public record InitializationConfig
{
    /// <summary>
    /// Initialization mode: auto, prompt, manual, disabled
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "auto";

    /// <summary>
    /// Behavior on Git clone: sync_to_manifest, sync_to_latest, empty, prompt
    /// </summary>
    [JsonPropertyName("on_clone")]
    public string OnClone { get; init; } = "sync_to_manifest";

    /// <summary>
    /// Behavior on Git branch change: preserve_local, sync_to_manifest, prompt
    /// </summary>
    [JsonPropertyName("on_branch_change")]
    public string OnBranchChange { get; init; } = "preserve_local";
}

/// <summary>
/// PP13-79: Collection tracking configuration
/// </summary>
public record CollectionTrackingConfig
{
    /// <summary>
    /// Collection name patterns to track (supports wildcards)
    /// </summary>
    [JsonPropertyName("tracked")]
    public List<string> Tracked { get; init; } = new() { "*" };

    /// <summary>
    /// Collection name patterns to exclude (supports wildcards)
    /// </summary>
    [JsonPropertyName("excluded")]
    public List<string> Excluded { get; init; } = new();
}

/// <summary>
/// PP13-79: Result of initialization check
/// </summary>
public record InitializationCheck
{
    /// <summary>
    /// Whether initialization is needed
    /// </summary>
    public bool NeedsInitialization { get; init; }

    /// <summary>
    /// Reason for initialization need
    /// </summary>
    public string Reason { get; init; } = "";

    /// <summary>
    /// Current local Dolt commit hash
    /// </summary>
    public string? CurrentDoltCommit { get; init; }

    /// <summary>
    /// Dolt commit hash from manifest
    /// </summary>
    public string? ManifestDoltCommit { get; init; }

    /// <summary>
    /// Current local branch name
    /// </summary>
    public string? CurrentBranch { get; init; }

    /// <summary>
    /// Branch name from manifest
    /// </summary>
    public string? ManifestBranch { get; init; }
}

/// <summary>
/// PP13-79: Result of DMMS initialization operation
/// </summary>
public record InitializationResult
{
    /// <summary>
    /// Whether initialization succeeded
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Action that was taken during initialization
    /// </summary>
    public InitializationAction ActionTaken { get; init; }

    /// <summary>
    /// Dolt commit hash after initialization
    /// </summary>
    public string? DoltCommit { get; init; }

    /// <summary>
    /// Dolt branch after initialization
    /// </summary>
    public string? DoltBranch { get; init; }

    /// <summary>
    /// Number of collections that were synced
    /// </summary>
    public int CollectionsSynced { get; init; }

    /// <summary>
    /// Error message if initialization failed
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// PP13-79: Actions that can be taken during initialization
/// </summary>
public enum InitializationAction
{
    /// <summary>
    /// No action was needed
    /// </summary>
    None,

    /// <summary>
    /// Cloned from remote and synced to manifest state
    /// </summary>
    ClonedAndSynced,

    /// <summary>
    /// Fetched from remote and synced to manifest state
    /// </summary>
    FetchedAndSynced,

    /// <summary>
    /// Checked out specified branch
    /// </summary>
    CheckedOutBranch,

    /// <summary>
    /// Checked out specified commit
    /// </summary>
    CheckedOutCommit,

    /// <summary>
    /// Synced existing local state to ChromaDB
    /// </summary>
    SyncedExisting,

    /// <summary>
    /// Initialization was skipped (manual mode or disabled)
    /// </summary>
    Skipped,

    /// <summary>
    /// Initialization failed
    /// </summary>
    Failed
}

/// <summary>
/// PP13-79: Initialization modes
/// </summary>
public static class InitializationMode
{
    /// <summary>
    /// Automatically sync on startup if manifest differs from local state
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// Ask user before syncing on startup
    /// </summary>
    public const string Prompt = "prompt";

    /// <summary>
    /// Only sync when explicitly requested
    /// </summary>
    public const string Manual = "manual";

    /// <summary>
    /// Never auto-sync; use local state only
    /// </summary>
    public const string Disabled = "disabled";

    /// <summary>
    /// Validates if a mode string is valid
    /// </summary>
    public static bool IsValid(string mode) =>
        mode == Auto || mode == Prompt || mode == Manual || mode == Disabled;
}

/// <summary>
/// PP13-79: On-clone behaviors
/// </summary>
public static class OnCloneBehavior
{
    /// <summary>
    /// Clone Dolt repo and checkout specified commit
    /// </summary>
    public const string SyncToManifest = "sync_to_manifest";

    /// <summary>
    /// Clone Dolt repo and checkout latest on default branch
    /// </summary>
    public const string SyncToLatest = "sync_to_latest";

    /// <summary>
    /// Start with empty DMMS state
    /// </summary>
    public const string Empty = "empty";

    /// <summary>
    /// Ask user what to do
    /// </summary>
    public const string Prompt = "prompt";

    /// <summary>
    /// Validates if a behavior string is valid
    /// </summary>
    public static bool IsValid(string behavior) =>
        behavior == SyncToManifest || behavior == SyncToLatest || behavior == Empty || behavior == Prompt;
}

/// <summary>
/// PP13-79: On-branch-change behaviors
/// </summary>
public static class OnBranchChangeBehavior
{
    /// <summary>
    /// Preserve local DMMS state
    /// </summary>
    public const string PreserveLocal = "preserve_local";

    /// <summary>
    /// Sync to manifest state for the new branch
    /// </summary>
    public const string SyncToManifest = "sync_to_manifest";

    /// <summary>
    /// Ask user what to do
    /// </summary>
    public const string Prompt = "prompt";

    /// <summary>
    /// Validates if a behavior string is valid
    /// </summary>
    public static bool IsValid(string behavior) =>
        behavior == PreserveLocal || behavior == SyncToManifest || behavior == Prompt;
}

/// <summary>
/// PP13-79-C1: Result of sync state check between local Dolt and manifest
/// </summary>
public record SyncStateCheckResult
{
    /// <summary>
    /// Whether local Dolt state matches the manifest
    /// </summary>
    public bool IsInSync { get; init; }

    /// <summary>
    /// Whether there are uncommitted local changes
    /// </summary>
    public bool HasLocalChanges { get; init; }

    /// <summary>
    /// Whether local is ahead of manifest (has commits not in manifest)
    /// </summary>
    public bool LocalAheadOfManifest { get; init; }

    /// <summary>
    /// Current local Dolt commit hash
    /// </summary>
    public string? LocalCommit { get; init; }

    /// <summary>
    /// Current local Dolt branch name
    /// </summary>
    public string? LocalBranch { get; init; }

    /// <summary>
    /// Commit hash from manifest
    /// </summary>
    public string? ManifestCommit { get; init; }

    /// <summary>
    /// Branch name from manifest
    /// </summary>
    public string? ManifestBranch { get; init; }

    /// <summary>
    /// Human-readable reason for the sync state
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Whether manifest exists at project root
    /// </summary>
    public bool ManifestExists { get; init; }

    /// <summary>
    /// Whether Dolt repository is initialized
    /// </summary>
    public bool DoltInitialized { get; init; }
}

/// <summary>
/// PP13-79-C1: Warning object for out-of-sync state, included in tool responses
/// </summary>
public record OutOfSyncWarning
{
    /// <summary>
    /// Type of warning (always "out_of_sync")
    /// </summary>
    public string Type { get; init; } = "out_of_sync";

    /// <summary>
    /// Human-readable message describing the sync issue
    /// </summary>
    public string Message { get; init; } = "";

    /// <summary>
    /// Current local Dolt state
    /// </summary>
    public SyncStateInfo? LocalState { get; init; }

    /// <summary>
    /// State specified in manifest
    /// </summary>
    public SyncStateInfo? ManifestState { get; init; }

    /// <summary>
    /// Recommended action for the user
    /// </summary>
    public string? ActionRequired { get; init; }
}

/// <summary>
/// PP13-79-C1: Simple state info for warning messages
/// </summary>
public record SyncStateInfo
{
    /// <summary>
    /// Branch name
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Commit hash
    /// </summary>
    public string? Commit { get; init; }
}
