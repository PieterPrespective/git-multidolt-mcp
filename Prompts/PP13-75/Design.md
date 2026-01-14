# PP13-75 Import Toolset Design Document

## Issue Overview

| Field | Value |
|-------|-------|
| Issue ID | PP13-75 |
| Type | Feature Design & Implementation |
| Status | Design Phase |
| Created | 2026-01-13 |
| Related Issues | PP13-72 (Merge Preview/Execute), PP13-73 (Conflict Resolution) |

## Executive Summary

Design and implementation plan for the DMMS Import toolset, consisting of two MCP tools:
1. **PreviewImport** - Preview import operations and identify potential conflicts before execution
2. **ExecuteImport** - Execute import operations with conflict resolution support

The toolset enables importing documents from external ChromaDB databases into the local DMMS-managed ChromaDB, with full conflict detection and resolution capabilities.

---

## 1. Requirements Analysis

### 1.1 Functional Requirements

#### Tool Parameters (Both Tools)

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filepath` | string | Yes | Path to the external ChromaDB database folder |
| `filter` | string (JSON) | No | Filter specifying what to import (default: import all) |

#### Filter Format Options

The `collections` field is an **array** to support:
- Wildcard matching on collection names
- Merging multiple remote collections into a single local collection
- Ordered processing of collection mappings

```json
// Option 1: Empty filter - Import all collections and documents
{}

// Option 2: Collection mapping with full import (array format)
{
  "collections": [
    { "name": "remote_collection_1", "import_into": "local_collection_1" },
    { "name": "remote_collection_2", "import_into": "local_collection_2" }
  ]
}

// Option 3: Collection name wildcards - merge multiple collections into one
{
  "collections": [
    { "name": "project_*_docs", "import_into": "all_project_docs" }
  ]
}

// Option 4: Collection mapping with document filter (wildcards supported)
{
  "collections": [
    {
      "name": "remote_collection_1",
      "import_into": "local_collection_1",
      "documents": ["*_summary", "doc_*", "specific_doc_id"]
    }
  ]
}

// Option 5: Complex filter - multiple sources to single target with document filtering
{
  "collections": [
    { "name": "archive_2024_*", "import_into": "consolidated_archive" },
    { "name": "archive_2025_*", "import_into": "consolidated_archive" },
    {
      "name": "current_project",
      "import_into": "active_docs",
      "documents": ["*_final", "*_approved"]
    }
  ]
}
```

**Wildcard Support:**
- `*` matches zero or more characters
- `project_*` matches `project_alpha`, `project_beta`, etc.
- `*_docs` matches `team_docs`, `archive_docs`, etc.
- `*_2024_*` matches `report_2024_q1`, `data_2024_annual`, etc.

### 1.2 Non-Functional Requirements

- **Consistency**: Import operations must be atomic - all or nothing
- **Conflict Visibility**: All conflicts must be clearly identified before execution
- **Performance**: Support large imports with efficient batch processing
- **Extensibility**: Architecture should support future import sources (markdown, docx, etc.)

### 1.3 CRITICAL: Integration with DMMS Infrastructure

**DO NOT directly copy documents into ChromaDB.** The import process MUST use the existing DMMS service infrastructure:

#### Required: Use IChromaDbService.AddDocumentsAsync()

The `IChromaDbService.AddDocumentsAsync()` method performs essential operations that raw ChromaDB operations would bypass:

1. **Document Chunking**: Automatically splits documents into 512-token chunks with 50-token overlap
2. **Chunk ID Management**: Generates proper chunk IDs (`{docId}_chunk_{index}`) and metadata (`source_id`, `chunk_index`, `total_chunks`)
3. **Local Change Tracking**: Sets `is_local_change = true` metadata for commit detection
4. **Single Batch Operation**: All chunks are added in ONE `collection.add()` call

#### Why Batch Operations are CRITICAL

```
BAD (Extremely Slow):
for each document:
    chromaCollection.add(document)  // Recalculates ALL embeddings each time!

