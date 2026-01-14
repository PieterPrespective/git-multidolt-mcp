# PP13-75 Assignment: Import Toolset Implementation

- IssueID = PP13-75
- Please read 'Prompts/BasePrompt.md' first for general context
- Please read 'Prompts/PP13-75/Design.md' for the complete design specification
- For implementation patterns, refer to:
  - `multidolt-mcp/Tools/PreviewDoltMergeTool.cs` - Pattern for preview tool structure
  - `multidolt-mcp/Tools/ExecuteDoltMergeTool.cs` - Pattern for execution tool structure
  - `multidolt-mcp/Services/ConflictAnalyzer.cs` - Pattern for conflict detection
  - `multidolt-mcp/Services/MergeConflictResolver.cs` - Pattern for resolution handling

## Problem Statement

DMMS currently lacks the ability to import content from external ChromaDB databases. This feature is essential for:
- Data migration from existing vector databases
- Collaboration workflows between different DMMS instances
- Integration with third-party ChromaDB-based applications

## Assignment Objectives

### Primary Goals

1. **Implement PreviewImportTool** - MCP tool for previewing import operations
2. **Implement ExecuteImportTool** - MCP tool for executing imports with conflict resolution
3. **Implement IExternalChromaDbReader** - Service for reading external ChromaDB databases
4. **Implement IImportAnalyzer** - Service for analyzing imports and detecting conflicts
5. **Implement IImportExecutor** - Service for executing imports with resolutions
6. **Create comprehensive test suite** - Unit, integration, and E2E tests

### Success Criteria

```
Build Status: 0 errors
Unit Tests: 12+ tests passing
Integration Tests: 16+ tests passing
E2E Tests: 7+ tests passing
Total Tests: 35+ tests passing
```

**Critical Validation Points:**
- Batch operations used (AddDocumentsAsync called once per collection, not per document)
- Proper metadata (is_local_change=true, chunk metadata present)
- Documents properly chunked after import

## Implementation Requirements

### Phase 1: Core Infrastructure

**Files to Create:**
- `multidolt-mcp/Models/ImportModels.cs` - All import-related data models
- `multidolt-mcp/Utilities/WildcardMatcher.cs` - Pattern matching utility
- `multidolt-mcp/Services/IExternalChromaDbReader.cs` - Interface
- `multidolt-mcp/Services/ExternalChromaDbReader.cs` - Implementation

**Key Implementations:**

1. **Import Filter Parsing (Array-Based Collections)**

   The `collections` field is an **array** to support:
   - Wildcard matching on collection names
   - Multiple source collections mapping to the same target (consolidation)
   - Ordered processing of collection mappings

   ```json
   // Empty filter - import all
   {}

   // Collection mapping with wildcards
   {
     "collections": [
       { "name": "project_*_docs", "import_into": "all_project_docs" }
     ]
   }

   // Multiple sources to single target (consolidation)
   {
     "collections": [
       { "name": "archive_2024_*", "import_into": "consolidated_archive" },
       { "name": "archive_2025_*", "import_into": "consolidated_archive" }
     ]
   }

   // With document filtering
   {
     "collections": [
       {
         "name": "remote_collection",
         "import_into": "local_collection",
         "documents": ["*_summary", "doc_*"]
       }
     ]
   }
   ```

2. **External Database Access**
   - Validate external database path exists and is valid ChromaDB
   - Create read-only PersistentClient for external database
   - List collections and retrieve documents
   - Apply collection name wildcard matching

3. **Wildcard Pattern Matching (for both collections AND documents)**
   - Support prefix wildcards: `*_summary` matches `doc_summary`
   - Support suffix wildcards: `doc_*` matches `doc_123`
   - Support middle wildcards: `project_*_docs` matches `project_alpha_docs`
   - Support exact matches: `doc_123` matches only `doc_123`

### Phase 2: Analysis Service

**Files to Create:**
- `multidolt-mcp/Services/IImportAnalyzer.cs` - Interface
- `multidolt-mcp/Services/ImportAnalyzer.cs` - Implementation

**Key Implementations:**

1. **Conflict Detection**
   - Compare documents by content hash (SHA-256)
   - Detect content modifications, metadata conflicts, ID collisions
   - Generate deterministic conflict IDs using: `Hash(sourceCol + targetCol + docId + type)`

