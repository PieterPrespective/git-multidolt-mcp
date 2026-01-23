using Embranch.Models;

namespace Embranch.Services;

/// <summary>
/// PP13-79-C1: Interface for checking sync state between local Dolt and manifest.
/// Provides methods to determine if sync is needed and if it's safe to sync.
/// </summary>
public interface ISyncStateChecker
{
    /// <summary>
    /// Checks if local Dolt state matches the manifest.
    /// Compares current branch and commit against manifest values.
    /// </summary>
    /// <returns>Result containing sync state comparison details</returns>
    Task<SyncStateCheckResult> CheckSyncStateAsync();

    /// <summary>
    /// Determines if it's safe to sync (no uncommitted changes).
    /// Returns false if syncing would lose local work.
    /// </summary>
    /// <returns>True if safe to sync, false if local changes would be lost</returns>
    Task<bool> IsSafeToSyncAsync();

    /// <summary>
    /// Gets the out-of-sync warning object if applicable.
    /// Returns null if state is in sync or manifest doesn't exist.
    /// </summary>
    /// <returns>Warning object with details about sync state mismatch, or null</returns>
    Task<OutOfSyncWarning?> GetOutOfSyncWarningAsync();

    /// <summary>
    /// Invalidates any cached sync state results.
    /// Should be called after Dolt operations that change state.
    /// </summary>
    void InvalidateCache();

    /// <summary>
    /// Gets the current project root being used for manifest lookup.
    /// </summary>
    /// <returns>Project root path or null if not determined</returns>
    Task<string?> GetProjectRootAsync();
}
