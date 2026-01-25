# Embranch - Version Controlled RAG for AI Agents

**Embranch** is a Model Context Protocol (MCP) server that brings **Git-like version control** to your RAG (Retrieval Augmented Generation) data. It combines **ChromaDB** for semantic vector search with **Dolt** for version control, enabling teams to collaborate on knowledge bases with full branching, merging, and history tracking.

## Overview

Embranch creates a bidirectional sync between:
- **ChromaDB** - Your working copy for semantic search and document operations
- **Dolt** - Your version control system for branching, commits, and team collaboration

```
                    ┌─────────────────────────────────────────────────┐
                    │             Embranch MCP Server                 │
                    ├─────────────────────────────────────────────────┤
                    │   ┌──────────────────┐  ┌──────────────────┐   │
                    │   │   ChromaDB       │  │     Dolt CLI     │   │
                    │   │   (Working Copy) │  │ (Version Control)│   │
                    │   │   • Semantic     │  │   • Branching    │   │
                    │   │     search       │  │   • Commits      │   │
                    │   │   • Real-time    │  │   • Push/Pull    │   │
                    │   │     queries      │  │   • Merge        │   │
                    │   └────────┬─────────┘  └────────┬─────────┘   │
                    │            │   Bidirectional     │             │
                    │            └────────Sync─────────┘             │
                    └─────────────────────────────────────────────────┘
```

Think of it like **Git for your AI knowledge base**:
- ChromaDB = Working directory (where you edit)
- Dolt = Git repository (where versions are stored)
- Sync happens at version control boundaries (commit, pull, checkout, merge)

## Key Features

- **Version Control for RAG Data** - Branch, commit, merge, and revert your knowledge base
- **Team Collaboration** - Push/pull via DoltHub for distributed teams
- **Semantic Search** - Full ChromaDB vector search capabilities
- **Conflict Resolution** - Preview and resolve merge conflicts before executing
- **Import/Export** - Import external ChromaDB databases with conflict handling
- **MCP Integration** - Works with Claude Desktop, Claude Code, and any MCP client

## Prerequisites

