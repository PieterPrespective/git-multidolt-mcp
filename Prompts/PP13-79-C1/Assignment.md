# PP13-79-C1: Simplified Manifest-Driven DMMS Initialization

## Date: 2026-01-19
## Type: Refactor/Enhancement - Initialization Logic Simplification
## Priority: High
## Depends On: PP13-79 (Git-Synchronized DMMS Initialization)

---

## Problem Statement

The current PP13-79 implementation has **duplicate configuration sources**:

1. **Environment Variables**: `DMMS_TARGET_BRANCH`, `DMMS_TARGET_COMMIT`, `DOLT_REMOTE_URL`
2. **Manifest File**: `.dmms/state.json` with `dolt.remote_url`, `dolt.current_branch`, `dolt.current_commit`

This duplication causes confusion:
- Environment variables are read but **never used** during startup
- Users expect env vars to work, but only the manifest file drives initialization
- Configuration is scattered across two sources
- `.mcp.json` configurations don't work as expected when syncing to different PCs

**Additional Issues:**
- `state.json` is not automatically created on first run
- `state.json` is not automatically updated when Dolt operations change state
- Initialization mode doesn't properly handle "no local changes" scenarios

---

## Solution Overview

### 1. Remove Redundant Environment Variables

**Remove from `DoltConfiguration`:**
- `TargetBranch` property
- `TargetCommit` property

**Remove from `Program.cs` ConfigurationUtility:**
- `DMMS_TARGET_BRANCH` env var handling
- `DMMS_TARGET_COMMIT` env var handling

**Keep in Environment Variables (file/debug settings only):**
- `DOLT_REPOSITORY_PATH` - local path to Dolt repo
- `DOLT_REMOTE_URL` - used ONLY for initial state.json creation if none exists
- `DOLT_EXECUTABLE_PATH` - path to Dolt CLI
- `DOLT_REMOTE_NAME` - remote name (default "origin")
- `DOLT_COMMAND_TIMEOUT` - CLI timeout
- `DOLT_DEBUG_LOGGING` - debug flag
- `DMMS_DATA_PATH` - data directory
- `CHROMA_DATA_PATH` - ChromaDB directory
- `DMMS_USE_MANIFEST` - whether to use manifest system
- `DMMS_INIT_MODE` - initialization mode (auto/manual/disabled)
- `DMMS_PROJECT_ROOT` - project root (optional, auto-detected)
- `DMMS_AUTO_DETECT_PROJECT_ROOT` - auto-detection flag
- Logging variables (`ENABLE_LOGGING`, `LOG_LEVEL`, `LOG_FILE_NAME`)

### 2. Manifest as Single Source of Truth

The `.dmms/state.json` file is the **only** source for:
- `dolt.remote_url` - remote repository URL
- `dolt.current_branch` - current branch name
- `dolt.current_commit` - current commit hash
- `dolt.default_branch` - default branch for the repo

### 3. Automatic State.json Creation

**On First Run (no manifest exists):**
1. Check if `DOLT_REMOTE_URL` env var is set
2. If set, create initial `state.json` with:
   - `dolt.remote_url` = value from env var
   - `dolt.current_branch` = "main" (default)
   - `dolt.current_commit` = null (to be populated after clone)
3. If not set, create minimal `state.json` with empty remote_url
4. Log that manifest was auto-created

### 4. Automatic State.json Updates

**Update manifest automatically when:**

