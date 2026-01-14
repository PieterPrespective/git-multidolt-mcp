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
    /// MCP tool for previewing import operations from external ChromaDB databases.
    /// Provides detailed conflict analysis and change preview before execution.
    /// </summary>
    [McpServerToolType]
    public class PreviewImportTool
    {
        private readonly ILogger<PreviewImportTool> _logger;
        private readonly IImportAnalyzer _importAnalyzer;
        private readonly IExternalChromaDbReader _externalDbReader;

        /// <summary>
        /// Initializes a new instance of the PreviewImportTool class
        /// </summary>
        /// <param name="logger">Logger for diagnostic information</param>
        /// <param name="importAnalyzer">Service for analyzing imports and detecting conflicts</param>
        /// <param name="externalDbReader">Service for reading external ChromaDB databases</param>
        public PreviewImportTool(
            ILogger<PreviewImportTool> logger,
            IImportAnalyzer importAnalyzer,
            IExternalChromaDbReader externalDbReader)
        {
            _logger = logger;
            _importAnalyzer = importAnalyzer;
            _externalDbReader = externalDbReader;
        }

        /// <summary>
        /// Preview an import operation to see potential conflicts and changes before executing.
        /// Returns detailed conflict information if conflicts would occur.
        /// </summary>
        /// <param name="filepath">Path to the external ChromaDB database folder</param>
        /// <param name="filter">JSON filter specifying what to import (empty = import all)</param>
        /// <param name="include_content_preview">Include content snippets in conflict details</param>
        [McpServerTool]
        [Description("Preview an import operation from an external ChromaDB database. Returns detailed conflict information and change preview.")]
        public async Task<object> PreviewImport(
            string filepath,
            string? filter = null,
            bool include_content_preview = false)
        {
            const string toolName = nameof(PreviewImportTool);
            const string methodName = nameof(PreviewImport);

            try
            {
                ToolLoggingUtility.LogToolStart(_logger, toolName, methodName,
                    $"filepath: {filepath}, include_content: {include_content_preview}");

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

                // Validate external database
                var validation = await _externalDbReader.ValidateExternalDbAsync(filepath);
                if (!validation.IsValid)
                {
                    var error = "INVALID_EXTERNAL_DATABASE";
                    ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {validation.ErrorMessage}");
                    return new
                    {
                        success = false,
                        error = error,
                        message = validation.ErrorMessage,
                        source_path = filepath
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

                // Analyze the import
                ToolLoggingUtility.LogToolInfo(_logger, toolName, "Analyzing import...");
                var preview = await _importAnalyzer.AnalyzeImportAsync(filepath, importFilter, include_content_preview);

                if (!preview.Success)
                {
                    const string error = "ANALYSIS_FAILED";
                    ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"{error}: {preview.ErrorMessage}");
                    return new
                    {
                        success = false,
                        error = error,
                        message = preview.ErrorMessage,
                        source_path = filepath
                    };
                }

                // Build response structure following PreviewDoltMergeTool pattern
                var response = new
                {
                    success = true,
                    source_path = filepath,
                    source_validation = new
                    {
                        is_valid = validation.IsValid,
                        collection_count = validation.CollectionCount,
                        total_documents = validation.TotalDocuments
                    },
                    can_auto_import = preview.CanAutoImport,
                    import_preview = new
                    {
                        has_conflicts = preview.TotalConflicts > 0,
                        total_conflicts = preview.TotalConflicts,
                        auto_resolvable = preview.AutoResolvableConflicts,
                        requires_manual = preview.ManualConflicts,
                        affected_collections = preview.Preview?.AffectedCollections ?? new List<string>(),
                        changes_preview = new
                        {
                            documents_to_add = preview.Preview?.DocumentsToAdd ?? 0,
                            documents_to_update = preview.Preview?.DocumentsToUpdate ?? 0,
                            documents_to_skip = preview.Preview?.DocumentsToSkip ?? 0,
                            collections_to_create = preview.Preview?.CollectionsToCreate ?? 0,
                            collections_to_update = preview.Preview?.CollectionsToUpdate ?? 0
                        }
                    },
                    conflicts = preview.Conflicts.Select(c => (object)new
                    {
                        conflict_id = c.ConflictId,
                        source_collection = c.SourceCollection,
                        target_collection = c.TargetCollection,
                        document_id = c.DocumentId,
                        conflict_type = c.Type.ToString().ToLowerInvariant(),
                        auto_resolvable = c.AutoResolvable,
                        source_content_hash = c.SourceContentHash,
                        target_content_hash = c.TargetContentHash,
                        source_content = include_content_preview ? c.SourceContent : null,
                        target_content = include_content_preview ? c.TargetContent : null,
                        suggested_resolution = c.SuggestedResolution,
                        resolution_options = c.ResolutionOptions
                    }).ToList(),
                    recommended_action = preview.RecommendedAction,
                    message = preview.Message
                };

                var conflictCount = preview.TotalConflicts;
                var autoResolvableCount = preview.AutoResolvableConflicts;

                ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName,
                    $"Preview complete: {conflictCount} conflicts ({autoResolvableCount} auto-resolvable)");

                return response;
            }
            catch (Exception ex)
            {
                ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
                return new
                {
                    success = false,
                    error = "OPERATION_FAILED",
                    message = $"Failed to preview import: {ex.Message}"
                };
            }
        }
    }
}
