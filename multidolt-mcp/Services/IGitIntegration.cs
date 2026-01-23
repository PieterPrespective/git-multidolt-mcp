namespace Embranch.Services;

/// <summary>
/// PP13-79: Interface for Git repository integration.
/// Provides helper methods to detect Git repositories and retrieve commit information.
/// </summary>
public interface IGitIntegration
{
    /// <summary>
    /// Gets the current Git commit hash (HEAD)
    /// </summary>
    /// <param name="repoPath">Path inside the Git repository</param>
    /// <returns>The full commit hash, or null if not in a Git repository</returns>
    Task<string?> GetCurrentGitCommitAsync(string repoPath);

    /// <summary>
    /// Checks if the current directory is inside a Git repository
    /// </summary>
    /// <param name="path">Path to check</param>
    /// <returns>True if the path is inside a Git repository</returns>
    Task<bool> IsGitRepositoryAsync(string path);

    /// <summary>
    /// Gets the Git root directory from a path inside the repository
    /// </summary>
    /// <param name="path">Path inside the Git repository</param>
    /// <returns>The absolute path to the Git root directory, or null if not in a Git repository</returns>
    Task<string?> GetGitRootAsync(string path);

    /// <summary>
    /// Checks if Git is installed and accessible
    /// </summary>
    /// <returns>True if Git is installed and accessible</returns>
    Task<bool> IsGitAvailableAsync();

    /// <summary>
    /// Gets the Git version
    /// </summary>
    /// <returns>Git version string, or null if Git is not available</returns>
    Task<string?> GetGitVersionAsync();

    /// <summary>
    /// Checks if a specific file is tracked by Git
    /// </summary>
    /// <param name="repoPath">Path to the Git repository</param>
    /// <param name="filePath">Path to the file to check</param>
    /// <returns>True if the file is tracked by Git</returns>
    Task<bool> IsFileTrackedAsync(string repoPath, string filePath);

    /// <summary>
    /// Gets the current Git branch name
    /// </summary>
    /// <param name="repoPath">Path inside the Git repository</param>
    /// <returns>The current branch name, or null if not on a branch or not in a Git repository</returns>
    Task<string?> GetCurrentBranchAsync(string repoPath);
}
