using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Embranch.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Embranch.Services
{
    /// <summary>
    /// Implementation of Dolt CLI wrapper using CliWrap for process execution
    /// </summary>
    public class DoltCli : IDoltCli
    {
        private readonly string _doltPath;
        private readonly string _repositoryPath;
        private readonly int _commandTimeout;
        private readonly bool _debugLogging;
        private readonly ILogger<DoltCli> _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Initialize a new DoltCli instance with configuration and logging
        /// </summary>
        /// <param name="config">Dolt configuration including executable path, repository path, and timeout settings</param>
        /// <param name="logger">Logger for debugging command execution and error reporting</param>
        public DoltCli(IOptions<DoltConfiguration> config, ILogger<DoltCli> logger)
        {
            
            var configuration = config.Value;
            _doltPath = configuration.DoltExecutablePath ?? "dolt";
            _repositoryPath = configuration.RepositoryPath;
            _commandTimeout = configuration.CommandTimeoutMs;
            _debugLogging = configuration.EnableDebugLogging;
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };

            logger.LogDebug("[DoltCli] DoltCLI Initialized, setting up repository directory..");
            // Ensure repository directory exists
            EnsureRepositoryDirectoryExists();
        }

        /// <summary>
        /// Ensures the repository directory exists, creating it and any parent directories if necessary
        /// </summary>
        private void EnsureRepositoryDirectoryExists()
        {
            try
            {
                if (string.IsNullOrEmpty(_repositoryPath))
                {
                    _logger.LogWarning("[DoltCli.EnsureRepositoryDirectoryExists] Repository path is null or empty, skipping directory creation");
                    return;
                }

                // Convert relative path to absolute path for better handling
                var absolutePath = Path.GetFullPath(_repositoryPath);
                
                if (!Directory.Exists(absolutePath))
                {
                    _logger.LogInformation("[DoltCli.EnsureRepositoryDirectoryExists] Creating repository directory: {RepositoryPath}", absolutePath);
                    Directory.CreateDirectory(absolutePath);
                    _logger.LogInformation("[DoltCli.EnsureRepositoryDirectoryExists] Successfully created repository directory: {RepositoryPath}", absolutePath);
                }
                else
                {
                    _logger.LogDebug("[DoltCli.EnsureRepositoryDirectoryExists] Repository directory already exists: {RepositoryPath}", absolutePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DoltCli.EnsureRepositoryDirectoryExists] Failed to create repository directory: {RepositoryPath}. Error: {ErrorMessage}", 
                    _repositoryPath, ex.Message);
                throw new InvalidOperationException($"Failed to create Dolt repository directory '{_repositoryPath}': {ex.Message}", ex);
            }
        }

        // ==================== Core Execution Methods ====================

        /// <summary>
        /// Core command execution method. Handles process management, timeout, and error capture.
        /// Uses CliWrap for robust async process execution with proper cancellation support.
        /// </summary>
        private async Task<DoltCommandResult> ExecuteDoltCommandAsync(params string[] args)
        {
            if (_debugLogging)
            {
                _logger.LogDebug("Executing: {DoltPath} {Args}", _doltPath, string.Join(" ", args));
            }
            
            try
            {
                var result = await Cli.Wrap(_doltPath)
                    .WithArguments(args)
                    .WithWorkingDirectory(_repositoryPath)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(new CancellationTokenSource(_commandTimeout).Token);

                var cmdResult = new DoltCommandResult(
                    Success: result.ExitCode == 0,
                    Output: result.StandardOutput,
                    Error: result.StandardError,
                    ExitCode: result.ExitCode
                );

                if (!cmdResult.Success && _debugLogging)
                {
                    _logger.LogWarning("Dolt command failed with exit code {ExitCode}: {Error}", 
                        cmdResult.ExitCode, cmdResult.Error);
                }

                return cmdResult;
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("Dolt command timed out after {Timeout}ms", _commandTimeout);
                return new DoltCommandResult(false, "", "Command timed out", -1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute dolt command");
                
                // Check if this is due to missing dolt executable
                if (ex is System.ComponentModel.Win32Exception || 
                    ex.Message.Contains("No such file or directory") ||
                    ex.Message.Contains("cannot find") ||
                    ex.Message.Contains("not found"))
                {
                    return new DoltCommandResult(false, "", 
                        "Dolt executable not found. Please ensure Dolt is installed and added to PATH environment variable.", -2);
                }
                
                return new DoltCommandResult(false, "", ex.Message, -1);
            }
        }

        /// <summary>
        /// Executes SQL queries with JSON result format (-r json flag).
        /// This provides structured, machine-readable output for reliable parsing.
        /// Throws DoltException on failure with detailed error information.
        /// </summary>
        private async Task<string> ExecuteSqlJsonAsync(string sql)
        {
            var result = await ExecuteDoltCommandAsync("sql", "-q", sql, "-r", "json");
            if (!result.Success)
            {
                throw new DoltException($"SQL query failed: {result.Error}", result.ExitCode, result.Error, result.Output);
            }
            return result.Output;
        }

        // ==================== Repository Management ====================

        public async Task<DoltCommandResult> CheckDoltAvailableAsync()
        {
            // Try to run 'dolt version' command to check if Dolt is available
            // This doesn't require a repository to exist
            try
            {
                var result = await Cli.Wrap(_doltPath)
                    .WithArguments("version")
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(new CancellationTokenSource(5000).Token);
                    
                if (result.ExitCode == 0)
                {
                    return new DoltCommandResult(true, result.StandardOutput, "", 0);
                }
                else
                {
                    return new DoltCommandResult(false, "", result.StandardError, result.ExitCode);
                }
            }
            catch (Exception ex)
            {
                // Check if this is due to missing dolt executable
                if (ex is System.ComponentModel.Win32Exception || 
                    ex.Message.Contains("No such file or directory") ||
                    ex.Message.Contains("cannot find") ||
                    ex.Message.Contains("not found"))
                {
                    return new DoltCommandResult(false, "", 
                        "Dolt executable not found. Please ensure Dolt is installed and added to PATH environment variable.", -2);
                }
                
                return new DoltCommandResult(false, "", $"Failed to check Dolt availability: {ex.Message}", -1);
            }
        }

        public async Task<bool> IsInitializedAsync()
        {
            try
            {
                // Check if .dolt directory exists in the repository path
                var doltDir = Path.Combine(_repositoryPath, ".dolt");
                if (!Directory.Exists(doltDir))
                {
                    return false;
                }

                // Try to execute a simple dolt command to verify the repository is valid
                var result = await ExecuteDoltCommandAsync("status");
                return result.Success;
            }
            catch
            {
                return false;
            }
        }

        public async Task<DoltCommandResult> InitAsync()
        {
            var result = await ExecuteDoltCommandAsync("init");
            
            // After init, we need to create an initial commit to establish proper database state
            // This fixes the "no database selected" issue in multi-user scenarios (PP13-68-C2)
            if (result.Success)
            {
                try
                {
                    // Create a minimal initial commit to establish database context
                    // This pattern is based on PP13-42 and PP13-43 solutions
                    var addResult = await ExecuteDoltCommandAsync("add", ".");
                    if (addResult.Success)
                    {
                        var commitResult = await ExecuteDoltCommandAsync("commit", "-m", "Initial repository setup", "--allow-empty");
                        if (!commitResult.Success)
                        {
                            _logger.LogDebug("Could not create initial commit: {Error}", commitResult.Error);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Could not create initial commit after init: {Message}", ex.Message);
                }
            }
            
            return result;
        }

        public async Task<DoltCommandResult> CloneAsync(string remoteUrl, string? localPath = null)
        {
            return localPath != null
                ? await ExecuteDoltCommandAsync("clone", remoteUrl, localPath)
                : await ExecuteDoltCommandAsync("clone", remoteUrl);
        }

        public async Task<RepositoryStatus> GetStatusAsync()
        {
            var result = await ExecuteDoltCommandAsync("status");
            if (!result.Success)
            {
                return new RepositoryStatus("", false, false, 
                    Enumerable.Empty<string>(), Enumerable.Empty<string>());
            }

            var output = result.Output;
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            // Parse branch from first line
            var branchMatch = Regex.Match(lines.FirstOrDefault() ?? "", @"On branch (.+)");
            var branch = branchMatch.Success ? branchMatch.Groups[1].Value : "";

            // Parse staged/unstaged sections
            var staged = new List<string>();
            var modified = new List<string>();
            bool inStaged = false;
            bool inUnstaged = false;

            foreach (var line in lines)
            {
                if (line.Contains("Changes to be committed"))
                    inStaged = true;
                else if (line.Contains("Changes not staged"))
                {
                    inStaged = false;
                    inUnstaged = true;
                }
                else if (inStaged && line.Contains("modified:"))
                {
                    var table = line.Split("modified:").Last().Trim();
                    staged.Add(table);
                }
                else if (inUnstaged && line.Contains("modified:"))
                {
                    var table = line.Split("modified:").Last().Trim();
                    modified.Add(table);
                }
            }

            return new RepositoryStatus(
                branch,
                staged.Any(),
                modified.Any(),
                staged,
                modified
            );
        }

        // ==================== Branch Operations ====================

        /// <summary>
        /// Implementation Note: Uses SQL function active_branch() rather than 'dolt branch' command
        /// to get reliable machine-readable output. Falls back to 'dolt branch --show-current' if SQL fails (PP13-68-C2).
        /// </summary>
        public async Task<string> GetCurrentBranchAsync()
        {
            try
            {
                var json = await ExecuteSqlJsonAsync("SELECT active_branch() as branch");
                using var doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty("rows", out var rows))
                {
                    var firstRow = rows.EnumerateArray().FirstOrDefault();
                    if (firstRow.ValueKind != JsonValueKind.Undefined && firstRow.TryGetProperty("branch", out var branch))
                    {
                        return branch.GetString();
                    }
                }
            }
            catch (DoltException ex) when (ex.Message.Contains("no database selected"))
            {
                // Fallback to using dolt branch command when SQL fails due to database context issues (PP13-68-C2)
                _logger.LogDebug("SQL query for current branch failed, falling back to 'dolt branch --show-current'");
                var result = await ExecuteDoltCommandAsync("branch", "--show-current");
                if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
                {
                    return result.Output.Trim();
                }
            }
            
            throw new DoltException("Failed to get current branch");
        }

        public async Task<IEnumerable<BranchInfo>> ListBranchesAsync()
        {
            var result = await ExecuteDoltCommandAsync("branch", "-v");
            if (!result.Success)
                return Enumerable.Empty<BranchInfo>();

            var branches = new List<BranchInfo>();
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var isCurrent = line.StartsWith("*");
                var parts = line.TrimStart('*', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    branches.Add(new BranchInfo(parts[0], isCurrent, parts[1]));
                }
            }
            return branches;
        }

        /// <summary>
        /// Lists all branches including remote tracking branches.
        /// Uses 'dolt branch -a -v' to get comprehensive branch listing.
        /// Remote branches are prefixed with 'remotes/origin/' in the output.
        /// </summary>
        /// <returns>Collection of all branch information including remote branches</returns>
        public async Task<IEnumerable<BranchInfo>> ListAllBranchesAsync()
        {
            var result = await ExecuteDoltCommandAsync("branch", "-a", "-v");
            if (!result.Success)
            {
                _logger.LogWarning("[DoltCli.ListAllBranchesAsync] Command failed: {Error}", result.Error);
                return Enumerable.Empty<BranchInfo>();
            }

            var branches = new List<BranchInfo>();
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var isCurrent = line.StartsWith("*");
                var trimmedLine = line.TrimStart('*', ' ');
                var parts = trimmedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 2)
                {
                    var branchName = parts[0];
                    var commitHash = parts[1];

                    // Parse remote branch references (e.g., remotes/origin/branchname)
                    var isRemote = branchName.StartsWith("remotes/");

                    branches.Add(new BranchInfo(branchName, isCurrent, commitHash, isRemote));
                }
            }

            if (_debugLogging)
            {
                var localCount = branches.Count(b => !b.IsRemote);
                var remoteCount = branches.Count(b => b.IsRemote);
                _logger.LogDebug("[DoltCli.ListAllBranchesAsync] Found {LocalCount} local and {RemoteCount} remote branches",
                    localCount, remoteCount);
            }

            return branches;
        }

        /// <summary>
        /// Checks if a branch exists either locally or as a remote tracking branch.
        /// </summary>
        /// <param name="branchName">Name of the branch to check (can be just the name or full remote path)</param>
        /// <returns>True if branch exists locally or remotely</returns>
        public async Task<bool> BranchExistsAsync(string branchName)
        {
            var allBranches = await ListAllBranchesAsync();

            // Check exact match first
            if (allBranches.Any(b => b.Name == branchName))
            {
                return true;
            }

            // Check for remote branch match (e.g., "mergetestbranch2" matches "remotes/origin/mergetestbranch2")
            var remotePattern = $"remotes/origin/{branchName}";
            if (allBranches.Any(b => b.Name == remotePattern))
            {
                return true;
            }

            // Also check if the branch name is a remote pattern itself
            if (branchName.StartsWith("remotes/") && allBranches.Any(b => b.Name == branchName))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the actual branch reference for a branch name, resolving remote references if needed.
        /// </summary>
        /// <param name="branchName">The branch name to resolve</param>
        /// <returns>The actual branch reference to use for Dolt commands, or null if not found</returns>
        public async Task<string?> ResolveBranchReferenceAsync(string branchName)
        {
            var allBranches = await ListAllBranchesAsync();

            // Check for exact local branch match first
            var localMatch = allBranches.FirstOrDefault(b => b.Name == branchName && !b.IsRemote);
            if (localMatch != null)
            {
                return localMatch.Name;
            }

            // Check for remote branch match
            var remotePattern = $"remotes/origin/{branchName}";
            var remoteMatch = allBranches.FirstOrDefault(b => b.Name == remotePattern);
            if (remoteMatch != null)
            {
                return remoteMatch.Name;
            }

            // Check if the provided name is already a full remote reference
            if (branchName.StartsWith("remotes/"))
            {
                var fullRemoteMatch = allBranches.FirstOrDefault(b => b.Name == branchName);
                if (fullRemoteMatch != null)
                {
                    return fullRemoteMatch.Name;
                }
            }

            return null;
        }

        /// <summary>
        /// PP13-72-C4: Check if a branch exists locally (not just as a remote tracking branch).
        /// </summary>
        /// <param name="branchName">Name of the branch to check</param>
        /// <returns>True if branch exists as a local branch, false if only remote or doesn't exist</returns>
        public async Task<bool> IsLocalBranchAsync(string branchName)
        {
            var allBranches = await ListAllBranchesAsync();

            // Check for exact local branch match (not remote)
            var isLocal = allBranches.Any(b => b.Name == branchName && !b.IsRemote);

            if (_debugLogging)
            {
                _logger.LogDebug("[DoltCli.IsLocalBranchAsync] Branch '{Branch}' is local: {IsLocal}", branchName, isLocal);
            }

            return isLocal;
        }

        /// <summary>
        /// PP13-72-C4: Get the commit hash for a branch without checking it out.
        /// Uses SQL HASHOF() function to retrieve the commit hash directly.
        /// </summary>
        /// <param name="branchName">Name of the branch (local or remote)</param>
        /// <returns>Commit hash, or null if branch not found</returns>
        public async Task<string?> GetBranchCommitHashAsync(string branchName)
        {
            try
            {
                // Try SQL HASHOF() approach first - works for local branches
                var sql = $"SELECT HASHOF('{branchName}') as hash";
                var json = await ExecuteSqlJsonAsync(sql);

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("rows", out var rows))
                {
                    var firstRow = rows.EnumerateArray().FirstOrDefault();
                    if (firstRow.ValueKind != JsonValueKind.Undefined &&
                        firstRow.TryGetProperty("hash", out var hash))
                    {
                        var hashValue = hash.GetString();
                        if (!string.IsNullOrEmpty(hashValue))
                        {
                            if (_debugLogging)
                            {
                                _logger.LogDebug("[DoltCli.GetBranchCommitHashAsync] Got hash for '{Branch}' via HASHOF: {Hash}",
                                    branchName, hashValue);
                            }
                            return hashValue;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[DoltCli.GetBranchCommitHashAsync] HASHOF failed for '{Branch}': {Error}. Trying branch list fallback.",
                    branchName, ex.Message);
            }

            // Fallback: Get hash from branch listing
            try
            {
                var allBranches = await ListAllBranchesAsync();

                // First try exact match
                var branch = allBranches.FirstOrDefault(b => b.Name == branchName);
                if (branch != null && !string.IsNullOrEmpty(branch.LastCommitHash))
                {
                    if (_debugLogging)
                    {
                        _logger.LogDebug("[DoltCli.GetBranchCommitHashAsync] Got hash for '{Branch}' via branch list: {Hash}",
                            branchName, branch.LastCommitHash);
                    }
                    return branch.LastCommitHash;
                }

                // Try remote branch pattern
                var remoteName = $"remotes/origin/{branchName}";
                var remoteBranch = allBranches.FirstOrDefault(b => b.Name == remoteName);
                if (remoteBranch != null && !string.IsNullOrEmpty(remoteBranch.LastCommitHash))
                {
                    if (_debugLogging)
                    {
                        _logger.LogDebug("[DoltCli.GetBranchCommitHashAsync] Got hash for '{Branch}' via remote branch list: {Hash}",
                            branchName, remoteBranch.LastCommitHash);
                    }
                    return remoteBranch.LastCommitHash;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DoltCli.GetBranchCommitHashAsync] Failed to get commit hash for branch '{Branch}'", branchName);
            }

            return null;
        }

        /// <summary>
        /// PP13-72-C4: Create a local tracking branch from a remote branch.
        /// Equivalent to: dolt checkout -b branchName remotes/origin/branchName
        /// </summary>
        /// <param name="branchName">Name of the branch to track</param>
        /// <param name="remote">Remote name (default: "origin")</param>
        /// <returns>Command result with success/failure status</returns>
        public async Task<DoltCommandResult> TrackRemoteBranchAsync(string branchName, string remote = "origin")
        {
            var remoteBranchRef = $"remotes/{remote}/{branchName}";

            _logger.LogInformation("[DoltCli.TrackRemoteBranchAsync] Creating local tracking branch '{Branch}' from '{RemoteBranch}'",
                branchName, remoteBranchRef);

            // Use checkout -b to create and checkout a new local branch tracking the remote
            var result = await ExecuteDoltCommandAsync("checkout", "-b", branchName, remoteBranchRef);

            if (result.Success)
            {
                _logger.LogInformation("[DoltCli.TrackRemoteBranchAsync] Successfully created local tracking branch '{Branch}'", branchName);
            }
            else
            {
                _logger.LogWarning("[DoltCli.TrackRemoteBranchAsync] Failed to create tracking branch '{Branch}': {Error}",
                    branchName, result.Error);
            }

            return result;
        }

        public async Task<DoltCommandResult> CheckoutAsync(string branchName, bool createNew = false)
        {
            return createNew
                ? await ExecuteDoltCommandAsync("checkout", "-b", branchName)
                : await ExecuteDoltCommandAsync("checkout", branchName);
        }

        public async Task<DoltCommandResult> CreateBranchAsync(string branchName)
        {
            return await ExecuteDoltCommandAsync("branch", branchName);
        }

        public async Task<DoltCommandResult> DeleteBranchAsync(string branchName, bool force = false)
        {
            return force
                ? await ExecuteDoltCommandAsync("branch", "-D", branchName)
                : await ExecuteDoltCommandAsync("branch", "-d", branchName);
        }

        public async Task<DoltCommandResult> RenameBranchAsync(string oldBranchName, string newBranchName, bool force = false)
        {
            return force
                ? await ExecuteDoltCommandAsync("branch", "-m", "-f", oldBranchName, newBranchName)
                : await ExecuteDoltCommandAsync("branch", "-m", oldBranchName, newBranchName);
        }

        // ==================== Commit Operations ====================

        public async Task<DoltCommandResult> AddAllAsync()
        {
            return await ExecuteDoltCommandAsync("add", "-A");
        }

        public async Task<DoltCommandResult> AddAsync(params string[] tables)
        {
            var args = new[] { "add" }.Concat(tables).ToArray();
            return await ExecuteDoltCommandAsync(args);
        }

        /// <summary>
        /// Implementation Note: Attempts to parse commit hash from command output.
        /// If parsing fails, queries DOLT_HASHOF('HEAD') as fallback.
        /// </summary>
        public async Task<CommitResult> CommitAsync(string message)
        {
            var result = await ExecuteDoltCommandAsync("commit", "-m", message);
            
            string? commitHash = null;
            if (result.Success)
            {
                // Parse commit hash from output like "commit abc123def456"
                var match = Regex.Match(result.Output, @"commit\s+([a-f0-9]{32})");
                if (match.Success)
                {
                    commitHash = match.Groups[1].Value!;
                }
                else
                {
                    // If parsing fails, try to get HEAD
                    try
                    {
                        commitHash = await GetHeadCommitHashAsync();
                    }
                    catch { }
                }
            }

            return new CommitResult(result.Success, commitHash ?? "", result.Success ? message : result.Error);
        }

        /// <summary>
        /// Implementation Note: Uses DOLT_HASHOF SQL function for reliable hash retrieval
        /// rather than parsing 'dolt log' output.
        /// </summary>
        public async Task<string> GetHeadCommitHashAsync()
        {
            var json = await ExecuteSqlJsonAsync("SELECT DOLT_HASHOF('HEAD') as hash");
            using var doc = JsonDocument.Parse(json);
            
            if (doc.RootElement.TryGetProperty("rows", out var rows))
            {
                var firstRow = rows.EnumerateArray().FirstOrDefault();
                if (firstRow.ValueKind != JsonValueKind.Undefined && firstRow.TryGetProperty("hash", out var hash))
                {
                    return hash.GetString();
                }
            }
            
            throw new DoltException("Failed to get HEAD commit hash");
        }

        /// <summary>
        /// Implementation Note: Uses --oneline format for performance.
        /// Author and date information are not available in this format and are set to defaults.
        /// </summary>
        public async Task<IEnumerable<CommitInfo>> GetLogAsync(int limit = 10)
        {
            var result = await ExecuteDoltCommandAsync("log", "--oneline", "-n", limit.ToString());
            if (!result.Success)
                return Enumerable.Empty<CommitInfo>();

            var commits = new List<CommitInfo>();
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', 2);
                if (parts.Length >= 2)
                {
                    // Strip ANSI color codes and extract just the commit message
                    var messageWithAnsi = parts[1];
                    var cleanMessage = StripAnsiColorCodes(messageWithAnsi);
                    
                    commits.Add(new CommitInfo(
                        parts[0],
                        cleanMessage,
                        "", // Author not available in --oneline format
                        DateTime.Now // Date not available in --oneline format
                    ));
                }
            }
            return commits;
        }

        // ==================== Remote Operations ====================

        public async Task<DoltCommandResult> AddRemoteAsync(string name, string url)
        {
            return await ExecuteDoltCommandAsync("remote", "add", name, url);
        }

        public async Task<DoltCommandResult> RemoveRemoteAsync(string name)
        {
            return await ExecuteDoltCommandAsync("remote", "remove", name);
        }

        /// <summary>
        /// Implementation Note: Parses 'dolt remote -v' output and deduplicates fetch/push entries
        /// by keeping only one entry per remote name.
        /// Enhanced parsing supports both TAB and space separation for maximum compatibility.
        /// </summary>
        public async Task<IEnumerable<RemoteInfo>> ListRemotesAsync()
        {
            var result = await ExecuteDoltCommandAsync("remote", "-v");
            if (!result.Success)
            {
                if (_debugLogging)
                {
                    _logger.LogWarning("[DoltCli.ListRemotesAsync] Command failed: {Error}", result.Error);
                }
                return Enumerable.Empty<RemoteInfo>();
            }

            if (_debugLogging)
            {
                _logger.LogDebug("[DoltCli.ListRemotesAsync] Raw output: '{Output}'", result.Output);
            }

            var remotes = new Dictionary<string, string>();
            var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    // Enhanced parsing: handle both TAB and multiple spaces using regex split
                    var parts = Regex.Split(line.Trim(), @"\s+", RegexOptions.None, TimeSpan.FromSeconds(1));
                    if (parts.Length >= 2)
                    {
                        var name = parts[0];
                        var urlWithDirection = parts[1];
                        
                        // Extract URL (remove direction like "(fetch)" or "(push)")
                        var url = urlWithDirection.Split(' ')[0];
                        
                        // Validate URL format
                        if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(name))
                        {
                            remotes[name] = url; // De-duplicate fetch/push entries
                        }
                        else if (_debugLogging)
                        {
                            _logger.LogWarning("[DoltCli.ListRemotesAsync] Skipped invalid entry - name: '{Name}', url: '{Url}'", name, url);
                        }
                    }
                    else if (_debugLogging)
                    {
                        _logger.LogWarning("[DoltCli.ListRemotesAsync] Skipped malformed line: '{Line}' (only {Count} parts)", line, parts.Length);
                    }
                }
                catch (Exception ex) when (ex is RegexMatchTimeoutException || ex is ArgumentException)
                {
                    if (_debugLogging)
                    {
                        _logger.LogWarning(ex, "[DoltCli.ListRemotesAsync] Failed to parse line: '{Line}'", line);
                    }
                    // Continue processing other lines
                }
            }

            if (_debugLogging)
            {
                _logger.LogDebug("[DoltCli.ListRemotesAsync] Found {Count} remotes: {RemoteNames}", 
                    remotes.Count, string.Join(", ", remotes.Keys));
            }

            return remotes.Select(kvp => new RemoteInfo(kvp.Key, kvp.Value));
        }

        public async Task<PushResult> PushAsync(string remote = "origin", string? branch = null)
        {
            var commandResult = branch != null
                ? await ExecuteDoltCommandAsync("push", remote, branch)
                : await ExecuteDoltCommandAsync("push", remote);

            // Analyze the push output to create structured result
            var pushResult = PushResultAnalyzer.AnalyzePushOutput(commandResult, _logger);

            // Calculate commit count if we have a commit range
            if (pushResult.CommitsPushed == -1 && pushResult.FromCommitHash != null && pushResult.ToCommitHash != null)
            {
                var commitCount = await PushResultAnalyzer.CalculateCommitCount(this, pushResult.FromCommitHash, pushResult.ToCommitHash);
                pushResult = pushResult with { CommitsPushed = commitCount };
            }

            return pushResult;
        }

        /// <summary>
        /// Implementation Note: Analyzes command output for fast-forward and conflict indicators
        /// using string matching patterns. Success is false if conflicts are detected.
        /// </summary>
        public async Task<PullResult> PullAsync(string remote = "origin", string? branch = null)
        {
            var result = branch != null
                ? await ExecuteDoltCommandAsync("pull", remote, branch)
                : await ExecuteDoltCommandAsync("pull", remote);

            // Parse output for fast-forward and conflict indicators
            var wasFastForward = result.Output.Contains("Fast-forward");
            var hasConflicts = result.Output.Contains("CONFLICT") || result.Output.Contains("conflict");

            return new PullResult(
                result.Success && !hasConflicts,
                wasFastForward,
                hasConflicts,
                result.Success ? result.Output : result.Error
            );
        }

        public async Task<DoltCommandResult> FetchAsync(string remote = "origin")
        {
            return await ExecuteDoltCommandAsync("fetch", remote);
        }

        // ==================== Merge Operations ====================

        public async Task<MergeResult> MergeAsync(string sourceBranch)
        {
            var result = await ExecuteDoltCommandAsync("merge", sourceBranch);
            
            var hasConflicts = result.Output.Contains("CONFLICT") || result.Output.Contains("conflict");
            string? mergeCommitHash = null;

            if (result.Success && !hasConflicts)
            {
                // Try to extract merge commit hash
                var match = Regex.Match(result.Output, @"commit\s+([a-f0-9]{32})");
                if (match.Success)
                {
                    mergeCommitHash = match.Groups[1].Value;
                }
            }

            return new MergeResult(
                result.Success && !hasConflicts,
                hasConflicts,
                mergeCommitHash,
                result.Success ? result.Output : result.Error
            );
        }

        public async Task<bool> HasConflictsAsync()
        {
            var result = await ExecuteDoltCommandAsync("conflicts", "cat");
            return result.Success && !string.IsNullOrWhiteSpace(result.Output);
        }

        /// <summary>
        /// PP13-73: Check if a specific table has unresolved merge conflicts.
        /// Uses the dolt_conflicts_tablename system table to check for conflicts.
        /// </summary>
        /// <param name="tableName">Name of the table to check for conflicts</param>
        /// <returns>True if the table has conflicts, false otherwise</returns>
        public async Task<bool> HasConflictsInTableAsync(string tableName)
        {
            try
            {
                // Query the conflict table to see if it has any rows
                var conflictTableName = $"dolt_conflicts_{tableName}";
                var sql = $"SELECT COUNT(*) AS conflict_count FROM {conflictTableName}";

                var count = await ExecuteScalarAsync<int>(sql);
                return count > 0;
            }
            catch (DoltException ex) when (ex.Message.Contains("table not found") ||
                                           ex.Message.Contains("doesn't exist") ||
                                           ex.Message.Contains("Unknown table"))
            {
                // No conflict table exists for this table, so no conflicts
                _logger.LogDebug("[DoltCli.HasConflictsInTableAsync] No conflict table found for {Table}: {Error}",
                    tableName, ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DoltCli.HasConflictsInTableAsync] Error checking conflicts for table {Table}", tableName);
                return false;
            }
        }

        /// <summary>
        /// PP13-73-C2: Check if a merge is currently in progress with unresolved conflicts.
        /// Used to detect stale merge state from failed previous merge attempts.
        /// A merge is considered "in progress" if any dolt_conflicts_* table has rows.
        /// </summary>
        /// <returns>True if there's a merge in progress with unresolved conflicts, false otherwise</returns>
        public async Task<bool> IsMergeInProgressAsync()
        {
            try
            {
                // Use the existing HasConflictsAsync method which is already proven to work
                var hasConflicts = await HasConflictsAsync();
                _logger.LogDebug("[DoltCli.IsMergeInProgressAsync] Merge in progress (HasConflictsAsync): {InProgress}", hasConflicts);

                if (hasConflicts)
                {
                    return true;
                }

                // Also check if there's an active merge by looking at dolt status
                // During a merge, status shows "MERGING" or contains merge information
                var statusResult = await ExecuteDoltCommandAsync("status");
                if (statusResult.Success && statusResult.Output != null)
                {
                    var output = statusResult.Output.ToUpperInvariant();
                    var isMerging = output.Contains("MERGING") ||
                                    output.Contains("MERGE IN PROGRESS") ||
                                    output.Contains("UNMERGED");

                    _logger.LogDebug("[DoltCli.IsMergeInProgressAsync] Status-based merge detection: {IsMerging}", isMerging);
                    return isMerging;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DoltCli.IsMergeInProgressAsync] Error checking merge state");
                return false;
            }
        }

        /// <summary>
        /// PP13-73-C2: Abort an in-progress merge, reverting all changes and clearing conflict state.
        /// Use this when conflict resolution fails and the merge cannot be completed.
        /// </summary>
        /// <returns>Command result indicating success/failure of the abort operation</returns>
        public async Task<DoltCommandResult> MergeAbortAsync()
        {
            _logger.LogInformation("[DoltCli.MergeAbortAsync] Aborting merge in progress");
            var result = await ExecuteDoltCommandAsync("merge", "--abort");

            if (result.Success)
            {
                _logger.LogInformation("[DoltCli.MergeAbortAsync] Merge aborted successfully");
            }
            else
            {
                _logger.LogWarning("[DoltCli.MergeAbortAsync] Failed to abort merge: {Error}", result.Error);
            }

            return result;
        }

        /// <summary>
        /// Implementation Note: This is a simplified implementation that returns placeholder data.
        /// Full implementation would require parsing Dolt's conflict output format,
        /// which varies by table structure and conflict type.
        /// </summary>
        public async Task<IEnumerable<ConflictInfo>> GetConflictsAsync(string tableName)
        {
            var result = await ExecuteDoltCommandAsync("conflicts", "cat", tableName);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return Enumerable.Empty<ConflictInfo>();

            // Parse conflict output format
            var conflicts = new List<ConflictInfo>();
            // Note: Actual parsing would depend on Dolt's conflict output format
            // This is a simplified implementation
            conflicts.Add(new ConflictInfo(
                tableName,
                "row_id",
                new Dictionary<string, object>(),
                new Dictionary<string, object>(),
                new Dictionary<string, object>()
            ));

            return conflicts;
        }

        public async Task<DoltCommandResult> ResolveConflictsAsync(string tableName, ConflictResolution resolution)
        {
            var strategy = resolution == ConflictResolution.Ours ? "--ours" : "--theirs";
            return await ExecuteDoltCommandAsync("conflicts", "resolve", strategy, tableName);
        }

        /// <summary>
        /// Resolves a conflict for a specific document within a table.
        /// This allows individual document resolution without affecting other conflicts in the same table.
        /// For keep_ours: Deletes the conflict row, keeping the document as-is in the working set.
        /// For keep_theirs: Updates the document with "their" values from the conflict table, then deletes the conflict row.
        /// PP13-73: Added collectionName parameter to support composite PK (doc_id, collection_name) and fixed column names.
        /// </summary>
        /// <param name="tableName">Name of the table containing the conflict (typically "documents")</param>
        /// <param name="documentId">ID of the document to resolve</param>
        /// <param name="collectionName">Name of the collection containing the document (required for composite PK)</param>
        /// <param name="resolution">Resolution strategy (Ours or Theirs)</param>
        /// <returns>Result indicating success or failure of the resolution</returns>
        public async Task<DoltCommandResult> ResolveDocumentConflictAsync(
            string tableName,
            string documentId,
            string collectionName,
            ConflictResolution resolution)
        {
            try
            {
                // Escape values for SQL
                var escapedDocId = documentId.Replace("'", "''");
                var escapedCollectionName = collectionName.Replace("'", "''");
                var conflictTableName = $"dolt_conflicts_{tableName}";

                // Build WHERE clause with composite key for both documents table and conflict table
                var docWhereClause = $"doc_id = '{escapedDocId}' AND collection_name = '{escapedCollectionName}'";
                var conflictWhereClause = $"our_doc_id = '{escapedDocId}' AND our_collection_name = '{escapedCollectionName}'";

                if (resolution == ConflictResolution.Ours)
                {
                    // For keep_ours: Simply delete the conflict row
                    // The document in the main table will be kept as-is (our version)
                    var deleteSql = $"DELETE FROM {conflictTableName} WHERE {conflictWhereClause}";
                    _logger.LogDebug("[DoltCli.ResolveDocumentConflictAsync] Resolving with ours for {DocId} in collection {Collection}: {Sql}",
                        documentId, collectionName, deleteSql);

                    // PP13-73-C2: Use transaction wrapper to avoid autocommit blocking during merge
                    var success = await ExecuteInTransactionAsync(deleteSql);
                    if (!success)
                    {
                        var errorMessage = $"Failed to delete conflict row for document {documentId} in collection {collectionName}";
                        _logger.LogWarning("[DoltCli.ResolveDocumentConflictAsync] {Error}", errorMessage);
                        return new DoltCommandResult(false, "", errorMessage, -1);
                    }

                    return new DoltCommandResult(true, $"Resolved conflict for {documentId} in {collectionName} with 'ours' strategy", "", 0);
                }
                else // Theirs
                {
                    // For keep_theirs: Update the document with their values, then delete the conflict
                    // PP13-73: Use proper column names and include collection_name in WHERE clauses

                    // Update all relevant columns from conflict table to main table
                    // The documents table columns are: content, content_hash, title, doc_type, metadata, updated_at
                    // The conflict table has: their_content, their_content_hash, their_title, their_doc_type, their_metadata
                    var updateDocSql = $@"
                        UPDATE {tableName}
                        SET content = (SELECT their_content FROM {conflictTableName} WHERE {conflictWhereClause}),
                            content_hash = (SELECT their_content_hash FROM {conflictTableName} WHERE {conflictWhereClause}),
                            title = (SELECT their_title FROM {conflictTableName} WHERE {conflictWhereClause}),
                            doc_type = (SELECT their_doc_type FROM {conflictTableName} WHERE {conflictWhereClause}),
                            metadata = (SELECT their_metadata FROM {conflictTableName} WHERE {conflictWhereClause}),
                            updated_at = CURRENT_TIMESTAMP
                        WHERE {docWhereClause}";

                    // Delete the conflict row after updating the document
                    var deleteSql = $"DELETE FROM {conflictTableName} WHERE {conflictWhereClause}";

                    _logger.LogDebug("[DoltCli.ResolveDocumentConflictAsync] Updating document {DocId} in collection {Collection} with theirs and deleting conflict: UPDATE: {UpdateSql}, DELETE: {DeleteSql}",
                        documentId, collectionName, updateDocSql, deleteSql);

                    // PP13-73-C2: Execute both UPDATE and DELETE within a single transaction
                    // This avoids the autocommit blocking issue during active merge state
                    var success = await ExecuteInTransactionAsync(updateDocSql, deleteSql);
                    if (!success)
                    {
                        var errorMessage = $"Failed to update document and delete conflict for {documentId} in collection {collectionName}";
                        _logger.LogWarning("[DoltCli.ResolveDocumentConflictAsync] {Error}", errorMessage);
                        return new DoltCommandResult(false, "", errorMessage, -1);
                    }

                    _logger.LogDebug("[DoltCli.ResolveDocumentConflictAsync] Successfully resolved conflict for {DocId} with 'theirs' strategy", documentId);
                    return new DoltCommandResult(true, $"Resolved conflict for {documentId} in {collectionName} with 'theirs' strategy", "", 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve document conflict for {DocId} in {Table} (collection: {Collection})",
                    documentId, tableName, collectionName);
                return new DoltCommandResult(false, "", ex.Message, -1);
            }
        }

        /// <summary>
        /// PP13-73-C3: Generates SQL statements for resolving a document conflict without executing them.
        /// This allows batch resolution to collect all SQL and execute in a single transaction.
        /// </summary>
        /// <param name="tableName">Name of the table containing the conflict (typically "documents")</param>
        /// <param name="documentId">ID of the document to resolve</param>
        /// <param name="collectionName">Name of the collection containing the document</param>
        /// <param name="resolution">Resolution strategy (Ours or Theirs)</param>
        /// <returns>Array of SQL statements for the resolution (empty array if error)</returns>
        public string[] GenerateConflictResolutionSql(
            string tableName,
            string documentId,
            string collectionName,
            ConflictResolution resolution)
        {
            try
            {
                // Escape values for SQL
                var escapedDocId = documentId.Replace("'", "''");
                var escapedCollectionName = collectionName.Replace("'", "''");
                var conflictTableName = $"dolt_conflicts_{tableName}";

                // Build WHERE clauses with composite key
                var docWhereClause = $"doc_id = '{escapedDocId}' AND collection_name = '{escapedCollectionName}'";
                var conflictWhereClause = $"our_doc_id = '{escapedDocId}' AND our_collection_name = '{escapedCollectionName}'";

                if (resolution == ConflictResolution.Ours)
                {
                    // For keep_ours: Simply delete the conflict row
                    var deleteSql = $"DELETE FROM {conflictTableName} WHERE {conflictWhereClause}";
                    _logger.LogDebug("[DoltCli.GenerateConflictResolutionSql] Generated keep_ours SQL for {DocId} in {Collection}: {Sql}",
                        documentId, collectionName, deleteSql);
                    return new[] { deleteSql };
                }
                else // Theirs
                {
                    // For keep_theirs: Update the document with their values, then delete the conflict
                    var updateDocSql = $@"
                        UPDATE {tableName}
                        SET content = (SELECT their_content FROM {conflictTableName} WHERE {conflictWhereClause}),
                            content_hash = (SELECT their_content_hash FROM {conflictTableName} WHERE {conflictWhereClause}),
                            title = (SELECT their_title FROM {conflictTableName} WHERE {conflictWhereClause}),
                            doc_type = (SELECT their_doc_type FROM {conflictTableName} WHERE {conflictWhereClause}),
                            metadata = (SELECT their_metadata FROM {conflictTableName} WHERE {conflictWhereClause}),
                            updated_at = CURRENT_TIMESTAMP
                        WHERE {docWhereClause}";

                    var deleteSql = $"DELETE FROM {conflictTableName} WHERE {conflictWhereClause}";

                    _logger.LogDebug("[DoltCli.GenerateConflictResolutionSql] Generated keep_theirs SQL for {DocId} in {Collection}: UPDATE + DELETE",
                        documentId, collectionName);
                    return new[] { updateDocSql, deleteSql };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DoltCli.GenerateConflictResolutionSql] Error generating SQL for {DocId} in {Collection}",
                    documentId, collectionName);
                return Array.Empty<string>();
            }
        }

        // ==================== Diff Operations ====================

        public async Task<DiffSummary> GetWorkingDiffAsync()
        {
            var result = await ExecuteDoltCommandAsync("diff", "--stat");
            if (!result.Success)
                return new DiffSummary(0, 0, 0, 0);

            // Parse diff stat output
            var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var summaryLine = lines.LastOrDefault() ?? "";
            
            // Extract numbers from summary line
            var added = 0;
            var deleted = 0;
            var modified = 0;
            
            var addMatch = Regex.Match(summaryLine, @"(\d+)\s+insertion");
            if (addMatch.Success)
                added = int.Parse(addMatch.Groups[1].Value);
                
            var delMatch = Regex.Match(summaryLine, @"(\d+)\s+deletion");
            if (delMatch.Success)
                deleted = int.Parse(delMatch.Groups[1].Value);

            return new DiffSummary(lines.Length - 1, added, modified, deleted);
        }

        /// <summary>
        /// Implementation Note: Uses Dolt's DOLT_DIFF SQL function for reliable diff data.
        /// The returned DiffRow objects have simplified metadata - full content hashes
        /// and detailed content would require additional queries or table schema inspection.
        /// </summary>
        public async Task<IEnumerable<DiffRow>> GetTableDiffAsync(string fromCommit, string toCommit, string tableName)
        {
            var sql = $"SELECT * FROM DOLT_DIFF('{fromCommit}', '{toCommit}', '{tableName}')";
            var json = await ExecuteSqlJsonAsync(sql);
            
            var rows = new List<DiffRow>();
            using var doc = JsonDocument.Parse(json);
            
            if (doc.RootElement.TryGetProperty("rows", out var rowsElement))
            {
                foreach (var row in rowsElement.EnumerateArray())
                {
                    var diffType = row.TryGetProperty("diff_type", out var dt) ? dt.GetString() : "";
                    var sourceId = row.TryGetProperty("id", out var id) ? id.GetString() : "";
                    
                    rows.Add(new DiffRow(
                        diffType,
                        sourceId,
                        "", // FromContentHash - would need additional columns
                        "", // ToContentHash - would need additional columns
                        "", // ToContent - would need additional columns
                        new Dictionary<string, object>() // Metadata
                    ));
                }
            }

            return rows;
        }

        // ==================== Reset Operations ====================

        public async Task<DoltCommandResult> ResetHardAsync(string commitHash)
        {
            return await ExecuteDoltCommandAsync("reset", "--hard", commitHash);
        }

        public async Task<DoltCommandResult> ResetSoftAsync(string commitRef = "HEAD~1")
        {
            return await ExecuteDoltCommandAsync("reset", "--soft", commitRef);
        }

        // ==================== SQL Operations ====================

        /// <summary>
        /// Detects if a SQL statement is a DDL (Data Definition Language) operation
        /// that typically doesn't return row data.
        /// </summary>
        /// <param name="sql">SQL statement to analyze</param>
        /// <returns>True if the statement is DDL, false otherwise</returns>
        private bool IsDdlStatement(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return false;

            var trimmedSql = sql.Trim().ToUpperInvariant();
            
            // Common DDL keywords that don't return data rows
            var ddlKeywords = new[]
            {
                "CREATE TABLE", "CREATE INDEX", "CREATE VIEW", "CREATE TRIGGER", "CREATE PROCEDURE",
                "DROP TABLE", "DROP INDEX", "DROP VIEW", "DROP TRIGGER", "DROP PROCEDURE",
                "ALTER TABLE", "ALTER INDEX", "ALTER VIEW",
                "RENAME TABLE",
                "TRUNCATE TABLE"
            };

            return ddlKeywords.Any(keyword => trimmedSql.StartsWith(keyword));
        }

        /// <summary>
        /// Suggests the appropriate method to use based on SQL statement type.
        /// </summary>
        /// <param name="sql">SQL statement to analyze</param>
        /// <returns>Suggested method name and description</returns>
        private string GetSuggestedMethod(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return "ExecuteAsync() for general SQL operations";

            var trimmedSql = sql.Trim().ToUpperInvariant();
            
            if (IsDdlStatement(sql))
                return "ExecuteAsync() for DDL statements (CREATE, DROP, ALTER, etc.)";
            
            if (trimmedSql.StartsWith("SELECT"))
            {
                // Check if it's a scalar query (single value)
                if (trimmedSql.Contains("COUNT(") || trimmedSql.Contains("MAX(") || 
                    trimmedSql.Contains("MIN(") || trimmedSql.Contains("SUM(") || 
                    trimmedSql.Contains("AVG(") || trimmedSql.Contains(" LIMIT 1"))
                {
                    return "ExecuteScalarAsync<T>() for single-value queries, or QueryAsync<T>() for row data";
                }
                return "QueryAsync<T>() for SELECT statements returning multiple rows";
            }
            
            if (trimmedSql.StartsWith("INSERT") || trimmedSql.StartsWith("UPDATE") || 
                trimmedSql.StartsWith("DELETE") || trimmedSql.StartsWith("REPLACE"))
            {
                return "ExecuteAsync() for DML statements (INSERT, UPDATE, DELETE, etc.)";
            }
            
            return "ExecuteAsync() for general SQL operations";
        }

        public async Task<string> QueryJsonAsync(string sql)
        {
            return await ExecuteSqlJsonAsync(sql);
        }

        /// <summary>
        /// Executes a SQL query and returns the results as typed objects.
        /// 
        /// IMPORTANT: Use this method for SELECT statements that return row data.
        /// For DDL operations (CREATE, DROP, ALTER) use ExecuteAsync() instead.
        /// For single-value queries, consider using ExecuteScalarAsync&lt;T&gt;().
        /// 
        /// This method handles empty responses gracefully and provides guidance
        /// when inappropriate SQL statement types are detected.
        /// </summary>
        /// <typeparam name="T">The type to deserialize each row into</typeparam>
        /// <param name="sql">SQL SELECT statement</param>
        /// <returns>Collection of typed objects representing the query results</returns>
        /// <exception cref="DoltException">When SQL execution fails</exception>
        /// <exception cref="InvalidOperationException">When used with inappropriate SQL statements</exception>
        public async Task<IEnumerable<T>> QueryAsync<T>(string sql) where T : new()
        {
            // Detect potential misuse and provide guidance
            if (IsDdlStatement(sql))
            {
                var suggestion = GetSuggestedMethod(sql);
                _logger?.LogWarning("QueryAsync<T>() called with DDL statement. Consider using {Suggestion}", suggestion);
                throw new InvalidOperationException(
                    $"QueryAsync<T>() is designed for SELECT statements that return row data. " +
                    $"For DDL statements like this one, use {suggestion}. " +
                    $"Statement: {sql.Substring(0, Math.Min(50, sql.Length))}...");
            }

            var json = await ExecuteSqlJsonAsync(sql);
            var results = new List<T>();
            
            // Handle empty responses (common for DDL statements or empty result sets)
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger?.LogDebug("SQL command returned no output. Statement: {SqlPreview}", 
                    sql.Substring(0, Math.Min(100, sql.Length)));
                
                // For DDL statements that somehow got through, provide guidance
                if (IsDdlStatement(sql))
                {
                    var suggestion = GetSuggestedMethod(sql);
                    throw new InvalidOperationException(
                        $"DDL statement returned no output as expected. Use {suggestion} instead. " +
                        $"Statement: {sql.Substring(0, Math.Min(50, sql.Length))}...");
                }
                
                // For other statements (like SELECT with no results), return empty list
                return results;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("rows", out var rows))
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        var jsonString = row.GetRawText();
                        var obj = JsonSerializer.Deserialize<T>(jsonString, _jsonOptions);
                        if (obj != null)
                            results.Add(obj);
                    }
                }

                return results;
            }
            catch (JsonException ex) when (string.IsNullOrWhiteSpace(json))
            {
                // This catch block handles the original issue: empty JSON causing parse errors
                _logger?.LogWarning("Empty or invalid JSON response for SQL statement. " +
                    "This often indicates a DDL statement was used with QueryAsync<T>(). " +
                    "Statement: {SqlPreview}", sql.Substring(0, Math.Min(100, sql.Length)));
                
                var suggestion = GetSuggestedMethod(sql);
                throw new InvalidOperationException(
                    $"Received empty or invalid JSON response. This typically happens when using " +
                    $"QueryAsync<T>() with DDL statements. Use {suggestion} instead. " +
                    $"Original error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Executes SQL statements that don't return row data (DDL, DML operations).
        /// 
        /// RECOMMENDED FOR:
        /// - DDL operations: CREATE TABLE, DROP TABLE, ALTER TABLE, etc.
        /// - DML operations: INSERT, UPDATE, DELETE, etc.
        /// - Administrative commands: TRUNCATE, GRANT, REVOKE, etc.
        /// 
        /// RETURNS: Number of affected rows (when available) or 1 for successful DDL operations.
        /// 
        /// For SELECT statements that return data, use QueryAsync&lt;T&gt;() instead.
        /// For single-value SELECT queries, consider ExecuteScalarAsync&lt;T&gt;().
        /// 
        /// Implementation Note: Dolt CLI doesn't always return affected row counts for DML operations.
        /// This method first tries to parse MySQL-style "X rows affected" output, then falls back
        /// to heuristic analysis: if the SQL contains INSERT/UPDATE/DELETE and succeeds, assumes 1+ rows affected.
        /// This is a conservative estimate to maintain compatibility with standard SQL expectations.
        /// </summary>
        /// <param name="sql">SQL statement to execute (DDL or DML)</param>
        /// <returns>Number of affected rows, or 1 for successful DDL operations</returns>
        /// <exception cref="DoltException">When SQL execution fails</exception>
        public async Task<int> ExecuteAsync(string sql)
        {
            var result = await ExecuteDoltCommandAsync("sql", "-q", sql);
            if (!result.Success)
            {
                throw new DoltException($"SQL execution failed: {result.Error}", result.ExitCode, result.Error);
            }

            // Try to parse affected rows from output (MySQL-style)
            var match = Regex.Match(result.Output, @"(\d+)\s+rows? affected");
            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value);
            }

            // Dolt doesn't always return rows affected for INSERT/UPDATE/DELETE
            // If the command succeeded and contains INSERT/UPDATE/DELETE, assume at least 1 row
            var upperSql = sql.ToUpperInvariant();
            if (upperSql.Contains("INSERT") || upperSql.Contains("UPDATE") || upperSql.Contains("DELETE"))
            {
                return 1; // Conservative estimate - at least 1 row was affected
            }

            return 0;
        }

        /// <summary>
        /// PP13-73-C2: Executes multiple SQL statements within a transaction.
        /// Required for conflict resolution during merge operations, as Dolt rejects
        /// modifications to dolt_conflicts_* tables when autocommit is enabled.
        ///
        /// PP13-73-C3: Added skipCommit parameter. When resolving merge conflicts,
        /// COMMIT should be skipped because Dolt blocks COMMIT until ALL conflicts
        /// are resolved. The actual commit happens via 'dolt commit' after all
        /// conflicts are resolved.
        ///
        /// Wraps statements with: SET @@autocommit = 0; [statements] [COMMIT if not skipped]; SET @@autocommit = 1;
        ///
        /// RECOMMENDED FOR:
        /// - Merge conflict resolution (DELETE/UPDATE on dolt_conflicts_* tables)
        /// - Multi-statement operations that must succeed or fail together
        /// - Operations during active merge state
        /// </summary>
        /// <param name="sqlStatements">SQL statements to execute within the transaction</param>
        /// <returns>True if all statements succeeded, false otherwise</returns>
        public async Task<bool> ExecuteInTransactionAsync(params string[] sqlStatements)
        {
            return await ExecuteInTransactionAsync(skipCommit: false, sqlStatements);
        }

        /// <summary>
        /// PP13-73-C3: Overload with skipCommit parameter for merge conflict resolution.
        /// When resolving merge conflicts, COMMIT should be skipped because Dolt blocks
        /// COMMIT until ALL conflicts are resolved. The actual commit happens via 'dolt commit'.
        /// </summary>
        public async Task<bool> ExecuteInTransactionAsync(bool skipCommit, params string[] sqlStatements)
        {
            if (sqlStatements == null || sqlStatements.Length == 0)
            {
                _logger.LogWarning("[DoltCli.ExecuteInTransactionAsync] No SQL statements provided");
                return false;
            }

            // Build transaction-wrapped SQL
            // Note: Dolt requires autocommit=0 for conflict table modifications during merge
            var transactionSql = new StringBuilder();
            transactionSql.AppendLine("SET @@autocommit = 0;");

            foreach (var statement in sqlStatements)
            {
                var trimmedStatement = statement.Trim();
                if (!string.IsNullOrEmpty(trimmedStatement))
                {
                    // Ensure statement ends with semicolon
                    if (!trimmedStatement.EndsWith(";"))
                    {
                        trimmedStatement += ";";
                    }
                    transactionSql.AppendLine(trimmedStatement);
                }
            }

            // PP13-73-C3: Handle COMMIT based on skipCommit flag
            // When skipCommit=false (normal use), we commit and re-enable autocommit
            // When skipCommit=true (merge conflict resolution), we enable dolt_allow_commit_conflicts
            // to allow committing the conflict resolution changes, then commit
            if (!skipCommit)
            {
                transactionSql.AppendLine("COMMIT;");
                transactionSql.AppendLine("SET @@autocommit = 1;");
            }
            else
            {
                // PP13-73-C3: For merge conflict resolution, enable dolt_allow_commit_conflicts
                // This allows us to commit even while other conflicts may exist
                transactionSql.AppendLine("SET @@dolt_allow_commit_conflicts = 1;");
                transactionSql.AppendLine("COMMIT;");
                transactionSql.AppendLine("SET @@dolt_allow_commit_conflicts = 0;");
                transactionSql.AppendLine("SET @@autocommit = 1;");
            }

            var fullSql = transactionSql.ToString();
            _logger.LogDebug("[DoltCli.ExecuteInTransactionAsync] Executing transaction SQL (skipCommit={SkipCommit}):\n{Sql}", skipCommit, fullSql);

            try
            {
                var result = await ExecuteDoltCommandAsync("sql", "-q", fullSql);
                if (!result.Success)
                {
                    _logger.LogError("[DoltCli.ExecuteInTransactionAsync] Transaction failed: {Error}", result.Error);
                    return false;
                }

                _logger.LogDebug("[DoltCli.ExecuteInTransactionAsync] Transaction completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DoltCli.ExecuteInTransactionAsync] Exception during transaction execution");
                return false;
            }
        }

        /// <summary>
        /// Executes a SQL query that returns a single scalar value.
        ///
        /// RECOMMENDED FOR:
        /// - Aggregate functions: COUNT(*), MAX(id), MIN(date), SUM(amount), AVG(score)
        /// - Single-value queries: SELECT active_branch(), SELECT DOLT_HASHOF('HEAD')
        /// - Existence checks: SELECT COUNT(*) FROM table WHERE condition
        /// - Queries with LIMIT 1 that return a single value
        ///
        /// For queries returning multiple rows, use QueryAsync&lt;T&gt;() instead.
        /// For DDL/DML statements, use ExecuteAsync() instead.
        /// </summary>
        /// <typeparam name="T">The type of the scalar value to return</typeparam>
        /// <param name="sql">SQL SELECT statement that returns a single value</param>
        /// <returns>The scalar value from the first column of the first row</returns>
        /// <exception cref="DoltException">When SQL execution fails</exception>
        /// <exception cref="InvalidOperationException">When no value is returned</exception>
        public async Task<T> ExecuteScalarAsync<T>(string sql)
        {
            var json = await ExecuteSqlJsonAsync(sql);
            
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("rows", out var rows))
            {
                var firstRow = rows.EnumerateArray().FirstOrDefault();
                if (firstRow.ValueKind != JsonValueKind.Undefined)
                {
                    // Get the first property value
                    var firstProp = firstRow.EnumerateObject().FirstOrDefault();
                    if (firstProp.Value.ValueKind != JsonValueKind.Undefined)
                    {
                        var jsonString = firstProp.Value.GetRawText();
                        return JsonSerializer.Deserialize<T>(jsonString, _jsonOptions)!;
                    }
                }
            }

            return default(T)!;
        }

        /// <summary>
        /// Execute a raw Dolt command with arbitrary arguments
        /// </summary>
        /// <param name="args">The command arguments to pass to dolt</param>
        /// <returns>Raw command result from Dolt</returns>
        public async Task<DoltCommandResult> ExecuteRawCommandAsync(params string[] args)
        {
            return await ExecuteDoltCommandAsync(args);
        }

        /// <summary>
        /// Preview merge conflicts without performing the actual merge
        /// Uses Dolt's DOLT_PREVIEW_MERGE_CONFLICTS_SUMMARY function if available
        /// </summary>
        /// <param name="sourceBranch">Source branch to merge from</param>
        /// <param name="targetBranch">Target branch to merge into</param>
        /// <returns>JSON string containing conflict summary from Dolt</returns>
        public async Task<string> PreviewMergeConflictsAsync(string sourceBranch, string targetBranch)
        {
            try
            {
                // Try using Dolt's merge conflict preview function if available
                var sql = $"SELECT * FROM DOLT_PREVIEW_MERGE_CONFLICTS_SUMMARY('{targetBranch}', '{sourceBranch}')";
                var result = await QueryJsonAsync(sql);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DOLT_PREVIEW_MERGE_CONFLICTS_SUMMARY not available, using fallback");
                
                // Fallback: return empty array indicating no conflicts detected via preview
                // In a more complete implementation, this could do basic diff analysis
                return "[]";
            }
        }

        /// <summary>
        /// Get detailed conflict information from conflict tables
        /// Queries the dolt_conflicts_{tableName} table for specific conflict details
        /// </summary>
        /// <param name="tableName">Name of the table to get conflict details for</param>
        /// <returns>Collection of dictionaries containing conflict data</returns>
        public async Task<IEnumerable<Dictionary<string, object>>> GetConflictDetailsAsync(string tableName)
        {
            try
            {
                var sql = $"SELECT * FROM dolt_conflicts_{tableName}";
                var json = await QueryJsonAsync(sql);
                
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("rows", out var rows))
                {
                    var conflicts = new List<Dictionary<string, object>>();
                    
                    foreach (var row in rows.EnumerateArray())
                    {
                        var conflict = new Dictionary<string, object>();
                        
                        foreach (var prop in row.EnumerateObject())
                        {
                            // Convert JsonElement to appropriate object type
                            object? value = prop.Value.ValueKind switch
                            {
                                JsonValueKind.String => prop.Value.GetString(),
                                JsonValueKind.Number => prop.Value.GetDecimal(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                JsonValueKind.Null => null,
                                _ => prop.Value.GetRawText()
                            };
                            
                            conflict[prop.Name] = value;
                        }
                        
                        conflicts.Add(conflict);
                    }
                    
                    return conflicts;
                }
                
                return new List<Dictionary<string, object>>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get conflict details for table {TableName}", tableName);
                return new List<Dictionary<string, object>>();
            }
        }

        /// <summary>
        /// Execute custom SQL for resolving specific conflicts
        /// Allows fine-grained control over conflict resolution beyond simple ours/theirs
        /// </summary>
        /// <param name="sql">SQL statement for conflict resolution</param>
        /// <returns>Number of rows affected by the resolution</returns>
        public async Task<int> ExecuteConflictResolutionAsync(string sql)
        {
            try
            {
                _logger.LogDebug("Executing conflict resolution SQL: {Sql}", sql);
                return await ExecuteAsync(sql);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute conflict resolution SQL");
                throw new DoltException($"Conflict resolution failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Strips ANSI color codes from git output and extracts clean commit message
        /// </summary>
        /// <param name="input">Raw git output with ANSI codes</param>
        /// <returns>Clean commit message without ANSI codes or branch info</returns>
        private static string StripAnsiColorCodes(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Remove ANSI color codes (pattern: \[\d+(;\d+)*m)
            var ansiPattern = @"\x1b\[\d+(;\d+)*m";
            var withoutAnsi = System.Text.RegularExpressions.Regex.Replace(input, ansiPattern, string.Empty);

            // Also handle the bracket-only format [0m[33m etc
            var bracketPattern = @"\[\d+(;\d+)*m";
            withoutAnsi = System.Text.RegularExpressions.Regex.Replace(withoutAnsi, bracketPattern, string.Empty);

            // Remove git branch information in parentheses like "(HEAD -> main)"
            var branchPattern = @"\([^)]*\)\s*";
            withoutAnsi = System.Text.RegularExpressions.Regex.Replace(withoutAnsi, branchPattern, string.Empty);

            return withoutAnsi.Trim();
        }

        /// <summary>
        /// Get a document's content at a specific commit using Dolt's AS OF clause
        /// Used for three-way merge conflict analysis
        /// </summary>
        /// <param name="tableName">Name of the table containing the document</param>
        /// <param name="documentId">ID of the document to retrieve</param>
        /// <param name="commitHash">Commit hash to retrieve the document at</param>
        /// <returns>Document content if found, null otherwise</returns>
        public async Task<DocumentContent?> GetDocumentAtCommitAsync(string tableName, string documentId, string commitHash)
        {
            try
            {
                // Escape the document ID for SQL
                var escapedDocId = documentId.Replace("'", "''");

                // Query using Dolt's AS OF clause to get historical document state
                var sql = $"SELECT * FROM `{tableName}` AS OF '{commitHash}' WHERE doc_id = '{escapedDocId}'";
                var json = await QueryJsonAsync(sql);

                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("rows", out var rows))
                {
                    var rowArray = rows.EnumerateArray().ToList();
                    if (rowArray.Any())
                    {
                        var row = rowArray.First();
                        var content = new DocumentContent
                        {
                            Exists = true,
                            CommitHash = commitHash
                        };

                        // Extract content from possible field names
                        if (row.TryGetProperty("content", out var contentProp))
                        {
                            content.Content = contentProp.GetString();
                        }
                        else if (row.TryGetProperty("document_text", out contentProp))
                        {
                            content.Content = contentProp.GetString();
                        }
                        else if (row.TryGetProperty("document_content", out contentProp))
                        {
                            content.Content = contentProp.GetString();
                        }

                        // Extract metadata from other fields
                        foreach (var prop in row.EnumerateObject())
                        {
                            if (prop.Name != "content" && prop.Name != "document_text" &&
                                prop.Name != "document_content" && prop.Name != "doc_id")
                            {
                                object? value = prop.Value.ValueKind switch
                                {
                                    JsonValueKind.String => prop.Value.GetString(),
                                    JsonValueKind.Number => prop.Value.GetDecimal(),
                                    JsonValueKind.True => true,
                                    JsonValueKind.False => false,
                                    JsonValueKind.Null => null,
                                    _ => prop.Value.GetRawText()
                                };
                                if (value != null)
                                {
                                    content.Metadata[prop.Name] = value;
                                }
                            }
                        }

                        return content;
                    }
                }

                // Document doesn't exist at this commit
                return new DocumentContent { Exists = false, CommitHash = commitHash };
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not get document {DocId} from {Table} at {Commit}: {Error}",
                    documentId, tableName, commitHash, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Get the merge base commit between two branches
        /// </summary>
        /// <param name="branch1">First branch name</param>
        /// <param name="branch2">Second branch name</param>
        /// <returns>Commit hash of the merge base</returns>
        public async Task<string?> GetMergeBaseAsync(string branch1, string branch2)
        {
            try
            {
                var result = await ExecuteDoltCommandAsync("merge-base", branch1, branch2);
                if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
                {
                    return result.Output.Trim();
                }

                _logger.LogWarning("Could not determine merge base between {Branch1} and {Branch2}: {Error}",
                    branch1, branch2, result.Error);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get merge base between {Branch1} and {Branch2}", branch1, branch2);
                return null;
            }
        }

        /// <summary>
        /// Get document changes between two commits for a specific table
        /// Uses Dolt's DOLT_DIFF function to identify added, modified, and deleted documents
        /// </summary>
        /// <param name="fromCommit">Starting commit hash</param>
        /// <param name="toCommit">Ending commit hash</param>
        /// <param name="tableName">Table to analyze changes for</param>
        /// <returns>Dictionary of document IDs to their change type (added/modified/deleted)</returns>
        public async Task<Dictionary<string, string>> GetDocumentChangesBetweenCommitsAsync(
            string fromCommit,
            string toCommit,
            string tableName)
        {
            var changes = new Dictionary<string, string>();

            try
            {
                // Query the DOLT_DIFF function to get all changes between commits
                var sql = $"SELECT diff_type, from_doc_id, to_doc_id FROM DOLT_DIFF('{fromCommit}', '{toCommit}', '{tableName}')";
                var json = await QueryJsonAsync(sql);

                if (string.IsNullOrWhiteSpace(json))
                {
                    return changes;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("rows", out var rows))
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        var diffType = row.TryGetProperty("diff_type", out var dt) ? dt.GetString() : null;
                        var fromDocId = row.TryGetProperty("from_doc_id", out var fromId) ? fromId.GetString() : null;
                        var toDocId = row.TryGetProperty("to_doc_id", out var toId) ? toId.GetString() : null;

                        // Determine the document ID and change type
                        var docId = toDocId ?? fromDocId;
                        if (string.IsNullOrEmpty(docId) || string.IsNullOrEmpty(diffType))
                        {
                            continue;
                        }

                        // Normalize diff type names
                        var normalizedType = diffType.ToLowerInvariant() switch
                        {
                            "added" or "insert" => "added",
                            "modified" or "update" => "modified",
                            "deleted" or "delete" or "removed" => "deleted",
                            _ => diffType.ToLowerInvariant()
                        };

                        changes[docId] = normalizedType;
                    }
                }

                _logger.LogDebug("Found {Count} document changes in {Table} between {From} and {To}",
                    changes.Count, tableName, fromCommit, toCommit);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not get document changes for {Table} between {From} and {To}",
                    tableName, fromCommit, toCommit);
            }

            return changes;
        }
    }
}