GOOD (Efficient):
chromaService.AddDocumentsAsync(collection, allDocuments, allIds, allMetadatas)
// Single embedding calculation for entire batch
```

**Performance Impact**: Adding documents individually causes ChromaDB to recalculate the entire collection's vector index after EACH add operation. For a 1000-document import:
- Individual adds: ~1000 embedding recalculations (hours)
- Batch add: 1 embedding calculation (seconds to minutes)

#### Required Metadata for Imported Documents

All imported documents MUST have:
```json
{
  "is_local_change": true,
  "source_id": "<base_document_id>",
  "chunk_index": 0,
  "total_chunks": 1,
  "import_source": "<external_db_path>",
  "import_timestamp": "<ISO8601_timestamp>"
}
```

---

## 2. Architecture Design

### 2.1 Component Overview

```
                     ┌─────────────────────────────────────────────────┐
                     │                  MCP Tools Layer                 │
                     ├─────────────────────┬───────────────────────────┤
                     │   PreviewImportTool │    ExecuteImportTool      │
                     └──────────┬──────────┴──────────────┬────────────┘
                                │                         │
                     ┌──────────┴─────────────────────────┴────────────┐
                     │                 Services Layer                   │
                     ├─────────────────────┬───────────────────────────┤
                     │   IImportAnalyzer   │    IImportExecutor        │
                     │   (Conflict         │    (Conflict Resolution   │
                     │    Detection)       │     & Import Execution)   │
                     └──────────┬──────────┴──────────────┬────────────┘
                                │                         │
                     ┌──────────┴─────────────────────────┴────────────┐
                     │              Data Access Layer                   │
                     ├─────────────────────┬───────────────────────────┤
                     │  IExternalChromaDb  │    IChromaDbService       │
                     │  (Read External DB) │    (Local DB Operations)  │
                     └─────────────────────┴───────────────────────────┘
```

### 2.2 New Interfaces

#### 2.2.1 IExternalChromaDbReader

```csharp
/// <summary>
/// Interface for reading from external ChromaDB databases (read-only access)
/// </summary>
public interface IExternalChromaDbReader
{
    /// <summary>
    /// Validates that the specified path contains a valid ChromaDB database
    /// </summary>
    Task<ExternalDbValidationResult> ValidateExternalDbAsync(string dbPath);

    /// <summary>
    /// Lists all collections in the external database
    /// </summary>
    Task<List<ExternalCollectionInfo>> ListExternalCollectionsAsync(string dbPath);

    /// <summary>
    /// Gets documents from an external collection with optional filtering
    /// </summary>
    Task<List<ExternalDocument>> GetExternalDocumentsAsync(
        string dbPath,
        string collectionName,
        List<string>? documentIdPatterns = null);

    /// <summary>
    /// Gets collection metadata from external database
    /// </summary>
    Task<Dictionary<string, object>?> GetExternalCollectionMetadataAsync(
        string dbPath,
        string collectionName);
}
```

#### 2.2.2 IImportAnalyzer

```csharp
/// <summary>
/// Analyzes import operations and detects potential conflicts
/// </summary>
public interface IImportAnalyzer
{
    /// <summary>
    /// Analyzes an import operation and returns preview information including conflicts
    /// </summary>
    Task<ImportPreviewResult> AnalyzeImportAsync(
        string sourcePath,
        ImportFilter? filter = null);

    /// <summary>
    /// Gets detailed conflict information for a specific collection pair
    /// </summary>
    Task<List<ImportConflictInfo>> GetDetailedImportConflictsAsync(
        string sourcePath,
        string sourceCollection,
        string targetCollection);

    /// <summary>
    /// Determines if a conflict can be auto-resolved
    /// </summary>
    Task<bool> CanAutoResolveImportConflictAsync(ImportConflictInfo conflict);
}
```

#### 2.2.3 IImportExecutor

```csharp
/// <summary>
/// Executes import operations with conflict resolution
/// </summary>
public interface IImportExecutor
{
    /// <summary>
    /// Executes an import operation with the specified resolutions
    /// </summary>
    Task<ImportExecutionResult> ExecuteImportAsync(
        string sourcePath,
        ImportFilter? filter = null,
        List<ImportConflictResolution>? resolutions = null,
        bool autoResolveRemaining = true,
        string defaultStrategy = "keep_source");

    /// <summary>
    /// Resolves a single import conflict
    /// </summary>
    Task<bool> ResolveImportConflictAsync(
        ImportConflictInfo conflict,
        ImportConflictResolution resolution);

    /// <summary>
    /// Auto-resolves conflicts based on default strategy
    /// </summary>
    Task<int> AutoResolveImportConflictsAsync(
        List<ImportConflictInfo> conflicts,
        string strategy = "keep_source");
}
```

### 2.3 New Models

#### 2.3.1 Import Filter Models

```csharp
/// <summary>
/// Represents an import filter configuration parsed from JSON
/// </summary>
public record ImportFilter
{
    /// <summary>
    /// Collection import specifications as an array.
    /// Array format enables:
    /// - Wildcard matching on collection names
    /// - Multiple source collections mapping to the same target
    /// - Ordered processing of collection mappings
    /// </summary>
    public List<CollectionImportSpec>? Collections { get; init; }

