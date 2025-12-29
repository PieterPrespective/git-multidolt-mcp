using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using DMMS.Models;

namespace DMMS.Services
{
    /// <summary>
    /// SQLite-based implementation of deletion tracking service
    /// </summary>
    public class SqliteDeletionTracker : IDeletionTracker, IDisposable
    {
        private readonly ILogger<SqliteDeletionTracker> _logger;
        private readonly string _dbPath;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private bool _disposed = false;

        /// <summary>
        /// Initializes a new instance of the SQLite deletion tracker
        /// </summary>
        public SqliteDeletionTracker(ILogger<SqliteDeletionTracker> logger, ServerConfiguration config)
        {
            _logger = logger;
            _dbPath = Path.Combine(config.DataPath, "dev", "deletion_tracking.db");
            var dbDirectory = Path.GetDirectoryName(_dbPath);
            if (!Directory.Exists(dbDirectory))
            {
                Directory.CreateDirectory(dbDirectory!);
            }
        }

        /// <summary>
        /// Initializes the deletion tracking database for a specific repository
        /// </summary>
        public async Task InitializeAsync(string repoPath)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                _logger.LogDebug("Creating deletion tracking database schema...");
                await CreateDatabaseSchemaAsync().WaitAsync(timeoutCts.Token);
                
                _logger.LogDebug($"Cleaning up stale deletion tracking for repository: {repoPath}");
                await CleanupStaleTrackingAsync(repoPath, requireSemaphore: false).WaitAsync(timeoutCts.Token);
                
