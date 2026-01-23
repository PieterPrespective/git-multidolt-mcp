# PP13-81: Empty Repository Initialization Blocks Clone Operations

## Date: 2026-01-23
## Type: Bug Fix - Initialization Flow Correction
## Priority: High
## Depends On: PP13-79-C1 (Manifest Auto-Sync)

---

## Problem Statement

When DMMS starts **without** the `DOLT_REMOTE_URL` environment variable configured, it auto-initializes an empty Dolt repository. This empty repository then **blocks all subsequent `DoltClone` operations**, leaving users unable to configure a remote repository via MCP tools. The only recovery path is manual cleanup outside of DMMS.

**Current Behavior:**
- DMMS starts without `DOLT_REMOTE_URL`
- Auto-creates manifest with `remote_url: null`
- `DmmsInitializer.InitializeFromManifestAsync` initializes an empty Dolt repo (`dolt init`)
- Empty repo has no `documents` table, no remote configured
- User calls `DoltClone` with a remote URL
- `DoltCloneTool` returns error: "Repository already exists. Use dolt_reset or manual cleanup."
- User is stuck with no MCP-based recovery path

**Desired Behavior:**
- DMMS should NOT auto-initialize an empty repo when no remote URL is configured
- `DoltClone` should have a `--force` option to overwrite existing empty repositories
- New `ManifestSetRemote` tool should allow updating the remote URL in the manifest
- Better empty repo detection should allow clone to proceed when existing repo has no meaningful data

---

## Root Cause Analysis

### Flow Analysis

**Program.cs (lines 473-546)** - Manifest auto-creation:
```csharp
// PP13-79-C1: Auto-create manifest on first run
var remoteUrl = Environment.GetEnvironmentVariable("DOLT_REMOTE_URL");

// Create initial manifest (remoteUrl may be null!)
manifest = manifestService.CreateDefaultManifest(
    remoteUrl: remoteUrl,  // NULL if env var not set
    defaultBranch: "main",
    initMode: serverConfig.InitMode
);

// Then calls InitializeFromManifestAsync with potentially null remote
var result = await initializer.InitializeFromManifestAsync(manifest, projectRoot);
```

**DmmsInitializer.cs (lines 79-96)** - Empty repo initialization:
```csharp
else if (!repoExists)
{
    // No remote and no local repo - initialize empty
    _logger.LogInformation("No remote URL configured, initializing empty Dolt repo");

    var initResult = await _doltCli.InitAsync();  // PROBLEM: Creates orphan repo
    // ...
}
```

**DoltCloneTool.cs (lines 87-98)** - Clone blocked:
```csharp
// Check if already initialized
var isInitialized = await _doltCli.IsInitializedAsync();
if (isInitialized)
{
    return new
    {
        success = false,
        error = "ALREADY_INITIALIZED",  // BLOCKS clone even for empty repos
        message = "Repository already exists. Use dolt_reset or manual cleanup."
    };
}
```

### Cascading Errors

The empty repo also causes downstream errors in **SyncManagerV2.cs (lines 138-142)**:
```csharp
if (!collections.Any())
{
    _logger.LogInformation("No collections found, checking default collection");
    return await _chromaToDoltDetector.DetectLocalChangesAsync("default");  // Phantom collection
}
```

This results in repeated "Collection default does not exist" errors because:
1. Empty repo has no `documents` table (no schema)
2. Sync logic assumes "default" collection exists
3. ChromaDB has no "default" collection either

---

## Solution Overview

Implement a **four-part fix**:

1. **Prevent Empty Init**: Don't auto-initialize empty repo when no remote URL configured
2. **Add Force Clone**: Add `--force` option to `DoltClone` to overwrite existing empty repos
3. **Add ManifestSetRemote Tool**: Allow updating remote URL in manifest post-creation
4. **Improve Empty Detection**: Smart detection of truly empty repositories in clone logic

### Key Design Principles

- **Backwards Compatible**: Existing workflows with `DOLT_REMOTE_URL` continue to work
- **User-Friendly**: Clear error messages with actionable suggestions
- **Non-Destructive**: Force option requires explicit confirmation for repos with data
- **Recovery Path**: Users can always recover via MCP tools without manual intervention

---

## Architecture

### Modified Components

#### 1. DmmsInitializer.cs - Prevent Empty Init

**Current logic:**
```csharp
if (!repoExists && !string.IsNullOrEmpty(manifest.Dolt.RemoteUrl))
{
    // Clone from remote
}
else if (!repoExists)
{
    // Initialize empty (PROBLEM)
}
```