    /// <summary>
    /// Returns true if this is an empty filter (import all)
    /// </summary>
    public bool IsImportAll => Collections == null || Collections.Count == 0;

    /// <summary>
    /// Gets all unique target collection names from the filter
    /// </summary>
    public HashSet<string> GetTargetCollections()
    {
        if (Collections == null) return new HashSet<string>();
        return Collections.Select(c => c.ImportInto).ToHashSet();
    }
}

/// <summary>
/// Specification for importing collection(s) - supports wildcards in name
/// </summary>
public record CollectionImportSpec
{
    /// <summary>
    /// Source collection name pattern in external database.
    /// Supports wildcards: * matches zero or more characters.
    /// Examples: "project_*", "*_docs", "archive_2024_*"
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Target collection name in local database.
    /// Multiple source collections can map to the same target,
    /// enabling collection consolidation during import.
    /// </summary>
    public string ImportInto { get; init; } = string.Empty;

    /// <summary>
    /// Document ID patterns to import (null = all documents).
    /// Supports wildcards: * matches zero or more characters.
    /// Examples: ["*_summary", "doc_*", "specific_doc_id"]
    /// </summary>
    public List<string>? Documents { get; init; }

    /// <summary>
    /// Returns true if the Name field contains wildcards
    /// </summary>
    public bool HasCollectionWildcard => Name.Contains('*');

    /// <summary>
    /// Returns true if document filtering is specified
    /// </summary>
    public bool HasDocumentFilter => Documents != null && Documents.Count > 0;
}
```

#### 2.3.2 External Data Models

```csharp
/// <summary>
/// Result of validating an external ChromaDB database
/// </summary>
public record ExternalDbValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public string DbPath { get; init; } = string.Empty;
    public int CollectionCount { get; init; }
    public long TotalDocuments { get; init; }
}

