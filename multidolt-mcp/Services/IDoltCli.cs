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
        /// Check if the Dolt executable is available and accessible
        /// </summary>
        /// <returns>Result indicating if Dolt is available with version info or error message</returns>
        Task<DoltCommandResult> CheckDoltAvailableAsync();
        
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
        /// List all branches including remote tracking branches
        /// </summary>
        /// <returns>Collection of all branch information including remote branches</returns>
        Task<IEnumerable<BranchInfo>> ListAllBranchesAsync();

        /// <summary>
        /// Checks if a branch exists either locally or as a remote tracking branch
        /// </summary>
        /// <param name="branchName">Name of the branch to check</param>
        /// <returns>True if branch exists locally or remotely</returns>
        Task<bool> BranchExistsAsync(string branchName);

        /// <summary>
        /// Gets the actual branch reference for a branch name, resolving remote references if needed
        /// </summary>
        /// <param name="branchName">The branch name to resolve</param>
        /// <returns>The actual branch reference to use, or null if not found</returns>
        Task<string?> ResolveBranchReferenceAsync(string branchName);

        /// <summary>
        /// PP13-72-C4: Check if a branch exists locally (not just as a remote tracking branch).
        /// Used to determine if auto-tracking is needed before merge operations.
        /// </summary>
        /// <param name="branchName">Name of the branch to check</param>
        /// <returns>True if branch exists as a local branch, false if only remote or doesn't exist</returns>
        Task<bool> IsLocalBranchAsync(string branchName);

        /// <summary>
        /// PP13-72-C4: Get the commit hash for a branch without checking it out.
        /// Uses SQL HASHOF() function to retrieve the commit hash directly.
        /// This eliminates side effects from branch checkouts during conflict analysis.
        /// </summary>
        /// <param name="branchName">Name of the branch (local or remote)</param>
        /// <returns>Commit hash, or null if branch not found</returns>
        Task<string?> GetBranchCommitHashAsync(string branchName);

        /// <summary>
        /// PP13-72-C4: Create a local tracking branch from a remote branch.
        /// Equivalent to: dolt checkout -b branchName remotes/origin/branchName
        /// Used to ensure remote branches are locally available for accurate merge preview.
        /// </summary>
        /// <param name="branchName">Name of the branch to track</param>
        /// <param name="remote">Remote name (default: "origin")</param>
        /// <returns>Command result with success/failure status</returns>
        Task<DoltCommandResult> TrackRemoteBranchAsync(string branchName, string remote = "origin");

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
        /// Rename an existing branch to a new name
        /// </summary>
        /// <param name="oldBranchName">Current name of the branch to rename</param>
        /// <param name="newBranchName">New name for the branch</param>
        /// <param name="force">If true, force rename even if new branch name already exists</param>
        /// <returns>Command result indicating success/failure of branch rename</returns>
        Task<DoltCommandResult> RenameBranchAsync(string oldBranchName, string newBranchName, bool force = false);
        
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
        /// <returns>Structured push result with detailed information about the push operation</returns>
        Task<PushResult> PushAsync(string remote = "origin", string? branch = null);
        
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
        /// PP13-73: Check if a specific table has unresolved merge conflicts.
        /// Used to detect auxiliary table conflicts before auto-resolution.
        /// </summary>
        /// <param name="tableName">Name of the table to check for conflicts</param>
        /// <returns>True if the table has conflicts, false otherwise</returns>
        Task<bool> HasConflictsInTableAsync(string tableName);

        /// <summary>
        /// PP13-73-C2: Check if a merge is currently in progress with unresolved conflicts.
        /// Used to detect stale merge state from failed previous merge attempts.
        /// </summary>
        /// <returns>True if there's a merge in progress with unresolved conflicts, false otherwise</returns>
        Task<bool> IsMergeInProgressAsync();

        /// <summary>
        /// PP13-73-C2: Abort an in-progress merge, reverting all changes and clearing conflict state.
        /// Use this when conflict resolution fails and the merge cannot be completed.
        /// </summary>
        /// <returns>Command result indicating success/failure of the abort operation</returns>
        Task<DoltCommandResult> MergeAbortAsync();

        /// <summary>
        /// PP13-73-C2: Executes multiple SQL statements within a transaction.
        /// Required for conflict resolution during merge operations, as Dolt rejects
        /// modifications to dolt_conflicts_* tables when autocommit is enabled.
        /// </summary>
        /// <param name="sqlStatements">SQL statements to execute within the transaction</param>
        /// <returns>True if all statements succeeded, false otherwise</returns>
        Task<bool> ExecuteInTransactionAsync(params string[] sqlStatements);

        /// <summary>
        /// PP13-73-C3: Executes multiple SQL statements within a transaction, with option to skip COMMIT.
        /// For merge conflict resolution, skipCommit should be true because Dolt blocks COMMIT until
        /// ALL conflicts are resolved. The actual commit happens via 'dolt commit'.
        /// </summary>
        /// <param name="skipCommit">If true, skips the COMMIT statement (use for merge conflict resolution)</param>
        /// <param name="sqlStatements">SQL statements to execute within the transaction</param>
        /// <returns>True if all statements succeeded, false otherwise</returns>
        Task<bool> ExecuteInTransactionAsync(bool skipCommit, params string[] sqlStatements);

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

        /// <summary>
        /// Resolve a conflict for a specific document within a table.
        /// This allows individual document resolution without affecting other conflicts in the same table.
        /// PP13-73: Added collectionName parameter to support composite PK (doc_id, collection_name).
        /// </summary>
        /// <param name="tableName">Name of the table containing the conflict (typically "documents")</param>
        /// <param name="documentId">ID of the document to resolve</param>
        /// <param name="collectionName">Name of the collection containing the document (required for composite PK)</param>
        /// <param name="resolution">Resolution strategy (Ours or Theirs)</param>
        /// <returns>Command result indicating success/failure of the resolution</returns>
        Task<DoltCommandResult> ResolveDocumentConflictAsync(string tableName, string documentId, string collectionName, ConflictResolution resolution);

        /// <summary>
        /// PP13-73-C3: Generates SQL statements for resolving a document conflict without executing them.
        /// This allows batch resolution to collect all SQL and execute in a single transaction.
        /// Dolt requires ALL conflicts to be resolved before COMMIT is allowed.
        /// </summary>
        /// <param name="tableName">Name of the table containing the conflict (typically "documents")</param>
        /// <param name="documentId">ID of the document to resolve</param>
        /// <param name="collectionName">Name of the collection containing the document</param>
        /// <param name="resolution">Resolution strategy (Ours or Theirs)</param>
        /// <returns>Array of SQL statements for the resolution (empty array if error)</returns>
        string[] GenerateConflictResolutionSql(string tableName, string documentId, string collectionName, ConflictResolution resolution);

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
        
        /// <summary>
        /// Execute a raw Dolt command with arbitrary arguments
        /// </summary>
        /// <param name="args">The command arguments to pass to dolt</param>
        /// <returns>Raw command result from Dolt</returns>
        Task<DoltCommandResult> ExecuteRawCommandAsync(params string[] args);
        
        // ==================== Enhanced Merge Operations ====================
        
        /// <summary>
        /// Preview merge conflicts without performing the actual merge
        /// Uses Dolt's DOLT_PREVIEW_MERGE_CONFLICTS_SUMMARY function if available
        /// </summary>
        /// <param name="sourceBranch">Source branch to merge from</param>
        /// <param name="targetBranch">Target branch to merge into</param>
        /// <returns>JSON string containing conflict summary from Dolt</returns>
        Task<string> PreviewMergeConflictsAsync(string sourceBranch, string targetBranch);
        
        /// <summary>
        /// Get detailed conflict information from conflict tables
        /// Queries the dolt_conflicts_{tableName} table for specific conflict details
        /// </summary>
        /// <param name="tableName">Name of the table to get conflict details for</param>
        /// <returns>Collection of dictionaries containing conflict data</returns>
        Task<IEnumerable<Dictionary<string, object>>> GetConflictDetailsAsync(string tableName);
        
        /// <summary>
        /// Execute custom SQL for resolving specific conflicts
        /// Allows fine-grained control over conflict resolution beyond simple ours/theirs
        /// </summary>
        /// <param name="sql">SQL statement for conflict resolution</param>
        /// <returns>Number of rows affected by the resolution</returns>
        Task<int> ExecuteConflictResolutionAsync(string sql);

        /// <summary>
        /// Get a document's content at a specific commit using Dolt's AS OF clause
        /// Used for three-way merge conflict analysis
        /// </summary>
        /// <param name="tableName">Name of the table containing the document</param>
        /// <param name="documentId">ID of the document to retrieve</param>
        /// <param name="commitHash">Commit hash to retrieve the document at</param>
        /// <returns>Document content if found, null otherwise</returns>
        Task<DocumentContent?> GetDocumentAtCommitAsync(string tableName, string documentId, string commitHash);

        /// <summary>
        /// Get the merge base commit between two branches
        /// </summary>
        /// <param name="branch1">First branch name</param>
        /// <param name="branch2">Second branch name</param>
        /// <returns>Commit hash of the merge base</returns>
        Task<string?> GetMergeBaseAsync(string branch1, string branch2);

        /// <summary>
        /// Get document changes between two commits for a specific table
        /// </summary>
        /// <param name="fromCommit">Starting commit hash</param>
        /// <param name="toCommit">Ending commit hash</param>
        /// <param name="tableName">Table to analyze changes for</param>
        /// <returns>Dictionary of document IDs to their change type (added/modified/deleted)</returns>
        Task<Dictionary<string, string>> GetDocumentChangesBetweenCommitsAsync(string fromCommit, string toCommit, string tableName);
    }
}