**New logic:**
```csharp
if (!repoExists && !string.IsNullOrEmpty(manifest.Dolt.RemoteUrl))
{
    // Clone from remote
}
else if (!repoExists)
{
    // No remote URL - don't initialize, wait for user configuration
    _logger.LogWarning("No Dolt repository found and no remote URL configured. " +
                       "Use DoltInit to create a local repo or DoltClone to clone from remote.");
    return new InitializationResult
    {
        Success = true,  // Not a failure - just pending configuration
        ActionTaken = InitializationAction.PendingConfiguration
    };
}
```

#### 2. DoltCloneTool.cs - Add Force Option

**New parameter:**
```csharp
[McpServerTool]
[Description("Clone an existing Dolt repository from DoltHub or another remote.")]
public virtual async Task<object> DoltClone(
    string remote_url,
    string? branch = null,
    string? commit = null,
    bool force = false)  // NEW: Force overwrite option
```

**Enhanced logic:**
```csharp
var isInitialized = await _doltCli.IsInitializedAsync();
if (isInitialized)
{
    if (force)
    {
        // Check if repo is truly empty (safe to overwrite)
        var isEmpty = await IsRepositoryEmptyAsync();

        if (isEmpty)
        {
            _logger.LogInformation("Force clone requested on empty repository, proceeding");
            await CleanupExistingRepositoryAsync();
        }
        else
        {
            // Has data - require explicit confirmation
            return new
            {
                success = false,
                error = "FORCE_REQUIRES_EMPTY",
                message = "Repository contains data. Use dolt_reset to clear first, or backup your data.",
                has_commits = true,
                suggestion = "Call dolt_reset to clear repository, then retry DoltClone"
            };
        }
    }
    else
    {
        // Provide helpful error with suggestions
        var isEmpty = await IsRepositoryEmptyAsync();
        return new
        {
            success = false,
            error = "ALREADY_INITIALIZED",
            message = isEmpty
                ? "Empty repository exists. Use force=true to overwrite, or dolt_reset to clear."
                : "Repository already exists with data. Use dolt_reset to clear first.",
            is_empty = isEmpty,
            suggestion = isEmpty
                ? "Retry with force=true to overwrite the empty repository"
                : "Use dolt_reset to clear the repository, then retry DoltClone"
        };
    }
}
```

**New helper methods:**
```csharp
/// <summary>
/// Checks if the existing Dolt repository is empty (no meaningful data).
/// An empty repo has:
/// - No commits beyond the initial auto-commit (or no commits at all)
/// - No tables with data (or no tables)
/// - No remote configured
/// </summary>
private async Task<bool> IsRepositoryEmptyAsync()
{
    try
    {
        // Check 1: Count commits
        var commits = await _doltCli.GetLogAsync(5);
        var commitList = commits?.ToList() ?? new List<CommitInfo>();

        // If more than 2 commits, likely has user data
        // (init creates 0-2 commits for schema setup)
        if (commitList.Count > 2)
            return false;

        // Check 2: Look for documents table with data
        try
        {
            var docCount = await _doltCli.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM documents");
            if (docCount > 0)
                return false;
        }
        catch
        {
            // No documents table = empty
        }

        // Check 3: Look for any user tables
        var tables = await _doltCli.QueryAsync<dynamic>("SHOW TABLES");
        var tableList = tables?.ToList() ?? new List<dynamic>();

        // Schema tables don't count as data
        var userTables = tableList.Where(t =>
        {
            var name = GetTableNameFromResult(t);
            return name != null &&
                   !name.StartsWith("__") &&  // Internal tables
                   name != "collections" &&    // Schema tables
                   name != "documents" &&
                   name != "chroma_sync_state" &&
                   name != "document_sync_log" &&
                   name != "local_changes" &&
                   name != "sync_operations";
        }).ToList();

        return userTables.Count == 0;
    }
    catch
    {
        // If we can't determine, assume not empty (safer)
        return false;
    }
}

/// <summary>
/// Cleans up an existing Dolt repository for fresh clone
/// </summary>
private async Task CleanupExistingRepositoryAsync()
{
    _logger.LogInformation("Cleaning up existing repository at: {Path}", _repositoryPath);

    var doltDir = Path.Combine(_repositoryPath, ".dolt");

    if (Directory.Exists(doltDir))
    {
        // Retry logic for file locking
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(doltDir, recursive: true);
                _logger.LogInformation("Successfully removed .dolt directory");
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(100 * (int)Math.Pow(2, attempt));
            }
        }
    }
}
```