                _logger.LogInformation($"Initialized deletion tracking for repository: {repoPath}");
            }
            catch (TimeoutException ex)
            {
                _logger.LogError(ex, "Timeout occurred during deletion tracker initialization");
                throw;
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Deletion tracker initialization was cancelled due to timeout");
                throw new TimeoutException("Deletion tracker initialization timed out after 10 seconds", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Tracks a document deletion in the external database
        /// </summary>
        public async Task TrackDeletionAsync(string repoPath, string docId, string collectionName, 
            string originalContentHash, Dictionary<string, object> originalMetadata, 
            string branchContext, string baseCommitHash)
        {
            await _semaphore.WaitAsync();
            try
            {
                var deletion = new DeletionRecord(docId, collectionName, repoPath, "mcp_tool", 
                    originalContentHash, JsonSerializer.Serialize(originalMetadata), 
                    branchContext, baseCommitHash);

                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = @"
                    INSERT INTO local_deletions 
                    (id, repo_path, doc_id, collection_name, deleted_at, deletion_source, 
                     original_content_hash, original_metadata, branch_context, base_commit_hash, 
                     sync_status, created_at)
                    VALUES 
                    (@id, @repoPath, @docId, @collectionName, @deletedAt, @deletionSource, 
                     @originalContentHash, @originalMetadata, @branchContext, @baseCommitHash, 
                     @syncStatus, @createdAt)";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@id", deletion.Id);
                command.Parameters.AddWithValue("@repoPath", deletion.RepoPath);
                command.Parameters.AddWithValue("@docId", deletion.DocId);
                command.Parameters.AddWithValue("@collectionName", deletion.CollectionName);
                command.Parameters.AddWithValue("@deletedAt", deletion.DeletedAt);
                command.Parameters.AddWithValue("@deletionSource", deletion.DeletionSource);
                command.Parameters.AddWithValue("@originalContentHash", deletion.OriginalContentHash ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@originalMetadata", deletion.OriginalMetadata ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@branchContext", deletion.BranchContext ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@baseCommitHash", deletion.BaseCommitHash ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@syncStatus", deletion.SyncStatus);
                command.Parameters.AddWithValue("@createdAt", deletion.CreatedAt);

                await command.ExecuteNonQueryAsync();
                _logger.LogInformation($"Tracked deletion: {docId} in {collectionName} for repo {repoPath}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Gets all pending deletions for a specific repository and collection
        /// </summary>
        public async Task<List<DeletionRecord>> GetPendingDeletionsAsync(string repoPath, string collectionName)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = @"
                    SELECT id, repo_path, doc_id, collection_name, deleted_at, deletion_source, 
                           original_content_hash, original_metadata, branch_context, base_commit_hash, 
                           sync_status, created_at
                    FROM local_deletions 
                    WHERE repo_path = @repoPath AND collection_name = @collectionName AND sync_status = 'pending'
                    ORDER BY deleted_at";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@repoPath", repoPath);
                command.Parameters.AddWithValue("@collectionName", collectionName);

                return await ReadDeletionRecordsAsync(command);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Gets all pending deletions for a specific repository (all collections)
        /// </summary>
        public async Task<List<DeletionRecord>> GetPendingDeletionsAsync(string repoPath)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = @"
                    SELECT id, repo_path, doc_id, collection_name, deleted_at, deletion_source, 
                           original_content_hash, original_metadata, branch_context, base_commit_hash, 
                           sync_status, created_at
                    FROM local_deletions 
                    WHERE repo_path = @repoPath AND sync_status = 'pending'
                    ORDER BY collection_name, deleted_at";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@repoPath", repoPath);

                return await ReadDeletionRecordsAsync(command);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Marks a deletion as staged (ready for commit)
        /// </summary>
        public async Task MarkDeletionStagedAsync(string repoPath, string docId, string collectionName)
        {
            await _semaphore.WaitAsync();
            try
            {
                await UpdateDeletionStatusInternalAsync(repoPath, docId, collectionName, "staged");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Marks a deletion as committed (synced successfully)
        /// </summary>
        public async Task MarkDeletionCommittedAsync(string repoPath, string docId, string collectionName)
        {
            await _semaphore.WaitAsync();
            try
            {
                await UpdateDeletionStatusInternalAsync(repoPath, docId, collectionName, "committed");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Cleans up committed deletion records for a repository
        /// </summary>
        public async Task CleanupCommittedDeletionsAsync(string repoPath)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = "DELETE FROM local_deletions WHERE repo_path = @repoPath AND sync_status = 'committed'";
                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@repoPath", repoPath);

                var deletedRows = await command.ExecuteNonQueryAsync();
                _logger.LogInformation($"Cleaned up {deletedRows} committed deletion records for repo: {repoPath}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Handles branch change operations with keep changes logic
        /// </summary>
        public async Task HandleBranchChangeAsync(string repoPath, string fromBranch, string toBranch, 
            string fromCommit, string toCommit, bool keepChanges)
        {
            if (!keepChanges)
            {
                await DiscardPendingDeletionsAsync(repoPath);
                return;
            }

            // Acquire semaphore once for the entire operation to prevent nested acquisitions
            await _semaphore.WaitAsync();
            try
            {
                var pendingDeletions = await GetPendingDeletionsInternalAsync(repoPath);
                
                foreach (var deletion in pendingDeletions)
                {
                    // TODO: Implement logic to check if document existed in target branch/commit
                    // For now, preserve all deletion tracking during branch changes with keep changes
                    await UpdateDeletionContextInternalAsync(repoPath, deletion.DocId, deletion.CollectionName, 
                        toBranch, toCommit);
                    _logger.LogInformation($"Preserved deletion tracking for {deletion.DocId} during branch change to {toBranch}");
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Cleans up stale tracking records (older than 30 days) - Interface implementation
        /// </summary>
        public async Task CleanupStaleTrackingAsync(string repoPath)
        {
            await CleanupStaleTrackingAsync(repoPath, requireSemaphore: true);
        }

        /// <summary>
        /// Cleans up stale tracking records (older than 30 days)
        /// </summary>
        /// <param name="repoPath">Repository path to clean up</param>
        /// <param name="requireSemaphore">Whether to acquire semaphore (default: true). Set to false when semaphore is already held.</param>
        public async Task CleanupStaleTrackingAsync(string repoPath, bool requireSemaphore = true)
        {
            if (requireSemaphore)
            {
                await _semaphore.WaitAsync();
            }
            
            try
            {
                await CleanupStaleTrackingInternalAsync(repoPath);
            }
            finally
            {
                if (requireSemaphore)
                {
                    _semaphore.Release();
                }
            }
        }

        /// <summary>
        /// Internal implementation of cleanup that assumes semaphore is already held
        /// </summary>
        private async Task CleanupStaleTrackingInternalAsync(string repoPath)
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-30);
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            const string sql = @"
                DELETE FROM local_deletions 
                WHERE repo_path = @repoPath AND created_at < @cutoffDate AND sync_status = 'pending'";
            
            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@repoPath", repoPath);
            command.Parameters.AddWithValue("@cutoffDate", cutoffDate);

            var deletedRows = await command.ExecuteNonQueryAsync();
            if (deletedRows > 0)
            {
                _logger.LogInformation($"Cleaned up {deletedRows} stale deletion tracking records for repo: {repoPath}");
            }
        }

        /// <summary>
        /// Removes a specific deletion tracking record
        /// </summary>
        public async Task RemoveDeletionTrackingAsync(string repoPath, string docId, string collectionName)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = "DELETE FROM local_deletions WHERE repo_path = @repoPath AND doc_id = @docId AND collection_name = @collectionName";
                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@repoPath", repoPath);
                command.Parameters.AddWithValue("@docId", docId);
                command.Parameters.AddWithValue("@collectionName", collectionName);

                var deletedRows = await command.ExecuteNonQueryAsync();
                _logger.LogInformation($"Removed {deletedRows} deletion tracking record(s) for {docId} in {collectionName}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Checks if a document has a pending deletion record
        /// </summary>
        public async Task<bool> HasPendingDeletionAsync(string repoPath, string docId, string collectionName)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = @"
                    SELECT COUNT(*) FROM local_deletions 
                    WHERE repo_path = @repoPath AND doc_id = @docId AND collection_name = @collectionName AND sync_status = 'pending'";
                
                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@repoPath", repoPath);
                command.Parameters.AddWithValue("@docId", docId);
                command.Parameters.AddWithValue("@collectionName", collectionName);

                var count = Convert.ToInt32(await command.ExecuteScalarAsync());
                return count > 0;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Updates the context information for a deletion record
        /// </summary>
        public async Task UpdateDeletionContextAsync(string repoPath, string docId, string collectionName, 
            string newBranchContext, string newBaseCommitHash)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = @"
                    UPDATE local_deletions 
                    SET branch_context = @branchContext, base_commit_hash = @baseCommitHash
                    WHERE repo_path = @repoPath AND doc_id = @docId AND collection_name = @collectionName";
                
                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@repoPath", repoPath);
                command.Parameters.AddWithValue("@docId", docId);
                command.Parameters.AddWithValue("@collectionName", collectionName);
                command.Parameters.AddWithValue("@branchContext", newBranchContext);
                command.Parameters.AddWithValue("@baseCommitHash", newBaseCommitHash);

                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Updates the context information for a deletion record (internal method - assumes caller holds semaphore)
        /// </summary>
        private async Task UpdateDeletionContextInternalAsync(string repoPath, string docId, string collectionName, 
            string newBranchContext, string newBaseCommitHash)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            const string sql = @"
                UPDATE local_deletions 
                SET branch_context = @branchContext, base_commit_hash = @baseCommitHash
                WHERE repo_path = @repoPath AND doc_id = @docId AND collection_name = @collectionName";
            
            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@repoPath", repoPath);
            command.Parameters.AddWithValue("@docId", docId);
            command.Parameters.AddWithValue("@collectionName", collectionName);
            command.Parameters.AddWithValue("@branchContext", newBranchContext);
            command.Parameters.AddWithValue("@baseCommitHash", newBaseCommitHash);

            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Gets all pending deletions for a specific repository (internal method - assumes caller holds semaphore)
        /// </summary>
        private async Task<List<DeletionRecord>> GetPendingDeletionsInternalAsync(string repoPath)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            const string sql = @"
                SELECT id, repo_path, doc_id, collection_name, deleted_at, deletion_source, 
                       original_content_hash, original_metadata, branch_context, base_commit_hash, 
                       sync_status, created_at
                FROM local_deletions 
                WHERE repo_path = @repoPath AND sync_status = 'pending'
                ORDER BY collection_name, deleted_at";

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@repoPath", repoPath);

            return await ReadDeletionRecordsAsync(command);
        }

        /// <summary>
        /// Tracks a collection deletion in the external database
        /// </summary>
        public async Task TrackCollectionDeletionAsync(string repoPath, string collectionName, 
            Dictionary<string, object> originalMetadata, string branchContext, string baseCommitHash)
        {
            await _semaphore.WaitAsync();
            try
            {
                var deletion = new CollectionDeletionRecord(collectionName, repoPath, "deletion",
                    "mcp_tool", JsonSerializer.Serialize(originalMetadata), null, null, 
                    branchContext, baseCommitHash);

                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = @"
                    INSERT INTO local_collection_deletions 
                    (id, repo_path, collection_name, operation_type, deleted_at, deletion_source, 
                     original_metadata, original_name, new_name, branch_context, base_commit_hash, 
                     sync_status, created_at)
                    VALUES 
                    (@id, @repoPath, @collectionName, @operationType, @deletedAt, @deletionSource, 
                     @originalMetadata, @originalName, @newName, @branchContext, @baseCommitHash, 
                     @syncStatus, @createdAt)";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@id", deletion.Id);
                command.Parameters.AddWithValue("@repoPath", deletion.RepoPath);
                command.Parameters.AddWithValue("@collectionName", deletion.CollectionName);
                command.Parameters.AddWithValue("@operationType", deletion.OperationType);
                command.Parameters.AddWithValue("@deletedAt", deletion.DeletedAt);
                command.Parameters.AddWithValue("@deletionSource", deletion.DeletionSource);
                command.Parameters.AddWithValue("@originalMetadata", (object?)deletion.OriginalMetadata ?? DBNull.Value);
                command.Parameters.AddWithValue("@originalName", (object?)deletion.OriginalName ?? DBNull.Value);
                command.Parameters.AddWithValue("@newName", (object?)deletion.NewName ?? DBNull.Value);
                command.Parameters.AddWithValue("@branchContext", (object?)deletion.BranchContext ?? DBNull.Value);
                command.Parameters.AddWithValue("@baseCommitHash", (object?)deletion.BaseCommitHash ?? DBNull.Value);
                command.Parameters.AddWithValue("@syncStatus", deletion.SyncStatus);
                command.Parameters.AddWithValue("@createdAt", deletion.CreatedAt);

                await command.ExecuteNonQueryAsync();

                _logger.LogInformation($"Tracked collection deletion: {collectionName} in repository {repoPath}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Tracks a collection rename/update operation in the external database
        /// </summary>
        public async Task TrackCollectionUpdateAsync(string repoPath, string oldCollectionName, string newCollectionName,
            Dictionary<string, object> originalMetadata, Dictionary<string, object> newMetadata,
            string branchContext, string baseCommitHash)
        {
            await _semaphore.WaitAsync();
            try
            {
                var operationType = oldCollectionName != newCollectionName ? "rename" : "metadata_update";
                var deletion = new CollectionDeletionRecord(oldCollectionName, repoPath, operationType,
                    "mcp_tool", JsonSerializer.Serialize(originalMetadata), oldCollectionName, newCollectionName,
                    branchContext, baseCommitHash);

                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = @"
                    INSERT INTO local_collection_deletions 
                    (id, repo_path, collection_name, operation_type, deleted_at, deletion_source, 
                     original_metadata, original_name, new_name, branch_context, base_commit_hash, 
                     sync_status, created_at)
                    VALUES 
                    (@id, @repoPath, @collectionName, @operationType, @deletedAt, @deletionSource, 
                     @originalMetadata, @originalName, @newName, @branchContext, @baseCommitHash, 
                     @syncStatus, @createdAt)";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@id", deletion.Id);
                command.Parameters.AddWithValue("@repoPath", deletion.RepoPath);
                command.Parameters.AddWithValue("@collectionName", deletion.CollectionName);
                command.Parameters.AddWithValue("@operationType", deletion.OperationType);
                command.Parameters.AddWithValue("@deletedAt", deletion.DeletedAt);
                command.Parameters.AddWithValue("@deletionSource", deletion.DeletionSource);
                command.Parameters.AddWithValue("@originalMetadata", (object?)deletion.OriginalMetadata ?? DBNull.Value);
                command.Parameters.AddWithValue("@originalName", (object?)deletion.OriginalName ?? DBNull.Value);
                command.Parameters.AddWithValue("@newName", (object?)deletion.NewName ?? DBNull.Value);
                command.Parameters.AddWithValue("@branchContext", (object?)deletion.BranchContext ?? DBNull.Value);
                command.Parameters.AddWithValue("@baseCommitHash", (object?)deletion.BaseCommitHash ?? DBNull.Value);
                command.Parameters.AddWithValue("@syncStatus", deletion.SyncStatus);
                command.Parameters.AddWithValue("@createdAt", deletion.CreatedAt);

                await command.ExecuteNonQueryAsync();

                _logger.LogInformation($"Tracked collection {operationType}: {oldCollectionName} -> {newCollectionName} in repository {repoPath}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Gets all pending collection deletions for a specific repository
        /// </summary>
        public async Task<List<CollectionDeletionRecord>> GetPendingCollectionDeletionsAsync(string repoPath)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = @"
                    SELECT id, repo_path, collection_name, operation_type, deleted_at, deletion_source,
                           original_metadata, original_name, new_name, branch_context, base_commit_hash, 
                           sync_status, created_at
                    FROM local_collection_deletions 
                    WHERE repo_path = @repoPath AND sync_status = 'pending'
                    ORDER BY collection_name, 
                             CASE operation_type 
                                 WHEN 'deletion' THEN 1 
                                 WHEN 'rename' THEN 2 
                                 WHEN 'metadata_update' THEN 3 
                                 ELSE 4 
                             END, 
                             deleted_at";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@repoPath", repoPath);

                return await ReadCollectionDeletionRecordsAsync(command);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Marks a collection deletion as committed (synced successfully)
        /// </summary>
        public async Task MarkCollectionDeletionCommittedAsync(string repoPath, string collectionName, string operationType)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = @"
                    UPDATE local_collection_deletions 
                    SET sync_status = 'committed'
                    WHERE repo_path = @repoPath AND collection_name = @collectionName AND operation_type = @operationType";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@repoPath", repoPath);
                command.Parameters.AddWithValue("@collectionName", collectionName);
                command.Parameters.AddWithValue("@operationType", operationType);

                var rowsAffected = await command.ExecuteNonQueryAsync();
                
                _logger.LogDebug($"Marked collection {operationType} as committed: {collectionName} in repository {repoPath} ({rowsAffected} rows affected)");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Cleans up committed collection deletion records for a repository
        /// </summary>
        public async Task CleanupCommittedCollectionDeletionsAsync(string repoPath)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = @"DELETE FROM local_collection_deletions WHERE repo_path = @repoPath AND sync_status = 'committed'";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@repoPath", repoPath);

                var rowsAffected = await command.ExecuteNonQueryAsync();
                
                _logger.LogDebug($"Cleaned up {rowsAffected} committed collection deletion records for repository {repoPath}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        #region Private Methods

        /// <summary>
        /// Creates the database schema if it doesn't exist
        /// </summary>
        private async Task CreateDatabaseSchemaAsync()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            const string createTableSql = @"
                CREATE TABLE IF NOT EXISTS local_deletions (
                    id TEXT PRIMARY KEY,
                    repo_path TEXT NOT NULL,
                    doc_id TEXT NOT NULL,
                    collection_name TEXT NOT NULL,
                    deleted_at DATETIME NOT NULL,
                    deletion_source TEXT NOT NULL,
                    original_content_hash TEXT,
                    original_metadata TEXT,
                    branch_context TEXT,
                    base_commit_hash TEXT,
                    sync_status TEXT DEFAULT 'pending',
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS local_collection_deletions (
                    id TEXT PRIMARY KEY,
                    repo_path TEXT NOT NULL,
                    collection_name TEXT NOT NULL,
                    operation_type TEXT NOT NULL,
                    deleted_at DATETIME NOT NULL,
                    deletion_source TEXT NOT NULL,
                    original_metadata TEXT,
                    original_name TEXT,
                    new_name TEXT,
                    branch_context TEXT,
                    base_commit_hash TEXT,
                    sync_status TEXT DEFAULT 'pending',
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS idx_repo_doc_collection ON local_deletions(repo_path, doc_id, collection_name);
                CREATE INDEX IF NOT EXISTS idx_repo_sync_status ON local_deletions(repo_path, sync_status);
                CREATE INDEX IF NOT EXISTS idx_repo_collection ON local_deletions(repo_path, collection_name);
                CREATE INDEX IF NOT EXISTS idx_repo_collection_operation ON local_collection_deletions(repo_path, collection_name, operation_type);
                CREATE INDEX IF NOT EXISTS idx_repo_collection_sync_status ON local_collection_deletions(repo_path, sync_status);";

            using var command = new SqliteCommand(createTableSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Updates the sync status of a deletion record (internal method - assumes caller holds semaphore)
        /// </summary>
        private async Task UpdateDeletionStatusInternalAsync(string repoPath, string docId, string collectionName, string status)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            const string sql = @"
                UPDATE local_deletions 
                SET sync_status = @status 
                WHERE repo_path = @repoPath AND doc_id = @docId AND collection_name = @collectionName";
            
            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@repoPath", repoPath);
            command.Parameters.AddWithValue("@docId", docId);
            command.Parameters.AddWithValue("@collectionName", collectionName);
            command.Parameters.AddWithValue("@status", status);

            await command.ExecuteNonQueryAsync();
            _logger.LogDebug($"Updated deletion status to {status} for {docId} in {collectionName}");
        }

        /// <summary>
        /// Discards all pending deletions for a repository (used when not keeping changes)
        /// </summary>
        private async Task DiscardPendingDeletionsAsync(string repoPath)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();

                const string sql = "DELETE FROM local_deletions WHERE repo_path = @repoPath AND sync_status = 'pending'";
                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@repoPath", repoPath);

                var deletedRows = await command.ExecuteNonQueryAsync();
                _logger.LogInformation($"Discarded {deletedRows} pending deletion records for repo: {repoPath}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Reads deletion records from a command result
        /// </summary>
        private async Task<List<DeletionRecord>> ReadDeletionRecordsAsync(SqliteCommand command)
        {
            var records = new List<DeletionRecord>();
            using var reader = await command.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                var record = new DeletionRecord
                {
                    Id = reader.GetString("id"),
                    RepoPath = reader.GetString("repo_path"),
                    DocId = reader.GetString("doc_id"),
                    CollectionName = reader.GetString("collection_name"),
                    DeletedAt = reader.GetDateTime("deleted_at"),
                    DeletionSource = reader.GetString("deletion_source"),
                    OriginalContentHash = reader.IsDBNull("original_content_hash") ? null : reader.GetString("original_content_hash"),
                    OriginalMetadata = reader.IsDBNull("original_metadata") ? null : reader.GetString("original_metadata"),
                    BranchContext = reader.IsDBNull("branch_context") ? null : reader.GetString("branch_context"),
                    BaseCommitHash = reader.IsDBNull("base_commit_hash") ? null : reader.GetString("base_commit_hash"),
                    SyncStatus = reader.GetString("sync_status"),
                    CreatedAt = reader.GetDateTime("created_at")
                };
                records.Add(record);
            }
            
            return records;
        }

        /// <summary>
        /// Reads collection deletion records from a command result
        /// </summary>
        private async Task<List<CollectionDeletionRecord>> ReadCollectionDeletionRecordsAsync(SqliteCommand command)
        {
            var records = new List<CollectionDeletionRecord>();
            using var reader = await command.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                var record = new CollectionDeletionRecord
                {
                    Id = reader.GetString("id"),
                    RepoPath = reader.GetString("repo_path"),
                    CollectionName = reader.GetString("collection_name"),
                    OperationType = reader.GetString("operation_type"),
                    DeletedAt = reader.GetDateTime("deleted_at"),
                    DeletionSource = reader.GetString("deletion_source"),
                    OriginalMetadata = reader.IsDBNull("original_metadata") ? null : reader.GetString("original_metadata"),
                    OriginalName = reader.IsDBNull("original_name") ? null : reader.GetString("original_name"),
                    NewName = reader.IsDBNull("new_name") ? null : reader.GetString("new_name"),
                    BranchContext = reader.IsDBNull("branch_context") ? null : reader.GetString("branch_context"),
                    BaseCommitHash = reader.IsDBNull("base_commit_hash") ? null : reader.GetString("base_commit_hash"),
                    SyncStatus = reader.GetString("sync_status"),
                    CreatedAt = reader.GetDateTime("created_at")
                };
                records.Add(record);
            }
            
            return records;
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Releases resources used by the deletion tracker
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected disposal method
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _semaphore?.Dispose();
                _disposed = true;
            }
        }

        #endregion
    }
}