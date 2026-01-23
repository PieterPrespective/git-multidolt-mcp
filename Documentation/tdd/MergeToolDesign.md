# DoltMerge MCP Tool Design Document

## Executive Summary

This document outlines the comprehensive design for implementing two MCP tools (`PreviewDoltMergeTool` and `ExecuteDoltMergeTool`) that provide advanced merge capabilities for the VM RAG MCP Server. These tools enable users to preview potential merge conflicts before executing a merge and provide fine-grained control over conflict resolution through a structured JSON interface.

The design leverages Dolt's cell-level three-way merge capabilities which automatically resolve most document conflicts, only requiring user intervention when the same field in the same document is modified differently on both branches.

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Tool Specifications](#2-tool-specifications)
3. [Data Models](#3-data-models)
4. [Implementation Details](#4-implementation-details)
5. [Error Handling](#5-error-handling)
6. [Testing Strategy](#6-testing-strategy)
7. [Integration Points](#7-integration-points)
8. [Performance Considerations](#8-performance-considerations)

---

## 1. Architecture Overview

### 1.1 Design Principles

- **Two-Phase Approach**: Separate preview and execution phases to enable informed decision-making
- **Automatic Resolution**: Leverage Dolt's cell-level merge for automatic conflict resolution where possible
- **User Control**: Provide structured JSON interface for manual conflict resolution preferences
- **Auxiliary Table Handling**: Ensure sync state tables are properly handled during merge operations
- **Consistent Pattern**: Follow existing tool patterns for error handling, logging, and response structures

### 1.2 Tool Interaction Flow

```
User Request → PreviewDoltMergeTool → Analyze Potential Merge
                      ↓
              [No Conflicts] → Auto-merge suggestion
                      ↓
              [Has Conflicts] → Structured conflict JSON with GUIDs
                      ↓
User Reviews → Provides resolution preferences (JSON)
                      ↓
              ExecuteDoltMergeTool → Apply resolutions
                      ↓
              Sync to ChromaDB → Update collections
```

### 1.3 Key Components

1. **PreviewDoltMergeTool**: Non-destructive analysis of merge potential
2. **ExecuteDoltMergeTool**: Executes merge with user-specified conflict resolutions
3. **ConflictAnalyzer**: Service for detecting and analyzing conflicts
4. **MergeConflictResolver**: Service for applying resolution strategies
5. **Enhanced Models**: Extended conflict information structures

---

## 2. Tool Specifications

### 2.1 PreviewDoltMergeTool

#### Purpose
Provides a non-destructive preview of what would happen if a merge were executed, including detection of conflicts and suggested resolutions.

#### MCP Tool Interface
```csharp
[McpServerTool]
[Description("Preview a merge operation to see potential conflicts and changes before executing. Returns detailed conflict information if conflicts would occur.")]
public async Task<object> PreviewDoltMerge(
    string source_branch,
    string? target_branch = null,
    bool include_auto_resolvable = false,
    bool detailed_diff = false)
```

#### Parameters
- `source_branch` (required): Branch to merge from
- `target_branch` (optional): Branch to merge into (defaults to current branch)
- `include_auto_resolvable` (optional): Include conflicts that can be auto-resolved
- `detailed_diff` (optional): Include full document content in conflict details

#### Response Structure
```json
{
  "success": true,
  "can_auto_merge": false,
  "source_branch": "feature/update-docs",
  "target_branch": "main",
  "merge_preview": {
    "has_conflicts": true,
    "total_conflicts": 2,
    "auto_resolvable": 1,
    "requires_manual": 1,
    "affected_collections": ["knowledge_docs", "issue_logs"],
    "changes_preview": {
      "documents_added": 5,
      "documents_modified": 12,
      "documents_deleted": 2,
      "collections_affected": 2
    }
  },
  "conflicts": [
    {
      "conflict_id": "conf_abc123def456",
      "collection": "knowledge_docs",
      "document_id": "doc-001",
      "conflict_type": "content_modification",
      "auto_resolvable": true,
      "suggested_resolution": "field_merge",
      "field_conflicts": [
        {
          "field": "content",
          "base_value": "Original text...",
          "our_value": "User A's edit...",
          "their_value": "User B's edit...",
          "base_hash": "hash1",
          "our_hash": "hash2",
          "their_hash": "hash3"
        }
      ],
      "resolution_options": ["keep_ours", "keep_theirs", "field_merge", "custom_merge"]
    }
  ],
  "auxiliary_table_status": {
    "sync_state_conflict": false,
    "local_changes_present": true,
    "local_changes_count": 3
  },
  "recommended_action": "Review conflicts and use execute_dolt_merge with resolution preferences",
  "message": "Merge preview shows 2 conflicts: 1 auto-resolvable, 1 requires manual resolution"
}
```

### 2.2 ExecuteDoltMergeTool

#### Purpose
Executes a merge operation with user-specified conflict resolutions, handling both automatic and manual resolution strategies.

#### MCP Tool Interface
```csharp
[McpServerTool]
[Description("Execute a merge operation with specified conflict resolutions. Use preview_dolt_merge first to identify conflicts and their IDs.")]
public async Task<object> ExecuteDoltMerge(
    string source_branch,
    string? target_branch = null,
    string? conflict_resolutions = null,
    bool auto_resolve_remaining = true,
    bool force_merge = false,
    string? merge_message = null)
```

#### Parameters
- `source_branch` (required): Branch to merge from
- `target_branch` (optional): Branch to merge into (defaults to current branch)
- `conflict_resolutions` (optional): JSON string with conflict resolution preferences
- `auto_resolve_remaining` (optional): Auto-resolve conflicts not specified in resolutions
- `force_merge` (optional): Force merge even with uncommitted local changes
- `merge_message` (optional): Custom merge commit message

#### Conflict Resolution JSON Format
```json
{
  "resolutions": [
    {
      "conflict_id": "conf_abc123def456",
      "resolution_type": "keep_theirs"
    },
    {
      "conflict_id": "conf_def789ghi012",
      "resolution_type": "field_merge",
      "field_resolutions": {
        "title": "ours",
        "content": "theirs",
        "metadata": "merge"
      }
    },
    {
      "conflict_id": "conf_xyz456789",
      "resolution_type": "custom",
      "custom_values": {
        "content": "Manually merged content combining both changes..."
      }
    }
  ],
  "default_strategy": "ours"
}
```

#### Response Structure
```json
{
  "success": true,
  "merge_result": {
    "merge_commit": "abc123def456789",
    "source_branch": "feature/update-docs",
    "target_branch": "main",
    "conflicts_resolved": 2,
    "auto_resolved": 1,
    "manually_resolved": 1,
    "merge_timestamp": "2024-01-15T10:30:00Z"
  },
  "sync_result": {
    "collections_synced": 2,
    "documents_added": 5,
    "documents_modified": 12,
    "documents_deleted": 2,
    "chunks_processed": 150
  },
  "auxiliary_tables_updated": {
    "sync_state": true,
    "local_changes": true,
    "sync_operations": true
  },
  "message": "Successfully merged feature/update-docs into main with 2 conflicts resolved"
}
```

---

## 3. Data Models

### 3.1 Enhanced Conflict Models

```csharp
namespace Embranch.Models
{
    /// <summary>
    /// Detailed merge conflict information with GUID for tracking
    /// </summary>
    public class DetailedConflictInfo
    {
        public string ConflictId { get; set; }  // GUID for tracking
        public string Collection { get; set; }
        public string DocumentId { get; set; }
        public ConflictType Type { get; set; }
        public bool AutoResolvable { get; set; }
        public string SuggestedResolution { get; set; }
        public List<FieldConflict> FieldConflicts { get; set; }
        public List<string> ResolutionOptions { get; set; }
        public Dictionary<string, object> BaseValues { get; set; }
        public Dictionary<string, object> OurValues { get; set; }
        public Dictionary<string, object> TheirValues { get; set; }
    }

    /// <summary>
    /// Field-level conflict information
    /// </summary>
    public class FieldConflict
    {
        public string FieldName { get; set; }
        public object BaseValue { get; set; }
        public object OurValue { get; set; }
        public object TheirValue { get; set; }
        public string BaseHash { get; set; }
        public string OurHash { get; set; }
        public string TheirHash { get; set; }
        public bool CanAutoMerge { get; set; }
    }

    /// <summary>
    /// Merge preview result
    /// </summary>
    public class MergePreviewResult
    {
        public bool Success { get; set; }
        public bool CanAutoMerge { get; set; }
        public string SourceBranch { get; set; }
        public string TargetBranch { get; set; }
        public MergePreviewInfo Preview { get; set; }
        public List<DetailedConflictInfo> Conflicts { get; set; }
        public AuxiliaryTableStatus AuxiliaryStatus { get; set; }
        public string RecommendedAction { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// User-specified conflict resolution
    /// </summary>
    public class ConflictResolutionRequest
    {
        public string ConflictId { get; set; }
        public ResolutionType ResolutionType { get; set; }
        public Dictionary<string, string> FieldResolutions { get; set; }
        public Dictionary<string, object> CustomValues { get; set; }
    }

    /// <summary>
    /// Resolution strategy types
    /// </summary>
    public enum ResolutionType
    {
        KeepOurs,
        KeepTheirs,
        FieldMerge,
        Custom,
        AutoResolve
    }

    /// <summary>
    /// Conflict types
    /// </summary>
    public enum ConflictType
    {
        ContentModification,
        MetadataConflict,
        AddAdd,  // Both branches added same document
        DeleteModify,  // One deleted, one modified
        SchemaConflict  // Structural changes
    }
}
```

### 3.2 Service Interfaces

```csharp
namespace Embranch.Services
{
    /// <summary>
    /// Service for analyzing potential merge conflicts
    /// </summary>
    public interface IConflictAnalyzer
    {
        Task<MergePreviewResult> AnalyzeMergeAsync(
            string sourceBranch, 
            string targetBranch,
            bool includeAutoResolvable,
            bool detailedDiff);
        
        Task<List<DetailedConflictInfo>> GetDetailedConflictsAsync(
            string tableName);
        
        Task<bool> CanAutoResolveConflictAsync(
            DetailedConflictInfo conflict);
    }

    /// <summary>
    /// Service for resolving merge conflicts
    /// </summary>
    public interface IMergeConflictResolver
    {
        Task<bool> ResolveConflictAsync(
            DetailedConflictInfo conflict,
            ConflictResolutionRequest resolution);
        
        Task<int> AutoResolveConflictsAsync(
            List<DetailedConflictInfo> conflicts);
        
        Task<bool> ApplyFieldMergeAsync(
            string tableName,
            string documentId,
            Dictionary<string, string> fieldResolutions);
        
        Task<bool> ApplyCustomResolutionAsync(
            string tableName,
            string documentId,
            Dictionary<string, object> customValues);
    }
}
```

---

## 4. Implementation Details

### 4.1 PreviewDoltMergeTool Implementation

```csharp
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Embranch.Services;
using Embranch.Models;
using Embranch.Utilities;

namespace Embranch.Tools;

[McpServerToolType]
public class PreviewDoltMergeTool
{
    private readonly ILogger<PreviewDoltMergeTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly IConflictAnalyzer _conflictAnalyzer;
    private readonly ISyncManagerV2 _syncManager;

    public PreviewDoltMergeTool(
        ILogger<PreviewDoltMergeTool> logger,
        IDoltCli doltCli,
        IConflictAnalyzer conflictAnalyzer,
        ISyncManagerV2 syncManager)
    {
        _logger = logger;
        _doltCli = doltCli;
        _conflictAnalyzer = conflictAnalyzer;
        _syncManager = syncManager;
    }

    [McpServerTool]
    [Description("Preview a merge operation to see potential conflicts and changes before executing. Returns detailed conflict information if conflicts would occur.")]
    public async Task<object> PreviewDoltMerge(
        string source_branch,
        string? target_branch = null,
        bool include_auto_resolvable = false,
        bool detailed_diff = false)
    {
        const string toolName = nameof(PreviewDoltMergeTool);
        const string methodName = nameof(PreviewDoltMerge);

        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName,
                $"source: {source_branch}, target: {target_branch}, auto_resolvable: {include_auto_resolvable}");

            // Validate Dolt availability
            var doltCheck = await _doltCli.CheckDoltAvailableAsync();
            if (!doltCheck.Success)
            {
                return new
                {
                    success = false,
                    error = "DOLT_NOT_AVAILABLE",
                    message = doltCheck.Error
                };
            }

            // Get current branch if target not specified
            if (string.IsNullOrEmpty(target_branch))
            {
                target_branch = await _doltCli.GetCurrentBranchAsync();
            }

            // Check for local changes
            var localChanges = await _syncManager.GetLocalChangesAsync();
            
            // Analyze the merge
            var mergePreview = await _conflictAnalyzer.AnalyzeMergeAsync(
                source_branch, 
                target_branch,
                include_auto_resolvable,
                detailed_diff);

            if (!mergePreview.Success)
            {
                return new
                {
                    success = false,
                    error = "ANALYSIS_FAILED",
                    message = mergePreview.Message
                };
            }

            // Build response
            var response = new
            {
                success = true,
                can_auto_merge = mergePreview.CanAutoMerge,
                source_branch = source_branch,
                target_branch = target_branch,
                merge_preview = new
                {
                    has_conflicts = mergePreview.Conflicts?.Any() ?? false,
                    total_conflicts = mergePreview.Conflicts?.Count ?? 0,
                    auto_resolvable = mergePreview.Conflicts?.Count(c => c.AutoResolvable) ?? 0,
                    requires_manual = mergePreview.Conflicts?.Count(c => !c.AutoResolvable) ?? 0,
                    affected_collections = mergePreview.Conflicts?
                        .Select(c => c.Collection)
                        .Distinct()
                        .ToList() ?? new List<string>(),
                    changes_preview = mergePreview.Preview
                },
                conflicts = mergePreview.Conflicts?.Select(c => new
                {
                    conflict_id = c.ConflictId,
                    collection = c.Collection,
                    document_id = c.DocumentId,
                    conflict_type = c.Type.ToString(),
                    auto_resolvable = c.AutoResolvable,
                    suggested_resolution = c.SuggestedResolution,
                    field_conflicts = c.FieldConflicts?.Select(fc => new
                    {
                        field = fc.FieldName,
                        base_value = detailed_diff ? fc.BaseValue : null,
                        our_value = detailed_diff ? fc.OurValue : null,
                        their_value = detailed_diff ? fc.TheirValue : null,
                        base_hash = fc.BaseHash,
                        our_hash = fc.OurHash,
                        their_hash = fc.TheirHash
                    }),
                    resolution_options = c.ResolutionOptions
                }),
                auxiliary_table_status = new
                {
                    sync_state_conflict = mergePreview.AuxiliaryStatus?.SyncStateConflict ?? false,
                    local_changes_present = localChanges?.HasChanges ?? false,
                    local_changes_count = localChanges?.TotalChanges ?? 0
                },
                recommended_action = mergePreview.RecommendedAction,
                message = mergePreview.Message
            };

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName,
                $"Preview complete: {mergePreview.Conflicts?.Count ?? 0} conflicts found");

            return response;
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to preview merge: {ex.Message}"
            };
        }
    }
}
```

### 4.2 ExecuteDoltMergeTool Implementation

```csharp
[McpServerToolType]
public class ExecuteDoltMergeTool
{
    private readonly ILogger<ExecuteDoltMergeTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly IMergeConflictResolver _conflictResolver;
    private readonly ISyncManagerV2 _syncManager;
    private readonly IConflictAnalyzer _conflictAnalyzer;

    // ... constructor ...

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
                $"source: {source_branch}, force: {force_merge}");

            // Parse resolution preferences
            List<ConflictResolutionRequest> resolutions = null;
            if (!string.IsNullOrEmpty(conflict_resolutions))
            {
                try
                {
                    var resolutionData = JsonSerializer.Deserialize<ConflictResolutionData>(
                        conflict_resolutions,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    resolutions = resolutionData?.Resolutions;
                }
                catch (JsonException ex)
                {
                    return new
                    {
                        success = false,
                        error = "INVALID_RESOLUTION_JSON",
                        message = $"Failed to parse conflict resolutions: {ex.Message}"
                    };
                }
            }

            // Execute the merge using SyncManagerV2
            var mergeResult = await _syncManager.ProcessMergeAsync(source_branch, force_merge);

            // Handle conflicts if they exist
            if (mergeResult.HasConflicts)
            {
                ToolLoggingUtility.LogToolInfo(_logger, toolName, 
                    $"Resolving {mergeResult.Conflicts.Count} conflicts");

                int resolvedCount = 0;
                int autoResolved = 0;

                // Get detailed conflicts
                var detailedConflicts = await _conflictAnalyzer.GetDetailedConflictsAsync("documents");

                // Apply user-specified resolutions
                if (resolutions != null)
                {
                    foreach (var resolution in resolutions)
                    {
                        var conflict = detailedConflicts.FirstOrDefault(
                            c => c.ConflictId == resolution.ConflictId);
                        
                        if (conflict != null)
                        {
                            var resolved = await _conflictResolver.ResolveConflictAsync(
                                conflict, resolution);
                            if (resolved) resolvedCount++;
                        }
                    }
                }

                // Auto-resolve remaining if requested
                if (auto_resolve_remaining)
                {
                    var unresolvedConflicts = detailedConflicts
                        .Where(c => resolutions == null || 
                                   !resolutions.Any(r => r.ConflictId == c.ConflictId))
                        .ToList();
                    
                    autoResolved = await _conflictResolver.AutoResolveConflictsAsync(
                        unresolvedConflicts);
                    resolvedCount += autoResolved;
                }

                // Check if all conflicts are resolved
                var remainingConflicts = await _doltCli.HasConflictsAsync();
                if (remainingConflicts)
                {
                    return new
                    {
                        success = false,
                        error = "UNRESOLVED_CONFLICTS",
                        message = "Not all conflicts could be resolved",
                        resolved = resolvedCount,
                        remaining = detailedConflicts.Count - resolvedCount
                    };
                }

                // Complete the merge
                var commitMessage = merge_message ?? 
                    $"Merge {source_branch} into {target_branch ?? "current"}";
                await _doltCli.CommitAsync(commitMessage);
            }

            // Build success response
            var response = new
            {
                success = true,
                merge_result = new
                {
                    merge_commit = mergeResult.MergeCommitHash ?? await _doltCli.GetHeadCommitHashAsync(),
                    source_branch = source_branch,
                    target_branch = target_branch ?? await _doltCli.GetCurrentBranchAsync(),
                    conflicts_resolved = resolvedCount,
                    auto_resolved = autoResolved,
                    manually_resolved = resolvedCount - autoResolved,
                    merge_timestamp = DateTime.UtcNow.ToString("O")
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
                message = mergeResult.Message ?? "Merge completed successfully"
            };

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName,
                "Merge executed successfully");

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
}
```

### 4.3 ConflictAnalyzer Implementation

```csharp
public class ConflictAnalyzer : IConflictAnalyzer
{
    private readonly IDoltCli _doltCli;
    private readonly ILogger<ConflictAnalyzer> _logger;

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
            // Use DOLT_PREVIEW_MERGE_CONFLICTS_SUMMARY if available
            var previewSql = $@"
                SELECT * FROM DOLT_PREVIEW_MERGE_CONFLICTS_SUMMARY('{targetBranch}', '{sourceBranch}')";
            
            var conflictSummary = await _doltCli.QueryJsonAsync(previewSql);
            
            // Parse conflict data
            var conflicts = ParseConflictSummary(conflictSummary);
            
            // Filter based on auto-resolvable preference
            if (!includeAutoResolvable)
            {
                conflicts = conflicts.Where(c => !c.AutoResolvable).ToList();
            }

            // Analyze each conflict
            foreach (var conflict in conflicts)
            {
                conflict.ConflictId = GenerateConflictId(conflict);
                conflict.AutoResolvable = await CanAutoResolveConflictAsync(conflict);
                conflict.SuggestedResolution = DetermineSuggestedResolution(conflict);
                conflict.ResolutionOptions = DetermineResolutionOptions(conflict);
            }

            result.Conflicts = conflicts;
            result.CanAutoMerge = !conflicts.Any(c => !c.AutoResolvable);
            result.Success = true;
            
            // Generate preview statistics
            result.Preview = await GenerateMergePreview(sourceBranch, targetBranch);
            
            // Check auxiliary tables
            result.AuxiliaryStatus = await CheckAuxiliaryTableStatus();
            
            // Determine recommended action
            result.RecommendedAction = DetermineRecommendedAction(result);
            result.Message = GeneratePreviewMessage(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze merge");
            result.Success = false;
            result.Message = ex.Message;
        }

        return result;
    }

    public async Task<bool> CanAutoResolveConflictAsync(DetailedConflictInfo conflict)
    {
        // Auto-resolvable if:
        // 1. Different fields were modified (no overlap)
        // 2. Metadata-only conflicts with clear precedence
        // 3. Add-add conflicts with identical content
        
        if (conflict.Type == ConflictType.ContentModification)
        {
            // Check if different fields were modified
            var baseToOurs = GetModifiedFields(conflict.BaseValues, conflict.OurValues);
            var baseToTheirs = GetModifiedFields(conflict.BaseValues, conflict.TheirValues);
            
            // No overlap = auto-resolvable
            return !baseToOurs.Intersect(baseToTheirs).Any();
        }
        
        if (conflict.Type == ConflictType.AddAdd)
        {
            // Check if content is identical
            var ourContent = conflict.OurValues["content"]?.ToString();
            var theirContent = conflict.TheirValues["content"]?.ToString();
            return ourContent == theirContent;
        }
        
        return false;
    }

    private string GenerateConflictId(DetailedConflictInfo conflict)
    {
        // Generate stable GUID based on conflict content
        var input = $"{conflict.Collection}_{conflict.DocumentId}_{conflict.Type}";
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return $"conf_{BitConverter.ToString(hash).Replace("-", "").Substring(0, 12).ToLower()}";
    }

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
}
```

### 4.4 MergeConflictResolver Implementation

```csharp
public class MergeConflictResolver : IMergeConflictResolver
{
    private readonly IDoltCli _doltCli;
    private readonly ILogger<MergeConflictResolver> _logger;

    public async Task<bool> ResolveConflictAsync(
        DetailedConflictInfo conflict,
        ConflictResolutionRequest resolution)
    {
        try
        {
            switch (resolution.ResolutionType)
            {
                case ResolutionType.KeepOurs:
                    return await _doltCli.ResolveConflictsAsync(
                        conflict.Collection, ConflictResolution.Ours).Success;
                
                case ResolutionType.KeepTheirs:
                    return await _doltCli.ResolveConflictsAsync(
                        conflict.Collection, ConflictResolution.Theirs).Success;
                
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
                    _logger.LogWarning($"Unknown resolution type: {resolution.ResolutionType}");
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to resolve conflict {conflict.ConflictId}");
            return false;
        }
    }

    public async Task<bool> ApplyFieldMergeAsync(
        string tableName,
        string documentId,
        Dictionary<string, string> fieldResolutions)
    {
        // Build UPDATE statement for conflict table
        var updates = new List<string>();
        foreach (var field in fieldResolutions)
        {
            var column = field.Value == "ours" ? $"our_{field.Key}" : $"their_{field.Key}";
            updates.Add($"our_{field.Key} = {column}");
        }

        var updateSql = $@"
            UPDATE dolt_conflicts_{tableName}
            SET {string.Join(", ", updates)}
            WHERE our_doc_id = '{documentId}'";

        var updateResult = await _doltCli.ExecuteAsync(updateSql);
        
        if (updateResult > 0)
        {
            // Delete the conflict marker
            var deleteSql = $@"
                DELETE FROM dolt_conflicts_{tableName}
                WHERE our_doc_id = '{documentId}'";
            
            await _doltCli.ExecuteAsync(deleteSql);
            return true;
        }

        return false;
    }

    public async Task<bool> ApplyCustomResolutionAsync(
        string tableName,
        string documentId,
        Dictionary<string, object> customValues)
    {
        // Update the conflict table with custom values
        var sets = customValues.Select(kvp => 
            $"our_{kvp.Key} = '{JsonSerializer.Serialize(kvp.Value)}'");
        
        var updateSql = $@"
            UPDATE dolt_conflicts_{tableName}
            SET {string.Join(", ", sets)}
            WHERE our_doc_id = '{documentId}'";

        var result = await _doltCli.ExecuteAsync(updateSql);
        
        if (result > 0)
        {
            // Remove conflict marker
            var deleteSql = $@"
                DELETE FROM dolt_conflicts_{tableName}
                WHERE our_doc_id = '{documentId}'";
            
            await _doltCli.ExecuteAsync(deleteSql);
            return true;
        }

        return false;
    }

    public async Task<int> AutoResolveConflictsAsync(List<DetailedConflictInfo> conflicts)
    {
        int resolved = 0;
        
        foreach (var conflict in conflicts.Where(c => c.AutoResolvable))
        {
            if (await AutoResolveConflictAsync(conflict))
            {
                resolved++;
            }
        }

        return resolved;
    }

    private async Task<bool> AutoResolveConflictAsync(DetailedConflictInfo conflict)
    {
        // Implement field-level merge for non-overlapping changes
        var baseToOurs = GetModifiedFields(conflict.BaseValues, conflict.OurValues);
        var baseToTheirs = GetModifiedFields(conflict.BaseValues, conflict.TheirValues);
        
        if (!baseToOurs.Intersect(baseToTheirs).Any())
        {
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
            
            return await ApplyFieldMergeAsync(
                conflict.Collection,
                conflict.DocumentId,
                fieldResolutions);
        }

        return false;
    }
}
```

---

## 5. Error Handling

### 5.1 Error Codes

| Code | Description | User Action |
|------|-------------|------------|
| `DOLT_NOT_AVAILABLE` | Dolt executable not found | Check Dolt installation |
| `NOT_INITIALIZED` | No repository configured | Run dolt_init or dolt_clone |
| `BRANCH_NOT_FOUND` | Source/target branch doesn't exist | Verify branch names |
| `ANALYSIS_FAILED` | Merge preview failed | Check repository state |
| `INVALID_RESOLUTION_JSON` | Malformed resolution JSON | Fix JSON format |
| `UNRESOLVED_CONFLICTS` | Conflicts remain after resolution | Provide more resolutions |
| `SYNC_FAILED` | ChromaDB sync failed | Check ChromaDB connection |
| `LOCAL_CHANGES_BLOCK` | Uncommitted changes prevent merge | Commit or use force_merge |

### 5.2 Recovery Strategies

1. **Abort Merge**: If execution fails mid-merge, use `dolt merge --abort`
2. **Rollback**: Track merge state and provide rollback to pre-merge commit
3. **Partial Resolution**: Save successfully resolved conflicts even if some fail
4. **State Persistence**: Store resolution progress for retry capability

---

## 6. Testing Strategy

### 6.1 Unit Tests

```csharp
[TestFixture]
public class PreviewDoltMergeToolTests
{
    [Test]
    public async Task PreviewMerge_NoConflicts_ReturnsAutoMergeTrue()
    {
        // Arrange
        var tool = CreateTool();
        
        // Act
        var result = await tool.PreviewDoltMerge("feature", "main");
        
        // Assert
        Assert.That(result.can_auto_merge, Is.True);
        Assert.That(result.conflicts, Is.Empty);
    }

    [Test]
    public async Task PreviewMerge_WithConflicts_ReturnsDetailedConflictInfo()
    {
        // Arrange - create conflicting changes
        
        // Act
        var result = await tool.PreviewDoltMerge("feature", "main", true, true);
        
        // Assert
        Assert.That(result.conflicts, Is.Not.Empty);
        Assert.That(result.conflicts[0].conflict_id, Is.Not.Null);
        Assert.That(result.conflicts[0].field_conflicts, Is.Not.Empty);
    }

    [Test]
    public async Task PreviewMerge_AutoResolvableConflicts_IdentifiesCorrectly()
    {
        // Test that non-overlapping field changes are marked auto-resolvable
    }
}
```

### 6.2 Integration Tests

```csharp
[TestFixture]
public class MergeToolIntegrationTests
{
    [Test]
    public async Task FullMergeWorkflow_WithConflictResolution_Success()
    {
        // 1. Create branches with conflicts
        await CreateConflictingBranches();
        
        // 2. Preview merge
        var preview = await previewTool.PreviewDoltMerge("feature", "main");
        Assert.That(preview.conflicts, Is.Not.Empty);
        
        // 3. Prepare resolutions
        var resolutions = JsonSerializer.Serialize(new
        {
            resolutions = preview.conflicts.Select(c => new
            {
                conflict_id = c.conflict_id,
                resolution_type = "keep_theirs"
            })
        });
        
        // 4. Execute merge
        var result = await executeTool.ExecuteDoltMerge(
            "feature", "main", resolutions);
        
        // 5. Verify success
        Assert.That(result.success, Is.True);
        Assert.That(result.merge_result.conflicts_resolved, 
            Is.EqualTo(preview.conflicts.Count));
        
        // 6. Verify ChromaDB sync
        Assert.That(result.sync_result.collections_synced, Is.GreaterThan(0));
    }

    [Test]
    public async Task MergeWithAuxiliaryTables_PreservessSyncState()
    {
        // Test that auxiliary tables are properly handled during merge
    }

    [Test]
    public async Task FieldLevelMerge_CombinesNonConflictingChanges()
    {
        // Test field-level merge resolution
    }

    [Test]
    public async Task CustomResolution_AppliesUserProvidedValues()
    {
        // Test custom value resolution
    }
}
```

### 6.3 Edge Case Tests

- Empty merge (no changes)
- Merge with deleted documents
- Large conflict sets (100+ conflicts)
- Concurrent merges
- Network interruptions during sync
- Invalid conflict IDs in resolution
- Partial resolution success

---

## 7. Integration Points

### 7.1 Changes to Existing Components

#### IDoltCli Extensions
```csharp
public interface IDoltCli
{
    // Add new method for preview functionality
    Task<string> PreviewMergeConflictsAsync(string sourceBranch, string targetBranch);
    
    // Add method for querying specific conflict table
    Task<IEnumerable<Dictionary<string, object>>> GetConflictDetailsAsync(string tableName);
    
    // Add method for custom SQL-based resolution
    Task<int> ExecuteConflictResolutionAsync(string sql);
}
```

#### ISyncManagerV2 Updates
```csharp
public interface ISyncManagerV2
{
    // Enhanced merge processing with resolution support
    Task<MergeSyncResultV2> ProcessMergeAsync(
        string sourceBranch,
        bool force = false,
        List<ConflictResolutionRequest> resolutions = null);
    
    // New method for handling auxiliary table conflicts
    Task<bool> ResolveAuxiliaryTableConflictsAsync(ResolutionStrategy strategy);
}
```

### 7.2 Service Registration

```csharp
// In Program.cs
builder.Services.AddSingleton<IConflictAnalyzer, ConflictAnalyzer>();
builder.Services.AddSingleton<IMergeConflictResolver, MergeConflictResolver>();

// Register tools
server.AddTool<PreviewDoltMergeTool>();
server.AddTool<ExecuteDoltMergeTool>();
```

---

## 8. Performance Considerations

### 8.1 Optimization Strategies

1. **Batch Conflict Detection**: Use single SQL query to detect all conflicts
2. **Caching**: Cache merge preview results with short TTL (5 minutes)
3. **Parallel Resolution**: Resolve independent conflicts in parallel
4. **Lazy Loading**: Load detailed conflict data only when requested
5. **Checksum Comparison**: Use checksums for quick conflict detection

### 8.2 Performance Metrics

- Preview generation: < 2 seconds for typical repositories
- Conflict resolution: < 100ms per conflict
- ChromaDB sync: Batch operations with 500-document chunks
- Memory usage: Stream large conflict sets instead of loading all

### 8.3 Scalability Considerations

```csharp
// For large conflict sets, use streaming
public async IAsyncEnumerable<DetailedConflictInfo> StreamConflictsAsync(
    string tableName,
    int batchSize = 100)
{
    int offset = 0;
    bool hasMore = true;
    
    while (hasMore)
    {
        var batch = await GetConflictBatchAsync(tableName, offset, batchSize);
        
        foreach (var conflict in batch)
        {
            yield return conflict;
        }
        
        offset += batchSize;
        hasMore = batch.Count == batchSize;
    }
}
```

---

## Summary

This design provides a comprehensive solution for merge conflict handling in the VM RAG MCP Server through two complementary tools:

1. **PreviewDoltMergeTool** enables users to understand merge implications before execution
2. **ExecuteDoltMergeTool** provides fine-grained control over conflict resolution

Key benefits:
- Leverages Dolt's cell-level merge for automatic resolution where possible
- Provides structured JSON interface for programmatic conflict resolution
- Maintains auxiliary table consistency during merge operations
- Follows established patterns from existing tools
- Comprehensive error handling and recovery options
- Extensive testing coverage for reliability

The implementation prioritizes user control while maximizing automatic resolution capabilities, ensuring efficient merge operations for version-controlled RAG data management.