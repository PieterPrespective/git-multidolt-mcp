using DMMS.Models;

namespace DMMS.Services
{
    /// <summary>
    /// Complete Dolt CLI wrapper - all operations via subprocess
    /// </summary>
    public interface IDoltCli
    {
        // ==================== Repository Management ====================
        
        /// <summary>
        /// Check if a Dolt repository is initialized in the configured repository path
        /// </summary>
        /// <returns>True if repository exists and is initialized, false otherwise</returns>
        Task<bool> IsInitializedAsync();
        
        /// <summary>
        /// Initialize a new Dolt repository in the configured repository path
        /// </summary>
        /// <returns>Command result indicating success/failure and any output messages</returns>
        Task<DoltCommandResult> InitAsync();
        
        /// <summary>
        /// Clone a repository from a remote URL (typically DoltHub)
        /// </summary>
        /// <param name="remoteUrl">The remote repository URL (e.g., "dolthub.com/username/repo")</param>
        /// <param name="localPath">Optional local directory name. If null, uses remote repo name</param>
        /// <returns>Command result indicating success/failure of the clone operation</returns>
        Task<DoltCommandResult> CloneAsync(string remoteUrl, string? localPath = null);
        
        /// <summary>
        /// Get the current status of the repository including staged and unstaged changes
        /// </summary>
        /// <returns>Repository status with branch info, staged changes, and modified tables</returns>
        Task<RepositoryStatus> GetStatusAsync();
        
        // ==================== Branch Operations ====================
        
        /// <summary>
        /// Get the name of the currently active branch
        /// </summary>
        /// <returns>The name of the current branch (e.g., "main", "feature/auth")</returns>
        Task<string> GetCurrentBranchAsync();
        
        /// <summary>
        /// List all local branches in the repository
        /// </summary>
        /// <returns>Collection of branch information including names, current status, and last commit hashes</returns>
        Task<IEnumerable<BranchInfo>> ListBranchesAsync();
        
        /// <summary>
        /// Create a new branch from the current HEAD (does not switch to the new branch)
        /// </summary>
        /// <param name="branchName">Name for the new branch</param>
        /// <returns>Command result indicating success/failure of branch creation</returns>
        Task<DoltCommandResult> CreateBranchAsync(string branchName);
        
        /// <summary>
        /// Delete a local branch
        /// </summary>
        /// <param name="branchName">Name of the branch to delete</param>
        /// <param name="force">If true, force delete even if branch has unmerged changes</param>
        /// <returns>Command result indicating success/failure of branch deletion</returns>
        Task<DoltCommandResult> DeleteBranchAsync(string branchName, bool force = false);
        
        /// <summary>
        /// Switch to an existing branch or create and switch to a new branch
        /// </summary>
        /// <param name="branchName">Name of the branch to checkout</param>
        /// <param name="createNew">If true, create the branch if it doesn't exist</param>
        /// <returns>Command result indicating success/failure of the checkout operation</returns>
        Task<DoltCommandResult> CheckoutAsync(string branchName, bool createNew = false);
        
        // ==================== Commit Operations ====================
        
        /// <summary>
        /// Stage all modified tables for the next commit
        /// </summary>
        /// <returns>Command result indicating success/failure of staging operation</returns>
        Task<DoltCommandResult> AddAllAsync();
        
        /// <summary>
        /// Stage specific tables for the next commit
        /// </summary>
        /// <param name="tables">Names of the tables to stage</param>
        /// <returns>Command result indicating success/failure of staging operation</returns>
        Task<DoltCommandResult> AddAsync(params string[] tables);
        
        /// <summary>
        /// Commit all staged changes with a message
        /// </summary>
        /// <param name="message">Commit message describing the changes</param>
        /// <returns>Commit result with success status, commit hash, and message</returns>
        Task<CommitResult> CommitAsync(string message);
        
        /// <summary>
        /// Get the commit hash of the current HEAD
        /// </summary>
        /// <returns>The full commit hash of HEAD</returns>
        Task<string> GetHeadCommitHashAsync();
        
        /// <summary>
        /// Get the commit history starting from HEAD
        /// </summary>
        /// <param name="limit">Maximum number of commits to return (default: 10)</param>
        /// <returns>Collection of commit information including hash, message, author, and date</returns>
        Task<IEnumerable<CommitInfo>> GetLogAsync(int limit = 10);
        
        // ==================== Remote Operations ====================
        
        /// <summary>
        /// Add a remote repository reference
        /// </summary>
        /// <param name="name">Name for the remote (typically "origin")</param>
        /// <param name="url">URL of the remote repository</param>
        /// <returns>Command result indicating success/failure of adding the remote</returns>
        Task<DoltCommandResult> AddRemoteAsync(string name, string url);
        
        /// <summary>
        /// Remove a remote repository reference
        /// </summary>
        /// <param name="name">Name of the remote to remove</param>
        /// <returns>Command result indicating success/failure of removing the remote</returns>
        Task<DoltCommandResult> RemoveRemoteAsync(string name);
        
        /// <summary>
        /// List all configured remote repositories
        /// </summary>
        /// <returns>Collection of remote information including names and URLs</returns>
        Task<IEnumerable<RemoteInfo>> ListRemotesAsync();
        
