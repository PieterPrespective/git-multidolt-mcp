using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using DMMS.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DMMS.Services
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
            return await ExecuteDoltCommandAsync("init");
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
        /// to get reliable machine-readable output.
        /// </summary>
        public async Task<string> GetCurrentBranchAsync()
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
                    commits.Add(new CommitInfo(
                        parts[0],
                        parts[1],
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
        /// </summary>
        public async Task<IEnumerable<RemoteInfo>> ListRemotesAsync()
        {
            var result = await ExecuteDoltCommandAsync("remote", "-v");
            if (!result.Success)
                return Enumerable.Empty<RemoteInfo>();

            var remotes = new Dictionary<string, string>();
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 2)
                {
                    var name = parts[0];
                    var urlParts = parts[1].Split(' ');
                    var url = urlParts[0];
                    remotes[name] = url; // De-duplicate fetch/push entries
                }
            }

            return remotes.Select(kvp => new RemoteInfo(kvp.Key, kvp.Value));
        }

        public async Task<DoltCommandResult> PushAsync(string remote = "origin", string? branch = null)
        {
            return branch != null
                ? await ExecuteDoltCommandAsync("push", remote, branch)
                : await ExecuteDoltCommandAsync("push", remote);
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

        public async Task<string> QueryJsonAsync(string sql)
        {
            return await ExecuteSqlJsonAsync(sql);
        }

        public async Task<IEnumerable<T>> QueryAsync<T>(string sql) where T : new()
        {
            var json = await ExecuteSqlJsonAsync(sql);
            var results = new List<T>();
            
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

        /// <summary>
        /// Implementation Note: Dolt CLI doesn't always return affected row counts for DML operations.
        /// This method first tries to parse MySQL-style "X rows affected" output, then falls back
        /// to heuristic analysis: if the SQL contains INSERT/UPDATE/DELETE and succeeds, assumes 1+ rows affected.
        /// This is a conservative estimate to maintain compatibility with standard SQL expectations.
        /// </summary>
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
    }
}