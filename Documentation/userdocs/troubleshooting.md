# Troubleshooting Guide

This guide helps you diagnose and resolve common issues with the VM RAG MCP Server.

## Quick Diagnosis

### Is the Server Running?

**Ask Claude:** "What version of the VM RAG server is running?"

✅ **Working:** Claude responds with version information  
❌ **Not working:** Claude says it doesn't have access to those tools

### Are Tools Available?

**Ask Claude:** "List my collections"

✅ **Working:** Claude shows collections or empty list  
❌ **Not working:** Claude reports an error or says it can't access the tool

### Can Claude See Your Data?

**Ask Claude:** "How many documents are in my knowledge base?"

✅ **Working:** Claude reports a count  
❌ **Not working:** Claude reports errors or no access

---

## Installation & Startup Issues

### Server Won't Start

#### Symptom: Claude says MCP server tools aren't available

**Possible Causes & Solutions:**

1. **Configuration File Issues**
   ```
   Check: %APPDATA%\Claude\claude_desktop_config.json
   ```
   - **Invalid JSON:** Use a JSON validator to check syntax
   - **Wrong path:** Ensure path to `Embranch.exe` is correct with double backslashes
   - **Missing quotes:** All string values must be in quotes

2. **File Permissions**
   ```bash
   # Test if you can run the executable directly
   "C:\Program Files\VM-RAG-MCP\Embranch.exe" --help
   ```
   - **Access denied:** Run Claude as administrator or fix file permissions
   - **File not found:** Verify the executable exists at the specified path

3. **Missing Dependencies**
   ```bash
   # Check .NET installation
   dotnet --list-runtimes
   
   # Should show: Microsoft.NETCore.App 9.0.x
   ```
   - **No .NET 9.0:** Install from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/9.0)

