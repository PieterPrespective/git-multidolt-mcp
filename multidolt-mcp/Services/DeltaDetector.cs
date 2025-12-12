using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DMMS.Models;
using Microsoft.Extensions.Logging;

namespace DMMS.Services
{
    /// <summary>
    /// Service for detecting changes between Dolt database state and ChromaDB sync state.
    /// Implements content-hash based change detection for efficient incremental synchronization.
    /// </summary>
    public class DeltaDetector
    {
        private readonly IDoltCli _dolt;
        private readonly ILogger<DeltaDetector>? _logger;

        /// <summary>
        /// Initialize the DeltaDetector with a Dolt CLI interface
        /// </summary>
        /// <param name="dolt">Dolt CLI interface for executing database queries</param>
        /// <param name="logger">Optional logger for debugging delta detection operations</param>
        public DeltaDetector(IDoltCli dolt, ILogger<DeltaDetector>? logger = null)
        {
            _dolt = dolt;
            _logger = logger;
        }

        /// <summary>
        /// Find documents that need syncing to ChromaDB (new or modified).
        /// Uses content hash comparison between source tables and sync log to detect changes.
        /// </summary>
        /// <param name="collectionName">The ChromaDB collection name to check against</param>
        /// <returns>Collection of documents that need to be synced (added or updated)</returns>
        public async Task<IEnumerable<DocumentDelta>> GetPendingSyncDocumentsAsync(string collectionName)
        {
            _logger?.LogDebug("Finding pending sync documents for collection {Collection}", collectionName);

            // Query combines issue_logs and knowledge_docs tables, comparing content hashes
            // with the sync log to identify new or modified documents
            // Note: We use CAST to ensure metadata comes as string for proper deserialization
            var sql = $@"
                SELECT 
                    'issue_logs' as source_table,
                    il.log_id as source_id,
                    il.content,
                    il.content_hash,
                    il.project_id as identifier,
                    CAST(JSON_OBJECT(
                        'issue_number', il.issue_number, 
                        'log_type', COALESCE(il.log_type, 'implementation'),
                        'title', COALESCE(il.title, ''),
                        'project_id', COALESCE(il.project_id, ''),
                        'created_at', COALESCE(il.created_at, ''),
                        'updated_at', COALESCE(il.updated_at, '')
                    ) AS CHAR) as metadata,
                    CASE 
                        WHEN dsl.content_hash IS NULL THEN 'new'
                        WHEN dsl.content_hash != il.content_hash THEN 'modified'
                    END as change_type
                FROM issue_logs il
                LEFT JOIN document_sync_log dsl 
                    ON dsl.source_table = 'issue_logs' 
                    AND dsl.source_id = il.log_id
                    AND dsl.chroma_collection = '{collectionName}'
                WHERE dsl.content_hash IS NULL 
                   OR dsl.content_hash != il.content_hash

                UNION ALL

                SELECT 
                    'knowledge_docs' as source_table,
                    kd.doc_id as source_id,
                    kd.content,
                    kd.content_hash,
                    kd.tool_name as identifier,
                    CAST(JSON_OBJECT(
                        'category', COALESCE(kd.category, ''),
                        'tool_version', COALESCE(kd.tool_version, ''),
                        'title', COALESCE(kd.title, ''),
                        'tool_name', COALESCE(kd.tool_name, ''),
                        'created_at', COALESCE(kd.created_at, ''),
                        'updated_at', COALESCE(kd.updated_at, '')
                    ) AS CHAR) as metadata,
                    CASE 
                        WHEN dsl.content_hash IS NULL THEN 'new'
                        WHEN dsl.content_hash != kd.content_hash THEN 'modified'
                    END as change_type
                FROM knowledge_docs kd
                LEFT JOIN document_sync_log dsl 
                    ON dsl.source_table = 'knowledge_docs' 
                    AND dsl.source_id = kd.doc_id
                    AND dsl.chroma_collection = '{collectionName}'
                WHERE dsl.content_hash IS NULL 
                   OR dsl.content_hash != kd.content_hash";

            try
            {
                var deltas = await _dolt.QueryAsync<DocumentDelta>(sql);
                var deltaList = deltas.ToList();
                
                _logger?.LogInformation("Found {Count} documents pending sync: {New} new, {Modified} modified",
                    deltaList.Count,
                    deltaList.Count(d => d.ChangeType == "new"),
                    deltaList.Count(d => d.ChangeType == "modified"));
                
                return deltaList;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get pending sync documents for collection {Collection}", collectionName);
                throw new DoltException($"Failed to get pending sync documents: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Find documents that have been deleted from Dolt but are still tracked in the sync log.
        /// These need to be removed from ChromaDB to maintain consistency.
        /// </summary>
        /// <param name="collectionName">The ChromaDB collection name to check</param>
        /// <returns>Collection of deleted documents that need to be removed from ChromaDB</returns>
        public async Task<IEnumerable<DeletedDocument>> GetDeletedDocumentsAsync(string collectionName)
        {
            _logger?.LogDebug("Finding deleted documents for collection {Collection}", collectionName);

            // Find documents in sync log that no longer exist in source tables
            var sql = $@"
                SELECT 
                    dsl.source_table,
                    dsl.source_id,
                    dsl.chroma_collection,
                    CAST(dsl.chunk_ids AS CHAR) as chunk_ids
                FROM document_sync_log dsl
                WHERE dsl.chroma_collection = '{collectionName}'
                  AND (
                      (dsl.source_table = 'issue_logs' 
                       AND dsl.source_id NOT IN (SELECT log_id FROM issue_logs))
                      OR
                      (dsl.source_table = 'knowledge_docs' 
                       AND dsl.source_id NOT IN (SELECT doc_id FROM knowledge_docs))
                  )";

            try
            {
                var deleted = await _dolt.QueryAsync<DeletedDocument>(sql);
                var deletedList = deleted.ToList();
                
                _logger?.LogInformation("Found {Count} deleted documents to remove from ChromaDB", 
                    deletedList.Count);
                
                return deletedList;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get deleted documents for collection {Collection}", collectionName);
                throw new DoltException($"Failed to get deleted documents: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Use Dolt's native DOLT_DIFF function for efficient commit-to-commit comparison.
        /// This leverages Dolt's built-in versioning capabilities for change detection.
        /// </summary>
        /// <param name="fromCommit">Starting commit hash or reference (e.g., "HEAD~1")</param>
        /// <param name="toCommit">Ending commit hash or reference (e.g., "HEAD")</param>
        /// <param name="tableName">Name of the table to analyze for differences</param>
        /// <returns>Collection of diff rows showing what changed between commits</returns>
        public async Task<IEnumerable<DiffRow>> GetCommitDiffAsync(
            string fromCommit, 
            string toCommit, 
            string tableName)
        {
            _logger?.LogDebug("Getting diff for table {Table} between {From} and {To}", 
                tableName, fromCommit, toCommit);

            // Validate table name to prevent SQL injection
            if (!IsValidTableName(tableName))
            {
                throw new ArgumentException($"Invalid table name: {tableName}");
            }

            // Get the appropriate ID column name based on the table
            var idColumn = GetIdColumn(tableName);

            // Use DOLT_DIFF table function for structured diff data
            var sql = $@"
                SELECT 
                    diff_type,
                    to_{idColumn} as source_id,
                    from_content_hash,
                    to_content_hash,
                    to_content,
                    to_metadata
                FROM DOLT_DIFF('{fromCommit}', '{toCommit}', '{tableName}')";

            try
            {
                var diffs = await _dolt.QueryAsync<DiffRow>(sql);
                var diffList = diffs.ToList();
                
                _logger?.LogInformation("Found {Count} differences in {Table}: {Added} added, {Modified} modified, {Removed} removed",
                    diffList.Count,
                    tableName,
                    diffList.Count(d => d.DiffType == "added"),
                    diffList.Count(d => d.DiffType == "modified"),
                    diffList.Count(d => d.DiffType == "removed"));
                
                return diffList;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to get commit diff for table {Table}", tableName);
                throw new DoltException($"Failed to get commit diff: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Check if the sync state exists for a collection and get the last sync commit
        /// </summary>
        /// <param name="collectionName">The ChromaDB collection name</param>
        /// <returns>The last sync commit hash, or null if no sync state exists</returns>
        public async Task<string?> GetLastSyncCommitAsync(string collectionName)
        {
            _logger?.LogDebug("Getting last sync commit for collection {Collection}", collectionName);

            var sql = $@"
                SELECT last_sync_commit 
                FROM chroma_sync_state 
                WHERE collection_name = '{collectionName}'";

            try
            {
                var result = await _dolt.ExecuteScalarAsync<string?>(sql);
                _logger?.LogDebug("Last sync commit for {Collection}: {Commit}", 
                    collectionName, result ?? "none");
                return result;
            }
            catch
            {
                _logger?.LogDebug("No sync state found for collection {Collection}", collectionName);
                return null;
            }
        }

        /// <summary>
        /// Get all documents that have changed since a specific commit
        /// </summary>
        /// <param name="sinceCommit">The commit to compare against (null for all documents)</param>
        /// <param name="collectionName">The ChromaDB collection name</param>
        /// <returns>All documents that have changed since the given commit</returns>
        public async Task<ChangeSummary> GetChangesSinceCommitAsync(
            string? sinceCommit, 
            string collectionName)
        {
            _logger?.LogDebug("Getting changes since commit {Commit} for collection {Collection}", 
                sinceCommit ?? "beginning", collectionName);

            var summary = new ChangeSummary();

            if (string.IsNullOrEmpty(sinceCommit))
            {
                // No previous sync - all documents are new
                summary.PendingDocuments = await GetPendingSyncDocumentsAsync(collectionName);
                summary.DeletedDocuments = Enumerable.Empty<DeletedDocument>();
            }
            else
            {
                // Get current HEAD commit
                var currentCommit = await _dolt.GetHeadCommitHashAsync();
                
                if (currentCommit == sinceCommit)
                {
                    _logger?.LogDebug("No changes detected - already at commit {Commit}", currentCommit);
                    return summary;
                }

                // Get diffs for both tables
                var issueLogDiffs = await GetCommitDiffAsync(sinceCommit, currentCommit, "issue_logs");
                var knowledgeDocDiffs = await GetCommitDiffAsync(sinceCommit, currentCommit, "knowledge_docs");

                // Combine and process diffs
                var allDiffs = issueLogDiffs.Concat(knowledgeDocDiffs).ToList();
                
                // Get full pending sync documents (includes content)
                summary.PendingDocuments = await GetPendingSyncDocumentsAsync(collectionName);
                
                // Get deleted documents
                summary.DeletedDocuments = await GetDeletedDocumentsAsync(collectionName);
            }

            _logger?.LogInformation("Change summary for {Collection}: {New} new, {Modified} modified, {Deleted} deleted",
                collectionName,
                summary.NewDocumentCount,
                summary.ModifiedDocumentCount,
                summary.DeletedDocumentCount);

            return summary;
        }

        // ==================== Helper Methods ====================

        /// <summary>
        /// Get the primary key column name for a specific table
        /// </summary>
        private string GetIdColumn(string tableName) => tableName switch
        {
            "issue_logs" => "log_id",
            "knowledge_docs" => "doc_id",
            "projects" => "project_id",
            "chroma_sync_state" => "collection_name",
            "document_sync_log" => "id",
            _ => "id"
        };

        /// <summary>
        /// Validate table name to prevent SQL injection
        /// </summary>
        private bool IsValidTableName(string tableName)
        {
            var validTables = new[] { 
                "issue_logs", 
                "knowledge_docs", 
                "projects",
                "chroma_sync_state",
                "document_sync_log",
                "sync_operations"
            };
            return validTables.Contains(tableName);
        }
    }

    /// <summary>
    /// Summary of all changes detected between Dolt and ChromaDB
    /// </summary>
    public class ChangeSummary
    {
        public IEnumerable<DocumentDelta> PendingDocuments { get; set; } = Enumerable.Empty<DocumentDelta>();
        public IEnumerable<DeletedDocument> DeletedDocuments { get; set; } = Enumerable.Empty<DeletedDocument>();
        
        public int NewDocumentCount => PendingDocuments.Count(d => d.ChangeType == "new");
        public int ModifiedDocumentCount => PendingDocuments.Count(d => d.ChangeType == "modified");
        public int DeletedDocumentCount => DeletedDocuments.Count();
        public int TotalChangeCount => NewDocumentCount + ModifiedDocumentCount + DeletedDocumentCount;
        
        public bool HasChanges => TotalChangeCount > 0;
    }
}