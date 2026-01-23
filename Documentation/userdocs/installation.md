# Installation Guide

## Prerequisites

Before installing the VM RAG MCP Server, ensure you have the following installed on your Windows system:

1. **.NET 9.0 Runtime or SDK**  
   Download from: [https://dotnet.microsoft.com/download/dotnet/9.0](https://dotnet.microsoft.com/download/dotnet/9.0)
   
2. **Python 3.8+ with ChromaDB**
   - Install Python from: [https://python.org](https://python.org)
   - Install ChromaDB: `pip install chromadb`
   
3. **Dolt** (Required for version control features)  
   Download from: [https://www.dolthub.com/docs/getting-started/installation/](https://www.dolthub.com/docs/getting-started/installation/)

4. **Claude Desktop Application**  
   Download from: [https://claude.ai/download](https://claude.ai/download)

## Download Options

### Option 1: Using the Pre-built Executable

1. Download the latest VM RAG MCP Server release from the releases page
2. Extract the ZIP file to a location of your choice (e.g., `C:\Program Files\VM-RAG-MCP`)
3. Note the full path to `Embranch.exe` for configuration

### Option 2: Building from Source

1. Clone the repository:
   ```bash
   git clone https://github.com/yourusername/vm-rag-mcp.git
   cd vm-rag-mcp
   ```

2. Build the project:
   ```bash
   dotnet build multidolt-mcp/Embranch.csproj -c Release
   ```

3. The executable will be located at:
   ```
   multidolt-mcp\bin\Release\net9.0\Embranch.exe
   ```

## Verifying Installation

### Step 1: Verify .NET Installation

```bash
dotnet --list-runtimes
```

You should see Microsoft.NETCore.App 9.0.x in the list.

### Step 2: Verify Python and ChromaDB

```bash
python --version
pip show chromadb
```

If ChromaDB is not installed:
```bash
pip install chromadb
```

### Step 3: Verify Dolt (Optional)

```bash
"C:\Program Files\Dolt\bin\dolt.exe" version
```

### Step 4: Test the Server

Open a Command Prompt or PowerShell and run:

```bash
"C:\Path\To\Embranch.exe" --help
```

You should see help information for the VM RAG MCP Server.

## Next Steps

- [Configuration Guide](configuration.md) - Setting up Claude Desktop and environment variables
- [Basic Usage Guide](basic-usage.md) - Essential workflows and daily usage
- [Tools Reference](tools-reference.md) - Complete guide to all available tools
- [Troubleshooting](troubleshooting.md) - Solutions to common problems