/// <summary>
/// Information about a collection in an external database
/// </summary>
public record ExternalCollectionInfo
{
    public string Name { get; init; } = string.Empty;
    public int DocumentCount { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// A document from an external ChromaDB database
/// </summary>
public record ExternalDocument
{
    public string DocId { get; init; } = string.Empty;
    public string CollectionName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public Dictionary<string, object>? Metadata { get; init; }
}
```

#### 2.3.3 Conflict Models

```csharp
/// <summary>
/// Types of import conflicts that can occur
/// </summary>
public enum ImportConflictType
{
    /// <summary>Document exists in both source and target with different content</summary>
    ContentModification,

    /// <summary>Document exists in both with different metadata only</summary>
    MetadataConflict,

    /// <summary>Collection exists in target with different structure/metadata</summary>
    CollectionMismatch,

    /// <summary>Document ID collision with different base documents</summary>
    IdCollision
}

/// <summary>
/// Detailed information about an import conflict
/// </summary>
public record ImportConflictInfo
{
    /// <summary>Unique identifier for this conflict (for resolution reference)</summary>
    public string ConflictId { get; init; } = string.Empty;

    /// <summary>Source collection name in external database</summary>
    public string SourceCollection { get; init; } = string.Empty;

    /// <summary>Target collection name in local database</summary>
    public string TargetCollection { get; init; } = string.Empty;

    /// <summary>Document ID involved in conflict</summary>
    public string DocumentId { get; init; } = string.Empty;

    /// <summary>Type of conflict</summary>
    public ImportConflictType Type { get; init; }

    /// <summary>Whether this conflict can be auto-resolved</summary>
    public bool AutoResolvable { get; init; }

    /// <summary>Content from source (external) database</summary>
    public string? SourceContent { get; init; }

    /// <summary>Content from target (local) database</summary>
    public string? TargetContent { get; init; }

    /// <summary>Content hash from source</summary>
    public string? SourceContentHash { get; init; }

    /// <summary>Content hash from target</summary>
    public string? TargetContentHash { get; init; }

    /// <summary>Suggested resolution strategy</summary>
    public string? SuggestedResolution { get; init; }

    /// <summary>Available resolution options</summary>
    public List<string> ResolutionOptions { get; init; } = new();
}

/// <summary>
/// Resolution types for import conflicts
/// </summary>
public enum ImportResolutionType
{
    /// <summary>Keep the source (external) version</summary>
    KeepSource,

    /// <summary>Keep the target (local) version</summary>
    KeepTarget,

    /// <summary>Merge fields from both versions</summary>
    Merge,

    /// <summary>Skip this document entirely</summary>
    Skip,

    /// <summary>Apply custom content</summary>
    Custom
}

/// <summary>
/// Resolution specification for an import conflict
/// </summary>
public record ImportConflictResolution
{
    /// <summary>ID of the conflict to resolve</summary>
    public string ConflictId { get; init; } = string.Empty;

    /// <summary>Type of resolution to apply</summary>
    public ImportResolutionType ResolutionType { get; init; }

    /// <summary>Custom content (when ResolutionType is Custom)</summary>
    public string? CustomContent { get; init; }

    /// <summary>Custom metadata (when ResolutionType is Custom)</summary>
    public Dictionary<string, object>? CustomMetadata { get; init; }
}
```

#### 2.3.4 Result Models

```csharp
/// <summary>
/// Result of analyzing an import operation
/// </summary>
public record ImportPreviewResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Path to the source database</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>Whether the import can proceed without conflicts</summary>
    public bool CanAutoImport { get; init; }

    /// <summary>Total conflicts detected</summary>
    public int TotalConflicts { get; init; }

    /// <summary>Conflicts that can be auto-resolved</summary>
    public int AutoResolvableConflicts { get; init; }

    /// <summary>Conflicts requiring manual resolution</summary>
    public int ManualConflicts { get; init; }

    /// <summary>Detailed conflict information</summary>
    public List<ImportConflictInfo> Conflicts { get; init; } = new();

    /// <summary>Preview of changes that will be made</summary>
    public ImportChangesPreview? Preview { get; init; }

    /// <summary>Recommended action based on analysis</summary>
    public string? RecommendedAction { get; init; }

    /// <summary>Human-readable message</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Preview of changes from an import operation
/// </summary>
public record ImportChangesPreview
{
    public int DocumentsToAdd { get; init; }
    public int DocumentsToUpdate { get; init; }
    public int DocumentsToSkip { get; init; }
    public int CollectionsToCreate { get; init; }
    public int CollectionsToUpdate { get; init; }
    public List<string> AffectedCollections { get; init; } = new();
}

/// <summary>
/// Result of executing an import operation
/// </summary>
public record ImportExecutionResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Number of documents successfully imported</summary>
    public int DocumentsImported { get; init; }

    /// <summary>Number of documents updated (due to conflict resolution)</summary>
    public int DocumentsUpdated { get; init; }

    /// <summary>Number of documents skipped</summary>
    public int DocumentsSkipped { get; init; }

    /// <summary>Number of collections created</summary>
    public int CollectionsCreated { get; init; }

    /// <summary>Number of conflicts resolved</summary>
    public int ConflictsResolved { get; init; }

    /// <summary>Breakdown of resolution types used</summary>
    public Dictionary<string, int>? ResolutionBreakdown { get; init; }

    /// <summary>Commit hash after import (if staged to Dolt)</summary>
    public string? CommitHash { get; init; }

    /// <summary>Human-readable summary</summary>
    public string? Message { get; init; }
}
```

---

## 3. Tool Implementation Design

### 3.1 PreviewImportTool

```csharp
/// <summary>
/// MCP tool for previewing import operations before execution.
/// Provides detailed conflict analysis and change preview.
/// </summary>
[McpServerToolType]
public class PreviewImportTool
{
    private readonly ILogger<PreviewImportTool> _logger;
    private readonly IImportAnalyzer _importAnalyzer;
    private readonly IExternalChromaDbReader _externalDbReader;