| Operation | Trigger | Update |
|-----------|---------|--------|
| `DoltCloneTool` | After successful clone | Set `remote_url`, `current_branch`, `current_commit` |
| `DoltCheckoutTool` | After successful checkout | Update `current_branch` and/or `current_commit` (see edge cases) |
| `DoltCommitTool` | After successful commit | Update `current_commit` |
| `DoltPullTool` | After successful pull | Update `current_commit` (if changed) |
| `DoltFetchTool` | After successful fetch | No update (fetch doesn't change HEAD) |
| `DoltPushTool` | After successful push | No update (push doesn't change local state) |
| `DoltInitTool` | After successful init | Create/update manifest with local repo state |
| `DoltResetTool` | After successful reset | Update `current_commit` |
| `ExecuteDoltMergeTool` | After successful merge only | Update `current_commit` (NOT during conflicts) |

**Important:** These updates happen **regardless of initialization mode**. The manifest always reflects current local Dolt state.

**Edge Cases for Manifest Updates:**

| Edge Case | Handling |
|-----------|----------|
| **Detached HEAD** | When checking out a specific commit (not a branch), set `current_branch` to `null` and `current_commit` to the commit hash |
| **Branch creation** | `dolt checkout -b new-branch` creates branch AND updates manifest with new branch name |
| **Merge conflicts** | Do NOT update manifest until merge is fully resolved and committed |
| **Branch deleted remotely** | If manifest branch doesn't exist on fetch/pull, log warning but don't fail |
| **Concurrent DMMS instances** | Use file locking when writing manifest to prevent corruption |

### 5. Revised Initialization Logic

**Initialization Mode Behavior:**

| Mode | On Startup | Description |
|------|------------|-------------|
| `auto` | Conditional sync | Sync only if safe (see below) |
| `manual` | No sync | User must call `sync_to_manifest` explicitly |
| `disabled` | No sync, no warnings | Manifest system inactive |

**"Auto" Mode - Safe Sync Conditions:**

Sync automatically **ONLY IF**:

1. **Local Dolt repo doesn't exist** → Clone from manifest's `remote_url`, checkout manifest's commit/branch
2. **Local repo exists AND state mismatches manifest AND no uncommitted changes** → Checkout manifest's commit/branch

Do **NOT** sync if:
- Local repo has uncommitted changes (would lose work)
- Local repo is ahead of manifest (user has new commits)

### 6. Out-of-Sync Warning System

When state is out-of-sync but sync was skipped (to protect local changes), **append a warning to all tool call responses**:

```json
{
  "result": { /* normal tool response */ },
  "dmms_warning": {
    "type": "out_of_sync",
    "message": "Local Dolt state differs from manifest. Local changes would be lost if synced.",
    "local_state": {
      "branch": "feature-x",
      "commit": "abc123..."
    },
    "manifest_state": {
      "branch": "main",
      "commit": "def456..."
    },
    "action_required": "Commit or stash local changes, then call sync_to_manifest to synchronize."
  }
}
```

**Warning appears on:**
- All Dolt operation tools
- All ChromaDB operation tools

**Warning does NOT appear on:**
- Read-only/status tools (DoltStatusTool, DoltBranchesTool, etc.)
- The warning check itself shouldn't block operations

---

## Architecture

### New/Modified Components

#### 1. `ISyncStateChecker` Interface (NEW)

```csharp
public interface ISyncStateChecker
{
    /// <summary>
    /// Checks if local Dolt state matches the manifest
    /// </summary>
    Task<SyncStateCheckResult> CheckSyncStateAsync();

    /// <summary>
    /// Determines if it's safe to sync (no uncommitted changes)
    /// </summary>
    Task<bool> IsSafeToSyncAsync();

    /// <summary>
    /// Gets the out-of-sync warning object if applicable
    /// </summary>
    Task<OutOfSyncWarning?> GetOutOfSyncWarningAsync();
}
```

#### 2. `SyncStateChecker` Implementation (NEW)

- Compares local Dolt HEAD with manifest commit
- Checks for uncommitted changes via `dolt status`
- Caches result for performance (invalidate on Dolt operations)

#### 3. Models (NEW/MODIFIED)

```csharp
public record SyncStateCheckResult
{
    public bool IsInSync { get; init; }
    public bool HasLocalChanges { get; init; }
    public bool LocalAheadOfManifest { get; init; }
    public string? LocalCommit { get; init; }
    public string? LocalBranch { get; init; }
    public string? ManifestCommit { get; init; }
    public string? ManifestBranch { get; init; }
    public string? Reason { get; init; }
}

public record OutOfSyncWarning
{
    public string Type { get; init; } = "out_of_sync";
    public string Message { get; init; } = "";
    public object? LocalState { get; init; }
    public object? ManifestState { get; init; }
    public string? ActionRequired { get; init; }
}
```

#### 4. Tool Response Wrapper Pattern

Create a helper to wrap tool responses with warnings:

```csharp
public static class ToolResponseHelper
{
    public static async Task<object> WrapWithSyncWarningAsync(
        object response,
        ISyncStateChecker syncChecker,
        bool includeWarning = true)
    {
        if (!includeWarning) return response;

        var warning = await syncChecker.GetOutOfSyncWarningAsync();
        if (warning == null) return response;

        return new
        {
            result = response,
            dmms_warning = warning
        };
    }
}
```

---

## Implementation Phases

### Phase 1: Remove Redundant Configuration

**Files to Modify:**
- `multidolt-mcp/Models/DoltConfiguration.cs` - Remove `TargetBranch`, `TargetCommit` properties
- `multidolt-mcp/Program.cs` - Remove env var handling for removed properties

**Verification:**
- Build succeeds
- Existing tests pass (update any that reference removed properties)

### Phase 2: Auto-Create Manifest on First Run

**Files to Modify:**
- `multidolt-mcp/Program.cs` - Add manifest creation logic when none exists
- `multidolt-mcp/Services/IDmmsStateManifest.cs` - Add `CreateInitialManifestAsync` method
- `multidolt-mcp/Services/DmmsStateManifest.cs` - Implement initial manifest creation

**Logic:**
```csharp
// In Program.cs startup, after detecting no manifest:
if (manifest == null && doltConfig.UseManifest)
{
    // Create initial manifest from env vars or defaults
    var initialManifest = await manifestService.CreateInitialManifestAsync(
        remoteUrl: Environment.GetEnvironmentVariable("DOLT_REMOTE_URL"),
        defaultBranch: "main"
    );
    await manifestService.WriteManifestAsync(projectRoot, initialManifest);
    manifest = initialManifest;
}
```

### Phase 3: Sync State Checker Service

**Files to Create:**
- `multidolt-mcp/Services/ISyncStateChecker.cs`
- `multidolt-mcp/Services/SyncStateChecker.cs`

**Files to Modify:**
- `multidolt-mcp/Program.cs` - Register `ISyncStateChecker`

### Phase 4: Revised Initialization Logic

**Files to Modify:**
- `multidolt-mcp/Services/DmmsInitializer.cs` - Implement safe sync conditions
- `multidolt-mcp/Program.cs` - Use new initialization logic

**Safe Sync Decision Tree:**
```
Has manifest?
├── No → Create initial manifest, continue with empty/clone
└── Yes → Check local Dolt state
    ├── No local repo → Clone from manifest remote, checkout manifest commit
    └── Local repo exists
        ├── State matches manifest → No action needed
        └── State differs
            ├── Has uncommitted changes → Skip sync, set warning flag
            ├── Local ahead of manifest → Skip sync, set warning flag
            └── Safe to sync → Checkout manifest commit/branch
```

### Phase 5: Auto-Update Manifest on Dolt Operations

**Files to Modify:**
- `multidolt-mcp/Tools/DoltCloneTool.cs` - Update manifest after clone
- `multidolt-mcp/Tools/DoltCheckoutTool.cs` - Update manifest after checkout
- `multidolt-mcp/Tools/DoltCommitTool.cs` - Update manifest after commit
- `multidolt-mcp/Tools/DoltPullTool.cs` - Update manifest after pull
- `multidolt-mcp/Tools/DoltInitTool.cs` - Update manifest after init
- `multidolt-mcp/Tools/DoltResetTool.cs` - Update manifest after reset
- `multidolt-mcp/Tools/ExecuteDoltMergeTool.cs` - Update manifest after merge

**Pattern for each tool:**
```csharp
// At end of successful operation:
var projectRoot = await ResolveProjectRootAsync();
if (projectRoot != null)
{
    var currentCommit = await _doltCli.GetHeadCommitHashAsync();
    var currentBranch = await _doltCli.GetCurrentBranchAsync();
    await _manifestService.UpdateDoltCommitAsync(projectRoot, currentCommit, currentBranch);
}
```

### Phase 6: Out-of-Sync Warning Integration

**Files to Create:**
- `multidolt-mcp/Utilities/ToolResponseHelper.cs`

**Files to Modify (add warning wrapper):**
- All Dolt mutation tools (commit, checkout, pull, push, merge, reset)
- All ChromaDB mutation tools (add, update, delete documents/collections)

**Pattern:**
```csharp
[McpServerTool]
public async Task<object> SomeOperation(...)
{
    // ... perform operation ...

    var response = new { /* normal response */ };

    return await ToolResponseHelper.WrapWithSyncWarningAsync(
        response,
        _syncStateChecker,
        includeWarning: true
    );
}
```

### Phase 7: Testing

**Files to Create:**
- `multidolt-mcp-testing/UnitTests/SyncStateCheckerTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_79_C1_AutoManifestTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_79_C1_SafeSyncTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_79_C1_AutoUpdateTests.cs`

---

## Test Specifications

### Unit Tests (SyncStateCheckerTests.cs) - 10+ tests

1. `CheckSyncState_NoManifest_ReturnsInSync` - No manifest means nothing to sync to
2. `CheckSyncState_MatchingState_ReturnsInSync`
3. `CheckSyncState_DifferentCommit_ReturnsOutOfSync`
4. `CheckSyncState_DifferentBranch_ReturnsOutOfSync`
5. `IsSafeToSync_NoLocalChanges_ReturnsTrue`
6. `IsSafeToSync_HasUncommittedChanges_ReturnsFalse`
7. `IsSafeToSync_LocalAheadOfManifest_ReturnsFalse`
8. `GetOutOfSyncWarning_WhenInSync_ReturnsNull`
9. `GetOutOfSyncWarning_WhenOutOfSync_ReturnsWarning`
10. `GetOutOfSyncWarning_WarningContainsCorrectState`

### Integration Tests (PP13_79_C1_AutoManifestTests.cs) - 8+ tests

1. `FirstRun_NoManifest_CreatesDefaultManifest`
2. `FirstRun_WithRemoteUrlEnvVar_CreatesManifestWithRemote`
3. `FirstRun_ManifestExists_DoesNotOverwrite`
4. `ManifestCreation_SetsCorrectDefaults`
5. `ManifestCreation_WritesToCorrectPath`
6. `ManifestCreation_LogsCreation`
7. `ManifestCreation_HandlesInvalidProjectRoot`
8. `ManifestCreation_WorksWithAutoDetectedProjectRoot`

### Integration Tests (PP13_79_C1_SafeSyncTests.cs) - 10+ tests

1. `AutoInit_NoLocalRepo_ClonesFromManifest`
2. `AutoInit_LocalRepoMatchesManifest_NoAction`
3. `AutoInit_LocalRepoDiffers_NoLocalChanges_Syncs`
4. `AutoInit_LocalRepoDiffers_HasLocalChanges_SkipsSync`
5. `AutoInit_LocalRepoDiffers_LocalAhead_SkipsSync`
6. `AutoInit_ManualMode_NeverSyncs`
7. `AutoInit_DisabledMode_NeverSyncs`
8. `AutoInit_NetworkError_GracefulFallback`
9. `AutoInit_InvalidManifestCommit_GracefulFallback`
10. `AutoInit_MixedState_BranchDiffersCommitMatches`

### Integration Tests (PP13_79_C1_AutoUpdateTests.cs) - 18+ tests

1. `DoltClone_UpdatesManifestWithRemoteAndCommit`
2. `DoltCheckout_Branch_UpdatesManifestBranch`
3. `DoltCheckout_Commit_UpdatesManifestCommit`
4. `DoltCommit_UpdatesManifestCommit`
5. `DoltPull_ChangesCommit_UpdatesManifest`
6. `DoltPull_NoChanges_ManifestUnchanged`
7. `DoltInit_CreatesOrUpdatesManifest`
8. `DoltReset_UpdatesManifestCommit`
9. `DoltMerge_UpdatesManifestCommit`
10. `MultipleOperations_ManifestStaysInSync`
11. `ManifestUpdate_PreservesOtherFields`
12. `ManifestUpdate_UpdatesTimestamp`
13. `DoltPush_DoesNotUpdateManifest` - Push doesn't change local state
14. `DoltFetch_DoesNotUpdateManifest` - Fetch doesn't change HEAD
15. `DoltCheckout_DetachedHead_SetsBranchToNull` - Checkout specific commit sets branch to null
16. `DoltCheckout_CreateBranch_UpdatesManifestWithNewBranch` - `checkout -b` updates with new branch
17. `DoltMerge_WithConflicts_DoesNotUpdateManifest` - Only update after conflict resolution
18. `ManifestWrite_ConcurrentAccess_UsesFileLocking` - File locking prevents corruption

### E2E Tests - 8+ tests

1. `FullWorkflow_Clone_Commit_Push_ManifestTracksAll`
2. `FullWorkflow_BranchSwitch_ManifestUpdates`
3. `OutOfSyncWarning_AppearsInToolResponses`
4. `OutOfSyncWarning_DisappearsAfterSync`
5. `NewProject_FirstRun_ToFullOperation`
6. `DetachedHead_Workflow_ManifestTracksCorrectly`
7. `BranchCreation_Workflow_ManifestUpdates`
8. `MergeConflict_Resolution_ManifestUpdatesAfterCommit`

---

## Execution Scenarios Checklist

### Scenario 1: Brand New Project (No Dolt, No Manifest)

| Step | Expected Behavior |
|------|-------------------|
| DMMS starts | Detects no manifest |
| Check env vars | If `DOLT_REMOTE_URL` set, include in new manifest |
| Create manifest | Write `.dmms/state.json` with defaults |
| Check Dolt repo | No local repo exists |
| Clone (if remote) | Clone from manifest's `remote_url` |
| Update manifest | Set `current_commit` and `current_branch` from cloned state |
| Ready | DMMS operational, manifest in sync |

### Scenario 2: Existing Project Synced to New PC (Manifest Exists, No Dolt)

| Step | Expected Behavior |
|------|-------------------|
| DMMS starts | Reads manifest from `.dmms/state.json` |
| Check Dolt repo | No local repo exists |
| Clone | Clone from manifest's `remote_url` |
| Checkout | Checkout manifest's `current_commit` |
| Sync ChromaDB | Sync ChromaDB to match Dolt state |
| Ready | State matches manifest |

### Scenario 3: Existing Project, Manifest and Dolt Match

| Step | Expected Behavior |
|------|-------------------|
| DMMS starts | Reads manifest |
| Compare states | Local Dolt matches manifest |
| No action | Skip sync, no warnings |
| Ready | Already in sync |

### Scenario 4: Existing Project, Manifest and Dolt Differ, No Local Changes

| Step | Expected Behavior |
|------|-------------------|
| DMMS starts | Reads manifest |
| Compare states | Local Dolt differs from manifest |
| Check for changes | No uncommitted changes |
| Safe to sync | Checkout manifest's commit/branch |
| Update ChromaDB | Sync to new Dolt state |
| Ready | State matches manifest |

### Scenario 5: Existing Project, Manifest and Dolt Differ, HAS Local Changes

| Step | Expected Behavior |
|------|-------------------|
| DMMS starts | Reads manifest |
| Compare states | Local Dolt differs from manifest |
| Check for changes | Has uncommitted changes |
| Skip sync | Do NOT checkout (would lose changes) |
| Set warning flag | Out-of-sync warning active |
| Ready | Warning appears on tool calls |

### Scenario 6: User Performs Dolt Commit

| Step | Expected Behavior |
|------|-------------------|
| User calls dolt_commit | Commit executes successfully |
| Get new state | Retrieve new commit hash |
| Update manifest | Write new `current_commit` to manifest |
| Return response | Include success + any warnings |

### Scenario 7: User Switches Branch

| Step | Expected Behavior |
|------|-------------------|
| User calls dolt_checkout branch | Checkout executes |
| Get new state | Retrieve new branch and commit |
| Update manifest | Write new `current_branch` and `current_commit` |
| Sync ChromaDB | Update ChromaDB to match new branch state |
| Return response | Include success + any warnings |

### Scenario 8: Manual Mode - User Must Explicitly Sync

| Step | Expected Behavior |
|------|-------------------|
| DMMS starts (mode=manual) | Reads manifest |
| Compare states | Detects mismatch |
| Skip sync | Mode is manual, don't auto-sync |
| Set warning flag | Out-of-sync warning active |
| User calls sync_to_manifest | Now sync executes |
| Ready | State matches manifest |

### Scenario 9: Disabled Mode - No Manifest System

| Step | Expected Behavior |
|------|-------------------|
| DMMS starts (mode=disabled) | Don't read manifest |
| No sync checks | Manifest system inactive |
| No warnings | No out-of-sync warnings |
| Tools work normally | All operations work without manifest overhead |

### Scenario 10: Network Failure During Clone

| Step | Expected Behavior |
|------|-------------------|
| DMMS starts | Reads manifest with remote_url |
| Attempt clone | Network error occurs |
| Graceful fallback | Log warning, continue without Dolt |
| Set warning | "Failed to clone from remote" warning |
| DMMS operational | ChromaDB works, Dolt unavailable |

---

## Success Criteria

1. **Single Source of Truth**: All Dolt targeting configuration lives in `state.json` only
2. **Auto-Creation**: Manifest created automatically on first run
3. **Auto-Update**: Manifest updated automatically on all state-changing Dolt operations
4. **Safe Sync**: Never lose uncommitted local changes
5. **Clear Warnings**: Out-of-sync state clearly communicated to users
6. **Backward Compatible**: Existing projects with manifests continue working
7. **Portable**: Configuration works when project synced to different PC
8. **Test Coverage**: 54+ tests covering all scenarios (10 unit + 8 auto-manifest + 10 safe-sync + 18 auto-update + 8 E2E)

---

## Files Summary

### Files to Create (6)
- `multidolt-mcp/Services/ISyncStateChecker.cs`
- `multidolt-mcp/Services/SyncStateChecker.cs`
- `multidolt-mcp/Utilities/ToolResponseHelper.cs`
- `multidolt-mcp-testing/UnitTests/SyncStateCheckerTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_79_C1_AutoManifestTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_79_C1_SafeSyncTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_79_C1_AutoUpdateTests.cs`

### Files to Modify (15+)
- `multidolt-mcp/Models/DoltConfiguration.cs`
- `multidolt-mcp/Program.cs`
- `multidolt-mcp/Services/IDmmsStateManifest.cs`
- `multidolt-mcp/Services/DmmsStateManifest.cs`
- `multidolt-mcp/Services/DmmsInitializer.cs`
- `multidolt-mcp/Tools/DoltCloneTool.cs`
- `multidolt-mcp/Tools/DoltCheckoutTool.cs`
- `multidolt-mcp/Tools/DoltCommitTool.cs`
- `multidolt-mcp/Tools/DoltPullTool.cs`
- `multidolt-mcp/Tools/DoltInitTool.cs`
- `multidolt-mcp/Tools/DoltResetTool.cs`
- `multidolt-mcp/Tools/ExecuteDoltMergeTool.cs`
- Various ChromaDB tools (for warning integration)

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Breaking existing workflows | Comprehensive testing, backward compatibility checks |
| Performance overhead from sync checks | Cache sync state, invalidate on Dolt operations |
| Manifest write failures | Graceful handling, log warnings, don't block operations |
| Race conditions on manifest updates | File locking or atomic writes |
| Complex merge scenarios | Focus on common cases, document edge cases |

---

## Implementation Order

1. **Phase 1**: Remove redundant config (low risk, high clarity)
2. **Phase 2**: Auto-create manifest (enables subsequent phases)
3. **Phase 3**: Sync state checker service (foundation for warnings)
4. **Phase 4**: Revised initialization logic (core behavior change)
5. **Phase 5**: Auto-update manifest on operations (keeps state accurate)
6. **Phase 6**: Out-of-sync warning integration (user communication)
7. **Phase 7**: Comprehensive testing