2. **Import Preview Generation**
   - Count documents to add, update, skip
   - Identify collections to create or update
   - Provide clear conflict summaries

**CRITICAL: Conflict ID Consistency**

Based on PP13-73 lessons learned, ensure conflict IDs are generated identically in both PreviewImportTool and ExecuteImportTool:

```csharp
private string GenerateImportConflictId(
    string sourceCollection,
    string targetCollection,
    string documentId,
    ImportConflictType type)
{
    var input = $"{sourceCollection}_{targetCollection}_{documentId}_{type}";
    using var sha256 = SHA256.Create();
    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
    var hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    return $"imp_{hashHex[..12]}";
}
```

### Phase 3: Execution Service

**Files to Create:**
- `multidolt-mcp/Services/IImportExecutor.cs` - Interface
- `multidolt-mcp/Services/ImportExecutor.cs` - Implementation

**Key Implementations:**

1. **Resolution Application**
   - Apply explicit resolutions by conflict ID
   - Auto-resolve remaining conflicts with default strategy
   - Support all resolution types: keep_source, keep_target, merge, skip, custom

2. **Document Import - CRITICAL REQUIREMENTS**

   **DO NOT directly copy documents into ChromaDB!** You MUST use `IChromaDbService.AddDocumentsAsync()`:

   ```csharp
   // WRONG - Extremely slow, recalculates embeddings for EACH document
   foreach (var doc in documents)
   {
       await chromaCollection.AddAsync(doc);  // DON'T DO THIS!
   }

   // CORRECT - Single embedding calculation, proper chunking
   await _chromaService.AddDocumentsAsync(
       targetCollection,
       contents,      // List<string> - all document contents
       ids,           // List<string> - all document IDs
       metadatas,     // List<Dictionary<string, object>> - all metadata
       allowDuplicateIds: false,
       markAsLocalChange: true
   );
   ```

   **Why this matters:**
   - `IChromaDbService.AddDocumentsAsync()` automatically chunks documents (512 tokens, 50 overlap)
   - Single batch = single embedding calculation (minutes vs hours for 1000+ docs)
   - Properly sets `is_local_change`, `source_id`, `chunk_index`, `total_chunks` metadata
   - Integrates with existing DMMS commit/sync infrastructure

   **Implementation Pattern:**
   ```csharp
   // Group ALL documents by target collection FIRST
   var docsByTarget = new Dictionary<string, List<DocumentData>>();

   foreach (var spec in filter.Collections)
   {
       foreach (var sourceCol in MatchWildcard(spec.Name))
       {
           var docs = await _externalReader.GetExternalDocumentsAsync(...);
           // Add to target batch (supports consolidation)
           docsByTarget[spec.ImportInto].AddRange(docs);
       }
   }

   // Then batch import per target collection
   foreach (var (target, docs) in docsByTarget)
   {
       await _chromaService.AddDocumentsAsync(target, docs.Contents, docs.Ids, docs.Metadatas);
   }
   ```

3. **Integration with Dolt Staging**
   - Optionally stage imported documents to Dolt
   - Generate commit message summarizing import

### Phase 4: MCP Tools

**Files to Create:**
- `multidolt-mcp/Tools/PreviewImportTool.cs`
- `multidolt-mcp/Tools/ExecuteImportTool.cs`

**PreviewImportTool Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filepath` | string | Yes | - | Path to external ChromaDB database |
| `filter` | string | No | `{}` | JSON filter for what to import |
| `include_content_preview` | bool | No | false | Include content in conflict details |

**ExecuteImportTool Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filepath` | string | Yes | - | Path to external ChromaDB database |
| `filter` | string | No | `{}` | JSON filter for what to import |
| `conflict_resolutions` | string | No | null | JSON with conflict resolutions |
| `auto_resolve_remaining` | bool | No | true | Auto-resolve unspecified conflicts |
| `default_strategy` | string | No | `keep_source` | Default auto-resolution strategy |
| `stage_to_dolt` | bool | No | true | Stage imports to Dolt |
| `commit_message` | string | No | null | Custom commit message |

**DI Registration in Program.cs:**
```csharp
builder.Services.AddSingleton<IExternalChromaDbReader, ExternalChromaDbReader>();
builder.Services.AddSingleton<IImportAnalyzer, ImportAnalyzer>();
builder.Services.AddSingleton<IImportExecutor, ImportExecutor>();
```