    /// <summary>
    /// Preview an import operation to see potential conflicts and changes before executing.
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
        // Implementation follows PreviewDoltMergeTool pattern
    }
}
```

#### PreviewImport Response Structure

```json
{
  "success": true,
  "source_path": "/path/to/external/chromadb",
  "source_validation": {
    "is_valid": true,
    "collection_count": 3,
    "total_documents": 150
  },
  "can_auto_import": false,
  "import_preview": {
    "has_conflicts": true,
    "total_conflicts": 5,
    "auto_resolvable": 3,
    "requires_manual": 2,
    "affected_collections": ["collection_a", "collection_b"],
    "changes_preview": {
      "documents_to_add": 120,
      "documents_to_update": 25,
      "documents_to_skip": 5,
      "collections_to_create": 1,
      "collections_to_update": 2
    }
  },
  "conflicts": [
    {
      "conflict_id": "imp_abc123def456",
      "source_collection": "external_docs",
      "target_collection": "local_docs",
      "document_id": "doc_001",
      "conflict_type": "content_modification",
      "auto_resolvable": false,
      "source_content_hash": "sha256:abc...",
      "target_content_hash": "sha256:def...",
      "suggested_resolution": "keep_source",
      "resolution_options": ["keep_source", "keep_target", "merge", "skip"]
    }
  ],
  "recommended_action": "Review 2 manual conflicts before proceeding",
  "message": "Import preview complete: 5 conflicts detected (3 auto-resolvable)"
}
```

### 3.2 ExecuteImportTool

```csharp
/// <summary>
/// MCP tool for executing import operations with conflict resolution.
/// Supports automatic resolution and custom conflict handling.
/// </summary>
[McpServerToolType]
public class ExecuteImportTool
{
    private readonly ILogger<ExecuteImportTool> _logger;
    private readonly IImportExecutor _importExecutor;
    private readonly IImportAnalyzer _importAnalyzer;
    private readonly ISyncManagerV2 _syncManager;

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
    [Description("Execute an import operation from an external ChromaDB database. Use preview_import first to identify conflicts.")]
    public async Task<object> ExecuteImport(
        string filepath,
        string? filter = null,
        string? conflict_resolutions = null,
        bool auto_resolve_remaining = true,
        string default_strategy = "keep_source",
        bool stage_to_dolt = true,
        string? commit_message = null)
    {
        // Implementation follows ExecuteDoltMergeTool pattern
    }
}
```

#### Conflict Resolution JSON Format

```json
// Format 1: Structured (recommended)
{
  "resolutions": [
    {
      "conflict_id": "imp_abc123def456",
      "resolution_type": "keep_source"
    },
    {
      "conflict_id": "imp_def789ghi012",
      "resolution_type": "keep_target"
    }
  ],
  "default_strategy": "keep_source"
}

// Format 2: Simple dictionary
{
  "imp_abc123def456": "keep_source",
  "imp_def789ghi012": "keep_target",
  "default_strategy": "skip"
}
```

#### ExecuteImport Response Structure

```json
{
  "success": true,
  "import_result": {
    "source_path": "/path/to/external/chromadb",
    "documents_imported": 120,
    "documents_updated": 25,
    "documents_skipped": 5,
    "collections_created": 1,
    "conflicts_total": 5,
    "conflicts_resolved": 5,
    "resolution_breakdown": {
      "keep_source": 3,
      "keep_target": 1,
      "skip": 1
    }
  },
  "dolt_staging": {
    "staged": true,
    "documents_staged": 145,
    "commit_hash": "abc123def456..."
  },
  "message": "Successfully imported 145 documents from external database"
}
```

---

## 4. Service Implementation Details

### 4.1 External ChromaDB Reader Implementation

The `ExternalChromaDbReader` will use Python.NET to instantiate a separate ChromaDB client pointed at the external database path.

```csharp
public class ExternalChromaDbReader : IExternalChromaDbReader
{
    private readonly ILogger<ExternalChromaDbReader> _logger;

    public async Task<ExternalDbValidationResult> ValidateExternalDbAsync(string dbPath)
    {
        // 1. Check if path exists and contains chroma.sqlite3
        // 2. Attempt to create PersistentClient with the path
        // 3. List collections to verify database integrity
        // 4. Return validation result with statistics
    }

    public async Task<List<ExternalDocument>> GetExternalDocumentsAsync(
        string dbPath,
        string collectionName,
        List<string>? documentIdPatterns = null)
    {
        // 1. Create PersistentClient pointing to external path
        // 2. Get collection by name
        // 3. If patterns provided, apply wildcard filtering
        // 4. Return documents with content, metadata, and computed hashes
    }
}
```

### 4.2 Import Analyzer Implementation

```csharp
public class ImportAnalyzer : IImportAnalyzer
{
    public async Task<ImportPreviewResult> AnalyzeImportAsync(
        string sourcePath, ImportFilter? filter = null)
    {
        // 1. Validate external database
        // 2. Parse filter to determine collection mappings
        // 3. For each source collection:
        //    a. Get documents from external DB
        //    b. Apply document patterns if specified
        //    c. Compare with local collection (if exists)
        //    d. Detect conflicts by comparing content hashes
        // 4. Generate conflict IDs (consistent across preview/execute)
        // 5. Build preview statistics
        // 6. Return comprehensive preview result
    }

