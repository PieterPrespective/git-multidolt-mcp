# Getting Started with VM RAG MCP Server

Welcome to the VM RAG (Version-Managed Retrieval-Augmented Generation) MCP Server documentation. This server enables Claude to interact with a version-controlled knowledge base through the Model Context Protocol (MCP).

## What is the VM RAG MCP Server?

The VM RAG MCP Server is a Model Context Protocol server that provides tools for:
- **Document Management**: Add, query, update, and delete documents using ChromaDB for semantic search
- **Version Control**: Track changes, create commits, and manage branches using Dolt
- **Knowledge Base Collaboration**: Share knowledge bases with teams via DoltHub
- **RAG Operations**: Semantic search and retrieval for AI assistance

## What is MCP?

The Model Context Protocol (MCP) is a standard that allows AI assistants like Claude to interact with external tools and services. Embranch implements this protocol to give Claude access to Dolt database functionality.

## Quick Start

Follow these steps to get the VM RAG MCP Server up and running:

1. **Install Prerequisites**
   - [.NET 9.0 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
   - [Python 3.8+](https://python.org) with ChromaDB installed (`pip install chromadb`)
   - [Claude Desktop](https://claude.ai/download)
   - [Dolt](https://www.dolthub.com/docs/getting-started/installation/) (for version control)

2. **Install VM RAG MCP Server**
   - Download the latest release
   - Extract to a folder (e.g., `C:\Program Files\VM-RAG-MCP`)
   - Note the path to `Embranch.exe`

3. **Configure Claude Desktop**
   - Edit `%APPDATA%\Claude\claude_desktop_config.json`
   - Add the VM RAG MCP Server configuration:
   ```json
   {
     "mcpServers": {
       "vm-rag": {
         "command": "C:\\Program Files\\VM-RAG-MCP\\Embranch.exe",
         "env": {
           "CHROMA_DATA_PATH": "./data/chroma_data",
           "DOLT_REPOSITORY_PATH": "./data/dolt-repo"
         }
       }
     }
   }
   ```

4. **Restart Claude Desktop**
   - Completely quit Claude from the system tray
   - Start Claude Desktop again

5. **Initialize Your Knowledge Base**
   - Ask Claude: "Initialize a new knowledge base"
   - Or: "Clone the knowledge base from [DoltHub URL]"

6. **Verify Installation**
   - Ask Claude: "What version of the VM RAG server is running?"
   - Ask Claude: "List my collections"
   - Claude should respond with the server version and available collections

## Features

### Document Management (ChromaDB)
- **Collection Management**: Create, modify, and delete document collections
- **Document Operations**: Add, update, delete, and retrieve documents
- **Semantic Search**: Query documents using natural language
- **Metadata Filtering**: Filter documents by metadata fields
- **Document Chunking**: Automatic text chunking and embedding generation

### Version Control (Dolt)
- **Repository Management**: Initialize new repositories or clone existing ones
- **Commit Operations**: Save document changes as versioned commits
- **Branch Management**: Create, switch between, and merge branches
- **Remote Sync**: Push and pull changes to/from DoltHub
- **History Tracking**: View commit history and document changes over time

### Integration Features
- **Automatic Synchronization**: ChromaDB state synced with Dolt repository
- **Change Detection**: Track additions, modifications, and deletions
- **Conflict Resolution**: Handle merge conflicts during collaborative workflows
- **Status Monitoring**: Check repository status and uncommitted changes

## System Requirements

### Minimum Requirements
- Windows 10 version 1903 or later (64-bit)
- .NET 9.0 Runtime
- Python 3.8+ with ChromaDB (`pip install chromadb`)
- 4 GB RAM
- 500 MB available disk space (plus space for knowledge bases)

### Recommended Requirements
- Windows 11 (64-bit)
- .NET 9.0 SDK (for development)
- Python 3.10+ with ChromaDB
- 8 GB RAM or more
- SSD with adequate space for your knowledge bases
- Dolt CLI for version control features

## Documentation Structure

This documentation is organized into the following sections:

- **[Installation](installation.md)**: Detailed installation instructions
- **[Configuration](configuration.md)**: Setting up the server in Claude Desktop
- **[Tool Reference](tools-reference.md)**: Complete guide to all available tools
- **[Basic Usage](basic-usage.md)**: Common operations and workflows
- **[Troubleshooting](troubleshooting.md)**: Solutions to common problems

## Getting Help

If you need assistance:

1. Check the [Troubleshooting Guide](troubleshooting.md)
2. Review the [FAQ](#frequently-asked-questions)
3. Report issues on GitHub
4. Contact the development team

## Frequently Asked Questions

**Q: Is the VM RAG MCP Server free to use?**
A: Yes, the VM RAG MCP Server is open-source software.

**Q: Can I use the server without Dolt installed?**
A: You can use the ChromaDB features without Dolt, but version control functionality requires Dolt to be installed.

**Q: Does this work with other AI assistants?**
A: The server is designed for Claude but can work with any MCP-compatible client.

**Q: How do I share knowledge bases with my team?**
A: Use DoltHub to push/pull knowledge base versions, similar to how Git works for code.

**Q: Can I import existing documents?**
A: Yes, you can add documents via the `chroma_add_documents` tool or by asking Claude to "add this document to the knowledge base."

**Q: What happens to my data when I switch branches?**
A: ChromaDB is updated to reflect the documents from the selected branch. Previous branch data is preserved in version control.

## Next Steps

Now that you understand what the VM RAG MCP Server is and how it works, proceed to:
1. [Complete the installation](installation.md)
2. [Configure Claude Desktop](configuration.md)
3. [Learn about available tools](tools-reference.md)
4. [Start using the knowledge base](basic-usage.md)