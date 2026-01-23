using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Embranch.Services;
using Embranch.Models;
using Embranch.Utilities;

namespace Embranch.Tools
{
    /// <summary>
    /// MCP tool for executing import operations from external ChromaDB databases.
    /// Supports automatic resolution and custom conflict handling.
    /// Uses IChromaDbService.AddDocumentsAsync for proper chunking and batch operations.
    /// Supports transparent migration of legacy ChromaDB databases.
    /// </summary>
    [McpServerToolType]
    public class ExecuteImportTool
    {
        private readonly ILogger<ExecuteImportTool> _logger;
        private readonly IImportExecutor _importExecutor;
        private readonly IImportAnalyzer _importAnalyzer;
        private readonly IExternalChromaDbReader _externalDbReader;
        private readonly ISyncManagerV2 _syncManager;
        private readonly ILegacyDbMigrator _legacyMigrator;

        /// <summary>
        /// Initializes a new instance of the ExecuteImportTool class
        /// </summary>
        /// <param name="logger">Logger for diagnostic information</param>
        /// <param name="importExecutor">Service for executing imports with conflict resolution</param>
        /// <param name="importAnalyzer">Service for analyzing imports and detecting conflicts</param>
        /// <param name="externalDbReader">Service for reading external ChromaDB databases</param>
        /// <param name="syncManager">Sync manager for staging to Dolt</param>
        /// <param name="legacyMigrator">Service for handling legacy database migration</param>
        public ExecuteImportTool(
            ILogger<ExecuteImportTool> logger,
            IImportExecutor importExecutor,
            IImportAnalyzer importAnalyzer,
            IExternalChromaDbReader externalDbReader,
            ISyncManagerV2 syncManager,
            ILegacyDbMigrator legacyMigrator)
        {
            _logger = logger;
            _importExecutor = importExecutor;
            _importAnalyzer = importAnalyzer;
            _externalDbReader = externalDbReader;
            _syncManager = syncManager;
            _legacyMigrator = legacyMigrator;
        }