#### 3. New Tool: ManifestSetRemoteTool.cs

**Create new file:**
```csharp
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;
using DMMS.Utilities;

namespace DMMS.Tools;

/// <summary>
/// PP13-81: MCP tool to update the remote URL in the DMMS manifest.
/// Allows configuration of remote repository without requiring restart.
/// </summary>
[McpServerToolType]
public class ManifestSetRemoteTool
{
    private readonly ILogger<ManifestSetRemoteTool> _logger;
    private readonly IDmmsStateManifest _manifestService;
    private readonly ISyncStateChecker _syncStateChecker;

    public ManifestSetRemoteTool(
        ILogger<ManifestSetRemoteTool> logger,
        IDmmsStateManifest manifestService,
        ISyncStateChecker syncStateChecker)
    {
        _logger = logger;
        _manifestService = manifestService;
        _syncStateChecker = syncStateChecker;
    }

    /// <summary>
    /// Update the remote URL in the DMMS manifest. After setting, use DoltClone to clone from the remote.
    /// </summary>
    [McpServerTool]
    [Description("Update the remote URL in the DMMS manifest. After setting, use DoltClone to clone from the remote.")]
    public async Task<object> ManifestSetRemote(string remote_url)
    {
        const string toolName = nameof(ManifestSetRemoteTool);
        const string methodName = nameof(ManifestSetRemote);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"remote_url: {remote_url}");

        try
        {
            if (string.IsNullOrWhiteSpace(remote_url))
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Remote URL is required");
                return new
                {
                    success = false,
                    error = "REMOTE_URL_REQUIRED",
                    message = "Remote URL is required"
                };
            }

            // Get project root
            var projectRoot = await _syncStateChecker.GetProjectRootAsync();
            if (string.IsNullOrEmpty(projectRoot))
            {
                projectRoot = Directory.GetCurrentDirectory();
            }

            // Read existing manifest
            var manifest = await _manifestService.ReadManifestAsync(projectRoot);

            if (manifest == null)
            {
                // Create new manifest with remote URL
                manifest = _manifestService.CreateDefaultManifest(
                    remoteUrl: remote_url,
                    defaultBranch: "main",
                    initMode: "auto"
                );
            }
            else
            {
                // Update existing manifest
                var updatedDolt = manifest.Dolt with
                {
                    RemoteUrl = remote_url
                };

                manifest = manifest with
                {
                    Dolt = updatedDolt,
                    UpdatedAt = DateTime.UtcNow
                };
            }

            // Write updated manifest
            await _manifestService.WriteManifestAsync(projectRoot, manifest);

            // Invalidate cache
            _syncStateChecker.InvalidateCache();

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName,
                $"Remote URL set to: {remote_url}");

            return new
            {
                success = true,
                message = $"Remote URL updated to: {remote_url}",
                manifest_path = _manifestService.GetManifestPath(projectRoot),
                next_step = "Use DoltClone to clone from the configured remote"
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to update manifest: {ex.Message}"
            };
        }
    }
}
```

#### 4. InitializationAction Enum Update

**Add new action to Models:**
```csharp
public static class InitializationAction
{
    public const string Skipped = "skipped";
    public const string SyncedExisting = "synced_existing";
    public const string CheckedOutBranch = "checked_out_branch";
    public const string CheckedOutCommit = "checked_out_commit";
    public const string Cloned = "cloned";
    public const string Failed = "failed";
    public const string PendingConfiguration = "pending_configuration";  // NEW
}
```

---

## Implementation Phases

### Phase 1: Prevent Empty Init (DmmsInitializer)
**Files to Modify:**
- `multidolt-mcp/Services/DmmsInitializer.cs`
- `multidolt-mcp/Models/InitializationModels.cs`

**Changes:**
1. Add `PendingConfiguration` to `InitializationAction`
2. Modify `InitializeFromManifestAsync` to return pending status instead of initializing empty repo
3. Update logging to provide clear guidance

### Phase 2: Force Clone Option (DoltCloneTool)
**Files to Modify:**
- `multidolt-mcp/Tools/DoltCloneTool.cs`

**Changes:**
1. Add `force` parameter to `DoltClone` method
2. Add `IsRepositoryEmptyAsync` helper method
3. Add `CleanupExistingRepositoryAsync` helper method
4. Update error responses with helpful suggestions

