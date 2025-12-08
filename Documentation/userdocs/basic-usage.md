# Basic Usage of DMMS

Once you have DMMS configured in Claude Desktop, you can start using its tools to manage both Dolt databases and Chroma vector databases.

## Available Tools

DMMS provides the following tools through the MCP interface:

### Server Management
- **GetServerVersion**: Returns the current version of the DMMS server

### Chroma Vector Database Tools
- **chroma_list_collections**: List all Chroma collections
- **chroma_create_collection**: Create new collections for documents
- **chroma_delete_collection**: Remove collections and their data
- **chroma_add_documents**: Store documents with metadata
- **chroma_query_documents**: Search documents by content
- **chroma_get_collection_count**: Get document counts
- **chroma_delete_documents**: Remove specific documents

### Future Dolt Tools (To be implemented)
- **ExecuteDoltCommand**: Execute Dolt CLI commands
- **QueryDatabase**: Run SQL queries on Dolt databases
- **ManageDatabase**: Create, clone, and manage Dolt databases
- **BranchOperations**: Manage Dolt branches
- **CommitOperations**: Create and manage commits

## Basic Workflow Examples

### 1. Checking Server Status

Ask Claude:
```
"Can you check if the DMMS server is running and what version it is?"
```

Claude will use the GetServerVersion tool to provide this information.

### 2. Working with Chroma Collections

**List existing collections:**
```
"Show me all my Chroma collections"
"What vector databases do I have?"
```

**Create a new collection:**
```
"Create a new Chroma collection called 'research_papers'"
"Set up a collection named 'meeting_notes' for storing company documents"
```

**Add documents to a collection:**
```
"Add this document to my 'research_papers' collection:
- ID: paper_ai_2024
- Content: 'Artificial Intelligence in Modern Healthcare Systems'
- Metadata: {'category': 'AI', 'year': 2024, 'author': 'Dr. Smith'}"
```

**Search for documents:**
```
"Search my 'research_papers' collection for documents about 'machine learning'"
"Find the 5 most relevant documents about 'neural networks' in my AI collection"
```

**Get collection statistics:**
```
"How many documents are in my 'meeting_notes' collection?"
"Show me document counts for all my collections"
```

### 3. Document Management Workflows

**Organizing research documents:**
```
1. "Create a collection called 'AI_Research'"
2. "Add these papers to 'AI_Research': [list of papers with metadata]"
3. "Search for papers about 'transformer models'"
4. "Get count of papers by year using metadata filters"
```

**Managing meeting notes:**
```
1. "Create a collection named 'company_meetings'"
2. "Store this meeting transcript with metadata about date, participants, topics"
3. "Find all meetings from last month that discussed budget"
4. "Delete outdated meeting notes from 2023"
```

### 4. Future Dolt Database Operations

Once additional tools are implemented, you'll be able to:

**Create a new database:**
```
"Create a new Dolt database called 'my_project'"
```

**Clone an existing database:**
```
"Clone the database from dolthub.com/myorg/mydb"
```

**Query data:**
```
"Show me all tables in the current database"
"Select the first 10 rows from the users table"
```

## Working with Multiple Databases

DMMS supports both Chroma vector databases and traditional Dolt databases:

### Chroma Collections
1. **Multiple collections**: Store different document types in separate collections
2. **Collection organization**: Use descriptive names like 'research_papers', 'meeting_notes'
3. **Cross-collection search**: Search across multiple collections when needed

### Future Dolt Integration
1. **Switch between databases**: Specify which database to work with
2. **Compare databases**: View differences between databases
3. **Sync databases**: Keep multiple databases in sync

## Best Practices

### 1. Always Verify Connection
Before starting work, verify the DMMS connection:
```
"Is DMMS connected and ready?"
"What version of DMMS is running?"
```

### 2. Use Descriptive Requests
Be specific about what you want to accomplish:

**Chroma Operations:**
- ✅ Good: "Create a collection named 'research_papers' for storing AI research documents"
- ❌ Vague: "Make a collection"

**Document Storage:**
- ✅ Good: "Add this document with ID 'meeting_2024_01_15' and metadata including date, participants, and topics"
- ❌ Vague: "Store this text"

