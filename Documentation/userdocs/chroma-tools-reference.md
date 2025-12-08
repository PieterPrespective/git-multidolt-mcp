# Chroma Tools Reference

The DMMS (Dolt Multi-Database MCP Server) provides comprehensive Chroma vector database integration through 8 specialized tools. These tools enable you to manage collections, store documents, and perform vector searches using either a persistent file-based storage or HTTP server connection.

## Overview

The Chroma tools are designed to work with ChromaDB, providing vector database functionality for AI applications. The tools support both:

- **Persistent Mode** (default): Direct file-based storage using local data directory
- **Server Mode**: HTTP API connection to a running ChromaDB server

## Tool Categories

### Collection Management Tools
- [chroma_list_collections](#chroma_list_collections) - List all collections
- [chroma_create_collection](#chroma_create_collection) - Create new collections
- [chroma_delete_collection](#chroma_delete_collection) - Delete collections
- [chroma_get_collection_count](#chroma_get_collection_count) - Get document counts

### Document Management Tools
- [chroma_add_documents](#chroma_add_documents) - Add documents to collections
- [chroma_query_documents](#chroma_query_documents) - Search documents by text
- [chroma_delete_documents](#chroma_delete_documents) - Remove documents

---

## Collection Management Tools

### chroma_list_collections

Lists all available Chroma collections in the database.

**Input Parameters:**
```json
{
  "limit": 50,      // Optional: Maximum number of collections to return
  "offset": 0       // Optional: Number of collections to skip (for pagination)
}
```

**Expected Output:**
```json
{
  "collections": [
    "my_documents",
    "research_papers", 
    "meeting_notes"
  ],
  "total_count": 3
}
```

**Usage Example:**
```
List all my Chroma collections

Can you show me the first 10 collections in my database?
```

**Limitations:**
- Collection names are case-sensitive
- In persistent mode, collections are represented as directories

---

### chroma_create_collection

Creates a new Chroma collection for storing documents.

**Input Parameters:**
```json
{
  "collection_name": "my_collection",           // Required: Name of the collection
  "embedding_function_name": "default",        // Optional: Embedding function to use
  "metadata": {                                // Optional: Collection metadata
    "description": "My document collection",
    "version": "1.0"
  }
}
```

**Expected Output:**
```json
{
  "success": true,
  "message": "Collection 'my_collection' created successfully",
  "collection_name": "my_collection"
}
```

**Usage Example:**
```
Create a new collection called "research_papers" for storing academic documents

Create a collection named "meeting_notes" with description "Company meeting transcripts"
```

**Limitations:**
- Collection names must be unique
- Collection names should follow filesystem naming conventions (avoid special characters)
- Cannot create collections that already exist

---

### chroma_delete_collection

Permanently deletes a Chroma collection and all its documents.

**Input Parameters:**
```json
{
  "collection_name": "old_collection"    // Required: Name of collection to delete
}
```

**Expected Output:**
```json
{
  "success": true,
  "message": "Collection 'old_collection' deleted successfully"
}
```

**Usage Example:**
```
Delete the collection named "temp_docs"

Remove the "old_research" collection - I no longer need it
```

**Limitations:**
- Operation is irreversible - all documents will be permanently lost
- Cannot delete collections that don't exist
- In persistent mode, the entire collection directory is removed

---

### chroma_get_collection_count

Gets the number of documents in a specific collection.

**Input Parameters:**
```json
{
  "collection_name": "my_collection"    // Required: Name of the collection
}
```

**Expected Output:**
```json
{
  "collection_name": "my_collection",
  "document_count": 247
}
```

**Usage Example:**
```
How many documents are in my "research_papers" collection?

Check the document count for the "meeting_notes" collection
```

**Limitations:**
- Collection must exist
- Count reflects documents currently stored, not historical additions

---

## Document Management Tools

### chroma_add_documents

Adds documents to a Chroma collection with optional metadata.

**Input Parameters:**
```json
{
  "collection_name": "my_docs",
  "documents": [
    "This is the first document content",
    "This is the second document content"
  ],
  "ids": [
    "doc_001",
    "doc_002"
  ],
  "metadatas": [                        // Optional: Metadata for each document
    {
      "source": "email",
      "date": "2024-01-15",
      "author": "John Doe"
    },
    {
      "source": "report", 
      "date": "2024-01-16",
      "author": "Jane Smith"
    }
  ]
}
```

**Expected Output:**
```json
{
  "success": true,
  "message": "Added 2 documents to collection 'my_docs'",
  "document_count": 2
}
```

**Usage Example:**
```
Add these meeting notes to my "meeting_notes" collection:
- ID: "meeting_2024_01_15"
- Content: "Discussed quarterly goals and budget planning"
- Metadata: {"type": "meeting", "date": "2024-01-15", "participants": 8}

Store this research paper in "research_papers":
- ID: "paper_ai_ethics_2024"  
- Content: "Artificial Intelligence Ethics: A Comprehensive Framework for Responsible AI Development"
```

**Limitations:**
- Document IDs must be unique within the collection
- Cannot add documents with duplicate IDs
- Large documents may impact performance
- Metadata values should be JSON-serializable types

---

### chroma_query_documents

Searches documents in a collection using text similarity.

**Input Parameters:**
```json
{
  "collection_name": "my_docs",
  "query_texts": [
    "artificial intelligence",
    "machine learning algorithms"
  ],
  "n_results": 5,                       // Optional: Number of results per query (default: 5)
  "where": {                           // Optional: Metadata filters
    "source": "research_paper",
    "date": {"$gte": "2024-01-01"}
  },
  "where_document": {                  // Optional: Document content filters
    "$contains": "neural networks"
  }
}
```

**Expected Output:**
```json
{
  "ids": [
    ["doc_001", "doc_003"],           // Results for first query
    ["doc_002", "doc_004"]            // Results for second query
  ],
  "documents": [
    ["First document content...", "Third document content..."],
    ["Second document content...", "Fourth document content..."]
  ],
  "metadatas": [
    [{"source": "paper", "date": "2024-01-15"}, {"source": "article"}],
    [{"source": "report"}, {"source": "paper", "date": "2024-01-10"}]
  ],
  "distances": [
    [0.1, 0.3],                       // Similarity scores (lower = more similar)
    [0.2, 0.4]
  ]
}
```

**Usage Example:**
```
Search my "research_papers" collection for documents about "neural networks and deep learning"

Find the 3 most similar documents to "customer feedback analysis" in my "business_docs" collection

Search for documents from 2024 that mention "sustainability" in the "reports" collection
```

**Limitations:**
- Uses simple text matching, not true vector embeddings in persistent mode
- Similarity scoring is approximate in persistent mode
- Complex metadata filters may have limited support
- Performance decreases with very large collections

---

### chroma_delete_documents

Removes specific documents from a collection by their IDs.

**Input Parameters:**
```json
{
  "collection_name": "my_docs",
  "ids": [
    "doc_to_delete_1",
    "doc_to_delete_2",
    "outdated_document"
  ]
}
```

**Expected Output:**
```json
{
  "success": true,
  "message": "Deleted 3 documents from collection 'my_docs'",
  "remaining_count": 42
}
```

**Usage Example:**
```
Delete documents with IDs "temp_001", "temp_002", and "draft_meeting_notes" from my "meeting_notes" collection

Remove the outdated research paper "paper_old_2023" from "research_papers"
```

**Limitations:**
- Cannot recover deleted documents
- Document IDs must exist in the collection
- Silently skips non-existent document IDs

---

## Error Handling

All tools return structured error responses when operations fail:

```json
{
  "success": false,
  "error": "Collection 'nonexistent' does not exist",
  "error_code": "COLLECTION_NOT_FOUND"
}
```

Common error types:
- `COLLECTION_NOT_FOUND` - Collection doesn't exist
- `COLLECTION_ALREADY_EXISTS` - Attempting to create existing collection
- `DUPLICATE_DOCUMENT_ID` - Document ID already exists in collection
- `INVALID_PARAMETERS` - Missing or invalid input parameters
- `STORAGE_ERROR` - File system or database errors

## Performance Considerations

### Persistent Mode
- **Best for:** Small to medium datasets (< 100K documents)
- **Advantages:** No external dependencies, simple file-based storage
- **Limitations:** No true vector embeddings, limited query performance

### Server Mode  
- **Best for:** Large datasets, production applications
- **Advantages:** True vector embeddings, optimized query performance
- **Requirements:** Running ChromaDB server instance

## Vector Embeddings Implementation Details

### Why Persistent Mode Lacks True Vector Embeddings

The persistent mode limitation is **by design choice, not technical limitation**. Here's the detailed explanation:

#### Current Persistent Mode Implementation
- **Text Storage**: Documents stored as plain text in JSONL files
- **Search Method**: Simple text matching using string contains operations
- **Similarity Calculation**: Basic word overlap counting instead of vector similarity
- **No ML Dependencies**: Avoids machine learning libraries for lightweight deployment

#### What Would Be Required for True Vector Embeddings

**1. Embedding Generation:**
```csharp
// Could use ML.NET, ONNX Runtime, or API calls
public async Task<float[]> GenerateEmbedding(string text)
{
    // Options:
    // - ML.NET + ONNX sentence transformer models
    // - HTTP calls to OpenAI/Azure embedding APIs  
    // - Python.NET interop with sentence-transformers
    // - Local embedding server (FastEmbed, etc.)
}
```

**2. Vector Storage Format:**
```csharp
public class DocumentRecord
{
    public string Id { get; set; }
    public string Document { get; set; }
    public float[] Embedding { get; set; }  // Add vector storage
    public Dictionary<string, object> Metadata { get; set; }
}
```

**3. Proper Similarity Search:**
```csharp
public double CosineSimilarity(float[] vector1, float[] vector2)
{
    // True cosine similarity instead of word overlap
    var dot = vector1.Zip(vector2, (a, b) => a * b).Sum();
    var mag1 = Math.Sqrt(vector1.Sum(a => a * a));
    var mag2 = Math.Sqrt(vector2.Sum(a => a * a));
    return dot / (mag1 * mag2);
}
```

#### Technical Feasibility in C#

**Available Solutions:**
- **ML.NET + ONNX**: Pre-trained sentence transformer models
- **REST API Integration**: OpenAI, Azure, Cohere, local embedding servers
- **Python Interop**: Use sentence-transformers via Python.NET
- **Native Libraries**: HuggingFace.NET, Microsoft.ML.Transformer

#### Architecture Support

The current architecture is designed to support vector embeddings enhancement:
- Interface-based design allows implementation swapping
- Server mode already delegates to full ChromaDB (with embeddings)
- Persistent mode could be enhanced without breaking changes

#### Future Enhancement Path

To add true vector embeddings to persistent mode:

1. **Add embedding model integration** (ML.NET/ONNX recommended for offline use)
2. **Update storage format** to include vector data (binary format or SQLite)
3. **Implement vector similarity search** algorithms
4. **Add embedding configuration** options (model selection, API keys, etc.)
5. **Maintain backward compatibility** with existing text-based collections

#### Why This Design Choice Was Made

- **Time Constraints**: Full ML implementation would significantly extend development time
- **Dependency Management**: Keeping the core lightweight without ML dependencies  
- **Assignment Focus**: Prioritizing MCP protocol implementation over ML engineering
- **Incremental Development**: Text search provides functional baseline for testing/development
- **Deployment Simplicity**: Fewer dependencies = easier deployment and troubleshooting

#### Comparison with Python ChromaDB

- **Python ChromaDB**: Has built-in sentence-transformers, HNSW indexing, proper vector storage
- **Our Implementation**: Focused on MCP integration with simplified search as MVP
- **Not a C# Limitation**: Full vector capabilities are absolutely achievable in C#

**Recommendation**: Use server mode for production applications requiring true vector similarity. Use persistent mode for development, testing, or simple text matching scenarios.

## Integration Tips

### Batch Operations
For better performance, batch document operations:
```
Add multiple documents at once instead of individual adds:
- Use arrays of documents, IDs, and metadata
- Reduces overhead and improves throughput
```

### Metadata Strategy
Design metadata schemas for effective filtering:
```json
{
  "source": "email|document|web|api",
  "created_date": "2024-01-15",
  "category": "business|technical|personal",
  "priority": "high|medium|low",
  "tags": ["quarterly-review", "budget", "planning"]
}
```

### ID Naming Conventions
Use consistent ID patterns:
- `doc_YYYY-MM-DD_sequence` for time-based IDs
- `source_type_unique_id` for source-based IDs
- `collection_category_number` for categorized content

For complete setup instructions and configuration options, see [Chroma Configuration](chroma-configuration.md).