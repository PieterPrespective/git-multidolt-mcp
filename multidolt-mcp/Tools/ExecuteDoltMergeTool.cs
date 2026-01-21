using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using DMMS.Services;
using DMMS.Models;
using DMMS.Utilities;

namespace DMMS.Tools
{
    /// <summary>
    /// MCP tool for executing merge operations with conflict resolution
    /// Supports automatic resolution, field-level merging, and custom conflict resolution
    /// </summary>
    [McpServerToolType]
    public class ExecuteDoltMergeTool
    {
        private readonly ILogger<ExecuteDoltMergeTool> _logger;
        private readonly IDoltCli _doltCli;
        private readonly IMergeConflictResolver _conflictResolver;
        private readonly ISyncManagerV2 _syncManager;
        private readonly IConflictAnalyzer _conflictAnalyzer;
        private readonly IDmmsStateManifest _manifestService;
        private readonly ISyncStateChecker _syncStateChecker;

        /// <summary>
        /// Initializes a new instance of the ExecuteDoltMergeTool class
        /// </summary>
        /// <param name="logger">Logger for diagnostic information</param>
        /// <param name="doltCli">Dolt CLI service for repository operations</param>
        /// <param name="conflictResolver">Service for resolving merge conflicts</param>
        /// <param name="syncManager">Sync manager for merge processing and ChromaDB sync</param>
        /// <param name="conflictAnalyzer">Service for analyzing conflicts during merge</param>
        /// <param name="manifestService">Manifest service for state tracking</param>
        /// <param name="syncStateChecker">Sync state checker service</param>
        public ExecuteDoltMergeTool(
            ILogger<ExecuteDoltMergeTool> logger,
            IDoltCli doltCli,
            IMergeConflictResolver conflictResolver,
            ISyncManagerV2 syncManager,
            IConflictAnalyzer conflictAnalyzer,
            IDmmsStateManifest manifestService,
            ISyncStateChecker syncStateChecker)
        {
            _logger = logger;
            _doltCli = doltCli;
            _conflictResolver = conflictResolver;
            _syncManager = syncManager;
            _conflictAnalyzer = conflictAnalyzer;
            _manifestService = manifestService;
            _syncStateChecker = syncStateChecker;
        }

