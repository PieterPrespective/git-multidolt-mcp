# Configuration Guide

This guide covers configuring the VM RAG MCP Server in Claude Desktop and understanding the configuration options.

## Claude Desktop Configuration

### Locating the Configuration File

The Claude Desktop configuration file is located at:
```
%APPDATA%\Claude\claude_desktop_config.json
```

To navigate there:
1. Press `Win + R` to open the Run dialog
2. Type `%APPDATA%\Claude` and press Enter
3. Open `claude_desktop_config.json` in a text editor

### Basic Configuration

Add the VM RAG MCP Server to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "vm-rag": {
      "command": "C:\\Program Files\\VM-RAG-MCP\\Embranch.exe"
    }
  }
}
```

Replace the path with your actual installation location. **Note:** Use double backslashes (`\\`) in Windows paths.

### Configuration with Environment Variables

The server uses environment variables for configuration. Here's a comprehensive example:

```json
{
  "mcpServers": {
    "vm-rag": {
      "command": "C:\\Program Files\\VM-RAG-MCP\\Embranch.exe",
      "env": {
        "CHROMA_DATA_PATH": "./data/chroma_data",
        "DOLT_REPOSITORY_PATH": "./data/dolt-repo",
        "DOLT_EXECUTABLE_PATH": "C:\\Program Files\\Dolt\\bin\\dolt.exe",
        "ENABLE_LOGGING": "true",
        "LOG_LEVEL": "Information",
        "LOG_FILE_NAME": "./logs/vm-rag.log"
      }
    }
  }
}
```

## Environment Variables Reference

### Core Storage Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `CHROMA_DATA_PATH` | ChromaDB storage directory | `./chroma_data` | `C:\\Data\\ChromaDB` |
| `DOLT_REPOSITORY_PATH` | Dolt repository directory | `./data/dolt-repo` | `C:\\Data\\DoltRepo` |

⚠️ **Important:** These paths define where your knowledge base is stored. Ensure:
- The directories are writable
- They're on a drive with sufficient space
- They're backed up if storing important data

### Dolt Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `DOLT_EXECUTABLE_PATH` | Path to Dolt CLI | `C:\Program Files\Dolt\bin\dolt.exe` (Windows) | `C:\\Tools\\dolt.exe` |
| `DOLT_REMOTE_NAME` | Default remote name | `origin` | `origin` |
| `DOLT_REMOTE_URL` | Default remote URL | *(none)* | `myorg/knowledge-base` |
| `DOLT_COMMAND_TIMEOUT` | Command timeout (ms) | `30000` | `60000` |
| `DOLT_DEBUG_LOGGING` | Enable Dolt debug logs | `false` | `true` |

### ChromaDB Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `CHROMA_HOST` | ChromaDB server host | `localhost` | `127.0.0.1` |
| `CHROMA_PORT` | ChromaDB server port | `8000` | `8001` |
| `CHROMA_MODE` | Mode: `persistent` or `client` | `persistent` | `persistent` |

### Server Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `MCP_PORT` | MCP server port | `6500` | `6501` |
| `CONNECTION_TIMEOUT` | Connection timeout (sec) | `86400` | `3600` |
| `BUFFER_SIZE` | Buffer size (bytes) | `16777216` | `33554432` |
| `MAX_RETRIES` | Maximum retry attempts | `3` | `5` |
| `RETRY_DELAY` | Retry delay (sec) | `1.0` | `2.0` |

### Logging Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `ENABLE_LOGGING` | Enable file logging | `false` | `true` |
| `LOG_LEVEL` | Log level | `Information` | `Debug` |
| `LOG_FILE_NAME` | Log file path | *(none)* | `./logs/vm-rag.log` |

**Log Levels:** `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`

## Storage Folder Configuration

### Critical Storage Settings

The server requires two main storage locations:

#### 1. ChromaDB Data Directory (`CHROMA_DATA_PATH`)
- **Purpose:** Stores document embeddings and metadata
- **Default:** `./chroma_data`
- **Requirements:**
  - Read/write access
  - Sufficient space for embeddings (typically 1KB-2KB per document chunk)
  - Fast storage recommended (SSD preferred)

#### 2. Dolt Repository Directory (`DOLT_REPOSITORY_PATH`)
- **Purpose:** Version control metadata and change tracking
- **Default:** `./data/dolt-repo`
- **Requirements:**
  - Read/write access
  - Git/Dolt repository structure will be created here
  - Should be included in backups

### Recommended Directory Structure

```
C:\MyProject\
├── vm-rag-data\
│   ├── chroma_data\          # ChromaDB storage (CHROMA_DATA_PATH)
│   │   ├── chroma.sqlite3    # ChromaDB database file
│   │   └── ...               # Other ChromaDB files
│   └── dolt-repo\            # Dolt repository (DOLT_REPOSITORY_PATH)
│       ├── .dolt\            # Dolt metadata
│       ├── documents\        # Dolt data tables
│       └── ...               # Other Dolt files
├── logs\                     # Log files (LOG_FILE_NAME directory)
│   └── vm-rag.log           # Application logs
└── .gitignore               # Exclude data directories from Git
```

### Configuration Example for Production

For a production setup with dedicated storage:

```json
{
  "mcpServers": {
    "vm-rag-prod": {
      "command": "C:\\Program Files\\VM-RAG-MCP\\Embranch.exe",
      "env": {
        "CHROMA_DATA_PATH": "D:\\KnowledgeBase\\chroma",
        "DOLT_REPOSITORY_PATH": "D:\\KnowledgeBase\\dolt-repo",
        "DOLT_REMOTE_URL": "myorg/production-kb",
        "ENABLE_LOGGING": "true",
        "LOG_LEVEL": "Warning",
        "LOG_FILE_NAME": "D:\\Logs\\vm-rag\\production.log"
      }
    }
  }
}
```

## Multiple Configuration Profiles

You can configure different profiles for different knowledge bases:

```json
{
  "mcpServers": {
    "vm-rag-dev": {
      "command": "C:\\Program Files\\VM-RAG-MCP\\Embranch.exe",
      "env": {
        "CHROMA_DATA_PATH": "./data/dev/chroma",
        "DOLT_REPOSITORY_PATH": "./data/dev/dolt-repo",
        "DOLT_REMOTE_URL": "myorg/dev-kb",
        "LOG_LEVEL": "Debug"
      }
    },
    "vm-rag-docs": {
      "command": "C:\\Program Files\\VM-RAG-MCP\\Embranch.exe",
      "env": {
        "CHROMA_DATA_PATH": "./data/docs/chroma",
        "DOLT_REPOSITORY_PATH": "./data/docs/dolt-repo",
        "DOLT_REMOTE_URL": "myorg/documentation-kb",
        "LOG_LEVEL": "Information"
      }
    }
  }
}
```

⚠️ **Note:** Only one server instance can run at a time per Claude session.

## Applying Configuration Changes

After editing the configuration:

1. **Save** the `claude_desktop_config.json` file
2. **Quit Claude Desktop completely**:
   - Right-click the Claude icon in system tray
   - Select "Quit Claude"
3. **Restart Claude Desktop**
4. **Verify** the configuration by asking Claude to list collections or check server version

## Security Considerations

### File Permissions
- Ensure the storage directories are only accessible to your user account
- Consider encrypting the storage drive if handling sensitive data

### DoltHub Credentials
- Dolt credentials are stored separately by the Dolt CLI
- Run `dolt login` in a terminal to configure DoltHub access
- Never store credentials in the MCP configuration file

### Network Access
- The server only listens on localhost by default
- ChromaDB connections are local unless explicitly configured otherwise

## Troubleshooting Configuration Issues

### Common Problems

| Issue | Symptom | Solution |
|-------|---------|----------|
| **Path not found** | Server won't start | Check all paths exist and use double backslashes |
| **Permission denied** | ChromaDB errors | Ensure write access to `CHROMA_DATA_PATH` |
| **Dolt not found** | Dolt tools fail | Verify `DOLT_EXECUTABLE_PATH` points to `dolt.exe` |
| **Python errors** | ChromaDB won't initialize | Ensure Python and ChromaDB are installed |
| **JSON syntax error** | Claude won't start server | Validate JSON with a JSON validator |

### Validation Steps

1. **Test paths manually:**
   ```bash
   # Test Dolt
   "C:\Program Files\Dolt\bin\dolt.exe" version
   
   # Test Python/ChromaDB
   python -c "import chromadb; print('ChromaDB OK')"
   ```

2. **Check directory permissions:**
   ```bash
   # Ensure you can create files in the data directories
   echo test > ./data/test.txt
   del ./data/test.txt
   ```

3. **Validate JSON:**
   Use an online JSON validator to check your configuration syntax.

For more troubleshooting help, see the [Troubleshooting Guide](troubleshooting.md).