- **Dolt** installed and in PATH ([installation guide](https://docs.dolthub.com/introduction/installation))
- **Python 3.10+** with ChromaDB (for vector operations)

## Installation

### Download Release (Recommended)

Download the latest release for your platform from [GitHub Releases](https://github.com/PieterPrespective/VMRAG/releases/latest):

| Platform | Download |
|----------|----------|
| Windows x64 | `embranch-win-x64.zip` |
| Linux x64 | `embranch-linux-x64.zip` |
| macOS Intel | `embranch-osx-x64.zip` |
| macOS Apple Silicon | `embranch-osx-arm64.zip` |

Extract to a folder of your choice (e.g., `C:\Tools\Embranch` or `~/tools/embranch`).

<details>
<summary><strong>Development Install (Build from Source)</strong></summary>

If you want to contribute or run the latest development version:

#### 1. Prerequisites
- **.NET 9.0 SDK** or later

#### 2. Clone the repository

```bash
git clone https://github.com/PieterPrespective/VMRAG.git
cd VMRAG
```

#### 3. Build the project

```bash
dotnet build Embranch/Embranch.csproj
```

#### 4. Run with dotnet

Use `dotnet run --project Embranch/Embranch.csproj` instead of the executable in your MCP configuration.

</details>

### Configure Your MCP Client

Embranch works with any MCP-compatible client. Below are setup instructions for the most common clients.

<details>
<summary><strong>Claude Desktop</strong></summary>

Add to your `claude_desktop_config.json`:

**Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
**macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "embranch": {
      "command": "C:/Tools/Embranch/Embranch.exe",
      "env": {
        "CHROMA_DATA_PATH": "./data/chroma",
        "DOLT_REPOSITORY_PATH": "./data/dolt-repo",
        "DOLT_EXECUTABLE_PATH": "C:\\Program Files\\Dolt\\bin\\dolt.exe",
        "EMBRANCH_DATA_PATH": "./data",
        "EMBRANCH_PROJECT_ROOT": "./data",
        "ENABLE_LOGGING": "True",
        "LOG_LEVEL": "Debug",
        "LOG_FILE_NAME": "./logs/embranch.log"
      }
    }
  }
}
```

Restart Claude Desktop after saving the configuration.
(On Windows, you'll need to explicitly quit it via right-clicking the system tray icon)

</details>

<details>
<summary><strong>Claude Code (CLI)</strong></summary>

Create a `.mcp.json` file in your project root:

```json
{
  "mcpServers": {
    "embranch": {
      "command": "C:/Tools/Embranch/Embranch.exe",
      "env": {
        "CHROMA_DATA_PATH": "./data/chroma",
        "DOLT_REPOSITORY_PATH": "./data/dolt-repo",
        "DOLT_EXECUTABLE_PATH": "C:\\Program Files\\Dolt\\bin\\dolt.exe",
        "EMBRANCH_DATA_PATH": "./data",
        "EMBRANCH_PROJECT_ROOT": "./data",
        "ENABLE_LOGGING": "True",
        "LOG_LEVEL": "Debug",
        "LOG_FILE_NAME": "./logs/embranch.log"
      }
    }
  }
}
```

Claude Code automatically detects `.mcp.json` in your project root when you start a session.

</details>

<details>
<summary><strong>Auto-initialization from Remote (Optional)</strong></summary>

To automatically initialize with a DoltHub repository on first startup, set the `DOLT_REMOTE_URL` environment variable:

```json
{
  "env": {
    "DOLT_REMOTE_URL": "your-org/your-database"
  }
}
```

This creates a manifest file (`.embranch/state.json`) that tracks the remote URL. For reproducible environments with specific branch/commit targets, use the `init_manifest` tool or edit the manifest file directly.

This is useful for:
- Setting up reproducible environments for team members
- Ensuring everyone starts from the same data state
- CI/CD pipelines that need a specific database version

</details>

## Quick Start

Once configured, you can use natural language with Claude:

```
"Initialize a new knowledge base called 'project-docs'"
"Add this document to my knowledge base: [your content]"
"Search for documents about authentication"
"Create a branch called 'feature-updates'"
"Commit my changes with message 'Added API documentation'"
"Push my changes to DoltHub"
```

## Available Tools

Embranch provides 34 MCP tools organized into categories:

### ChromaDB Collection Management

| Tool | Description |
|------|-------------|
| `chroma_list_collections` | List all collections in the ChromaDB database |
| `chroma_create_collection` | Create a new collection for organizing documents |
| `chroma_delete_collection` | Delete a collection and all its documents |
| `chroma_get_collection_info` | Get metadata and statistics about a collection |
| `chroma_get_collection_count` | Get the number of documents in a collection |
| `chroma_modify_collection` | Update collection name or metadata |
| `chroma_peek_collection` | Preview documents in a collection |

### ChromaDB Document Operations

| Tool | Description |
|------|-------------|
| `chroma_add_documents` | Add documents with automatic chunking and embedding |
| `chroma_query_documents` | Semantic search with advanced filtering |
| `chroma_get_documents` | Retrieve documents by ID or filter |
| `chroma_update_documents` | Update existing document content or metadata |
| `chroma_delete_documents` | Remove documents from a collection |

### Dolt Repository Setup

| Tool | Description |
|------|-------------|
| `dolt_init` | Initialize a new Dolt repository for version control |
| `dolt_clone` | Clone an existing repository from DoltHub or remote |

### Dolt Version Control Operations

| Tool | Description |
|------|-------------|
| `dolt_status` | View current branch, commit, and uncommitted changes |
| `dolt_commit` | Commit ChromaDB changes to create a new version |
| `dolt_checkout` | Switch branches or revert to a specific commit |
| `dolt_reset` | Reset to a previous state, discarding changes |

### Dolt Branch Management

| Tool | Description |
|------|-------------|
| `dolt_branches` | List, create, rename, or delete branches |
| `dolt_commits` | View commit history and details |
| `dolt_show` | Show detailed information about a commit |
| `dolt_find` | Search for commits, branches, or content |

### Dolt Remote Synchronization

| Tool | Description |
|------|-------------|
| `dolt_fetch` | Download remote changes without merging |
| `dolt_pull` | Fetch and merge remote changes into current branch |
| `dolt_push` | Upload local commits to remote repository (DoltHub) |

### Merge Operations

| Tool | Description |
|------|-------------|
| `preview_dolt_merge` | Preview merge conflicts before executing |
| `execute_dolt_merge` | Execute merge with conflict resolution options |

### Import Operations

| Tool | Description |
|------|-------------|
| `preview_import` | Preview importing external ChromaDB with conflict detection |
| `execute_import` | Import external ChromaDB with resolution strategies |

### Server & Diagnostics

| Tool | Description |
|------|-------------|
| `get_server_version` | Get Embranch server version information |
| `diagnostics` | Run diagnostic checks on the system |

### Manifest Management

| Tool | Description |
|------|-------------|
| `init_manifest` | Initialize state manifest for reproducible environments |
| `update_manifest` | Update manifest with current Dolt state |
| `sync_to_manifest` | Synchronize local state to match manifest |

## Configuration

### Environment Variables

#### Core Storage Paths

| Variable | Description | Default |
|----------|-------------|---------|
| `CHROMA_DATA_PATH` | Path for ChromaDB persistent storage | `./data/chroma` |
| `DOLT_REPOSITORY_PATH` | Path for Dolt repository | `./data/dolt-repo` |
| `EMBRANCH_DATA_PATH` | Base data path for Embranch | `./data` |
| `EMBRANCH_PROJECT_ROOT` | Project root for manifest and state files | `./data` |

#### Dolt Configuration

| Variable | Description | Default |
|----------|-------------|---------|
| `DOLT_EXECUTABLE_PATH` | Path to Dolt executable | `dolt` (from PATH) |
| `DOLT_REPOSITORY_PATH` | Path to Dolt repository | `./data/dolt-repo` |
| `DOLT_REMOTE_URL` | DoltHub remote URL for initial manifest creation | - |
| `DOLT_REMOTE_NAME` | Name for the remote | `origin` |
| `DOLT_COMMAND_TIMEOUT` | Command timeout in milliseconds | `30000` |

#### Logging Configuration

| Variable | Description | Default |
|----------|-------------|---------|
| `ENABLE_LOGGING` | Enable detailed logging (`True`/`False`) | `False` |
| `LOG_LEVEL` | Logging verbosity (Debug/Info/Warning/Error) | `Information` |
| `LOG_FILE_NAME` | Path to log file | `./logs/embranch.log` |

## Setting Up DoltHub Remote

To enable team collaboration and remote backup, you'll need to set up a DoltHub account and create a remote database.

> For a comprehensive guide, see the official [Dolt and DoltHub Getting Started](https://www.dolthub.com/blog/2020-02-03-dolt-and-dolthub-getting-started/) tutorial.

### 1. Install Dolt

Download and install Dolt for your platform:

```bash
# Windows (using Chocolatey)
choco install dolt

# macOS (using Homebrew)
brew install dolt

# Linux
sudo bash -c 'curl -L https://github.com/dolthub/dolt/releases/latest/download/install.sh | bash'
```

Verify installation:
```bash
dolt version
```

### 2. Create a DoltHub Account

1. Go to [DoltHub.com](https://www.dolthub.com/) and sign up for a free account
2. Note your username - you'll use it in remote URLs (e.g., `your-username/database-name`)

### 3. Configure Dolt Credentials

Authenticate Dolt with your DoltHub account:

```bash
dolt login
```

This opens a browser window to complete authentication. Your credentials are stored locally for future use.

### 4. Create a Database on DoltHub

**Option A: Via DoltHub Web Interface**
1. Log in to [DoltHub](https://www.dolthub.com/)
2. Click "New Database"
3. Choose a name and visibility (public/private)
4. Note the database path: `your-username/database-name`

**Option B: Via Command Line**
```bash
# Initialize locally first
dolt init

# Add remote
dolt remote add origin your-username/database-name

# Push to create on DoltHub
dolt push -u origin main
```

### 5. Configure Embranch to Use Your Remote

Set the `DOLT_REMOTE_URL` environment variable in your MCP configuration:

```json
{
  "env": {
    "DOLT_REMOTE_URL": "your-username/database-name"
  }
}
```

Or clone an existing database using the `dolt_clone` tool:
```
"Clone the knowledge base from your-username/database-name"
```

### 6. Team Access (Optional)

To collaborate with others:
1. Go to your database on DoltHub
2. Navigate to Settings > Collaborators
3. Add team members by their DoltHub username
4. They can then clone and push to the shared database

## Architecture

Embranch uses a **bidirectional sync model**:

1. **Chroma to Dolt**: Before `commit` - stages local ChromaDB changes to Dolt
2. **Dolt to Chroma**: After `pull`, `checkout`, `merge`, `reset` - updates ChromaDB from Dolt

```
User Workflow:
    ┌─────────────────────────────────────────────────────────────┐
    │                                                             │
    │   Add/Edit Documents ──► ChromaDB ──► Commit ──► Dolt      │
    │         (working)         (sync)       (version)           │
    │                                                             │
    │   Pull/Checkout ──► Dolt ──► Sync ──► ChromaDB             │
    │      (fetch)      (version)         (working)              │
    │                                                             │
    └─────────────────────────────────────────────────────────────┘
```

### Data Storage

- **User Data** (versioned in Dolt): `documents`, `collections` tables
- **Operational Metadata** (local SQLite): sync state, deletion tracking
- **Vector Embeddings** (ChromaDB): semantic search indices

## Typical Workflows

### Solo Developer

```
1. dolt_init / dolt_clone     → Set up repository
2. chroma_add_documents       → Add your content
3. chroma_query_documents     → Search and verify
4. dolt_commit                → Save version
5. dolt_push                  → Backup to DoltHub
```

### Team Collaboration

```
1. dolt_clone                 → Get team repository
2. dolt_checkout (new branch) → Create feature branch
3. chroma_add/update          → Make changes
4. dolt_commit                → Commit changes
5. preview_dolt_merge         → Check for conflicts
6. execute_dolt_merge         → Merge to main
7. dolt_push                  → Share with team
```

### Importing External Data

```
1. preview_import             → Scan external DB, find conflicts
2. execute_import             → Import with resolution strategy
3. dolt_commit                → Version the import
```

## Troubleshooting

### Common Issues

**"DOLT_EXECUTABLE_NOT_FOUND"**
- Ensure Dolt is installed: `dolt version`
- Add Dolt to PATH or set `DOLT_EXECUTABLE_PATH`

**"NOT_INITIALIZED"**
- Run `dolt_init` or `dolt_clone` first

**"UNCOMMITTED_CHANGES"**
- Commit or reset changes before checkout/pull
- Use `if_uncommitted` parameter: `commit_first`, `reset_first`, or `carry`

**Merge Conflicts**
- Use `preview_dolt_merge` first to see conflicts
- Provide `conflict_resolutions` JSON to `execute_dolt_merge`

## Project Status: Active Side Project

Embranch is maintained as a passion project. Here's what that means for you:

| Expectation | Reality |
|-------------|---------|
| Response time | Days to weeks (I have a day job) |
| Release schedule | When ready, not on a calendar |
| Support | Community-driven, best effort |
| Roadmap | Flexible, driven by community needs |

### How You Can Help

- **Star the repo** - Helps others discover the project
- **Report bugs** - Detailed reports help me fix things faster
- **Improve docs** - PRs for documentation are always welcome
- **Answer questions** - Help others in Discussions
- **Contribute code** - See CONTRIBUTING.md

### Need More?

If you need guaranteed support, custom features, or SLAs, I offer limited consulting. [Contact me](mailto:Pieter.Weterings@Prespective-Software.com) to discuss.

## Technology Stack

- **Runtime**: .NET 9.0 (C#)
- **MCP SDK**: ModelContextProtocol.Server
- **Version Control**: Dolt (Git for data)
- **Vector DB**: ChromaDB via Python.NET
- **CLI Wrapper**: CliWrap for async process execution
- **Testing**: NUnit

## License

Apache License 2.0 - see [LICENSE](LICENSE) file for details.

## Acknowledgments

- [Dolt](https://www.dolthub.com/) - The world's first version-controlled SQL database
- [ChromaDB](https://www.trychroma.com/) - The AI-native open-source embedding database
- [Model Context Protocol](https://modelcontextprotocol.io/) - Open standard for AI-tool integration