        /// <summary>
        /// Push the current branch or specified branch to a remote repository
        /// </summary>
        /// <param name="remote">Name of the remote to push to (default: "origin")</param>
        /// <param name="branch">Specific branch to push. If null, pushes current branch</param>
        /// <returns>Command result indicating success/failure of the push operation</returns>
        Task<DoltCommandResult> PushAsync(string remote = "origin", string? branch = null);
        
        /// <summary>
        /// Pull changes from a remote repository and merge into current branch
        /// </summary>
        /// <param name="remote">Name of the remote to pull from (default: "origin")</param>
        /// <param name="branch">Specific branch to pull. If null, pulls current branch</param>
        /// <returns>Pull result with success status, fast-forward indication, and conflict status</returns>
        Task<PullResult> PullAsync(string remote = "origin", string? branch = null);
        
        /// <summary>
        /// Fetch changes from remote repository without merging
        /// </summary>
        /// <param name="remote">Name of the remote to fetch from (default: "origin")</param>
        /// <returns>Command result indicating success/failure of the fetch operation</returns>
        Task<DoltCommandResult> FetchAsync(string remote = "origin");
        
        // ==================== Merge Operations ====================
        
        /// <summary>
        /// Merge a source branch into the current branch
        /// </summary>
        /// <param name="sourceBranch">Name of the branch to merge from</param>
        /// <returns>Merge result with success status, conflict information, and merge commit hash</returns>
        Task<MergeResult> MergeAsync(string sourceBranch);
        
        /// <summary>
        /// Check if there are any unresolved merge conflicts in the repository
        /// </summary>
        /// <returns>True if conflicts exist, false if repository is clean</returns>
        Task<bool> HasConflictsAsync();
        
        /// <summary>
        /// Get detailed information about conflicts in a specific table
        /// </summary>
        /// <param name="tableName">Name of the table to check for conflicts</param>
        /// <returns>Collection of conflict details including conflicting values from each branch</returns>
        Task<IEnumerable<ConflictInfo>> GetConflictsAsync(string tableName);
        
        /// <summary>
        /// Resolve merge conflicts in a table using a resolution strategy
        /// </summary>
        /// <param name="tableName">Name of the table to resolve conflicts for</param>
        /// <param name="resolution">Strategy to use (Ours or Theirs)</param>
        /// <returns>Command result indicating success/failure of conflict resolution</returns>
        Task<DoltCommandResult> ResolveConflictsAsync(string tableName, ConflictResolution resolution);
        
        // ==================== Diff Operations ====================
        
        /// <summary>
        /// Get a summary of uncommitted changes in the working directory
        /// </summary>
        /// <returns>Summary with counts of tables changed and rows added/modified/deleted</returns>
        Task<DiffSummary> GetWorkingDiffAsync();
        
        /// <summary>
        /// Get detailed differences between two commits for a specific table
        /// </summary>
        /// <param name="fromCommit">Starting commit hash to compare from</param>
        /// <param name="toCommit">Ending commit hash to compare to</param>
        /// <param name="tableName">Name of the table to analyze differences for</param>
        /// <returns>Collection of diff rows showing added, modified, or removed records</returns>
        Task<IEnumerable<DiffRow>> GetTableDiffAsync(string fromCommit, string toCommit, string tableName);
        
        // ==================== Reset Operations ====================
        
        /// <summary>
        /// Hard reset to a specific commit, discarding all changes
        /// WARNING: This permanently destroys uncommitted changes
        /// </summary>
        /// <param name="commitHash">The commit hash to reset to</param>
        /// <returns>Command result indicating success/failure of the reset operation</returns>
        Task<DoltCommandResult> ResetHardAsync(string commitHash);
        
        /// <summary>
        /// Soft reset to a previous commit, keeping changes staged
        /// </summary>
        /// <param name="commitRef">Commit reference to reset to (default: "HEAD~1" for previous commit)</param>
        /// <returns>Command result indicating success/failure of the reset operation</returns>
        Task<DoltCommandResult> ResetSoftAsync(string commitRef = "HEAD~1");
        
        // ==================== SQL Operations ====================
        
        /// <summary>
        /// Execute a SQL query and return raw JSON results from Dolt
        /// </summary>
        /// <param name="sql">The SQL query to execute</param>
        /// <returns>JSON string containing query results in Dolt's format</returns>
        Task<string> QueryJsonAsync(string sql);
        
        /// <summary>
        /// Execute a SQL query and deserialize results to typed objects
        /// </summary>
        /// <typeparam name="T">Type to deserialize results into</typeparam>
        /// <param name="sql">The SQL query to execute</param>
        /// <returns>Collection of typed objects representing query results</returns>
        Task<IEnumerable<T>> QueryAsync<T>(string sql) where T : new();
        
        /// <summary>
        /// Execute a SQL statement that modifies data (INSERT/UPDATE/DELETE)
        /// </summary>
        /// <param name="sql">The SQL statement to execute</param>
        /// <returns>Number of rows affected (estimated for Dolt compatibility)</returns>
        Task<int> ExecuteAsync(string sql);
        
        /// <summary>
        /// Execute a SQL query and return a single scalar value
        /// </summary>
        /// <typeparam name="T">Type of the expected return value</typeparam>
        /// <param name="sql">The SQL query to execute (should return a single value)</param>
        /// <returns>The scalar result of the query</returns>
        Task<T> ExecuteScalarAsync<T>(string sql);
    }
}