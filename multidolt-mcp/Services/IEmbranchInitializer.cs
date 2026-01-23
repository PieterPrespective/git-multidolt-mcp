using Embranch.Models;

namespace Embranch.Services;

/// <summary>
/// PP13-79: Interface for Embranch state initialization.
/// Handles initializing Embranch state based on manifest and configuration,
/// including cloning from remote, fetching updates, and syncing ChromaDB.
/// </summary>
public interface IEmbranchInitializer
{
    /// <summary>
    /// Initializes Embranch state based on the manifest configuration.
    /// This may involve cloning, fetching, checking out branches/commits,
    /// and syncing ChromaDB to match the Dolt state.
    /// </summary>
    /// <param name="manifest">The manifest to initialize from</param>
    /// <param name="projectRoot">Path to the project root directory</param>
    /// <returns>Result indicating success and actions taken</returns>
    Task<InitializationResult> InitializeFromManifestAsync(DmmsManifest manifest, string projectRoot);

    /// <summary>
    /// Checks if initialization is needed based on manifest vs current state.
    /// Compares manifest Dolt commit/branch against local state.
    /// </summary>
    /// <param name="manifest">The manifest to check against</param>
    /// <returns>Check result indicating if initialization is needed and why</returns>
    Task<InitializationCheck> CheckInitializationNeededAsync(DmmsManifest manifest);

    /// <summary>
    /// Syncs local Embranch state to match a specific Dolt commit.
    /// This includes checking out the commit and syncing ChromaDB.
    /// </summary>
    /// <param name="doltCommit">The Dolt commit hash to sync to</param>
    /// <param name="branch">Optional branch name (for tracking purposes)</param>
    /// <returns>Result of the sync operation</returns>
    Task<SyncResultV2> SyncToCommitAsync(string doltCommit, string? branch = null);

    /// <summary>
    /// Syncs local Embranch state to match a specific Dolt branch (latest commit on branch).
    /// This includes checking out the branch and syncing ChromaDB.
    /// </summary>
    /// <param name="branchName">The Dolt branch name to sync to</param>
    /// <returns>Result of the sync operation</returns>
    Task<SyncResultV2> SyncToBranchAsync(string branchName);

    /// <summary>
    /// Gets the current initialization state for diagnostics.
    /// </summary>
    /// <returns>Current state information</returns>
    Task<EmbranchInitializationState> GetCurrentStateAsync();
}

/// <summary>
/// PP13-79: Current Embranch initialization state for diagnostics
/// </summary>
public class EmbranchInitializationState
{
    /// <summary>
    /// Whether Dolt repository is initialized
    /// </summary>
    public bool DoltInitialized { get; set; }

    /// <summary>
    /// Current Dolt commit hash
    /// </summary>
    public string? CurrentDoltCommit { get; set; }

    /// <summary>
    /// Current Dolt branch name
    /// </summary>
    public string? CurrentDoltBranch { get; set; }

    /// <summary>
    /// Whether manifest exists
    /// </summary>
    public bool ManifestExists { get; set; }

    /// <summary>
    /// Manifest Dolt commit (if manifest exists)
    /// </summary>
    public string? ManifestDoltCommit { get; set; }

    /// <summary>
    /// Manifest Dolt branch (if manifest exists)
    /// </summary>
    public string? ManifestDoltBranch { get; set; }

    /// <summary>
    /// Whether state matches manifest
    /// </summary>
    public bool StateMatchesManifest { get; set; }

    /// <summary>
    /// Detected project root path
    /// </summary>
    public string? ProjectRoot { get; set; }

    /// <summary>
    /// Whether project is in a Git repository
    /// </summary>
    public bool IsGitRepository { get; set; }

    /// <summary>
    /// Current Git commit (if in Git repo)
    /// </summary>
    public string? CurrentGitCommit { get; set; }
}
