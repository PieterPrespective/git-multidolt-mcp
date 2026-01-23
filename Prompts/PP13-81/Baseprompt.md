# PP13-81 Base Prompt

## Project Context

You are implementing **PP13-81: Empty Repository Initialization Blocks Clone Operations** for the DMMS (Dolt-Managed Multimedia System) project.

## Background

DMMS uses a manifest-based system (PP13-79-C1) to track Dolt repository state. When starting without the `DOLT_REMOTE_URL` environment variable, DMMS auto-creates an empty Dolt repository via `dolt init`. This empty repository then **blocks all subsequent `DoltClone` operations**, leaving users unable to configure a remote repository via MCP tools.

## The Problem

When DMMS starts without `DOLT_REMOTE_URL`:

```
1. Manifest auto-created with remote_url: null
2. DmmsInitializer calls dolt init (creates empty repo)
3. User tries DoltClone with remote URL
4. ERROR: "Repository already exists. Use dolt_reset or manual cleanup."
5. User is stuck with no MCP-based recovery path
```

Cascading errors also occur:
- "table not found: documents" (empty repo has no schema)
- "Collection default does not exist" (phantom default collection)

## The Solution

Implement a **four-part fix**:

1. **Prevent Empty Init**: Don't auto-initialize empty repo when no remote URL configured
2. **Add Force Clone**: Add `force` parameter to `DoltClone` to overwrite existing empty repos
3. **Add ManifestSetRemote Tool**: Allow updating remote URL in manifest post-creation
4. **Improve Empty Detection**: Smart detection of truly empty repositories

## Key Design Constraints

- **Backwards Compatible**: Existing `DOLT_REMOTE_URL` workflows unchanged
- **Non-Destructive**: Force option requires empty repo or explicit confirmation
- **User-Friendly**: Clear error messages with actionable suggestions
- **Recovery Path**: Always recoverable via MCP tools (no manual intervention required)

## Existing Resources

### DmmsInitializer (Needs Modification)
Location: `multidolt-mcp/Services/DmmsInitializer.cs`

Key method to modify:
- `InitializeFromManifestAsync` - lines 79-96 currently calls `dolt init` when no remote

### DoltCloneTool (Needs Modification)
Location: `multidolt-mcp/Tools/DoltCloneTool.cs`

Key areas:
- Lines 87-98: Check for existing repo (add force logic here)
- Add `IsRepositoryEmptyAsync` and `CleanupExistingRepositoryAsync` helpers

### DmmsStateManifest (Reference)
Location: `multidolt-mcp/Services/DmmsStateManifest.cs`

Key methods:
- `ReadManifestAsync` - reads manifest from project root
- `WriteManifestAsync` - writes manifest
- `CreateDefaultManifest` - creates new manifest with optional remote URL

### Program.cs (Needs Modification)
Location: `multidolt-mcp/Program.cs`

Key areas:
- Lines 473-546: Manifest auto-creation and initialization flow
- Tool registration section (add new ManifestSetRemoteTool)

## Implementation Tracking

Use the **Chroma MCP server** collection `PP13-81` to track development progress:
- Log planned approach at start
- Log phase completion after each phase
- Include test counts, build status, key decisions

## Namespace Convention

Use `DMMSTesting.IntegrationTests` namespace for integration tests to ensure `GlobalTestSetup` initializes `PythonContext` correctly.

## Success Metrics

- Startup without `DOLT_REMOTE_URL` does NOT create empty Dolt repo
- `DoltClone --force` on empty repo succeeds
- `ManifestSetRemote` followed by `DoltClone` works end-to-end
- 18+ tests pass (10 unit, 8 integration)
- Build succeeds with 0 errors

## Reference Assignment

See `Prompts/PP13-81/Assignment.md` for full design document including:
- Detailed root cause analysis
- Implementation phases
- Code patterns and examples
- Test specifications

## Related Issues

Query the `ProjectDevelopmentLog` Chroma collection for related context:
- PP13-79: Original manifest system implementation
- PP13-79-C1: Manifest auto-sync and safe sync checks
