# Troubleshooting DMMS

This guide helps you diagnose and fix common issues with the Dolt Multi-Database MCP Server.

## Quick Diagnostics

### 1. Test DMMS Executable

Open Command Prompt or PowerShell and run:
```bash
"C:\Path\To\DMMS.exe" --help
```

If this fails, check:
- The path is correct
- .NET 9.0 runtime is installed
- The executable has proper permissions

### 2. Check .NET Installation

```bash
dotnet --info
```

You should see .NET 9.0 in the list of installed runtimes.

### 3. Verify Claude Configuration

Check your `%APPDATA%\Claude\claude_desktop_config.json` for:
- Correct JSON syntax
- Proper path formatting (double backslashes)
- No trailing commas

## Common Issues and Solutions

### DMMS Not Appearing in Claude

**Symptoms:**
- No MCP indicator in Claude
- Tools not available
- No response from DMMS commands

**Solutions:**

1. **Verify configuration file location:**
   ```
   %APPDATA%\Claude\claude_desktop_config.json
   ```

2. **Check JSON syntax:**
   ```json
   {
     "mcpServers": {
       "dmms": {
         "command": "C:\\Path\\To\\DMMS.exe"
       }
     }
   }
   ```

3. **Restart Claude completely:**
   - Exit Claude from system tray
   - Wait 10 seconds
   - Start Claude again

### "Command Not Found" Error

**Cause:** Incorrect path to DMMS.exe

**Solution:**
1. Verify the exact path to DMMS.exe
2. Use double backslashes in the path
3. Avoid spaces in the path if possible, or use quotes

**Example with spaces:**
```json
{
  "mcpServers": {
    "dmms": {
      "command": "\"C:\\Program Files\\DMMS\\DMMS.exe\""
    }
  }
}
```

### Server Starts but Immediately Crashes

**Possible causes:**
- Missing dependencies
- Configuration errors
- Permission issues

**Debugging steps:**

1. **Run DMMS manually to see error messages:**
   ```bash
   "C:\Path\To\DMMS.exe"
   ```

2. **Check Windows Event Viewer:**
   - Press `Win + X`, select "Event Viewer"
   - Navigate to Windows Logs > Application
   - Look for .NET or DMMS errors

3. **Enable debug logging:**
   ```json
   {
     "mcpServers": {
       "dmms": {
         "command": "C:\\Path\\To\\DMMS.exe",
         "env": {
           "DMMS_LOG_LEVEL": "Debug"
         }
       }
     }
   }
   ```

### .NET Runtime Errors

**Error:** "The required library hostfxr.dll was not found"

**Solution:**
1. Install .NET 9.0 Runtime: https://dotnet.microsoft.com/download/dotnet/9.0
2. Choose the x64 version for 64-bit Windows
3. Restart your computer after installation

### Permission Denied Errors

**Symptoms:**
- "Access is denied" errors
- Unable to execute DMMS.exe

**Solutions:**

1. **Check file permissions:**
   - Right-click DMMS.exe
   - Select Properties > Security
   - Ensure your user has "Read & Execute" permission

2. **Unblock the file (if downloaded):**
   - Right-click DMMS.exe
   - Select Properties
   - Check for "Unblock" checkbox at the bottom
   - Click "Unblock" if present

3. **Run Claude as Administrator (last resort):**
   - Right-click Claude Desktop
   - Select "Run as administrator"

### Tools Not Working

**Symptom:** Claude says tools are not available or not responding

**Debugging:**

1. **Check if DMMS is running:**
   - Open Task Manager
   - Look for DMMS.exe in the processes

2. **Test with a simple command:**
   Ask Claude: "Can you get the DMMS server version?"

3. **Check for error messages in Claude's response**

## Logging and Debugging

### Enable Verbose Logging

Add to your configuration:
```json
{
  "mcpServers": {
    "dmms": {
      "command": "C:\\Path\\To\\DMMS.exe",
      "args": ["--verbose"],
      "env": {
        "DMMS_LOG_LEVEL": "Debug",
        "DMMS_LOG_FILE": "C:\\Logs\\dmms.log"
      }
    }
  }
}
```

### Log File Locations

