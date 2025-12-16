# MCP Tools Specification for VM RAG Server

**Document Version**: 1.1  
**Date**: December 15, 2025  
**Purpose**: Comprehensive MCP tool definitions for LLM interaction with version-controlled ChromaDB

---

## Overview

This document specifies all MCP tools exposed by the VM RAG Server. The tools are divided into two categories:

- **ChromaDB Tools (`chroma_*`)** = Document read/write operations (the LLM's primary interface)
- **Dolt Tools (`dolt_*`)** = Version control operations (branching, commits, sync)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              LLM INTERACTION MODEL                           │
│                                                                             │
│   ┌─────────────────┐                           ┌─────────────────────┐    │
│   │      LLM        │                           │   MCP Server        │    │
│   │                 │                           │                     │    │
│   │  Document Ops   │───── chroma_* tools ─────►│   ChromaDB          │    │
│   │  (read/write)   │                           │   (working copy)    │    │
│   │                 │                           │                     │    │
│   │  Version Ctrl   │───── dolt_* tools ───────►│   Dolt CLI          │    │
│   │  (branching,    │                           │   (version control) │    │
│   │   commits, etc) │                           │                     │    │
│   └─────────────────┘                           └─────────────────────┘    │
│                                                                             │
│   KEY PRINCIPLE: LLM uses chroma_* tools for all document operations.       │
│   Changes made via chroma_* are tracked as "local changes" until committed  │
│   with dolt_commit. Dolt tools manage versioning, branching, and sync.      │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Tool Naming Conventions

### ChromaDB Tools (Document Operations)

| Category | Tools |
|----------|-------|
| Collection Info | `chroma_list_collections`, `chroma_get_collection_info`, `chroma_get_collection_count`, `chroma_peek_collection` |
| Collection Management | `chroma_create_collection`, `chroma_modify_collection`, `chroma_delete_collection` |
| Document Operations | `chroma_add_documents`, `chroma_query_documents`, `chroma_get_documents`, `chroma_update_documents`, `chroma_delete_documents` |

### Dolt Tools (Version Control)

| Category | Tools |
|----------|-------|
| Information | `dolt_status`, `dolt_branches`, `dolt_commits`, `dolt_show`, `dolt_find` |
| Setup | `dolt_init`, `dolt_clone` |
| Sync | `dolt_fetch`, `dolt_pull`, `dolt_push` |
| Local Operations | `dolt_commit`, `dolt_checkout`, `dolt_reset` |

---

# Part 1: ChromaDB Tools (Document Operations)

These tools provide the LLM's primary interface for reading and writing documents. All changes made through these tools are tracked as "local changes" in the version control system until committed with `dolt_commit`.

## Collection Information Tools

### 1. chroma_list_collections

**Purpose**: List all collections in the ChromaDB database

**When to Use**:
- To discover what collections exist
- To find the right collection to query
- To get an overview of the knowledge base structure

```typescript
// Tool Definition
{
  name: "chroma_list_collections",
  description: "List all collections in the ChromaDB database with pagination support. Returns collection names and basic metadata.",
  inputSchema: {
    type: "object",
    properties: {
      limit: {
        type: "number",
        description: "Maximum number of collections to return. Default: 100",
        default: 100
      },
      offset: {
        type: "number",
        description: "Number of collections to skip (for pagination). Default: 0",
        default: 0
      }
    },
    required: []
  }
}
```

**Output Schema**:
```json
{
  "collections": [
    {
      "name": "string - Collection name",
      "metadata": "object - Collection metadata (if any)",
      "tenant": "string - Tenant name",
      "database": "string - Database name"
    }
  ],
  "total_count": "number - Total collections in database",
  "has_more": "boolean - Whether more collections exist beyond limit"
}
```

**Example Usage**:
```
User: "What knowledge bases do we have?"
LLM: [calls chroma_list_collections]

Response:
{
  "collections": [
    {
      "name": "vmrag_main",
      "metadata": {
        "description": "Main knowledge base",
        "embedding_model": "text-embedding-3-small"
      }
    },
    {
      "name": "vmrag_feature_api_docs",
      "metadata": {
        "description": "API documentation branch"
      }
    }
  ],
  "total_count": 2,
  "has_more": false
}
```

---

### 2. chroma_get_collection_info

**Purpose**: Get detailed information about a specific collection

**When to Use**:
- To understand a collection's configuration
- To check embedding model settings
- To verify collection exists before operations

```typescript
{
  name: "chroma_get_collection_info",
  description: "Get detailed information about a specific collection including its configuration, metadata, and embedding function settings.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Name of the collection to get information about"
      }
    },
    required: ["collection_name"]
  }
}
```

**Output Schema**:
```json
{
  "name": "string - Collection name",
  "id": "string - Collection UUID",
  "metadata": {
    "description": "string - Collection description",
    "embedding_model": "string - Model used for embeddings",
    "hnsw:space": "string - Distance metric (l2, ip, cosine)",
    "hnsw:M": "number - HNSW M parameter",
    "hnsw:construction_ef": "number - HNSW construction EF",
    "hnsw:search_ef": "number - HNSW search EF"
  },
  "tenant": "string - Tenant name",
  "database": "string - Database name",
  "document_count": "number - Number of documents in collection"
}
```

**Example Usage**:
```
User: "Tell me about the main knowledge base"
LLM: [calls chroma_get_collection_info with collection_name="vmrag_main"]
```

**Error Cases**:
- `COLLECTION_NOT_FOUND`: Collection does not exist.

---

### 3. chroma_get_collection_count

**Purpose**: Get the number of documents in a collection

**When to Use**:
- Quick check of collection size
- Before bulk operations to estimate scope
- To verify documents were added

```typescript
{
  name: "chroma_get_collection_count",
  description: "Get the total number of documents in a collection. This is faster than retrieving all documents when you only need the count.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Name of the collection to count documents in"
      }
    },
    required: ["collection_name"]
  }
}
```

**Output Schema**:
```json
{
  "collection_name": "string",
  "count": "number - Total documents in collection"
}
```

**Example Usage**:
```
User: "How many documents are in our knowledge base?"
LLM: [calls chroma_get_collection_count with collection_name="vmrag_main"]

Response:
{
  "collection_name": "vmrag_main",
  "count": 1547
}
```

---

### 4. chroma_peek_collection

**Purpose**: View a sample of documents from a collection

**When to Use**:
- To quickly see what's in a collection
- To understand document structure and metadata
- For debugging or exploration

```typescript
{
  name: "chroma_peek_collection",
  description: "View a sample of documents from a collection. Useful for quickly understanding what kind of content is stored without querying.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Name of the collection to peek into"
      },
      limit: {
        type: "number",
        description: "Number of documents to return. Default: 5, Max: 20",
        default: 5,
        maximum: 20
      }
    },
    required: ["collection_name"]
  }
}
```

**Output Schema**:
```json
{
  "collection_name": "string",
  "documents": [
    {
      "id": "string - Document ID",
      "document": "string - Document content (may be truncated)",
      "metadata": "object - Document metadata"
    }
  ],
  "total_in_collection": "number - Total documents in collection"
}
```

**Example Usage**:
```
User: "Show me some examples from the knowledge base"
LLM: [calls chroma_peek_collection with collection_name="vmrag_main", limit=3]
```

---

## Collection Management Tools

### 5. chroma_create_collection

**Purpose**: Create a new collection with optional configuration

**When to Use**:
- Setting up a new knowledge base
- Creating a collection for a specific purpose
- Usually called by `dolt_init` or `dolt_clone` internally

```typescript
{
  name: "chroma_create_collection",
  description: "Create a new ChromaDB collection. Collections store documents with embeddings for semantic search. Configure HNSW parameters for performance tuning.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Name for the new collection. Use alphanumeric characters and underscores."
      },
      metadata: {
        type: "object",
        description: "Optional metadata for the collection (description, settings, etc.)",
        properties: {
          description: { type: "string" },
          "hnsw:space": { 
            type: "string", 
            enum: ["l2", "ip", "cosine"],
            description: "Distance metric. Default: 'l2'"
          },
          "hnsw:M": { 
            type: "number",
            description: "HNSW M parameter (connections per node). Default: 16"
          },
          "hnsw:construction_ef": {
            type: "number",
            description: "HNSW construction EF. Default: 100"
          },
          "hnsw:search_ef": {
            type: "number",
            description: "HNSW search EF. Default: 10"
          }
        }
      },
      embedding_function: {
        type: "string",
        description: "Embedding function to use: 'default', 'openai', 'cohere', 'huggingface'. Default: 'default'",
        default: "default"
      },
      get_or_create: {
        type: "boolean",
        description: "If true, returns existing collection if it exists. Default: false",
        default: false
      }
    },
    required: ["collection_name"]
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "collection": {
    "name": "string - Collection name",
    "id": "string - Collection UUID",
    "created": "boolean - True if newly created, false if existing (get_or_create)"
  },
  "message": "string - Human-readable status"
}
```

**Example Usage**:
```
User: "Create a new collection for API documentation"
LLM: [calls chroma_create_collection with collection_name="api_docs", 
      metadata={description: "REST API documentation", "hnsw:space": "cosine"}]
```

**Error Cases**:
- `COLLECTION_EXISTS`: Collection already exists (when get_or_create=false).
- `INVALID_NAME`: Collection name contains invalid characters.

---

### 6. chroma_modify_collection

**Purpose**: Update a collection's name or metadata

**When to Use**:
- Renaming a collection
- Updating collection description
- Changing collection settings

```typescript
{
  name: "chroma_modify_collection",
  description: "Update a collection's name or metadata. Note: Changing HNSW parameters after creation has no effect on existing data.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Current name of the collection to modify"
      },
      new_name: {
        type: "string",
        description: "New name for the collection (optional)"
      },
      new_metadata: {
        type: "object",
        description: "New metadata to set (replaces existing metadata)"
      }
    },
    required: ["collection_name"]
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "collection": {
    "name": "string - Collection name (possibly updated)",
    "metadata": "object - Updated metadata"
  },
  "changes": {
    "name_changed": "boolean",
    "metadata_changed": "boolean"
  },
  "message": "string - Human-readable status"
}
```

**Example Usage**:
```
User: "Rename the api_docs collection to rest_api_v2"
LLM: [calls chroma_modify_collection with collection_name="api_docs", new_name="rest_api_v2"]
```

---

### 7. chroma_delete_collection

**Purpose**: Delete a collection and all its documents

**When to Use**:
- Removing unused collections
- Cleaning up test data
- ⚠️ Use with caution - this is destructive

```typescript
{
  name: "chroma_delete_collection",
  description: "Delete a collection and all its documents. WARNING: This action is irreversible. Consider using dolt_commit first to save the current state.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Name of the collection to delete"
      },
      confirm: {
        type: "boolean",
        description: "Must be true to confirm deletion. Safety check.",
        default: false
      }
    },
    required: ["collection_name"]
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "deleted_collection": "string - Name of deleted collection",
  "documents_deleted": "number - Number of documents that were in the collection",
  "message": "string - Human-readable status"
}
```

**Example Usage**:
```
User: "Delete the old test collection"
LLM: "I'll delete the 'test_collection'. This will remove all 23 documents. Are you sure?"
User: "Yes, delete it"
LLM: [calls chroma_delete_collection with collection_name="test_collection", confirm=true]
```

**Error Cases**:
- `COLLECTION_NOT_FOUND`: Collection does not exist.
- `CONFIRMATION_REQUIRED`: confirm must be true.

---

## Document Operations Tools

### 8. chroma_add_documents

**Purpose**: Add new documents to a collection

**When to Use**:
- Adding new knowledge to the system
- Importing documents
- Building up a knowledge base

> **Version Control Note**: Added documents appear as "local changes" in `dolt_status` until committed with `dolt_commit`.

```typescript
{
  name: "chroma_add_documents",
  description: "Add one or more documents to a collection. Documents are automatically chunked, embedded, and indexed for semantic search. Each document can have optional metadata for filtering.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Name of the collection to add documents to"
      },
      documents: {
        type: "array",
        description: "Array of document texts to add",
        items: { type: "string" }
      },
      ids: {
        type: "array",
        description: "Optional array of unique IDs for the documents. If not provided, UUIDs are generated.",
        items: { type: "string" }
      },
      metadatas: {
        type: "array",
        description: "Optional array of metadata objects, one per document",
        items: { type: "object" }
      }
    },
    required: ["collection_name", "documents"]
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "collection_name": "string",
  "documents_added": "number - Count of documents added",
  "ids": "array - IDs assigned to the documents",
  "chunks_created": "number - Total chunks created (documents are chunked)",
  "message": "string - Human-readable status"
}
```

**Example Usage**:
```
User: "Add this documentation to the knowledge base:
      
      # Authentication API
      The authentication endpoint accepts POST requests to /api/auth/login..."

LLM: [calls chroma_add_documents with 
      collection_name="vmrag_main",
      documents=["# Authentication API\nThe authentication endpoint..."],
      metadatas=[{
        "title": "Authentication API",
        "doc_type": "api_reference",
        "endpoint": "/api/auth/login"
      }]]

Response:
{
  "success": true,
  "collection_name": "vmrag_main",
  "documents_added": 1,
  "ids": ["doc_a1b2c3d4"],
  "chunks_created": 3,
  "message": "Added 1 document (3 chunks) to vmrag_main. Use dolt_commit to save this change."
}
```

**Error Cases**:
- `COLLECTION_NOT_FOUND`: Collection does not exist.
- `DUPLICATE_ID`: A document with the same ID already exists.
- `INVALID_METADATA`: Metadata contains unsupported types.

---

### 9. chroma_query_documents

**Purpose**: Search for documents using semantic similarity

**When to Use**:
- Finding relevant documents for a question
- RAG retrieval
- Exploring knowledge base content

This is the **primary tool for RAG retrieval**.

```typescript
{
  name: "chroma_query_documents",
  description: "Query documents using semantic search. Returns documents most similar to the query text, with optional metadata filtering. This is the primary retrieval mechanism for RAG.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Name of the collection to query"
      },
      query_texts: {
        type: "array",
        description: "Array of query strings to search for. Each query returns its own set of results.",
        items: { type: "string" }
      },
      n_results: {
        type: "number",
        description: "Number of results to return per query. Default: 5",
        default: 5
      },
      where: {
        type: "object",
        description: "Metadata filter using ChromaDB query operators",
        examples: [
          {"doc_type": "api_reference"},
          {"priority": {"$gt": 5}},
          {"$and": [{"status": "active"}, {"category": "auth"}]}
        ]
      },
      where_document: {
        type: "object",
        description: "Document content filter",
        examples: [
          {"$contains": "authentication"},
          {"$not_contains": "deprecated"}
        ]
      },
      include: {
        type: "array",
        description: "What to include in results: 'documents', 'metadatas', 'distances', 'embeddings'. Default: ['documents', 'metadatas', 'distances']",
        items: { 
          type: "string",
          enum: ["documents", "metadatas", "distances", "embeddings"]
        },
        default: ["documents", "metadatas", "distances"]
      }
    },
    required: ["collection_name", "query_texts"]
  }
}
```

**Output Schema**:
```json
{
  "collection_name": "string",
  "results": [
    {
      "query": "string - The query text",
      "matches": [
        {
          "id": "string - Document/chunk ID",
          "document": "string - Document content",
          "metadata": {
            "source_id": "string - Original document ID",
            "chunk_index": "number - Chunk position",
            "total_chunks": "number - Total chunks in document",
            "title": "string - Document title",
            "...": "any other metadata fields"
          },
          "distance": "number - Similarity distance (lower = more similar)"
        }
      ]
    }
  ]
}
```

**Filter Operators**:
```
Comparison:
  $eq    - Equal to
  $ne    - Not equal to  
  $gt    - Greater than
  $gte   - Greater than or equal
  $lt    - Less than
  $lte   - Less than or equal
  $in    - In array
  $nin   - Not in array

Logical:
  $and   - All conditions must match
  $or    - Any condition must match

Document:
  $contains     - Document contains text
  $not_contains - Document doesn't contain text
```

**Example Usage**:
```
User: "How do I authenticate with the API?"
LLM: [calls chroma_query_documents with
      collection_name="vmrag_main",
      query_texts=["how to authenticate with API"],
      n_results=5,
      where={"doc_type": "api_reference"}]

Response:
{
  "collection_name": "vmrag_main",
  "results": [
    {
      "query": "how to authenticate with API",
      "matches": [
        {
          "id": "doc_a1b2c3d4_chunk_0",
          "document": "# Authentication API\nThe authentication endpoint accepts POST requests to /api/auth/login with a JSON body containing 'username' and 'password' fields...",
          "metadata": {
            "source_id": "doc_a1b2c3d4",
            "chunk_index": 0,
            "total_chunks": 3,
            "title": "Authentication API",
            "doc_type": "api_reference",
            "endpoint": "/api/auth/login"
          },
          "distance": 0.234
        },
        // ... more matches
      ]
    }
  ]
}
```

**Advanced Filter Examples**:
```javascript
// Find active API docs about authentication
where: {
  "$and": [
    {"doc_type": "api_reference"},
    {"status": {"$ne": "deprecated"}},
    {"category": {"$in": ["auth", "security"]}}
  ]
}

// Find recent documents with high priority
where: {
  "$and": [
    {"priority": {"$gte": 8}},
    {"created_date": {"$gt": "2025-01-01"}}
  ]
}

// Text filter: must contain "REST" but not "deprecated"
where_document: {
  "$and": [
    {"$contains": "REST"},
    {"$not_contains": "deprecated"}
  ]
}
```

---

### 10. chroma_get_documents

**Purpose**: Retrieve specific documents by ID or filter

**When to Use**:
- Getting a specific document you know exists
- Retrieving documents by metadata filter (not semantic search)
- Pagination through documents

```typescript
{
  name: "chroma_get_documents",
  description: "Retrieve documents by their IDs or using metadata filters. Unlike query, this does not use semantic search - it returns exact matches.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Name of the collection to get documents from"
      },
      ids: {
        type: "array",
        description: "Optional array of document IDs to retrieve",
        items: { type: "string" }
      },
      where: {
        type: "object",
        description: "Optional metadata filter (same syntax as query)"
      },
      where_document: {
        type: "object",
        description: "Optional document content filter"
      },
      include: {
        type: "array",
        description: "What to include: 'documents', 'metadatas', 'embeddings'. Default: ['documents', 'metadatas']",
        default: ["documents", "metadatas"]
      },
      limit: {
        type: "number",
        description: "Maximum documents to return. Default: 100",
        default: 100
      },
      offset: {
        type: "number",
        description: "Number of documents to skip (pagination). Default: 0",
        default: 0
      }
    },
    required: ["collection_name"]
  }
}
```

**Output Schema**:
```json
{
  "collection_name": "string",
  "documents": [
    {
      "id": "string",
      "document": "string",
      "metadata": "object"
    }
  ],
  "total_matching": "number - Total documents matching filter",
  "has_more": "boolean - More documents available"
}
```

**Example Usage**:
```
User: "Get the document with ID doc_a1b2c3d4"
LLM: [calls chroma_get_documents with
      collection_name="vmrag_main",
      ids=["doc_a1b2c3d4"]]

User: "Show me all deprecated API docs"
LLM: [calls chroma_get_documents with
      collection_name="vmrag_main",
      where={"status": "deprecated"},
      limit=20]
```

---

### 11. chroma_update_documents

**Purpose**: Update existing documents' content or metadata

**When to Use**:
- Correcting document content
- Updating metadata (status, tags, etc.)
- Refreshing outdated information

> **Version Control Note**: Updated documents appear as "modified" in `dolt_status` until committed.

```typescript
{
  name: "chroma_update_documents",
  description: "Update existing documents' content, metadata, or embeddings. The document must already exist in the collection.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Name of the collection containing the documents"
      },
      ids: {
        type: "array",
        description: "Array of document IDs to update",
        items: { type: "string" }
      },
      documents: {
        type: "array",
        description: "Optional new document texts (must match ids length)",
        items: { type: "string" }
      },
      metadatas: {
        type: "array",
        description: "Optional new metadata objects (must match ids length). Replaces existing metadata.",
        items: { type: "object" }
      },
      embeddings: {
        type: "array",
        description: "Optional new embeddings (must match ids length). Usually auto-generated from documents.",
        items: { 
          type: "array",
          items: { type: "number" }
        }
      }
    },
    required: ["collection_name", "ids"]
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "collection_name": "string",
  "documents_updated": "number",
  "ids": "array - IDs of updated documents",
  "message": "string - Human-readable status"
}
```

**Example Usage**:
```
User: "Mark the authentication doc as deprecated"
LLM: [calls chroma_update_documents with
      collection_name="vmrag_main",
      ids=["doc_a1b2c3d4"],
      metadatas=[{
        "title": "Authentication API",
        "doc_type": "api_reference",
        "status": "deprecated",
        "deprecated_date": "2025-12-15"
      }]]

User: "Update the content of the login docs"
LLM: [calls chroma_update_documents with
      collection_name="vmrag_main",
      ids=["doc_a1b2c3d4"],
      documents=["# Authentication API (v2)\nThe new authentication..."],
      metadatas=[{
        "title": "Authentication API (v2)",
        "doc_type": "api_reference",
        "version": "2.0"
      }]]
```

**Error Cases**:
- `DOCUMENT_NOT_FOUND`: One or more IDs don't exist.
- `LENGTH_MISMATCH`: Arrays (documents, metadatas) don't match ids length.

---

### 12. chroma_delete_documents

**Purpose**: Delete specific documents from a collection

**When to Use**:
- Removing outdated documents
- Cleaning up incorrect entries
- Managing collection size

> **Version Control Note**: Deleted documents appear as "deleted" in `dolt_status` until committed.

```typescript
{
  name: "chroma_delete_documents",
  description: "Delete specific documents from a collection by their IDs. This removes the documents and their embeddings permanently from the collection.",
  inputSchema: {
    type: "object",
    properties: {
      collection_name: {
        type: "string",
        description: "Name of the collection to delete documents from"
      },
      ids: {
        type: "array",
        description: "Array of document IDs to delete",
        items: { type: "string" }
      },
      where: {
        type: "object",
        description: "Optional: Delete documents matching this filter instead of by IDs"
      },
      where_document: {
        type: "object",
        description: "Optional: Delete documents matching this content filter"
      }
    },
    required: ["collection_name"]
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "collection_name": "string",
  "documents_deleted": "number",
  "ids_deleted": "array - IDs of deleted documents",
  "message": "string - Human-readable status"
}
```

**Example Usage**:
```
User: "Delete the old authentication doc"
LLM: [calls chroma_delete_documents with
      collection_name="vmrag_main",
      ids=["doc_a1b2c3d4"]]

User: "Remove all deprecated documents"
LLM: [calls chroma_delete_documents with
      collection_name="vmrag_main",
      where={"status": "deprecated"}]
```

**Error Cases**:
- `COLLECTION_NOT_FOUND`: Collection does not exist.
- `NO_SELECTION`: Neither ids nor where/where_document provided.

---

## ChromaDB Tool Interaction Patterns

### Pattern 1: RAG Retrieval
```
User: "How do I configure SSL?"

1. chroma_query_documents(
     collection_name="vmrag_main",
     query_texts=["how to configure SSL certificates"],
     n_results=5
   )
2. LLM synthesizes answer from retrieved documents
```

### Pattern 2: Add Knowledge
```
User: "Add this document to the knowledge base: ..."

1. chroma_add_documents(
     collection_name="vmrag_main",
     documents=["..."],
     metadatas=[{title: "...", doc_type: "..."}]
   )
2. Optionally: dolt_commit(message="Added SSL documentation")
```

### Pattern 3: Update and Track
```
User: "Update the API docs and save the change"

1. chroma_update_documents(
     collection_name="vmrag_main",
     ids=["doc_123"],
     documents=["Updated content..."]
   )
2. dolt_status()  // Shows 1 modified document
3. dolt_commit(message="Updated API authentication docs")
```

### Pattern 4: Explore Collection
```
User: "What's in the knowledge base?"

1. chroma_list_collections()
2. chroma_get_collection_count(collection_name="vmrag_main")
3. chroma_peek_collection(collection_name="vmrag_main", limit=5)
```

---

## ChromaDB + Dolt Integration

### How Changes Are Tracked

When you use ChromaDB tools, changes are automatically tracked:

| ChromaDB Operation | Dolt Status Shows |
|-------------------|-------------------|
| `chroma_add_documents` | "Added" documents |
| `chroma_update_documents` | "Modified" documents |
| `chroma_delete_documents` | "Deleted" documents |

### Workflow Example

```
1. User queries knowledge base
   → chroma_query_documents (no change tracking)

2. User adds new document
   → chroma_add_documents
   → dolt_status shows: "1 new document"

3. User commits change
   → dolt_commit(message="Added REST API docs")
   → dolt_status shows: "clean"

4. User pushes to team
   → dolt_push
   → Changes available to other team members
```

### Important Notes

1. **Uncommitted changes persist locally** - If you add documents but don't commit, they remain in ChromaDB but aren't versioned.

2. **Checkout/Pull overwrites uncommitted changes** - Use `dolt_status` to check for local changes before `dolt_checkout` or `dolt_pull`.

3. **Collection names may change per branch** - When switching branches, you may be working with different collections (e.g., `vmrag_main` vs `vmrag_feature_x`).

---

# Part 2: Dolt Tools (Version Control)

---

These tools manage version control operations. See Part 1 for ChromaDB document operations.

### 1. dolt_status

**Purpose**: Get the current state of the MCP server's version control

**When to Use**: 
- Before any version control operation to understand current state
- To check if there are uncommitted local changes
- To verify which branch and commit you're on

```typescript
// Tool Definition
{
  name: "dolt_status",
  description: "Get the current version control status including active branch, current commit, and any uncommitted local changes in the ChromaDB working copy.",
  inputSchema: {
    type: "object",
    properties: {
      verbose: {
        type: "boolean",
        description: "If true, includes detailed list of changed documents. Default: false",
        default: false
      }
    },
    required: []
  }
}
```

**Output Schema**:
```json
{
  "branch": "string - Current branch name",
  "commit": {
    "hash": "string - Current commit hash (40 chars)",
    "short_hash": "string - Short hash (7 chars)",
    "message": "string - Commit message",
    "author": "string - Commit author",
    "timestamp": "string - ISO 8601 timestamp"
  },
  "remote": {
    "name": "string - Remote name (e.g., 'origin')",
    "url": "string - Remote URL",
    "connected": "boolean - Whether remote is reachable"
  },
  "local_changes": {
    "has_changes": "boolean - True if uncommitted changes exist",
    "summary": {
      "added": "number - Count of new documents",
      "modified": "number - Count of modified documents", 
      "deleted": "number - Count of deleted documents",
      "total": "number - Total changes"
    },
    "documents": "array - List of changed doc IDs (only if verbose=true)"
  },
  "sync_state": {
    "ahead": "number - Commits ahead of remote",
    "behind": "number - Commits behind remote",
    "diverged": "boolean - True if local and remote have diverged"
  }
}
```

**Example Usage**:
```
User: "What's the current state of my knowledge base?"
LLM: [calls dolt_status with verbose=false]

Response:
{
  "branch": "main",
  "commit": {
    "hash": "abc123def456...",
    "short_hash": "abc123d",
    "message": "Added API documentation",
    "author": "user@example.com",
    "timestamp": "2025-12-13T10:30:00Z"
  },
  "remote": {
    "name": "origin",
    "url": "https://doltremoteapi.dolthub.com/myorg/knowledge-base",
    "connected": true
  },
  "local_changes": {
    "has_changes": true,
    "summary": {
      "added": 2,
      "modified": 1,
      "deleted": 0,
      "total": 3
    }
  },
  "sync_state": {
    "ahead": 0,
    "behind": 0,
    "diverged": false
  }
}
```

**Error Cases**:
- `NOT_INITIALIZED`: No Dolt repository configured. Use `dolt_init` or `dolt_clone` first.
- `REMOTE_UNREACHABLE`: Cannot connect to remote (sync_state may be stale).

---

### 2. dolt_branches

**Purpose**: List available branches on the remote repository

**When to Use**:
- To see what branches exist before checkout
- To find a specific branch to work on
- To understand the project's branch structure

```typescript
{
  name: "dolt_branches",
  description: "List all branches available on the remote Dolt repository, including their latest commit information.",
  inputSchema: {
    type: "object",
    properties: {
      include_local: {
        type: "boolean",
        description: "Include local-only branches not on remote. Default: true",
        default: true
      },
      filter: {
        type: "string",
        description: "Filter branches by name pattern (supports * wildcard). Example: 'feature/*'"
      }
    },
    required: []
  }
}
```

**Output Schema**:
```json
{
  "current_branch": "string - Currently checked out branch",
  "branches": [
    {
      "name": "string - Branch name",
      "is_current": "boolean - True if this is the active branch",
      "is_local": "boolean - True if exists locally",
      "is_remote": "boolean - True if exists on remote",
      "latest_commit": {
        "hash": "string - Commit hash",
        "short_hash": "string - Short hash",
        "message": "string - Commit message",
        "timestamp": "string - ISO 8601 timestamp"
      },
      "ahead": "number - Commits ahead of remote (if local)",
      "behind": "number - Commits behind remote (if local)"
    }
  ],
  "total_count": "number - Total branches found"
}
```

**Example Usage**:
```
User: "What branches are available?"
LLM: [calls dolt_branches]

Response:
{
  "current_branch": "main",
  "branches": [
    {
      "name": "main",
      "is_current": true,
      "is_local": true,
      "is_remote": true,
      "latest_commit": {
        "hash": "abc123...",
        "short_hash": "abc123d",
        "message": "Initial import",
        "timestamp": "2025-12-10T08:00:00Z"
      },
      "ahead": 0,
      "behind": 0
    },
    {
      "name": "feature/new-api-docs",
      "is_current": false,
      "is_local": false,
      "is_remote": true,
      "latest_commit": {
        "hash": "def456...",
        "short_hash": "def456a",
        "message": "WIP: API v2 documentation",
        "timestamp": "2025-12-12T14:30:00Z"
      }
    }
  ],
  "total_count": 2
}
```

---

### 3. dolt_commits

**Purpose**: List commits on a specific branch with their metadata

**When to Use**:
- To see the history of a branch
- To find a specific commit to checkout or compare
- To understand what changes have been made over time

```typescript
{
  name: "dolt_commits",
  description: "List commits on a specified branch, including commit messages, authors, and timestamps. Returns most recent commits first.",
  inputSchema: {
    type: "object",
    properties: {
      branch: {
        type: "string",
        description: "Branch name to list commits from. Default: current branch"
      },
      limit: {
        type: "number",
        description: "Maximum number of commits to return. Default: 20, Max: 100",
        default: 20,
        minimum: 1,
        maximum: 100
      },
      offset: {
        type: "number",
        description: "Number of commits to skip (for pagination). Default: 0",
        default: 0
      },
      since: {
        type: "string",
        description: "Only show commits after this date (ISO 8601 format)"
      },
      until: {
        type: "string",
        description: "Only show commits before this date (ISO 8601 format)"
      }
    },
    required: []
  }
}
```

**Output Schema**:
```json
{
  "branch": "string - Branch name",
  "commits": [
    {
      "hash": "string - Full commit hash",
      "short_hash": "string - Short hash (7 chars)",
      "message": "string - Commit message",
      "author": "string - Author name/email",
      "timestamp": "string - ISO 8601 timestamp",
      "parent_hash": "string - Parent commit hash (null for initial)",
      "stats": {
        "documents_added": "number",
        "documents_modified": "number",
        "documents_deleted": "number"
      }
    }
  ],
  "total_commits": "number - Total commits on branch",
  "has_more": "boolean - True if more commits exist beyond limit"
}
```

**Example Usage**:
```
User: "Show me the last 5 commits on main"
LLM: [calls dolt_commits with branch="main", limit=5]
```

---

### 4. dolt_show

**Purpose**: Show detailed information about a specific commit

**When to Use**:
- To see what documents were changed in a commit
- To review commit details before checkout
- To understand the contents of a specific version

```typescript
{
  name: "dolt_show",
  description: "Show detailed information about a specific commit, including the list of documents that were added, modified, or deleted.",
  inputSchema: {
    type: "object",
    properties: {
      commit: {
        type: "string",
        description: "Commit hash (full or short) or reference (e.g., 'HEAD', 'HEAD~1', 'main')"
      },
      include_diff: {
        type: "boolean",
        description: "Include content diff for changed documents. Default: false (can be large)",
        default: false
      },
      diff_limit: {
        type: "number",
        description: "Max documents to include diff for (if include_diff=true). Default: 10",
        default: 10
      }
    },
    required: ["commit"]
  }
}
```

**Output Schema**:
```json
{
  "commit": {
    "hash": "string - Full commit hash",
    "short_hash": "string - Short hash",
    "message": "string - Full commit message",
    "author": "string - Author",
    "timestamp": "string - ISO 8601 timestamp",
    "parent_hash": "string - Parent commit hash"
  },
  "changes": {
    "summary": {
      "added": "number",
      "modified": "number",
      "deleted": "number",
      "total": "number"
    },
    "documents": [
      {
        "doc_id": "string - Document ID",
        "collection": "string - Collection name",
        "change_type": "string - 'added' | 'modified' | 'deleted'",
        "title": "string - Document title (if available)",
        "diff": {
          "content_before": "string - Previous content (if include_diff and modified)",
          "content_after": "string - New content (if include_diff and added/modified)",
          "metadata_changes": "object - Changed metadata fields"
        }
      }
    ]
  },
  "branches": "array - Branches containing this commit"
}
```

**Example Usage**:
```
User: "What changed in commit abc123d?"
LLM: [calls dolt_show with commit="abc123d"]
```

---

### 5. dolt_find

**Purpose**: Find commits by hash pattern or message content

**When to Use**:
- To find a commit when you only remember part of the hash or message
- To search for commits related to a specific topic
- To locate when a particular change was made

```typescript
{
  name: "dolt_find",
  description: "Search for commits by partial hash or message content. Useful for finding specific commits when you don't have the full hash.",
  inputSchema: {
    type: "object",
    properties: {
      query: {
        type: "string",
        description: "Search query - matches against commit hash (prefix) and message (contains)"
      },
      search_type: {
        type: "string",
        enum: ["all", "hash", "message"],
        description: "What to search: 'all' (default), 'hash' only, or 'message' only",
        default: "all"
      },
      branch: {
        type: "string",
        description: "Limit search to specific branch. Default: all branches"
      },
      limit: {
        type: "number",
        description: "Maximum results to return. Default: 10",
        default: 10
      }
    },
    required: ["query"]
  }
}
```

**Output Schema**:
```json
{
  "query": "string - Search query used",
  "results": [
    {
      "hash": "string - Full commit hash",
      "short_hash": "string - Short hash",
      "message": "string - Commit message",
      "author": "string - Author",
      "timestamp": "string - ISO 8601 timestamp",
      "branch": "string - Branch containing this commit",
      "match_type": "string - 'hash' or 'message'"
    }
  ],
  "total_found": "number - Total matching commits"
}
```

**Example Usage**:
```
User: "Find the commit where we added the API docs"
LLM: [calls dolt_find with query="API docs", search_type="message"]

User: "Find commit starting with abc1"
LLM: [calls dolt_find with query="abc1", search_type="hash"]
```

---

### 6. dolt_init

**Purpose**: Initialize a new Dolt repository for the MCP server

**When to Use**:
- Starting a new knowledge base that you want to version control
- Setting up the MCP server for the first time
- When you have an existing ChromaDB collection to add version control to

```typescript
{
  name: "dolt_init",
  description: "Initialize a new Dolt repository for version control. Use this when starting a new knowledge base or adding version control to an existing ChromaDB collection. For cloning an existing repository, use dolt_clone instead.",
  inputSchema: {
    type: "object",
    properties: {
      remote_url: {
        type: "string",
        description: "Optional DoltHub remote URL to configure (e.g., 'myorg/my-knowledge-base'). If provided, the repository will be set up to push/pull from this remote."
      },
      initial_branch: {
        type: "string",
        description: "Name of the initial branch. Default: 'main'",
        default: "main"
      },
      import_existing: {
        type: "boolean",
        description: "If true, imports any existing documents from ChromaDB into the initial commit. Default: true",
        default: true
      },
      commit_message: {
        type: "string",
        description: "Commit message for initial import. Default: 'Initial import from ChromaDB'",
        default: "Initial import from ChromaDB"
      }
    },
    required: []
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "repository": {
    "path": "string - Local repository path",
    "branch": "string - Initial branch name",
    "commit": {
      "hash": "string - Initial commit hash (if import_existing)",
      "message": "string - Commit message"
    }
  },
  "remote": {
    "configured": "boolean - Whether remote was configured",
    "name": "string - Remote name",
    "url": "string - Remote URL"
  },
  "import_summary": {
    "documents_imported": "number - Documents imported from ChromaDB",
    "collections": "array - Collection names imported"
  },
  "message": "string - Human-readable status message"
}
```

**Example Usage**:
```
User: "Set up version control for my knowledge base and connect it to DoltHub"
LLM: [calls dolt_init with remote_url="myorg/knowledge-base", import_existing=true]

Response:
{
  "success": true,
  "repository": {
    "path": "./data/dolt-repo",
    "branch": "main",
    "commit": {
      "hash": "abc123...",
      "message": "Initial import from ChromaDB"
    }
  },
  "remote": {
    "configured": true,
    "name": "origin",
    "url": "https://doltremoteapi.dolthub.com/myorg/knowledge-base"
  },
  "import_summary": {
    "documents_imported": 42,
    "collections": ["vmrag_main"]
  },
  "message": "Repository initialized with 42 documents. Remote 'origin' configured. Use dolt_push to upload to DoltHub."
}
```

**Error Cases**:
- `ALREADY_INITIALIZED`: Repository already exists. Use dolt_status to check state.
- `INVALID_REMOTE_URL`: Remote URL format is invalid.
- `CHROMADB_ERROR`: Failed to read existing ChromaDB documents.

---

### 7. dolt_clone

**Purpose**: Clone an existing Dolt repository from DoltHub

**When to Use**:
- Joining an existing project with a shared knowledge base
- Setting up a new machine with an existing repository
- Getting a copy of someone else's knowledge base

```typescript
{
  name: "dolt_clone",
  description: "Clone an existing Dolt repository from DoltHub or another remote. This downloads the repository and populates the local ChromaDB with the documents from the specified branch/commit.",
  inputSchema: {
    type: "object",
    properties: {
      remote_url: {
        type: "string",
        description: "DoltHub repository URL (e.g., 'myorg/knowledge-base' or full URL)"
      },
      branch: {
        type: "string",
        description: "Branch to checkout after clone. Default: repository's default branch"
      },
      commit: {
        type: "string",
        description: "Specific commit to checkout. If provided, overrides branch."
      }
    },
    required: ["remote_url"]
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "repository": {
    "path": "string - Local repository path",
    "remote_url": "string - Cloned from URL"
  },
  "checkout": {
    "branch": "string - Checked out branch",
    "commit": {
      "hash": "string - Current commit hash",
      "message": "string - Commit message",
      "timestamp": "string - Commit timestamp"
    }
  },
  "sync_summary": {
    "documents_loaded": "number - Documents synced to ChromaDB",
    "collections_created": "array - Collection names created"
  },
  "message": "string - Human-readable status message"
}
```

**Example Usage**:
```
User: "Clone the team knowledge base from DoltHub"
LLM: [calls dolt_clone with remote_url="myteam/shared-knowledge"]

User: "Clone the knowledge base but use the feature branch"
LLM: [calls dolt_clone with remote_url="myteam/shared-knowledge", branch="feature/new-docs"]

User: "Clone and checkout a specific commit"
LLM: [calls dolt_clone with remote_url="myteam/shared-knowledge", commit="abc123d"]
```

**Error Cases**:
- `ALREADY_INITIALIZED`: Repository already exists. Use dolt_reset or manual cleanup.
- `REMOTE_NOT_FOUND`: Repository does not exist at the specified URL.
- `AUTHENTICATION_FAILED`: Not authorized to access this repository.
- `BRANCH_NOT_FOUND`: Specified branch does not exist.
- `COMMIT_NOT_FOUND`: Specified commit does not exist.

---

### 8. dolt_fetch

**Purpose**: Fetch updates from the remote without applying them

**When to Use**:
- To see what commits are available on the remote
- Before pulling to understand what will be merged
- To check if you're behind the remote

```typescript
{
  name: "dolt_fetch",
  description: "Fetch commits from the remote repository without applying them to your local ChromaDB. Use this to see what changes are available before pulling.",
  inputSchema: {
    type: "object",
    properties: {
      remote: {
        type: "string",
        description: "Remote name to fetch from. Default: 'origin'",
        default: "origin"
      },
      branch: {
        type: "string",
        description: "Specific branch to fetch. Default: all branches"
      }
    },
    required: []
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "remote": "string - Remote name",
  "updates": {
    "branches_updated": [
      {
        "name": "string - Branch name",
        "old_commit": "string - Previous known commit (null if new)",
        "new_commit": "string - Fetched commit",
        "commits_fetched": "number - New commits on this branch"
      }
    ],
    "new_branches": "array - Newly discovered branches",
    "total_commits_fetched": "number"
  },
  "current_branch_status": {
    "branch": "string - Your current branch",
    "behind": "number - Commits behind remote",
    "ahead": "number - Commits ahead of remote"
  },
  "message": "string - Human-readable summary"
}
```

**Example Usage**:
```
User: "Check if there are any updates available"
LLM: [calls dolt_fetch]

Response:
{
  "success": true,
  "remote": "origin",
  "updates": {
    "branches_updated": [
      {
        "name": "main",
        "old_commit": "abc123...",
        "new_commit": "def456...",
        "commits_fetched": 3
      }
    ],
    "new_branches": ["feature/new-api"],
    "total_commits_fetched": 5
  },
  "current_branch_status": {
    "branch": "main",
    "behind": 3,
    "ahead": 0
  },
  "message": "Fetched 5 new commits. Your branch 'main' is 3 commits behind origin/main."
}
```

---

### 9. dolt_pull

**Purpose**: Fetch and merge changes from the remote

**When to Use**:
- To get the latest changes from the team
- To update your local knowledge base with remote updates
- After someone else has pushed changes

```typescript
{
  name: "dolt_pull",
  description: "Fetch changes from the remote and merge them into your current branch. This updates both the Dolt repository and the local ChromaDB with the merged content.",
  inputSchema: {
    type: "object",
    properties: {
      remote: {
        type: "string",
        description: "Remote name to pull from. Default: 'origin'",
        default: "origin"
      },
      branch: {
        type: "string",
        description: "Remote branch to pull. Default: current branch's upstream"
      },
      if_uncommitted: {
        type: "string",
        enum: ["abort", "commit_first", "reset_first", "stash"],
        description: "Action if local uncommitted changes exist: 'abort' (default, return error), 'commit_first' (auto-commit with generated message), 'reset_first' (discard local changes), 'stash' (save changes, pull, restore)",
        default: "abort"
      },
      commit_message: {
        type: "string",
        description: "Commit message if if_uncommitted='commit_first'. Default: 'Auto-commit before pull'"
      }
    },
    required: []
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "action_taken": {
    "uncommitted_handling": "string - What was done with uncommitted changes",
    "pre_commit": {
      "created": "boolean - Whether a commit was created first",
      "hash": "string - Pre-pull commit hash if created"
    }
  },
  "pull_result": {
    "merge_type": "string - 'fast_forward' | 'merge' | 'already_up_to_date'",
    "commits_merged": "number - Number of commits merged",
    "from_commit": "string - Remote commit hash merged from",
    "to_commit": "string - Resulting commit hash"
  },
  "sync_summary": {
    "documents_added": "number",
    "documents_modified": "number",
    "documents_deleted": "number",
    "total_changes": "number"
  },
  "message": "string - Human-readable summary"
}
```

**Example Usage**:
```
User: "Get the latest updates from the team"
LLM: [calls dolt_pull]

# If uncommitted changes exist:
Response:
{
  "success": false,
  "error": "UNCOMMITTED_CHANGES",
  "message": "You have 3 uncommitted changes. Choose an action: 'commit_first' to save your changes, 'reset_first' to discard them, or 'stash' to temporarily save them.",
  "local_changes": {
    "added": 2,
    "modified": 1,
    "deleted": 0
  }
}

User: "Commit my changes first, then pull"
LLM: [calls dolt_pull with if_uncommitted="commit_first", commit_message="WIP: My local changes"]
```

**Error Cases**:
- `UNCOMMITTED_CHANGES`: Local changes exist and if_uncommitted='abort'.
- `MERGE_CONFLICT`: Merge conflicts occurred (manual resolution needed).
- `REMOTE_UNREACHABLE`: Cannot connect to remote.
- `NO_UPSTREAM`: Current branch has no upstream branch configured.

---

### 10. dolt_checkout

**Purpose**: Switch to a different branch or commit

**When to Use**:
- To switch to a different branch to work on
- To view the state of the knowledge base at a specific commit
- To start work on a feature branch

```typescript
{
  name: "dolt_checkout",
  description: "Switch to a different branch or commit. This updates the local ChromaDB to reflect the documents at that branch/commit.",
  inputSchema: {
    type: "object",
    properties: {
      target: {
        type: "string",
        description: "Branch name or commit hash to checkout"
      },
      create_branch: {
        type: "boolean",
        description: "If true, creates a new branch with the given name. Default: false",
        default: false
      },
      from: {
        type: "string",
        description: "Base branch/commit for new branch (only used with create_branch=true). Default: current HEAD"
      },
      if_uncommitted: {
        type: "string",
        enum: ["abort", "commit_first", "reset_first", "carry"],
        description: "Action if local uncommitted changes exist: 'abort' (default), 'commit_first', 'reset_first', 'carry' (bring changes to new branch)",
        default: "abort"
      },
      commit_message: {
        type: "string",
        description: "Commit message if if_uncommitted='commit_first'"
      }
    },
    required: ["target"]
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "action_taken": {
    "uncommitted_handling": "string - What was done with uncommitted changes",
    "branch_created": "boolean - Whether a new branch was created"
  },
  "checkout_result": {
    "from_branch": "string - Previous branch",
    "from_commit": "string - Previous commit hash",
    "to_branch": "string - New current branch",
    "to_commit": "string - New current commit hash"
  },
  "sync_summary": {
    "documents_added": "number - Docs added to ChromaDB",
    "documents_modified": "number - Docs modified in ChromaDB",
    "documents_deleted": "number - Docs removed from ChromaDB",
    "total_changes": "number"
  },
  "message": "string - Human-readable summary"
}
```

**Example Usage**:
```
User: "Switch to the feature branch"
LLM: [calls dolt_checkout with target="feature/new-docs"]

User: "Create a new branch for my work"
LLM: [calls dolt_checkout with target="feature/my-changes", create_branch=true]

User: "Go back to how things were 3 commits ago"
LLM: [calls dolt_checkout with target="HEAD~3"]
```

---

### 11. dolt_reset

**Purpose**: Reset to a specific commit, discarding local changes

**When to Use**:
- To discard all local uncommitted changes
- To reset to a previous commit
- To sync with the remote's latest commit

```typescript
{
  name: "dolt_reset",
  description: "Reset the current branch to a specific commit, updating ChromaDB to match. WARNING: This discards uncommitted local changes.",
  inputSchema: {
    type: "object",
    properties: {
      target: {
        type: "string",
        description: "Commit to reset to. Options: 'HEAD' (discard uncommitted), 'origin/main' (match remote), or specific commit hash. Default: 'HEAD'",
        default: "HEAD"
      },
      confirm_discard: {
        type: "boolean",
        description: "Must be true to confirm discarding uncommitted changes. Safety check.",
        default: false
      }
    },
    required: []
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "reset_result": {
    "from_commit": "string - Previous commit hash",
    "to_commit": "string - Reset to commit hash",
    "discarded_changes": {
      "added": "number - Uncommitted adds discarded",
      "modified": "number - Uncommitted modifications discarded",
      "deleted": "number - Uncommitted deletes discarded",
      "total": "number"
    }
  },
  "sync_summary": {
    "documents_restored": "number - Docs restored to previous state",
    "documents_removed": "number - Docs removed that were added locally"
  },
  "message": "string - Human-readable summary"
}
```

**Example Usage**:
```
User: "Discard all my local changes"
LLM: [calls dolt_reset with target="HEAD", confirm_discard=true]

User: "Reset to match the remote"
LLM: [calls dolt_reset with target="origin/main", confirm_discard=true]
```

**Error Cases**:
- `CONFIRMATION_REQUIRED`: confirm_discard must be true when there are uncommitted changes.
- `COMMIT_NOT_FOUND`: Target commit does not exist.

---

### 12. dolt_commit

**Purpose**: Commit current ChromaDB state to the Dolt repository

**When to Use**:
- To save your current work as a version
- Before switching branches
- Before pulling to ensure your changes are saved

```typescript
{
  name: "dolt_commit",
  description: "Commit the current state of ChromaDB to the Dolt repository, creating a new version that can be pushed, shared, or reverted to.",
  inputSchema: {
    type: "object",
    properties: {
      message: {
        type: "string",
        description: "Commit message describing the changes. Required."
      },
      author: {
        type: "string",
        description: "Author name/email for the commit. Default: configured user"
      }
    },
    required: ["message"]
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "commit": {
    "hash": "string - New commit hash",
    "short_hash": "string - Short hash",
    "message": "string - Commit message",
    "author": "string - Commit author",
    "timestamp": "string - Commit timestamp",
    "parent_hash": "string - Parent commit hash"
  },
  "changes_committed": {
    "added": "number - Documents added",
    "modified": "number - Documents modified",
    "deleted": "number - Documents deleted",
    "total": "number"
  },
  "message": "string - Human-readable summary"
}
```

**Example Usage**:
```
User: "Save my current work"
LLM: [calls dolt_commit with message="Added documentation for REST API endpoints"]

Response:
{
  "success": true,
  "commit": {
    "hash": "abc123def456...",
    "short_hash": "abc123d",
    "message": "Added documentation for REST API endpoints",
    "author": "user@example.com",
    "timestamp": "2025-12-13T15:30:00Z",
    "parent_hash": "xyz789..."
  },
  "changes_committed": {
    "added": 5,
    "modified": 2,
    "deleted": 0,
    "total": 7
  },
  "message": "Created commit abc123d with 7 document changes."
}
```

**Error Cases**:
- `NO_CHANGES`: Nothing to commit (no local changes).
- `MESSAGE_REQUIRED`: Commit message is required.

---

### 13. dolt_push

**Purpose**: Push local commits to the remote repository

**When to Use**:
- To share your commits with the team
- To back up your work to DoltHub
- After committing changes you want others to have

```typescript
{
  name: "dolt_push",
  description: "Push local commits to the remote Dolt repository (DoltHub). Only committed changes are pushed - uncommitted local changes are not affected.",
  inputSchema: {
    type: "object",
    properties: {
      remote: {
        type: "string",
        description: "Remote name to push to. Default: 'origin'",
        default: "origin"
      },
      branch: {
        type: "string",
        description: "Branch to push. Default: current branch"
      },
      set_upstream: {
        type: "boolean",
        description: "Set upstream tracking for the branch. Default: true for new branches",
        default: true
      },
      force: {
        type: "boolean",
        description: "Force push (WARNING: can overwrite remote changes). Default: false",
        default: false
      }
    },
    required: []
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "push_result": {
    "remote": "string - Remote name",
    "branch": "string - Branch pushed",
    "commits_pushed": "number - Number of commits pushed",
    "from_commit": "string - Local commit hash",
    "to_url": "string - Remote URL"
  },
  "remote_state": {
    "remote_branch": "string - Remote branch name",
    "remote_commit": "string - New remote HEAD commit"
  },
  "message": "string - Human-readable summary"
}
```

**Example Usage**:
```
User: "Push my changes to the team"
LLM: [calls dolt_push]

Response:
{
  "success": true,
  "push_result": {
    "remote": "origin",
    "branch": "main",
    "commits_pushed": 2,
    "from_commit": "abc123...",
    "to_url": "https://doltremoteapi.dolthub.com/myteam/knowledge-base"
  },
  "remote_state": {
    "remote_branch": "main",
    "remote_commit": "abc123..."
  },
  "message": "Pushed 2 commits to origin/main."
}
```

**Error Cases**:
- `UNCOMMITTED_CHANGES`: Warning that uncommitted changes won't be pushed (not an error, just info).
- `REMOTE_REJECTED`: Remote rejected push (usually need to pull first).
- `AUTHENTICATION_FAILED`: Not authorized to push to this repository.
- `NO_COMMITS_TO_PUSH`: Already up to date with remote.

---

## Git Integration Feature

### 14. dolt_link_git (Optional/Advanced)

**Purpose**: Link Dolt repository state to a Git repository's branch/commit

**When to Use**:
- To keep knowledge base versions in sync with code versions
- To track which documentation version corresponds to which code version
- For reproducible builds/environments

```typescript
{
  name: "dolt_link_git",
  description: "Link the current Dolt commit to a Git repository's commit. Creates a mapping that allows tracking which knowledge base version corresponds to which code version.",
  inputSchema: {
    type: "object",
    properties: {
      git_repo_path: {
        type: "string",
        description: "Path to Git repository. Default: current directory",
        default: "."
      },
      git_commit: {
        type: "string",
        description: "Git commit to link to. Default: current HEAD"
      },
      bidirectional: {
        type: "boolean",
        description: "If true, also stores the Dolt commit hash in a file in the Git repo. Default: false",
        default: false
      },
      link_file: {
        type: "string",
        description: "Path for bidirectional link file (relative to Git repo). Default: '.dolt-version'",
        default: ".dolt-version"
      }
    },
    required: []
  }
}
```

**Output Schema**:
```json
{
  "success": "boolean",
  "link": {
    "dolt_commit": "string - Dolt commit hash",
    "dolt_branch": "string - Dolt branch",
    "git_commit": "string - Git commit hash",
    "git_branch": "string - Git branch",
    "git_repo": "string - Git repository path"
  },
  "bidirectional": {
    "enabled": "boolean",
    "link_file": "string - Path to link file",
    "file_created": "boolean - Whether link file was created/updated"
  },
  "message": "string - Human-readable summary"
}
```

### Git Integration Implementation Notes

**Requirements for Git Integration**:

1. **Reading Git State**:
   ```csharp
   // Using LibGit2Sharp or git CLI
   var gitCommit = await ExecuteGitCommand("rev-parse HEAD");
   var gitBranch = await ExecuteGitCommand("rev-parse --abbrev-ref HEAD");
   ```

2. **Storing Link in Dolt**:
   ```sql
   CREATE TABLE git_links (
       id INT AUTO_INCREMENT PRIMARY KEY,
       dolt_commit VARCHAR(40) NOT NULL,
       dolt_branch VARCHAR(255) NOT NULL,
       git_commit VARCHAR(40) NOT NULL,
       git_branch VARCHAR(255),
       git_repo_url VARCHAR(500),
       linked_at DATETIME DEFAULT CURRENT_TIMESTAMP,
       metadata JSON,
       
       INDEX idx_dolt_commit (dolt_commit),
       INDEX idx_git_commit (git_commit)
   );
   ```

3. **Bidirectional Link File** (`.dolt-version`):
   ```json
   {
     "dolt_remote": "myorg/knowledge-base",
     "dolt_branch": "main",
     "dolt_commit": "abc123def456...",
     "linked_at": "2025-12-13T15:30:00Z"
   }
   ```

4. **Bare Git Repository Support**:
   - Works with bare repos using `git --git-dir=/path/to/repo.git` 
   - Link file would need to be stored in a different location (e.g., Dolt metadata only)
   - Can read commits/branches but cannot create link file in worktree

5. **Auto-Link on Commit** (optional enhancement):
   ```typescript
   // In dolt_commit, optionally auto-link
   {
     auto_link_git: {
       type: "boolean",
       description: "Automatically link to current Git HEAD. Default: false"
     }
   }
   ```

---

## Tool Response Conventions

### Success Response Pattern
```json
{
  "success": true,
  "...tool_specific_data...": {},
  "message": "Human-readable success message"
}
```

### Error Response Pattern
```json
{
  "success": false,
  "error": "ERROR_CODE",
  "message": "Human-readable error message",
  "details": {
    "...additional_context...": {}
  },
  "suggestions": [
    "Suggested action 1",
    "Suggested action 2"
  ]
}
```

### Common Error Codes

| Code | Description | Typical Resolution |
|------|-------------|-------------------|
| `NOT_INITIALIZED` | No Dolt repository | Use `dolt_init` or `dolt_clone` |
| `UNCOMMITTED_CHANGES` | Local changes exist | Commit, reset, or use `if_uncommitted` parameter |
| `REMOTE_UNREACHABLE` | Cannot connect to remote | Check network, verify URL |
| `AUTHENTICATION_FAILED` | Not authorized | Check credentials, run `dolt login` |
| `BRANCH_NOT_FOUND` | Branch doesn't exist | Check spelling, use `dolt_branches` |
| `COMMIT_NOT_FOUND` | Commit doesn't exist | Check hash, use `dolt_find` |
| `MERGE_CONFLICT` | Conflicting changes | Manual resolution required |
| `NO_CHANGES` | Nothing to commit/push | Already up to date |

---

## Tool Interaction Patterns

### Pattern 1: First-Time Setup
```
1. dolt_clone(remote_url="team/knowledge-base")
   OR
   dolt_init(remote_url="my/new-repo")
```

### Pattern 2: Daily Workflow
```
1. dolt_status()                    # Check current state
2. dolt_fetch()                     # See if updates available
3. dolt_pull()                      # Get updates
4. ... work with documents ...
5. dolt_status()                    # Verify changes
6. dolt_commit(message="...")       # Save work
7. dolt_push()                      # Share with team
```

### Pattern 3: Branch Workflow
```
1. dolt_branches()                  # List available branches
2. dolt_checkout(target="feature/x", create_branch=true)
3. ... work on feature ...
4. dolt_commit(message="Feature X implementation")
5. dolt_checkout(target="main")
6. dolt_pull()                      # Update main
7. dolt_checkout(target="feature/x")
8. dolt_push()                      # Push feature branch
```

### Pattern 4: Recovery
```
# Discard all local changes:
1. dolt_reset(target="HEAD", confirm_discard=true)

# Reset to remote state:
1. dolt_fetch()
2. dolt_reset(target="origin/main", confirm_discard=true)

# Find and restore old version:
1. dolt_find(query="before the bug")
2. dolt_show(commit="abc123d")
3. dolt_checkout(target="abc123d")
```

---

## Implementation Notes

### Required C# Classes

```csharp
// Tool handlers should be implemented in:
// McpTools/DoltVersionControlTools.cs

public class DoltVersionControlTools
{
    private readonly IDoltCli _dolt;
    private readonly ISyncManager _syncManager;
    private readonly IChromaManager _chromaManager;
    
    [McpTool("dolt_status")] 
    public async Task<ToolResult> StatusAsync(bool verbose = false);
    
    [McpTool("dolt_branches")]
    public async Task<ToolResult> BranchesAsync(bool includeLocal = true, string filter = null);
    
    [McpTool("dolt_commits")]
    public async Task<ToolResult> CommitsAsync(string branch = null, int limit = 20, ...);
    
    // ... etc
}
```

### Sync Manager Integration

All tools that modify state must coordinate with the SyncManager:

```csharp
// Tools that trigger Dolt → Chroma sync:
// - dolt_clone (after clone)
// - dolt_pull (after merge)
// - dolt_checkout (after checkout)
// - dolt_reset (after reset)

// Tools that trigger Chroma → Dolt sync:
// - dolt_commit (before commit)
// - dolt_pull with if_uncommitted="commit_first"
// - dolt_checkout with if_uncommitted="commit_first"
```

---

## Appendix: Tool Summary Table

| Tool | Purpose | Modifies ChromaDB | Modifies Dolt | Requires Network |
|------|---------|-------------------|---------------|------------------|
| `dolt_status` | Get current state | No | No | Optional |
| `dolt_branches` | List branches | No | No | Yes (for remote) |
| `dolt_commits` | List commits | No | No | No |
| `dolt_show` | Show commit details | No | No | No |
| `dolt_find` | Search commits | No | No | No |
| `dolt_init` | Initialize repo | Yes (import) | Yes | Optional |
| `dolt_clone` | Clone repo | Yes | Yes | Yes |
| `dolt_fetch` | Fetch updates | No | Yes (refs) | Yes |
| `dolt_pull` | Fetch + merge | Yes | Yes | Yes |
| `dolt_checkout` | Switch branch/commit | Yes | Yes | No |
| `dolt_reset` | Reset to commit | Yes | Yes | No |
| `dolt_commit` | Create commit | No | Yes | No |
| `dolt_push` | Push to remote | No | No | Yes |
| `dolt_link_git` | Link to Git | No | Yes | No |
