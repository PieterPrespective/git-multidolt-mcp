# PP13-82: Rename DMMS to Embranch - Implementation Plan

## Overview

This plan outlines the systematic approach to rename all DMMS namespace references to Embranch throughout the project. The rename includes namespaces, class names, file names, folder names, and documentation references.

---

## Chroma MCP Logging Requirements

Per `Prompts/BasePrompt.md`, all work on this issue must be logged to the Chroma MCP databases.

### Pre-Work Checklist
- [ ] Verify Chroma MCP targets a storage folder local to the current project
- [ ] Confirm `ProjectDevelopmentLog` collection exists (create if not)
- [ ] Confirm `PP13-82` collection exists (create if not) - this is the **IssueDB**
- [ ] Query `ProjectDevelopmentLog` for related issues (PP13-32, PP13-41, PP13-22 identified as relevant)
- [ ] Log planned approach to `PP13-82` collection marked as "Planned Approach"

### During Work - Log to IssueDB (PP13-82)
Log progress updates at these checkpoints:
1. **After Phase 2**: Log file/folder renames completed
2. **After Phase 4**: Log project file updates completed
3. **After Phase 7**: Log class/interface renames completed
4. **After Phase 13**: Log validation results

### Logging Format
```
Collection: PP13-82
Document ID: PP13-82_{action}_{date}
Metadata: {
  "issue_id": "PP13-82",
  "type": "{planned_approach|progress_update|issue_completion}",
  "status": "{planning|in_progress|completed}",
  "timestamp": "{YYYY-MM-DD}",
  "phase": "{phase_number}"
}
```

### Post-Work - Log Completion to ProjectDevelopmentLog
When work is complete, add a summary document to `ProjectDevelopmentLog`:
```
Document ID: PP13-82_completion_{date}
Content: Summary of changes, files modified, test results
Metadata: {
  "issue_id": "PP13-82",
  "type": "issue_completion",
  "status": "completed",
  "timestamp": "{date}",
  "files_modified": {count},
  "build_status": "success|failure",
  "related_issues": "PP13-32,PP13-41,PP13-22"
}
```

### Related Issues (from ProjectDevelopmentLog query)
- **PP13-32**: DMMSManualTesting URL Parsing Fix - contains DMMS namespace references
- **PP13-41**: Tool Registration and Documentation Update - previous DMMS to VM RAG updates
- **PP13-22**: Documentation Setup for DMMS - initial documentation with DMMS references

---

## Scope Summary

| Category | Count | Description |
|----------|-------|-------------|
| Namespace declarations | ~203 files | `namespace DMMS;` / `namespace DMMSTesting;` |
| Using statements | ~217 files | `using DMMS.*` / `using DMMSTesting.*` |
| Project files (.csproj) | 3 | Assembly and root namespace settings |
| Solution file (.sln) | 1 | Project references and names |
| Class/Interface renames | 6 | `Dmms*` / `IDmms*` classes and interfaces |
| File renames | 4 | Files with DMMS in filename |
| Folder renames | 1 | DMMSManualTesting directory |
| Config files (.json) | 2+ | Configuration references |
| Documentation (.md) | 10+ | Path and reference updates |

---

## Execution Phases

### Phase 1: Preparation

**1.1 Backup and Branch**
- Create a new git branch: `feature/PP13-82-rename-to-embranch`
- Ensure all changes are committed on main before branching

**1.2 Close IDE and Clean Build Artifacts**
- Close Visual Studio or any IDE that may lock files
- Delete `bin/` and `obj/` directories in all projects to avoid stale references
- Delete `.vs/` folder (will be regenerated)

---

### Phase 2: File and Folder Renames

Execute in this specific order to prevent reference issues:

**2.1 Rename Directory**
```
DMMSManualTesting/ → EmbranchManualTesting/
```

**2.2 Rename Project Files**
```
multidolt-mcp/DMMS.csproj → multidolt-mcp/Embranch.csproj
multidolt-mcp-testing/DMMSTesting.csproj → multidolt-mcp-testing/EmbranchTesting.csproj
EmbranchManualTesting/DMMSManualTesting.csproj → EmbranchManualTesting/EmbranchManualTesting.csproj
```

**2.3 Rename Solution File**
```
DMMS.sln → Embranch.sln
```

**2.4 Rename Source Files with DMMS in Name**
```
multidolt-mcp/Services/DmmsInitializer.cs → EmbranchInitializer.cs
multidolt-mcp/Services/DmmsStateManifest.cs → EmbranchStateManifest.cs
multidolt-mcp/Services/IDmmsInitializer.cs → IEmbranchInitializer.cs
multidolt-mcp/Services/IDmmsStateManifest.cs → IEmbranchStateManifest.cs
```

---

### Phase 3: Update Solution File (Embranch.sln)

Update project references in the solution file:
- Change `"DMMS"` to `"Embranch"` in project name
- Change `"DMMSTesting"` to `"EmbranchTesting"` in project name
- Change `"DMMSManualTesting"` to `"EmbranchManualTesting"` in project name
- Update paths to new .csproj file names