Default log locations:
- DMMS logs: `%TEMP%\DMMS\logs\`
- Claude logs: `%APPDATA%\Claude\logs\`

### Reading Log Files

Look for:
- ERROR level messages
- Stack traces
- Connection failures
- Tool execution errors

## Getting Further Help

If you're still experiencing issues:

1. **Collect diagnostic information:**
   - DMMS version (`DMMS.exe --version`)
   - .NET version (`dotnet --info`)
   - Claude Desktop version
   - Complete error messages
   - Relevant log entries

2. **Check for updates:**
   - Latest DMMS release
   - Latest Claude Desktop version
   - .NET runtime updates

3. **Report issues:**
   - GitHub Issues page
   - Include diagnostic information
   - Provide reproduction steps

## Chroma-Specific Troubleshooting

### Chroma Collection Issues

**Issue:** "Collection does not exist" error

**Solutions:**
1. **List available collections first:**
   ```
   Can you list all my Chroma collections?
   ```

2. **Check collection name spelling:**
   - Collection names are case-sensitive
   - Avoid special characters

3. **In persistent mode, check directory structure:**
   ```
   {CHROMA_DATA_PATH}/
   ├── collection_name/
   │   ├── metadata.json
   │   └── documents.jsonl
   ```

### Chroma Document Issues

**Issue:** "Document ID already exists" error

**Solution:** Use unique document IDs or delete existing document first:
```
Delete the document with ID "duplicate_id" from "my_collection", then try adding again
```

**Issue:** Documents not found in search

**Debugging steps:**
1. **Check document was added successfully:**
   ```
   How many documents are in my "collection_name" collection?
   ```

2. **Verify search terms:**
   - Use exact words from document content
   - Try broader search terms

3. **In persistent mode, check documents.jsonl format:**
   - Each line should be valid JSON
   - No empty lines between documents

### Chroma Storage Issues

**Issue:** Permission denied accessing Chroma data directory

**Solutions:**
1. **Check directory permissions:**
   ```powershell
   icacls "C:\Path\To\ChromaData" /grant "%USERNAME%:(F)"
   ```

2. **Ensure directory exists:**
   ```powershell
   mkdir "C:\Path\To\ChromaData"
   ```

3. **Update CHROMA_DATA_PATH to writable location:**
   ```json
   {
     "env": {
       "CHROMA_DATA_PATH": "%USERPROFILE%\\Documents\\ChromaData"
     }
   }
   ```

### Chroma Server Mode Issues

**Issue:** Connection refused to ChromaDB server

**Debugging steps:**
1. **Verify ChromaDB server is running:**
   ```bash
   curl http://localhost:8000/api/v1/heartbeat
   ```

2. **Check server configuration:**
   ```json
   {
     "env": {
       "CHROMA_MODE": "server",
       "CHROMA_HOST": "localhost",
       "CHROMA_PORT": "8000"
     }
   }
   ```

3. **Start ChromaDB server if needed:**
   ```bash
   pip install chromadb
   chroma run --host localhost --port 8000
   ```

**Issue:** ChromaDB server timeouts

**Solutions:**
1. **Increase timeout values:**
   ```json
   {
     "env": {
       "CONNECTION_TIMEOUT": "60.0",
       "MAX_RETRIES": "5"
     }
   }
   ```

2. **Check network connectivity:**
   ```bash
   ping your-chroma-host
   ```

### Chroma Data Corruption

**Issue:** Invalid JSON in documents.jsonl

**Solutions:**
1. **Backup corrupted file:**
   ```powershell
   copy "documents.jsonl" "documents.jsonl.backup"
   ```

2. **Remove malformed lines:**
   - Open documents.jsonl in text editor
   - Look for lines that don't start/end with `{}`
   - Remove or fix malformed JSON lines

3. **Recreate collection if severely corrupted:**
   ```
   Delete the "corrupted_collection" and create a new one with the same name
   ```

### Chroma Performance Issues

**Issue:** Slow query performance in persistent mode

**Solutions:**
1. **Reduce collection size:**
   - Split large collections into smaller ones
   - Remove unused documents

2. **Use more specific search terms:**
   - Avoid very common words
   - Use multiple relevant keywords

3. **Consider switching to server mode:**
   ```json
   {
     "env": {
       "CHROMA_MODE": "server"
     }
   }
   ```

**Issue:** Memory usage growing over time

**Solutions:**
1. **Restart DMMS periodically:**
   - Exit and restart Claude Desktop
   - Memory will be cleared

2. **Monitor collection sizes:**
   ```
   Can you show me the document count for all my collections?
   ```

### Chroma Configuration Validation

**Test Configuration:**
```json
{
  "mcpServers": {
    "dmms-test": {
      "command": "C:\\Path\\To\\DMMS.exe",
      "env": {
        "CHROMA_MODE": "persistent",
        "CHROMA_DATA_PATH": "./test_chroma_data",
        "ENABLE_LOGGING": "true",
        "LOG_LEVEL": "Debug"
      }
    }
  }
}
```

**Validation Steps:**
1. Test server startup
2. Create a test collection
3. Add a test document
4. Query the document
5. Delete the test collection

### Chroma Migration Issues

**Issue:** Moving from persistent to server mode

**Steps:**
1. **Export data from persistent mode:**
   - Backup your CHROMA_DATA_PATH directory
   - Note all collection names and document IDs

2. **Setup ChromaDB server:**
   - Install and start ChromaDB server
   - Verify connectivity

3. **Update configuration to server mode:**
   ```json
   {
     "env": {
       "CHROMA_MODE": "server",
       "CHROMA_HOST": "localhost",
       "CHROMA_PORT": "8000"
     }
   }
   ```

4. **Manually recreate collections and add documents**

## Frequently Asked Questions

**Q: Can I use DMMS with Claude.ai web version?**
A: No, MCP servers only work with Claude Desktop application.

**Q: Does DMMS work on Windows 11?**
A: Yes, DMMS is compatible with Windows 10 and Windows 11.

**Q: Can I run multiple instances of DMMS?**
A: Yes, you can configure multiple instances with different names in the configuration.

**Q: How do I update DMMS?**
A: Download the latest release and replace the executable, then restart Claude Desktop.

**Q: Is DMMS compatible with WSL?**
A: DMMS runs natively on Windows. For WSL compatibility, see the WSL documentation.

**Q: Can I use both Chroma modes simultaneously?**
A: Yes, configure multiple DMMS instances with different names and modes.

**Q: What's the maximum collection size for persistent mode?**
A: While there's no hard limit, performance degrades significantly above 10,000 documents per collection.

**Q: Can I backup my Chroma data?**
A: In persistent mode, simply backup the CHROMA_DATA_PATH directory. For server mode, follow ChromaDB backup procedures.

**Q: How do I migrate between Chroma modes?**
A: Currently requires manual export/import of data. See Chroma Migration Issues section above.