# Dolt Interface Implementation Plan for VM RAG MCP Server

## Executive Summary

This document provides a step-by-step implementation plan for integrating Dolt CLI commands into your C# MCP server. The approach uses **Dolt CLI exclusively** (via subprocess execution) for all operations—both version control and data queries. This ensures a single, consistent interface with no port conflicts or server management overhead.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Package Recommendations](#2-package-recommendations)
3. [Dolt Functions to Interface](#3-dolt-functions-to-interface)
4. [Database Schema Design](#4-database-schema-design)
   - 4.1 Core Tables (Generalized Schema)
   - 4.2 SQL Queries for Delta Detection
   - 4.3 Schema Mapping: Dolt ↔ ChromaDB (Bidirectional)
   - 4.4 Ensuring Consistency Across Clones
   - 4.5 The document_sync_log Table
5. [Delta Detection & Sync Processing](#5-delta-detection--sync-processing)
   - 5.1 Bidirectional Sync Model
   - 5.2 Operation Processing Matrix
   - 5.3 Use Cases for Chroma → Dolt Sync
   - 5.4 Chroma → Dolt Delta Detection
   - 5.5 Chroma → Dolt Sync Implementation
   - 5.6 Updated SyncManager with Bidirectional Support
6. [Implementation Steps](#6-implementation-steps)
7. [Acceptance Tests (Gherkin BDD)](#7-acceptance-tests-gherkin-bdd)
   - T0: Bidirectional Sync - Chroma to Dolt
   - T0.5: Workflow Integration Tests
   - T1: Copy RAG Data Across DoltHub
   - T2: Fast-Forward RAG Data Sync
   - T3: Merge RAG Data Between Branches
8. [Additional Test Scenarios](#8-additional-test-scenarios)

**Appendices**
- [Appendix A: Configuration Schema](#appendix-a-configuration-schema)
- [Appendix B: Quick Reference - Dolt CLI Commands](#appendix-b-quick-reference---dolt-cli-commands)
- [Appendix C: JSON Output Format](#appendix-c-json-output-format)
- [Appendix D: Schema Design Rationale](#appendix-d-schema-design-rationale)

---

## 1. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         C# MCP Server (.NET 9.0)                            │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌───────────────────────────────────────────┐       ┌─────────────────┐   │
│  │           DoltCliWrapper                  │       │  ChromaManager  │   │
│  │  (All operations via dolt.exe process)    │       │  (Python.NET)   │   │
│  │                                           │       │                 │   │
│  │  • Version Control: commit, push, pull    │       │  • Add/Update   │   │
│  │  • Branching: checkout, merge, branch     │       │  • Delete       │   │
│  │  • Data: dolt sql -q "SELECT/INSERT..."   │       │  • Query        │   │
│  │  • Diff: dolt sql -q "DOLT_DIFF(...)"     │       │  • Get by ID    │   │
│  └──────────────────┬────────────────────────┘       └────────┬────────┘   │
│                     │                                         │            │
│                     ▼                                         ▼            │
│  ┌───────────────────────────────────────────────────────────────────────┐ │
│  │                    Bidirectional SyncManager                          │ │
│  │                                                                       │ │
│  │  ┌─────────────────────┐              ┌─────────────────────┐        │ │
│  │  │   Dolt → Chroma     │              │   Chroma → Dolt     │        │ │
│  │  │   (Pull/Checkout)   │              │   (Pre-Commit)      │        │ │
│  │  │                     │              │                     │        │ │
│  │  │  • After pull       │              │  • Before commit    │        │ │
│  │  │  • After checkout   │              │  • New documents    │        │ │
│  │  │  • After merge      │              │  • Local edits      │        │ │
│  │  │  • After reset      │              │  • Initialize DB    │        │ │
│  │  └─────────────────────┘              └─────────────────────┘        │ │
│  │                                                                       │ │
│  │  Delta Detection: content_hash comparison in both directions          │ │
│  └───────────────────────────────────────────────────────────────────────┘ │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
              │                                               │
              ▼                                               ▼
      ┌──────────────┐                                ┌──────────────┐
      │   Dolt CLI   │◄──────────────────────────────►│   ChromaDB   │
      │  (dolt.exe)  │     Bidirectional Sync         │  (Python)    │
      │              │                                │              │
      │  Repository  │  Dolt = Version Control        │  Collections │
      │  (filesystem)│  Chroma = Working Copy         │  (persisted) │
      └──────────────┘                                └──────────────┘
```

### Bidirectional Sync Model

The VM RAG MCP Server uses a **bidirectional sync model** where:

1. **ChromaDB is the working copy** - Users read and write documents directly to ChromaDB via MCP tools
2. **Dolt is version control** - Provides branching, commits, history, and remote sync (DoltHub)
3. **Sync happens at version control boundaries**:
   - **Chroma → Dolt**: Before `commit` (stage local changes)
   - **Dolt → Chroma**: After `pull`, `checkout`, `merge`, `reset` (update working copy)

This is analogous to how Git works with a working directory:
- ChromaDB = Working directory (where you edit files)
- Dolt = Git repository (where versions are stored)
- Sync = `git add` (Chroma→Dolt) and `git checkout` (Dolt→Chroma)
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
              │                                               │
              ▼                                               ▼
      ┌──────────────┐                                ┌──────────────┐
      │   Dolt CLI   │                                │   ChromaDB   │
      │  (dolt.exe)  │                                │  (Python)    │
      │              │                                │              │
      │  Repository  │                                │  Collections │
      │  (filesystem)│                                │  (persisted) │
      └──────────────┘                                └──────────────┘
```

### CLI-Only Approach Benefits

1. **Single interface** - All Dolt operations go through `dolt.exe`, no SQL server to manage
2. **No port conflicts** - No risk of conflicting with MySQL, MariaDB, or other Dolt instances
3. **Simpler deployment** - Just need `dolt.exe` in PATH
4. **Atomic operations** - Each CLI call is self-contained
5. **Environment agnostic** - Works identically in CI/CD, containers, and local dev

### How Data Operations Work via CLI

```bash
# Queries via CLI (returns results to stdout)
dolt sql -q "SELECT * FROM issue_logs WHERE project_id = 'proj-001'" -r json

# Inserts/Updates via CLI
dolt sql -q "INSERT INTO issue_logs (log_id, content, ...) VALUES (...)"

# Dolt-specific functions via CLI
dolt sql -q "SELECT active_branch()"
dolt sql -q "SELECT DOLT_HASHOF('HEAD')"
dolt sql -q "SELECT * FROM DOLT_DIFF('abc123', 'HEAD', 'issue_logs')" -r json
```

---

## 2. Package Recommendations

### C# Packages

```xml
<!-- Add to your .csproj file -->
<ItemGroup>
    <!-- For clean process management (Dolt CLI) -->
    <PackageReference Include="CliWrap" Version="3.6.6" />
    
    <!-- JSON handling for parsing dolt sql -r json output -->
    <PackageReference Include="System.Text.Json" Version="8.0.4" />
    
    <!-- SHA-256 hashing (built into .NET) -->
    <!-- System.Security.Cryptography - included in SDK -->
</ItemGroup>
```

### Why CliWrap?

CliWrap provides a clean, async-friendly API for process execution:

```csharp
// Clean fluent API
var result = await Cli.Wrap("dolt")
    .WithArguments(new[] { "sql", "-q", query, "-r", "json" })
    .WithWorkingDirectory(_repositoryPath)
    .ExecuteBufferedAsync();

// vs raw Process.Start (more verbose, harder to handle async)
```

### Alternative: Python via Python.NET

If you prefer consistency with your ChromaDB approach, `doltpy` also uses CLI under the hood:

```python
# doltpy is essentially a CLI wrapper too
from doltpy.cli import Dolt

repo = Dolt("./my_db")
repo.sql("SELECT * FROM documents", result_format="json")
repo.commit("message")
repo.push("origin", "main")
```

**Recommendation**: Use C# with `CliWrap` for type safety and better integration with your existing codebase.

### Dolt Installation

```bash
# Windows (PowerShell as Admin)
choco install dolt

# Or manual download from https://github.com/dolthub/dolt/releases
# Add to PATH: C:\Program Files\dolt\bin

# Verify installation
dolt version
```

---

## 3. Dolt Functions to Interface

### 3.1 Complete CLI Command Reference

| Category | Operation | CLI Command | Output Format |
|----------|-----------|-------------|---------------|
| **Repository** | Initialize | `dolt init` | text |
| | Clone | `dolt clone <remote>` | text |
| | Status | `dolt status` | text |
| **Branching** | List branches | `dolt branch` | text |
| | Create branch | `dolt branch <name>` | text |
| | Delete branch | `dolt branch -d <name>` | text |
| | Checkout | `dolt checkout <branch>` | text |
| | Checkout new | `dolt checkout -b <branch>` | text |
| | Current branch | `dolt sql -q "SELECT active_branch()"` | json |
| **Commits** | Stage all | `dolt add -A` | text |
| | Commit | `dolt commit -m "<msg>"` | text |
| | HEAD hash | `dolt sql -q "SELECT DOLT_HASHOF('HEAD')"` | json |
| | Log | `dolt log --oneline -n <N>` | text |
| **Remote** | Add remote | `dolt remote add <name> <url>` | text |
| | Push | `dolt push <remote> <branch>` | text |
| | Pull | `dolt pull <remote> <branch>` | text |
| | Fetch | `dolt fetch <remote>` | text |
| **Merge** | Merge | `dolt merge <branch>` | text |
| | List conflicts | `dolt conflicts cat <table>` | text |
| | Resolve ours | `dolt conflicts resolve --ours <table>` | text |
| | Resolve theirs | `dolt conflicts resolve --theirs <table>` | text |
| **Diff** | Working changes | `dolt diff` | text |
| | Between commits | `dolt diff <from> <to>` | text |
| | Table diff (SQL) | `dolt sql -q "SELECT * FROM DOLT_DIFF(...)" -r json` | json |
| **Reset** | Hard reset | `dolt reset --hard <commit>` | text |
| | Soft reset | `dolt reset --soft HEAD~1` | text |
| **Data Queries** | Select | `dolt sql -q "SELECT ..." -r json` | json |
| | Insert | `dolt sql -q "INSERT ..."` | text |
| | Update | `dolt sql -q "UPDATE ..."` | text |
| | Delete | `dolt sql -q "DELETE ..."` | text |

### 3.2 C# Interface Definition

```csharp
/// <summary>
/// Complete Dolt CLI wrapper - all operations via subprocess
/// </summary>
public interface IDoltCli
{
    // ==================== Repository Management ====================
    
    /// <summary>Initialize a new Dolt repository</summary>
    Task<CommandResult> InitAsync();
    
    /// <summary>Clone a repository from DoltHub</summary>
    Task<CommandResult> CloneAsync(string remoteUrl, string localPath = null);
    
    /// <summary>Get repository status (staged, unstaged changes)</summary>
    Task<RepositoryStatus> GetStatusAsync();
    
    // ==================== Branch Operations ====================
    
    /// <summary>Get the current active branch name</summary>
    Task<string> GetCurrentBranchAsync();
    
    /// <summary>List all branches</summary>
    Task<IEnumerable<BranchInfo>> ListBranchesAsync();
    
    /// <summary>Create a new branch (does not switch to it)</summary>
    Task<CommandResult> CreateBranchAsync(string branchName);
    
    /// <summary>Delete a branch</summary>
    Task<CommandResult> DeleteBranchAsync(string branchName, bool force = false);
    
    /// <summary>Switch to a branch, optionally creating it</summary>
    Task<CommandResult> CheckoutAsync(string branchName, bool createNew = false);
    
    // ==================== Commit Operations ====================
    
    /// <summary>Stage all changes</summary>
    Task<CommandResult> AddAllAsync();
    
    /// <summary>Stage specific tables</summary>
    Task<CommandResult> AddAsync(params string[] tables);
    
    /// <summary>Commit staged changes</summary>
    Task<CommitResult> CommitAsync(string message);
    
    /// <summary>Get the current HEAD commit hash</summary>
    Task<string> GetHeadCommitHashAsync();
    
    /// <summary>Get commit history</summary>
    Task<IEnumerable<CommitInfo>> GetLogAsync(int limit = 10);
    
    // ==================== Remote Operations ====================
    
    /// <summary>Add a remote</summary>
    Task<CommandResult> AddRemoteAsync(string name, string url);
    
    /// <summary>Remove a remote</summary>
    Task<CommandResult> RemoveRemoteAsync(string name);
    
    /// <summary>List remotes</summary>
    Task<IEnumerable<RemoteInfo>> ListRemotesAsync();
    
    /// <summary>Push to remote</summary>
    Task<CommandResult> PushAsync(string remote = "origin", string branch = null);
    
    /// <summary>Pull from remote</summary>
    Task<PullResult> PullAsync(string remote = "origin", string branch = null);
    
    /// <summary>Fetch from remote (no merge)</summary>
    Task<CommandResult> FetchAsync(string remote = "origin");
    
    // ==================== Merge Operations ====================
    
    /// <summary>Merge a branch into current branch</summary>
    Task<MergeResult> MergeAsync(string sourceBranch);
    
    /// <summary>Check if there are unresolved conflicts</summary>
    Task<bool> HasConflictsAsync();
    
    /// <summary>Get conflict details for a table</summary>
    Task<IEnumerable<ConflictInfo>> GetConflictsAsync(string tableName);
    
    /// <summary>Resolve conflicts using a strategy</summary>
    Task<CommandResult> ResolveConflictsAsync(string tableName, ConflictResolution resolution);
    
    // ==================== Diff Operations ====================
    
    /// <summary>Get uncommitted changes</summary>
    Task<DiffSummary> GetWorkingDiffAsync();
    
    /// <summary>Get diff between two commits for a specific table</summary>
    Task<IEnumerable<DiffRow>> GetTableDiffAsync(string fromCommit, string toCommit, string tableName);
    
    // ==================== Reset Operations ====================
    
    /// <summary>Hard reset to a specific commit</summary>
    Task<CommandResult> ResetHardAsync(string commitHash);
    
    /// <summary>Soft reset (keep changes staged)</summary>
    Task<CommandResult> ResetSoftAsync(string commitRef = "HEAD~1");
    
    // ==================== SQL Operations ====================
    
    /// <summary>Execute a SQL query and return results as JSON</summary>
    Task<string> QueryJsonAsync(string sql);
    
    /// <summary>Execute a SQL query and return typed results</summary>
    Task<IEnumerable<T>> QueryAsync<T>(string sql) where T : new();
    
    /// <summary>Execute a SQL statement (INSERT/UPDATE/DELETE)</summary>
    Task<int> ExecuteAsync(string sql);
    
    /// <summary>Execute a SQL query and return a single scalar value</summary>
    Task<T> ExecuteScalarAsync<T>(string sql);
}

// ==================== Supporting Types ====================

public record CommandResult(bool Success, string Output, string Error, int ExitCode);

public record CommitResult(bool Success, string CommitHash, string Message);

public record PullResult(bool Success, bool WasFastForward, bool HasConflicts, string Message);

public record MergeResult(bool Success, bool HasConflicts, string MergeCommitHash, string Message);

public record BranchInfo(string Name, bool IsCurrent, string LastCommitHash);

public record CommitInfo(string Hash, string Message, string Author, DateTime Date);

public record RemoteInfo(string Name, string Url);

public record DiffRow(
    string DiffType,      // "added", "modified", "removed"
    string SourceId,
    string FromContentHash,
    string ToContentHash,
    string ToContent,
    Dictionary<string, object> Metadata
);

public record ConflictInfo(
    string TableName,
    string RowId,
    Dictionary<string, object> OurValues,
    Dictionary<string, object> TheirValues,
    Dictionary<string, object> BaseValues
);

public enum ConflictResolution { Ours, Theirs }

public record RepositoryStatus(
    string Branch,
    bool HasStagedChanges,
    bool HasUnstagedChanges,
    IEnumerable<string> StagedTables,
    IEnumerable<string> ModifiedTables
);

public record DiffSummary(
    int TablesChanged,
    int RowsAdded,
    int RowsModified,
    int RowsDeleted
);
```

### 3.3 Implementation of Core CLI Wrapper

```csharp
// File: Services/DoltCli.cs
using CliWrap;
using CliWrap.Buffered;
using System.Text.Json;
using System.Text.RegularExpressions;

public class DoltCli : IDoltCli
{
    private readonly string _doltPath;
    private readonly string _repositoryPath;
    private readonly ILogger<DoltCli> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public DoltCli(DoltConfiguration config, ILogger<DoltCli> logger)
    {
        _doltPath = config.DoltExecutablePath ?? "dolt";
        _repositoryPath = config.RepositoryPath;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
    }

    // ==================== Core Execution Methods ====================

    private async Task<CommandResult> ExecuteAsync(params string[] args)
    {
        _logger.LogDebug("Executing: dolt {Args}", string.Join(" ", args));
        
        try
        {
            var result = await Cli.Wrap(_doltPath)
                .WithArguments(args)
                .WithWorkingDirectory(_repositoryPath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            var cmdResult = new CommandResult(
                Success: result.ExitCode == 0,
                Output: result.StandardOutput,
                Error: result.StandardError,
                ExitCode: result.ExitCode
            );

            if (!cmdResult.Success)
            {
                _logger.LogWarning("Dolt command failed: {Error}", cmdResult.Error);
            }

            return cmdResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute dolt command");
            return new CommandResult(false, "", ex.Message, -1);
        }
    }

    private async Task<string> ExecuteSqlJsonAsync(string sql)
    {
        var result = await ExecuteAsync("sql", "-q", sql, "-r", "json");
        if (!result.Success)
        {
            throw new DoltException($"SQL query failed: {result.Error}");
        }
        return result.Output;
    }

    // ==================== Branch Operations ====================

    public async Task<string> GetCurrentBranchAsync()
    {
        var json = await ExecuteSqlJsonAsync("SELECT active_branch() as branch");
        var rows = JsonSerializer.Deserialize<JsonElement>(json);
        return rows.GetProperty("rows")[0].GetProperty("branch").GetString();
    }

    public async Task<IEnumerable<BranchInfo>> ListBranchesAsync()
    {
        var result = await ExecuteAsync("branch", "-v");
        if (!result.Success) return Enumerable.Empty<BranchInfo>();

        var branches = new List<BranchInfo>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var isCurrent = line.StartsWith("*");
            var parts = line.TrimStart('*', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                branches.Add(new BranchInfo(parts[0], isCurrent, parts[1]));
            }
        }
        return branches;
    }

    public async Task<CommandResult> CheckoutAsync(string branchName, bool createNew = false)
    {
        return createNew
            ? await ExecuteAsync("checkout", "-b", branchName)
            : await ExecuteAsync("checkout", branchName);
    }

    public async Task<CommandResult> CreateBranchAsync(string branchName)
    {
        return await ExecuteAsync("branch", branchName);
    }

    public async Task<CommandResult> DeleteBranchAsync(string branchName, bool force = false)
    {
        return force
            ? await ExecuteAsync("branch", "-D", branchName)
            : await ExecuteAsync("branch", "-d", branchName);
    }

    // ==================== Commit Operations ====================

    public async Task<CommandResult> AddAllAsync()
    {
        return await ExecuteAsync("add", "-A");
    }

    public async Task<CommandResult> AddAsync(params string[] tables)
    {
        var args = new[] { "add" }.Concat(tables).ToArray();
        return await ExecuteAsync(args);
    }

    public async Task<CommitResult> CommitAsync(string message)
    {
        var result = await ExecuteAsync("commit", "-m", message);
        
        string commitHash = null;
        if (result.Success)
        {
            // Parse commit hash from output like "commit abc123def456"
            var match = Regex.Match(result.Output, @"commit\s+([a-f0-9]+)");
            commitHash = match.Success ? match.Groups[1].Value : await GetHeadCommitHashAsync();
        }

        return new CommitResult(result.Success, commitHash, result.Success ? message : result.Error);
    }

    public async Task<string> GetHeadCommitHashAsync()
    {
        var json = await ExecuteSqlJsonAsync("SELECT DOLT_HASHOF('HEAD') as hash");
        var rows = JsonSerializer.Deserialize<JsonElement>(json);
        return rows.GetProperty("rows")[0].GetProperty("hash").GetString();
    }

    public async Task<IEnumerable<CommitInfo>> GetLogAsync(int limit = 10)
    {
        var json = await ExecuteSqlJsonAsync(
            $"SELECT commit_hash, message, committer, date FROM dolt_log LIMIT {limit}");
        
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        var commits = new List<CommitInfo>();
        
        foreach (var row in result.GetProperty("rows").EnumerateArray())
        {
            commits.Add(new CommitInfo(
                row.GetProperty("commit_hash").GetString(),
                row.GetProperty("message").GetString(),
                row.GetProperty("committer").GetString(),
                DateTime.Parse(row.GetProperty("date").GetString())
            ));
        }
        
        return commits;
    }

    // ==================== Remote Operations ====================

    public async Task<CommandResult> AddRemoteAsync(string name, string url)
    {
        return await ExecuteAsync("remote", "add", name, url);
    }

    public async Task<CommandResult> PushAsync(string remote = "origin", string branch = null)
    {
        branch ??= await GetCurrentBranchAsync();
        return await ExecuteAsync("push", remote, branch);
    }

    public async Task<PullResult> PullAsync(string remote = "origin", string branch = null)
    {
        branch ??= await GetCurrentBranchAsync();
        var result = await ExecuteAsync("pull", remote, branch);

        return new PullResult(
            Success: result.Success,
            WasFastForward: result.Output.Contains("Fast-forward"),
            HasConflicts: result.Output.Contains("CONFLICT") || result.Error.Contains("CONFLICT"),
            Message: result.Success ? result.Output : result.Error
        );
    }

    public async Task<CommandResult> FetchAsync(string remote = "origin")
    {
        return await ExecuteAsync("fetch", remote);
    }

    // ==================== Merge Operations ====================

    public async Task<MergeResult> MergeAsync(string sourceBranch)
    {
        var result = await ExecuteAsync("merge", sourceBranch);
        
        string mergeCommitHash = null;
        if (result.Success && !result.Output.Contains("CONFLICT"))
        {
            mergeCommitHash = await GetHeadCommitHashAsync();
        }

        return new MergeResult(
            Success: result.Success && !result.Output.Contains("CONFLICT"),
            HasConflicts: result.Output.Contains("CONFLICT") || result.Error.Contains("CONFLICT"),
            MergeCommitHash: mergeCommitHash,
            Message: result.Output + result.Error
        );
    }

    public async Task<bool> HasConflictsAsync()
    {
        var json = await ExecuteSqlJsonAsync(
            "SELECT COUNT(*) as cnt FROM dolt_conflicts WHERE table_name IS NOT NULL");
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        var count = result.GetProperty("rows")[0].GetProperty("cnt").GetInt32();
        return count > 0;
    }

    public async Task<IEnumerable<ConflictInfo>> GetConflictsAsync(string tableName)
    {
        var result = await ExecuteAsync("conflicts", "cat", tableName);
        // Parse conflict output - format varies by table structure
        // Implementation depends on your table schema
        return ParseConflicts(result.Output, tableName);
    }

    public async Task<CommandResult> ResolveConflictsAsync(string tableName, ConflictResolution resolution)
    {
        var strategy = resolution == ConflictResolution.Ours ? "--ours" : "--theirs";
        return await ExecuteAsync("conflicts", "resolve", strategy, tableName);
    }

    // ==================== Diff Operations ====================

    public async Task<IEnumerable<DiffRow>> GetTableDiffAsync(
        string fromCommit, 
        string toCommit, 
        string tableName)
    {
        // Use DOLT_DIFF table function for structured diff data
        var sql = $@"
            SELECT 
                diff_type,
                to_{GetIdColumn(tableName)} as source_id,
                from_content_hash,
                to_content_hash,
                to_content
            FROM DOLT_DIFF('{fromCommit}', '{toCommit}', '{tableName}')";
        
        var json = await ExecuteSqlJsonAsync(sql);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        
        var diffs = new List<DiffRow>();
        foreach (var row in result.GetProperty("rows").EnumerateArray())
        {
            diffs.Add(new DiffRow(
                DiffType: row.GetProperty("diff_type").GetString(),
                SourceId: row.GetProperty("source_id").GetString(),
                FromContentHash: row.TryGetProperty("from_content_hash", out var fch) ? fch.GetString() : null,
                ToContentHash: row.TryGetProperty("to_content_hash", out var tch) ? tch.GetString() : null,
                ToContent: row.TryGetProperty("to_content", out var tc) ? tc.GetString() : null,
                Metadata: new Dictionary<string, object>()
            ));
        }
        
        return diffs;
    }

    // ==================== Reset Operations ====================

    public async Task<CommandResult> ResetHardAsync(string commitHash)
    {
        return await ExecuteAsync("reset", "--hard", commitHash);
    }

    public async Task<CommandResult> ResetSoftAsync(string commitRef = "HEAD~1")
    {
        return await ExecuteAsync("reset", "--soft", commitRef);
    }

    // ==================== SQL Operations ====================

    public async Task<string> QueryJsonAsync(string sql)
    {
        return await ExecuteSqlJsonAsync(sql);
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql) where T : new()
    {
        var json = await ExecuteSqlJsonAsync(sql);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        
        var items = new List<T>();
        foreach (var row in result.GetProperty("rows").EnumerateArray())
        {
            items.Add(JsonSerializer.Deserialize<T>(row.GetRawText(), _jsonOptions));
        }
        return items;
    }

    public async Task<int> ExecuteAsync(string sql)
    {
        var result = await ExecuteAsync("sql", "-q", sql);
        if (!result.Success)
        {
            throw new DoltException($"SQL execution failed: {result.Error}");
        }
        
        // Parse affected rows from output if available
        var match = Regex.Match(result.Output, @"(\d+)\s+row");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    public async Task<T> ExecuteScalarAsync<T>(string sql)
    {
        var json = await ExecuteSqlJsonAsync(sql);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        var firstRow = result.GetProperty("rows")[0];
        var firstProperty = firstRow.EnumerateObject().First();
        return JsonSerializer.Deserialize<T>(firstProperty.Value.GetRawText());
    }

    // ==================== Helper Methods ====================

    private string GetIdColumn(string tableName) => tableName switch
    {
        "issue_logs" => "log_id",
        "knowledge_docs" => "doc_id",
        "projects" => "project_id",
        _ => "id"
    };

    private IEnumerable<ConflictInfo> ParseConflicts(string output, string tableName)
    {
        // Implementation depends on conflict output format
        // This is a placeholder - actual implementation needed
        return Enumerable.Empty<ConflictInfo>();
    }
}

public class DoltException : Exception
{
    public DoltException(string message) : base(message) { }
    public DoltException(string message, Exception inner) : base(message, inner) { }
}
```

---

## 4. Database Schema Design

### 4.1 Core Tables (Generalized Schema)

The schema is designed to be **collection-agnostic**, supporting any ChromaDB collection regardless of its metadata structure. All metadata is preserved in JSON format, ensuring no data loss during bidirectional sync.

> **Design Note**: See [Appendix D: Schema Design Rationale](#appendix-d-schema-design-rationale) for discussion of alternative approaches and the rationale for this generalized design.

```sql
-- ============================================
-- COLLECTION REGISTRY
-- ============================================

CREATE TABLE collections (
    collection_name VARCHAR(255) PRIMARY KEY,
    display_name VARCHAR(255),
    description TEXT,
    embedding_model VARCHAR(100),              -- Model used for embeddings
    chunk_size INT DEFAULT 512,                -- Chunking parameters for this collection
    chunk_overlap INT DEFAULT 50,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    document_count INT DEFAULT 0,
    metadata JSON                              -- Collection-level metadata
);

-- ============================================
-- GENERALIZED DOCUMENT STORAGE
-- ============================================

CREATE TABLE documents (
    doc_id VARCHAR(64) NOT NULL,              -- From ChromaDB source_id or generated UUID
    collection_name VARCHAR(255) NOT NULL,    -- Which ChromaDB collection this belongs to
    
    -- Content
    content LONGTEXT NOT NULL,                -- Full document text (reassembled from chunks)
    content_hash CHAR(64) NOT NULL,           -- SHA-256 for change detection
    
    -- Optional extracted fields for common queries (nullable)
    title VARCHAR(500),                       -- Extracted from metadata if present
    doc_type VARCHAR(100),                    -- User-defined categorization
    
    -- Flexible metadata (stores EVERYTHING from ChromaDB)
    metadata JSON NOT NULL,                   -- All ChromaDB metadata preserved exactly
    
    -- Timestamps
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    
    -- Primary key is composite (same doc_id can exist in different collections)
    PRIMARY KEY (doc_id, collection_name),
    
    -- Foreign key to collections
    FOREIGN KEY (collection_name) REFERENCES collections(collection_name) ON DELETE CASCADE,
    
    -- Indexes for common queries
    INDEX idx_collection (collection_name),
    INDEX idx_doc_type (collection_name, doc_type),
    INDEX idx_content_hash (content_hash),
    INDEX idx_updated (updated_at),
    FULLTEXT INDEX idx_title (title)
);

-- ============================================
-- SYNC STATE TRACKING
-- ============================================

CREATE TABLE chroma_sync_state (
    collection_name VARCHAR(255) PRIMARY KEY,
    last_sync_commit VARCHAR(40),
    last_sync_at DATETIME,
    document_count INT DEFAULT 0,
    chunk_count INT DEFAULT 0,
    embedding_model VARCHAR(100),
    sync_status ENUM('synced', 'pending', 'error', 'in_progress') DEFAULT 'pending',
    error_message TEXT,
    metadata JSON,
    
    FOREIGN KEY (collection_name) REFERENCES collections(collection_name) ON DELETE CASCADE
);

CREATE TABLE document_sync_log (
    id INT AUTO_INCREMENT PRIMARY KEY,
    doc_id VARCHAR(64) NOT NULL,
    collection_name VARCHAR(255) NOT NULL,
    content_hash CHAR(64) NOT NULL,
    chunk_ids JSON NOT NULL,                  -- ["doc-001_chunk_0", "doc-001_chunk_1"]
    chunk_count INT NOT NULL,
    synced_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    sync_direction ENUM('chroma_to_dolt', 'dolt_to_chroma') NOT NULL,
    sync_action ENUM('added', 'modified', 'deleted') NOT NULL,
    
    UNIQUE KEY uk_doc_collection (doc_id, collection_name),
    INDEX idx_content_hash (content_hash),
    INDEX idx_collection (collection_name),
    INDEX idx_synced_at (synced_at)
);

-- ============================================
-- OPERATION AUDIT LOG (for debugging/rollback)
-- ============================================

CREATE TABLE sync_operations (
    operation_id INT AUTO_INCREMENT PRIMARY KEY,
    operation_type ENUM('commit', 'push', 'pull', 'merge', 'checkout', 'reset', 'init') NOT NULL,
    dolt_branch VARCHAR(255) NOT NULL,
    dolt_commit_before VARCHAR(40),
    dolt_commit_after VARCHAR(40),
    collections_affected JSON,                -- ["collection1", "collection2"]
    documents_added INT DEFAULT 0,
    documents_modified INT DEFAULT 0,
    documents_deleted INT DEFAULT 0,
    chunks_processed INT DEFAULT 0,
    operation_status ENUM('started', 'completed', 'failed', 'rolled_back') NOT NULL,
    error_message TEXT,
    started_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    completed_at DATETIME,
    metadata JSON
);
```

#### Metadata Preservation

The `metadata` JSON column preserves **all** ChromaDB metadata exactly as stored. This supports arbitrary use cases:

```sql
-- Recipe database
INSERT INTO documents (doc_id, collection_name, content, content_hash, title, doc_type, metadata)
VALUES ('recipe-001', 'recipes', 'Boil pasta for 10 minutes...', 'abc123...', 'Spaghetti Carbonara', 'recipe',
        '{"cuisine": "Italian", "prep_time": 30, "servings": 4, "ingredients": ["pasta", "eggs", "bacon"]}');

-- Customer support tickets
INSERT INTO documents (doc_id, collection_name, content, content_hash, title, doc_type, metadata)
VALUES ('ticket-123', 'support', 'Customer reports login issues...', 'def456...', 'Login Bug Report', 'ticket',
        '{"customer": "Acme Corp", "priority": "high", "status": "open", "assigned_to": "jane@example.com"}');

-- Research papers
INSERT INTO documents (doc_id, collection_name, content, content_hash, title, doc_type, metadata)
VALUES ('paper-2024-001', 'research', 'Abstract: This paper explores...', 'ghi789...', 'ML in Healthcare', 'paper',
        '{"authors": ["Smith", "Jones"], "journal": "Nature", "year": 2024, "doi": "10.1234/nature.2024"}');
```

#### Querying Metadata with JSON Functions

Dolt supports MySQL JSON functions for querying metadata:

```sql
-- Find Italian recipes with prep_time under 30 minutes
SELECT doc_id, title, JSON_EXTRACT(metadata, '$.prep_time') as prep_time
FROM documents 
WHERE collection_name = 'recipes'
  AND JSON_EXTRACT(metadata, '$.cuisine') = 'Italian'
  AND JSON_EXTRACT(metadata, '$.prep_time') < 30;

-- Find high-priority open tickets
SELECT doc_id, title, JSON_EXTRACT(metadata, '$.customer') as customer
FROM documents
WHERE collection_name = 'support'
  AND JSON_EXTRACT(metadata, '$.priority') = 'high'
  AND JSON_EXTRACT(metadata, '$.status') = 'open';

-- Find papers by a specific author
SELECT doc_id, title
FROM documents
WHERE collection_name = 'research'
  AND JSON_CONTAINS(metadata, '"Smith"', '$.authors');

-- Full-text search combined with metadata filter
SELECT doc_id, title, content
FROM documents
WHERE collection_name = 'recipes'
  AND MATCH(title) AGAINST('pasta' IN NATURAL LANGUAGE MODE)
  AND JSON_EXTRACT(metadata, '$.cuisine') = 'Italian';
```

### 4.2 SQL Queries for Delta Detection (via CLI)

```csharp
public class DeltaDetector
{
    private readonly IDoltCli _dolt;

    public DeltaDetector(IDoltCli dolt)
    {
        _dolt = dolt;
    }

    /// <summary>
    /// Find documents in Dolt that need syncing to ChromaDB (new or modified)
    /// </summary>
    public async Task<IEnumerable<DocumentDelta>> GetPendingSyncDocumentsAsync(string collectionName)
    {
        var sql = $@"
            SELECT 
                d.doc_id,
                d.collection_name,
                d.content,
                d.content_hash,
                d.title,
                d.doc_type,
                d.metadata,
                CASE 
                    WHEN dsl.content_hash IS NULL THEN 'new'
                    WHEN dsl.content_hash != d.content_hash THEN 'modified'
                END as change_type
            FROM documents d
            LEFT JOIN document_sync_log dsl 
                ON dsl.doc_id = d.doc_id 
                AND dsl.collection_name = d.collection_name
            WHERE d.collection_name = '{collectionName}'
              AND (dsl.content_hash IS NULL OR dsl.content_hash != d.content_hash)";

        return await _dolt.QueryAsync<DocumentDelta>(sql);
    }

    /// <summary>
    /// Find documents deleted from Dolt but still tracked in sync log
    /// </summary>
    public async Task<IEnumerable<DeletedDocument>> GetDeletedDocumentsAsync(string collectionName)
    {
        var sql = $@"
            SELECT 
                dsl.doc_id,
                dsl.collection_name,
                dsl.chunk_ids,
                dsl.chunk_count
            FROM document_sync_log dsl
            LEFT JOIN documents d 
                ON d.doc_id = dsl.doc_id 
                AND d.collection_name = dsl.collection_name
            WHERE dsl.collection_name = '{collectionName}'
              AND d.doc_id IS NULL";

        return await _dolt.QueryAsync<DeletedDocument>(sql);
    }

    /// <summary>
    /// Get all documents in a collection from Dolt
    /// </summary>
    public async Task<IEnumerable<DocumentRecord>> GetAllDocumentsAsync(string collectionName)
    {
        var sql = $@"
            SELECT 
                doc_id,
                collection_name,
                content,
                content_hash,
                title,
                doc_type,
                metadata,
                created_at,
                updated_at
            FROM documents
            WHERE collection_name = '{collectionName}'
            ORDER BY updated_at DESC";

        return await _dolt.QueryAsync<DocumentRecord>(sql);
    }

    /// <summary>
    /// Use Dolt's native diff for efficient commit-to-commit comparison
    /// </summary>
    public async Task<IEnumerable<DiffRow>> GetCommitDiffAsync(
        string fromCommit, 
        string toCommit)
    {
        // With generalized schema, we only diff the 'documents' table
        return await _dolt.GetTableDiffAsync(fromCommit, toCommit, "documents");
    }
}

public record DocumentDelta(
    string DocId,
    string CollectionName,
    string Content,
    string ContentHash,
    string Title,
    string DocType,
    string Metadata,      // JSON string - preserved exactly
    string ChangeType     // "new" or "modified"
);

public record DeletedDocument(
    string DocId,
    string CollectionName,
    string ChunkIds,      // JSON array of chunk IDs
    int ChunkCount
);

public record DocumentRecord(
    string DocId,
    string CollectionName,
    string Content,
    string ContentHash,
    string Title,
    string DocType,
    string Metadata,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
```

### 4.3 Schema Mapping: Dolt ↔ ChromaDB

Understanding how data flows between Dolt and ChromaDB is critical for maintaining consistency across clones.

#### Key Principle: Bidirectional Sync with Clear Roles

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    BIDIRECTIONAL DATA FLOW                                   │
│                                                                             │
│   Dolt (Version Control)              ChromaDB (Working Copy)               │
│   ──────────────────────              ───────────────────────               │
│                                                                             │
│   documents (generalized)             Collection: {any collection}          │
│   ┌─────────────────────┐             ┌─────────────────────────┐           │
│   │ doc_id (PK)         │◄───────────►│ Ids: ["doc-001_chunk_0",│           │
│   │ collection_name (PK)│  sync both  │       "doc-001_chunk_1"]│           │
│   │ content             │  directions ├─────────────────────────┤           │
│   │ content_hash        │◄───────────►│ Documents: [chunk text] │           │
│   │ title (optional)    │             ├─────────────────────────┤           │
│   │ doc_type (optional) │             │ Embeddings: [vectors]   │ ◀─ Generated
│   │ metadata (JSON) ────┼─────────────│ Metadatas: [            │           │
│   │   {ANY FIELDS}      │  preserved  │   {source_id: "doc-001",│           │
│   └─────────────────────┘   exactly   │    content_hash: "...", │           │
│          ▲                            │    chunk_index: 0,      │           │
│          │                            │    ...ANY USER FIELDS..}│           │
│   document_sync_log                   │ ]                       │           │
│   ┌─────────────────────┐             ├─────────────────────────┤           │
│   │ doc_id              │             │ Distances: [...]        │ ◀─ Query-time
│   │ collection_name     │             └─────────────────────────┘           │
│   │ content_hash        │                                                   │
│   │ chunk_ids (JSON)    │                                                   │
│   │ sync_direction      │◀── Tracks which direction last synced            │
│   └─────────────────────┘                                                   │
│                                                                             │
│   KEY INSIGHT: metadata JSON preserves ALL ChromaDB metadata fields         │
│   regardless of structure - recipes, tickets, research papers, anything!    │
│                                                                             │
│   SYNC TRIGGERS:                                                            │
│   ─────────────                                                             │
│   Chroma → Dolt: Before commit (stage local changes to version control)     │
│   Dolt → Chroma: After pull/checkout/merge/reset (update working copy)      │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

**Role Clarification:**
- **ChromaDB** = Working copy where users create and edit documents with arbitrary metadata
- **Dolt** = Version control system that stores history and enables sharing
- **Neither is "always right"** - the most recently modified version wins, tracked by timestamps and hashes
- **Metadata preserved exactly** - JSON column stores all user fields without schema requirements

#### Field Mapping Reference (Bidirectional)

| Dolt Field | ChromaDB Field | Dolt → Chroma | Chroma → Dolt |
|------------|----------------|---------------|---------------|
| `doc_id` | `metadatas[].source_id` | Direct copy | Direct copy |
| `collection_name` | `metadatas[].collection_name` | Direct copy | Direct copy |
| `content` | `documents[]` | Chunk (512/50) | Reassemble chunks |
| `content_hash` | `metadatas[].content_hash` | SHA256(content) | Verify or recalculate |
| `title` | `metadatas[].title` | Extract from metadata JSON | Direct copy to extracted field |
| `doc_type` | `metadatas[].doc_type` | Extract from metadata JSON | Direct copy to extracted field |
| `metadata` (JSON) | `metadatas[].*` | Merge with system fields | **Preserve ALL fields exactly** |
| N/A | `ids[]` | Generate: `{id}_chunk_{N}` | Parse to get source_id |
| N/A | `embeddings[]` | Generate via model | Discard (regenerated) |
| N/A | `metadatas[].chunk_index` | Set during chunking | Used for reassembly |
| N/A | `metadatas[].total_chunks` | Set during chunking | Used for reassembly |
| N/A | `metadatas[].is_local_change` | N/A | Flag for unsynced changes |

**Key Insight**: The `metadata` JSON column in Dolt stores ALL ChromaDB metadata fields exactly as they were. This means any user-defined fields (cuisine, priority, authors, tags, etc.) are preserved without schema changes.

#### ChromaDB Collection Structure

```python
# What gets stored in ChromaDB for each synced document
# Example 1: Recipe document
collection.add(
    ids=["recipe-001_chunk_0", "recipe-001_chunk_1"],
    documents=["Boil pasta for 10...", "Add eggs and bacon..."],
    embeddings=[[0.1, 0.2, ...], [0.3, 0.4, ...]],
    metadatas=[
        {
            # System fields (managed by sync)
            "source_id": "recipe-001",
            "collection_name": "recipes",
            "content_hash": "abc123...",
            "chunk_index": 0,
            "total_chunks": 2,
            
            # User fields (preserved exactly from Dolt metadata JSON)
            "title": "Spaghetti Carbonara",
            "doc_type": "recipe",
            "cuisine": "Italian",
            "prep_time": 30,
            "servings": 4,
            "ingredients": ["pasta", "eggs", "bacon", "parmesan"]
        },
        {
            # Second chunk - same metadata except chunk_index
            "source_id": "recipe-001",
            "collection_name": "recipes",
            "content_hash": "abc123...",
            "chunk_index": 1,
            "total_chunks": 2,
            "title": "Spaghetti Carbonara",
            "doc_type": "recipe",
            "cuisine": "Italian",
            "prep_time": 30,
            "servings": 4,
            "ingredients": ["pasta", "eggs", "bacon", "parmesan"]
        }
    ]
)

# Example 2: Support ticket (completely different schema, same system!)
collection.add(
    ids=["ticket-123_chunk_0"],
    documents=["Customer reports login issues after password reset..."],
    embeddings=[[0.5, 0.6, ...]],
    metadatas=[
        {
            # System fields
            "source_id": "ticket-123",
            "collection_name": "support",
            "content_hash": "def456...",
            "chunk_index": 0,
            "total_chunks": 1,
            
            # User fields (completely different from recipes!)
            "title": "Login Bug Report",
            "doc_type": "ticket",
            "customer": "Acme Corp",
            "priority": "high",
            "status": "open",
            "assigned_to": "jane@example.com",
            "created_date": "2025-01-15"
        }
    ]
)
```

#### Conversion Implementation

```csharp
// File: Services/DocumentConverter.cs
public class DocumentConverter
{
    private readonly int _chunkSize;
    private readonly int _chunkOverlap;
    
    public DocumentConverter(int chunkSize = 512, int chunkOverlap = 50)
    {
        _chunkSize = chunkSize;
        _chunkOverlap = chunkOverlap;
    }
    
    /// <summary>
    /// Convert a Dolt document to ChromaDB entries.
    /// Preserves ALL metadata fields from the JSON column.
    /// </summary>
    public ChromaEntries ConvertDoltToChroma(
        DoltDocument doc, 
        string currentCommit)
    {
        // 1. Chunk the content
        var chunks = ChunkContent(doc.Content);
        
        // 2. Generate deterministic IDs
        var ids = chunks.Select((_, i) => $"{doc.DocId}_chunk_{i}").ToList();
        
        // 3. Build metadata for each chunk
        var metadatas = chunks.Select((_, i) => 
        {
            // Start with user metadata (preserved exactly)
            var metadata = doc.Metadata != null 
                ? new Dictionary<string, object>(doc.Metadata)
                : new Dictionary<string, object>();
            
            // Add/override system fields
            metadata["source_id"] = doc.DocId;
            metadata["collection_name"] = doc.CollectionName;
            metadata["content_hash"] = doc.ContentHash;
            metadata["dolt_commit"] = currentCommit;
            metadata["chunk_index"] = i;
            metadata["total_chunks"] = chunks.Count;
            
            // Extract common fields if present (for indexed queries)
            if (doc.Title != null) metadata["title"] = doc.Title;
            if (doc.DocType != null) metadata["doc_type"] = doc.DocType;
            
            return metadata;
        }).ToList();
        
        return new ChromaEntries(ids, chunks, metadatas);
    }
    
    /// <summary>
    /// Chunk content with overlap for context preservation
    /// </summary>
    public List<string> ChunkContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return new List<string> { "" };
            
        var chunks = new List<string>();
        var start = 0;
        
        while (start < content.Length)
        {
            var length = Math.Min(_chunkSize, content.Length - start);
            chunks.Add(content.Substring(start, length));
            
            // Move forward by (chunkSize - overlap)
            start += _chunkSize - _chunkOverlap;
            
            // Prevent infinite loop on small content
            if (start <= 0 && chunks.Count > 0) break;
        }
        
        return chunks;
    }
    
    /// <summary>
    /// Reconstruct chunk IDs for a document (for deletion)
    /// </summary>
    public List<string> GetChunkIds(string sourceId, int totalChunks)
    {
        return Enumerable.Range(0, totalChunks)
            .Select(i => $"{sourceId}_chunk_{i}")
            .ToList();
    }
    
    // ==================== Chroma → Dolt Conversion ====================
    
    /// <summary>
    /// Convert ChromaDB chunks back to a Dolt document.
    /// Reassembles chunked content and preserves ALL metadata.
    /// </summary>
    public DoltDocument ConvertChromaToDolt(IEnumerable<ChromaChunk> chunks)
    {
        var chunkList = chunks.OrderBy(c => c.ChunkIndex).ToList();
        
        if (!chunkList.Any())
            throw new ArgumentException("No chunks provided for conversion");
        
        // Get metadata from first chunk (all chunks share document-level metadata)
        var firstChunk = chunkList.First();
        var metadata = new Dictionary<string, object>(firstChunk.Metadata);
        
        // Reassemble content from chunks
        var content = ReassembleContent(chunkList);
        
        // Calculate content hash for the reassembled content
        var contentHash = ComputeContentHash(content);
        
        // Extract system fields, remove from user metadata
        var docId = ExtractAndRemove(metadata, "source_id") ?? Guid.NewGuid().ToString();
        var collectionName = ExtractAndRemove(metadata, "collection_name") ?? "default";
        var title = ExtractAndRemove(metadata, "title");
        var docType = ExtractAndRemove(metadata, "doc_type");
        
        // Remove other system fields from user metadata
        ExtractAndRemove(metadata, "content_hash");
        ExtractAndRemove(metadata, "dolt_commit");
        ExtractAndRemove(metadata, "chunk_index");
        ExtractAndRemove(metadata, "total_chunks");
        ExtractAndRemove(metadata, "is_local_change");
        
        return new DoltDocument(
            DocId: docId,
            CollectionName: collectionName,
            Content: content,
            ContentHash: contentHash,
            Title: title,
            DocType: docType,
            Metadata: metadata  // Remaining fields = user metadata (preserved exactly)
        );
    }
    
    private string ExtractAndRemove(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var value))
        {
            dict.Remove(key);
            return value?.ToString();
        }
        return null;
    }
    
    /// <summary>
    /// Reassemble original content from ordered chunks.
    /// Handles overlap by detecting and removing duplicated text.
    /// </summary>
    public string ReassembleContent(List<ChromaChunk> orderedChunks)
    {
        if (orderedChunks.Count == 0) return "";
        if (orderedChunks.Count == 1) return orderedChunks[0].Document;
        
        var result = new StringBuilder();
        result.Append(orderedChunks[0].Document);
        
        for (int i = 1; i < orderedChunks.Count; i++)
        {
            var currentChunk = orderedChunks[i].Document;
            var previousChunk = orderedChunks[i - 1].Document;
            
            // Find overlap: the end of previous chunk should match start of current
            var overlapLength = FindOverlap(previousChunk, currentChunk);
            
            // Append only the non-overlapping part
            if (overlapLength > 0 && overlapLength < currentChunk.Length)
            {
                result.Append(currentChunk.Substring(overlapLength));
            }
            else
            {
                // No overlap detected, append full chunk (shouldn't happen with proper chunking)
                result.Append(currentChunk);
            }
        }
        
        return result.ToString();
    }
    
    /// <summary>
    /// Find the overlap length between end of first string and start of second
    /// </summary>
    private int FindOverlap(string first, string second)
    {
        // Start with expected overlap size and search for match
        var maxOverlap = Math.Min(_chunkOverlap + 10, Math.Min(first.Length, second.Length));
        
        for (int overlapLen = maxOverlap; overlapLen > 0; overlapLen--)
        {
            var endOfFirst = first.Substring(first.Length - overlapLen);
            var startOfSecond = second.Substring(0, overlapLen);
            
            if (endOfFirst == startOfSecond)
            {
                return overlapLen;
            }
        }
        
        return 0; // No overlap found
    }
    
    /// <summary>
    /// Compute SHA-256 hash of content
    /// </summary>
    public string ComputeContentHash(string content)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    
    /// <summary>
    /// Extract source_id from a chunk ID (e.g., "recipe-001_chunk_0" → "recipe-001")
    /// </summary>
    public string ExtractSourceIdFromChunkId(string chunkId)
    {
        var lastChunkIndex = chunkId.LastIndexOf("_chunk_");
        return lastChunkIndex > 0 ? chunkId.Substring(0, lastChunkIndex) : chunkId;
    }
    
    /// <summary>
    /// Group ChromaDB results by source document
    /// </summary>
    public Dictionary<string, List<ChromaChunk>> GroupChunksByDocument(IEnumerable<ChromaChunk> chunks)
    {
        return chunks
            .GroupBy(c => c.Metadata.TryGetValue("source_id", out var sid) ? sid.ToString() : "unknown")
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.ChunkIndex).ToList());
    }
}

/// <summary>
/// Represents a single chunk from ChromaDB
/// </summary>
public record ChromaChunk(
    string Id,
    string Document,
    Dictionary<string, object> Metadata,
    float[] Embedding = null
)
{
    public int ChunkIndex => Metadata.TryGetValue("chunk_index", out var idx) ? Convert.ToInt32(idx) : 0;
    public int TotalChunks => Metadata.TryGetValue("total_chunks", out var tc) ? Convert.ToInt32(tc) : 1;
    public string SourceId => Metadata.TryGetValue("source_id", out var sid) ? sid.ToString() : null;
    public string CollectionName => Metadata.TryGetValue("collection_name", out var cn) ? cn.ToString() : null;
    public string ContentHash => Metadata.TryGetValue("content_hash", out var ch) ? ch.ToString() : null;
    public bool IsLocalChange => Metadata.TryGetValue("is_local_change", out var lc) && Convert.ToBoolean(lc);
}

/// <summary>
/// Generalized document record for Dolt storage.
/// All user metadata is preserved in the Metadata dictionary.
/// </summary>
public record DoltDocument(
    string DocId,
    string CollectionName,
    string Content,
    string ContentHash,
    string Title = null,                          // Extracted for indexed queries
    string DocType = null,                        // Extracted for indexed queries
    Dictionary<string, object> Metadata = null   // ALL other fields preserved exactly
);

public record ChromaEntries(
    List<string> Ids,
    List<string> Documents,
    List<Dictionary<string, object>> Metadatas
);
```

#### Reverse Lookup: ChromaDB → Dolt

When query results need full document context:

```csharp
/// <summary>
/// Fetch full document from Dolt based on ChromaDB query result
/// </summary>
public async Task<FullDocumentResult> GetFullDocumentAsync(
    ChromaQueryResult chromaResult)
{
    var sourceId = chromaResult.Metadata["source_id"].ToString();
    var sourceTable = chromaResult.Metadata["source_table"].ToString();
    
    // Query Dolt for full document
    var sql = sourceTable == "issue_logs"
        ? $"SELECT * FROM issue_logs WHERE log_id = '{sourceId}'"
        : $"SELECT * FROM knowledge_docs WHERE doc_id = '{sourceId}'";
    
    var fullDoc = (await _dolt.QueryAsync<dynamic>(sql)).FirstOrDefault();
    
    return new FullDocumentResult
    {
        // From ChromaDB (the matching chunk)
        MatchedChunkText = chromaResult.Document,
        MatchedChunkIndex = (int)chromaResult.Metadata["chunk_index"],
        Distance = chromaResult.Distance,
        
        // From Dolt (full context)
        FullContent = fullDoc?.content,
        SourceId = sourceId,
        SourceTable = sourceTable,
        Title = fullDoc?.title,
        Metadata = fullDoc
    };
}

public record FullDocumentResult
{
    public string MatchedChunkText { get; init; }
    public int MatchedChunkIndex { get; init; }
    public float Distance { get; init; }
    public string FullContent { get; init; }
    public string SourceId { get; init; }
    public string SourceTable { get; init; }
    public string Title { get; init; }
    public dynamic Metadata { get; init; }
}
```

### 4.4 Ensuring Consistency Across Clones

#### The Consistency Challenge

ChromaDB embeddings are **not directly portable** because:
1. They're derived data (can be regenerated from source)
2. They're model-specific (different embedding models = incompatible vectors)
3. ChromaDB doesn't have built-in version control

#### Solution: Regenerate from Dolt State

When cloning or pulling, ChromaDB is regenerated from Dolt's versioned state:

```
Clone A (Developer 1)                    Clone B (Developer 2)
─────────────────────                    ─────────────────────
1. dolt clone org/repo                   1. dolt clone org/repo
   ↓                                        ↓
2. Dolt contains:                        2. Dolt contains: (IDENTICAL)
   • issue_logs (3 rows)                    • issue_logs (3 rows)
   • knowledge_docs (2 rows)                • knowledge_docs (2 rows)
   • document_sync_log (empty*)             • document_sync_log (empty*)
   • chroma_sync_state (empty*)             • chroma_sync_state (empty*)
   ↓                                        ↓
3. MCP Server starts                     3. MCP Server starts
   ↓                                        ↓
4. Detects: No sync state for            4. Detects: No sync state for
   current branch collection                current branch collection
   ↓                                        ↓
5. Full sync triggered:                  5. Full sync triggered:
   • Read all docs from Dolt                • Read all docs from Dolt
   • Chunk each document                    • Chunk each document
   • Generate embeddings                    • Generate embeddings
   • Store in ChromaDB                      • Store in ChromaDB
   • Update sync state in Dolt              • Update sync state in Dolt
   ↓                                        ↓
6. Result: ChromaDB matches              6. Result: ChromaDB matches
   Dolt state exactly                       Dolt state exactly

* sync tables may have data if synced before push, but will be validated
```

#### Consistency Guarantee Matrix

| Property | Clone A | Clone B | Guaranteed Match? |
|----------|---------|---------|-------------------|
| Dolt document content | ✓ | ✓ | Yes (versioned) |
| Dolt content_hash | ✓ | ✓ | Yes (versioned) |
| ChromaDB chunk text | ✓ | ✓ | Yes (deterministic chunking) |
| ChromaDB chunk IDs | ✓ | ✓ | Yes (deterministic: `{id}_chunk_{N}`) |
| ChromaDB embeddings | ✓ | ✓ | Yes* (same model = same vectors) |
| Query result ranking | ✓ | ✓ | Yes (same embeddings = same distances) |

*Embeddings match only if both clones use identical embedding model and version.

#### Sync Validation Implementation

```csharp
// File: Services/SyncValidator.cs
public class SyncValidator
{
    private readonly IDoltCli _dolt;
    private readonly IChromaManager _chromaManager;
    private readonly ILogger<SyncValidator> _logger;
    
    public SyncValidator(
        IDoltCli dolt, 
        IChromaManager chromaManager,
        ILogger<SyncValidator> logger)
    {
        _dolt = dolt;
        _chromaManager = chromaManager;
        _logger = logger;
    }
    
    /// <summary>
    /// Validate that ChromaDB collection matches Dolt state
    /// </summary>
    public async Task<ValidationResult> ValidateCollectionAsync(string collectionName)
    {
        var issues = new List<string>();
        
        // 1. Check if sync state exists
        var syncState = await GetSyncStateAsync(collectionName);
        if (syncState == null)
        {
            return new ValidationResult
            {
                IsValid = false,
                NeedsFullSync = true,
                Issues = new[] { "No sync state found - full sync required" }
            };
        }
        
        // 2. Check if commits match
        var currentCommit = await _dolt.GetHeadCommitHashAsync();
        if (syncState.LastSyncCommit != currentCommit)
        {
            return new ValidationResult
            {
                IsValid = false,
                NeedsIncrementalSync = true,
                Issues = new[] { $"Sync commit {syncState.LastSyncCommit} != HEAD {currentCommit}" }
            };
        }
        
        // 3. Validate embedding model matches
        var configuredModel = _configuration.EmbeddingModel;
        if (syncState.EmbeddingModel != configuredModel)
        {
            issues.Add($"Model mismatch: collection uses '{syncState.EmbeddingModel}', " +
                      $"system configured for '{configuredModel}'");
        }
        
        // 4. Spot-check document count
        var doltDocCount = await GetDoltDocumentCountAsync();
        if (Math.Abs(syncState.DocumentCount - doltDocCount) > 0)
        {
            issues.Add($"Document count mismatch: Dolt has {doltDocCount}, " +
                      $"sync state shows {syncState.DocumentCount}");
        }
        
        // 5. Validate content hashes match (sample check)
        var hashMismatches = await ValidateContentHashesAsync(collectionName);
        issues.AddRange(hashMismatches);
        
        return new ValidationResult
        {
            IsValid = !issues.Any(),
            NeedsFullSync = issues.Any(i => i.Contains("Model mismatch")),
            Issues = issues
        };
    }
    
    /// <summary>
    /// Validate that content hashes in ChromaDB metadata match Dolt
    /// </summary>
    private async Task<List<string>> ValidateContentHashesAsync(string collectionName)
    {
        var issues = new List<string>();
        
        // Get sync log entries
        var syncLogs = await _dolt.QueryAsync<SyncLogEntry>(
            $"SELECT source_id, content_hash, chunk_ids FROM document_sync_log " +
            $"WHERE chroma_collection = '{collectionName}'");
        
        foreach (var log in syncLogs.Take(10)) // Sample check first 10
        {
            var chunkIds = JsonSerializer.Deserialize<List<string>>(log.ChunkIds);
            if (chunkIds == null || !chunkIds.Any()) continue;
            
            // Get first chunk from ChromaDB
            var chromaDoc = await _chromaManager.GetAsync(collectionName, chunkIds.First());
            if (chromaDoc == null)
            {
                issues.Add($"Document {log.SourceId}: chunk not found in ChromaDB");
                continue;
            }
            
            var chromaHash = chromaDoc.Metadata["content_hash"]?.ToString();
            if (chromaHash != log.ContentHash)
            {
                issues.Add($"Document {log.SourceId}: hash mismatch " +
                          $"(Dolt: {log.ContentHash}, Chroma: {chromaHash})");
            }
        }
        
        return issues;
    }
    
    /// <summary>
    /// Ensure embedding model consistency before sync
    /// </summary>
    public async Task<ModelValidationResult> ValidateEmbeddingModelAsync(string collectionName)
    {
        var syncState = await GetSyncStateAsync(collectionName);
        if (syncState == null)
        {
            return new ModelValidationResult { IsCompatible = true, IsNewCollection = true };
        }
        
        var configuredModel = _configuration.EmbeddingModel;
        var storedModel = syncState.EmbeddingModel;
        
        if (storedModel == configuredModel)
        {
            return new ModelValidationResult { IsCompatible = true };
        }
        
        return new ModelValidationResult
        {
            IsCompatible = false,
            StoredModel = storedModel,
            ConfiguredModel = configuredModel,
            Message = $"Collection was created with '{storedModel}' but system is configured " +
                     $"for '{configuredModel}'. Options:\n" +
                     $"  1. Regenerate collection with new model (recommended)\n" +
                     $"  2. Change configuration to use '{storedModel}'"
        };
    }
    
    private async Task<SyncState> GetSyncStateAsync(string collectionName)
    {
        try
        {
            var results = await _dolt.QueryAsync<SyncState>(
                $"SELECT * FROM chroma_sync_state WHERE collection_name = '{collectionName}'");
            return results.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
    
    private async Task<int> GetDoltDocumentCountAsync()
    {
        var issueCount = await _dolt.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM issue_logs");
        var knowledgeCount = await _dolt.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM knowledge_docs");
        return issueCount + knowledgeCount;
    }
}

public record SyncState(
    string CollectionName,
    string LastSyncCommit,
    DateTime? LastSyncAt,
    int DocumentCount,
    int ChunkCount,
    string EmbeddingModel,
    string SyncStatus
);

public record SyncLogEntry(
    string SourceId,
    string ContentHash,
    string ChunkIds
);

public record ValidationResult
{
    public bool IsValid { get; init; }
    public bool NeedsFullSync { get; init; }
    public bool NeedsIncrementalSync { get; init; }
    public IEnumerable<string> Issues { get; init; } = Array.Empty<string>();
}

public record ModelValidationResult
{
    public bool IsCompatible { get; init; }
    public bool IsNewCollection { get; init; }
    public string StoredModel { get; init; }
    public string ConfiguredModel { get; init; }
    public string Message { get; init; }
}
```

### 4.5 The document_sync_log Table: Critical for Bidirectional Mapping

The `document_sync_log` table is the **key bridge** between Dolt and ChromaDB:

```sql
-- Example state after syncing 3 documents:
SELECT * FROM document_sync_log;

| id | source_table  | source_id | content_hash     | chroma_collection | chunk_ids                                    | synced_at           |
|----|---------------|-----------|------------------|-------------------|----------------------------------------------|---------------------|
| 1  | issue_logs    | log-001   | abc123...        | vmrag_main        | ["log-001_chunk_0","log-001_chunk_1"]        | 2025-01-15 10:30:00 |
| 2  | issue_logs    | log-002   | def456...        | vmrag_main        | ["log-002_chunk_0"]                          | 2025-01-15 10:30:01 |
| 3  | knowledge_docs| doc-001   | ghi789...        | vmrag_main        | ["doc-001_chunk_0","doc-001_chunk_1","..."]  | 2025-01-15 10:30:02 |
```

**This table enables:**

1. **Forward lookup** (Dolt → ChromaDB): "What chunks exist for this document?"
2. **Reverse lookup** (ChromaDB → Dolt): "What document does this chunk belong to?"
3. **Change detection**: Compare `content_hash` to detect modifications
4. **Deletion tracking**: Find orphaned sync entries when documents are deleted
5. **Chunk cleanup**: Know exactly which ChromaDB IDs to delete when updating

---

## 5. Delta Detection & Sync Processing

### 5.1 Bidirectional Sync Model

The MCP server operates with ChromaDB as the **working copy** where users directly create, read, update, and delete documents. Dolt serves as the **version control layer** for history, branching, and sharing via DoltHub.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         USER WORKFLOW                                        │
│                                                                             │
│   1. User adds/edits documents ──────────────────► ChromaDB (working copy)  │
│                                                           │                 │
│   2. User commits changes ───► Chroma → Dolt sync ───────►│                 │
│                                                           ▼                 │
│   3. Dolt commits version ◄───────────────────────── Dolt (version control) │
│                                                           │                 │
│   4. User pushes to DoltHub ─────────────────────────────►│ ───► DoltHub    │
│                                                                             │
│   5. Collaborator pulls ◄──────────────────────────────── DoltHub           │
│                                                           │                 │
│   6. Dolt → Chroma sync ─────────────────────────────────►│                 │
│                                                           ▼                 │
│   7. Collaborator sees changes ◄──────────────────── ChromaDB               │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 5.2 Operation Processing Matrix

| Operation | Pre-Action | Main Action | Post-Action | Sync Direction |
|-----------|------------|-------------|-------------|----------------|
| **Add Document** | N/A | Write to ChromaDB | Mark as local change | User → Chroma |
| **Edit Document** | N/A | Update in ChromaDB | Mark as local change | User → Chroma |
| **Delete Document** | N/A | Remove from ChromaDB | Mark as pending delete | User → Chroma |
| **Stage Changes** | Detect local changes | Write to Dolt tables | Clear local flags | Chroma → Dolt |
| **Commit** | Stage if auto-stage | `dolt add -A` + `commit` | Update sync state | Chroma → Dolt |
| **Push** | Verify committed | `dolt push` | N/A | Dolt → Remote |
| **Pull** | Check local changes | `dolt pull` | Sync to ChromaDB | Remote → Dolt → Chroma |
| **Checkout** | Check local changes | `dolt checkout` | Load branch collection | Dolt → Chroma |
| **Merge** | Check local changes | `dolt merge` | Sync merged docs | Dolt → Chroma |
| **Reset** | Warn about local loss | `dolt reset --hard` | Regenerate ChromaDB | Dolt → Chroma |
| **Initialize DB** | Create Dolt repo | Import from ChromaDB | Full Chroma → Dolt | Chroma → Dolt |

### 5.3 Use Cases for Chroma → Dolt Sync

#### Use Case 1: New Project Initialization
A user starts a new learning database in ChromaDB and wants to enable version control:

```
1. User creates documents in ChromaDB via MCP tools
2. User decides to enable version management
3. System initializes Dolt repository
4. System syncs ALL ChromaDB documents to Dolt tables
5. System creates initial commit
6. User can now push to DoltHub for sharing
```

#### Use Case 2: Local Edits Before Commit
A user modifies documents in the working copy and wants to commit:

```
1. User edits/adds/deletes documents in ChromaDB
2. ChromaDB tracks changes via is_local_change flag
3. User runs commit command
4. System detects local changes in ChromaDB
5. System syncs changes to Dolt tables (INSERT/UPDATE/DELETE)
6. System commits in Dolt
7. Local change flags are cleared
```

#### Use Case 3: Offline Work with Later Sync
A user works offline, making many changes:

```
1. User makes multiple document changes over time
2. Each change is tracked in ChromaDB metadata
3. User comes online and commits
4. System batches all local changes to Dolt
5. Single commit captures all offline work
```

### 5.4 Chroma → Dolt Delta Detection

```csharp
// File: Services/ChromaToDoltDetector.cs
public class ChromaToDoltDetector
{
    private readonly IChromaManager _chromaManager;
    private readonly IDoltCli _dolt;
    private readonly DocumentConverter _converter;
    
    public ChromaToDoltDetector(
        IChromaManager chromaManager, 
        IDoltCli dolt,
        DocumentConverter converter)
    {
        _chromaManager = chromaManager;
        _dolt = dolt;
        _converter = converter;
    }
    
    /// <summary>
    /// Find all documents in ChromaDB that have local changes not yet in Dolt
    /// </summary>
    public async Task<LocalChanges> DetectLocalChangesAsync(string collectionName)
    {
        var changes = new LocalChanges();
        
        // Method 1: Check is_local_change flag (for tracked changes)
        var flaggedChanges = await GetFlaggedLocalChangesAsync(collectionName);
        
        // Method 2: Compare content hashes (for comprehensive check)
        var hashMismatches = await CompareContentHashesAsync(collectionName);
        
        // Method 3: Find documents only in Chroma (new documents)
        var chromaOnlyDocs = await FindChromaOnlyDocumentsAsync(collectionName);
        
        // Method 4: Find documents only in Dolt (deleted from Chroma)
        var deletedDocs = await FindDeletedDocumentsAsync(collectionName);
        
        // Combine and deduplicate
        changes.NewDocuments = chromaOnlyDocs;
        changes.ModifiedDocuments = hashMismatches
            .Union(flaggedChanges.Where(f => !chromaOnlyDocs.Any(n => n.SourceId == f.SourceId)))
            .ToList();
        changes.DeletedDocuments = deletedDocs;
        
        return changes;
    }
    
    /// <summary>
    /// Get documents flagged as locally modified
    /// </summary>
    private async Task<List<ChromaDocument>> GetFlaggedLocalChangesAsync(string collectionName)
    {
        // Query ChromaDB for documents with is_local_change = true
        var results = await _chromaManager.QueryByMetadataAsync(
            collectionName,
            new Dictionary<string, object> { ["is_local_change"] = true }
        );
        
        return GroupAndConvertChunks(results);
    }
    
    /// <summary>
    /// Compare content hashes between ChromaDB and Dolt
    /// </summary>
    private async Task<List<ChromaDocument>> CompareContentHashesAsync(string collectionName)
    {
        var mismatches = new List<ChromaDocument>();
        
        // Get all unique source_ids from ChromaDB
        var chromaDocs = await GetAllChromaDocumentsAsync(collectionName);
        
        foreach (var chromaDoc in chromaDocs)
        {
            // Get corresponding Dolt document
            var doltHash = await GetDoltContentHashAsync(chromaDoc.SourceTable, chromaDoc.SourceId);
            
            if (doltHash == null)
            {
                // Document doesn't exist in Dolt - it's new
                continue; // Handled by FindChromaOnlyDocumentsAsync
            }
            
            if (doltHash != chromaDoc.ContentHash)
            {
                // Content has changed
                mismatches.Add(chromaDoc);
            }
        }
        
        return mismatches;
    }
    
    /// <summary>
    /// Find documents that exist in ChromaDB but not in Dolt
    /// </summary>
    private async Task<List<ChromaDocument>> FindChromaOnlyDocumentsAsync(string collectionName)
    {
        var chromaOnly = new List<ChromaDocument>();
        var chromaDocs = await GetAllChromaDocumentsAsync(collectionName);
        
        foreach (var chromaDoc in chromaDocs)
        {
            var existsInDolt = await DocumentExistsInDoltAsync(chromaDoc.SourceTable, chromaDoc.SourceId);
            if (!existsInDolt)
            {
                chromaOnly.Add(chromaDoc);
            }
        }
        
        return chromaOnly;
    }
    
    /// <summary>
    /// Find documents that exist in Dolt but were deleted from ChromaDB
    /// </summary>
    private async Task<List<DeletedDocument>> FindDeletedDocumentsAsync(string collectionName)
    {
        var deleted = new List<DeletedDocument>();
        
        // Get all document IDs from Dolt
        var doltIssueLogs = await _dolt.QueryAsync<IdRecord>("SELECT log_id FROM issue_logs");
        var doltKnowledgeDocs = await _dolt.QueryAsync<IdRecord>("SELECT doc_id FROM knowledge_docs");
        
        // Get all source_ids from ChromaDB
        var chromaSourceIds = await GetAllChromaSourceIdsAsync(collectionName);
        
        // Find Dolt documents not in ChromaDB
        foreach (var doltDoc in doltIssueLogs)
        {
            if (!chromaSourceIds.Contains(doltDoc.Id))
            {
                deleted.Add(new DeletedDocument("issue_logs", doltDoc.Id, collectionName));
            }
        }
        
        foreach (var doltDoc in doltKnowledgeDocs)
        {
            if (!chromaSourceIds.Contains(doltDoc.Id))
            {
                deleted.Add(new DeletedDocument("knowledge_docs", doltDoc.Id, collectionName));
            }
        }
        
        return deleted;
    }
    
    /// <summary>
    /// Get all documents from ChromaDB, grouped by source_id
    /// </summary>
    private async Task<List<ChromaDocument>> GetAllChromaDocumentsAsync(string collectionName)
    {
        var allChunks = await _chromaManager.GetAllAsync(collectionName);
        var grouped = _converter.GroupChunksByDocument(allChunks);
        
        return grouped.Select(g => 
        {
            var doltDoc = _converter.ConvertChromaToDolt(g.Value.Select(c => 
                new ChromaChunk(c.Id, c.Document, c.Metadata)));
            
            return new ChromaDocument(
                SourceTable: doltDoc.SourceTable,
                SourceId: doltDoc.SourceId,
                Content: doltDoc.Content,
                ContentHash: doltDoc.ContentHash,
                Metadata: g.Value.First().Metadata,
                Chunks: g.Value
            );
        }).ToList();
    }
    
    private async Task<string> GetDoltContentHashAsync(string table, string id)
    {
        var idColumn = table == "issue_logs" ? "log_id" : "doc_id";
        try
        {
            return await _dolt.ExecuteScalarAsync<string>(
                $"SELECT content_hash FROM {table} WHERE {idColumn} = '{id}'");
        }
        catch
        {
            return null;
        }
    }
    
    private async Task<bool> DocumentExistsInDoltAsync(string table, string id)
    {
        var idColumn = table == "issue_logs" ? "log_id" : "doc_id";
        try
        {
            var count = await _dolt.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {table} WHERE {idColumn} = '{id}'");
            return count > 0;
        }
        catch
        {
            return false;
        }
    }
    
    private async Task<HashSet<string>> GetAllChromaSourceIdsAsync(string collectionName)
    {
        var allChunks = await _chromaManager.GetAllAsync(collectionName);
        return allChunks
            .Select(c => c.Metadata.TryGetValue("source_id", out var sid) ? sid.ToString() : null)
            .Where(id => id != null)
            .ToHashSet();
    }
    
    private List<ChromaDocument> GroupAndConvertChunks(IEnumerable<ChromaChunkResult> chunks)
    {
        var grouped = chunks
            .GroupBy(c => c.Metadata.TryGetValue("source_id", out var sid) ? sid.ToString() : "unknown");
        
        return grouped.Select(g =>
        {
            var orderedChunks = g.OrderBy(c => 
                c.Metadata.TryGetValue("chunk_index", out var idx) ? Convert.ToInt32(idx) : 0).ToList();
            
            var doltDoc = _converter.ConvertChromaToDolt(orderedChunks.Select(c =>
                new ChromaChunk(c.Id, c.Document, c.Metadata)));
            
            return new ChromaDocument(
                doltDoc.SourceTable,
                doltDoc.SourceId,
                doltDoc.Content,
                doltDoc.ContentHash,
                orderedChunks.First().Metadata,
                orderedChunks
            );
        }).ToList();
    }
}

// Supporting types
public record LocalChanges
{
    public List<ChromaDocument> NewDocuments { get; set; } = new();
    public List<ChromaDocument> ModifiedDocuments { get; set; } = new();
    public List<DeletedDocument> DeletedDocuments { get; set; } = new();
    
    public bool HasChanges => NewDocuments.Any() || ModifiedDocuments.Any() || DeletedDocuments.Any();
    public int TotalChanges => NewDocuments.Count + ModifiedDocuments.Count + DeletedDocuments.Count;
}

public record ChromaDocument(
    string SourceTable,
    string SourceId,
    string Content,
    string ContentHash,
    Dictionary<string, object> Metadata,
    IEnumerable<dynamic> Chunks
);

public record IdRecord(string Id);
```

### 5.5 Chroma → Dolt Sync Implementation

```csharp
// File: Services/ChromaToDoltSyncer.cs
public class ChromaToDoltSyncer
{
    private readonly IDoltCli _dolt;
    private readonly IChromaManager _chromaManager;
    private readonly ChromaToDoltDetector _detector;
    private readonly DocumentConverter _converter;
    private readonly ILogger<ChromaToDoltSyncer> _logger;
    
    public ChromaToDoltSyncer(
        IDoltCli dolt,
        IChromaManager chromaManager,
        ChromaToDoltDetector detector,
        DocumentConverter converter,
        ILogger<ChromaToDoltSyncer> logger)
    {
        _dolt = dolt;
        _chromaManager = chromaManager;
        _detector = detector;
        _converter = converter;
        _logger = logger;
    }
    
    /// <summary>
    /// Stage all local ChromaDB changes to Dolt (like 'git add')
    /// </summary>
    public async Task<StageResult> StageLocalChangesAsync(string collectionName)
    {
        var result = new StageResult();
        
        _logger.LogInformation("Detecting local changes in collection {Collection}", collectionName);
        
        // Detect all local changes
        var changes = await _detector.DetectLocalChangesAsync(collectionName);
        
        if (!changes.HasChanges)
        {
            _logger.LogInformation("No local changes to stage");
            result.Status = StageStatus.NoChanges;
            return result;
        }
        
        _logger.LogInformation(
            "Found {New} new, {Modified} modified, {Deleted} deleted documents",
            changes.NewDocuments.Count,
            changes.ModifiedDocuments.Count,
            changes.DeletedDocuments.Count);
        
        try
        {
            // Process new documents
            foreach (var doc in changes.NewDocuments)
            {
                await InsertDocumentToDoltAsync(doc);
                await ClearLocalChangeFlagAsync(collectionName, doc.SourceId);
                result.Added++;
            }
            
            // Process modified documents
            foreach (var doc in changes.ModifiedDocuments)
            {
                await UpdateDocumentInDoltAsync(doc);
                await ClearLocalChangeFlagAsync(collectionName, doc.SourceId);
                result.Modified++;
            }
            
            // Process deleted documents
            foreach (var deleted in changes.DeletedDocuments)
            {
                await DeleteDocumentFromDoltAsync(deleted);
                result.Deleted++;
            }
            
            result.Status = StageStatus.Completed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stage local changes");
            result.Status = StageStatus.Failed;
            result.ErrorMessage = ex.Message;
        }
        
        return result;
    }
    
    /// <summary>
    /// Initialize Dolt repository from existing ChromaDB collection.
    /// Used when enabling version control on an existing knowledge base.
    /// </summary>
    public async Task<InitResult> InitializeFromChromaAsync(
        string collectionName,
        string repositoryPath,
        string initialCommitMessage = "Initial import from ChromaDB")
    {
        var result = new InitResult();
        
        _logger.LogInformation("Initializing Dolt repository from ChromaDB collection {Collection}", collectionName);
        
        try
        {
            // 1. Initialize Dolt repository
            var initResult = await _dolt.InitAsync();
            if (!initResult.Success)
            {
                throw new Exception($"Failed to initialize Dolt: {initResult.Error}");
            }
            
            // 2. Create schema tables
            await CreateSchemaTablesAsync();
            
            // 3. Get all documents from ChromaDB
            var chromaDocs = await GetAllChromaDocumentsAsync(collectionName);
            
            _logger.LogInformation("Found {Count} documents to import", chromaDocs.Count);
            
            // 4. Insert all documents into Dolt
            foreach (var doc in chromaDocs)
            {
                await InsertDocumentToDoltAsync(doc);
                result.DocumentsImported++;
            }
            
            // 5. Create initial commit
            await _dolt.AddAllAsync();
            var commitResult = await _dolt.CommitAsync(initialCommitMessage);
            
            if (!commitResult.Success)
            {
                throw new Exception($"Failed to create initial commit: {commitResult.Message}");
            }
            
            // 6. Initialize sync state
            await InitializeSyncStateAsync(collectionName, commitResult.CommitHash, chromaDocs.Count);
            
            // 7. Clear all local change flags (now committed)
            await ClearAllLocalChangeFlagsAsync(collectionName);
            
            result.Status = InitStatus.Completed;
            result.CommitHash = commitResult.CommitHash;
            
            _logger.LogInformation(
                "Successfully initialized Dolt repository with {Count} documents at commit {Commit}",
                result.DocumentsImported, result.CommitHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize from ChromaDB");
            result.Status = InitStatus.Failed;
            result.ErrorMessage = ex.Message;
        }
        
        return result;
    }
    
    /// <summary>
    /// Insert a new document into Dolt tables
    /// </summary>
    private async Task InsertDocumentToDoltAsync(ChromaDocument doc)
    {
        var table = doc.SourceTable;
        var escapedContent = doc.Content.Replace("'", "''");
        var escapedTitle = doc.Metadata.TryGetValue("title", out var t) 
            ? t.ToString().Replace("'", "''") : "";
        
        string sql;
        if (table == "issue_logs")
        {
            var projectId = doc.Metadata.TryGetValue("project_id", out var pid) ? pid.ToString() : "default";
            var issueNumber = doc.Metadata.TryGetValue("issue_number", out var inum) ? Convert.ToInt32(inum) : 0;
            var logType = doc.Metadata.TryGetValue("log_type", out var lt) ? lt.ToString() : "implementation";
            
            sql = $@"
                INSERT INTO issue_logs (log_id, project_id, issue_number, title, content, content_hash, log_type, created_at, updated_at)
                VALUES ('{doc.SourceId}', '{projectId}', {issueNumber}, '{escapedTitle}', '{escapedContent}', '{doc.ContentHash}', '{logType}', NOW(), NOW())
                ON DUPLICATE KEY UPDATE
                    content = '{escapedContent}',
                    content_hash = '{doc.ContentHash}',
                    title = '{escapedTitle}',
                    updated_at = NOW()";
        }
        else // knowledge_docs
        {
            var category = doc.Metadata.TryGetValue("category", out var cat) ? cat.ToString() : "general";
            var toolName = doc.Metadata.TryGetValue("tool_name", out var tn) ? tn.ToString() : "unknown";
            var toolVersion = doc.Metadata.TryGetValue("tool_version", out var tv) ? tv.ToString() : "";
            
            sql = $@"
                INSERT INTO knowledge_docs (doc_id, category, tool_name, tool_version, title, content, content_hash, created_at, updated_at)
                VALUES ('{doc.SourceId}', '{category}', '{toolName}', '{toolVersion}', '{escapedTitle}', '{escapedContent}', '{doc.ContentHash}', NOW(), NOW())
                ON DUPLICATE KEY UPDATE
                    content = '{escapedContent}',
                    content_hash = '{doc.ContentHash}',
                    title = '{escapedTitle}',
                    updated_at = NOW()";
        }
        
        await _dolt.ExecuteAsync(sql);
        
        _logger.LogDebug("Inserted/updated document {Id} in {Table}", doc.SourceId, table);
    }
    
    /// <summary>
    /// Update an existing document in Dolt
    /// </summary>
    private async Task UpdateDocumentInDoltAsync(ChromaDocument doc)
    {
        // InsertDocumentToDoltAsync uses ON DUPLICATE KEY UPDATE, so it handles both
        await InsertDocumentToDoltAsync(doc);
    }
    
    /// <summary>
    /// Delete a document from Dolt
    /// </summary>
    private async Task DeleteDocumentFromDoltAsync(DeletedDocument doc)
    {
        var idColumn = doc.SourceTable == "issue_logs" ? "log_id" : "doc_id";
        var sql = $"DELETE FROM {doc.SourceTable} WHERE {idColumn} = '{doc.SourceId}'";
        
        await _dolt.ExecuteAsync(sql);
        
        // Also clean up sync log
        await _dolt.ExecuteAsync(
            $"DELETE FROM document_sync_log WHERE source_table = '{doc.SourceTable}' AND source_id = '{doc.SourceId}'");
        
        _logger.LogDebug("Deleted document {Id} from {Table}", doc.SourceId, doc.SourceTable);
    }
    
    /// <summary>
    /// Clear the local change flag for a document in ChromaDB
    /// </summary>
    private async Task ClearLocalChangeFlagAsync(string collectionName, string sourceId)
    {
        await _chromaManager.UpdateMetadataAsync(
            collectionName,
            sourceId,
            new Dictionary<string, object> { ["is_local_change"] = false }
        );
    }
    
    /// <summary>
    /// Clear all local change flags in a collection
    /// </summary>
    private async Task ClearAllLocalChangeFlagsAsync(string collectionName)
    {
        var flaggedDocs = await _chromaManager.QueryByMetadataAsync(
            collectionName,
            new Dictionary<string, object> { ["is_local_change"] = true }
        );
        
        foreach (var doc in flaggedDocs)
        {
            var sourceId = doc.Metadata.TryGetValue("source_id", out var sid) ? sid.ToString() : null;
            if (sourceId != null)
            {
                await ClearLocalChangeFlagAsync(collectionName, sourceId);
            }
        }
    }
    
    /// <summary>
    /// Create the Dolt schema tables if they don't exist
    /// </summary>
    private async Task CreateSchemaTablesAsync()
    {
        // Projects table
        await _dolt.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS projects (
                project_id VARCHAR(36) PRIMARY KEY,
                name VARCHAR(255) NOT NULL,
                repository_url VARCHAR(500),
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                metadata JSON
            )");
        
        // Ensure default project exists
        await _dolt.ExecuteAsync(@"
            INSERT IGNORE INTO projects (project_id, name, created_at)
            VALUES ('default', 'Default Project', NOW())");
        
        // Issue logs table
        await _dolt.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS issue_logs (
                log_id VARCHAR(36) PRIMARY KEY,
                project_id VARCHAR(36) NOT NULL DEFAULT 'default',
                issue_number INT NOT NULL DEFAULT 0,
                title VARCHAR(500),
                content LONGTEXT NOT NULL,
                content_hash CHAR(64) NOT NULL,
                log_type VARCHAR(50) DEFAULT 'implementation',
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                metadata JSON,
                INDEX idx_content_hash (content_hash),
                INDEX idx_project_issue (project_id, issue_number)
            )");
        
        // Knowledge docs table
        await _dolt.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS knowledge_docs (
                doc_id VARCHAR(36) PRIMARY KEY,
                category VARCHAR(100) NOT NULL DEFAULT 'general',
                tool_name VARCHAR(255) NOT NULL DEFAULT 'unknown',
                tool_version VARCHAR(50),
                title VARCHAR(500) NOT NULL,
                content LONGTEXT NOT NULL,
                content_hash CHAR(64) NOT NULL,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                metadata JSON,
                INDEX idx_content_hash (content_hash),
                INDEX idx_tool (tool_name, tool_version),
                INDEX idx_category (category)
            )");
        
        // Sync state table
        await _dolt.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS chroma_sync_state (
                collection_name VARCHAR(255) PRIMARY KEY,
                last_sync_commit VARCHAR(40),
                last_sync_at DATETIME,
                document_count INT DEFAULT 0,
                chunk_count INT DEFAULT 0,
                embedding_model VARCHAR(100),
                sync_status VARCHAR(50) DEFAULT 'pending',
                error_message TEXT,
                metadata JSON
            )");
        
        // Document sync log table
        await _dolt.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS document_sync_log (
                id INT AUTO_INCREMENT PRIMARY KEY,
                source_table VARCHAR(50) NOT NULL,
                source_id VARCHAR(36) NOT NULL,
                content_hash CHAR(64) NOT NULL,
                chroma_collection VARCHAR(255) NOT NULL,
                chunk_ids JSON,
                synced_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                embedding_model VARCHAR(100),
                sync_action VARCHAR(20) NOT NULL,
                sync_direction VARCHAR(20) DEFAULT 'dolt_to_chroma',
                UNIQUE KEY uk_source_collection (source_table, source_id, chroma_collection),
                INDEX idx_content_hash (content_hash),
                INDEX idx_collection (chroma_collection)
            )");
        
        // Sync operations audit table
        await _dolt.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS sync_operations (
                operation_id INT AUTO_INCREMENT PRIMARY KEY,
                operation_type VARCHAR(50) NOT NULL,
                dolt_branch VARCHAR(255) NOT NULL,
                dolt_commit_before VARCHAR(40),
                dolt_commit_after VARCHAR(40),
                chroma_collections_affected JSON,
                documents_added INT DEFAULT 0,
                documents_modified INT DEFAULT 0,
                documents_deleted INT DEFAULT 0,
                chunks_processed INT DEFAULT 0,
                operation_status VARCHAR(50) NOT NULL,
                error_message TEXT,
                started_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                completed_at DATETIME,
                metadata JSON
            )");
    }
    
    private async Task InitializeSyncStateAsync(string collectionName, string commitHash, int docCount)
    {
        await _dolt.ExecuteAsync($@"
            INSERT INTO chroma_sync_state 
                (collection_name, last_sync_commit, last_sync_at, document_count, sync_status)
            VALUES 
                ('{collectionName}', '{commitHash}', NOW(), {docCount}, 'synced')
            ON DUPLICATE KEY UPDATE
                last_sync_commit = '{commitHash}',
                last_sync_at = NOW(),
                document_count = {docCount},
                sync_status = 'synced'");
    }
    
    private async Task<List<ChromaDocument>> GetAllChromaDocumentsAsync(string collectionName)
    {
        var allChunks = await _chromaManager.GetAllAsync(collectionName);
        var grouped = _converter.GroupChunksByDocument(allChunks);
        
        return grouped.Select(g =>
        {
            var doltDoc = _converter.ConvertChromaToDolt(g.Value.Select(c =>
                new ChromaChunk(c.Id, c.Document, c.Metadata)));
            
            return new ChromaDocument(
                doltDoc.SourceTable,
                doltDoc.SourceId,
                doltDoc.Content,
                doltDoc.ContentHash,
                g.Value.First().Metadata,
                g.Value
            );
        }).ToList();
    }
}

// Supporting types
public record StageResult
{
    public StageStatus Status { get; set; }
    public int Added { get; set; }
    public int Modified { get; set; }
    public int Deleted { get; set; }
    public string ErrorMessage { get; set; }
    
    public int TotalChanges => Added + Modified + Deleted;
}

public enum StageStatus { Completed, NoChanges, Failed }

public record InitResult
{
    public InitStatus Status { get; set; }
    public int DocumentsImported { get; set; }
    public string CommitHash { get; set; }
    public string ErrorMessage { get; set; }
}

public enum InitStatus { Completed, Failed }
```

### 5.6 Updated SyncManager with Bidirectional Support

```csharp
// File: Services/SyncManager.cs
public class SyncManager : ISyncManager
{
    private readonly IDoltCli _dolt;
    private readonly IChromaManager _chromaManager;
    private readonly IEmbeddingService _embeddingService;
    private readonly DeltaDetector _deltaDetector;
    private readonly ChromaToDoltSyncer _chromaToDoltSyncer;
    private readonly ChromaToDoltDetector _chromaToDoltDetector;
    private readonly DocumentConverter _converter;
    private readonly ILogger<SyncManager> _logger;

    public SyncManager(
        IDoltCli dolt,
        IChromaManager chromaManager,
        IEmbeddingService embeddingService,
        ChromaToDoltSyncer chromaToDoltSyncer,
        ChromaToDoltDetector chromaToDoltDetector,
        DocumentConverter converter,
        ILogger<SyncManager> logger)
    {
        _dolt = dolt;
        _chromaManager = chromaManager;
        _embeddingService = embeddingService;
        _chromaToDoltSyncer = chromaToDoltSyncer;
        _chromaToDoltDetector = chromaToDoltDetector;
        _converter = converter;
        _deltaDetector = new DeltaDetector(dolt);
        _logger = logger;
    }

    // ==================== Bidirectional Commit Processing ====================

    /// <summary>
    /// Process a commit with automatic Chroma → Dolt staging.
    /// This is the primary commit method that handles the full bidirectional workflow.
    /// </summary>
    public async Task<SyncResult> ProcessCommitAsync(
        string message, 
        bool autoStageFromChroma = true,
        bool syncBackToChroma = false)
    {
        var result = new SyncResult();
        var branch = await _dolt.GetCurrentBranchAsync();
        var collectionName = GetCollectionName(branch);
        var beforeCommit = await _dolt.GetHeadCommitHashAsync();

        var operationId = await LogOperationStartAsync("commit", branch, beforeCommit);

        try
        {
            // STEP 1: Stage local ChromaDB changes to Dolt (Chroma → Dolt)
            if (autoStageFromChroma)
            {
                _logger.LogInformation("Auto-staging local ChromaDB changes to Dolt");
                
                var stageResult = await _chromaToDoltSyncer.StageLocalChangesAsync(collectionName);
                
                if (stageResult.Status == StageStatus.Failed)
                {
                    await LogOperationFailedAsync(operationId, stageResult.ErrorMessage);
                    result.Status = SyncStatus.Failed;
                    result.ErrorMessage = $"Failed to stage local changes: {stageResult.ErrorMessage}";
                    return result;
                }
                
                result.StagedFromChroma = stageResult.TotalChanges;
                _logger.LogInformation(
                    "Staged {Count} changes from ChromaDB ({Added} added, {Modified} modified, {Deleted} deleted)",
                    stageResult.TotalChanges, stageResult.Added, stageResult.Modified, stageResult.Deleted);
            }

            // STEP 2: Stage and commit in Dolt
            await _dolt.AddAllAsync();
            var commitResult = await _dolt.CommitAsync(message);
            
            if (!commitResult.Success)
            {
                // Check if it's just "nothing to commit"
                if (commitResult.Message.Contains("nothing to commit"))
                {
                    result.Status = SyncStatus.NoChanges;
                    result.ErrorMessage = "No changes to commit";
                    await LogOperationCompletedAsync(operationId, beforeCommit, result);
                    return result;
                }
                
                await LogOperationFailedAsync(operationId, commitResult.Message);
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = commitResult.Message;
                return result;
            }

            var afterCommit = commitResult.CommitHash;
            result.CommitHash = afterCommit;
            
            // STEP 3 (Optional): Sync committed changes back to ChromaDB
            // This is typically not needed since changes came FROM ChromaDB,
            // but useful for updating metadata like dolt_commit hash
            if (syncBackToChroma)
            {
                await UpdateChromaMetadataWithCommitAsync(collectionName, afterCommit);
            }

            // STEP 4: Update sync state
            await UpdateSyncStateAsync(branch, afterCommit, result);

            await LogOperationCompletedAsync(operationId, afterCommit, result);
            result.Status = SyncStatus.Completed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Commit processing failed");
            await LogOperationFailedAsync(operationId, ex.Message);
            throw;
        }

        return result;
    }

    // ==================== Local Change Detection ====================

    /// <summary>
    /// Check if there are uncommitted local changes in ChromaDB
    /// </summary>
    public async Task<LocalChanges> GetLocalChangesAsync()
    {
        var branch = await _dolt.GetCurrentBranchAsync();
        var collectionName = GetCollectionName(branch);
        return await _chromaToDoltDetector.DetectLocalChangesAsync(collectionName);
    }

    /// <summary>
    /// Get status summary showing local changes and sync state
    /// </summary>
    public async Task<StatusSummary> GetStatusAsync()
    {
        var branch = await _dolt.GetCurrentBranchAsync();
        var collectionName = GetCollectionName(branch);
        var currentCommit = await _dolt.GetHeadCommitHashAsync();
        
        var localChanges = await _chromaToDoltDetector.DetectLocalChangesAsync(collectionName);
        var doltStatus = await _dolt.GetStatusAsync();
        
        return new StatusSummary
        {
            Branch = branch,
            CurrentCommit = currentCommit,
            CollectionName = collectionName,
            LocalChanges = localChanges,
            HasUncommittedDoltChanges = doltStatus.HasStagedChanges || doltStatus.HasUnstagedChanges,
            HasUncommittedChromaChanges = localChanges.HasChanges
        };
    }

    // ==================== Initialize from ChromaDB ====================

    /// <summary>
    /// Initialize version control for an existing ChromaDB collection.
    /// Creates Dolt repository and imports all documents.
    /// </summary>
    public async Task<InitResult> InitializeVersionControlAsync(
        string collectionName,
        string initialCommitMessage = "Initial import from ChromaDB")
    {
        _logger.LogInformation("Initializing version control for collection {Collection}", collectionName);
        return await _chromaToDoltSyncer.InitializeFromChromaAsync(
            collectionName, 
            _dolt.RepositoryPath, 
            initialCommitMessage);
    }

    // ==================== Pull Processing (with local change warning) ====================

    public async Task<SyncResult> ProcessPullAsync(string remote = "origin", bool force = false)
    {
        var result = new SyncResult();
        var branch = await _dolt.GetCurrentBranchAsync();
        var collectionName = GetCollectionName(branch);
        var beforeCommit = await _dolt.GetHeadCommitHashAsync();

        // Check for local changes before pull
        if (!force)
        {
            var localChanges = await _chromaToDoltDetector.DetectLocalChangesAsync(collectionName);
            if (localChanges.HasChanges)
            {
                result.Status = SyncStatus.LocalChangesExist;
                result.ErrorMessage = $"You have {localChanges.TotalChanges} uncommitted local changes. " +
                    "Commit them first, or use force=true to discard local changes.";
                result.LocalChanges = localChanges;
                return result;
            }
        }

        var operationId = await LogOperationStartAsync("pull", branch, beforeCommit);

        try
        {
            // Pull from remote
            var pullResult = await _dolt.PullAsync(remote, branch);
            
            if (!pullResult.Success)
            {
                if (pullResult.HasConflicts)
                {
                    result.Status = SyncStatus.Conflicts;
                    result.ErrorMessage = "Pull resulted in conflicts. Resolve before syncing.";
                }
                else
                {
                    result.Status = SyncStatus.Failed;
                    result.ErrorMessage = pullResult.Message;
                }
                await LogOperationFailedAsync(operationId, result.ErrorMessage);
                return result;
            }

            var afterCommit = await _dolt.GetHeadCommitHashAsync();

            // If no changes (same commit), return early
            if (beforeCommit == afterCommit)
            {
                result.Status = SyncStatus.NoChanges;
                await LogOperationCompletedAsync(operationId, afterCommit, result);
                return result;
            }

            // Sync all changes between commits
            await SyncCommitRangeAsync(beforeCommit, afterCommit, branch, result);

            await LogOperationCompletedAsync(operationId, afterCommit, result);
            result.Status = SyncStatus.Completed;
            result.WasFastForward = pullResult.WasFastForward;
        }
        catch (Exception ex)
        {
            await LogOperationFailedAsync(operationId, ex.Message);
            throw;
        }

        return result;
    }

    // ==================== Checkout Processing (with local change warning) ====================

    public async Task<SyncResult> ProcessCheckoutAsync(
        string targetBranch, 
        bool createNew = false,
        bool force = false)
    {
        var result = new SyncResult();
        var previousBranch = await _dolt.GetCurrentBranchAsync();
        var previousCollection = GetCollectionName(previousBranch);

        // Check for local changes before checkout (unless creating new branch from current)
        if (!force && !createNew)
        {
            var localChanges = await _chromaToDoltDetector.DetectLocalChangesAsync(previousCollection);
            if (localChanges.HasChanges)
            {
                result.Status = SyncStatus.LocalChangesExist;
                result.ErrorMessage = $"You have {localChanges.TotalChanges} uncommitted local changes on branch '{previousBranch}'. " +
                    "Commit them first, or use force=true to discard local changes.";
                result.LocalChanges = localChanges;
                return result;
            }
        }

        var operationId = await LogOperationStartAsync("checkout", targetBranch, null);

        try
        {
            // Checkout in Dolt
            var checkoutResult = await _dolt.CheckoutAsync(targetBranch, createNew);
            
            if (!checkoutResult.Success)
            {
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = checkoutResult.Error;
                await LogOperationFailedAsync(operationId, result.ErrorMessage);
                return result;
            }

            // Get collection name for target branch
            var collectionName = GetCollectionName(targetBranch);
            var collectionExists = await _chromaManager.CollectionExistsAsync(collectionName);

            if (createNew)
            {
                // New branch: clone collection from parent branch (including local changes)
                var parentCollection = GetCollectionName(previousBranch);
                await _chromaManager.CloneCollectionAsync(parentCollection, collectionName);
                _logger.LogInformation("Created collection {Collection} from {Parent}", 
                    collectionName, parentCollection);
            }
            else if (!collectionExists)
            {
                // Existing branch but no collection: full sync from Dolt
                await FullSyncToChromaAsync(targetBranch, collectionName, result);
            }
            else
            {
                // Existing collection: check if incremental sync needed
                var lastSyncCommit = await GetLastSyncCommitAsync(collectionName);
                var currentCommit = await _dolt.GetHeadCommitHashAsync();

                if (lastSyncCommit != currentCommit)
                {
                    await SyncCommitRangeAsync(lastSyncCommit, currentCommit, targetBranch, result);
                }
            }

            var afterCommit = await _dolt.GetHeadCommitHashAsync();
            await LogOperationCompletedAsync(operationId, afterCommit, result);
            result.Status = SyncStatus.Completed;
        }
        catch (Exception ex)
        {
            await LogOperationFailedAsync(operationId, ex.Message);
            throw;
        }

        return result;
    }

    // ==================== Merge Processing (with local change warning) ====================

    public async Task<MergeSyncResult> ProcessMergeAsync(string sourceBranch, bool force = false)
    {
        var result = new MergeSyncResult();
        var targetBranch = await _dolt.GetCurrentBranchAsync();
        var collectionName = GetCollectionName(targetBranch);
        var beforeCommit = await _dolt.GetHeadCommitHashAsync();

        // Check for local changes before merge
        if (!force)
        {
            var localChanges = await _chromaToDoltDetector.DetectLocalChangesAsync(collectionName);
            if (localChanges.HasChanges)
            {
                result.Status = MergeSyncStatus.LocalChangesExist;
                result.ErrorMessage = $"You have {localChanges.TotalChanges} uncommitted local changes. " +
                    "Commit them first, or use force=true to discard local changes.";
                return result;
            }
        }

        var operationId = await LogOperationStartAsync("merge", targetBranch, beforeCommit);

        try
        {
            // Attempt merge
            var mergeResult = await _dolt.MergeAsync(sourceBranch);

            if (mergeResult.HasConflicts)
            {
                result.HasConflicts = true;
                result.Conflicts = (await _dolt.GetConflictsAsync("issue_logs"))
                    .Concat(await _dolt.GetConflictsAsync("knowledge_docs"))
                    .ToList();
                result.Status = MergeSyncStatus.ConflictsDetected;
                await LogOperationFailedAsync(operationId, "Merge conflicts detected");
                return result;
            }

            if (!mergeResult.Success)
            {
                result.Status = MergeSyncStatus.Failed;
                result.ErrorMessage = mergeResult.Message;
                await LogOperationFailedAsync(operationId, result.ErrorMessage);
                return result;
            }

            var afterCommit = mergeResult.MergeCommitHash ?? await _dolt.GetHeadCommitHashAsync();

            // Sync merged changes
            await SyncCommitRangeAsync(beforeCommit, afterCommit, targetBranch, result);

            await LogOperationCompletedAsync(operationId, afterCommit, result);
            result.Status = MergeSyncStatus.Completed;
        }
        catch (Exception ex)
        {
            await LogOperationFailedAsync(operationId, ex.Message);
            throw;
        }

        return result;
    }

    // ==================== Reset Processing ====================

    public async Task<SyncResult> ProcessResetAsync(string targetCommit)
    {
        var result = new SyncResult();
        var branch = await _dolt.GetCurrentBranchAsync();
        var beforeCommit = await _dolt.GetHeadCommitHashAsync();

        var operationId = await LogOperationStartAsync("reset", branch, beforeCommit);

        try
        {
            // Reset Dolt
            var resetResult = await _dolt.ResetHardAsync(targetCommit);
            
            if (!resetResult.Success)
            {
                result.Status = SyncStatus.Failed;
                result.ErrorMessage = resetResult.Error;
                await LogOperationFailedAsync(operationId, result.ErrorMessage);
                return result;
            }

            // Full regeneration (reset can go forward or backward)
            var collectionName = GetCollectionName(branch);
            
            // Delete existing collection
            await _chromaManager.DeleteCollectionAsync(collectionName);
            
            // Full sync from reset state
            await FullSyncToChromaAsync(branch, collectionName, result);

            await LogOperationCompletedAsync(operationId, targetCommit, result);
            result.Status = SyncStatus.Completed;
        }
        catch (Exception ex)
        {
            await LogOperationFailedAsync(operationId, ex.Message);
            throw;
        }

        return result;
    }

    // ==================== Change Detection ====================

    public async Task<bool> HasPendingChangesAsync()
    {
        var branch = await _dolt.GetCurrentBranchAsync();
        var collectionName = GetCollectionName(branch);
        var lastSyncCommit = await GetLastSyncCommitAsync(collectionName);
        var currentCommit = await _dolt.GetHeadCommitHashAsync();

        return lastSyncCommit != currentCommit;
    }

    public async Task<PendingChanges> GetPendingChangesAsync()
    {
        var branch = await _dolt.GetCurrentBranchAsync();
        var collectionName = GetCollectionName(branch);

        var pendingDocs = await _deltaDetector.GetPendingSyncDocumentsAsync(collectionName);
        var deletedDocs = await _deltaDetector.GetDeletedDocumentsAsync(collectionName);

        return new PendingChanges
        {
            NewDocuments = pendingDocs.Where(d => d.ChangeType == "new").ToList(),
            ModifiedDocuments = pendingDocs.Where(d => d.ChangeType == "modified").ToList(),
            DeletedDocuments = deletedDocs.ToList()
        };
    }

    // ==================== Helper Methods ====================

    private async Task SyncCommitRangeAsync(
        string fromCommit, 
        string toCommit, 
        string branch,
        SyncResult result)
    {
        var issueChanges = await _dolt.GetTableDiffAsync(fromCommit, toCommit, "issue_logs");
        var knowledgeChanges = await _dolt.GetTableDiffAsync(fromCommit, toCommit, "knowledge_docs");

        foreach (var change in issueChanges.Concat(knowledgeChanges))
        {
            await ProcessDiffRowAsync(change, branch, result);
        }

        await UpdateSyncStateAsync(branch, toCommit, result);
    }

    private async Task ProcessDiffRowAsync(DiffRow diff, string branch, SyncResult result)
    {
        var collectionName = GetCollectionName(branch);

        switch (diff.DiffType)
        {
            case "added":
                await AddDocumentToChromaAsync(diff, collectionName);
                result.Added++;
                break;

            case "modified":
                await UpdateDocumentInChromaAsync(diff, collectionName);
                result.Modified++;
                break;

            case "removed":
                await RemoveDocumentFromChromaAsync(diff.SourceId, collectionName);
                result.Deleted++;
                break;
        }
    }

    private async Task AddDocumentToChromaAsync(DiffRow diff, string collectionName)
    {
        // Chunk the content
        var chunks = ChunkContent(diff.ToContent);
        var chunkIds = chunks.Select((_, i) => $"{diff.SourceId}_chunk_{i}").ToList();

        // Generate embeddings
        var embeddings = await _embeddingService.EmbedAsync(chunks);

        // Build metadata for each chunk
        var metadatas = chunks.Select((_, i) => new Dictionary<string, object>
        {
            ["source_id"] = diff.SourceId,
            ["content_hash"] = diff.ToContentHash,
            ["chunk_index"] = i,
            ["total_chunks"] = chunks.Count
        }).ToList();

        // Add to ChromaDB
        await _chromaManager.AddDocumentsAsync(collectionName, chunkIds, chunks, embeddings, metadatas);

        // Update sync log
        await UpdateDocumentSyncLogAsync(diff, collectionName, chunkIds, "added");
    }

    private async Task UpdateDocumentInChromaAsync(DiffRow diff, string collectionName)
    {
        // Remove old chunks first
        await RemoveDocumentFromChromaAsync(diff.SourceId, collectionName);
        
        // Add new chunks
        await AddDocumentToChromaAsync(diff, collectionName);
    }

    private async Task RemoveDocumentFromChromaAsync(string sourceId, string collectionName)
    {
        // Get existing chunk IDs from sync log
        var sql = $@"SELECT chunk_ids FROM document_sync_log 
                     WHERE source_id = '{sourceId}' AND chroma_collection = '{collectionName}'";
        var chunkIdsJson = await _dolt.ExecuteScalarAsync<string>(sql);
        
        if (!string.IsNullOrEmpty(chunkIdsJson))
        {
            var chunkIds = JsonSerializer.Deserialize<List<string>>(chunkIdsJson);
            await _chromaManager.DeleteDocumentsAsync(collectionName, chunkIds);
        }

        // Remove from sync log
        await _dolt.ExecuteAsync(
            $"DELETE FROM document_sync_log WHERE source_id = '{sourceId}' AND chroma_collection = '{collectionName}'");
    }

    private async Task FullSyncToChromaAsync(string branch, string collectionName, SyncResult result)
    {
        _logger.LogInformation("Starting full sync for branch {Branch}", branch);

        // Create collection
        await _chromaManager.CreateCollectionAsync(collectionName, new Dictionary<string, object>
        {
            ["dolt_branch"] = branch,
            ["source"] = "dolt"
        });

        // Get all documents from both tables
        var issueLogs = await _dolt.QueryAsync<DocumentRecord>(
            "SELECT log_id as source_id, content, content_hash, project_id as identifier FROM issue_logs");
        var knowledgeDocs = await _dolt.QueryAsync<DocumentRecord>(
            "SELECT doc_id as source_id, content, content_hash, tool_name as identifier FROM knowledge_docs");

        foreach (var doc in issueLogs.Concat(knowledgeDocs))
        {
            var diff = new DiffRow("added", doc.SourceId, null, doc.ContentHash, doc.Content, new());
            await AddDocumentToChromaAsync(diff, collectionName);
            result.Added++;
        }

        var currentCommit = await _dolt.GetHeadCommitHashAsync();
        await UpdateSyncStateAsync(branch, currentCommit, result);
    }

    private string GetCollectionName(string branch)
    {
        var safeBranch = branch.Replace("/", "-").Replace("_", "-");
        if (safeBranch.Length > 20) safeBranch = safeBranch.Substring(0, 20);
        return $"vmrag_{safeBranch}";
    }

    private List<string> ChunkContent(string content, int chunkSize = 512, int overlap = 50)
    {
        var chunks = new List<string>();
        var start = 0;
        
        while (start < content.Length)
        {
            var end = Math.Min(start + chunkSize, content.Length);
            chunks.Add(content.Substring(start, end - start));
            start = end - overlap;
            if (start < 0) break;
        }
        
        return chunks;
    }

    // Sync state and logging methods...
    private async Task<string> GetLastSyncCommitAsync(string collectionName)
    {
        try
        {
            return await _dolt.ExecuteScalarAsync<string>(
                $"SELECT last_sync_commit FROM chroma_sync_state WHERE collection_name = '{collectionName}'");
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateSyncStateAsync(string branch, string commit, SyncResult result)
    {
        var collectionName = GetCollectionName(branch);
        var sql = $@"
            INSERT INTO chroma_sync_state 
                (collection_name, last_sync_commit, last_sync_at, document_count, sync_status)
            VALUES 
                ('{collectionName}', '{commit}', NOW(), {result.Added}, 'synced')
            ON DUPLICATE KEY UPDATE
                last_sync_commit = '{commit}',
                last_sync_at = NOW(),
                document_count = document_count + {result.Added} - {result.Deleted},
                sync_status = 'synced'";
        
        await _dolt.ExecuteAsync(sql);
    }

    private async Task UpdateDocumentSyncLogAsync(
        DiffRow diff, 
        string collectionName, 
        List<string> chunkIds,
        string action)
    {
        var chunkIdsJson = JsonSerializer.Serialize(chunkIds);
        var sql = $@"
            INSERT INTO document_sync_log 
                (source_table, source_id, content_hash, chroma_collection, chunk_ids, sync_action)
            VALUES 
                ('issue_logs', '{diff.SourceId}', '{diff.ToContentHash}', '{collectionName}', '{chunkIdsJson}', '{action}')
            ON DUPLICATE KEY UPDATE
                content_hash = '{diff.ToContentHash}',
                chunk_ids = '{chunkIdsJson}',
                synced_at = NOW(),
                sync_action = '{action}'";
        
        await _dolt.ExecuteAsync(sql);
    }

    private async Task<int> LogOperationStartAsync(string opType, string branch, string beforeCommit)
    {
        var sql = $@"
            INSERT INTO sync_operations 
                (operation_type, dolt_branch, dolt_commit_before, operation_status)
            VALUES 
                ('{opType}', '{branch}', '{beforeCommit ?? ""}', 'started')";
        
        await _dolt.ExecuteAsync(sql);
        return await _dolt.ExecuteScalarAsync<int>("SELECT LAST_INSERT_ID()");
    }

    private async Task LogOperationCompletedAsync(int operationId, string afterCommit, SyncResult result)
    {
        var sql = $@"
            UPDATE sync_operations SET
                dolt_commit_after = '{afterCommit}',
                documents_added = {result.Added},
                documents_modified = {result.Modified},
                documents_deleted = {result.Deleted},
                operation_status = 'completed',
                completed_at = NOW()
            WHERE operation_id = {operationId}";
        
        await _dolt.ExecuteAsync(sql);
    }

    private async Task LogOperationFailedAsync(int operationId, string error)
    {
        var escapedError = error.Replace("'", "''");
        var sql = $@"
            UPDATE sync_operations SET
                operation_status = 'failed',
                error_message = '{escapedError}',
                completed_at = NOW()
            WHERE operation_id = {operationId}";
        
        await _dolt.ExecuteAsync(sql);
    }
}

// Supporting types
public record DocumentRecord(string SourceId, string Content, string ContentHash, string Identifier);

public class SyncResult
{
    public SyncStatus Status { get; set; }
    public string CommitHash { get; set; }
    public int Added { get; set; }
    public int Modified { get; set; }
    public int Deleted { get; set; }
    public bool WasFastForward { get; set; }
    public string ErrorMessage { get; set; }
    
    // Bidirectional sync properties
    public int StagedFromChroma { get; set; }  // Documents staged from Chroma → Dolt
    public LocalChanges LocalChanges { get; set; }  // Uncommitted local changes (if any)
    public SyncDirection Direction { get; set; } = SyncDirection.DoltToChroma;
}

public class MergeSyncResult : SyncResult
{
    public new MergeSyncStatus Status { get; set; }
    public bool HasConflicts { get; set; }
    public List<ConflictInfo> Conflicts { get; set; } = new();
}

public class PendingChanges
{
    public List<DocumentDelta> NewDocuments { get; set; } = new();
    public List<DocumentDelta> ModifiedDocuments { get; set; } = new();
    public List<DeletedDocument> DeletedDocuments { get; set; } = new();
    
    public bool HasChanges => NewDocuments.Any() || ModifiedDocuments.Any() || DeletedDocuments.Any();
    public int TotalChanges => NewDocuments.Count + ModifiedDocuments.Count + DeletedDocuments.Count;
}

/// <summary>
/// Summary of current sync status for display
/// </summary>
public class StatusSummary
{
    public string Branch { get; set; }
    public string CurrentCommit { get; set; }
    public string CollectionName { get; set; }
    public LocalChanges LocalChanges { get; set; }
    public bool HasUncommittedDoltChanges { get; set; }
    public bool HasUncommittedChromaChanges { get; set; }
    
    public bool IsClean => !HasUncommittedDoltChanges && !HasUncommittedChromaChanges;
}

public enum SyncStatus 
{ 
    Completed, 
    NoChanges, 
    Failed, 
    Conflicts,
    LocalChangesExist  // New: local changes must be committed first
}

public enum MergeSyncStatus 
{ 
    Completed, 
    Failed, 
    ConflictsDetected,
    LocalChangesExist  // New: local changes must be committed first
}

public enum SyncDirection
{
    DoltToChroma,      // Dolt → ChromaDB (pull, checkout, merge, reset)
    ChromaToDolt,      // ChromaDB → Dolt (stage, commit)
    Bidirectional      // Both directions in one operation
}
```

---

## 6. Implementation Steps

### Phase 1: Core Infrastructure (Week 1)

#### Step 1.1: Project Setup

```bash
# Create project structure
mkdir -p VmRagMcp/Services
mkdir -p VmRagMcp/Models
mkdir -p VmRagMcp/Configuration
mkdir -p VmRagMcp/McpTools
```

```xml
<!-- VmRagMcp.csproj additions -->
<ItemGroup>
    <PackageReference Include="CliWrap" Version="3.6.6" />
    <PackageReference Include="System.Text.Json" Version="8.0.4" />
</ItemGroup>
```

#### Step 1.2: Implement DoltCli class

1. Create `Services/DoltCli.cs` with the implementation from Section 3.3
2. Create `Configuration/DoltConfiguration.cs`:

```csharp
public class DoltConfiguration
{
    public string DoltExecutablePath { get; set; } = "dolt";
    public string RepositoryPath { get; set; } = "./data/dolt-repo";
    public string RemoteName { get; set; } = "origin";
    public string RemoteUrl { get; set; }
}
```

#### Step 1.3: Implement DeltaDetector

Create `Services/DeltaDetector.cs` with the implementation from Section 4.2

#### Step 1.4: Implement SyncManager

Create `Services/SyncManager.cs` with the implementation from Section 5.2

### Phase 2: MCP Tool Integration (Week 2)

> **📋 Full Specification**: See **[MCP_Tools_Specification.md](./MCP_Tools_Specification.md)** for complete tool definitions, input/output schemas, and usage examples.
>
> **📖 User Guide**: See **[UserDocumentationOutline.md](./UserDocumentationOutline.md)** for LLM interaction guidelines and workflow documentation.

#### 2.1 Tool Overview

The MCP server exposes **13 tools** for version control operations. The LLM reads/writes documents via ChromaDB tools (`chroma_*`), and manages versions via these Dolt tools (`dolt_*`):

| Tool | Purpose | Category |
|------|---------|----------|
| `dolt_status` | Current branch, commit, and local changes | Information |
| `dolt_branches` | List branches on remote | Information |
| `dolt_commits` | List commits on a branch | Information |
| `dolt_show` | Show details of a specific commit | Information |
| `dolt_find` | Search commits by hash or message | Information |
| `dolt_init` | Initialize new repository | Setup |
| `dolt_clone` | Clone existing repository from DoltHub | Setup |
| `dolt_fetch` | Fetch updates from remote (no merge) | Sync |
| `dolt_pull` | Fetch and merge remote changes | Sync |
| `dolt_push` | Push local commits to remote | Sync |
| `dolt_commit` | Commit ChromaDB state to Dolt | Local |
| `dolt_checkout` | Switch branch or commit | Local |
| `dolt_reset` | Reset to specific commit | Local |
| `dolt_link_git` | Link Dolt commit to Git commit | Advanced |

#### 2.2 Core Design Principle

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         LLM INTERACTION MODEL                                │
│                                                                             │
│   DOCUMENT OPERATIONS (chroma_* tools)     VERSION CONTROL (dolt_* tools)   │
│   ────────────────────────────────────     ──────────────────────────────   │
│                                                                             │
│   chroma_add      → Add documents          dolt_status   → Check state      │
│   chroma_query    → Search documents       dolt_commit   → Save version     │
│   chroma_get      → Retrieve document      dolt_push     → Share changes    │
│   chroma_update   → Modify document        dolt_pull     → Get updates      │
│   chroma_delete   → Remove document        dolt_checkout → Switch branch    │
│                                                                             │
│   ┌─────────────┐                         ┌─────────────┐                   │
│   │  ChromaDB   │◄────── sync ───────────►│    Dolt     │                   │
│   │  (working   │                         │  (version   │                   │
│   │   copy)     │                         │   control)  │                   │
│   └─────────────┘                         └──────┬──────┘                   │
│         ▲                                        │                          │
│         │                                        ▼                          │
│   LLM reads/writes                         ┌─────────────┐                  │
│   documents here                           │   DoltHub   │                  │
│                                            │  (sharing)  │                  │
│                                            └─────────────┘                  │
└─────────────────────────────────────────────────────────────────────────────┘
```

#### 2.3 Tool Implementation

```csharp
// File: McpTools/DoltVersionControlTools.cs
using System.Text.Json;

public class DoltVersionControlTools
{
    private readonly IDoltCli _dolt;
    private readonly ISyncManager _syncManager;
    private readonly IChromaManager _chromaManager;
    private readonly IGitIntegration _gitIntegration;
    private readonly ILogger<DoltVersionControlTools> _logger;

    public DoltVersionControlTools(
        IDoltCli dolt,
        ISyncManager syncManager,
        IChromaManager chromaManager,
        IGitIntegration gitIntegration,
        ILogger<DoltVersionControlTools> logger)
    {
        _dolt = dolt;
        _syncManager = syncManager;
        _chromaManager = chromaManager;
        _gitIntegration = gitIntegration;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // INFORMATION TOOLS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get the current version control status including active branch, 
    /// current commit, and any uncommitted local changes.
    /// </summary>
    [McpTool("dolt_status")]
    [McpDescription("Get the current version control status including active branch, current commit, and any uncommitted local changes in the ChromaDB working copy.")]
    public async Task<ToolResult> StatusAsync(
        [McpParameter("verbose", "Include detailed list of changed documents", required: false)]
        bool verbose = false)
    {
        try
        {
            var status = await _syncManager.GetStatusAsync();
            var remoteStatus = await _dolt.GetRemoteStatusAsync();
            
            var result = new
            {
                branch = status.Branch,
                commit = new
                {
                    hash = status.CurrentCommit,
                    short_hash = status.CurrentCommit?.Substring(0, 7),
                    message = await _dolt.GetCommitMessageAsync(status.CurrentCommit),
                    timestamp = await _dolt.GetCommitTimestampAsync(status.CurrentCommit)
                },
                remote = new
                {
                    name = remoteStatus?.RemoteName ?? "origin",
                    url = remoteStatus?.RemoteUrl,
                    connected = remoteStatus?.IsReachable ?? false
                },
                local_changes = new
                {
                    has_changes = status.LocalChanges.HasChanges,
                    summary = new
                    {
                        added = status.LocalChanges.NewDocuments.Count,
                        modified = status.LocalChanges.ModifiedDocuments.Count,
                        deleted = status.LocalChanges.DeletedDocuments.Count,
                        total = status.LocalChanges.TotalChanges
                    },
                    documents = verbose ? status.LocalChanges.NewDocuments
                        .Concat(status.LocalChanges.ModifiedDocuments)
                        .Select(d => d.SourceId).ToList() : null
                },
                sync_state = new
                {
                    ahead = remoteStatus?.CommitsAhead ?? 0,
                    behind = remoteStatus?.CommitsBehind ?? 0,
                    diverged = remoteStatus?.HasDiverged ?? false
                }
            };

            return ToolResult.Success(result);
        }
        catch (RepositoryNotInitializedException)
        {
            return ToolResult.Error("NOT_INITIALIZED", 
                "No Dolt repository configured. Use dolt_init or dolt_clone first.",
                new[] { "Run dolt_init to create a new repository", 
                        "Run dolt_clone to clone an existing repository" });
        }
    }

    /// <summary>
    /// List all branches available on the remote Dolt repository.
    /// </summary>
    [McpTool("dolt_branches")]
    [McpDescription("List all branches available on the remote Dolt repository, including their latest commit information.")]
    public async Task<ToolResult> BranchesAsync(
        [McpParameter("include_local", "Include local-only branches not on remote", required: false)]
        bool includeLocal = true,
        [McpParameter("filter", "Filter branches by name pattern (supports * wildcard)", required: false)]
        string filter = null)
    {
        try
        {
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            var branches = await _dolt.ListBranchesAsync(includeLocal, filter);
            
            var branchInfos = new List<object>();
            foreach (var branch in branches)
            {
                var latestCommit = await _dolt.GetBranchHeadAsync(branch.Name);
                var tracking = await _dolt.GetBranchTrackingAsync(branch.Name);
                
                branchInfos.Add(new
                {
                    name = branch.Name,
                    is_current = branch.Name == currentBranch,
                    is_local = branch.IsLocal,
                    is_remote = branch.IsRemote,
                    latest_commit = new
                    {
                        hash = latestCommit?.Hash,
                        short_hash = latestCommit?.Hash?.Substring(0, 7),
                        message = latestCommit?.Message,
                        timestamp = latestCommit?.Timestamp
                    },
                    ahead = tracking?.Ahead ?? 0,
                    behind = tracking?.Behind ?? 0
                });
            }

            return ToolResult.Success(new
            {
                current_branch = currentBranch,
                branches = branchInfos,
                total_count = branchInfos.Count
            });
        }
        catch (RepositoryNotInitializedException)
        {
            return ToolResult.Error("NOT_INITIALIZED", 
                "No Dolt repository configured. Use dolt_init or dolt_clone first.");
        }
    }

    /// <summary>
    /// List commits on a specified branch with their metadata.
    /// </summary>
    [McpTool("dolt_commits")]
    [McpDescription("List commits on a specified branch, including commit messages, authors, and timestamps. Returns most recent commits first.")]
    public async Task<ToolResult> CommitsAsync(
        [McpParameter("branch", "Branch name to list commits from. Default: current branch", required: false)]
        string branch = null,
        [McpParameter("limit", "Maximum number of commits to return (1-100). Default: 20", required: false)]
        int limit = 20,
        [McpParameter("offset", "Number of commits to skip for pagination. Default: 0", required: false)]
        int offset = 0,
        [McpParameter("since", "Only show commits after this date (ISO 8601)", required: false)]
        string since = null,
        [McpParameter("until", "Only show commits before this date (ISO 8601)", required: false)]
        string until = null)
    {
        try
        {
            limit = Math.Clamp(limit, 1, 100);
            branch ??= await _dolt.GetCurrentBranchAsync();
            
            DateTime? sinceDate = since != null ? DateTime.Parse(since) : null;
            DateTime? untilDate = until != null ? DateTime.Parse(until) : null;
            
            var (commits, totalCount) = await _dolt.GetCommitsAsync(
                branch, limit, offset, sinceDate, untilDate);
            
            var commitInfos = commits.Select(c => new
            {
                hash = c.Hash,
                short_hash = c.Hash?.Substring(0, 7),
                message = c.Message,
                author = c.Author,
                timestamp = c.Timestamp.ToString("o"),
                parent_hash = c.ParentHash,
                stats = new
                {
                    documents_added = c.Stats?.Added ?? 0,
                    documents_modified = c.Stats?.Modified ?? 0,
                    documents_deleted = c.Stats?.Deleted ?? 0
                }
            }).ToList();

            return ToolResult.Success(new
            {
                branch,
                commits = commitInfos,
                total_commits = totalCount,
                has_more = offset + commits.Count < totalCount
            });
        }
        catch (BranchNotFoundException ex)
        {
            return ToolResult.Error("BRANCH_NOT_FOUND", 
                $"Branch '{ex.BranchName}' not found. Use dolt_branches to see available branches.");
        }
    }

    /// <summary>
    /// Show detailed information about a specific commit.
    /// </summary>
    [McpTool("dolt_show")]
    [McpDescription("Show detailed information about a specific commit, including the list of documents that were added, modified, or deleted.")]
    public async Task<ToolResult> ShowAsync(
        [McpParameter("commit", "Commit hash (full or short) or reference (e.g., 'HEAD', 'HEAD~1', 'main')", required: true)]
        string commit,
        [McpParameter("include_diff", "Include content diff for changed documents. Default: false", required: false)]
        bool includeDiff = false,
        [McpParameter("diff_limit", "Max documents to include diff for. Default: 10", required: false)]
        int diffLimit = 10)
    {
        try
        {
            var resolvedCommit = await _dolt.ResolveCommitRefAsync(commit);
            var commitInfo = await _dolt.GetCommitDetailsAsync(resolvedCommit);
            var changes = await _dolt.GetCommitChangesAsync(resolvedCommit);
            var branches = await _dolt.GetBranchesContainingCommitAsync(resolvedCommit);
            
            var documentChanges = new List<object>();
            var diffCount = 0;
            
            foreach (var change in changes)
            {
                var docChange = new Dictionary<string, object>
                {
                    ["doc_id"] = change.DocId,
                    ["collection"] = change.CollectionName,
                    ["change_type"] = change.ChangeType.ToString().ToLower(),
                    ["title"] = change.Title
                };
                
                if (includeDiff && diffCount < diffLimit)
                {
                    docChange["diff"] = new
                    {
                        content_before = change.ChangeType != ChangeType.Added ? change.ContentBefore : null,
                        content_after = change.ChangeType != ChangeType.Deleted ? change.ContentAfter : null,
                        metadata_changes = change.MetadataChanges
                    };
                    diffCount++;
                }
                
                documentChanges.Add(docChange);
            }

            return ToolResult.Success(new
            {
                commit = new
                {
                    hash = commitInfo.Hash,
                    short_hash = commitInfo.Hash?.Substring(0, 7),
                    message = commitInfo.Message,
                    author = commitInfo.Author,
                    timestamp = commitInfo.Timestamp.ToString("o"),
                    parent_hash = commitInfo.ParentHash
                },
                changes = new
                {
                    summary = new
                    {
                        added = changes.Count(c => c.ChangeType == ChangeType.Added),
                        modified = changes.Count(c => c.ChangeType == ChangeType.Modified),
                        deleted = changes.Count(c => c.ChangeType == ChangeType.Deleted),
                        total = changes.Count
                    },
                    documents = documentChanges
                },
                branches
            });
        }
        catch (CommitNotFoundException)
        {
            return ToolResult.Error("COMMIT_NOT_FOUND", 
                $"Commit '{commit}' not found. Use dolt_commits or dolt_find to locate commits.");
        }
    }

    /// <summary>
    /// Search for commits by partial hash or message content.
    /// </summary>
    [McpTool("dolt_find")]
    [McpDescription("Search for commits by partial hash or message content. Useful for finding specific commits when you don't have the full hash.")]
    public async Task<ToolResult> FindAsync(
        [McpParameter("query", "Search query - matches against commit hash prefix and message content", required: true)]
        string query,
        [McpParameter("search_type", "What to search: 'all' (default), 'hash', or 'message'", required: false)]
        string searchType = "all",
        [McpParameter("branch", "Limit search to specific branch. Default: all branches", required: false)]
        string branch = null,
        [McpParameter("limit", "Maximum results to return. Default: 10", required: false)]
        int limit = 10)
    {
        try
        {
            var searchMode = searchType?.ToLower() switch
            {
                "hash" => CommitSearchMode.HashOnly,
                "message" => CommitSearchMode.MessageOnly,
                _ => CommitSearchMode.All
            };
            
            var results = await _dolt.SearchCommitsAsync(query, searchMode, branch, limit);
            
            var resultInfos = results.Select(r => new
            {
                hash = r.Commit.Hash,
                short_hash = r.Commit.Hash?.Substring(0, 7),
                message = r.Commit.Message,
                author = r.Commit.Author,
                timestamp = r.Commit.Timestamp.ToString("o"),
                branch = r.Branch,
                match_type = r.MatchType.ToString().ToLower()
            }).ToList();

            return ToolResult.Success(new
            {
                query,
                results = resultInfos,
                total_found = resultInfos.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching commits");
            return ToolResult.Error("SEARCH_FAILED", ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SETUP TOOLS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initialize a new Dolt repository for version control.
    /// </summary>
    [McpTool("dolt_init")]
    [McpDescription("Initialize a new Dolt repository for version control. Use this when starting a new knowledge base. For cloning an existing repository, use dolt_clone instead.")]
    public async Task<ToolResult> InitAsync(
        [McpParameter("remote_url", "DoltHub remote URL to configure (e.g., 'myorg/my-knowledge-base')", required: false)]
        string remoteUrl = null,
        [McpParameter("initial_branch", "Name of the initial branch. Default: 'main'", required: false)]
        string initialBranch = "main",
        [McpParameter("import_existing", "Import existing ChromaDB documents into initial commit. Default: true", required: false)]
        bool importExisting = true,
        [McpParameter("commit_message", "Commit message for initial import. Default: 'Initial import from ChromaDB'", required: false)]
        string commitMessage = "Initial import from ChromaDB")
    {
        try
        {
            // Check if already initialized
            if (await _dolt.IsInitializedAsync())
            {
                return ToolResult.Error("ALREADY_INITIALIZED", 
                    "Repository already exists. Use dolt_status to check state.");
            }

            // Initialize repository
            await _dolt.InitAsync(initialBranch);
            
            // Configure remote if provided
            if (!string.IsNullOrEmpty(remoteUrl))
            {
                var fullUrl = remoteUrl.StartsWith("http") 
                    ? remoteUrl 
                    : $"https://doltremoteapi.dolthub.com/{remoteUrl}";
                await _dolt.AddRemoteAsync("origin", fullUrl);
            }

            // Import existing documents
            int documentsImported = 0;
            var collectionsImported = new List<string>();
            string commitHash = null;
            
            if (importExisting)
            {
                var collections = await _chromaManager.ListCollectionsAsync();
                foreach (var collection in collections)
                {
                    var result = await _syncManager.InitializeFromChromaAsync(collection, commitMessage);
                    documentsImported += result.DocumentsImported;
                    collectionsImported.Add(collection);
                    commitHash = result.CommitHash;
                }
            }

            return ToolResult.Success(new
            {
                success = true,
                repository = new
                {
                    path = _dolt.RepositoryPath,
                    branch = initialBranch,
                    commit = new
                    {
                        hash = commitHash,
                        message = commitMessage
                    }
                },
                remote = new
                {
                    configured = !string.IsNullOrEmpty(remoteUrl),
                    name = "origin",
                    url = remoteUrl
                },
                import_summary = new
                {
                    documents_imported = documentsImported,
                    collections = collectionsImported
                },
                message = $"Repository initialized with {documentsImported} documents." +
                    (remoteUrl != null ? " Remote 'origin' configured. Use dolt_push to upload to DoltHub." : "")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize repository");
            return ToolResult.Error("INIT_FAILED", ex.Message);
        }
    }

    /// <summary>
    /// Clone an existing Dolt repository from DoltHub.
    /// </summary>
    [McpTool("dolt_clone")]
    [McpDescription("Clone an existing Dolt repository from DoltHub. This downloads the repository and populates the local ChromaDB with documents from the specified branch/commit.")]
    public async Task<ToolResult> CloneAsync(
        [McpParameter("remote_url", "DoltHub repository URL (e.g., 'myorg/knowledge-base')", required: true)]
        string remoteUrl,
        [McpParameter("branch", "Branch to checkout after clone. Default: repository's default branch", required: false)]
        string branch = null,
        [McpParameter("commit", "Specific commit to checkout. Overrides branch if provided.", required: false)]
        string commit = null)
    {
        try
        {
            // Check if already initialized
            if (await _dolt.IsInitializedAsync())
            {
                return ToolResult.Error("ALREADY_INITIALIZED", 
                    "Repository already exists. Use dolt_reset or delete the repository first.");
            }

            // Normalize URL
            var fullUrl = remoteUrl.StartsWith("http") 
                ? remoteUrl 
                : $"https://doltremoteapi.dolthub.com/{remoteUrl}";

            // Clone repository
            await _dolt.CloneAsync(fullUrl);

            // Checkout specific branch or commit
            if (!string.IsNullOrEmpty(commit))
            {
                await _dolt.CheckoutAsync(commit);
            }
            else if (!string.IsNullOrEmpty(branch))
            {
                await _dolt.CheckoutAsync(branch);
            }

            // Get current state
            var currentBranch = await _dolt.GetCurrentBranchAsync();
            var currentCommit = await _dolt.GetHeadCommitHashAsync();
            var commitInfo = await _dolt.GetCommitDetailsAsync(currentCommit);

            // Sync to ChromaDB
            var collectionName = GetCollectionName(currentBranch);
            var syncResult = await _syncManager.FullSyncToChromaAsync(currentBranch, collectionName);

            return ToolResult.Success(new
            {
                success = true,
                repository = new
                {
                    path = _dolt.RepositoryPath,
                    remote_url = fullUrl
                },
                checkout = new
                {
                    branch = currentBranch,
                    commit = new
                    {
                        hash = currentCommit,
                        message = commitInfo.Message,
                        timestamp = commitInfo.Timestamp.ToString("o")
                    }
                },
                sync_summary = new
                {
                    documents_loaded = syncResult.Added,
                    collections_created = new[] { collectionName }
                },
                message = $"Cloned repository and loaded {syncResult.Added} documents into ChromaDB."
            });
        }
        catch (RemoteNotFoundException)
        {
            return ToolResult.Error("REMOTE_NOT_FOUND", 
                $"Repository not found at '{remoteUrl}'. Check the URL and your access permissions.");
        }
        catch (AuthenticationException)
        {
            return ToolResult.Error("AUTHENTICATION_FAILED", 
                "Not authorized to access this repository. Run 'dolt login' to authenticate.");
        }
        catch (BranchNotFoundException)
        {
            return ToolResult.Error("BRANCH_NOT_FOUND", 
                $"Branch '{branch}' not found in the repository.");
        }
        catch (CommitNotFoundException)
        {
            return ToolResult.Error("COMMIT_NOT_FOUND", 
                $"Commit '{commit}' not found in the repository.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SYNC TOOLS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fetch commits from the remote without applying them.
    /// </summary>
    [McpTool("dolt_fetch")]
    [McpDescription("Fetch commits from the remote repository without applying them to your local ChromaDB. Use this to see what changes are available before pulling.")]
    public async Task<ToolResult> FetchAsync(
        [McpParameter("remote", "Remote name to fetch from. Default: 'origin'", required: false)]
        string remote = "origin",
        [McpParameter("branch", "Specific branch to fetch. Default: all branches", required: false)]
        string branch = null)
    {
        try
        {
            var beforeState = await _dolt.GetAllBranchHeadsAsync();
            
            await _dolt.FetchAsync(remote, branch);
            
            var afterState = await _dolt.GetAllBranchHeadsAsync();
            
            // Calculate what changed
            var branchesUpdated = new List<object>();
            var newBranches = new List<string>();
            var totalCommitsFetched = 0;
            
            foreach (var (branchName, newCommit) in afterState)
            {
                if (beforeState.TryGetValue(branchName, out var oldCommit))
                {
                    if (oldCommit != newCommit)
                    {
                        var commitCount = await _dolt.GetCommitCountBetweenAsync(oldCommit, newCommit);
                        branchesUpdated.Add(new
                        {
                            name = branchName,
                            old_commit = oldCommit?.Substring(0, 7),
                            new_commit = newCommit?.Substring(0, 7),
                            commits_fetched = commitCount
                        });
                        totalCommitsFetched += commitCount;
                    }
                }
                else
                {
                    newBranches.Add(branchName);
                }
            }

            var currentBranch = await _dolt.GetCurrentBranchAsync();
            var tracking = await _dolt.GetBranchTrackingAsync(currentBranch);

            return ToolResult.Success(new
            {
                success = true,
                remote,
                updates = new
                {
                    branches_updated = branchesUpdated,
                    new_branches = newBranches,
                    total_commits_fetched = totalCommitsFetched
                },
                current_branch_status = new
                {
                    branch = currentBranch,
                    behind = tracking?.Behind ?? 0,
                    ahead = tracking?.Ahead ?? 0
                },
                message = totalCommitsFetched > 0 
                    ? $"Fetched {totalCommitsFetched} new commits. Your branch '{currentBranch}' is {tracking?.Behind ?? 0} commits behind."
                    : "Already up to date."
            });
        }
        catch (RemoteUnreachableException)
        {
            return ToolResult.Error("REMOTE_UNREACHABLE", 
                $"Cannot connect to remote '{remote}'. Check your network connection.");
        }
    }

    /// <summary>
    /// Fetch and merge changes from the remote.
    /// </summary>
    [McpTool("dolt_pull")]
    [McpDescription("Fetch changes from the remote and merge them into your current branch. This updates both the Dolt repository and the local ChromaDB with the merged content.")]
    public async Task<ToolResult> PullAsync(
        [McpParameter("remote", "Remote name to pull from. Default: 'origin'", required: false)]
        string remote = "origin",
        [McpParameter("branch", "Remote branch to pull. Default: current branch's upstream", required: false)]
        string branch = null,
        [McpParameter("if_uncommitted", "Action if local uncommitted changes exist: 'abort' (default), 'commit_first', 'reset_first', 'stash'", required: false)]
        string ifUncommitted = "abort",
        [McpParameter("commit_message", "Commit message if if_uncommitted='commit_first'", required: false)]
        string commitMessage = "Auto-commit before pull")
    {
        try
        {
            var status = await _syncManager.GetStatusAsync();
            
            // Handle uncommitted changes
            if (status.LocalChanges.HasChanges)
            {
                switch (ifUncommitted?.ToLower())
                {
                    case "abort":
                    default:
                        return ToolResult.Error("UNCOMMITTED_CHANGES",
                            $"You have {status.LocalChanges.TotalChanges} uncommitted changes.",
                            new
                            {
                                local_changes = new
                                {
                                    added = status.LocalChanges.NewDocuments.Count,
                                    modified = status.LocalChanges.ModifiedDocuments.Count,
                                    deleted = status.LocalChanges.DeletedDocuments.Count
                                }
                            },
                            new[] { 
                                "Use if_uncommitted='commit_first' to save your changes", 
                                "Use if_uncommitted='reset_first' to discard changes",
                                "Use if_uncommitted='stash' to temporarily save changes" 
                            });
                    
                    case "commit_first":
                        await _syncManager.ProcessCommitAsync(commitMessage, autoStageFromChroma: true);
                        break;
                    
                    case "reset_first":
                        await _syncManager.ProcessResetAsync("HEAD");
                        break;
                    
                    case "stash":
                        await _syncManager.StashChangesAsync();
                        break;
                }
            }

            var result = await _syncManager.ProcessPullAsync(remote);
            
            // Restore stashed changes if applicable
            if (ifUncommitted?.ToLower() == "stash" && status.LocalChanges.HasChanges)
            {
                await _syncManager.PopStashAsync();
            }

            return ToolResult.Success(new
            {
                success = result.Status == SyncStatus.Completed,
                action_taken = new
                {
                    uncommitted_handling = ifUncommitted,
                    pre_commit = ifUncommitted == "commit_first" ? new { created = true } : null
                },
                pull_result = new
                {
                    merge_type = result.WasFastForward ? "fast_forward" : "merge",
                    commits_merged = result.Added + result.Modified,
                    to_commit = result.CommitHash
                },
                sync_summary = new
                {
                    documents_added = result.Added,
                    documents_modified = result.Modified,
                    documents_deleted = result.Deleted,
                    total_changes = result.Added + result.Modified + result.Deleted
                },
                message = result.Status == SyncStatus.Completed 
                    ? $"Pull successful. {result.Added + result.Modified + result.Deleted} documents updated in ChromaDB."
                    : result.ErrorMessage
            });
        }
        catch (MergeConflictException ex)
        {
            return ToolResult.Error("MERGE_CONFLICT", 
                "Merge conflicts occurred. Manual resolution required.",
                new { conflicts = ex.Conflicts });
        }
        catch (RemoteUnreachableException)
        {
            return ToolResult.Error("REMOTE_UNREACHABLE", 
                $"Cannot connect to remote '{remote}'.");
        }
    }

    /// <summary>
    /// Push local commits to the remote repository.
    /// </summary>
    [McpTool("dolt_push")]
    [McpDescription("Push local commits to the remote Dolt repository (DoltHub). Only committed changes are pushed.")]
    public async Task<ToolResult> PushAsync(
        [McpParameter("remote", "Remote name to push to. Default: 'origin'", required: false)]
        string remote = "origin",
        [McpParameter("branch", "Branch to push. Default: current branch", required: false)]
        string branch = null,
        [McpParameter("set_upstream", "Set upstream tracking for the branch. Default: true", required: false)]
        bool setUpstream = true,
        [McpParameter("force", "Force push (WARNING: can overwrite remote changes). Default: false", required: false)]
        bool force = false)
    {
        try
        {
            branch ??= await _dolt.GetCurrentBranchAsync();
            
            // Check for uncommitted changes (warn but don't block)
            var status = await _syncManager.GetStatusAsync();
            var uncommittedWarning = status.LocalChanges.HasChanges 
                ? $"Note: You have {status.LocalChanges.TotalChanges} uncommitted local changes that will not be pushed."
                : null;

            var beforeCommit = await _dolt.GetHeadCommitHashAsync();
            var result = await _dolt.PushAsync(remote, branch, setUpstream, force);
            
            if (!result.Success)
            {
                if (result.Error.Contains("rejected"))
                {
                    return ToolResult.Error("REMOTE_REJECTED",
                        "Push rejected. The remote has changes you don't have.",
                        new[] { "Run dolt_pull first to get remote changes", 
                                "Use force=true to overwrite (not recommended)" });
                }
                return ToolResult.Error("PUSH_FAILED", result.Error);
            }

            return ToolResult.Success(new
            {
                success = true,
                push_result = new
                {
                    remote,
                    branch,
                    commits_pushed = result.CommitsPushed,
                    from_commit = beforeCommit?.Substring(0, 7),
                    to_url = await _dolt.GetRemoteUrlAsync(remote)
                },
                remote_state = new
                {
                    remote_branch = $"{remote}/{branch}",
                    remote_commit = beforeCommit?.Substring(0, 7)
                },
                warning = uncommittedWarning,
                message = $"Pushed {result.CommitsPushed} commits to {remote}/{branch}."
            });
        }
        catch (AuthenticationException)
        {
            return ToolResult.Error("AUTHENTICATION_FAILED", 
                "Not authorized to push to this repository.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // LOCAL OPERATIONS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Commit the current ChromaDB state to the Dolt repository.
    /// </summary>
    [McpTool("dolt_commit")]
    [McpDescription("Commit the current state of ChromaDB to the Dolt repository, creating a new version that can be pushed, shared, or reverted to.")]
    public async Task<ToolResult> CommitAsync(
        [McpParameter("message", "Commit message describing the changes", required: true)]
        string message,
        [McpParameter("author", "Author name/email for the commit. Default: configured user", required: false)]
        string author = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return ToolResult.Error("MESSAGE_REQUIRED", "Commit message is required.");
            }

            var result = await _syncManager.ProcessCommitAsync(message, autoStageFromChroma: true);
            
            if (result.Status == SyncStatus.NoChanges)
            {
                return ToolResult.Success(new
                {
                    success = true,
                    status = "no_changes",
                    message = "Nothing to commit. Working copy is clean."
                });
            }

            var commitInfo = await _dolt.GetCommitDetailsAsync(result.CommitHash);

            return ToolResult.Success(new
            {
                success = result.Status == SyncStatus.Completed,
                commit = new
                {
                    hash = result.CommitHash,
                    short_hash = result.CommitHash?.Substring(0, 7),
                    message = message,
                    author = commitInfo.Author,
                    timestamp = commitInfo.Timestamp.ToString("o"),
                    parent_hash = commitInfo.ParentHash
                },
                changes_committed = new
                {
                    added = result.Added,
                    modified = result.Modified,
                    deleted = result.Deleted,
                    total = result.Added + result.Modified + result.Deleted
                },
                message = $"Created commit {result.CommitHash?.Substring(0, 7)} with {result.Added + result.Modified + result.Deleted} document changes."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Commit failed");
            return ToolResult.Error("COMMIT_FAILED", ex.Message);
        }
    }

    /// <summary>
    /// Switch to a different branch or commit.
    /// </summary>
    [McpTool("dolt_checkout")]
    [McpDescription("Switch to a different branch or commit. This updates the local ChromaDB to reflect the documents at that branch/commit.")]
    public async Task<ToolResult> CheckoutAsync(
        [McpParameter("target", "Branch name or commit hash to checkout", required: true)]
        string target,
        [McpParameter("create_branch", "Create a new branch with the given name. Default: false", required: false)]
        bool createBranch = false,
        [McpParameter("from", "Base branch/commit for new branch (only with create_branch=true)", required: false)]
        string from = null,
        [McpParameter("if_uncommitted", "Action if local uncommitted changes exist: 'abort' (default), 'commit_first', 'reset_first', 'carry'", required: false)]
        string ifUncommitted = "abort",
        [McpParameter("commit_message", "Commit message if if_uncommitted='commit_first'", required: false)]
        string commitMessage = null)
    {
        try
        {
            var previousBranch = await _dolt.GetCurrentBranchAsync();
            var previousCommit = await _dolt.GetHeadCommitHashAsync();
            var status = await _syncManager.GetStatusAsync();
            
            // Handle uncommitted changes
            if (status.LocalChanges.HasChanges && ifUncommitted?.ToLower() != "carry")
            {
                switch (ifUncommitted?.ToLower())
                {
                    case "abort":
                    default:
                        return ToolResult.Error("UNCOMMITTED_CHANGES",
                            $"You have {status.LocalChanges.TotalChanges} uncommitted changes on branch '{previousBranch}'.",
                            new { local_changes = status.LocalChanges },
                            new[] { 
                                "Use if_uncommitted='commit_first' to save changes",
                                "Use if_uncommitted='reset_first' to discard changes",
                                "Use if_uncommitted='carry' to bring changes to new branch"
                            });
                    
                    case "commit_first":
                        var msg = commitMessage ?? $"WIP: Changes before checkout to {target}";
                        await _syncManager.ProcessCommitAsync(msg, autoStageFromChroma: true);
                        break;
                    
                    case "reset_first":
                        await _syncManager.ProcessResetAsync("HEAD");
                        break;
                }
            }

            // Perform checkout
            if (createBranch)
            {
                await _dolt.CreateBranchAsync(target, from);
            }
            await _dolt.CheckoutAsync(target);

            // Sync ChromaDB
            var newBranch = await _dolt.GetCurrentBranchAsync();
            var newCommit = await _dolt.GetHeadCommitHashAsync();
            var collectionName = GetCollectionName(newBranch);
            var syncResult = await _syncManager.FullSyncToChromaAsync(newBranch, collectionName);

            return ToolResult.Success(new
            {
                success = true,
                action_taken = new
                {
                    uncommitted_handling = ifUncommitted,
                    branch_created = createBranch
                },
                checkout_result = new
                {
                    from_branch = previousBranch,
                    from_commit = previousCommit?.Substring(0, 7),
                    to_branch = newBranch,
                    to_commit = newCommit?.Substring(0, 7)
                },
                sync_summary = new
                {
                    documents_added = syncResult.Added,
                    documents_modified = syncResult.Modified,
                    documents_deleted = syncResult.Deleted,
                    total_changes = syncResult.Added + syncResult.Modified + syncResult.Deleted
                },
                message = createBranch 
                    ? $"Created and switched to branch '{target}' with {syncResult.Added} documents."
                    : $"Switched to '{target}' with {syncResult.Added + syncResult.Modified + syncResult.Deleted} document changes."
            });
        }
        catch (BranchNotFoundException)
        {
            return ToolResult.Error("BRANCH_NOT_FOUND", 
                $"Branch '{target}' not found. Use dolt_branches to see available branches, or use create_branch=true to create it.");
        }
        catch (CommitNotFoundException)
        {
            return ToolResult.Error("COMMIT_NOT_FOUND", 
                $"Commit '{target}' not found.");
        }
    }

    /// <summary>
    /// Reset to a specific commit, discarding local changes.
    /// </summary>
    [McpTool("dolt_reset")]
    [McpDescription("Reset the current branch to a specific commit, updating ChromaDB to match. WARNING: This discards uncommitted local changes.")]
    public async Task<ToolResult> ResetAsync(
        [McpParameter("target", "Commit to reset to: 'HEAD', 'origin/main', or commit hash. Default: 'HEAD'", required: false)]
        string target = "HEAD",
        [McpParameter("confirm_discard", "Must be true to confirm discarding changes. Safety check.", required: false)]
        bool confirmDiscard = false)
    {
        try
        {
            var status = await _syncManager.GetStatusAsync();
            
            // Require confirmation if there are changes
            if (status.LocalChanges.HasChanges && !confirmDiscard)
            {
                return ToolResult.Error("CONFIRMATION_REQUIRED",
                    $"This will discard {status.LocalChanges.TotalChanges} uncommitted changes. Set confirm_discard=true to proceed.",
                    new
                    {
                        changes_to_discard = new
                        {
                            added = status.LocalChanges.NewDocuments.Count,
                            modified = status.LocalChanges.ModifiedDocuments.Count,
                            deleted = status.LocalChanges.DeletedDocuments.Count
                        }
                    });
            }

            var fromCommit = await _dolt.GetHeadCommitHashAsync();
            var resolvedTarget = await _dolt.ResolveCommitRefAsync(target);
            
            var result = await _syncManager.ProcessResetAsync(resolvedTarget);

            return ToolResult.Success(new
            {
                success = result.Status == SyncStatus.Completed,
                reset_result = new
                {
                    from_commit = fromCommit?.Substring(0, 7),
                    to_commit = resolvedTarget?.Substring(0, 7),
                    discarded_changes = new
                    {
                        added = status.LocalChanges.NewDocuments.Count,
                        modified = status.LocalChanges.ModifiedDocuments.Count,
                        deleted = status.LocalChanges.DeletedDocuments.Count,
                        total = status.LocalChanges.TotalChanges
                    }
                },
                sync_summary = new
                {
                    documents_restored = result.Added,
                    documents_removed = result.Deleted
                },
                message = $"Reset to {resolvedTarget?.Substring(0, 7)}. ChromaDB updated with {result.Added} documents."
            });
        }
        catch (CommitNotFoundException)
        {
            return ToolResult.Error("COMMIT_NOT_FOUND", 
                $"Target '{target}' not found. Use dolt_commits or dolt_find to locate valid commits.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ADVANCED TOOLS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Link Dolt commit to a Git repository's commit.
    /// </summary>
    [McpTool("dolt_link_git")]
    [McpDescription("Link the current Dolt commit to a Git repository's commit, allowing tracking of which knowledge base version corresponds to which code version.")]
    public async Task<ToolResult> LinkGitAsync(
        [McpParameter("git_repo_path", "Path to Git repository. Default: current directory", required: false)]
        string gitRepoPath = ".",
        [McpParameter("git_commit", "Git commit to link to. Default: current HEAD", required: false)]
        string gitCommit = null,
        [McpParameter("bidirectional", "Also store Dolt commit in a file in the Git repo. Default: false", required: false)]
        bool bidirectional = false,
        [McpParameter("link_file", "Path for bidirectional link file. Default: '.dolt-version'", required: false)]
        string linkFile = ".dolt-version")
    {
        try
        {
            // Get current Dolt state
            var doltCommit = await _dolt.GetHeadCommitHashAsync();
            var doltBranch = await _dolt.GetCurrentBranchAsync();
            
            // Get Git state
            var gitInfo = await _gitIntegration.GetGitInfoAsync(gitRepoPath);
            var resolvedGitCommit = gitCommit ?? gitInfo.HeadCommit;
            
            // Store link in Dolt
            await _dolt.ExecuteAsync($@"
                INSERT INTO git_links (dolt_commit, dolt_branch, git_commit, git_branch, git_repo_url, linked_at)
                VALUES ('{doltCommit}', '{doltBranch}', '{resolvedGitCommit}', '{gitInfo.Branch}', '{gitInfo.RemoteUrl}', NOW())
            ");
            
            // Bidirectional link file
            bool fileCreated = false;
            if (bidirectional)
            {
                var linkContent = new
                {
                    dolt_remote = await _dolt.GetRemoteUrlAsync("origin"),
                    dolt_branch = doltBranch,
                    dolt_commit = doltCommit,
                    linked_at = DateTime.UtcNow.ToString("o")
                };
                
                var linkPath = Path.Combine(gitRepoPath, linkFile);
                await File.WriteAllTextAsync(linkPath, JsonSerializer.Serialize(linkContent, new JsonSerializerOptions { WriteIndented = true }));
                fileCreated = true;
            }

            return ToolResult.Success(new
            {
                success = true,
                link = new
                {
                    dolt_commit = doltCommit?.Substring(0, 7),
                    dolt_branch = doltBranch,
                    git_commit = resolvedGitCommit?.Substring(0, 7),
                    git_branch = gitInfo.Branch,
                    git_repo = gitRepoPath
                },
                bidirectional = new
                {
                    enabled = bidirectional,
                    link_file = linkFile,
                    file_created = fileCreated
                },
                message = $"Linked Dolt commit {doltCommit?.Substring(0, 7)} to Git commit {resolvedGitCommit?.Substring(0, 7)}."
            });
        }
        catch (GitRepositoryNotFoundException)
        {
            return ToolResult.Error("GIT_REPO_NOT_FOUND", 
                $"No Git repository found at '{gitRepoPath}'.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to link Git");
            return ToolResult.Error("LINK_FAILED", ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════════

    private string GetCollectionName(string branch)
    {
        return $"vmrag_{branch.Replace("/", "_").Replace("-", "_")}";
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// SUPPORTING TYPES
// ═══════════════════════════════════════════════════════════════════════════

public class ToolResult
{
    public bool Success { get; set; }
    public object Content { get; set; }
    public string Error { get; set; }
    public string Message { get; set; }
    public object Details { get; set; }
    public string[] Suggestions { get; set; }

    public static ToolResult Success(object content) => new() { Success = true, Content = content };
    
    public static ToolResult Error(string error, string message, object details = null, string[] suggestions = null) => 
        new() { Success = false, Error = error, Message = message, Details = details, Suggestions = suggestions };
}

public enum CommitSearchMode { All, HashOnly, MessageOnly }
public enum ChangeType { Added, Modified, Deleted }

public class RepositoryNotInitializedException : Exception { }
public class BranchNotFoundException : Exception { public string BranchName { get; set; } }
public class CommitNotFoundException : Exception { }
public class RemoteNotFoundException : Exception { }
public class RemoteUnreachableException : Exception { }
public class AuthenticationException : Exception { }
public class MergeConflictException : Exception { public List<object> Conflicts { get; set; } }
public class GitRepositoryNotFoundException : Exception { }
```

#### 2.4 Tool Registration

```csharp
// File: Program.cs or Startup.cs
services.AddMcpTools<DoltVersionControlTools>();

// The MCP server should expose these tools with their schemas
// Tool schemas are derived from the [McpTool] and [McpParameter] attributes
```

### Phase 3: Testing (Week 3)

Implement acceptance tests as defined in Section 7.

### Phase 4: DoltHub Integration (Week 4)

1. Create DoltHub account and repository
2. Configure remote in your local Dolt repo
3. Test push/pull workflows
4. Document team onboarding process

---

## 7. Acceptance Tests (Gherkin BDD)

### T0: Bidirectional Sync - Chroma to Dolt

```gherkin
Feature: Bidirectional Sync - ChromaDB to Dolt
  As a user
  I want my local ChromaDB changes to be saved to version control
  So that I can commit, share, and collaborate on my knowledge base

  Background:
    Given VM RAG MCP server is running
    And ChromaDB collection "vmrag_main" exists
    And Dolt repository is initialized

  @T0 @chroma-to-dolt @new-project
  Scenario: Initialize version control for existing ChromaDB collection
    # User has been using ChromaDB without version control
    Given ChromaDB collection "my-knowledge-base" contains:
      | source_id | title              | content                         |
      | doc-001   | Python Basics      | Python is a programming lang... |
      | doc-002   | REST API Design    | RESTful APIs use HTTP methods...|
      | doc-003   | Database Indexing  | Indexes improve query perf...   |
    And no Dolt repository exists
    
    When I call "dolt_init" with collection "my-knowledge-base"
    Then the response should show:
      | field             | value     |
      | success           | true      |
      | documentsImported | 3         |
    And Dolt should have tables: issue_logs, knowledge_docs, chroma_sync_state
    And Dolt issue_logs or knowledge_docs should contain 3 records
    And a commit should exist with message "Initial import from ChromaDB"
    And all documents should have is_local_change = false

  @T0 @chroma-to-dolt @local-changes
  Scenario: Detect and commit local changes from ChromaDB
    Given Dolt and ChromaDB are synced at commit "abc123"
    And ChromaDB has 5 documents
    
    # User adds documents via MCP (writes to ChromaDB)
    When I add a new document to ChromaDB:
      | source_id | title           | content                    |
      | doc-new   | New Feature Doc | This document was added... |
    And I modify document "doc-001" in ChromaDB:
      | field   | new_value                     |
      | content | Updated content with fixes... |
    And I delete document "doc-003" from ChromaDB
    
    # Check status shows local changes
    When I call "dolt_status"
    Then the response should show:
      | field                       | value |
      | localChanges.hasChanges     | true  |
      | localChanges.newDocuments   | 1     |
      | localChanges.modifiedDocuments | 1  |
      | localChanges.deletedDocuments | 1   |
      | localChanges.totalChanges   | 3     |
    
    # Commit stages ChromaDB changes to Dolt
    When I call "dolt_commit" with message "Added new feature, fixed doc-001, removed doc-003"
    Then the response should show:
      | field            | value |
      | success          | true  |
      | stagedFromChroma | 3     |
    And Dolt should contain document "doc-new"
    And Dolt document "doc-001" should have updated content
    And Dolt should NOT contain document "doc-003"
    And all ChromaDB documents should have is_local_change = false

  @T0 @chroma-to-dolt @pull-with-local-changes
  Scenario: Pull blocked when local changes exist
    Given ChromaDB has uncommitted local changes:
      | change_type | source_id |
      | new         | local-doc |
      | modified    | doc-002   |
    
    When I call "dolt_pull"
    Then the response should show:
      | field   | value                |
      | success | false                |
      | status  | local_changes_exist  |
    And the response should include hint "Commit your changes first"
    
    # Force pull discards local changes
    When I call "dolt_pull" with force=true
    Then the response should show success = true
    And ChromaDB should match Dolt state
    And local-doc should NOT exist in ChromaDB
    And doc-002 should have original content from Dolt

  @T0 @chroma-to-dolt @checkout-with-local-changes
  Scenario: Checkout blocked when local changes exist
    Given I am on branch "main" with uncommitted local changes
    And branch "feature/new-stuff" exists
    
    When I call "dolt_checkout" with branch "feature/new-stuff"
    Then the response should show:
      | field   | value                |
      | success | false                |
      | status  | local_changes_exist  |
    And I should remain on branch "main"
    
    # Creating new branch preserves local changes
    When I call "dolt_checkout" with branch "feature/with-changes" and create=true
    Then the response should show success = true
    And I should be on branch "feature/with-changes"
    And local changes should be preserved in the new branch collection

  @T0 @chroma-to-dolt @chunk-reassembly
  Scenario: Chunked documents reassemble correctly for Dolt storage
    Given a document with 2000 characters of content
    And chunk_size is 512 with overlap 50
    
    When I add the document to ChromaDB via MCP
    Then ChromaDB should have 5 chunks for the document
    
    When I call "dolt_commit" with message "Add long document"
    Then Dolt should have 1 record with full 2000 character content
    And the content_hash in Dolt should match SHA256 of reassembled content
    And document_sync_log should have chunk_ids for all 5 chunks

  @T0 @chroma-to-dolt @offline-work
  Scenario: Batch commit after offline work
    Given Dolt is synced at commit "start-commit"
    
    # Simulate offline work - multiple changes over time
    When I add document "offline-1" to ChromaDB
    And I add document "offline-2" to ChromaDB  
    And I modify document "existing-1" in ChromaDB
    And I add document "offline-3" to ChromaDB
    And I delete document "old-doc" from ChromaDB
    
    # All changes committed in single batch
    When I call "dolt_commit" with message "Batch: offline work from Dec 12-13"
    Then the response should show:
      | field            | value |
      | success          | true  |
      | stagedFromChroma | 5     |
    And Dolt should have a single new commit
    And all 5 changes should be in that commit
```

### T0.5: Workflow Integration Tests

```gherkin
Feature: Complete Workflow Integration
  End-to-end workflows combining Chroma and Dolt operations

  @T0.5 @workflow @new-user
  Scenario: New user creates and shares knowledge base
    # Start with nothing
    Given no Dolt repository exists
    And ChromaDB is empty
    
    # Create knowledge base via MCP
    When I add documents via MCP tools:
      | title                  | content                          |
      | Setup Guide            | How to configure the system...   |
      | Troubleshooting        | Common issues and solutions...   |
      | API Reference          | Endpoint documentation...        |
    Then ChromaDB should have 3 documents
    And all documents should have is_local_change = true
    
    # Enable version control
    When I call "dolt_init" with collection "vmrag_main"
    Then version control should be initialized
    And documents should be committed
    And is_local_change should be false for all documents
    
    # Share with team
    When I configure remote "origin" as "myorg/knowledge-base"
    And I call "dolt_push"
    Then push should succeed
    And team members can clone the repository

  @T0.5 @workflow @collaboration
  Scenario: Two developers collaborate with bidirectional sync
    Given Developer A and Developer B both have cloned "myorg/shared-kb"
    And both are synced to commit "initial"
    
    # Developer A adds documents locally
    When Developer A adds document "feature-a-doc" via MCP
    And Developer A calls "dolt_status"
    Then Developer A should see 1 local change
    
    # Developer A commits and pushes
    When Developer A calls "dolt_commit" with message "Add feature A docs"
    And Developer A calls "dolt_push"
    Then push should succeed
    
    # Developer B has also made local changes
    When Developer B adds document "feature-b-doc" via MCP
    And Developer B calls "dolt_pull"
    Then Developer B should see local_changes_exist error
    
    # Developer B commits their changes first
    When Developer B calls "dolt_commit" with message "Add feature B docs"
    And Developer B calls "dolt_pull"
    Then pull should succeed
    And Developer B should have both feature-a-doc and feature-b-doc
    
    # Developer B pushes merged state
    When Developer B calls "dolt_push"
    Then push should succeed
    
    # Developer A pulls to get Developer B's changes
    When Developer A calls "dolt_pull"
    Then Developer A should have both documents

  @T0.5 @workflow @branch-workflow
  Scenario: Feature branch workflow with local changes
    Given I am on branch "main" with 10 documents
    
    # Start feature work
    When I call "dolt_checkout" with branch "feature/new-api" and create=true
    Then I should be on branch "feature/new-api"
    And ChromaDB collection "vmrag_feature-new-api" should have 10 documents
    
    # Make changes on feature branch
    When I add document "api-v2-doc" via MCP
    And I modify document "api-doc" via MCP
    And I call "dolt_commit" with message "WIP: New API design"
    Then commit should succeed on feature branch
    
    # Switch back to main (no local changes now)
    When I call "dolt_checkout" with branch "main"
    Then I should be on branch "main"
    And ChromaDB should switch to "vmrag_main" collection
    And "api-v2-doc" should NOT be in main collection
    
    # Merge feature branch
    When I call "dolt_merge" with source_branch "feature/new-api"
    Then merge should succeed
    And main should now have "api-v2-doc"
    And main should have updated "api-doc"
```

### T1: Copy RAG Data Across DoltHub Test

```gherkin
Feature: Copy RAG Data Across DoltHub
  As a developer
  I want to copy RAG data from one project to another via DoltHub
  So that I can share knowledge bases between team members

  Background:
    Given DoltHub remote "testorg/vmrag-test" exists
    And the remote database has the standard VM RAG schema

  @T1 @copy @dolthub
  Scenario: Full database copy via DoltHub
    # Step 1: Create and populate source ChromaDB
    Given a new VM RAG MCP server instance "source-project"
    And the Dolt database is initialized at "./source-db"
    And the ChromaDB is initialized at "./source-chroma"
    
    # Step 2: Fill with test data
    When I add the following issue logs via MCP:
      | project_id | issue_number | title              | content                                    | log_type       |
      | proj-001   | 101          | Auth Bug Fix       | Fixed JWT validation timeout issue...      | implementation |
      | proj-001   | 102          | Performance Tuning | Optimized database queries by adding...    | resolution     |
      | proj-001   | 103          | API Refactor       | Restructured the REST endpoints to...      | investigation  |
    And I add the following knowledge docs via MCP:
      | category | tool_name     | title                 | content                              |
      | api      | EntityFramework| EF Core Migrations   | Database migrations allow you to...   |
      | tooling  | Docker        | Container Best Practices | When containerizing .NET apps...   |
    And I call "dolt_commit" with message "Initial test data"
    Then the commit should succeed
    And ChromaDB should contain 5 documents total
    
    # Step 3: Push to DoltHub
    When I configure remote "origin" as "testorg/vmrag-test"
    And I call "dolt_push" with remote "origin"
    Then the push should succeed
    And DoltHub should contain 3 issue_logs records
    And DoltHub should contain 2 knowledge_docs records

    # Step 4: Pull into empty project
    Given a new VM RAG MCP server instance "target-project"
    And the Dolt database is empty at "./target-db"
    And the ChromaDB is empty at "./target-chroma"
    When I run "dolt clone testorg/vmrag-test ./target-db"
    And I initialize VM RAG MCP server for "./target-db"
    And I call "dolt_checkout" with branch "main"
    Then ChromaDB sync should complete
    
    # Step 5: Verify data integrity
    Then the target Dolt database should contain 3 issue_logs records
    And the target Dolt database should contain 2 knowledge_docs records
    And the target ChromaDB should have 5 documents

    # Step 6: Validate query equivalence
    When I search "JWT authentication timeout" in source-project
    And I search "JWT authentication timeout" in target-project
    Then the search results should return the same document IDs
    And the relevance scores should differ by less than 0.01

  @T1 @copy @content-hash
  Scenario: Verify content hash integrity after copy
    Given source-project has document with:
      | field        | value                          |
      | log_id       | log-test-001                   |
      | content      | This is test content for hash  |
      | content_hash | <calculated SHA-256>           |
    When the document is copied to target-project via DoltHub
    Then target-project should have document "log-test-001"
    And the content_hash should match exactly
    And the content should be byte-for-byte identical
```

### T2: Fast-Forward RAG Data Across DoltHub Test

```gherkin
Feature: Fast-Forward RAG Data Sync
  As a developer
  I want incremental updates to sync efficiently via DoltHub
  So that only changed documents are re-embedded

  Background:
    Given DoltHub remote "testorg/vmrag-test" exists with initial data
    And source-project has VM RAG MCP server connected to the remote
    And target-project has VM RAG MCP server connected to same remote
    And both projects are synced to the same commit "initial-commit"

  @T2 @fastforward @change-detection
  Scenario: Detect and sync incremental changes
    # Step 1: Update data in source project
    Given source-project ChromaDB has 5 documents in collection "vmrag_main"
    When I update issue log "log-001" in source-project:
      | field   | new_value                        |
      | content | Updated investigation notes...   |
    And I add new issue log in source-project:
      | log_id   | project_id | issue_number | content                    |
      | log-006  | proj-001   | 106          | New feature implementation |
    And I delete issue log "log-003" in source-project
    
    # Step 2: Verify change detection
    When I call "dolt_status" in source-project
    Then the response should show pending changes:
      | change_type | count |
      | new         | 1     |
      | modified    | 1     |
      | deleted     | 1     |
    And sync_status should be "pending"

    # Step 3: Commit and push changes
    When I call "dolt_commit" with message "Sprint 5 updates"
    Then the commit should succeed
    And sync result should show:
      | metric   | value |
      | added    | 1     |
      | modified | 1     |
      | deleted  | 1     |
    When I call "dolt_push"
    Then the push should succeed

    # Step 4: Pull in secondary project
    Given target-project last sync commit is "initial-commit"
    When I call "dolt_pull" in target-project
    Then the pull should succeed
    And the response should indicate "wasFastForward": true
    And sync result should show:
      | metric   | value |
      | added    | 1     |
      | modified | 1     |
      | deleted  | 1     |
    
    # Step 5: Validate new data
    Then target-project should have 5 documents (5 - 1 + 1)
    When I search "Updated investigation" in target-project
    Then results should include document "log-001"
    When I search content from "log-003" in target-project
    Then results should NOT include document "log-003"
    When I search "New feature implementation" in target-project
    Then results should include document "log-006"

  @T2 @fastforward @no-changes
  Scenario: No sync when already up-to-date
    Given source-project and target-project are on same commit
    When I call "dolt_pull" in target-project
    Then the response should show:
      | field  | value      |
      | status | NoChanges  |
      | added  | 0          |
    And no ChromaDB operations should be performed
```

### T3: Merge RAG Data Across DoltHub Test

```gherkin
Feature: Merge RAG Data Between Branches
  As a team lead
  I want to merge branch changes into main
  So that feature work is integrated into the shared knowledge base

  Background:
    Given DoltHub remote "testorg/vmrag-test" exists
    And project-A has VM RAG MCP server on branch "main"
    And project-B has VM RAG MCP server on branch "main"
    And both are synced to initial commit

  @T3 @merge @parallel-changes
  Scenario: Merge parallel branch changes without conflicts
    # Step 1A: Project A creates branch and adds document A
    When I call "dolt_checkout" in project-A with:
      | branch | create |
      | feature/auth | true |
    And I add issue log in project-A:
      | log_id   | project_id | issue_number | title           | content                        |
      | log-A01  | proj-001   | 201          | Auth Enhancement| Implemented OAuth2 flow for... |
    And I call "dolt_commit" in project-A with message "Added OAuth2 notes"
    And I call "dolt_push" in project-A

    # Step 1B: Project B creates branch and adds document B  
    When I call "dolt_checkout" in project-B with:
      | branch | create |
      | feature/db-opt | true |
    And I add issue log in project-B:
      | log_id   | project_id | issue_number | title           | content                        |
      | log-B01  | proj-001   | 202          | DB Optimization | Added indexes for query perf...|
    And I call "dolt_commit" in project-B with message "Added DB optimization notes"
    And I call "dolt_push" in project-B

    # Step 2: Merge branch B into branch A (in project A)
    When I run "dolt fetch origin" in project-A
    And I run "dolt checkout feature/auth" in project-A
    And I call "dolt_merge" in project-A with source_branch "origin/feature/db-opt"
    Then the merge should succeed without conflicts
    And the response should show:
      | field      | value |
      | success    | true  |
      | hasConflicts | false |
    
    # Step 3: Validate merged content
    Then project-A Dolt should have both documents:
      | log_id   | exists |
      | log-A01  | true   |
      | log-B01  | true   |
    And project-A ChromaDB should have both documents embedded
    
    When I search "OAuth2 authentication" in project-A
    Then results should include document "log-A01"
    When I search "database index optimization" in project-A
    Then results should include document "log-B01"

  @T3 @merge @conflict-resolution
  Scenario: Merge with conflict resolution
    # Setup: Both projects modify the same document
    Given document "shared-doc-001" exists on branch "main"
    
    When project-A creates branch "feature/update-a" from main
    And project-A updates "shared-doc-001" content to "Version A: Auth updated..."
    And project-A commits with message "Update A"
    
    And project-B creates branch "feature/update-b" from main
    And project-B updates "shared-doc-001" content to "Version B: Auth refactored..."
    And project-B commits with message "Update B"
    
    # Attempt merge
    When project-A checks out "feature/update-a"
    And project-A fetches "feature/update-b" from origin
    And I call "dolt_merge" in project-A with source_branch "origin/feature/update-b"
    
    Then the response should show:
      | field        | value |
      | success      | false |
      | hasConflicts | true  |
    And conflicts should list:
      | table      | row_id          |
      | issue_logs | shared-doc-001  |
    
    # Resolve conflict
    When I run "dolt conflicts resolve --ours issue_logs" in project-A
    And I call "dolt_commit" with message "Resolved: kept our version"
    
    Then the document "shared-doc-001" should contain "Version A: Auth updated"
    And ChromaDB should have updated embedding for "shared-doc-001"

  @T3 @merge @query-validation
  Scenario: Query merged content from multiple sources
    Given project-A has merged both feature branches
    And project-A ChromaDB is synced
    
    When I search "How did we handle authentication?" in project-A
    Then results should be ranked by semantic similarity
    And results should include contributions from both:
      | source_branch     | document |
      | feature/auth      | log-A01  |
      | feature/db-opt    | log-B01  |
```

---

## 8. Additional Test Scenarios

### Scenario: Branch Isolation

```gherkin
Feature: Branch Isolation in ChromaDB
  Branches should have isolated ChromaDB collections

  @isolation @branch
  Scenario: Changes on feature branch don't affect main
    Given I am on branch "main" with 5 documents
    And ChromaDB collection "vmrag_main" has 5 documents
    
    When I call "dolt_checkout" with branch "feature/test" and create true
    Then ChromaDB should have new collection "vmrag_feature-test"
    And collection "vmrag_feature-test" should have 5 documents (copied from main)
    
    When I add 3 new documents on branch "feature/test"
    And I call "dolt_commit" with message "Feature changes"
    Then collection "vmrag_feature-test" should have 8 documents
    And collection "vmrag_main" should still have 5 documents
    
    When I call "dolt_checkout" with branch "main"
    And I search for content from new documents
    Then results should NOT include the new documents

  @isolation @checkout-switch
  Scenario: Checkout switches active collection
    Given branch "main" collection has document with content "Main branch content"
    And branch "feature" collection has document with content "Feature branch content"
    
    When I am on branch "main"
    And I search "branch content"
    Then top result should contain "Main branch content"
    
    When I call "dolt_checkout" with branch "feature"
    And I search "branch content"  
    Then top result should contain "Feature branch content"
```

### Scenario: Reset and Recovery

```gherkin
Feature: Reset and Recovery
  Support for reverting to previous states

  @reset @hard
  Scenario: Hard reset regenerates ChromaDB
    Given commit history: A -> B -> C (HEAD)
    And commit A had 3 documents
    And commit C has 7 documents
    And ChromaDB has 7 documents
    
    When I call "dolt_reset" with commit "A"
    Then Dolt HEAD should be at commit A
    And ChromaDB should be regenerated
    And ChromaDB should have 3 documents
    And documents from commits B and C should NOT exist in ChromaDB

  @reset @recovery  
  Scenario: Recover from failed sync
    Given a sync operation failed midway
    And sync_operations shows status "failed"
    And ChromaDB is out of sync with Dolt
    
    When I call "dolt_checkout" with branch "main"
    Then system should detect sync mismatch
    And system should perform full resync
    And ChromaDB should match Dolt state
    And sync_status should become "synced"
```

### Scenario: Embedding Model Consistency

```gherkin
Feature: Embedding Model Consistency
  Ensure embedding model changes are handled properly

  @embedding @model-mismatch
  Scenario: Detect embedding model mismatch
    Given ChromaDB collection was created with model "text-embedding-ada-002"
    And chroma_sync_state shows embedding_model "text-embedding-ada-002"
    When system is configured to use model "text-embedding-3-small"
    And I attempt to sync new documents
    Then a warning should be logged about model mismatch
    And new documents should NOT be embedded
    And user should be notified to:
      | option     | description                    |
      | regenerate | Full re-embed with new model   |
      | revert     | Use original model             |

  @embedding @regeneration
  Scenario: Full regeneration on model change
    Given I confirm regeneration with new model
    When regeneration starts
    Then all existing chunks should be deleted
    And all documents should be re-chunked  
    And all chunks should be re-embedded with "text-embedding-3-small"
    And chroma_sync_state should show new embedding_model
```

### Scenario: Cross-Clone Consistency

```gherkin
Feature: Cross-Clone Data Consistency
  Multiple clones should have identical query results when synced to same commit

  Background:
    Given DoltHub remote "testorg/vmrag-shared" exists
    And the remote has 5 issue_logs and 3 knowledge_docs
    And all documents were committed at commit "abc123"

  @consistency @clone-sync
  Scenario: Two fresh clones produce identical ChromaDB state
    # Clone A setup
    Given developer A clones the repository to "./clone-a"
    And developer A initializes VM RAG MCP server
    When developer A's server syncs ChromaDB
    Then clone-a ChromaDB should have collection "vmrag_main"
    And clone-a should have 8 documents synced
    
    # Clone B setup (completely independent)
    Given developer B clones the repository to "./clone-b"
    And developer B initializes VM RAG MCP server
    When developer B's server syncs ChromaDB
    Then clone-b ChromaDB should have collection "vmrag_main"
    And clone-b should have 8 documents synced
    
    # Validation
    Then clone-a and clone-b should have identical:
      | property                  | match_type        |
      | document_sync_log entries | exact             |
      | ChromaDB chunk IDs        | exact             |
      | ChromaDB chunk content    | exact             |
      | chroma_sync_state         | exact             |
    
    When developer A searches "authentication bug fix"
    And developer B searches "authentication bug fix"
    Then both should return the same documents in same order
    And distance scores should differ by less than 0.001

  @consistency @content-hash-validation
  Scenario: Content hash validates document integrity
    Given clone-a has document "log-001" with content_hash "sha256_abc..."
    When clone-b syncs from DoltHub
    Then clone-b document "log-001" should have content_hash "sha256_abc..."
    And clone-b ChromaDB metadata for "log-001" chunks should have:
      | field        | value           |
      | content_hash | sha256_abc...   |
      | source_id    | log-001         |

  @consistency @chunk-determinism
  Scenario: Chunking produces identical results across clones
    Given document "log-002" has content of 1500 characters
    And chunk_size is 512 with overlap 50
    
    When clone-a chunks and syncs the document
    Then clone-a should create chunks:
      | chunk_id         | char_range  |
      | log-002_chunk_0  | 0-512       |
      | log-002_chunk_1  | 462-974     |
      | log-002_chunk_2  | 924-1436    |
      | log-002_chunk_3  | 1386-1500   |
    
    When clone-b chunks and syncs the same document
    Then clone-b should create identical chunk IDs and ranges
    And chunk text content should be byte-for-byte identical

  @consistency @sync-state-tracking
  Scenario: Sync state enables consistency verification
    Given clone-a synced at commit "abc123" 
    And clone-a chroma_sync_state shows:
      | field            | value          |
      | last_sync_commit | abc123         |
      | document_count   | 8              |
      | chunk_count      | 24             |
      | embedding_model  | text-embedding-3-small |
    
    When clone-b syncs from the same commit
    Then clone-b chroma_sync_state should show identical values
    And validation check should pass with no issues

  @consistency @model-mismatch-prevention
  Scenario: Prevent mixing embeddings from different models
    Given clone-a used embedding model "text-embedding-ada-002"
    And clone-a chroma_sync_state shows embedding_model "text-embedding-ada-002"
    
    When clone-b is configured with model "text-embedding-3-small"
    And clone-b attempts to sync
    Then clone-b should detect model mismatch
    And clone-b should NOT add new embeddings
    And clone-b should prompt for resolution:
      | option              | result                          |
      | full_regeneration   | Delete all, re-embed with new   |
      | use_existing_model  | Switch config to ada-002        |
      | abort               | Cancel sync operation           |

  @consistency @after-pull
  Scenario: Consistency maintained after pull with changes
    Given clone-a and clone-b are both synced to commit "abc123"
    
    When clone-a adds a new document and commits "def456"
    And clone-a pushes to DoltHub
    
    When clone-b pulls from DoltHub
    And clone-b syncs ChromaDB
    Then clone-b should be at commit "def456"
    And clone-b should have the new document
    And clone-b query results should match clone-a for:
      | query                    | expected_top_result |
      | "authentication bug"     | same document ID    |
      | "new document content"   | the new document    |
```

### Scenario: Error Handling

```gherkin
Feature: Error Handling and Audit Logging
  Comprehensive error handling and operation logging

  @error @network
  Scenario: DoltHub network failure during push
    Given I have committed local changes
    When I call "dolt_push"
    And the network connection fails
    Then the response should show:
      | field   | value                   |
      | success | false                   |
      | message | Network connection lost |
    And local Dolt state should remain unchanged
    And ChromaDB should remain unchanged
    And sync_operations should log:
      | field          | value  |
      | operation_type | push   |
      | status         | failed |

  @audit @complete-trail
  Scenario: All operations create audit trail
    When I perform operations:
      | operation | parameters                  |
      | commit    | message: "Test"             |
      | push      | remote: origin              |
      | pull      | remote: origin              |
    Then sync_operations should have 3 entries
    And each entry should have all required fields:
      | field               | required |
      | operation_id        | yes      |
      | operation_type      | yes      |
      | dolt_branch         | yes      |
      | dolt_commit_before  | yes      |
      | dolt_commit_after   | yes      |
      | started_at          | yes      |
      | completed_at        | yes      |
      | operation_status    | yes      |
```

### Scenario: Large Dataset Performance

```gherkin
Feature: Large Dataset Performance
  Handle larger datasets efficiently

  @performance @batch-sync
  Scenario: Batch processing for large changesets
    Given I have 500 new documents to sync
    When I call "dolt_commit" with sync enabled
    Then documents should be processed in batches
    And each batch should be logged
    And total sync time should be under 5 minutes
    And memory usage should remain stable

  @performance @incremental
  Scenario: Incremental sync is efficient
    Given ChromaDB has 10,000 existing documents
    And I add 5 new documents to Dolt
    When I call "dolt_commit"
    Then only 5 documents should be embedded
    And DOLT_DIFF should be used (not full table scan)
    And sync should complete in under 10 seconds
```

---

## Appendix A: Configuration Schema

```json
{
  "Dolt": {
    "ExecutablePath": "dolt",
    "RepositoryPath": "./data/dolt-repo",
    "Remote": {
      "Name": "origin",
      "Url": "https://doltremoteapi.dolthub.com/yourorg/yourrepo"
    }
  },
  "ChromaDb": {
    "PersistDirectory": "./data/chroma-db",
    "EmbeddingModel": "text-embedding-3-small"
  },
  "Sync": {
    "AutoSyncOnCommit": true,
    "BatchSize": 50,
    "ChunkSize": 512,
    "ChunkOverlap": 50
  }
}
```

---

## Appendix B: Quick Reference - Dolt CLI Commands

```bash
# ==================== Repository Setup ====================
dolt init                              # Initialize new repo
dolt clone <remote-url>                # Clone from DoltHub
dolt remote add origin <url>           # Add remote

# ==================== Branch Operations ====================
dolt branch                            # List branches
dolt branch <name>                     # Create branch
dolt branch -d <name>                  # Delete branch
dolt checkout <branch>                 # Switch branch
dolt checkout -b <branch>              # Create and switch

# ==================== Commit Operations ====================
dolt add -A                            # Stage all changes
dolt commit -m "<message>"             # Commit staged changes
dolt log --oneline -n 10               # Show history

# ==================== Remote Operations ====================
dolt push origin <branch>              # Push to remote
dolt pull origin <branch>              # Pull from remote
dolt fetch origin                      # Fetch without merge

# ==================== Merge Operations ====================
dolt merge <branch>                    # Merge branch
dolt conflicts cat <table>             # Show conflicts
dolt conflicts resolve --ours <table>  # Resolve with ours
dolt conflicts resolve --theirs <table># Resolve with theirs

# ==================== Diff and Status ====================
dolt status                            # Show working status
dolt diff                              # Show uncommitted changes
dolt diff <from> <to>                  # Diff between commits

# ==================== Reset ====================
dolt reset --hard <commit>             # Hard reset
dolt reset --soft HEAD~1               # Soft reset

# ==================== SQL Queries via CLI ====================
dolt sql -q "SELECT * FROM table" -r json          # Query as JSON
dolt sql -q "SELECT active_branch()"               # Get current branch
dolt sql -q "SELECT DOLT_HASHOF('HEAD')"          # Get HEAD hash
dolt sql -q "SELECT * FROM DOLT_DIFF(...)" -r json # Get structured diff
dolt sql -q "INSERT INTO table (...) VALUES (...)" # Insert data
dolt sql -q "UPDATE table SET ... WHERE ..."       # Update data
dolt sql -q "DELETE FROM table WHERE ..."          # Delete data
```

---

## Appendix C: JSON Output Format

When using `-r json` flag, Dolt returns results in this format:

```json
{
  "rows": [
    {
      "column1": "value1",
      "column2": "value2"
    },
    {
      "column1": "value3",
      "column2": "value4"
    }
  ]
}
```

Example parsing in C#:

```csharp
var json = await ExecuteSqlJsonAsync("SELECT log_id, content FROM issue_logs LIMIT 2");
// json = {"rows":[{"log_id":"abc","content":"..."},{"log_id":"def","content":"..."}]}

var result = JsonSerializer.Deserialize<JsonElement>(json);
foreach (var row in result.GetProperty("rows").EnumerateArray())
{
    var logId = row.GetProperty("log_id").GetString();
    var content = row.GetProperty("content").GetString();
}
```

---

## Appendix D: Schema Design Rationale

This appendix documents the design decision to use a **generalized schema** instead of domain-specific tables, including the alternatives considered and trade-offs involved.

### D.1 The Problem: ChromaDB is Schema-less

ChromaDB stores documents with arbitrary metadata. Users can store any fields they want:

```python
# Recipe database
collection.add(
    ids=["recipe-001"],
    documents=["Boil pasta for 10 minutes..."],
    metadatas=[{"recipe_name": "Pasta", "cuisine": "Italian", "prep_time": 30, "ingredients": ["pasta", "eggs"]}]
)

# Customer support tickets
collection.add(
    ids=["ticket-123"],
    documents=["Customer reports login issues..."],
    metadatas=[{"ticket_id": "T-123", "customer": "Acme Corp", "priority": "high", "status": "open"}]
)

# Research papers
collection.add(
    ids=["paper-2024-001"],
    documents=["Abstract: This paper explores..."],
    metadatas=[{"authors": ["Smith", "Jones"], "journal": "Nature", "year": 2024, "doi": "10.1234/..."}]
)

# Personal notes
collection.add(
    ids=["note-001"],
    documents=["Remember to call the dentist..."],
    metadatas=[{"tags": ["todo", "personal"], "mood": "stressed", "location": "home"}]
)
```

A version control system for ChromaDB must handle **any** of these use cases without code changes.

### D.2 Alternative Approaches Considered

#### Option A: Domain-Specific Tables (Rejected)

```sql
-- Rigid schema assuming specific use case
CREATE TABLE issue_logs (
    log_id VARCHAR(36) PRIMARY KEY,
    project_id VARCHAR(36) NOT NULL,
    issue_number INT NOT NULL,
    title VARCHAR(500),
    content LONGTEXT NOT NULL,
    content_hash CHAR(64) NOT NULL,
    log_type ENUM('investigation', 'implementation', 'resolution') DEFAULT 'implementation',
    metadata JSON
);

CREATE TABLE knowledge_docs (
    doc_id VARCHAR(36) PRIMARY KEY,
    category VARCHAR(100) NOT NULL,
    tool_name VARCHAR(255) NOT NULL,
    tool_version VARCHAR(50),
    title VARCHAR(500) NOT NULL,
    content LONGTEXT NOT NULL,
    content_hash CHAR(64) NOT NULL,
    metadata JSON
);
```

**Problems:**
- ❌ Only works for the assumed use case (issue tracking + knowledge base)
- ❌ Recipes, tickets, research papers would lose metadata or fail entirely
- ❌ Adding new document types requires schema migration
- ❌ Different users need different table structures

**Benefits:**
- ✅ Fast queries on indexed columns (`project_id`, `issue_number`, etc.)
- ✅ Type safety enforced by database
- ✅ Clear, self-documenting schema

#### Option B: One Table Per Collection (Considered)

```sql
-- Dynamically create tables based on collection name
CREATE TABLE collection_recipes (
    doc_id VARCHAR(64) PRIMARY KEY,
    content LONGTEXT NOT NULL,
    content_hash CHAR(64) NOT NULL,
    metadata JSON NOT NULL
);

CREATE TABLE collection_support_tickets (
    doc_id VARCHAR(64) PRIMARY KEY,
    content LONGTEXT NOT NULL,
    content_hash CHAR(64) NOT NULL,
    metadata JSON NOT NULL
);
```

**Problems:**
- ❌ Requires dynamic DDL (CREATE TABLE) at runtime
- ❌ Complicates sync logic (which table to query?)
- ❌ Dolt branch switching becomes complex (tables may not exist on all branches)
- ❌ Cross-collection queries require UNION across unknown tables

**Benefits:**
- ✅ Collection isolation
- ✅ Can add collection-specific indexes

#### Option C: Generalized Single Table with JSON (Selected)

```sql
CREATE TABLE documents (
    doc_id VARCHAR(64) NOT NULL,
    collection_name VARCHAR(255) NOT NULL,
    content LONGTEXT NOT NULL,
    content_hash CHAR(64) NOT NULL,
    title VARCHAR(500),                    -- Extracted for common queries
    doc_type VARCHAR(100),                 -- Extracted for common queries
    metadata JSON NOT NULL,                -- ALL fields preserved exactly
    created_at DATETIME,
    updated_at DATETIME,
    PRIMARY KEY (doc_id, collection_name)
);
```

**Benefits:**
- ✅ Works with ANY ChromaDB collection without schema changes
- ✅ All metadata preserved exactly (no data loss)
- ✅ Simple sync logic (one table to manage)
- ✅ Cross-collection queries are straightforward
- ✅ Branch switching works seamlessly
- ✅ Common fields (`title`, `doc_type`) extracted for indexed queries

**Trade-offs:**
- ⚠️ JSON queries slightly slower than column queries
- ⚠️ No compile-time type checking on metadata fields
- ⚠️ Slightly more storage for repeated field names in JSON

### D.3 Why Option C Wins

| Criterion | Option A (Rigid) | Option B (Per-Collection) | Option C (Generalized) |
|-----------|------------------|---------------------------|------------------------|
| Arbitrary metadata | ❌ Lost | ✅ Preserved | ✅ Preserved |
| No schema migration | ❌ Required | ✅ Not needed | ✅ Not needed |
| Simple sync logic | ✅ Simple | ❌ Complex | ✅ Simple |
| Branch switching | ✅ Works | ❌ Complicated | ✅ Works |
| Query performance | ✅ Fast | ✅ Fast | ⚠️ JSON functions |
| Type safety | ✅ Enforced | ⚠️ Partial | ⚠️ Runtime |
| Cross-collection queries | ❌ Hard | ❌ Hard | ✅ Easy |

**The generalized schema is the only option that works for a truly reusable MCP server** that can version-control any ChromaDB collection.

### D.4 Hybrid Approach: Extracted Fields

To mitigate the query performance trade-off, we extract commonly-used fields into indexed columns:

```sql
CREATE TABLE documents (
    -- ... other fields ...
    
    -- Extracted from metadata JSON for fast indexed queries
    title VARCHAR(500),
    doc_type VARCHAR(100),
    
    -- Full metadata preserved for completeness
    metadata JSON NOT NULL,
    
    -- Indexes on extracted fields
    INDEX idx_doc_type (collection_name, doc_type),
    FULLTEXT INDEX idx_title (title)
);
```

This gives us:
- **Fast queries** on common fields (`WHERE doc_type = 'recipe'`)
- **Full metadata preservation** for user-defined fields
- **No data loss** regardless of what users store in ChromaDB

### D.5 JSON Query Examples

Dolt (MySQL-compatible) supports rich JSON querying:

```sql
-- Simple equality
SELECT * FROM documents 
WHERE JSON_EXTRACT(metadata, '$.cuisine') = 'Italian';

-- Numeric comparison
SELECT * FROM documents 
WHERE JSON_EXTRACT(metadata, '$.prep_time') < 30;

-- Array contains
SELECT * FROM documents 
WHERE JSON_CONTAINS(metadata, '"Smith"', '$.authors');

-- Nested objects
SELECT * FROM documents 
WHERE JSON_EXTRACT(metadata, '$.address.city') = 'New York';

-- Multiple conditions
SELECT * FROM documents 
WHERE JSON_EXTRACT(metadata, '$.priority') = 'high'
  AND JSON_EXTRACT(metadata, '$.status') = 'open';

-- Combining indexed + JSON fields
SELECT * FROM documents 
WHERE doc_type = 'recipe'                              -- Uses index (fast)
  AND JSON_EXTRACT(metadata, '$.cuisine') = 'Italian'; -- JSON filter
```

### D.6 Performance Considerations

| Query Type | Performance | Recommendation |
|------------|-------------|----------------|
| Filter by `collection_name` | ⚡ Fast (indexed) | Always include in WHERE |
| Filter by `doc_type` | ⚡ Fast (indexed) | Use when available |
| Filter by `title` | ⚡ Fast (fulltext) | Use MATCH...AGAINST |
| Filter by `content_hash` | ⚡ Fast (indexed) | Use for sync detection |
| Filter by JSON field | 🐢 Slower | OK for small-medium datasets |
| JSON + indexed field | ⚡ Fast | Use indexed field to reduce scan |

**For large datasets (>100K documents)**, consider:
1. Adding generated columns for frequently-queried JSON fields
2. Using partial indexes on JSON expressions (if Dolt supports)
3. Extracting more fields to dedicated columns

### D.7 Migration Path

If you started with a domain-specific schema and need to migrate to generalized:

```sql
-- 1. Create new generalized table
CREATE TABLE documents (...);

-- 2. Migrate issue_logs
INSERT INTO documents (doc_id, collection_name, content, content_hash, title, doc_type, metadata)
SELECT 
    log_id,
    'vmrag_issues',
    content,
    content_hash,
    title,
    log_type,
    JSON_OBJECT(
        'project_id', project_id,
        'issue_number', issue_number,
        'log_type', log_type
    )
FROM issue_logs;

-- 3. Migrate knowledge_docs
INSERT INTO documents (doc_id, collection_name, content, content_hash, title, doc_type, metadata)
SELECT 
    doc_id,
    'vmrag_knowledge',
    content,
    content_hash,
    title,
    category,
    JSON_OBJECT(
        'category', category,
        'tool_name', tool_name,
        'tool_version', tool_version
    )
FROM knowledge_docs;

-- 4. Drop old tables (after verification)
DROP TABLE issue_logs;
DROP TABLE knowledge_docs;
```

### D.8 Conclusion

The generalized schema with JSON metadata is the correct choice for a reusable ChromaDB version control system because:

1. **It works with any data** - Recipes, tickets, research papers, personal notes
2. **No schema migrations** - New metadata fields "just work"
3. **Bidirectional sync is clean** - All metadata round-trips perfectly
4. **Performance is acceptable** - Extracted fields provide indexed queries where needed
5. **Future-proof** - New use cases don't require code changes

The trade-off of slightly slower JSON queries is acceptable given the flexibility gained. For users with specific performance requirements on certain fields, those fields can be extracted to dedicated indexed columns.