---

### Phase 4: Update Project Files (.csproj)

**4.1 Embranch.csproj (formerly DMMS.csproj)**
```xml
<AssemblyName>DMMS</AssemblyName> → <AssemblyName>Embranch</AssemblyName>
<RootNamespace>DMMS</RootNamespace> → <RootNamespace>Embranch</RootNamespace>
<DocumentationFile>...\DMMS.xml</DocumentationFile> → <DocumentationFile>...\Embranch.xml</DocumentationFile>
<_Parameter1>DMMSTesting</_Parameter1> → <_Parameter1>EmbranchTesting</_Parameter1>
```

**4.2 EmbranchTesting.csproj (formerly DMMSTesting.csproj)**
```xml
<AssemblyName>DMMSTesting</AssemblyName> → <AssemblyName>EmbranchTesting</AssemblyName>
<RootNamespace>DMMSTesting</RootNamespace> → <RootNamespace>EmbranchTesting</RootNamespace>
<DocumentationFile>...\DMMSTesting.xml</DocumentationFile> → <DocumentationFile>...\EmbranchTesting.xml</DocumentationFile>
<ProjectReference Include="..\multidolt-mcp\DMMS.csproj" /> → Embranch.csproj
```

**4.3 EmbranchManualTesting.csproj (formerly DMMSManualTesting.csproj)**
```xml
<AssemblyName>DMMSManualTesting</AssemblyName> → <AssemblyName>EmbranchManualTesting</AssemblyName>
<RootNamespace>DMMSManualTesting</RootNamespace> → <RootNamespace>EmbranchManualTesting</RootNamespace>
<ProjectReference Include="..\multidolt-mcp\DMMS.csproj" /> → Embranch.csproj
```

---

### Phase 5: Namespace Declarations (Bulk Replace)

**5.1 Main Project Namespaces**
Replace in all files under `multidolt-mcp/`:
```csharp
namespace DMMS; → namespace Embranch;
```

**5.2 Test Project Namespaces**
Replace in all files under `multidolt-mcp-testing/`:
```csharp
namespace DMMSTesting; → namespace EmbranchTesting;
```

**5.3 Manual Testing Project**
Replace in all files under `EmbranchManualTesting/`:
```csharp
namespace DMMS; → namespace Embranch;
```

---

### Phase 6: Using Statements (Bulk Replace)

Apply these replacements across all .cs files:

```csharp
using DMMS; → using Embranch;
using DMMS.Logging; → using Embranch.Logging;
using DMMS.Models; → using Embranch.Models;
using DMMS.Services; → using Embranch.Services;
using DMMS.Tools; → using Embranch.Tools;
using DMMS.Utilities; → using Embranch.Utilities;
using DMMSTesting; → using EmbranchTesting;
```

---

### Phase 7: Class and Interface Renames

**7.1 Interface Renames**
| Old Name | New Name | File |
|----------|----------|------|
| `IDmmsInitializer` | `IEmbranchInitializer` | IEmbranchInitializer.cs |
| `IDmmsStateManifest` | `IEmbranchStateManifest` | IEmbranchStateManifest.cs |

**7.2 Class Renames**
| Old Name | New Name | File |
|----------|----------|------|
| `DmmsInitializer` | `EmbranchInitializer` | EmbranchInitializer.cs |
| `DmmsStateManifest` | `EmbranchStateManifest` | EmbranchStateManifest.cs |
| `DmmsInitializationState` | `EmbranchInitializationState` | EmbranchInitializer.cs |

**7.3 Update All References**
Search and replace across entire codebase:
```
IDmmsInitializer → IEmbranchInitializer
IDmmsStateManifest → IEmbranchStateManifest
DmmsInitializer → EmbranchInitializer
DmmsStateManifest → EmbranchStateManifest
DmmsInitializationState → EmbranchInitializationState
```

---

### Phase 8: Configuration Files

**8.1 Launch Settings**
File: `EmbranchManualTesting/Properties/launchSettings.json`
```json
"DMMSManualTesting": { → "EmbranchManualTesting": {
```

**8.2 DocFX Configuration**
File: `Documentation/docfx.json`
```json
"_appFooter": "Copyright © DMMS Team" → "Copyright © Embranch Team"
```

---

### Phase 9: Strings and Log Messages

**9.1 Crash Log File Name**
File: `multidolt-mcp/Program.cs`
```csharp
"DMMS_crash.log" → "Embranch_crash.log"
```

**9.2 Other String References**
Search for any remaining string literals containing "DMMS" and update appropriately.

---

### Phase 10: Documentation Updates

Update all markdown files in `Documentation/` folder:

| Pattern | Replacement |
|---------|-------------|
| `DMMS.exe` | `Embranch.exe` |
| `DMMS.csproj` | `Embranch.csproj` |
| `DMMS.dll` | `Embranch.dll` |
| `DMMS Team` | `Embranch Team` |
| General "DMMS" references | "Embranch" |

