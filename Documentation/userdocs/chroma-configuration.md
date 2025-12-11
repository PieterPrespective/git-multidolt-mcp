# Chroma Configuration

This guide covers how to configure the Chroma vector database integration in DMMS (Dolt Multi-Database MCP Server). DMMS supports two operational modes for Chroma: **Persistent Mode** (file-based) and **Server Mode** (HTTP API).

## Quick Start

### Basic .mcp.json Configuration

Create or update your `.mcp.json` file in your project directory:

```json
{
  "mcpServers": {
    "dmms-chroma": {
      "command": "C:\\Path\\To\\DMMS.exe",
      "env": {
        "CHROMA_MODE": "persistent",
        "CHROMA_DATA_PATH": "./chroma_data",
        "ENABLE_LOGGING": "false"
      }
    }
  }
}
```

### Claude Desktop Configuration

Add to your Claude Desktop configuration file (`%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "dmms-chroma": {
      "command": "C:\\Program Files\\DMMS\\DMMS.exe",
      "env": {
        "CHROMA_MODE": "persistent",
        "CHROMA_DATA_PATH": "C:\\MyData\\ChromaVectorDB",
        "ENABLE_LOGGING": "true",
        "LOG_LEVEL": "Information"
      }
    }
  }
}
```

## Operational Modes

### Persistent Mode (Recommended)

**Best for:** Development, small to medium datasets, simple setups

Persistent mode stores vector data directly in local files without requiring a separate ChromaDB server.

#### Configuration:
```json
{
  "env": {
    "CHROMA_MODE": "persistent",
    "CHROMA_DATA_PATH": "./chroma_data"
  }
}
```

#### File Structure:
```
chroma_data/
├── collection_1/
│   ├── metadata.json          # Collection metadata
│   └── documents.jsonl        # Document storage (JSON Lines format)
├── collection_2/
│   ├── metadata.json
│   └── documents.jsonl
└── ...
```

#### Features:
- ✅ No external dependencies
- ✅ Simple file-based storage
- ✅ Automatic directory creation
- ✅ Human-readable data format
- ❌ Limited to text similarity search (no true vector embeddings)
- ❌ Performance decreases with large datasets

---

### Server Mode

**Best for:** Production applications, large datasets, true vector embeddings

Server mode connects to an external ChromaDB server instance via HTTP API.

#### Configuration:
```json
{
  "env": {
    "CHROMA_MODE": "server",
    "CHROMA_HOST": "localhost",
    "CHROMA_PORT": "8000"
  }
}
```

#### Prerequisites:
1. Install and run ChromaDB server:
   ```bash
   pip install chromadb
   chroma run --host localhost --port 8000
   ```

2. Verify server is running:
   ```bash
   curl http://localhost:8000/api/v1/heartbeat
   ```

#### Features:
- ✅ True vector embeddings and similarity search
- ✅ Optimized for large datasets
- ✅ Advanced filtering capabilities
- ✅ Production-ready performance
- ❌ Requires external ChromaDB server
- ❌ Additional setup complexity

## Environment Variables Reference

### Core Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `CHROMA_MODE` | `persistent` | Operation mode: `persistent` or `server` |
| `CHROMA_DATA_PATH` | `./chroma_data` | Local data directory for persistent mode |
| `CHROMA_HOST` | `localhost` | ChromaDB server host for server mode |
| `CHROMA_PORT` | `8000` | ChromaDB server port for server mode |

### Logging Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `ENABLE_LOGGING` | `false` | Enable or disable logging |
| `LOG_LEVEL` | `Information` | Log level: `Debug`, `Information`, `Warning`, `Error` |
| `LOG_FILE_NAME` | `dmms.log` | Log file name |

### Advanced Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `CONNECTION_TIMEOUT` | `86400.0` | Connection timeout in seconds |
| `BUFFER_SIZE` | `16777216` | Buffer size for data transfers (16MB) |
| `MAX_RETRIES` | `3` | Maximum retry attempts for operations |
| `RETRY_DELAY` | `1.0` | Delay between retry attempts in seconds |

## Storage Configuration

### Persistent Mode Storage

#### Data Directory Structure
```
{CHROMA_DATA_PATH}/
├── collection_name/
│   ├── metadata.json          # Collection information
│   └── documents.jsonl        # Document storage
```

#### Collection Metadata Format
```json
{
  "name": "my_collection",
  "metadata": {
    "description": "My document collection",
    "created_by": "user"
  },
  "created_at": "2024-01-15T10:30:00.000Z",
  "document_count": 125,
  "last_updated": "2024-01-16T14:20:00.000Z"
}
```

#### Document Storage Format (JSONL)
```jsonl
{"id": "doc_001", "document": "Document content here", "metadata": {"source": "email", "date": "2024-01-15"}, "created_at": "2024-01-15T10:30:00.000Z"}
{"id": "doc_002", "document": "Another document", "metadata": {"source": "file", "category": "report"}, "created_at": "2024-01-15T10:31:00.000Z"}
```

### Storage Location Recommendations

#### Development
```json
{
  "CHROMA_DATA_PATH": "./chroma_data"
}
```

#### Production (Windows)
```json
{
  "CHROMA_DATA_PATH": "C:\\ProgramData\\DMMS\\ChromaData"
}
```

#### User-specific Data
```json
{
  "CHROMA_DATA_PATH": "%USERPROFILE%\\Documents\\ChromaVectorDB"
}
```

## Configuration Examples

### Development Setup
```json
{
  "mcpServers": {
    "dmms-dev": {
      "command": "C:\\Dev\\DMMS\\bin\\Debug\\net9.0\\DMMS.exe",
      "env": {
        "CHROMA_MODE": "persistent",
        "CHROMA_DATA_PATH": "./dev_chroma_data",
        "ENABLE_LOGGING": "true",
        "LOG_LEVEL": "Debug"
      }
    }
  }
}
```

### Production Setup (Persistent)
```json
{
  "mcpServers": {
    "dmms-prod": {
      "command": "C:\\Program Files\\DMMS\\DMMS.exe",
      "env": {
        "CHROMA_MODE": "persistent",
        "CHROMA_DATA_PATH": "C:\\ProgramData\\DMMS\\ChromaData",
        "ENABLE_LOGGING": "true",
        "LOG_LEVEL": "Information",
        "LOG_FILE_NAME": "dmms_prod.log"
      }
    }
  }
}
```

### Production Setup (Server Mode)
```json
{
  "mcpServers": {
    "dmms-server": {
      "command": "C:\\Program Files\\DMMS\\DMMS.exe",
      "env": {
        "CHROMA_MODE": "server",
        "CHROMA_HOST": "chroma-server.company.com",
        "CHROMA_PORT": "8000",
        "CONNECTION_TIMEOUT": "30.0",
        "MAX_RETRIES": "5",
        "ENABLE_LOGGING": "true",
        "LOG_LEVEL": "Warning"
      }
    }
  }
}
```

### Multiple Environment Setup
```json
{
  "mcpServers": {
    "dmms-dev": {
      "command": "C:\\Dev\\DMMS\\DMMS.exe",
      "env": {
        "CHROMA_MODE": "persistent",
        "CHROMA_DATA_PATH": "./dev_data"
      }
    },
    "dmms-prod": {
      "command": "C:\\Program Files\\DMMS\\DMMS.exe",
      "env": {
        "CHROMA_MODE": "server",
        "CHROMA_HOST": "prod-chroma.company.com",
        "CHROMA_PORT": "8000"
      }
    }
  }
}
```

## Security Considerations

### File Permissions (Persistent Mode)
Ensure proper permissions for the data directory:

```powershell
# Windows - Set appropriate permissions
icacls "C:\ProgramData\DMMS\ChromaData" /grant "Users:(OI)(CI)F"
```

### Network Security (Server Mode)
- Use HTTPS when possible
- Configure firewall rules for ChromaDB port
- Consider VPN or private network access
- Implement authentication if available

## Database Migration and Compatibility

### Automatic Migration from Older ChromaDB Versions

DMMS automatically detects and migrates older ChromaDB databases from previous versions and other Chroma implementations. This ensures compatibility with existing data from various sources.

#### Supported Migration Sources
- **chromadb-mcp**: Legacy ChromaDB MCP implementations
- **ChromaDB 0.4.x and earlier**: Older schema versions
- **Custom ChromaDB setups**: Various schema configurations

#### Migration Process
When DMMS starts with an existing ChromaDB database, it automatically:

1. **Detects schema version**: Analyzes the SQLite database structure
2. **Identifies compatibility issues**: Checks for column name variations and format differences
3. **Performs safe migration**: Updates schema while preserving all data
4. **Validates migration**: Ensures collections and documents remain accessible

#### Migration Features
- ✅ **Automatic detection** of older database formats
- ✅ **Zero data loss** migration process
- ✅ **Idempotent operations** (safe to run multiple times)
- ✅ **Backward compatibility** with various schema versions
- ✅ **Configuration preservation** with sensible defaults

#### Common Migration Scenarios

**Legacy Column Names:**
- `config_json_str` → `configuration_json_str`
- Empty configuration objects get default `_type` fields
- Metadata normalization and validation

**Database Path Examples:**
```json
{
  "env": {
    "CHROMA_MODE": "persistent",
    "CHROMA_DATA_PATH": "C:\\Users\\Username\\existing_chroma_db"
  }
}
```

#### Migration Validation
After migration, DMMS validates:
- Collection accessibility
- Document count preservation  
- Metadata integrity
- Query functionality

If migration issues occur, check the logs for detailed diagnostics:
```json
{
  "env": {
    "ENABLE_LOGGING": "true",
    "LOG_LEVEL": "Debug"
  }
}
```

### Manual Migration Between Modes

#### From Persistent to Server Mode

1. Export collections from persistent storage:
   ```bash
   # Custom migration script (not included in DMMS)
   python migrate_persistent_to_server.py
   ```

2. Update configuration:
   ```json
   {
     "CHROMA_MODE": "server",
     "CHROMA_HOST": "localhost",
     "CHROMA_PORT": "8000"
   }
   ```

#### From Server to Persistent Mode

1. Export data from ChromaDB server
2. Update configuration to persistent mode
3. Import data using DMMS tools

## Troubleshooting Configuration

### Common Issues

#### Persistent Mode Issues

**Issue:** Permission denied accessing data directory
```
Solution: Ensure DMMS has read/write permissions to CHROMA_DATA_PATH
```

**Issue:** Invalid JSON in documents.jsonl
```
Solution: Check for corrupted files, restore from backup
```

**Issue:** Collection not found
```
Solution: Verify directory exists and has proper structure
```

**Issue:** Database migration errors (older ChromaDB versions)
```
Error: "no such column: configuration_json_str"
Solution: DMMS automatically handles this migration. Enable debug logging to troubleshoot:
{
  "ENABLE_LOGGING": "true",
  "LOG_LEVEL": "Debug"
}
```

**Issue:** Python.NET execution errors during migration
```
Error: "'list' object has no attribute 'GetLength'"
Solution: This has been resolved in current DMMS versions. Update to latest version.
```

**Issue:** File locking during database operations
```
Error: "The process cannot access the file because it is being used by another process"
Solution: Normal during heavy operations. DMMS handles this gracefully with retry logic.
```

#### Server Mode Issues

**Issue:** Connection refused to ChromaDB server
```
Solution: Verify server is running and accessible
curl http://CHROMA_HOST:CHROMA_PORT/api/v1/heartbeat
```

**Issue:** Timeout errors
```
Solution: Increase CONNECTION_TIMEOUT value
```

**Issue:** Authentication errors
```
Solution: Configure proper authentication credentials
```

### Diagnostic Steps

1. **Check Configuration Syntax:**
   ```bash
   # Validate JSON syntax
   python -m json.tool .mcp.json
   ```

2. **Test DMMS Startup:**
   ```bash
   # Run DMMS manually to check for errors
   "C:\Path\To\DMMS.exe"
   ```

3. **Check Logs:**
   ```bash
   # Enable logging and check log file
   tail -f dmms.log
   ```

4. **Verify Environment:**
   ```bash
   # Check environment variables
   echo %CHROMA_MODE%
   echo %CHROMA_DATA_PATH%
   ```

## Performance Tuning

### Persistent Mode Optimization

- **Use SSD storage** for better I/O performance
- **Regular cleanup** of unused collections
- **Batch document operations** when possible
- **Monitor disk space** usage

### Server Mode Optimization

- **Dedicated ChromaDB server** for production
- **Adequate RAM** for vector operations
- **Network optimization** between DMMS and ChromaDB
- **Connection pooling** configuration

## Backup and Recovery

### Persistent Mode Backup
```powershell
# Simple backup strategy
robocopy "C:\ProgramData\DMMS\ChromaData" "C:\Backups\ChromaData" /E /DCOPY:DAT
```

### Server Mode Backup
Follow ChromaDB server backup procedures for your deployment.

For more information, see:
- [Chroma Tools Reference](chroma-tools-reference.md)
- [Troubleshooting Guide](troubleshooting.md)
- [Basic Usage Examples](basic-usage.md)