        /// <summary>
        /// Execute a merge operation with specified conflict resolutions. Use preview_dolt_merge first to identify conflicts and their IDs.
        /// </summary>
        [McpServerTool]
        [Description("Execute a merge operation with specified conflict resolutions. Use preview_dolt_merge first to identify conflicts and their IDs.")]
        public async Task<object> ExecuteDoltMerge(
            string source_branch,
            string? target_branch = null,
            string? conflict_resolutions = null,
            bool auto_resolve_remaining = true,
            bool force_merge = false,
            string? merge_message = null)
        {
            const string toolName = nameof(ExecuteDoltMergeTool);
            const string methodName = nameof(ExecuteDoltMerge);

            try
            {
                ToolLoggingUtility.LogToolStart(_logger, toolName, methodName,
                    $"source: {source_branch}, target: {target_branch}, force: {force_merge}");

                // Validate required parameters
                if (string.IsNullOrWhiteSpace(source_branch))
                {
                    const string error = "source_branch parameter is required";
                    ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                    return new
                    {
                        success = false,
                        error = "INVALID_PARAMETERS",
                        message = error
                    };
                }

                // Validate Dolt availability
                var doltCheck = await _doltCli.CheckDoltAvailableAsync();
                if (!doltCheck.Success)
                {
                    const string error = "DOLT_NOT_AVAILABLE";
                    ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {doltCheck.Error}");
                    return new
                    {
                        success = false,
                        error = error,
                        message = doltCheck.Error
                    };
                }

                // Check if repository is initialized
                var isInitialized = await _doltCli.IsInitializedAsync();
                if (!isInitialized)
                {
                    const string error = "NOT_INITIALIZED";
                    const string errorMessage = "No Dolt repository configured. Use dolt_init or dolt_clone first.";
                    ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {errorMessage}");
                    return new
                    {
                        success = false,
                        error = error,
                        message = errorMessage
                    };
                }

                // Get current branch if target not specified
                if (string.IsNullOrEmpty(target_branch))
                {
                    target_branch = await _doltCli.GetCurrentBranchAsync();
                    ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Using current branch as target: {target_branch}");
                }

                // PP13-73-C2: Check for existing merge state from a previous failed merge
                var mergeInProgress = await _doltCli.IsMergeInProgressAsync();
                if (mergeInProgress)
                {
                    ToolLoggingUtility.LogToolInfo(_logger, toolName, "Detected existing merge in progress - aborting previous merge first");
                    var abortResult = await _doltCli.MergeAbortAsync();
                    if (!abortResult.Success)
                    {
                        const string error = "MERGE_CLEANUP_FAILED";
                        var errorMessage = $"A previous merge is in progress and could not be aborted: {abortResult.Error}. " +
                                           "Please manually resolve or abort the existing merge before starting a new one.";
                        ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {errorMessage}");
                        return new
                        {
                            success = false,
                            error = error,
                            message = errorMessage
                        };
                    }
                    ToolLoggingUtility.LogToolInfo(_logger, toolName, "Previous merge aborted successfully, proceeding with new merge");
                }

                // Parse conflict resolution preferences
                // Supports two formats:
                // 1. Structured: {"Resolutions": [{"ConflictId": "conf_xxx", "ResolutionType": "KeepOurs"}], "DefaultStrategy": "ours"}
                // 2. Simple dictionary: {"conflict_id": "keep_ours", "conflict_id2": "keep_theirs"}
                List<ConflictResolutionRequest>? resolutions = null;
                string? defaultStrategy = "ours";

                if (!string.IsNullOrEmpty(conflict_resolutions))
                {
                    var (parsedResolutions, parsedDefault, parseError) = ParseConflictResolutions(conflict_resolutions);

                    if (parseError != null)
                    {
                        // Completely invalid JSON - return error
                        ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName,
                            $"INVALID_RESOLUTION_JSON: {parseError}");
                        return new
                        {
                            success = false,
                            error = "INVALID_RESOLUTION_JSON",
                            message = $"Failed to parse conflict resolutions: {parseError}"
                        };
                    }

                    resolutions = parsedResolutions;
                    defaultStrategy = parsedDefault ?? "ours";

                    if (resolutions != null && resolutions.Any())
                    {
                        ToolLoggingUtility.LogToolInfo(_logger, toolName,
                            $"Parsed {resolutions.Count} specific resolutions with default strategy: {defaultStrategy}");
                    }
                    else
                    {
                        // Valid JSON but no resolutions found (e.g., empty object)
                        ToolLoggingUtility.LogToolInfo(_logger, toolName,
                            "No conflict resolutions specified - will use auto-resolution only");
                    }
                }

                // Get state before merge
                var beforeCommit = await _doltCli.GetHeadCommitHashAsync();
                
                // Execute the merge using sync manager
                ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Starting merge from {source_branch} to {target_branch}");
                var mergeResult = await _syncManager.ProcessMergeAsync(source_branch, force_merge);

                // Track resolution statistics
                int resolvedCount = 0;
                int autoResolved = 0;
                int manuallyResolved = 0;

                // PP13-73: Auto-resolve auxiliary table conflicts before handling document conflicts
                // These tables contain system/sync data that can be safely auto-resolved with --ours
                var auxiliaryTables = new[] { "chroma_sync_state", "document_sync_log", "local_changes" };
                var auxiliaryTablesResolved = new List<string>();

                foreach (var auxTable in auxiliaryTables)
                {
                    if (await _doltCli.HasConflictsInTableAsync(auxTable))
                    {
                        ToolLoggingUtility.LogToolInfo(_logger, toolName,
                            $"Auto-resolving conflicts in auxiliary table '{auxTable}' with --ours");

                        var resolveResult = await _doltCli.ResolveConflictsAsync(auxTable, ConflictResolution.Ours);
                        if (resolveResult.Success)
                        {
                            auxiliaryTablesResolved.Add(auxTable);
                            _logger.LogInformation("Successfully auto-resolved auxiliary table {Table} with --ours", auxTable);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to auto-resolve auxiliary table {Table}: {Error}", auxTable, resolveResult.Error);
                        }
                    }
                }

                // PP13-73: Handle collections table specially - it contains user data with FK relationships
                // Auto-resolve with --ours to preserve current branch's collections (safer than --theirs which could cascade delete documents)
                if (await _doltCli.HasConflictsInTableAsync("collections"))
                {
                    ToolLoggingUtility.LogToolInfo(_logger, toolName,
                        "Auto-resolving conflicts in 'collections' table with --ours (preserving current branch collections)");

                    var collectionsResolveResult = await _doltCli.ResolveConflictsAsync("collections", ConflictResolution.Ours);
                    if (collectionsResolveResult.Success)
                    {
                        auxiliaryTablesResolved.Add("collections");
                        _logger.LogInformation("Successfully auto-resolved collections table with --ours");
                    }
                    else
                    {
                        _logger.LogWarning("Failed to auto-resolve collections table: {Error}", collectionsResolveResult.Error);
                    }
                }

                if (auxiliaryTablesResolved.Any())
                {
                    ToolLoggingUtility.LogToolInfo(_logger, toolName,
                        $"Auto-resolved {auxiliaryTablesResolved.Count} auxiliary table(s): {string.Join(", ", auxiliaryTablesResolved)}");
                }

                // Handle conflicts if they exist
                if (mergeResult.HasConflicts && mergeResult.Conflicts.Any())
                {
                    ToolLoggingUtility.LogToolInfo(_logger, toolName,
                        $"Processing {mergeResult.Conflicts.Count} merge conflicts");

                    // Get detailed conflicts for resolution
                    var detailedConflicts = await _conflictAnalyzer.GetDetailedConflictsAsync("documents");

                    // PP13-73-C3: Apply user-specified resolutions using batch resolution
                    // Dolt requires ALL conflicts to be resolved before COMMIT is allowed,
                    // so we collect all resolutions and execute them in a single transaction.
                    if (resolutions != null && resolutions.Any())
                    {
                        // Collect all (conflict, resolution) pairs for batch resolution
                        var batchResolutions = new List<(DetailedConflictInfo Conflict, ConflictResolutionRequest Resolution)>();

                        foreach (var resolution in resolutions)
                        {
                            var conflict = detailedConflicts.FirstOrDefault(
                                c => c.ConflictId == resolution.ConflictId);

                            if (conflict != null)
                            {
                                batchResolutions.Add((conflict, resolution));
                                ToolLoggingUtility.LogToolInfo(_logger, toolName,
                                    $"Queuing user resolution for conflict {resolution.ConflictId}: {resolution.ResolutionType}");
                            }
                            else
                            {
                                _logger.LogWarning("Conflict ID {ConflictId} not found in merge conflicts", resolution.ConflictId);
                            }
                        }

                        // Execute all resolutions in a single transaction
                        if (batchResolutions.Any())
                        {
                            ToolLoggingUtility.LogToolInfo(_logger, toolName,
                                $"PP13-73-C3: Executing batch resolution for {batchResolutions.Count} conflicts");

                            var batchResult = await _conflictResolver.ResolveBatchAsync(batchResolutions);

                            // Process batch results
                            resolvedCount += batchResult.SuccessfullyResolved;
                            manuallyResolved += batchResult.SuccessfullyResolved;

                            if (!batchResult.Success)
                            {
                                _logger.LogWarning("PP13-73-C3: Batch resolution had failures: {Error}",
                                    batchResult.ErrorMessage ?? "Unknown error");

                                // Log individual failures
                                foreach (var outcome in batchResult.ResolutionOutcomes.Where(o => !o.Success))
                                {
                                    _logger.LogWarning("Failed to resolve conflict {ConflictId}: {Error}",
                                        outcome.ConflictId, outcome.ErrorMessage);
                                }
                            }
                            else
                            {
                                ToolLoggingUtility.LogToolInfo(_logger, toolName,
                                    $"PP13-73-C3: Successfully resolved {batchResult.SuccessfullyResolved} conflicts in batch");
                            }
                        }
                    }

                    // Auto-resolve remaining conflicts if requested
                    if (auto_resolve_remaining)
                    {
                        var unresolvedConflicts = detailedConflicts
                            .Where(c => resolutions == null || 
                                       !resolutions.Any(r => r.ConflictId == c.ConflictId))
                            .ToList();
                        
                        if (unresolvedConflicts.Any())
                        {
                            ToolLoggingUtility.LogToolInfo(_logger, toolName, 
                                $"Auto-resolving {unresolvedConflicts.Count} remaining conflicts");
                            
                            autoResolved = await _conflictResolver.AutoResolveConflictsAsync(unresolvedConflicts);
                            resolvedCount += autoResolved;
                        }
                    }

                    // Check if all conflicts are resolved
                    var remainingConflicts = await _doltCli.HasConflictsAsync();
                    if (remainingConflicts)
                    {
                        var unresolved = mergeResult.Conflicts.Count - resolvedCount;

                        // PP13-73-C2: Abort the merge to clean up state when resolution fails
                        ToolLoggingUtility.LogToolInfo(_logger, toolName, "Conflict resolution incomplete - aborting merge to clean up state");
                        var abortResult = await _doltCli.MergeAbortAsync();
                        if (!abortResult.Success)
                        {
                            _logger.LogWarning("Failed to abort merge after incomplete resolution: {Error}", abortResult.Error);
                        }

                        const string error = "UNRESOLVED_CONFLICTS";
                        var errorMessage = $"Not all conflicts could be resolved: {unresolved} conflicts remain. Merge has been aborted.";
                        ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {errorMessage}");

                        return new
                        {
                            success = false,
                            error = error,
                            message = errorMessage,
                            conflicts_total = mergeResult.Conflicts.Count,
                            conflicts_resolved = resolvedCount,
                            conflicts_remaining = unresolved,
                            merge_aborted = abortResult.Success,
                            resolution_breakdown = new
                            {
                                manually_resolved = manuallyResolved,
                                auto_resolved = autoResolved
                            }
                        };
                    }

                    // Complete the merge with a commit
                    var commitMessage = merge_message ??
                        $"Merge {source_branch} into {target_branch ?? "current"} with {resolvedCount} conflicts resolved";

                    ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Committing merge: {commitMessage}");
                    var commitResult = await _doltCli.CommitAsync(commitMessage);

                    if (!commitResult.Success)
                    {
                        // PP13-73-C2: Abort the merge when commit fails
                        ToolLoggingUtility.LogToolInfo(_logger, toolName, "Merge commit failed - aborting merge to clean up state");
                        var abortResult = await _doltCli.MergeAbortAsync();
                        if (!abortResult.Success)
                        {
                            _logger.LogWarning("Failed to abort merge after commit failure: {Error}", abortResult.Error);
                        }

                        const string error = "MERGE_COMMIT_FAILED";
                        var errorMessage = $"Failed to commit merge: {commitResult.Message}. Merge has been aborted.";
                        ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {errorMessage}");
                        return new
                        {
                            success = false,
                            error = error,
                            message = errorMessage,
                            merge_aborted = abortResult.Success
                        };
                    }

                    // PP13-73-C4: Sync ChromaDB after successful conflict resolution and commit
                    // This ensures ChromaDB reflects the resolved merge state (especially for keep_theirs resolutions)
                    ToolLoggingUtility.LogToolInfo(_logger, toolName, "PP13-73-C4: Syncing ChromaDB after conflict resolution");
                    var postResolutionSync = await _syncManager.FullSyncAsync(forceSync: true);

                    if (postResolutionSync.Success)
                    {
                        // Update mergeResult with the new sync statistics
                        mergeResult.Added = postResolutionSync.Added;
                        mergeResult.Modified = postResolutionSync.Modified;
                        mergeResult.Deleted = postResolutionSync.Deleted;
                        mergeResult.ChunksProcessed = postResolutionSync.ChunksProcessed;

                        ToolLoggingUtility.LogToolInfo(_logger, toolName,
                            $"PP13-73-C4: ChromaDB sync completed - Added: {postResolutionSync.Added}, Modified: {postResolutionSync.Modified}, Deleted: {postResolutionSync.Deleted}");
                    }
                    else
                    {
                        _logger.LogWarning("PP13-73-C4: ChromaDB sync after conflict resolution failed: {Error}", postResolutionSync.ErrorMessage);
                    }
                }

                // Get final state after merge
                var afterCommit = await _doltCli.GetHeadCommitHashAsync();
                var mergeCommitHash = mergeResult.MergeCommitHash ?? afterCommit;

                // PP13-79-C1: Update manifest after successful merge (NOT during conflicts)
                await UpdateManifestAfterMergeAsync(mergeCommitHash, target_branch);

                // Build success response
                var response = new
                {
                    success = true,
                    merge_result = new
                    {
                        merge_commit = mergeCommitHash ?? "",
                        source_branch = source_branch,
                        target_branch = target_branch,
                        conflicts_total = mergeResult.Conflicts?.Count ?? 0,
                        conflicts_resolved = resolvedCount,
                        auto_resolved = autoResolved,
                        manually_resolved = manuallyResolved,
                        merge_timestamp = DateTime.UtcNow.ToString("O"),
                        before_commit = beforeCommit ?? "",
                        after_commit = afterCommit ?? ""
                    },
                    sync_result = new
                    {
                        collections_synced = mergeResult.CollectionsSynced ?? 0,
                        documents_added = mergeResult.Added,
                        documents_modified = mergeResult.Modified,
                        documents_deleted = mergeResult.Deleted,
                        chunks_processed = mergeResult.ChunksProcessed
                    },
                    auxiliary_tables_updated = new
                    {
                        sync_state = true,
                        local_changes = true,
                        sync_operations = true
                    },
                    message = GenerateSuccessMessage(mergeResult, resolvedCount, source_branch, target_branch)
                };

                ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName,
                    $"Merge completed: {resolvedCount} conflicts resolved, {mergeResult.Added + mergeResult.Modified + mergeResult.Deleted} documents synced");