**Files to Update:**
- `Documentation/userdocs/installation.md`
- `Documentation/userdocs/Configuration.md`
- `Documentation/userdocs/Troubleshooting.md`
- `Documentation/userdocs/Getting-started.md`
- And all other documentation files with DMMS references

---

### Phase 11: Update BasePrompt.md

File: `Prompts/BasePrompt.md`
```markdown
| ProjectNamespace | DMMS  | Default namespace for the Dolt Multi-Database MCP server |
```
Change to:
```markdown
| ProjectNamespace | Embranch | Default namespace for the Embranch MCP server |
```

---

### Phase 12: Comments Update (Optional)

Search for "DMMS" in all comments and XML documentation:
- Update descriptive comments mentioning "DMMS"
- Update `<summary>` tags referencing DMMS
- Update `<remarks>` sections

This phase is optional but recommended for consistency.

---

### Phase 13: Validation

**13.1 Clean and Rebuild**
```bash
dotnet clean Embranch.sln
dotnet build Embranch.sln
```

**13.2 Run All Tests**
```bash
dotnet test Embranch.sln
```

**13.3 Verify No Remaining References**
Search entire codebase for any remaining "DMMS" references:
```bash
grep -ri "DMMS" --include="*.cs" --include="*.csproj" --include="*.sln" --include="*.json" --include="*.md"
```

**13.4 Manual Smoke Test**
- Start the MCP server
- Verify it initializes correctly
- Test basic functionality

**13.5 Log Validation Results to IssueDB**
Log validation results to `PP13-82` collection:
```
Document ID: PP13-82_validation_{date}
Content: Build status, test results, remaining DMMS references (if any)
Metadata: { "type": "progress_update", "phase": "13", "status": "in_progress" }
```

---

## File Lists by Phase

### Phase 2 - Files to Rename
1. `DMMSManualTesting/` → `EmbranchManualTesting/`
2. `multidolt-mcp/DMMS.csproj` → `Embranch.csproj`
3. `multidolt-mcp-testing/DMMSTesting.csproj` → `EmbranchTesting.csproj`
4. `EmbranchManualTesting/DMMSManualTesting.csproj` → `EmbranchManualTesting.csproj`
5. `DMMS.sln` → `Embranch.sln`
6. `multidolt-mcp/Services/DmmsInitializer.cs` → `EmbranchInitializer.cs`
7. `multidolt-mcp/Services/DmmsStateManifest.cs` → `EmbranchStateManifest.cs`
8. `multidolt-mcp/Services/IDmmsInitializer.cs` → `IEmbranchInitializer.cs`
9. `multidolt-mcp/Services/IDmmsStateManifest.cs` → `IEmbranchStateManifest.cs`

### Phase 5-6 - Directories for Bulk Replace
- `multidolt-mcp/` - All .cs files (namespace DMMS → Embranch)
- `multidolt-mcp-testing/` - All .cs files (namespace DMMSTesting → EmbranchTesting)
- `EmbranchManualTesting/` - All .cs files (namespace DMMS → Embranch)

---

## Risk Mitigation

1. **Git Branch Protection**: All changes on feature branch, merge only after validation
2. **Incremental Commits**: Commit after each phase for easy rollback
3. **Automated Testing**: Run full test suite after completion
4. **IDE Refactoring**: Consider using Visual Studio's rename refactoring where applicable for type-safe renames

---

## Rollback Plan

If issues are encountered:
1. `git checkout main` to return to pre-rename state
2. Delete the feature branch if needed
3. Investigate specific issues before re-attempting

---

## Estimated Impact

- **Breaking Changes**: None (internal rename only)
- **API Compatibility**: N/A (namespace change is internal)
- **Configuration Updates Required**: Users must update paths to Embranch.exe in their MCP configs

---

## Post-Completion Tasks

1. **Log Completion to Chroma Databases**:
   - Add completion summary to `PP13-82` collection (IssueDB)
   - Add completion summary to `ProjectDevelopmentLog` collection
   - Include: files modified count, test results, build status
2. Update any external documentation or wikis
3. Notify team members of the rename
4. Update CI/CD pipelines if they reference DMMS
5. Update any deployment scripts

---

## Chroma Logging Commands Reference

### Log Progress Update (during work)
```
Collection: PP13-82
Add document with:
- ID: PP13-82_progress_{phase}_{date}
- Content: Description of completed work
- Metadata: {"issue_id": "PP13-82", "type": "progress_update", "phase": "{N}", "status": "in_progress", "timestamp": "{date}"}
```

### Log Completion (after validation passes)
```
Collection: PP13-82
Add document with:
- ID: PP13-82_completion_{date}
- Content: Full summary of all changes
- Metadata: {"issue_id": "PP13-82", "type": "issue_completion", "status": "completed", "timestamp": "{date}", "files_modified": {count}, "tests_passed": {count}}

Collection: ProjectDevelopmentLog
Add document with:
- ID: PP13-82_completion_{date}
- Content: Issue completion summary for project history
- Metadata: {"issue_id": "PP13-82", "type": "issue_completion", "status": "completed", "timestamp": "{date}", "related_issues": "PP13-32,PP13-41,PP13-22"}
```