    private string GenerateImportConflictId(
        string sourceCollection,
        string targetCollection,
        string documentId,
        ImportConflictType type)
    {
        // Generate deterministic conflict ID using:
        // Hash of: sourceCollection + targetCollection + documentId + type
        // Prefix with "imp_" for import conflicts
    }
}
```

### 4.3 Import Executor Implementation

**CRITICAL**: The ImportExecutor MUST use `IChromaDbService` for all document operations - never directly access ChromaDB.

```csharp
public class ImportExecutor : IImportExecutor
{
    private readonly IChromaDbService _chromaService;  // REQUIRED - use this for all operations
    private readonly IExternalChromaDbReader _externalReader;
    private readonly IImportAnalyzer _analyzer;
    private readonly ILogger<ImportExecutor> _logger;

    public async Task<ImportExecutionResult> ExecuteImportAsync(
        string sourcePath,
        ImportFilter? filter = null,
        List<ImportConflictResolution>? resolutions = null,
        bool autoResolveRemaining = true,
        string defaultStrategy = "keep_source")
    {
        // 1. Re-analyze to get current conflict state
        var preview = await _analyzer.AnalyzeImportAsync(sourcePath, filter);

        // 2. Apply explicit resolutions and auto-resolve remaining
        var resolvedConflicts = ApplyResolutions(preview.Conflicts, resolutions, defaultStrategy);

        // 3. Group documents by TARGET collection for batch import
        //    CRITICAL: Multiple source collections may map to same target!
        var documentsByTargetCollection = new Dictionary<string, List<(string id, string content, Dictionary<string, object> metadata)>>();

        // 4. For each collection spec in filter:
        foreach (var spec in filter?.Collections ?? GetAllCollectionSpecs())
        {
            var sourceCollections = MatchCollectionsByPattern(spec.Name);
            foreach (var sourceCol in sourceCollections)
            {
                var docs = await _externalReader.GetExternalDocumentsAsync(sourcePath, sourceCol, spec.Documents);

                // Add to target collection batch (consolidation support)
                if (!documentsByTargetCollection.ContainsKey(spec.ImportInto))
                    documentsByTargetCollection[spec.ImportInto] = new();

                foreach (var doc in docs)
                {
                    // Apply conflict resolution if applicable
                    if (ShouldSkipDocument(doc, resolvedConflicts)) continue;

                    var metadata = BuildImportMetadata(doc, sourcePath, sourceCol);
                    documentsByTargetCollection[spec.ImportInto].Add((doc.DocId, doc.Content, metadata));
                }
            }
        }

        // 5. CRITICAL: Batch import per target collection
        //    Uses IChromaDbService.AddDocumentsAsync for:
        //    - Automatic chunking (512 tokens)
        //    - Single embedding calculation per collection
        //    - Proper metadata (is_local_change=true, chunk info)
        foreach (var (targetCollection, documents) in documentsByTargetCollection)
        {
            // Ensure collection exists
            await _chromaService.CreateCollectionAsync(targetCollection);

            // BATCH ADD - all documents at once!
            var ids = documents.Select(d => d.id).ToList();
            var contents = documents.Select(d => d.content).ToList();
            var metadatas = documents.Select(d => d.metadata).ToList();

            await _chromaService.AddDocumentsAsync(
                targetCollection,
                contents,
                ids,
                metadatas,
                allowDuplicateIds: false,
                markAsLocalChange: true  // CRITICAL for commit detection
            );
        }

        // 6. Return execution result with statistics
    }