                return response;
            }
            catch (Exception ex)
            {
                ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
                return new
                {
                    success = false,
                    error = "OPERATION_FAILED",
                    message = $"Failed to execute merge: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Generate an appropriate success message based on merge results
        /// </summary>
        private string GenerateSuccessMessage(Services.MergeSyncResultV2 mergeResult, int resolvedCount, string sourceBranch, string targetBranch)
        {
            if (!mergeResult.HasConflicts || !mergeResult.Conflicts.Any())
            {
                return $"Successfully merged {sourceBranch} into {targetBranch} with no conflicts";
            }

            var totalChanges = mergeResult.Added + mergeResult.Modified + mergeResult.Deleted;
            return $"Successfully merged {sourceBranch} into {targetBranch} with {resolvedCount} conflicts resolved and {totalChanges} documents synchronized";
        }

        /// <summary>
        /// Parse conflict resolutions from JSON string. Supports two formats:
        /// 1. Structured format: {"Resolutions": [{"ConflictId": "conf_xxx", "ResolutionType": "KeepOurs"}], "DefaultStrategy": "ours"}
        /// 2. Simple dictionary format: {"conflict_id": "keep_ours", "conflict_id2": "keep_theirs"}
        /// </summary>
        /// <param name="jsonString">JSON string containing conflict resolutions</param>
        /// <returns>Tuple of (resolutions, defaultStrategy, errorMessage). errorMessage is null on success.</returns>
        private (List<ConflictResolutionRequest>? Resolutions, string? DefaultStrategy, string? Error) ParseConflictResolutions(string jsonString)
        {
            string defaultStrategy = "ours";
            string? lastError = null;

            try
            {
                // First try the structured format
                var resolutionData = JsonSerializer.Deserialize<ConflictResolutionData>(
                    jsonString,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (resolutionData?.Resolutions != null && resolutionData.Resolutions.Any())
                {
                    defaultStrategy = resolutionData.DefaultStrategy ?? "ours";
                    return (resolutionData.Resolutions, defaultStrategy, null);
                }

                // Structured format parsed but had no resolutions - valid JSON, just empty
                // Continue to try simple dictionary format
            }
            catch (JsonException ex)
            {
                // Structured format failed, capture error and try simple dictionary format
                lastError = ex.Message;
            }

            try
            {
                // Try simple dictionary format: {"conflict_id": "keep_ours", "conflict_id2": "keep_theirs"}
                var simpleDictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    jsonString,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (simpleDictionary != null)
                {
                    var resolutions = new List<ConflictResolutionRequest>();

                    foreach (var kvp in simpleDictionary)
                    {
                        // Skip "defaultStrategy" or "default_strategy" keys - use them to set the default
                        var keyLower = kvp.Key.ToLowerInvariant();
                        if (keyLower == "defaultstrategy" || keyLower == "default_strategy")
                        {
                            defaultStrategy = kvp.Value;
                            continue;
                        }

                        // Parse the resolution type from the string value
                        var resolutionType = ParseResolutionTypeFromString(kvp.Value);
                        if (resolutionType.HasValue)
                        {
                            resolutions.Add(new ConflictResolutionRequest
                            {
                                ConflictId = kvp.Key,
                                ResolutionType = resolutionType.Value
                            });
                        }
                        else
                        {
                            _logger.LogWarning("Unknown resolution type '{ResolutionType}' for conflict {ConflictId}",
                                kvp.Value, kvp.Key);
                        }
                    }

                    if (resolutions.Any())
                    {
                        _logger.LogDebug("Parsed {Count} resolutions from simple dictionary format", resolutions.Count);
                        return (resolutions, defaultStrategy, null);
                    }

                    // Valid JSON dictionary but no resolutions parsed - return success with empty list
                    return (resolutions, defaultStrategy, null);
                }
            }
            catch (JsonException ex)
            {
                // Both formats failed - return the error
                _logger.LogWarning("Failed to parse conflict resolutions: {Error}", ex.Message);
                return (null, null, ex.Message);
            }

            // If we got here, both formats failed but no exception was thrown
            // This happens when the JSON is malformed in a way that doesn't throw
            return (null, null, lastError ?? "Unable to parse conflict resolutions");
        }

        /// <summary>
        /// Parse resolution type from various string formats.
        /// Supports: keep_ours, keepours, ours, keep_theirs, keeptheirs, theirs, auto, autoresolve, etc.
        /// </summary>
        private ResolutionType? ParseResolutionTypeFromString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var normalized = value.ToLowerInvariant().Replace("_", "").Replace("-", "").Trim();

            return normalized switch
            {
                "keepours" or "ours" => ResolutionType.KeepOurs,
                "keeptheirs" or "theirs" => ResolutionType.KeepTheirs,
                "fieldmerge" or "merge" => ResolutionType.FieldMerge,
                "custom" => ResolutionType.Custom,
                "auto" or "autoresolve" or "automatic" => ResolutionType.AutoResolve,
                _ => null
            };
        }

        /// <summary>
        /// PP13-79-C1: Updates the manifest after a successful merge
        /// Only called after merge completes successfully (not during conflicts)
        /// </summary>
        private async Task UpdateManifestAfterMergeAsync(string? commitHash, string? branch)
        {
            if (string.IsNullOrEmpty(commitHash))
            {
                return;
            }

            try
            {
                ToolLoggingUtility.LogToolInfo(_logger, nameof(ExecuteDoltMergeTool),
                    $"PP13-79-C1: Updating manifest after successful merge...");

                var projectRoot = await _syncStateChecker.GetProjectRootAsync();
                if (string.IsNullOrEmpty(projectRoot))
                {
                    return;
                }

                var manifestExists = await _manifestService.ManifestExistsAsync(projectRoot);
                if (!manifestExists)
                {
                    return;
                }

                await _manifestService.UpdateDoltCommitAsync(projectRoot, commitHash, branch ?? "main");
                _syncStateChecker.InvalidateCache();

                ToolLoggingUtility.LogToolInfo(_logger, nameof(ExecuteDoltMergeTool),
                    $"✅ PP13-79-C1: Manifest updated with merge commit {commitHash.Substring(0, 7)}");
            }
            catch (Exception ex)
            {
                ToolLoggingUtility.LogToolWarning(_logger, nameof(ExecuteDoltMergeTool),
                    $"⚠️ PP13-79-C1: Failed to update manifest: {ex.Message}");
            }
        }
    }
}