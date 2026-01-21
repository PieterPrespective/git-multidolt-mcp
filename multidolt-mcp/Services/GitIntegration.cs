using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace DMMS.Services;

/// <summary>
/// PP13-79: Implementation of Git repository integration.
/// Uses CliWrap for reliable Git command execution with proper timeout handling.
/// </summary>
public class GitIntegration : IGitIntegration
{
    private readonly ILogger<GitIntegration> _logger;
    private const int DefaultTimeoutMs = 5000;
    private string? _cachedGitRoot;
    private string? _cachedGitRootPath;

    public GitIntegration(ILogger<GitIntegration> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetCurrentGitCommitAsync(string repoPath)
    {
        try
        {
            if (!await IsGitRepositoryAsync(repoPath))
            {
                _logger.LogDebug("[GitIntegration.GetCurrentGitCommitAsync] Path is not a Git repository: {Path}", repoPath);
                return null;
            }

            var result = await ExecuteGitCommandAsync(repoPath, "rev-parse", "HEAD");

            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
            {
                var commit = result.Output.Trim();
                _logger.LogDebug("[GitIntegration.GetCurrentGitCommitAsync] Current Git commit: {Commit}", commit);
                return commit;
            }

            _logger.LogDebug("[GitIntegration.GetCurrentGitCommitAsync] Failed to get Git commit: {Error}", result.Error);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GitIntegration.GetCurrentGitCommitAsync] Error getting Git commit at: {Path}", repoPath);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsGitRepositoryAsync(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            var result = await ExecuteGitCommandAsync(path, "rev-parse", "--is-inside-work-tree");

            var isRepo = result.Success && result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            _logger.LogDebug("[GitIntegration.IsGitRepositoryAsync] Path {Path} is Git repo: {IsRepo}", path, isRepo);
            return isRepo;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GitIntegration.IsGitRepositoryAsync] Error checking Git repository at: {Path}", path);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetGitRootAsync(string path)
    {
        try
        {
            // Use caching for performance
            if (_cachedGitRootPath == path && _cachedGitRoot != null)
            {
                return _cachedGitRoot;
            }

            if (!Directory.Exists(path))
            {
                return null;
            }

            var result = await ExecuteGitCommandAsync(path, "rev-parse", "--show-toplevel");

            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
            {
                var gitRoot = result.Output.Trim();

                // Normalize path separators for cross-platform compatibility
                gitRoot = Path.GetFullPath(gitRoot);

                // Cache the result
                _cachedGitRootPath = path;
                _cachedGitRoot = gitRoot;

                _logger.LogDebug("[GitIntegration.GetGitRootAsync] Git root for {Path}: {GitRoot}", path, gitRoot);
                return gitRoot;
            }

            _logger.LogDebug("[GitIntegration.GetGitRootAsync] Path is not in a Git repository: {Path}", path);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GitIntegration.GetGitRootAsync] Error getting Git root at: {Path}", path);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsGitAvailableAsync()
    {
        try
        {
            var result = await ExecuteGitCommandWithoutWorkingDirAsync("--version");
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetGitVersionAsync()
    {
        try
        {
            var result = await ExecuteGitCommandWithoutWorkingDirAsync("--version");

            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
            {
                return result.Output.Trim();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GitIntegration.GetGitVersionAsync] Error getting Git version");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsFileTrackedAsync(string repoPath, string filePath)
    {
        try
        {
            if (!await IsGitRepositoryAsync(repoPath))
            {
                return false;
            }

            // Make file path relative to repo root
            var gitRoot = await GetGitRootAsync(repoPath);
            if (gitRoot == null)
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(gitRoot, filePath);

            // git ls-files returns the file path if tracked, empty if not
            var result = await ExecuteGitCommandAsync(repoPath, "ls-files", relativePath);

            return result.Success && !string.IsNullOrWhiteSpace(result.Output);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GitIntegration.IsFileTrackedAsync] Error checking if file is tracked: {FilePath}", filePath);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetCurrentBranchAsync(string repoPath)
    {
        try
        {
            if (!await IsGitRepositoryAsync(repoPath))
            {
                return null;
            }

            var result = await ExecuteGitCommandAsync(repoPath, "rev-parse", "--abbrev-ref", "HEAD");

            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
            {
                var branch = result.Output.Trim();

                // "HEAD" is returned when in detached HEAD state
                if (branch == "HEAD")
                {
                    _logger.LogDebug("[GitIntegration.GetCurrentBranchAsync] Repository is in detached HEAD state");
                    return null;
                }

                _logger.LogDebug("[GitIntegration.GetCurrentBranchAsync] Current Git branch: {Branch}", branch);
                return branch;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GitIntegration.GetCurrentBranchAsync] Error getting current Git branch at: {Path}", repoPath);
            return null;
        }
    }

    /// <summary>
    /// Executes a Git command in the specified working directory
    /// </summary>
    private async Task<GitCommandResult> ExecuteGitCommandAsync(string workingDir, params string[] args)
    {
        try
        {
            _logger.LogDebug("[GitIntegration] Executing: git {Args} in {WorkingDir}", string.Join(" ", args), workingDir);

            var result = await Cli.Wrap("git")
                .WithArguments(args)
                .WithWorkingDirectory(workingDir)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(new CancellationTokenSource(DefaultTimeoutMs).Token);

            return new GitCommandResult(
                Success: result.ExitCode == 0,
                Output: result.StandardOutput,
                Error: result.StandardError,
                ExitCode: result.ExitCode
            );
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[GitIntegration] Git command timed out after {Timeout}ms", DefaultTimeoutMs);
            return new GitCommandResult(false, "", "Command timed out", -1);
        }
        catch (Exception ex)
        {
            // Check if Git is not installed
            if (IsGitNotFoundError(ex))
            {
                _logger.LogDebug("[GitIntegration] Git executable not found");
                return new GitCommandResult(false, "",
                    "Git executable not found. Please ensure Git is installed and added to PATH.", -2);
            }

            return new GitCommandResult(false, "", ex.Message, -1);
        }
    }

    /// <summary>
    /// Executes a Git command without a specific working directory
    /// </summary>
    private async Task<GitCommandResult> ExecuteGitCommandWithoutWorkingDirAsync(params string[] args)
    {
        try
        {
            var result = await Cli.Wrap("git")
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(new CancellationTokenSource(DefaultTimeoutMs).Token);

            return new GitCommandResult(
                Success: result.ExitCode == 0,
                Output: result.StandardOutput,
                Error: result.StandardError,
                ExitCode: result.ExitCode
            );
        }
        catch (OperationCanceledException)
        {
            return new GitCommandResult(false, "", "Command timed out", -1);
        }
        catch (Exception ex)
        {
            if (IsGitNotFoundError(ex))
            {
                return new GitCommandResult(false, "",
                    "Git executable not found. Please ensure Git is installed and added to PATH.", -2);
            }

            return new GitCommandResult(false, "", ex.Message, -1);
        }
    }

    /// <summary>
    /// Checks if an exception indicates Git is not installed
    /// </summary>
    private static bool IsGitNotFoundError(Exception ex)
    {
        return ex is System.ComponentModel.Win32Exception ||
               ex.Message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("is not recognized", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Result of a Git command execution
    /// </summary>
    private record GitCommandResult(bool Success, string Output, string Error, int ExitCode);
}