**Search Operations:**
- ✅ Good: "Search my 'technical_docs' collection for documents about 'API authentication' from 2024"
- ❌ Vague: "Find stuff about APIs"

### 3. Use Consistent Naming Conventions
Establish patterns for collection and document naming:

**Collection Names:**
- Use underscores: `meeting_notes`, `research_papers`
- Be descriptive: `customer_feedback`, `product_documentation`
- Include purpose: `training_materials`, `legal_documents`

**Document IDs:**
- Include dates: `doc_2024_01_15_meeting`
- Use categories: `research_ai_ethics_2024`
- Be unique and meaningful: `contract_acme_corp_renewal_2024`

### 4. Organize with Metadata
Use consistent metadata schemas for better organization:

```json
{
  "category": "meeting|research|documentation|legal",
  "date": "2024-01-15",
  "author": "john.doe@company.com", 
  "tags": ["quarterly-review", "budget", "planning"],
  "priority": "high|medium|low",
  "status": "draft|review|final"
}
```

### 5. Check Operation Results
After performing operations, verify the results:
```
"Did the last operation complete successfully? Show me the document count for my collection."
"List my collections to confirm the new one was created"
```

## Understanding MCP Tools

MCP (Model Context Protocol) tools are functions that Claude can execute on your behalf. When you ask Claude to perform database operations, it:

1. **Interprets your request**: Understands what you want to accomplish
2. **Selects the appropriate DMMS tool**: Chooses from Chroma or future Dolt tools
3. **Executes the tool with proper parameters**: Calls the tool with correct arguments
4. **Returns the results to you**: Provides formatted output and status

### Tool Categories in DMMS:

**Server Tools:**
- `get_server_version` - Check DMMS status and version

**Chroma Collection Tools:**
- `chroma_list_collections` - List all collections
- `chroma_create_collection` - Create new collections
- `chroma_delete_collection` - Remove collections

**Chroma Document Tools:**
- `chroma_add_documents` - Store documents with metadata
- `chroma_query_documents` - Search document content
- `chroma_get_collection_count` - Get document counts
- `chroma_delete_documents` - Remove specific documents

## Error Handling

If you encounter errors:

### Connection Errors
Claude will inform you if DMMS is not responding:
```
"I cannot connect to the DMMS server. Please check your configuration."
```

**Solutions:**
- Verify DMMS is running
- Check Claude Desktop configuration
- Restart Claude Desktop

### Chroma Operation Errors
Common Chroma errors and their meanings:

**"Collection does not exist"**
- Solution: Create the collection first or check spelling

**"Document ID already exists"** 
- Solution: Use unique IDs or delete existing document

**"Invalid parameters"**
- Solution: Check required fields (collection_name, document content, etc.)

**Permission Errors**
- Ensure DMMS has read/write access to Chroma data directory
- Check file permissions on Windows

### Error Recovery
When operations fail:
1. Check the specific error message
2. Verify your input parameters
3. Try a simpler version of the operation
4. Check the troubleshooting guide

## Getting Help

### Available Tools
Ask Claude to show what's available:
```
"What DMMS tools are available for working with vector databases?"
"Show me the Chroma tools I can use"
```

### Request Examples
Get specific usage examples:
```
"Show me an example of adding documents to a Chroma collection"
"How do I search for documents with metadata filters?"
"What's the format for document IDs and metadata?"
```

### Documentation References
- **Detailed Tool Reference**: [Chroma Tools Reference](chroma-tools-reference.md)
- **Configuration Help**: [Chroma Configuration](chroma-configuration.md)  
- **Problem Solving**: [Troubleshooting Guide](troubleshooting.md)

## Next Steps

### For Chroma Vector Databases:
1. **Learn tool details**: Read the [Chroma Tools Reference](chroma-tools-reference.md)
2. **Configure storage**: Set up [Chroma Configuration](chroma-configuration.md)
3. **Plan your schema**: Design metadata structures for your documents

### For Future Dolt Integration:
- Stay tuned for Dolt database management tools
- Learn about version-controlled databases
- Plan data workflows combining vector and relational data

### Advanced Usage:
- Combine Chroma collections with structured data
- Set up automated document ingestion workflows
- Implement backup and recovery strategies