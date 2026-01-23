using System.Text.Json;
using Embranch.Models;
using Microsoft.Extensions.Logging;

namespace Embranch.Services
{
    /// <summary>
    /// Service implementation for resolving merge conflicts using various resolution strategies
    /// Supports field-level merging, custom values, and automatic resolution
    /// </summary>
    public class MergeConflictResolver : IMergeConflictResolver
    {
        private readonly IDoltCli _doltCli;
        private readonly ILogger<MergeConflictResolver> _logger;

        /// <summary>
        /// Initializes a new instance of the MergeConflictResolver class
        /// </summary>
        /// <param name="doltCli">Dolt CLI service for executing Dolt operations</param>
        /// <param name="logger">Logger for diagnostic information</param>
        public MergeConflictResolver(IDoltCli doltCli, ILogger<MergeConflictResolver> logger)
        {
            _doltCli = doltCli;
            _logger = logger;
        }

        /// <summary>
        /// Resolve a specific conflict using the provided resolution strategy
        /// </summary>
        public async Task<bool> ResolveConflictAsync(
            DetailedConflictInfo conflict,
            ConflictResolutionRequest resolution)
        {
            _logger.LogInformation("Resolving conflict {ConflictId} using strategy {Strategy}", 
                conflict.ConflictId, resolution.ResolutionType);

            try
            {
                switch (resolution.ResolutionType)
                {
                    case ResolutionType.KeepOurs:
                        return await ResolveKeepOurs(conflict);
                    
                    case ResolutionType.KeepTheirs:
                        return await ResolveKeepTheirs(conflict);
                    
                    case ResolutionType.FieldMerge:
                        return await ApplyFieldMergeAsync(
                            conflict.Collection,
                            conflict.DocumentId,
                            resolution.FieldResolutions);
                    
                    case ResolutionType.Custom:
                        return await ApplyCustomResolutionAsync(
                            conflict.Collection,
                            conflict.DocumentId,
                            resolution.CustomValues);
                    
                    case ResolutionType.AutoResolve:
                        return await AutoResolveConflictAsync(conflict);
                    
                    default:
                        _logger.LogWarning("Unknown resolution type: {ResolutionType}", resolution.ResolutionType);
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve conflict {ConflictId}", conflict.ConflictId);
                return false;
            }
        }

        /// <summary>
        /// Automatically resolve all conflicts that can be safely auto-resolved
        /// </summary>
        public async Task<int> AutoResolveConflictsAsync(List<DetailedConflictInfo> conflicts)
        {
            _logger.LogInformation("Attempting auto-resolution for {ConflictCount} conflicts", conflicts.Count);
            
            int resolved = 0;
            
            foreach (var conflict in conflicts.Where(c => c.AutoResolvable))
            {
                try
                {
                    if (await AutoResolveConflictAsync(conflict))
                    {
                        resolved++;
                        _logger.LogDebug("Auto-resolved conflict {ConflictId}", conflict.ConflictId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to auto-resolve conflict {ConflictId}", conflict.ConflictId);
                }
            }

            _logger.LogInformation("Auto-resolved {Resolved} of {Total} auto-resolvable conflicts", 
                resolved, conflicts.Count(c => c.AutoResolvable));
            
            return resolved;
        }

        /// <summary>
        /// Apply field-level merge resolution where different fields are kept from different branches
        /// </summary>
        public async Task<bool> ApplyFieldMergeAsync(
            string tableName,
            string documentId,
            Dictionary<string, string> fieldResolutions)
        {
            _logger.LogDebug("Applying field merge for document {DocumentId} in table {Table}", 
                documentId, tableName);

            try
            {
                // Build UPDATE statement for conflict table
                var updates = new List<string>();
                foreach (var field in fieldResolutions)
                {
                    var sourceColumn = field.Value.ToLower() == "ours" ? $"our_{field.Key}" : $"their_{field.Key}";
                    var targetColumn = $"our_{field.Key}"; // We update the "our" columns to reflect resolution
                    updates.Add($"{targetColumn} = {sourceColumn}");
                }

                if (!updates.Any())
                {
                    _logger.LogWarning("No field resolutions provided for document {DocumentId}", documentId);
                    return false;
                }

                var updateSql = $@"
                    UPDATE dolt_conflicts_{tableName}
                    SET {string.Join(", ", updates)}
                    WHERE our_doc_id = '{documentId}'";

                _logger.LogDebug("Executing field merge SQL: {Sql}", updateSql);
                var updateResult = await _doltCli.ExecuteConflictResolutionAsync(updateSql);
                
                if (updateResult > 0)
                {
                    // Delete the conflict marker after successful resolution
                    var deleteSql = $@"
                        DELETE FROM dolt_conflicts_{tableName}
                        WHERE our_doc_id = '{documentId}'";
                    
                    await _doltCli.ExecuteConflictResolutionAsync(deleteSql);
                    _logger.LogDebug("Field merge applied successfully for document {DocumentId}", documentId);
                    return true;
                }

                _logger.LogWarning("Field merge update affected 0 rows for document {DocumentId}", documentId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply field merge for document {DocumentId}", documentId);
                return false;
            }
        }

        /// <summary>
        /// Apply custom user-provided values to resolve a conflict
        /// </summary>
        public async Task<bool> ApplyCustomResolutionAsync(
            string tableName,
            string documentId,
            Dictionary<string, object> customValues)
        {
            _logger.LogDebug("Applying custom resolution for document {DocumentId} in table {Table}", 
                documentId, tableName);

            try
            {
                if (!customValues.Any())
                {
                    _logger.LogWarning("No custom values provided for document {DocumentId}", documentId);
                    return false;
                }

                // Update the conflict table with custom values
                var sets = customValues.Select(kvp => 
                {
                    var jsonValue = JsonSerializer.Serialize(kvp.Value);
                    // Escape single quotes in JSON for SQL safety
                    var escapedValue = jsonValue.Replace("'", "''");
                    return $"our_{kvp.Key} = '{escapedValue}'";
                });
                
                var updateSql = $@"
                    UPDATE dolt_conflicts_{tableName}
                    SET {string.Join(", ", sets)}
                    WHERE our_doc_id = '{documentId}'";

                _logger.LogDebug("Executing custom resolution SQL: {Sql}", updateSql);
                var result = await _doltCli.ExecuteConflictResolutionAsync(updateSql);
                
                if (result > 0)
                {
                    // Remove conflict marker after successful resolution
                    var deleteSql = $@"
                        DELETE FROM dolt_conflicts_{tableName}
                        WHERE our_doc_id = '{documentId}'";
                    
                    await _doltCli.ExecuteConflictResolutionAsync(deleteSql);
                    _logger.LogDebug("Custom resolution applied successfully for document {DocumentId}", documentId);
                    return true;
                }

                _logger.LogWarning("Custom resolution update affected 0 rows for document {DocumentId}", documentId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply custom resolution for document {DocumentId}", documentId);
                return false;
            }
        }

        /// <summary>
        /// PP13-73-C3: Resolve multiple conflicts in a single transaction.
        /// Dolt requires ALL conflicts to be resolved before COMMIT is allowed.
        /// This method collects all resolution SQL statements and executes them atomically.
        /// </summary>
        /// <param name="conflictResolutions">List of (conflict, resolution) tuples to resolve</param>
        /// <returns>BatchResolutionResult with per-conflict outcomes and overall success</returns>
        public async Task<BatchResolutionResult> ResolveBatchAsync(
            List<(DetailedConflictInfo Conflict, ConflictResolutionRequest Resolution)> conflictResolutions)
        {
            var result = new BatchResolutionResult
            {
                TotalAttempted = conflictResolutions.Count,
                ResolutionOutcomes = new List<ConflictResolutionOutcome>()
            };

            if (conflictResolutions.Count == 0)
            {
                _logger.LogInformation("PP13-73-C3: ResolveBatchAsync called with empty list - nothing to resolve");
                result.Success = true;
                return result;
            }

            _logger.LogInformation("PP13-73-C3: Starting batch resolution for {Count} conflicts", conflictResolutions.Count);

            // Collect all SQL statements for all resolutions
            var allSqlStatements = new List<string>();
            var resolutionMap = new Dictionary<int, (DetailedConflictInfo Conflict, ConflictResolutionRequest Resolution)>();

            for (int i = 0; i < conflictResolutions.Count; i++)
            {
                var (conflict, resolution) = conflictResolutions[i];
                resolutionMap[i] = (conflict, resolution);

                // Track this resolution attempt
                var outcome = new ConflictResolutionOutcome
                {
                    ConflictId = conflict.ConflictId,
                    DocumentId = conflict.DocumentId,
                    CollectionName = conflict.Collection,
                    ResolutionType = resolution.ResolutionType
                };
                result.ResolutionOutcomes.Add(outcome);

                // Determine the ConflictResolution enum value based on ResolutionType
                ConflictResolution conflictResolutionStrategy;
                switch (resolution.ResolutionType)
                {
                    case ResolutionType.KeepOurs:
                        conflictResolutionStrategy = ConflictResolution.Ours;
                        break;
                    case ResolutionType.KeepTheirs:
                        conflictResolutionStrategy = ConflictResolution.Theirs;
                        break;
                    default:
                        // For non-standard resolution types (FieldMerge, Custom, AutoResolve),
                        // we need to handle them differently or fall back to individual resolution
                        _logger.LogWarning("PP13-73-C3: Resolution type {Type} for conflict {ConflictId} not supported in batch mode, skipping",
                            resolution.ResolutionType, conflict.ConflictId);
                        outcome.Success = false;
                        outcome.ErrorMessage = $"Resolution type {resolution.ResolutionType} not supported in batch mode";
                        result.FailedCount++;
                        continue;
                }

                // Generate SQL for this resolution
                var sqlStatements = _doltCli.GenerateConflictResolutionSql(
                    "documents",
                    conflict.DocumentId,
                    conflict.Collection,
                    conflictResolutionStrategy);

                if (sqlStatements == null || sqlStatements.Length == 0)
                {
                    _logger.LogWarning("PP13-73-C3: Failed to generate SQL for conflict {ConflictId}", conflict.ConflictId);
                    outcome.Success = false;
                    outcome.ErrorMessage = "Failed to generate resolution SQL";
                    result.FailedCount++;
                    continue;
                }

                _logger.LogDebug("PP13-73-C3: Generated {Count} SQL statement(s) for conflict {ConflictId} ({Strategy})",
                    sqlStatements.Length, conflict.ConflictId, resolution.ResolutionType);

                allSqlStatements.AddRange(sqlStatements);
            }

            // If we have no SQL statements to execute, either all resolutions failed to generate
            // or the list was empty
            if (allSqlStatements.Count == 0)
            {
                _logger.LogWarning("PP13-73-C3: No SQL statements generated for batch resolution");
                result.Success = result.FailedCount == 0;
                result.ErrorMessage = result.FailedCount > 0 ? "All resolutions failed to generate SQL" : null;
                return result;
            }

            // Execute all SQL statements in a single transaction
            // PP13-73-C3: Use skipCommit=true because Dolt blocks COMMIT until ALL conflicts are resolved
            // The actual commit happens via 'dolt commit' after all conflicts are resolved
            _logger.LogInformation("PP13-73-C3: Executing {Count} SQL statements in single transaction (skipCommit=true)", allSqlStatements.Count);
            try
            {
                var transactionSuccess = await _doltCli.ExecuteInTransactionAsync(skipCommit: true, allSqlStatements.ToArray());

                if (transactionSuccess)
                {
                    _logger.LogInformation("PP13-73-C3: Batch transaction succeeded - all {Count} conflicts resolved", conflictResolutions.Count);

                    // Mark all resolutions that weren't already failed as successful
                    foreach (var outcome in result.ResolutionOutcomes)
                    {
                        if (outcome.ErrorMessage == null)
                        {
                            outcome.Success = true;
                            result.SuccessfullyResolved++;
                        }
                    }
                    result.Success = result.FailedCount == 0;
                }
                else
                {
                    _logger.LogError("PP13-73-C3: Batch transaction FAILED - conflict resolutions were rolled back");
                    result.Success = false;
                    result.ErrorMessage = "Transaction failed - all resolutions rolled back. This may indicate remaining conflicts or a Dolt constraint violation.";

                    // Mark all resolutions as failed
                    foreach (var outcome in result.ResolutionOutcomes)
                    {
                        if (outcome.ErrorMessage == null)
                        {
                            outcome.Success = false;
                            outcome.ErrorMessage = "Transaction rolled back";
                            result.FailedCount++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PP13-73-C3: Exception during batch transaction execution");
                result.Success = false;
                result.ErrorMessage = $"Transaction exception: {ex.Message}";

                foreach (var outcome in result.ResolutionOutcomes)
                {
                    if (outcome.ErrorMessage == null)
                    {
                        outcome.Success = false;
                        outcome.ErrorMessage = "Transaction exception";
                        result.FailedCount++;
                    }
                }
            }

            return result;
        }

        #region Private Helper Methods

        /// <summary>
        /// Resolve conflict by keeping our version (target branch).
        /// Uses per-document resolution to avoid affecting other conflicts in the same table.
        /// PP13-73: Now passes collection_name for proper composite key support.
        /// </summary>
        private async Task<bool> ResolveKeepOurs(DetailedConflictInfo conflict)
        {
            _logger.LogDebug("Resolving conflict {ConflictId} (doc: {DocId}, collection: {Collection}) by keeping ours",
                conflict.ConflictId, conflict.DocumentId, conflict.Collection);

            try
            {
                // Use per-document resolution to only resolve this specific conflict
                // PP13-73: Pass collection name for composite key support
                var result = await _doltCli.ResolveDocumentConflictAsync(
                    "documents",            // tableName - always "documents" for document conflicts
                    conflict.DocumentId,
                    conflict.Collection,    // collectionName from DetailedConflictInfo
                    ConflictResolution.Ours);

                if (result.Success)
                {
                    _logger.LogInformation("Successfully resolved conflict {ConflictId} for document {DocId} in collection {Collection} with 'ours' strategy",
                        conflict.ConflictId, conflict.DocumentId, conflict.Collection);
                }
                else
                {
                    _logger.LogWarning("Failed to resolve conflict {ConflictId} for document {DocId} in collection {Collection} with 'ours' strategy: {Error}",
                        conflict.ConflictId, conflict.DocumentId, conflict.Collection, result.Error);
                }

                return result.Success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve conflict {ConflictId} (doc: {DocId}, collection: {Collection}) with ours strategy",
                    conflict.ConflictId, conflict.DocumentId, conflict.Collection);
                return false;
            }
        }

        /// <summary>
        /// Resolve conflict by keeping their version (source branch).
        /// Uses per-document resolution to avoid affecting other conflicts in the same table.
        /// PP13-73: Now passes collection_name for proper composite key support and verifies resolution.
        /// </summary>
        private async Task<bool> ResolveKeepTheirs(DetailedConflictInfo conflict)
        {
            _logger.LogDebug("Resolving conflict {ConflictId} (doc: {DocId}, collection: {Collection}) by keeping theirs",
                conflict.ConflictId, conflict.DocumentId, conflict.Collection);

            try
            {
                // Use per-document resolution to only resolve this specific conflict
                // PP13-73: Pass collection name for composite key support
                var result = await _doltCli.ResolveDocumentConflictAsync(
                    "documents",            // tableName - always "documents" for document conflicts
                    conflict.DocumentId,
                    conflict.Collection,    // collectionName from DetailedConflictInfo
                    ConflictResolution.Theirs);

                if (!result.Success)
                {
                    _logger.LogWarning("Failed to resolve conflict {ConflictId} for document {DocId} in collection {Collection} with 'theirs' strategy: {Error}",
                        conflict.ConflictId, conflict.DocumentId, conflict.Collection, result.Error);
                    return false;
                }

                // PP13-73-C2: Post-resolution verification - verify document content was actually updated
                if (!string.IsNullOrEmpty(conflict.TheirsContentHash))
                {
                    var verified = await VerifyDocumentContentAsync(
                        conflict.DocumentId,
                        conflict.Collection,
                        conflict.TheirsContentHash,
                        "theirs");

                    if (!verified)
                    {
                        _logger.LogError("PP13-73-C2: Post-resolution verification FAILED for conflict {ConflictId}. Document content was NOT updated to 'theirs' version.",
                            conflict.ConflictId);
                        return false;
                    }

                    _logger.LogDebug("PP13-73-C2: Post-resolution verification passed for conflict {ConflictId}", conflict.ConflictId);
                }

                _logger.LogInformation("Successfully resolved conflict {ConflictId} for document {DocId} in collection {Collection} with 'theirs' strategy",
                    conflict.ConflictId, conflict.DocumentId, conflict.Collection);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve conflict {ConflictId} (doc: {DocId}, collection: {Collection}) with theirs strategy",
                    conflict.ConflictId, conflict.DocumentId, conflict.Collection);
                return false;
            }
        }

        /// <summary>
        /// PP13-73-C2: Verify that a document's content matches the expected hash after resolution.
        /// This ensures that the resolution actually updated the document content.
        /// </summary>
        /// <param name="documentId">ID of the document to verify</param>
        /// <param name="collectionName">Collection containing the document</param>
        /// <param name="expectedContentHash">Expected content hash after resolution</param>
        /// <param name="strategy">Resolution strategy used (for logging)</param>
        /// <returns>True if document content matches expected hash, false otherwise</returns>
        private async Task<bool> VerifyDocumentContentAsync(
            string documentId,
            string collectionName,
            string expectedContentHash,
            string strategy)
        {
            try
            {
                var escapedDocId = documentId.Replace("'", "''");
                var escapedCollection = collectionName.Replace("'", "''");

                // Query the document's current content hash
                var sql = $"SELECT content_hash FROM documents WHERE doc_id = '{escapedDocId}' AND collection_name = '{escapedCollection}'";
                var actualHash = await _doltCli.ExecuteScalarAsync<string>(sql);

                if (string.IsNullOrEmpty(actualHash))
                {
                    _logger.LogWarning("PP13-73-C2: Verification failed - document {DocId} in {Collection} not found after resolution",
                        documentId, collectionName);
                    return false;
                }

                // Compare hashes (trim to handle potential whitespace/format differences)
                var matches = string.Equals(actualHash.Trim(), expectedContentHash.Trim(), StringComparison.OrdinalIgnoreCase);

                if (!matches)
                {
                    _logger.LogWarning("PP13-73-C2: Content hash mismatch for {DocId} in {Collection}. Expected: {Expected}, Actual: {Actual}",
                        documentId, collectionName, expectedContentHash, actualHash);
                }

                return matches;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PP13-73-C2: Failed to verify document content for {DocId} in {Collection}", documentId, collectionName);
                // Don't fail the resolution if verification query fails - the resolution itself may have succeeded
                return true;
            }
        }

        /// <summary>
        /// Automatically resolve a single conflict based on its characteristics
        /// </summary>
        private async Task<bool> AutoResolveConflictAsync(DetailedConflictInfo conflict)
        {
            _logger.LogDebug("Auto-resolving conflict {ConflictId} of type {Type}", 
                conflict.ConflictId, conflict.Type);

            try
            {
                // Handle different conflict types with appropriate auto-resolution strategies
                switch (conflict.Type)
                {
                    case ConflictType.ContentModification:
                        return await AutoResolveContentModification(conflict);
                    
                    case ConflictType.AddAdd:
                        return await AutoResolveAddAdd(conflict);
                    
                    case ConflictType.MetadataConflict:
                        return await AutoResolveMetadataConflict(conflict);
                    
                    default:
                        _logger.LogWarning("Cannot auto-resolve conflict type {Type}", conflict.Type);
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-resolve conflict {ConflictId}", conflict.ConflictId);
                return false;
            }
        }

        /// <summary>
        /// Auto-resolve content modification conflicts with non-overlapping changes
        /// </summary>
        private async Task<bool> AutoResolveContentModification(DetailedConflictInfo conflict)
        {
            // Implement field-level merge for non-overlapping changes
            var baseToOurs = GetModifiedFields(conflict.BaseValues, conflict.OurValues);
            var baseToTheirs = GetModifiedFields(conflict.BaseValues, conflict.TheirValues);
            
            var overlap = baseToOurs.Intersect(baseToTheirs);
            if (overlap.Any())
            {
                _logger.LogDebug("Cannot auto-resolve - overlapping changes in fields: {Fields}", 
                    string.Join(", ", overlap));
                return false;
            }
            
            // No overlapping changes - merge both
            var fieldResolutions = new Dictionary<string, string>();
            
            foreach (var field in baseToOurs)
            {
                fieldResolutions[field] = "ours";
            }
            
            foreach (var field in baseToTheirs)
            {
                fieldResolutions[field] = "theirs";
            }
            
            _logger.LogDebug("Auto-resolving with field merge: ours={OursFields}, theirs={TheirsFields}", 
                string.Join(",", baseToOurs), string.Join(",", baseToTheirs));
            
            return await ApplyFieldMergeAsync(
                conflict.Collection,
                conflict.DocumentId,
                fieldResolutions);
        }

        /// <summary>
        /// Auto-resolve add-add conflicts with identical content
        /// </summary>
        private async Task<bool> AutoResolveAddAdd(DetailedConflictInfo conflict)
        {
            // For identical content, we can safely keep either version (keep ours)
            var ourContent = conflict.OurValues.GetValueOrDefault("content")?.ToString();
            var theirContent = conflict.TheirValues.GetValueOrDefault("content")?.ToString();
            
            if (string.Equals(ourContent, theirContent, StringComparison.Ordinal))
            {
                _logger.LogDebug("Auto-resolving identical add-add conflict by keeping ours");
                return await ResolveKeepOurs(conflict);
            }
            
            _logger.LogDebug("Cannot auto-resolve add-add conflict - content differs");
            return false;
        }

        /// <summary>
        /// Auto-resolve metadata conflicts by preferring newer timestamps
        /// </summary>
        private async Task<bool> AutoResolveMetadataConflict(DetailedConflictInfo conflict)
        {
            // For metadata conflicts, prefer the version with the newer timestamp
            var ourTimestamp = ExtractTimestamp(conflict.OurValues);
            var theirTimestamp = ExtractTimestamp(conflict.TheirValues);
            
            if (ourTimestamp.HasValue && theirTimestamp.HasValue)
            {
                if (ourTimestamp > theirTimestamp)
                {
                    _logger.LogDebug("Auto-resolving metadata conflict by keeping ours (newer timestamp)");
                    return await ResolveKeepOurs(conflict);
                }
                else
                {
                    _logger.LogDebug("Auto-resolving metadata conflict by keeping theirs (newer timestamp)");
                    return await ResolveKeepTheirs(conflict);
                }
            }
            
            // Fallback to keeping ours if timestamps can't be compared
            _logger.LogDebug("Auto-resolving metadata conflict by keeping ours (timestamp comparison failed)");
            return await ResolveKeepOurs(conflict);
        }

        /// <summary>
        /// Get list of fields that were modified between two versions
        /// </summary>
        private List<string> GetModifiedFields(
            Dictionary<string, object> baseValues,
            Dictionary<string, object> newValues)
        {
            var modified = new List<string>();
            
            foreach (var kvp in newValues)
            {
                if (!baseValues.ContainsKey(kvp.Key) || 
                    !Equals(baseValues[kvp.Key], kvp.Value))
                {
                    modified.Add(kvp.Key);
                }
            }
            
            return modified;
        }

        /// <summary>
        /// Extract timestamp from document values for metadata conflict resolution
        /// </summary>
        private DateTime? ExtractTimestamp(Dictionary<string, object> values)
        {
            // Try common timestamp field names
            var timestampFields = new[] { "timestamp", "updated_at", "modified_at", "last_modified" };
            
            foreach (var field in timestampFields)
            {
                if (values.TryGetValue(field, out var value))
                {
                    if (DateTime.TryParse(value?.ToString(), out var timestamp))
                    {
                        return timestamp;
                    }
                }
            }
            
            return null;
        }

        #endregion
    }
}