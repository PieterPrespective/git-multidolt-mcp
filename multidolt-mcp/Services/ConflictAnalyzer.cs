using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DMMS.Models;
using Microsoft.Extensions.Logging;

namespace DMMS.Services
{
    /// <summary>
    /// Service implementation for analyzing merge conflicts
    /// Provides detailed conflict detection and auto-resolution identification
    /// </summary>
    public class ConflictAnalyzer : IConflictAnalyzer
    {
        private readonly IDoltCli _doltCli;
        private readonly ILogger<ConflictAnalyzer> _logger;

        /// <summary>
        /// Set of internal/system tables that should be excluded from conflict analysis
        /// and change counts to avoid reporting infrastructure tables as user data changes
        /// </summary>
        private static readonly HashSet<string> InternalTables = new(StringComparer.OrdinalIgnoreCase)
        {
            "chroma_sync_state",
            "document_sync_log",
            "sync_operations",
            "local_changes",
            "collections",
            "dolt_docs",
            "dolt_ignore",
            "dolt_procedures",
            "dolt_schemas",
            "dolt_query_catalog"
        };

        /// <summary>
        /// Checks if a table name is an internal/system table that should be excluded from user-facing metrics
        /// </summary>
        /// <param name="tableName">Name of the table to check</param>
        /// <returns>True if the table is internal and should be filtered out</returns>
        private static bool IsInternalTable(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                return true;

            // Filter out internal tables and Dolt system tables
            return InternalTables.Contains(tableName) ||
                   tableName.StartsWith("dolt_", StringComparison.OrdinalIgnoreCase) ||
                   tableName.StartsWith("__", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the conflict JSON from Dolt is a table-level summary rather than document-level conflicts.
        /// Table-level summaries have 'num_data_conflicts' and 'table' fields but NOT 'document_id' or 'collection'.
        /// These summaries don't provide document-level granularity needed for proper conflict detection.
        /// PP13-72-C2: Also returns true for empty results ({"rows": []}) to ensure fallback is triggered.
        /// </summary>
        /// <param name="conflictJson">The JSON returned from Dolt's preview merge conflicts</param>
        /// <returns>True if the JSON is a table-level summary or empty, requiring fallback to three-way diff</returns>
        private bool IsDoltTableLevelSummary(string conflictJson)
        {
            if (string.IsNullOrWhiteSpace(conflictJson))
            {
                _logger.LogDebug("PP13-72-C2 DIAG: IsDoltTableLevelSummary returning false for null/empty input");
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(conflictJson);
                var root = doc.RootElement;

                // PP13-72-C2: If root is an array (document-level conflicts), it's NOT a table-level summary
                if (root.ValueKind == JsonValueKind.Array)
                {
                    _logger.LogDebug("PP13-72-C2 DIAG: Root element is array - not a table-level summary");
                    return false;
                }

                // Check for the {"rows": [...]} structure from DOLT_PREVIEW_MERGE_CONFLICTS_SUMMARY
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    var rowCount = rows.GetArrayLength();
                    _logger.LogDebug("PP13-72-C2 DIAG: IsDoltTableLevelSummary found rows array with {Count} elements", rowCount);

                    // PP13-72-C2 FIX: Empty rows array means no document-level data from Dolt
                    // This should trigger the three-way diff fallback to properly detect document conflicts
                    if (rowCount == 0)
                    {
                        _logger.LogDebug("PP13-72-C2 DIAG: Empty rows array detected - returning true to trigger fallback");
                        return true;
                    }

                    foreach (var row in rows.EnumerateArray())
                    {
                        // Table-level summaries have "num_data_conflicts" and "table"
                        // but NOT "document_id" or "collection"
                        bool hasNumDataConflicts = row.TryGetProperty("num_data_conflicts", out _);
                        bool hasTableName = row.TryGetProperty("table", out _);
                        bool hasDocumentId = row.TryGetProperty("document_id", out _) ||
                                             row.TryGetProperty("doc_id", out _);
                        bool hasCollection = row.TryGetProperty("collection", out _) ||
                                             row.TryGetProperty("collection_name", out _);

                        _logger.LogDebug("PP13-72-C2 DIAG: Row analysis - hasNumDataConflicts={Num}, hasTableName={Table}, hasDocumentId={DocId}, hasCollection={Coll}",
                            hasNumDataConflicts, hasTableName, hasDocumentId, hasCollection);

                        // If we have table-level fields but no document-level fields, this is a table summary
                        if (hasNumDataConflicts && hasTableName && !hasDocumentId && !hasCollection)
                        {
                            _logger.LogDebug("Detected table-level summary format from Dolt - will use three-way diff fallback");
                            return true;
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("PP13-72-C2 DIAG: No 'rows' array found in JSON, checking root element kind: {Kind}", root.ValueKind);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug("Could not parse conflict JSON to detect format: {Error}", ex.Message);
            }

            _logger.LogDebug("PP13-72-C2 DIAG: IsDoltTableLevelSummary returning false - no triggers matched");
            return false;
        }

        /// <summary>
        /// Initializes a new instance of the ConflictAnalyzer class
        /// </summary>
        /// <param name="doltCli">Dolt CLI service for executing Dolt operations</param>
        /// <param name="logger">Logger for diagnostic information</param>
        public ConflictAnalyzer(IDoltCli doltCli, ILogger<ConflictAnalyzer> logger)
        {
            _doltCli = doltCli;
            _logger = logger;
        }

        /// <summary>
        /// Analyze a potential merge operation to detect conflicts and provide preview information
        /// </summary>
        public async Task<MergePreviewResult> AnalyzeMergeAsync(
            string sourceBranch,
            string targetBranch,
            bool includeAutoResolvable,
            bool detailedDiff)
        {
            var result = new MergePreviewResult
            {
                SourceBranch = sourceBranch,
                TargetBranch = targetBranch,
                Conflicts = new List<DetailedConflictInfo>()
            };

            try
            {
                _logger.LogInformation("Analyzing merge from {Source} to {Target}", sourceBranch, targetBranch);

                // Use Dolt's merge conflict preview if available
                string conflictSummaryJson;
                try
                {
                    conflictSummaryJson = await _doltCli.PreviewMergeConflictsAsync(sourceBranch, targetBranch);
                    _logger.LogDebug("PP13-72-C2 DIAG: PreviewMergeConflictsAsync returned: '{Result}' (length={Length})",
                        conflictSummaryJson?.Substring(0, Math.Min(200, conflictSummaryJson?.Length ?? 0)) ?? "null",
                        conflictSummaryJson?.Length ?? 0);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Dolt merge preview not available, falling back to manual analysis: {Error}", ex.Message);
                    conflictSummaryJson = await FallbackConflictAnalysis(sourceBranch, targetBranch);
                }

                // PP13-71-C1: Trigger fallback if empty, null, or if Dolt returned a table-level summary
                // Table-level summaries don't provide document-level granularity needed for proper conflict detection
                var isNullOrEmpty = string.IsNullOrWhiteSpace(conflictSummaryJson);
                var isEmptyArray = conflictSummaryJson == "[]";
                var isTableLevelSummary = IsDoltTableLevelSummary(conflictSummaryJson);

                _logger.LogDebug("PP13-72-C2 DIAG: Fallback trigger check - isNullOrEmpty={IsNull}, isEmptyArray={IsEmpty}, isTableLevelSummary={IsTable}",
                    isNullOrEmpty, isEmptyArray, isTableLevelSummary);

                if (isNullOrEmpty || isEmptyArray || isTableLevelSummary)
                {
                    _logger.LogInformation("Using three-way diff for document-level conflict detection (trigger: null={Null}, empty={Empty}, tableSummary={Table})",
                        isNullOrEmpty, isEmptyArray, isTableLevelSummary);
                    conflictSummaryJson = await FallbackConflictAnalysis(sourceBranch, targetBranch);
                    _logger.LogDebug("PP13-72-C2 DIAG: FallbackConflictAnalysis returned: '{Result}' (length={Length})",
                        conflictSummaryJson?.Substring(0, Math.Min(500, conflictSummaryJson?.Length ?? 0)) ?? "null",
                        conflictSummaryJson?.Length ?? 0);
                }
                else
                {
                    _logger.LogDebug("PP13-72-C2 DIAG: NOT triggering fallback - using Dolt's response directly");
                }

                // Parse conflict data
                var allConflicts = await ParseConflictSummary(conflictSummaryJson);
                
                // Store total before filtering
                var totalConflictsDetected = allConflicts.Count;
                
                // Filter based on auto-resolvable preference
                var conflicts = allConflicts;
                if (!includeAutoResolvable)
                {
                    conflicts = allConflicts.Where(c => !c.AutoResolvable).ToList();
                }

                // Analyze each conflict for detailed information
                foreach (var conflict in conflicts)
                {
                    conflict.ConflictId = GenerateConflictId(conflict);
                    conflict.AutoResolvable = await CanAutoResolveConflictAsync(conflict);
                    conflict.SuggestedResolution = DetermineSuggestedResolution(conflict);
                    conflict.ResolutionOptions = DetermineResolutionOptions(conflict);
                    
                    // Add detailed field conflicts if requested
                    if (detailedDiff)
                    {
                        conflict.FieldConflicts = await GetFieldConflicts(conflict);
                    }
                }

                result.Conflicts = conflicts;
                result.TotalConflictsDetected = totalConflictsDetected;
                result.CanAutoMerge = !allConflicts.Any(c => !c.AutoResolvable);
                result.Success = true;
                
                // Generate preview statistics
                result.Preview = await GenerateMergePreview(sourceBranch, targetBranch);
                
                // Check auxiliary tables
                result.AuxiliaryStatus = await CheckAuxiliaryTableStatus();
                
                // Determine recommended action
                result.RecommendedAction = DetermineRecommendedAction(result);
                result.Message = GeneratePreviewMessage(result);

                _logger.LogInformation("Merge analysis complete: {ConflictCount} conflicts, {AutoResolvable} auto-resolvable", 
                    conflicts.Count, conflicts.Count(c => c.AutoResolvable));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze merge");
                result.Success = false;
                result.Message = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Get detailed conflict information for a specific table
        /// </summary>
        public async Task<List<DetailedConflictInfo>> GetDetailedConflictsAsync(string tableName)
        {
            _logger.LogDebug("Getting detailed conflicts for table {Table}", tableName);
            
            var conflicts = new List<DetailedConflictInfo>();
            
            try
            {
                var conflictData = await _doltCli.GetConflictDetailsAsync(tableName);
                
                foreach (var row in conflictData)
                {
                    var conflict = ConvertToDetailedConflictInfo(row, tableName);
                    conflict.ConflictId = GenerateConflictId(conflict);
                    conflict.AutoResolvable = await CanAutoResolveConflictAsync(conflict);
                    
                    conflicts.Add(conflict);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get detailed conflicts for table {Table}", tableName);
            }
            
            return conflicts;
        }

        /// <summary>
        /// Generate resolution preview showing what each resolution option would produce
        /// </summary>
        public async Task<ResolutionPreview> GenerateResolutionPreviewAsync(
            DetailedConflictInfo conflict,
            ResolutionType resolutionType)
        {
            var preview = new ResolutionPreview
            {
                ConflictId = conflict.ConflictId,
                ResolutionType = resolutionType,
                ResultingContent = new DocumentContent { Exists = true }
            };

            try
            {
                _logger.LogDebug("Generating resolution preview for conflict {ConflictId} with {ResType}",
                    conflict.ConflictId, resolutionType);

                switch (resolutionType)
                {
                    case ResolutionType.KeepOurs:
                        preview.ResultingContent = BuildDocumentFromValues(conflict.OurValues);
                        preview.Description = "Keep all changes from the target branch (ours)";
                        preview.ConfidenceLevel = 100;
                        
                        // Check what would be lost from theirs
                        foreach (var kvp in conflict.TheirValues)
                        {
                            if (!conflict.OurValues.ContainsKey(kvp.Key) ||
                                !Equals(conflict.OurValues[kvp.Key], kvp.Value))
                            {
                                preview.DataLossWarnings.Add($"Field '{kvp.Key}' from source branch will be lost");
                            }
                        }
                        break;

                    case ResolutionType.KeepTheirs:
                        preview.ResultingContent = BuildDocumentFromValues(conflict.TheirValues);
                        preview.Description = "Keep all changes from the source branch (theirs)";
                        preview.ConfidenceLevel = 100;
                        
                        // Check what would be lost from ours
                        foreach (var kvp in conflict.OurValues)
                        {
                            if (!conflict.TheirValues.ContainsKey(kvp.Key) ||
                                !Equals(conflict.TheirValues[kvp.Key], kvp.Value))
                            {
                                preview.DataLossWarnings.Add($"Field '{kvp.Key}' from target branch will be lost");
                            }
                        }
                        break;

                    case ResolutionType.AutoResolve:
                    case ResolutionType.FieldMerge:
                        preview = await GenerateFieldMergePreview(conflict);
                        break;

                    case ResolutionType.Custom:
                        preview.Description = "Custom resolution - values will be provided by user";
                        preview.ConfidenceLevel = 0;
                        break;

                    default:
                        preview.Description = "Unknown resolution type";
                        preview.ConfidenceLevel = 0;
                        break;
                }

                return preview;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate resolution preview for conflict {ConflictId}",
                    conflict.ConflictId);
                preview.Description = $"Error generating preview: {ex.Message}";
                preview.ConfidenceLevel = 0;
                return preview;
            }
        }

        /// <summary>
        /// Generate field merge preview for intelligent field-level merging
        /// </summary>
        private async Task<ResolutionPreview> GenerateFieldMergePreview(DetailedConflictInfo conflict)
        {
            var preview = new ResolutionPreview
            {
                ConflictId = conflict.ConflictId,
                ResolutionType = ResolutionType.FieldMerge,
                ResultingContent = new DocumentContent { Exists = true }
            };

            var mergedValues = new Dictionary<string, object>();
            var confidence = 100;

            // Start with base values as foundation
            foreach (var kvp in conflict.BaseValues)
            {
                mergedValues[kvp.Key] = kvp.Value;
            }

            // Analyze each field for merging strategy
            var allFields = conflict.BaseValues.Keys
                .Union(conflict.OurValues.Keys)
                .Union(conflict.TheirValues.Keys)
                .Distinct();

            foreach (var field in allFields)
            {
                var baseVal = conflict.BaseValues.GetValueOrDefault(field);
                var ourVal = conflict.OurValues.GetValueOrDefault(field);
                var theirVal = conflict.TheirValues.GetValueOrDefault(field);

                // Determine merge strategy for this field
                if (Equals(ourVal, theirVal))
                {
                    // Both sides agree - no conflict
                    mergedValues[field] = ourVal ?? baseVal;
                }
                else if (Equals(baseVal, ourVal))
                {
                    // Only their side changed
                    mergedValues[field] = theirVal ?? baseVal;
                }
                else if (Equals(baseVal, theirVal))
                {
                    // Only our side changed
                    mergedValues[field] = ourVal ?? baseVal;
                }
                else
                {
                    // Both sides changed differently - need heuristics
                    var mergeResult = DetermineFieldMergeStrategy(field, baseVal, ourVal, theirVal);
                    mergedValues[field] = mergeResult.Value;
                    
                    if (!mergeResult.IsConfident)
                    {
                        confidence = Math.Min(confidence, 50);
                        preview.DataLossWarnings.Add(
                            $"Field '{field}' has conflicting changes - using {mergeResult.Strategy}");
                    }
                }
            }

            preview.ResultingContent = BuildDocumentFromValues(mergedValues);
            preview.ConfidenceLevel = confidence;
            preview.Description = confidence >= 80 
                ? "Automatic field-level merge with high confidence"
                : "Field-level merge with conflicts - manual review recommended";

            return preview;
        }

        /// <summary>
        /// Determine merge strategy for a conflicting field
        /// </summary>
        private (object? Value, string Strategy, bool IsConfident) DetermineFieldMergeStrategy(
            string fieldName,
            object? baseVal,
            object? ourVal,
            object? theirVal)
        {
            // For timestamps, prefer the newer one
            if (fieldName.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Contains("updated", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Contains("modified", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.TryParse(ourVal?.ToString(), out var ourDate) &&
                    DateTime.TryParse(theirVal?.ToString(), out var theirDate))
                {
                    return ourDate > theirDate 
                        ? (ourVal, "newer timestamp", true)
                        : (theirVal, "newer timestamp", true);
                }
            }

            // For version fields, prefer higher version
            if (fieldName.Contains("version", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(ourVal?.ToString(), out var ourVer) &&
                    int.TryParse(theirVal?.ToString(), out var theirVer))
                {
                    return ourVer > theirVer
                        ? (ourVal, "higher version", true)
                        : (theirVal, "higher version", true);
                }
            }

            // For content fields, we can't auto-merge safely
            if (fieldName.Equals("content", StringComparison.OrdinalIgnoreCase))
            {
                // Default to ours but mark as low confidence
                return (ourVal, "target branch content (requires review)", false);
            }

            // Default strategy: prefer non-null, then ours
            if (ourVal != null && theirVal == null)
                return (ourVal, "non-null value", true);
            if (ourVal == null && theirVal != null)
                return (theirVal, "non-null value", true);

            // Both non-null and different - default to ours with low confidence
            return (ourVal, "target branch value (conflict)", false);
        }

        /// <summary>
        /// Build document content from value dictionary
        /// </summary>
        private DocumentContent BuildDocumentFromValues(Dictionary<string, object> values)
        {
            var content = new DocumentContent
            {
                Exists = true
            };

            foreach (var kvp in values)
            {
                if (kvp.Key == "content" || kvp.Key == "document_content")
                {
                    content.Content = kvp.Value?.ToString();
                }
                else if (kvp.Key == "id" || kvp.Key == "document_id")
                {
                    // Skip ID fields
                    continue;
                }
                else if ((kvp.Key.Contains("updated", StringComparison.OrdinalIgnoreCase) ||
                         kvp.Key.Contains("modified", StringComparison.OrdinalIgnoreCase)) &&
                         DateTime.TryParse(kvp.Value?.ToString(), out var timestamp))
                {
                    content.LastModified = timestamp;
                    content.Metadata[kvp.Key] = kvp.Value;
                }
                else
                {
                    content.Metadata[kvp.Key] = kvp.Value;
                }
            }

            return content;
        }

        /// <summary>
        /// Get content comparison for a specific document across branches
        /// </summary>
        public async Task<ContentComparison> GetContentComparisonAsync(
            string tableName,
            string documentId,
            string sourceBranch,
            string targetBranch)
        {
            var comparison = new ContentComparison
            {
                TableName = tableName,
                DocumentId = documentId
            };
            
            try
            {
                _logger.LogDebug("Getting content comparison for {Table}/{DocId} between {Source} and {Target}",
                    tableName, documentId, sourceBranch, targetBranch);
                
                // Get merge base
                var mergeBaseResult = await ExecuteDoltCommandAsync("merge-base", sourceBranch, targetBranch);
                var mergeBase = mergeBaseResult.Success ? mergeBaseResult.Output?.Trim() : null;
                
                if (string.IsNullOrEmpty(mergeBase))
                {
                    _logger.LogWarning("Could not determine merge base, using current HEAD");
                    mergeBase = await _doltCli.GetHeadCommitHashAsync();
                }
                
                // Get content from base
                comparison.BaseContent = await GetDocumentAtCommit(tableName, documentId, mergeBase);
                
                // Get content from source branch
                comparison.SourceContent = await GetDocumentAtBranch(tableName, documentId, sourceBranch);
                
                // Get content from target branch  
                comparison.TargetContent = await GetDocumentAtBranch(tableName, documentId, targetBranch);
                
                // Analyze conflicts
                if (comparison.SourceContent?.Exists == true && comparison.TargetContent?.Exists == true)
                {
                    comparison.HasConflicts = !AreDocumentsEqual(comparison.SourceContent, comparison.TargetContent);
                    
                    if (comparison.HasConflicts)
                    {
                        comparison.ConflictingFields = GetConflictingFields(comparison.SourceContent, comparison.TargetContent);
                        comparison.SuggestedResolution = DetermineSuggestedResolution(comparison);
                    }
                }
                else if (comparison.SourceContent?.Exists != comparison.TargetContent?.Exists)
                {
                    // One exists, one doesn't - this is a delete-modify conflict
                    comparison.HasConflicts = true;
                    comparison.SuggestedResolution = "delete_modify_conflict";
                }
                
                return comparison;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get content comparison for {Table}/{DocId}", tableName, documentId);
                return comparison;
            }
        }

        /// <summary>
        /// Determine if a specific conflict can be automatically resolved.
        ///
        /// Auto-resolvable scenarios:
        /// - Only one branch modified the document (fast-forward)
        /// - Both branches made identical changes
        /// - Non-overlapping field modifications
        ///
        /// NOT auto-resolvable:
        /// - Both branches modified the same document content differently
        /// - Delete-modify conflicts
        /// </summary>
        public async Task<bool> CanAutoResolveConflictAsync(DetailedConflictInfo conflict)
        {
            try
            {
                // Check using the new content fields first (more reliable)
                if (conflict.OursContent != null || conflict.TheirsContent != null || conflict.BaseContent != null)
                {
                    // If both branches modified content differently from base = NOT auto-resolvable
                    var oursChangedFromBase = conflict.OursContent != conflict.BaseContent;
                    var theirsChangedFromBase = conflict.TheirsContent != conflict.BaseContent;
                    var bothChanged = oursChangedFromBase && theirsChangedFromBase;
                    var contentDiffers = conflict.OursContent != conflict.TheirsContent;

                    if (bothChanged && contentDiffers)
                    {
                        _logger.LogDebug("Conflict {ConflictId}: Both branches modified content differently - NOT auto-resolvable",
                            conflict.ConflictId);
                        return false;
                    }

                    // If content is identical in both branches = auto-resolvable
                    if (!contentDiffers)
                    {
                        _logger.LogDebug("Conflict {ConflictId}: Both branches have identical content - auto-resolvable",
                            conflict.ConflictId);
                        return true;
                    }

                    // If only one branch changed = auto-resolvable (use changed version)
                    if (oursChangedFromBase != theirsChangedFromBase)
                    {
                        _logger.LogDebug("Conflict {ConflictId}: Only one branch changed content - auto-resolvable",
                            conflict.ConflictId);
                        return true;
                    }
                }

                // Fallback to field-level analysis if content fields aren't populated
                if (conflict.Type == ConflictType.ContentModification)
                {
                    // Check if different fields were modified
                    var baseToOurs = GetModifiedFields(conflict.BaseValues, conflict.OurValues);
                    var baseToTheirs = GetModifiedFields(conflict.BaseValues, conflict.TheirValues);

                    // Check specifically for 'content' field overlap
                    var contentModifiedByOurs = baseToOurs.Contains("content");
                    var contentModifiedByTheirs = baseToTheirs.Contains("content");

                    if (contentModifiedByOurs && contentModifiedByTheirs)
                    {
                        // Both modified content - check if values are different
                        var ourContent = conflict.OurValues.GetValueOrDefault("content")?.ToString();
                        var theirContent = conflict.TheirValues.GetValueOrDefault("content")?.ToString();

                        if (ourContent != theirContent)
                        {
                            _logger.LogDebug("Conflict {ConflictId}: Both branches modified 'content' field differently - NOT auto-resolvable",
                                conflict.ConflictId);
                            return false;
                        }
                    }

                    // No overlap in modified fields = auto-resolvable
                    var hasOverlap = baseToOurs.Intersect(baseToTheirs).Any();
                    _logger.LogDebug("Conflict {ConflictId}: Modified fields overlap = {HasOverlap}",
                        conflict.ConflictId, hasOverlap);

                    return !hasOverlap;
                }

                if (conflict.Type == ConflictType.AddAdd)
                {
                    // Check if content is identical
                    var ourContent = conflict.OursContent ?? conflict.OurValues.GetValueOrDefault("content")?.ToString();
                    var theirContent = conflict.TheirsContent ?? conflict.TheirValues.GetValueOrDefault("content")?.ToString();
                    var isIdentical = string.Equals(ourContent, theirContent, StringComparison.Ordinal);

                    _logger.LogDebug("AddAdd conflict {ConflictId}: Content identical = {IsIdentical}",
                        conflict.ConflictId, isIdentical);

                    return isIdentical;
                }

                // Delete-modify conflicts are NOT auto-resolvable
                if (conflict.Type == ConflictType.DeleteModify)
                {
                    _logger.LogDebug("Conflict {ConflictId}: Delete-modify conflict - NOT auto-resolvable",
                        conflict.ConflictId);
                    return false;
                }

                // Metadata-only conflicts can often be auto-resolved by preferring newer timestamps
                if (conflict.Type == ConflictType.MetadataConflict)
                {
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error analyzing auto-resolve potential for conflict {ConflictId}",
                    conflict.ConflictId);
                return false;
            }
        }

        #region Private Helper Methods

        /// <summary>
        /// Fallback conflict analysis when Dolt's native preview is not available.
        /// Implements a proper three-way merge analysis by:
        /// 1. Finding the merge base commit
        /// 2. Comparing base → source changes
        /// 3. Comparing base → target changes
        /// 4. Identifying overlapping document modifications
        /// </summary>
        private async Task<string> FallbackConflictAnalysis(string sourceBranch, string targetBranch)
        {
            _logger.LogInformation("PP13-72-C2 DIAG: Entering FallbackConflictAnalysis for {Source} -> {Target}",
                sourceBranch, targetBranch);

            var conflicts = new List<DetailedConflictInfo>();

            try
            {
                // Step 1: Get the merge base commit
                _logger.LogDebug("PP13-72-C2 DIAG: Step 1 - Getting merge base");
                var mergeBase = await _doltCli.GetMergeBaseAsync(sourceBranch, targetBranch);
                if (string.IsNullOrEmpty(mergeBase))
                {
                    _logger.LogWarning("PP13-72-C2 DIAG: Could not determine merge base - returning empty conflict list");
                    return "[]";
                }

                _logger.LogDebug("PP13-72-C2 DIAG: Merge base commit: {MergeBase}", mergeBase);

                // Step 2: Get the HEAD commits for both branches WITHOUT checkouts (PP13-72-C4)
                // Using GetBranchCommitHashAsync eliminates side effects from branch checkouts
                _logger.LogDebug("PP13-72-C4 DIAG: Step 2 - Getting HEAD commits for both branches without checkouts");

                // PP13-72-C4: Get commit hashes without checking out branches
                var sourceCommit = await _doltCli.GetBranchCommitHashAsync(sourceBranch);
                if (string.IsNullOrEmpty(sourceCommit))
                {
                    _logger.LogWarning("PP13-72-C4: Could not get commit hash for source branch '{Branch}'", sourceBranch);
                    return "[]";
                }
                _logger.LogDebug("PP13-72-C4 DIAG: Source branch HEAD commit: {Commit}", sourceCommit);

                var targetCommit = await _doltCli.GetBranchCommitHashAsync(targetBranch);
                if (string.IsNullOrEmpty(targetCommit))
                {
                    _logger.LogWarning("PP13-72-C4: Could not get commit hash for target branch '{Branch}'", targetBranch);
                    return "[]";
                }
                _logger.LogDebug("PP13-72-C4 DIAG: Target branch HEAD commit: {Commit}", targetCommit);

                _logger.LogDebug("PP13-72-C4 DIAG: Commits - Source={Source}, Target={Target}, MergeBase={Base}",
                    sourceCommit, targetCommit, mergeBase);

                // Step 3: Get list of user document tables (filter out internal tables)
                _logger.LogDebug("PP13-72-C2 DIAG: Step 3 - Getting affected tables");
                var affectedTables = await GetUserAffectedTables(mergeBase, sourceCommit, targetCommit);
                _logger.LogDebug("PP13-72-C2 DIAG: User tables affected ({Count}): {Tables}",
                    affectedTables.Count, string.Join(", ", affectedTables));

                // Step 4: For each table, detect document-level conflicts
                _logger.LogDebug("PP13-72-C2 DIAG: Step 4 - Detecting document conflicts for {Count} tables", affectedTables.Count);
                foreach (var tableName in affectedTables)
                {
                    _logger.LogDebug("PP13-72-C2 DIAG: Analyzing table: {Table}", tableName);
                    var tableConflicts = await DetectDocumentConflictsForTable(
                        tableName, mergeBase, sourceCommit, targetCommit, sourceBranch, targetBranch);
                    _logger.LogDebug("PP13-72-C2 DIAG: Table {Table} produced {Count} conflicts", tableName, tableConflicts.Count);
                    conflicts.AddRange(tableConflicts);
                }

                _logger.LogInformation("PP13-72-C2 DIAG: Three-way diff analysis found {Count} document-level conflicts", conflicts.Count);

                // Convert to JSON for parsing by standard flow
                var conflictData = conflicts.Select(c => new
                {
                    collection = c.Collection,
                    document_id = c.DocumentId,
                    conflict_type = c.Type.ToString().ToLowerInvariant(),
                    base_content = c.BaseContent,
                    our_content = c.OursContent,
                    their_content = c.TheirsContent,
                    base_content_hash = c.BaseContentHash,
                    our_content_hash = c.OursContentHash,
                    their_content_hash = c.TheirsContentHash
                }).ToList();

                return JsonSerializer.Serialize(conflictData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Three-way diff analysis failed");
                return "[]";
            }
        }

        /// <summary>
        /// Gets list of user-facing tables that have changes, excluding internal/system tables
        /// </summary>
        private async Task<List<string>> GetUserAffectedTables(string mergeBase, string sourceCommit, string targetCommit)
        {
            var allTables = new HashSet<string>();

            try
            {
                // Get tables changed between merge base and source
                var sourceDiff = await ExecuteDoltCommandAsync("diff", "--name-only", mergeBase, sourceCommit);
                if (sourceDiff.Success && !string.IsNullOrWhiteSpace(sourceDiff.Output))
                {
                    foreach (var table in sourceDiff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = table.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            allTables.Add(trimmed);
                        }
                    }
                }

                // Get tables changed between merge base and target
                var targetDiff = await ExecuteDoltCommandAsync("diff", "--name-only", mergeBase, targetCommit);
                if (targetDiff.Success && !string.IsNullOrWhiteSpace(targetDiff.Output))
                {
                    foreach (var table in targetDiff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = table.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            allTables.Add(trimmed);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting affected tables");
            }

            // Filter out internal tables and return only user tables
            return allTables.Where(t => !IsInternalTable(t)).ToList();
        }

        /// <summary>
        /// Detects document-level conflicts for a specific table by comparing changes
        /// from both branches against the merge base
        /// </summary>
        private async Task<List<DetailedConflictInfo>> DetectDocumentConflictsForTable(
            string tableName,
            string mergeBase,
            string sourceCommit,
            string targetCommit,
            string sourceBranch,
            string targetBranch)
        {
            var conflicts = new List<DetailedConflictInfo>();

            try
            {
                // Get document changes from base to source (theirs)
                var sourceChanges = await _doltCli.GetDocumentChangesBetweenCommitsAsync(mergeBase, sourceCommit, tableName);

                // Get document changes from base to target (ours)
                var targetChanges = await _doltCli.GetDocumentChangesBetweenCommitsAsync(mergeBase, targetCommit, tableName);

                // Find documents modified in both branches (potential conflicts)
                var overlappingDocs = sourceChanges.Keys.Intersect(targetChanges.Keys).ToList();

                _logger.LogDebug("Table {Table}: source changed {S}, target changed {T}, overlapping {O}",
                    tableName, sourceChanges.Count, targetChanges.Count, overlappingDocs.Count);

                foreach (var docId in overlappingDocs)
                {
                    var sourceChangeType = sourceChanges[docId];
                    var targetChangeType = targetChanges[docId];

                    // Get document content at each commit
                    var baseDoc = await _doltCli.GetDocumentAtCommitAsync(tableName, docId, mergeBase);
                    var sourceDoc = await _doltCli.GetDocumentAtCommitAsync(tableName, docId, sourceCommit);
                    var targetDoc = await _doltCli.GetDocumentAtCommitAsync(tableName, docId, targetCommit);

                    // Determine if this is a real conflict
                    var isConflict = DetermineIfConflict(sourceChangeType, targetChangeType, baseDoc, sourceDoc, targetDoc);

                    if (isConflict)
                    {
                        // Extract the actual collection name from the document metadata
                        // The documents table stores collection_name as a column
                        var collectionName = tableName; // Default to table name if not found
                        var docWithCollection = targetDoc ?? sourceDoc ?? baseDoc;
                        if (docWithCollection?.Metadata != null &&
                            docWithCollection.Metadata.TryGetValue("collection_name", out var collNameObj))
                        {
                            collectionName = collNameObj?.ToString() ?? tableName;
                        }

                        var conflict = new DetailedConflictInfo
                        {
                            Collection = collectionName,
                            DocumentId = docId,
                            Type = DetermineConflictType(sourceChangeType, targetChangeType),
                            BaseContent = baseDoc?.Content,
                            OursContent = targetDoc?.Content,  // Target = ours (we're merging into target)
                            TheirsContent = sourceDoc?.Content, // Source = theirs (merging from source)
                            BaseContentHash = ComputeValueHash(baseDoc?.Content),
                            OursContentHash = ComputeValueHash(targetDoc?.Content),
                            TheirsContentHash = ComputeValueHash(sourceDoc?.Content)
                        };

                        // Populate value dictionaries for field-level analysis
                        if (baseDoc != null)
                        {
                            conflict.BaseValues["content"] = baseDoc.Content ?? "";
                            foreach (var kvp in baseDoc.Metadata)
                            {
                                conflict.BaseValues[kvp.Key] = kvp.Value;
                            }
                        }

                        if (targetDoc != null)
                        {
                            conflict.OurValues["content"] = targetDoc.Content ?? "";
                            foreach (var kvp in targetDoc.Metadata)
                            {
                                conflict.OurValues[kvp.Key] = kvp.Value;
                            }
                        }

                        if (sourceDoc != null)
                        {
                            conflict.TheirValues["content"] = sourceDoc.Content ?? "";
                            foreach (var kvp in sourceDoc.Metadata)
                            {
                                conflict.TheirValues[kvp.Key] = kvp.Value;
                            }
                        }

                        conflicts.Add(conflict);
                        _logger.LogDebug("Detected conflict for document {DocId} in {Table}: {Type}",
                            docId, tableName, conflict.Type);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error detecting conflicts for table {Table}", tableName);
            }

            return conflicts;
        }

        /// <summary>
        /// Determines if overlapping changes constitute a real conflict
        /// </summary>
        private bool DetermineIfConflict(
            string sourceChangeType,
            string targetChangeType,
            DocumentContent? baseDoc,
            DocumentContent? sourceDoc,
            DocumentContent? targetDoc)
        {
            // If both made identical changes, it's not a conflict
            if (sourceDoc?.Content == targetDoc?.Content)
            {
                _logger.LogDebug("No conflict - identical changes in both branches");
                return false;
            }

            // If one side didn't actually change from base, it's not a conflict
            if (sourceDoc?.Content == baseDoc?.Content)
            {
                _logger.LogDebug("No conflict - source didn't change from base");
                return false;
            }

            if (targetDoc?.Content == baseDoc?.Content)
            {
                _logger.LogDebug("No conflict - target didn't change from base");
                return false;
            }

            // Delete-modify or modify-delete scenarios are conflicts
            if (sourceChangeType == "deleted" && targetChangeType == "modified")
            {
                return true;
            }

            if (sourceChangeType == "modified" && targetChangeType == "deleted")
            {
                return true;
            }

            // Both modified/added differently from base - this is a conflict
            return true;
        }

        /// <summary>
        /// Determines the conflict type based on the change types from both branches
        /// </summary>
        private ConflictType DetermineConflictType(string sourceChangeType, string targetChangeType)
        {
            if (sourceChangeType == "added" && targetChangeType == "added")
            {
                return ConflictType.AddAdd;
            }

            if ((sourceChangeType == "deleted" && targetChangeType == "modified") ||
                (sourceChangeType == "modified" && targetChangeType == "deleted"))
            {
                return ConflictType.DeleteModify;
            }

            return ConflictType.ContentModification;
        }

        /// <summary>
        /// Parse conflict summary JSON from Dolt into structured conflict objects
        /// </summary>
        private async Task<List<DetailedConflictInfo>> ParseConflictSummary(string conflictJson)
        {
            var conflicts = new List<DetailedConflictInfo>();
            
            if (string.IsNullOrWhiteSpace(conflictJson) || conflictJson == "[]")
            {
                return conflicts; // No conflicts
            }

            _logger.LogDebug("Parsing conflict JSON: {Json}", conflictJson);

            try
            {
                // First, try to parse as JsonElement to understand the structure
                var jsonDocument = JsonDocument.Parse(conflictJson);
                var rootElement = jsonDocument.RootElement;

                if (rootElement.ValueKind == JsonValueKind.Array)
                {
                    // Parse as array of conflicts
                    foreach (var conflictElement in rootElement.EnumerateArray())
                    {
                        var conflict = ParseSingleConflictElement(conflictElement);
                        if (conflict != null)
                        {
                            conflicts.Add(conflict);
                        }
                    }
                }
                else if (rootElement.ValueKind == JsonValueKind.Object)
                {
                    // Check if this is an empty object indicating successful auto-merge
                    if (rootElement.EnumerateObject().Any())
                    {
                        // Single conflict object with actual content
                        var conflict = ParseSingleConflictElement(rootElement);
                        if (conflict != null)
                        {
                            conflicts.Add(conflict);
                        }
                    }
                    else
                    {
                        // Empty object {} indicates successful auto-merge, no conflicts
                        _logger.LogDebug("Empty conflict object detected - indicates successful auto-merge");
                    }
                }
                else
                {
                    _logger.LogWarning("Unexpected JSON format for conflict summary: {ValueKind}", rootElement.ValueKind);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse conflict summary JSON: {Json}", conflictJson);
            }
            
            return conflicts;
        }

        /// <summary>
        /// Parse a single conflict element from JSON
        /// </summary>
        private DetailedConflictInfo? ParseSingleConflictElement(JsonElement conflictElement)
        {
            try
            {
                var conflict = new DetailedConflictInfo
                {
                    Type = ConflictType.ContentModification // Default, can be refined
                };

                // Try to extract common fields with flexible property names
                // First check for table/collection name
                if (conflictElement.TryGetProperty("table_name", out var tableNameProp) ||
                    conflictElement.TryGetProperty("table", out tableNameProp) ||
                    conflictElement.TryGetProperty("collection_name", out tableNameProp) ||
                    conflictElement.TryGetProperty("collection", out tableNameProp))
                {
                    conflict.Collection = tableNameProp.GetString() ?? "";
                }

                // If collection is still empty, check for nested structure
                if (string.IsNullOrEmpty(conflict.Collection))
                {
                    // Check for Dolt conflict structure (e.g., from dolt_conflicts table)
                    if (conflictElement.TryGetProperty("our_table", out var ourTableProp))
                    {
                        conflict.Collection = ourTableProp.GetString() ?? "";
                    }
                    else if (conflictElement.TryGetProperty("base_table", out var baseTableProp))
                    {
                        conflict.Collection = baseTableProp.GetString() ?? "";
                    }
                }

                // Try to extract document ID with multiple strategies
                if (conflictElement.TryGetProperty("doc_id", out var docIdProp) ||
                    conflictElement.TryGetProperty("document_id", out docIdProp) ||
                    conflictElement.TryGetProperty("id", out docIdProp))
                {
                    conflict.DocumentId = docIdProp.GetString() ?? "";
                }

                // If document ID is still empty, try to extract from conflict data
                if (string.IsNullOrEmpty(conflict.DocumentId))
                {
                    // Check for Dolt's conflict row structure
                    if (conflictElement.TryGetProperty("our_id", out var ourIdProp))
                    {
                        conflict.DocumentId = ourIdProp.GetString() ?? "";
                    }
                    else if (conflictElement.TryGetProperty("their_id", out var theirIdProp))
                    {
                        conflict.DocumentId = theirIdProp.GetString() ?? "";
                    }
                    else if (conflictElement.TryGetProperty("base_id", out var baseIdProp))
                    {
                        conflict.DocumentId = baseIdProp.GetString() ?? "";
                    }
                    
                    // Try to extract from nested row data
                    if (string.IsNullOrEmpty(conflict.DocumentId))
                    {
                        if (conflictElement.TryGetProperty("our_row", out var ourRow) && 
                            ourRow.TryGetProperty("id", out var ourRowId))
                        {
                            conflict.DocumentId = ourRowId.GetString() ?? "";
                        }
                        else if (conflictElement.TryGetProperty("their_row", out var theirRow) &&
                                theirRow.TryGetProperty("id", out var theirRowId))
                        {
                            conflict.DocumentId = theirRowId.GetString() ?? "";
                        }
                    }
                }
                
                // Extract conflict type if available
                if (conflictElement.TryGetProperty("conflict_type", out var typeProp) ||
                    conflictElement.TryGetProperty("type", out typeProp))
                {
                    var typeStr = typeProp.GetString();
                    conflict.Type = ParseConflictType(typeStr);
                }

                // Extract content fields from three-way diff output
                if (conflictElement.TryGetProperty("base_content", out var baseContentProp))
                {
                    conflict.BaseContent = baseContentProp.GetString();
                }
                if (conflictElement.TryGetProperty("our_content", out var ourContentProp))
                {
                    conflict.OursContent = ourContentProp.GetString();
                }
                if (conflictElement.TryGetProperty("their_content", out var theirContentProp))
                {
                    conflict.TheirsContent = theirContentProp.GetString();
                }

                // Extract content hashes
                if (conflictElement.TryGetProperty("base_content_hash", out var baseHashProp))
                {
                    conflict.BaseContentHash = baseHashProp.GetString();
                }
                if (conflictElement.TryGetProperty("our_content_hash", out var ourHashProp))
                {
                    conflict.OursContentHash = ourHashProp.GetString();
                }
                if (conflictElement.TryGetProperty("their_content_hash", out var theirHashProp))
                {
                    conflict.TheirsContentHash = theirHashProp.GetString();
                }

                // Extract base, our, and their values if present
                ExtractConflictValues(conflictElement, conflict);
                
                // Log warning if critical fields are missing
                if (string.IsNullOrEmpty(conflict.Collection))
                {
                    _logger.LogWarning("Could not extract collection name from conflict element: {Json}", 
                        conflictElement.ToString());
                }
                if (string.IsNullOrEmpty(conflict.DocumentId))
                {
                    _logger.LogWarning("Could not extract document ID from conflict element: {Json}", 
                        conflictElement.ToString());
                }

                return conflict;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse single conflict element: {Json}", 
                    conflictElement.ToString());
                return null;
            }
        }

        /// <summary>
        /// Parse conflict type from string
        /// </summary>
        private ConflictType ParseConflictType(string? typeStr)
        {
            if (string.IsNullOrWhiteSpace(typeStr))
                return ConflictType.ContentModification;
            
            switch (typeStr.ToLowerInvariant())
            {
                case "add-add":
                case "addadd":
                case "add_add":
                    return ConflictType.AddAdd;
                    
                case "delete-modify":
                case "deletemodify":
                case "delete_modify":
                case "modify-delete":
                    return ConflictType.DeleteModify;
                    
                case "metadata":
                case "meta":
                    return ConflictType.MetadataConflict;
                    
                case "schema":
                    return ConflictType.SchemaConflict;
                    
                default:
                    return ConflictType.ContentModification;
            }
        }

        /// <summary>
        /// Extract conflict values from JSON element
        /// </summary>
        private void ExtractConflictValues(JsonElement conflictElement, DetailedConflictInfo conflict)
        {
            // Extract base values
            if (conflictElement.TryGetProperty("base_row", out var baseRow) ||
                conflictElement.TryGetProperty("base", out baseRow))
            {
                ExtractValuesToDict(baseRow, conflict.BaseValues);
            }
            
            // Extract our values
            if (conflictElement.TryGetProperty("our_row", out var ourRow) ||
                conflictElement.TryGetProperty("ours", out ourRow) ||
                conflictElement.TryGetProperty("our", out ourRow))
            {
                ExtractValuesToDict(ourRow, conflict.OurValues);
            }
            
            // Extract their values
            if (conflictElement.TryGetProperty("their_row", out var theirRow) ||
                conflictElement.TryGetProperty("theirs", out theirRow) ||
                conflictElement.TryGetProperty("their", out theirRow))
            {
                ExtractValuesToDict(theirRow, conflict.TheirValues);
            }
            
            // Also check for flattened field structure (e.g., base_content, our_content, their_content)
            foreach (var property in conflictElement.EnumerateObject())
            {
                var propName = property.Name;
                
                if (propName.StartsWith("base_", StringComparison.OrdinalIgnoreCase))
                {
                    var fieldName = propName.Substring(5);
                    conflict.BaseValues[fieldName] = JsonElementToObject(property.Value);
                }
                else if (propName.StartsWith("our_", StringComparison.OrdinalIgnoreCase))
                {
                    var fieldName = propName.Substring(4);
                    conflict.OurValues[fieldName] = JsonElementToObject(property.Value);
                }
                else if (propName.StartsWith("their_", StringComparison.OrdinalIgnoreCase))
                {
                    var fieldName = propName.Substring(6);
                    conflict.TheirValues[fieldName] = JsonElementToObject(property.Value);
                }
            }
        }

        /// <summary>
        /// Extract values from JSON element to dictionary
        /// </summary>
        private void ExtractValuesToDict(JsonElement element, Dictionary<string, object> dict)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    dict[property.Name] = JsonElementToObject(property.Value);
                }
            }
        }

        /// <summary>
        /// Convert JsonElement to object
        /// </summary>
        private object JsonElementToObject(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString() ?? "";
                case JsonValueKind.Number:
                    if (element.TryGetInt32(out var intVal))
                        return intVal;
                    if (element.TryGetInt64(out var longVal))
                        return longVal;
                    if (element.TryGetDouble(out var doubleVal))
                        return doubleVal;
                    return element.GetRawText();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null!;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(JsonElementToObject).ToList();
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in element.EnumerateObject())
                    {
                        dict[prop.Name] = JsonElementToObject(prop.Value);
                    }
                    return dict;
                default:
                    return element.GetRawText();
            }
        }

        /// <summary>
        /// Generate a stable conflict ID based on conflict characteristics.
        /// PP13-73-C1: The ID is deterministically generated from Collection, DocumentId, and Type
        /// to ensure consistency between Preview and Execute operations.
        /// </summary>
        private string GenerateConflictId(DetailedConflictInfo conflict)
        {
            // Generate stable GUID based on conflict content
            var input = $"{conflict.Collection}_{conflict.DocumentId}_{conflict.Type}";

            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            var hashString = BitConverter.ToString(hash).Replace("-", "").Substring(0, 12).ToLower();

            var conflictId = $"conf_{hashString}";

            // PP13-73-C1: Debug logging for conflict ID generation traceability
            _logger.LogDebug("PP13-73-C1: GenerateConflictId input='{Input}' => '{ConflictId}'",
                input, conflictId);

            return conflictId;
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
        /// Convert raw conflict data to DetailedConflictInfo object
        /// </summary>
        private DetailedConflictInfo ConvertToDetailedConflictInfo(
            Dictionary<string, object> conflictRow,
            string tableName)
        {
            // PP13-73-C1: Extract actual collection name from conflict row for consistent ID generation
            // The conflict row should contain 'our_collection_name' field from dolt_conflicts_documents table
            var collectionName = conflictRow.GetValueOrDefault("our_collection_name")?.ToString() ?? tableName;

            var conflict = new DetailedConflictInfo
            {
                Collection = collectionName,
                DocumentId = conflictRow.GetValueOrDefault("our_doc_id")?.ToString() ?? "",
                Type = ConflictType.ContentModification
            };

            // PP13-73-C1: Debug logging to track collection value used in ID generation
            _logger.LogDebug("PP13-73-C1: ConvertToDetailedConflictInfo using Collection='{Collection}' for doc '{DocId}' (tableName was '{TableName}')",
                conflict.Collection, conflict.DocumentId, tableName);

            // Extract base, our, and their values from conflict row
            foreach (var kvp in conflictRow)
            {
                if (kvp.Key.StartsWith("base_"))
                {
                    var fieldName = kvp.Key.Substring(5);
                    conflict.BaseValues[fieldName] = kvp.Value;
                }
                else if (kvp.Key.StartsWith("our_"))
                {
                    var fieldName = kvp.Key.Substring(4);
                    conflict.OurValues[fieldName] = kvp.Value;
                }
                else if (kvp.Key.StartsWith("their_"))
                {
                    var fieldName = kvp.Key.Substring(6);
                    conflict.TheirValues[fieldName] = kvp.Value;
                }
            }

            return conflict;
        }

        /// <summary>
        /// Get field-level conflict details for a specific conflict
        /// </summary>
        private async Task<List<FieldConflict>> GetFieldConflicts(DetailedConflictInfo conflict)
        {
            var fieldConflicts = new List<FieldConflict>();
            
            // Identify all fields that have conflicts
            var allFields = conflict.BaseValues.Keys
                .Union(conflict.OurValues.Keys)
                .Union(conflict.TheirValues.Keys)
                .ToList();

            foreach (var field in allFields)
            {
                var baseValue = conflict.BaseValues.GetValueOrDefault(field);
                var ourValue = conflict.OurValues.GetValueOrDefault(field);
                var theirValue = conflict.TheirValues.GetValueOrDefault(field);

                // Check if this field actually has a conflict
                var hasConflict = !Equals(ourValue, theirValue);
                
                if (hasConflict)
                {
                    var fieldConflict = new FieldConflict
                    {
                        FieldName = field,
                        BaseValue = baseValue,
                        OurValue = ourValue,
                        TheirValue = theirValue,
                        BaseHash = ComputeValueHash(baseValue),
                        OurHash = ComputeValueHash(ourValue),
                        TheirHash = ComputeValueHash(theirValue),
                        CanAutoMerge = !Equals(baseValue, ourValue) && !Equals(baseValue, theirValue) && 
                                      !Equals(ourValue, theirValue)
                    };
                    
                    fieldConflicts.Add(fieldConflict);
                }
            }

            return fieldConflicts;
        }

        /// <summary>
        /// Compute hash of a field value for change tracking
        /// </summary>
        private string ComputeValueHash(object? value)
        {
            if (value == null) return "null";
            
            var valueString = value.ToString() ?? "";
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(valueString));
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 8).ToLower();
        }

        /// <summary>
        /// Determine suggested resolution strategy for a conflict
        /// </summary>
        private string DetermineSuggestedResolution(DetailedConflictInfo conflict)
        {
            if (conflict.AutoResolvable)
            {
                return conflict.Type == ConflictType.ContentModification ? "field_merge" : "auto_resolve";
            }
            
            return "manual_review";
        }

        /// <summary>
        /// Determine available resolution options for a conflict
        /// </summary>
        private List<string> DetermineResolutionOptions(DetailedConflictInfo conflict)
        {
            var options = new List<string> { "keep_ours", "keep_theirs" };
            
            if (conflict.Type == ConflictType.ContentModification)
            {
                options.Add("field_merge");
                options.Add("custom_merge");
            }
            
            if (conflict.AutoResolvable)
            {
                options.Add("auto_resolve");
            }
            
            return options;
        }

        /// <summary>
        /// Generate merge preview statistics.
        /// Filters out internal/system tables to report only user-facing data changes.
        /// </summary>
        private async Task<MergePreviewInfo> GenerateMergePreview(string sourceBranch, string targetBranch)
        {
            try
            {
                _logger.LogDebug("Generating merge preview statistics for {Source} -> {Target}", sourceBranch, targetBranch);

                // Get the merge base commit
                var mergeBase = await _doltCli.GetMergeBaseAsync(sourceBranch, targetBranch);

                if (string.IsNullOrEmpty(mergeBase))
                {
                    _logger.LogWarning("Could not determine merge base, using target branch HEAD");
                    mergeBase = await _doltCli.GetHeadCommitHashAsync();
                }

                var previewInfo = new MergePreviewInfo
                {
                    DocumentsAdded = 0,
                    DocumentsModified = 0,
                    DocumentsDeleted = 0,
                    CollectionsAffected = 0
                };

                // Get list of affected tables/collections
                var tablesResult = await ExecuteDoltCommandAsync("diff", "--name-only", mergeBase, sourceBranch);
                if (tablesResult.Success && !string.IsNullOrWhiteSpace(tablesResult.Output))
                {
                    var allTables = tablesResult.Output
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .Distinct()
                        .ToList();

                    // Filter out internal tables for user-facing counts
                    var userTables = allTables.Where(t => !IsInternalTable(t)).ToList();

                    _logger.LogDebug("Found {Total} tables changed, {User} are user tables (filtered out {Internal} internal)",
                        allTables.Count, userTables.Count, allTables.Count - userTables.Count);

                    previewInfo.CollectionsAffected = userTables.Count;

                    // Analyze only user tables for document changes
                    foreach (var table in userTables)
                    {
                        await AnalyzeTableChanges(table, mergeBase, sourceBranch, previewInfo);
                    }
                }

                _logger.LogDebug("Merge preview: +{Added} ~{Modified} -{Deleted} documents across {Collections} collections",
                    previewInfo.DocumentsAdded, previewInfo.DocumentsModified,
                    previewInfo.DocumentsDeleted, previewInfo.CollectionsAffected);

                return previewInfo;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate accurate merge preview, using estimates");
                return new MergePreviewInfo
                {
                    DocumentsAdded = 0,
                    DocumentsModified = 0,
                    DocumentsDeleted = 0,
                    CollectionsAffected = 0
                };
            }
        }

        /// <summary>
        /// Check status of auxiliary tables for conflicts
        /// </summary>
        private async Task<AuxiliaryTableStatus> CheckAuxiliaryTableStatus()
        {
            return new AuxiliaryTableStatus
            {
                SyncStateConflict = false,
                LocalChangesConflict = false,
                SyncOperationsConflict = false
            };
        }

        /// <summary>
        /// Determine recommended action based on analysis results
        /// </summary>
        private string DetermineRecommendedAction(MergePreviewResult result)
        {
            if (!result.Conflicts.Any())
            {
                return "Execute merge - no conflicts detected";
            }
            
            if (result.CanAutoMerge)
            {
                return "Execute merge with auto-resolution";
            }
            
            return "Review conflicts and provide resolution preferences";
        }

        /// <summary>
        /// Generate human-readable preview message
        /// </summary>
        private string GeneratePreviewMessage(MergePreviewResult result)
        {
            var conflictCount = result.Conflicts.Count;
            var autoResolvable = result.Conflicts.Count(c => c.AutoResolvable);
            var manualRequired = conflictCount - autoResolvable;
            
            if (conflictCount == 0)
            {
                return "No conflicts detected - merge can proceed automatically";
            }
            
            if (manualRequired == 0)
            {
                return $"All {conflictCount} conflicts can be automatically resolved";
            }
            
            return $"Merge has {conflictCount} conflicts: {autoResolvable} auto-resolvable, {manualRequired} require manual resolution";
        }

        /// <summary>
        /// Execute a Dolt command directly
        /// </summary>
        private async Task<DoltCommandResult> ExecuteDoltCommandAsync(params string[] args)
        {
            try
            {
                // Use reflection to access the internal ExecuteDoltCommandAsync method
                var doltCliType = _doltCli.GetType();
                var executeMethod = doltCliType.GetMethod("ExecuteDoltCommandAsync", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (executeMethod != null)
                {
                    var task = executeMethod.Invoke(_doltCli, new object[] { args }) as Task<DoltCommandResult>;
                    if (task != null)
                    {
                        return await task;
                    }
                }
                
                // Fallback if reflection fails
                _logger.LogWarning("Could not execute Dolt command directly, using fallback");
                return new DoltCommandResult(false, "Command execution not available", "", 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute Dolt command: {Args}", string.Join(" ", args));
                return new DoltCommandResult(false, ex.Message, "", 1);
            }
        }

        /// <summary>
        /// Parse diff statistics from Dolt diff output
        /// </summary>
        private MergePreviewInfo ParseDiffStatistics(string diffOutput)
        {
            var info = new MergePreviewInfo
            {
                DocumentsAdded = 0,
                DocumentsModified = 0,
                DocumentsDeleted = 0,
                CollectionsAffected = 0
            };
            
            if (string.IsNullOrWhiteSpace(diffOutput))
                return info;
            
            // Try to parse JSON format first
            if (diffOutput.TrimStart().StartsWith("{") || diffOutput.TrimStart().StartsWith("["))
            {
                try
                {
                    var jsonDoc = JsonDocument.Parse(diffOutput);
                    // Parse JSON diff statistics
                    if (jsonDoc.RootElement.TryGetProperty("tables_changed", out var tables))
                    {
                        info.CollectionsAffected = tables.GetInt32();
                    }
                    if (jsonDoc.RootElement.TryGetProperty("rows_added", out var added))
                    {
                        info.DocumentsAdded = added.GetInt32();
                    }
                    if (jsonDoc.RootElement.TryGetProperty("rows_modified", out var modified))
                    {
                        info.DocumentsModified = modified.GetInt32();
                    }
                    if (jsonDoc.RootElement.TryGetProperty("rows_deleted", out var deleted))
                    {
                        info.DocumentsDeleted = deleted.GetInt32();
                    }
                    return info;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Failed to parse as JSON, trying text format: {Error}", ex.Message);
                }
            }
            
            // Parse text format (e.g., "3 tables changed, 10 rows added, 5 rows modified, 2 rows deleted")
            var lines = diffOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Contains("tables changed") || line.Contains("table changed"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s+tables?\s+changed");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var tables))
                    {
                        info.CollectionsAffected = tables;
                    }
                }
                
                if (line.Contains("rows"))
                {
                    var addMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s+rows?\s+added");
                    if (addMatch.Success && int.TryParse(addMatch.Groups[1].Value, out var added))
                    {
                        info.DocumentsAdded = added;
                    }
                    
                    var modMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s+rows?\s+modified");
                    if (modMatch.Success && int.TryParse(modMatch.Groups[1].Value, out var modified))
                    {
                        info.DocumentsModified = modified;
                    }
                    
                    var delMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s+rows?\s+deleted");
                    if (delMatch.Success && int.TryParse(delMatch.Groups[1].Value, out var deleted))
                    {
                        info.DocumentsDeleted = deleted;
                    }
                }
            }
            
            return info;
        }

        /// <summary>
        /// Get document content at a specific commit
        /// </summary>
        private async Task<DocumentContent> GetDocumentAtCommit(string tableName, string documentId, string commitHash)
        {
            var content = new DocumentContent
            {
                CommitHash = commitHash,
                Exists = false
            };
            
            try
            {
                // Query for document at specific commit using correct schema
                var sql = $"SELECT * FROM `{tableName}` AS OF '{commitHash}' WHERE doc_id = '{documentId}'";
                var result = await _doltCli.QueryJsonAsync(sql);
                
                if (!string.IsNullOrWhiteSpace(result))
                {
                    var jsonDoc = JsonDocument.Parse(result);
                    if (jsonDoc.RootElement.TryGetProperty("rows", out var rows))
                    {
                        var rowArray = rows.EnumerateArray().ToList();
                        if (rowArray.Any())
                        {
                            content.Exists = true;
                            var row = rowArray.First();
                            
                            // Extract content from various possible field names
                            if (row.TryGetProperty("content", out var contentProp) ||
                                row.TryGetProperty("document_text", out contentProp) ||
                                row.TryGetProperty("document_content", out contentProp))
                            {
                                content.Content = contentProp.GetString();
                            }
                            
                            // Extract all other fields as metadata
                            foreach (var prop in row.EnumerateObject())
                            {
                                if (prop.Name != "content" && prop.Name != "document_text" && 
                                    prop.Name != "document_content" && prop.Name != "doc_id")
                                {
                                    content.Metadata[prop.Name] = JsonElementToObject(prop.Value);
                                }
                            }
                            
                            // Try to extract last modified timestamp
                            if (row.TryGetProperty("updated_at", out var updatedProp) ||
                                row.TryGetProperty("modified_at", out updatedProp) ||
                                row.TryGetProperty("timestamp", out updatedProp))
                            {
                                if (DateTime.TryParse(updatedProp.GetString(), out var timestamp))
                                {
                                    content.LastModified = timestamp;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not get document {DocId} from {Table} at {Commit}: {Error}",
                    documentId, tableName, commitHash, ex.Message);
            }
            
            return content;
        }

        /// <summary>
        /// Get document content at a specific branch
        /// </summary>
        private async Task<DocumentContent> GetDocumentAtBranch(string tableName, string documentId, string branchName)
        {
            // First checkout the branch temporarily to query it
            var currentBranch = await _doltCli.GetCurrentBranchAsync();
            var needsCheckout = currentBranch != branchName;
            
            try
            {
                if (needsCheckout)
                {
                    await _doltCli.CheckoutAsync(branchName);
                }
                
                // Get the HEAD commit of this branch
                var commitHash = await _doltCli.GetHeadCommitHashAsync();
                
                // Query for the document
                var content = await GetDocumentAtCommit(tableName, documentId, commitHash);
                content.CommitHash = commitHash;
                
                return content;
            }
            finally
            {
                // Switch back to original branch
                if (needsCheckout && !string.IsNullOrEmpty(currentBranch))
                {
                    await _doltCli.CheckoutAsync(currentBranch);
                }
            }
        }

        /// <summary>
        /// Check if two documents are equal
        /// </summary>
        private bool AreDocumentsEqual(DocumentContent doc1, DocumentContent doc2)
        {
            if (doc1.Content != doc2.Content)
            {
                _logger.LogDebug("Documents differ in content: '{Content1}' vs '{Content2}'", doc1.Content, doc2.Content);
                return false;
            }
            
            // Check metadata equality
            if (doc1.Metadata.Count != doc2.Metadata.Count)
            {
                _logger.LogDebug("Documents differ in metadata count: {Count1} vs {Count2}", doc1.Metadata.Count, doc2.Metadata.Count);
                return false;
            }
            
            foreach (var kvp in doc1.Metadata)
            {
                if (!doc2.Metadata.TryGetValue(kvp.Key, out var value2))
                {
                    _logger.LogDebug("Documents differ in metadata field '{Key}': '{Value1}' vs missing", 
                        kvp.Key, kvp.Value);
                    return false;
                }
                
                // Handle null and string comparisons more carefully
                var val1Str = kvp.Value?.ToString() ?? "";
                var val2Str = value2?.ToString() ?? "";
                
                if (val1Str != val2Str)
                {
                    _logger.LogDebug("Documents differ in metadata field '{Key}': '{Value1}' vs '{Value2}' (types: {Type1} vs {Type2})", 
                        kvp.Key, val1Str, val2Str, kvp.Value?.GetType().Name ?? "null", value2?.GetType().Name ?? "null");
                    return false;
                }
            }
            
            _logger.LogDebug("Documents are equal: content and metadata match");
            return true;
        }

        /// <summary>
        /// Get list of conflicting fields between two documents
        /// </summary>
        private List<string> GetConflictingFields(DocumentContent doc1, DocumentContent doc2)
        {
            var conflictingFields = new List<string>();
            
            if (doc1.Content != doc2.Content)
            {
                conflictingFields.Add("content");
            }
            
            // Check all metadata fields
            var allKeys = doc1.Metadata.Keys.Union(doc2.Metadata.Keys).Distinct();
            
            foreach (var key in allKeys)
            {
                var value1 = doc1.Metadata.GetValueOrDefault(key);
                var value2 = doc2.Metadata.GetValueOrDefault(key);
                
                if (!Equals(value1, value2))
                {
                    conflictingFields.Add(key);
                }
            }
            
            return conflictingFields;
        }

        /// <summary>
        /// Determine suggested resolution based on content comparison
        /// </summary>
        private string DetermineSuggestedResolution(ContentComparison comparison)
        {
            if (!comparison.HasConflicts)
                return "no_conflict";
            
            // If only metadata conflicts, suggest auto-merge
            if (comparison.ConflictingFields.All(f => f != "content"))
            {
                return "auto_merge_metadata";
            }
            
            // If content is identical in source and target but different from base
            if (comparison.SourceContent?.Content == comparison.TargetContent?.Content &&
                comparison.SourceContent?.Content != comparison.BaseContent?.Content)
            {
                return "identical_changes";
            }
            
            // If one side didn't change from base
            if (comparison.SourceContent?.Content == comparison.BaseContent?.Content)
            {
                return "use_target_changes";
            }
            if (comparison.TargetContent?.Content == comparison.BaseContent?.Content)
            {
                return "use_source_changes";
            }
            
            // Both sides changed differently
            return "manual_merge_required";
        }

        /// <summary>
        /// Analyze changes in a specific table
        /// </summary>
        private async Task AnalyzeTableChanges(string tableName, string fromCommit, string toCommit, MergePreviewInfo info)
        {
            try
            {
                // Query the diff for this specific table
                var sql = $"SELECT diff_type, COUNT(*) as cnt FROM DOLT_DIFF('{fromCommit}', '{toCommit}', '{tableName}') GROUP BY diff_type";
                var result = await _doltCli.QueryJsonAsync(sql);
                
                if (!string.IsNullOrWhiteSpace(result))
                {
                    var jsonDoc = JsonDocument.Parse(result);
                    if (jsonDoc.RootElement.TryGetProperty("rows", out var rows))
                    {
                        foreach (var row in rows.EnumerateArray())
                        {
                            if (row.TryGetProperty("diff_type", out var diffType) &&
                                row.TryGetProperty("cnt", out var count))
                            {
                                var type = diffType.GetString();
                                var cnt = count.GetInt32();
                                
                                switch (type?.ToLower())
                                {
                                    case "added":
                                    case "insert":
                                        info.DocumentsAdded += cnt;
                                        break;
                                    case "modified":
                                    case "update":
                                        info.DocumentsModified += cnt;
                                        break;
                                    case "deleted":
                                    case "delete":
                                    case "removed":
                                        info.DocumentsDeleted += cnt;
                                        break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not analyze table {Table} changes: {Error}", tableName, ex.Message);
            }
        }

        #endregion
    }
}