### Phase 3: ManifestSetRemote Tool
**Files to Create:**
- `multidolt-mcp/Tools/ManifestSetRemoteTool.cs`

**Files to Modify:**
- `multidolt-mcp/Program.cs` (register tool)

**Changes:**
1. Create new `ManifestSetRemoteTool` class
2. Register with `.WithTools<ManifestSetRemoteTool>()`

### Phase 4: Program.cs Startup Logic
**Files to Modify:**
- `multidolt-mcp/Program.cs`

**Changes:**
1. Handle `PendingConfiguration` result from initializer
2. Log clear guidance when no remote is configured

### Phase 5: Testing
**Files to Create:**
- `multidolt-mcp-testing/UnitTests/PP13_81_EmptyRepoTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_81_ForceCloneTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_81_ManifestSetRemoteTests.cs`

---

## Test Specifications

### Unit Tests (10+ tests)

**DmmsInitializer Tests:**
1. `InitializeFromManifest_NoRemoteUrl_ReturnsPendingConfiguration`
2. `InitializeFromManifest_WithRemoteUrl_ClonesFromRemote`
3. `InitializeFromManifest_NoRemote_DoesNotCallDoltInit`

**DoltCloneTool Tests:**
4. `DoltClone_ExistingRepo_ReturnsAlreadyInitialized`
5. `DoltClone_EmptyRepo_WithForce_CleansAndClones`
6. `DoltClone_RepoWithData_WithForce_ReturnsForceRequiresEmpty`
7. `IsRepositoryEmpty_NoCommits_ReturnsTrue`
8. `IsRepositoryEmpty_WithDocuments_ReturnsFalse`
9. `IsRepositoryEmpty_OnlySchemaCommits_ReturnsTrue`

**ManifestSetRemoteTool Tests:**
10. `ManifestSetRemote_ValidUrl_UpdatesManifest`
11. `ManifestSetRemote_EmptyUrl_ReturnsError`
12. `ManifestSetRemote_NoExistingManifest_CreatesNew`

### Integration Tests (8+ tests)

**E2E Workflow Tests:**
1. `FreshStart_NoRemoteUrl_DoesNotCreateEmptyRepo`
2. `FreshStart_ThenSetRemote_ThenClone_Succeeds`
3. `EmptyRepo_ForceClone_OverwritesSuccessfully`
4. `RepoWithData_ForceClone_Blocked`
5. `ManifestSetRemote_ThenClone_WorksEndToEnd`
6. `ForceClone_CleansUpCorrectly_NoLeftoverFiles`
7. `PendingConfiguration_StartupLogs_ClearGuidance`
8. `MultipleForceClone_Idempotent`

---

## Success Criteria

1. **Startup without DOLT_REMOTE_URL** does NOT create empty Dolt repo
2. **DoltClone --force** on empty repo succeeds
3. **DoltClone --force** on repo with data returns clear error
4. **ManifestSetRemote** followed by DoltClone works end-to-end
5. **Clear error messages** with actionable suggestions at every failure point
6. **Backwards compatible** - existing workflows continue to work
7. **18+ tests** pass covering all scenarios
8. **Build succeeds** with 0 errors

---

## Related Work

- **Depends On**: PP13-79-C1 (Manifest Auto-Sync)
- **Related**: PP13-79 (Manifest System)
- **Uses**: `IDmmsStateManifest`, `IDmmsInitializer`, `ISyncStateChecker`

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Force clone accidentally deletes user data | Require empty repo check before force; clear error for repos with data |
| File locking prevents cleanup | Retry logic with exponential backoff |
| User confusion about pending state | Clear log messages with specific next steps |
| Backwards compatibility issues | Existing DOLT_REMOTE_URL workflow unchanged |

---

## Implementation Order

1. **Phase 1**: DmmsInitializer changes (prevent empty init)
2. **Phase 2**: DoltCloneTool force option
3. **Phase 3**: ManifestSetRemoteTool
4. **Phase 4**: Program.cs startup logic updates
5. **Phase 5**: Testing

---

## Log File References from Original Issue

**Log File:** `Examples/260123_ManifestIssue/vm-rag.log`

| Line Range | Content |
|------------|---------|
| 526-536 | Manifest auto-creation without remote |
| 536-537 | Empty repo initialization decision |
| 549-603 | Sync failure due to missing documents table |
| 628-631 | DoltClone blocked by existing repo |
| 644-666 | Repeated "Collection default does not exist" errors |