        /// <summary>
        /// Execute an import operation with specified conflict resolutions.
        /// Use preview_import first to identify conflicts and their IDs.
        /// </summary>
        /// <param name="filepath">Path to the external ChromaDB database folder</param>
        /// <param name="filter">JSON filter specifying what to import (empty = import all)</param>
        /// <param name="conflict_resolutions">JSON specifying how to resolve conflicts</param>
        /// <param name="auto_resolve_remaining">Auto-resolve conflicts not explicitly specified</param>
        /// <param name="default_strategy">Default strategy for auto-resolution (keep_source, keep_target, skip)</param>
        /// <param name="stage_to_dolt">Whether to stage imported documents to Dolt</param>
        /// <param name="commit_message">Optional commit message if staging to Dolt</param>
        [McpServerTool]
        [Description(@"Execute an import operation from an external ChromaDB database. Call preview_import first to see conflicts and get conflict IDs.

PARAMETERS:
- filepath, filter: Same as preview_import (see that tool for filter format with wildcards, collection mapping, and document filtering)
- conflict_resolutions (optional): JSON specifying how to resolve specific conflicts (see format below)
- auto_resolve_remaining (default: true): Auto-resolve unspecified conflicts using default_strategy
- default_strategy (default: 'keep_source'): Resolution for auto-resolved conflicts:
  * 'keep_source' - Use external database version (overwrites local)
  * 'keep_target' - Keep local version (skip this document)
  * 'skip' - Skip the document entirely
- stage_to_dolt (default: true): Commit imported documents to Dolt after import
- commit_message (optional): Custom commit message (auto-generated if omitted)

CONFLICT RESOLUTION FORMAT (use conflict IDs from preview_import):
1. Structured: {""resolutions"": [{""conflict_id"": ""imp_abc123"", ""resolution_type"": ""keep_source""}], ""default_strategy"": ""skip""}
2. Simple: {""imp_abc123"": ""keep_source"", ""imp_def456"": ""keep_target""}

WORKFLOW: preview_import -> review conflicts -> execute_import with resolutions")]
        public async Task<object> ExecuteImport(
            string filepath,
            string? filter = null,
            string? conflict_resolutions = null,
            bool auto_resolve_remaining = true,
            string default_strategy = "keep_source",
            bool stage_to_dolt = true,
            string? commit_message = null)
        {
            const string toolName = nameof(ExecuteImportTool);
            const string methodName = nameof(ExecuteImport);

            try
            {
                ToolLoggingUtility.LogToolStart(_logger, toolName, methodName,
                    $"filepath: {filepath}, auto_resolve: {auto_resolve_remaining}, default_strategy: {default_strategy}");

                // Validate required parameters
                if (string.IsNullOrWhiteSpace(filepath))
                {
                    const string error = "filepath parameter is required";
                    ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                    return new
                    {
                        success = false,
                        error = "INVALID_PARAMETERS",
                        message = error
                    };
                }

                // Create legacy-aware context for this import operation
                await using var legacyContext = await LegacyDbImportContext.CreateAsync(
                    _legacyMigrator, filepath, _logger);

                if (legacyContext.WasMigrated)
                {
                    ToolLoggingUtility.LogToolInfo(_logger, toolName,
                        $"Legacy database detected and migrated for compatibility. Using migrated copy.");
                }

                // Use effective path (migrated copy if needed, original otherwise)
                var effectivePath = legacyContext.EffectivePath;

                // Validate external database
                var validation = await _externalDbReader.ValidateExternalDbAsync(effectivePath);
                if (!validation.IsValid)
                {
                    var error = "INVALID_EXTERNAL_DATABASE";
                    ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {validation.ErrorMessage}");
                    return new
                    {
                        success = false,
                        error = error,
                        message = validation.ErrorMessage,
                        source_path = filepath,
                        migration_info = legacyContext.MigrationInfo
                    };
                }

                ToolLoggingUtility.LogToolInfo(_logger, toolName,
                    $"External database validated: {validation.CollectionCount} collections, {validation.TotalDocuments} documents");

                // Parse filter if provided
                ImportFilter? importFilter = null;
                if (!string.IsNullOrEmpty(filter))
                {
                    try
                    {
                        importFilter = JsonSerializer.Deserialize<ImportFilter>(filter, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        ToolLoggingUtility.LogToolInfo(_logger, toolName, "Filter parsed successfully");
                    }
                    catch (JsonException ex)
                    {
                        const string error = "INVALID_FILTER_JSON";
                        ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {ex.Message}");
                        return new
                        {
                            success = false,
                            error = error,
                            message = $"Failed to parse filter JSON: {ex.Message}"
                        };
                    }
                }

                // Parse conflict resolutions if provided
                List<ImportConflictResolution>? resolutions = null;
                if (!string.IsNullOrEmpty(conflict_resolutions))
                {
                    var (parsedResolutions, parsedDefault, parseError) = ParseConflictResolutions(conflict_resolutions);

                    if (parseError != null)
                    {
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
                    if (!string.IsNullOrEmpty(parsedDefault))
                    {
                        default_strategy = parsedDefault;
                    }

                    if (resolutions != null && resolutions.Any())
                    {
                        ToolLoggingUtility.LogToolInfo(_logger, toolName,
                            $"Parsed {resolutions.Count} specific resolutions with default strategy: {default_strategy}");
                    }
                }

                // Execute the import using effective path
                ToolLoggingUtility.LogToolInfo(_logger, toolName, "Executing import...");
                var result = await _importExecutor.ExecuteImportAsync(
                    effectivePath,
                    importFilter,
                    resolutions,
                    auto_resolve_remaining,
                    default_strategy);

                if (!result.Success)
                {
                    const string error = "IMPORT_FAILED";
                    ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {result.ErrorMessage}");
                    return new
                    {
                        success = false,
                        error = error,
                        message = result.ErrorMessage,
                        source_path = filepath,
                        partial_result = new
                        {
                            documents_imported = result.DocumentsImported,
                            documents_updated = result.DocumentsUpdated,
                            documents_skipped = result.DocumentsSkipped,
                            conflicts_resolved = result.ConflictsResolved
                        },
                        migration_info = legacyContext.MigrationInfo
                    };
                }

                // Stage to Dolt if requested
                string? commitHash = null;
                bool doltStaged = false;
                int documentsStaged = 0;

                if (stage_to_dolt)
                {
                    try
                    {
                        ToolLoggingUtility.LogToolInfo(_logger, toolName, "Staging import to Dolt...");

                        // Get local changes to count
                        var localChanges = await _syncManager.GetLocalChangesAsync();
                        documentsStaged = localChanges?.TotalChanges ?? 0;

                        if (documentsStaged > 0)
                        {
                            // Create commit message
                            var finalCommitMessage = commit_message ??
                                $"Import from {Path.GetFileName(filepath)}: {result.DocumentsImported} added, {result.DocumentsUpdated} updated";

                            // Commit the changes using ProcessCommitAsync
                            var commitResult = await _syncManager.ProcessCommitAsync(
                                finalCommitMessage,
                                autoStageFromChroma: true,
                                syncBackToChroma: false);
                            if (commitResult.Success)
                            {
                                commitHash = commitResult.CommitHash;
                                doltStaged = true;
                                ToolLoggingUtility.LogToolInfo(_logger, toolName,
                                    $"Successfully staged to Dolt: {commitHash}");
                            }
                            else
                            {
                                ToolLoggingUtility.LogToolWarning(_logger, toolName,
                                    $"Failed to stage to Dolt: {commitResult.ErrorMessage}");
                            }
                        }
                        else
                        {
                            ToolLoggingUtility.LogToolInfo(_logger, toolName,
                                "No local changes to stage to Dolt");
                        }
                    }
                    catch (Exception ex)
                    {
                        ToolLoggingUtility.LogToolWarning(_logger, toolName,
                            $"Error staging to Dolt: {ex.Message}");
                    }
                }

                // Build success response
                var response = new
                {
                    success = true,
                    import_result = new
                    {
                        source_path = filepath, // Report original path to user
                        documents_imported = result.DocumentsImported,
                        documents_updated = result.DocumentsUpdated,
                        documents_skipped = result.DocumentsSkipped,
                        collections_created = result.CollectionsCreated,
                        conflicts_total = result.ConflictsResolved,
                        conflicts_resolved = result.ConflictsResolved,
                        resolution_breakdown = result.ResolutionBreakdown
                    },
                    dolt_staging = new
                    {
                        staged = doltStaged,
                        documents_staged = documentsStaged,
                        commit_hash = commitHash
                    },
                    message = result.Message,
                    migration_info = legacyContext.WasMigrated ? legacyContext.MigrationInfo : null
                };

                ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName,
                    $"Import complete: {result.DocumentsImported} imported, {result.DocumentsUpdated} updated, {result.ConflictsResolved} conflicts resolved" +
                    (legacyContext.WasMigrated ? " [migrated legacy DB]" : ""));

                return response;
            }
            catch (Exception ex)
            {
                ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
                return new
                {
                    success = false,
                    error = "OPERATION_FAILED",
                    message = $"Failed to execute import: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Parse conflict resolutions from JSON string. Supports two formats:
        /// 1. Structured format: {"resolutions": [{"conflict_id": "imp_xxx", "resolution_type": "keep_source"}], "default_strategy": "keep_source"}
        /// 2. Simple dictionary format: {"conflict_id": "keep_source", "conflict_id2": "keep_target"}
        /// </summary>
        /// <param name="jsonString">JSON string containing conflict resolutions</param>
        /// <returns>Tuple of (resolutions, defaultStrategy, errorMessage). errorMessage is null on success.</returns>
        private (List<ImportConflictResolution>? Resolutions, string? DefaultStrategy, string? Error) ParseConflictResolutions(string jsonString)
        {
            string? defaultStrategy = null;
            string? lastError = null;

            try
            {
                // First try the structured format
                var resolutionData = JsonSerializer.Deserialize<ImportResolutionData>(
                    jsonString,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (resolutionData?.Resolutions != null && resolutionData.Resolutions.Any())
                {
                    defaultStrategy = resolutionData.DefaultStrategy;
                    return (resolutionData.Resolutions, defaultStrategy, null);
                }

                // Structured format parsed but had no resolutions - valid JSON, just empty
            }
            catch (JsonException ex)
            {
                // Structured format failed, capture error and try simple dictionary format
                lastError = ex.Message;
            }

            try
            {
                // Try simple dictionary format: {"conflict_id": "keep_source", "conflict_id2": "keep_target"}
                var simpleDictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    jsonString,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (simpleDictionary != null)
                {
                    var resolutions = new List<ImportConflictResolution>();

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
                        var resolutionType = ImportUtility.ParseResolutionType(kvp.Value);
                        resolutions.Add(new ImportConflictResolution
                        {
                            ConflictId = kvp.Key,
                            ResolutionType = resolutionType
                        });
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
            return (null, null, lastError ?? "Unable to parse conflict resolutions");
        }
    }
}
