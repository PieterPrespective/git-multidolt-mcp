using DMMS.Models;

namespace DMMS.Services;

/// <summary>
/// PP13-79: Interface for DMMS state manifest operations.
/// Manages reading, writing, and updating the .dmms/state.json file
/// that tracks Dolt repository state and Git-Dolt commit mappings.
/// </summary>
public interface IDmmsStateManifest
{
    /// <summary>
    /// Reads the state manifest from the project directory
    /// </summary>
    /// <param name="projectPath">Path to the project root (containing .dmms folder)</param>
    /// <returns>The manifest if found and valid, null otherwise</returns>
    Task<DmmsManifest?> ReadManifestAsync(string projectPath);

    /// <summary>
    /// Writes/updates the state manifest to the project directory
    /// </summary>
    /// <param name="projectPath">Path to the project root</param>
    /// <param name="manifest">The manifest to write</param>
    Task WriteManifestAsync(string projectPath, DmmsManifest manifest);

    /// <summary>
    /// Checks if a manifest exists in the project
    /// </summary>
    /// <param name="projectPath">Path to the project root</param>
    /// <returns>True if .dmms/state.json exists</returns>
    Task<bool> ManifestExistsAsync(string projectPath);

    /// <summary>
    /// Updates the Dolt commit reference in the manifest.
    /// This is called after Dolt commits to keep manifest in sync.
    /// </summary>
    /// <param name="projectPath">Path to the project root</param>
    /// <param name="commitHash">New Dolt commit hash</param>
    /// <param name="branch">Current Dolt branch name</param>
    Task UpdateDoltCommitAsync(string projectPath, string commitHash, string branch);

    /// <summary>
    /// Records a Git-Dolt commit mapping.
    /// This associates a Git commit with a Dolt commit for state reconstruction.
    /// </summary>
    /// <param name="projectPath">Path to the project root</param>
    /// <param name="gitCommit">Git commit hash</param>
    /// <param name="doltCommit">Corresponding Dolt commit hash</param>
    Task RecordGitMappingAsync(string projectPath, string gitCommit, string doltCommit);

    /// <summary>
    /// Gets the manifest file path for a project
    /// </summary>
    /// <param name="projectPath">Path to the project root</param>
    /// <returns>Full path to .dmms/state.json</returns>
    string GetManifestPath(string projectPath);

    /// <summary>
    /// Gets the .dmms directory path for a project
    /// </summary>
    /// <param name="projectPath">Path to the project root</param>
    /// <returns>Full path to .dmms directory</returns>
    string GetDmmsDirectoryPath(string projectPath);

    /// <summary>
    /// Validates a manifest structure
    /// </summary>
    /// <param name="manifest">The manifest to validate</param>
    /// <returns>True if the manifest is valid</returns>
    bool ValidateManifest(DmmsManifest manifest);

    /// <summary>
    /// Creates a default manifest with the specified remote URL
    /// </summary>
    /// <param name="remoteUrl">Optional Dolt remote URL</param>
    /// <param name="defaultBranch">Default branch name (default: "main")</param>
    /// <param name="initMode">Initialization mode (default: "auto")</param>
    /// <returns>A new DmmsManifest with default values</returns>
    DmmsManifest CreateDefaultManifest(string? remoteUrl = null, string defaultBranch = "main", string initMode = "auto");
}