    private Dictionary<string, object> BuildImportMetadata(
        ExternalDocument doc, string sourcePath, string sourceCollection)
    {
        var metadata = new Dictionary<string, object>(doc.Metadata ?? new());
        metadata["import_source"] = sourcePath;
        metadata["import_source_collection"] = sourceCollection;
        metadata["import_timestamp"] = DateTime.UtcNow.ToString("O");
        // Note: is_local_change is set by AddDocumentsAsync
        return metadata;
    }
}
```

**Key Implementation Notes:**

1. **Batch by Target Collection**: Group all documents destined for the same target collection, then add them in ONE batch call
2. **Never Add Documents Individually**: Each call to `AddDocumentsAsync` triggers embedding recalculation
3. **Use IChromaDbService**: This handles chunking, metadata, and batch operations correctly
4. **Collection Consolidation**: Multiple source collections can feed into one target - batch them together

---

## 5. Implementation Phases

### Phase 1: Core Infrastructure
1. Create model files (ImportModels.cs)
2. Create IExternalChromaDbReader interface and implementation
3. Add wildcard pattern matching utility

### Phase 2: Analysis Service
1. Create IImportAnalyzer interface
2. Implement ImportAnalyzer service
3. Implement conflict ID generation (deterministic)

### Phase 3: Execution Service
1. Create IImportExecutor interface
2. Implement ImportExecutor service
3. Implement resolution handling

### Phase 4: MCP Tools
1. Implement PreviewImportTool
2. Implement ExecuteImportTool
3. Register tools in DI container

### Phase 5: Testing
1. Unit tests for pattern matching
2. Unit tests for conflict detection
3. Integration tests for full import workflow
4. End-to-end tests with real databases

### Phase 6: Documentation
1. Update tool documentation
2. Add usage examples
3. Update BasePrompt if needed

---

## 6. Test Descriptions

### 6.1 Unit Tests

| Test Name | Description |
|-----------|-------------|
| `ImportFilter_ParseEmptyFilter_ReturnsImportAll` | Verify empty JSON filter results in ImportAll mode |
| `ImportFilter_ParseCollectionArray_ExtractsCorrectly` | Verify collection array is parsed correctly |
| `ImportFilter_ParseDocumentPatterns_SupportsWildcards` | Verify wildcard patterns are parsed and applied |
| `ImportFilter_MultipleSourcesSameTarget_ParsesCorrectly` | Verify multiple collections can map to same target |
| `ImportFilter_GetTargetCollections_ReturnsUniqueSet` | Verify unique target collections are extracted |
| `WildcardMatcher_AsteriskPrefix_MatchesSuffix` | Verify `*_summary` matches `doc_summary` |
| `WildcardMatcher_AsteriskSuffix_MatchesPrefix` | Verify `doc_*` matches `doc_123` |
| `WildcardMatcher_MiddleWildcard_MatchesPattern` | Verify `project_*_docs` matches `project_alpha_docs` |
| `WildcardMatcher_ExactMatch_MatchesExactly` | Verify `doc_123` only matches `doc_123` |
| `WildcardMatcher_CollectionPattern_MatchesMultiple` | Verify `archive_*` matches multiple collection names |
| `ConflictIdGeneration_SameInput_ReturnsSameId` | Verify conflict IDs are deterministic |
| `ConflictIdGeneration_DifferentInput_ReturnsDifferentId` | Verify unique inputs produce unique IDs |

### 6.2 Integration Tests

| Test Name | Description |
|-----------|-------------|
| `ExternalDbReader_ValidPath_ReturnsValidResult` | Verify external DB validation works correctly |
| `ExternalDbReader_InvalidPath_ReturnsError` | Verify invalid paths are handled gracefully |
| `ExternalDbReader_GetDocuments_ReturnsAllDocuments` | Verify document retrieval works correctly |
| `ExternalDbReader_GetDocuments_AppliesPatternFilter` | Verify document pattern filtering works |
| `ExternalDbReader_ListCollections_WithWildcard_MatchesMultiple` | Verify collection wildcard matching works |
| `ImportAnalyzer_NoConflicts_ReturnsCleanPreview` | Verify preview when no conflicts exist |
| `ImportAnalyzer_WithConflicts_DetectsAllConflicts` | Verify all conflicts are detected |
| `ImportAnalyzer_ConflictIds_ConsistentAcrossCalls` | Verify conflict IDs are consistent |
| `ImportAnalyzer_MultipleSourcesSameTarget_AggregatesCorrectly` | Verify multi-source consolidation preview |
| `ImportExecutor_NoConflicts_ImportsSuccessfully` | Verify clean import execution |
| `ImportExecutor_WithResolutions_AppliesCorrectly` | Verify resolutions are applied correctly |
| `ImportExecutor_AutoResolve_UsesDefaultStrategy` | Verify auto-resolution uses specified strategy |
| `ImportExecutor_CollectionConsolidation_MergesDocuments` | Verify multiple sources merge into single target |
| `ImportExecutor_UsesBatchAdd_NotIndividualAdds` | Verify AddDocumentsAsync is called once per target collection (not per document) |
| `ImportExecutor_SetsProperMetadata_IsLocalChangeTrue` | Verify imported documents have is_local_change=true metadata |
| `ImportExecutor_DocumentsProperlyChunked_AfterImport` | Verify imported documents are chunked with proper chunk metadata |

### 6.3 End-to-End Tests

| Test Name | Description |
|-----------|-------------|
| `PreviewImport_ValidDatabase_ReturnsPreview` | Full preview flow test |
| `ExecuteImport_AfterPreview_UsesCorrectConflictIds` | Verify conflict IDs match between preview and execute |
| `ImportWorkflow_FullCycle_StagesToDolt` | Complete import with Dolt staging |
| `ImportWorkflow_CollectionMapping_CreatesCorrectly` | Verify collection mapping works end-to-end |
| `ImportWorkflow_DocumentFiltering_ImportsCorrectSubset` | Verify document filtering works end-to-end |
| `ImportWorkflow_CollectionWildcard_MatchesAndImports` | Verify collection wildcard matching in full workflow |
| `ImportWorkflow_CollectionConsolidation_MergesMultipleSources` | Verify multiple collections merge into one target |

---

## 7. Risk Analysis

| Risk | Mitigation |
|------|------------|
| External DB locked by another process | Use read-only access, handle lock exceptions gracefully |
| Large imports causing memory issues | Implement batch processing with configurable batch size |
| Conflict ID mismatch (like PP13-73) | Use deterministic ID generation with same inputs |
| External DB schema incompatibility | Validate schema before import, provide clear error messages |
| Python.NET context conflicts | Use separate context or careful resource management |

---

## 8. Future Extensibility

### 8.1 Additional Import Sources (Future)

The architecture supports adding new import sources:

```csharp
public interface IImportSourceReader
{
    Task<ImportSourceValidationResult> ValidateSourceAsync(string path);
    Task<List<ImportDocument>> ReadDocumentsAsync(string path, ImportFilter? filter);
}