### Phase 5: Testing

**Files to Create:**
- `multidolt-mcp-testing/UnitTests/ImportFilterTests.cs`
- `multidolt-mcp-testing/UnitTests/WildcardMatcherTests.cs`
- `multidolt-mcp-testing/UnitTests/ImportConflictIdTests.cs`
- `multidolt-mcp-testing/IntegrationTests/ExternalChromaDbReaderTests.cs`
- `multidolt-mcp-testing/IntegrationTests/ImportAnalyzerTests.cs`
- `multidolt-mcp-testing/IntegrationTests/ImportExecutorTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_75_ImportToolIntegrationTests.cs`

**Required Test Coverage:**

Unit Tests (minimum 12):
- ImportFilter parsing (empty, array format, patterns)
- ImportFilter multiple sources to same target parsing
- ImportFilter GetTargetCollections unique set extraction
- WildcardMatcher (prefix, suffix, middle, exact)
- WildcardMatcher collection pattern matching
- Conflict ID generation (deterministic, unique)

Integration Tests (minimum 16):
- ExternalDbReader validation and document retrieval
- ExternalDbReader collection wildcard matching
- ImportAnalyzer conflict detection
- ImportAnalyzer multi-source consolidation preview
- ImportExecutor resolution application
- ImportExecutor collection consolidation
- ImportExecutor batch add verification (AddDocumentsAsync called once per target)
- ImportExecutor metadata verification (is_local_change=true)
- ImportExecutor chunking verification (proper chunk metadata after import)
- Full import workflow

E2E Tests (minimum 7):
- PreviewImport tool execution
- ExecuteImport tool execution
- Conflict ID consistency between preview and execute
- Collection mapping workflow
- Document filtering workflow
- Collection wildcard matching in full workflow
- Collection consolidation (multiple sources to one target)

## Technical Constraints

- **DO NOT** modify existing merge tools (PreviewDoltMergeTool, ExecuteDoltMergeTool)
- **MAINTAIN** compatibility with existing ISyncManagerV2 and IChromaDbService interfaces
- **PRESERVE** the external database as read-only (never write to source)
- **ENSURE** deterministic conflict IDs to avoid PP13-73 style issues
- **USE** existing PythonContext for ChromaDB access
- **CRITICAL: USE IChromaDbService.AddDocumentsAsync()** for ALL document imports:
  - Never directly access ChromaDB collections
  - Never add documents individually (causes massive performance issues)
  - Always batch documents per target collection
  - This ensures proper chunking, metadata, and embedding calculation

## Validation Process

1. Build the solution: `dotnet build` - expect 0 errors
2. Run unit tests: `dotnet test --filter "Category=Unit"`
3. Run integration tests: `dotnet test --filter "Category=Integration"`
4. Run E2E tests: `dotnet test --filter "FullyQualifiedName~PP13_75"`
5. Manual validation with test databases

## Expected Outcome

Two new MCP tools accessible to users:
- `preview_import` - Preview imports from external ChromaDB databases with conflict detection
- `execute_import` - Execute imports with full conflict resolution support

These tools follow the established patterns from the merge toolset (PP13-72, PP13-73) ensuring consistency in user experience and code architecture.

**Priority**: Medium - Enables important data migration and collaboration workflows

## Files to Create/Modify Summary

**New Files (11):**
- `multidolt-mcp/Models/ImportModels.cs`
- `multidolt-mcp/Utilities/WildcardMatcher.cs`
- `multidolt-mcp/Services/IExternalChromaDbReader.cs`
- `multidolt-mcp/Services/ExternalChromaDbReader.cs`
- `multidolt-mcp/Services/IImportAnalyzer.cs`
- `multidolt-mcp/Services/ImportAnalyzer.cs`
- `multidolt-mcp/Services/IImportExecutor.cs`
- `multidolt-mcp/Services/ImportExecutor.cs`
- `multidolt-mcp/Tools/PreviewImportTool.cs`
- `multidolt-mcp/Tools/ExecuteImportTool.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_75_ImportToolIntegrationTests.cs`

**Modified Files (1):**
- `multidolt-mcp/Program.cs` - Add DI registrations

Please log all development actions to the 'chroma-feat-design-planning-mcp' database under collection 'PP13-75' following the established pattern.
