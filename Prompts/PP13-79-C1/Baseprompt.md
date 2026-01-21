# PP13-79-C1 Base Prompt

## Project Context

You are implementing **PP13-79-C1: Simplified Manifest-Driven DMMS Initialization** for the DMMS (Dolt-Managed Multimedia System) project.

## Background

DMMS uses ChromaDB for vector storage with Dolt for version control. PP13-79 introduced a manifest system (`.dmms/state.json`) to track Dolt state for Git synchronization. However, the implementation has duplicate configuration sources - environment variables (`DMMS_TARGET_BRANCH`, `DMMS_TARGET_COMMIT`) that are read but never used.

## The Problem

1. **Duplicate Config**: Environment variables and manifest file both specify Dolt targeting
2. **Unused Variables**: `DMMS_TARGET_BRANCH` and `DMMS_TARGET_COMMIT` env vars are read but ignored
3. **No Auto-Creation**: Manifest isn't created automatically on first run
4. **No Auto-Update**: Manifest isn't updated when Dolt operations change state
5. **Unsafe Sync**: Current logic doesn't check for uncommitted changes before syncing

## The Solution

1. **Remove redundant env vars** - Only `state.json` specifies repository/branch/commit
2. **Auto-create manifest** on first run (using `DOLT_REMOTE_URL` if set)
3. **Auto-update manifest** after all state-changing Dolt operations
4. **Safe sync logic** - Never sync if it would lose uncommitted changes
5. **Out-of-sync warnings** - Append warnings to tool responses when state differs

## Key Design Constraints

- **Single Source of Truth**: `state.json` is the only source for Dolt targeting
- **Portable**: Configuration must work when synced to different PCs
- **Safe**: Never lose uncommitted local changes
- **Transparent**: Users see clear warnings when out of sync
- **Backward Compatible**: Existing manifests continue working

## Existing Resources

### PP13-79 Implementation (Builds On)
- `multidolt-mcp/Models/ManifestModels.cs` - Manifest data structures
- `multidolt-mcp/Services/IDmmsStateManifest.cs` - Manifest service interface
- `multidolt-mcp/Services/DmmsStateManifest.cs` - Manifest read/write
- `multidolt-mcp/Services/IDmmsInitializer.cs` - Initialization interface
- `multidolt-mcp/Services/DmmsInitializer.cs` - Initialization logic
- `multidolt-mcp/Tools/InitManifestTool.cs` - Manual manifest creation
- `multidolt-mcp/Tools/UpdateManifestTool.cs` - Manual manifest update
- `multidolt-mcp/Tools/SyncToManifestTool.cs` - Manual sync trigger

### Dolt Tools (Need Modification)
- `DoltCloneTool.cs` - Clone remote repository
- `DoltCheckoutTool.cs` - Switch branches/commits
- `DoltCommitTool.cs` - Commit changes
- `DoltPullTool.cs` - Pull from remote
- `DoltInitTool.cs` - Initialize new repository
- `DoltResetTool.cs` - Reset to commit
- `ExecuteDoltMergeTool.cs` - Execute merge

### Existing Tests (Reference)
- `multidolt-mcp-testing/UnitTests/DmmsStateManifestTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_79_ManifestIntegrationTests.cs`
- `multidolt-mcp-testing/IntegrationTests/PP13_79_InitializationE2ETests.cs`

## Implementation Tracking

Use the **Chroma MCP server** collection `PP13-79-C1` to track development progress:
- Log planned approach at start
- Log phase completion after each phase
- Include test counts, build status, key decisions

## Namespace Convention

Use `DMMSTesting.IntegrationTests` namespace for integration tests to ensure `GlobalTestSetup` initializes `PythonContext` correctly.

## Key Scenarios to Handle

1. **Brand new project** - No Dolt, no manifest → Create manifest, clone if remote URL set
2. **Synced to new PC** - Manifest exists, no Dolt → Clone from manifest, checkout manifest commit
3. **In sync** - Manifest and Dolt match → No action
4. **Out of sync, safe** - Differs, no local changes → Auto-sync
5. **Out of sync, unsafe** - Differs, has local changes → Skip sync, warn user
6. **After Dolt operations** - Always update manifest to match new state

## Success Metrics

- Environment variables simplified (removed duplicates)
- Manifest auto-created on first run
- Manifest auto-updated after Dolt operations
- Safe sync never loses uncommitted changes
- Out-of-sync warnings appear in tool responses
- 54+ tests pass covering all scenarios
- Build succeeds with 0 errors

## Reference Assignment

See `Prompts/PP13-79-C1/Assignment.md` for full design document including:
- Detailed architecture
- Implementation phases
- File lists
- Code patterns
- Test specifications
- All execution scenarios