// Future implementations:
// - MarkdownImportReader (reads .md files from directory)
// - DocxImportReader (reads .docx files)
// - CsvImportReader (reads CSV/TSV files)
// - JsonImportReader (reads JSON document arrays)
```

### 8.2 Configuration Options (Future)

```csharp
public record ImportConfiguration
{
    public int BatchSize { get; init; } = 100;
    public bool PreserveOriginalIds { get; init; } = true;
    public bool GenerateEmbeddings { get; init; } = true;
    public string DefaultConflictStrategy { get; init; } = "keep_source";
    public bool AutoStageToChroma { get; init; } = true;
    public bool AutoStageToDolt { get; init; } = true;
}
```

---

## 9. Dependencies

### 9.1 Existing Services Used

| Service | Usage |
|---------|-------|
| `IChromaDbService` | Local ChromaDB operations |
| `ISyncManagerV2` | Staging imports to Dolt |
| `ILogger<T>` | Logging throughout |
| `PythonContext` | Python.NET for external ChromaDB access |

### 9.2 New Dependencies

None - uses existing Python.NET infrastructure

---

## 10. Success Criteria

1. **PreviewImport tool** correctly identifies all conflicts between external and local databases
2. **ExecuteImport tool** successfully imports documents with proper conflict resolution
3. **Conflict IDs are consistent** between preview and execute operations
4. **Filter system** correctly applies collection mappings, wildcards, and document patterns
5. **Collection consolidation** works correctly (multiple sources to single target)
6. **Batch operations used correctly** - `AddDocumentsAsync` called once per target collection, not per document
7. **Proper metadata set** - All imported documents have `is_local_change=true` and chunk metadata
8. **All unit tests pass** (minimum 12 unit tests)
9. **All integration tests pass** (minimum 16 integration tests)
10. **End-to-end tests pass** (minimum 7 E2E tests)
11. **Build succeeds** with 0 errors
12. **Documentation complete** for both tools

---

## Appendix A: File Structure

```
multidolt-mcp/
├── Models/
│   └── ImportModels.cs          # All import-related models
├── Services/
│   ├── IExternalChromaDbReader.cs
│   ├── ExternalChromaDbReader.cs
│   ├── IImportAnalyzer.cs
│   ├── ImportAnalyzer.cs
│   ├── IImportExecutor.cs
│   └── ImportExecutor.cs
├── Tools/
│   ├── PreviewImportTool.cs
│   └── ExecuteImportTool.cs
└── Utilities/
    └── WildcardMatcher.cs       # Pattern matching utility

multidolt-mcp-testing/
├── UnitTests/
│   ├── ImportFilterTests.cs
│   ├── WildcardMatcherTests.cs
│   └── ImportConflictIdTests.cs
└── IntegrationTests/
    ├── ExternalChromaDbReaderTests.cs
    ├── ImportAnalyzerTests.cs
    ├── ImportExecutorTests.cs
    └── PP13_75_ImportToolIntegrationTests.cs
```

---

*Document Version: 1.0*
*Last Updated: 2026-01-13*