4. **Python/ChromaDB Issues**
   ```bash
   # Test Python and ChromaDB
   python --version
   python -c "import chromadb; print('ChromaDB version:', chromadb.__version__)"
   ```
   - **Python not found:** Install Python 3.8+ from [python.org](https://python.org)
   - **ChromaDB not found:** Run `pip install chromadb`
   - **Version conflicts:** Try `pip install --upgrade chromadb`

---

#### Symptom: Server starts but crashes immediately

**Check Application Logs:**

If logging is enabled, check the log file:
```
Location: As specified in LOG_FILE_NAME environment variable
```

**Common Crash Causes:**

1. **Python Context Initialization Failed**
   ```
   Error: "Failed to initialize Python context"
   ```
   **Solution:**
   - Ensure Python is in your PATH
   - Verify ChromaDB installation: `pip install chromadb`
   - Try specifying Python path explicitly:
     ```json
     "env": {
       "PYTHONPATH": "C:\\Python311\\Lib\\site-packages"
     }
     ```

2. **Storage Directory Issues**
   ```
   Error: Access denied to storage path
   ```
   **Solution:**
   - Ensure directories exist and are writable
   - Check `CHROMA_DATA_PATH` and `DOLT_REPOSITORY_PATH` permissions
   - Try using absolute paths instead of relative paths

3. **Port Conflicts**
   ```
   Error: Port already in use
   ```
   **Solution:**
   ```json
   "env": {
     "MCP_PORT": "6501",
     "CHROMA_PORT": "8001"
   }
   ```

---

## Configuration Issues

### Storage Directory Problems

#### Symptom: "Permission denied" or "Access denied" errors

**Solutions:**

1. **Check Directory Permissions**
   ```bash
   # Test write access
   echo test > "./data/test.txt"
   del "./data/test.txt"
   ```

2. **Use Absolute Paths**
   ```json
   {
     "env": {
       "CHROMA_DATA_PATH": "C:\\MyProject\\Data\\ChromaDB",
       "DOLT_REPOSITORY_PATH": "C:\\MyProject\\Data\\DoltRepo"
     }
   }
   ```

3. **Create Directories Manually**
   ```bash
   mkdir "C:\MyProject\Data\ChromaDB"
   mkdir "C:\MyProject\Data\DoltRepo"
   ```

4. **Run Claude as Administrator**
   - Right-click Claude Desktop
   - Select "Run as administrator"

#### Symptom: Data disappears after restart

**Causes:**
- Using relative paths that resolve differently
- Temporary directories being cleaned up
- Multiple server instances with different data paths

**Solution:** Use absolute paths in configuration:
```json
{
  "env": {
    "CHROMA_DATA_PATH": "C:\\PermanentLocation\\ChromaDB",
    "DOLT_REPOSITORY_PATH": "C:\\PermanentLocation\\DoltRepo"
  }
}
```

---

### Dolt Configuration Issues

#### Symptom: "DOLT_EXECUTABLE_PATH not found" errors

**Solutions:**

1. **Install Dolt**
   - Download from [dolthub.com](https://www.dolthub.com/docs/getting-started/installation/)
   - Add to PATH or specify full path

2. **Specify Full Path**
   ```json
   {
     "env": {
       "DOLT_EXECUTABLE_PATH": "C:\\Program Files\\Dolt\\bin\\dolt.exe"
     }
   }
   ```

3. **Test Dolt Installation**
   ```bash
   "C:\Program Files\Dolt\bin\dolt.exe" version
   ```

#### Symptom: Authentication failed with DoltHub

**Solutions:**

1. **Login to Dolt**
   ```bash
   "C:\Program Files\Dolt\bin\dolt.exe" login
   ```

2. **Check Credentials**
   ```bash
   "C:\Program Files\Dolt\bin\dolt.exe" creds ls
   ```

3. **Verify Remote URL Format**
   - Correct: `myorg/my-repo`
   - Correct: `https://doltremoteapi.dolthub.com/myorg/my-repo`
   - Incorrect: `https://dolthub.com/myorg/my-repo` (this is web UI URL)

---

## Runtime Issues

### ChromaDB Operations Failing

#### Symptom: "Collection not found" errors

**Solutions:**

1. **List Available Collections**
   ```
   Ask Claude: "List my collections"
   ```

2. **Check Collection Names**
   - Collection names are case-sensitive
   - No spaces in names (use underscores)
   - Alphanumeric characters only

3. **Create Missing Collection**
   ```
   Ask Claude: "Create a collection named vmrag_main"
   ```

#### Symptom: Search returns no results

**Possible Causes:**

1. **Empty Collection**
   ```
   Ask Claude: "How many documents are in my collection?"
   ```

2. **Embedding Model Mismatch**
   - Documents added with one model won't match queries from another
   - Solution: Recreate collection with consistent embedding model

3. **Query Too Specific**
   ```
   Instead of: "exact phrase match"
   Try: "main concepts or keywords"
   ```

4. **Wrong Collection**
   ```
   Ask Claude: "Which collections contain documents about [topic]?"
   ```

---

### Version Control Issues

#### Symptom: "NOT_INITIALIZED" errors

**Solutions:**

1. **Initialize Repository**
   ```
   Ask Claude: "Initialize a new knowledge base"
   ```

2. **Clone Existing Repository**
   ```
   Ask Claude: "Clone the knowledge base from myorg/my-repo"
   ```

3. **Check Repository Status**
   ```
   Ask Claude: "What's the status of my repository?"
   ```

#### Symptom: "UNCOMMITTED_CHANGES" blocks operations

**Understanding the Issue:**
When you have uncommitted changes, certain operations (pull, checkout, reset) are blocked to prevent data loss.

**Solutions:**

1. **Commit Your Changes**
   ```
   Ask Claude: "Commit my changes with message 'Work in progress'"
   ```

2. **Discard Changes**
   ```
   Ask Claude: "Discard all my uncommitted changes"
   ```

3. **Stash Changes (via pull/checkout with parameters)**
   ```
   Ask Claude: "Pull the latest changes and commit my work first"
   ```

#### Symptom: "REMOTE_REJECTED" on push

**Causes:**
- Remote has commits you don't have locally
- Branch protection rules
- Authentication issues

**Solutions:**

1. **Pull First**
   ```
   Ask Claude: "Pull the latest changes, then push"
   ```

2. **Check Remote Status**
   ```
   Ask Claude: "Fetch updates and show me the status"
   ```

3. **Force Push (Dangerous)**
   ```
   Only if you're sure you want to overwrite remote:
   Ask Claude: "Force push my changes" 
   (Claude should warn you about this)
   ```

---

## Performance Issues

### Slow Operations

#### Symptom: Document operations take too long

**Causes & Solutions:**

1. **Large Collection Size**
   - **Problem:** Collections with >100,000 documents can be slow
   - **Solution:** Split into smaller, topic-specific collections

2. **Large Documents**
   - **Problem:** Very large documents (>50KB) slow down embedding
   - **Solution:** Break large documents into smaller sections

3. **Storage on Slow Disk**
   - **Problem:** Hard drives slower than SSDs
   - **Solution:** Move data directories to SSD

4. **Insufficient RAM**
   - **Problem:** ChromaDB loads collections into memory
   - **Solution:** 
     - Reduce collection sizes
     - Increase system RAM
     - Use client mode instead of persistent mode

#### Symptom: Branch switching takes forever

**Causes:**
- Large number of documents need re-embedding
- Slow disk I/O
- Network issues if using client mode

**Solutions:**

1. **Use Smaller Collections**
2. **Switch to SSD Storage**
3. **Enable Verbose Logging** to see what's happening:
   ```json
   {
     "env": {
       "ENABLE_LOGGING": "true",
       "LOG_LEVEL": "Debug"
     }
   }
   ```

---

## Data Integrity Issues

### Missing Documents After Operations

#### Symptom: Documents disappear after branch switch

**This is Normal Behavior:**
- Different branches contain different document sets
- Switching branches updates ChromaDB to match that branch's content

**To Verify:**
1. **Check Current Branch**
   ```
   Ask Claude: "What branch am I on and what's the status?"
   ```

2. **List Documents in Different Branches**
   ```
   Ask Claude: "Switch to main branch and show me the document count"
   Ask Claude: "Switch to feature branch and show me the document count"
   ```

3. **View Commit History**
   ```
   Ask Claude: "Show me the recent commits to understand what changed"
   ```

#### Symptom: Documents lost after reset/pull

**Possible Causes:**

1. **Reset Discarded Uncommitted Changes**
   - **Prevention:** Always commit before resetting
   - **Recovery:** Check if documents are in version history

2. **Pull Brought in Different Version**
   - **Check:** Compare commits before and after pull
   - **Recovery:** Checkout previous commit if needed

3. **Merge Conflict Resolution**
   - **Check:** Look for conflict resolution in recent commits
   - **Recovery:** May need manual recovery from backups

---

## Diagnostic Commands

### System Information Gathering

When reporting issues, gather this information:

```bash
# .NET version
dotnet --info

# Python version
python --version

# ChromaDB version  
python -c "import chromadb; print(chromadb.__version__)"

# Dolt version
"C:\Program Files\Dolt\bin\dolt.exe" version

# Server version (ask Claude)
"What version of the VM RAG server is running?"

# Configuration check
type "%APPDATA%\Claude\claude_desktop_config.json"
```

### Log Analysis

Enable detailed logging for troubleshooting:

```json
{
  "env": {
    "ENABLE_LOGGING": "true",
    "LOG_LEVEL": "Debug",
    "LOG_FILE_NAME": "./logs/vm-rag-debug.log"
  }
}
```

**Key log patterns to look for:**

- `Failed to initialize Python context` → Python/ChromaDB issues
- `Access denied` → Permission problems
- `Collection not found` → ChromaDB issues
- `Command not found` → Dolt installation issues
- `Authentication failed` → DoltHub credential issues

---

## Recovery Procedures

### Complete Reset

If everything is broken and you want to start fresh:

1. **Stop Claude Desktop**
2. **Remove Data Directories**
   ```bash
   rd /s /q "./data"
   rd /s /q "./chroma_data"  
   ```
3. **Restart Claude Desktop**
4. **Re-initialize**
   ```
   Ask Claude: "Initialize a new knowledge base"
   ```

### Backup and Restore

#### Creating Backups

1. **Data Directories** (copy these folders):
   - ChromaDB data: `CHROMA_DATA_PATH` folder
   - Dolt repository: `DOLT_REPOSITORY_PATH` folder

2. **DoltHub Sync** (automatic backup):
   ```
   Ask Claude: "Push all my changes to DoltHub"
   ```

#### Restoring from Backup

1. **From Directory Backup:**
   - Stop Claude Desktop
   - Replace data directories with backup copies
   - Restart Claude Desktop

2. **From DoltHub:**
   ```
   Ask Claude: "Clone the knowledge base from myorg/my-repo"
   ```

---

## Getting Additional Help

### Before Reporting Issues

1. ✅ Check this troubleshooting guide
2. ✅ Verify your configuration matches the examples
3. ✅ Test with a fresh data directory
4. ✅ Gather diagnostic information (versions, logs, config)

### Reporting Issues

Include this information:

- **Problem description:** What you were trying to do and what happened
- **Error messages:** Exact text of any error messages
- **Configuration:** Your `claude_desktop_config.json` (remove sensitive info)
- **Environment:** OS version, .NET version, Python version, ChromaDB version
- **Steps to reproduce:** Minimal steps to recreate the problem
- **Log files:** Relevant portions of debug logs

### Community Resources

- **Documentation:** This guide and other documentation files
- **GitHub Issues:** Project repository issue tracker
- **DoltHub Docs:** [docs.dolthub.com](https://docs.dolthub.com) for Dolt-specific issues
- **ChromaDB Docs:** [docs.trychroma.com](https://docs.trychroma.com) for ChromaDB issues

---

## Common "It's Working Now" Solutions

Sometimes these simple steps fix mysterious issues:

1. **Restart Claude Desktop** (completely quit and restart)
2. **Restart your computer** (clears memory and temporary issues)
3. **Update dependencies** (`pip install --upgrade chromadb`)
4. **Clear temporary files** (delete and recreate data directories)
5. **Run as administrator** (fixes permission issues)
6. **Use absolute paths** (resolves path confusion)
7. **Check for Windows updates** (can fix .NET issues)
8. **Reinstall .NET 9.0 Runtime** (fixes corrupted installations)

---

*If you're still having issues after trying these solutions, please report the problem with as much detail as possible including error messages, configuration, and steps to reproduce.*