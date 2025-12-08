# Versioned RAG Architecture: Dolt + ChromaDB

## Overview

This design uses **Dolt as a versioned document store** (source of truth) and **ChromaDB for vector search** (retrieval layer). Dolt handles Git-flow branching, merging, and audit trails while ChromaDB handles all embedding storage and semantic queries.

**Key principle**: Dolt stores no vectors—only source documents and sync metadata. All vector operations happen in ChromaDB.

---

## 1. Dolt Schema Design

### Database Structure

sql

```sql
-- Initialize database
-- dolt init
-- dolt sql

-- ============================================
-- CORE DOCUMENT TABLES
-- ============================================

-- Projects table (for issue logs)
CREATE TABLE projects (
    project_id VARCHAR(36) PRIMARY KEY,
    name VARCHAR(255) NOT NULL,
    repository_url VARCHAR(500),
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    metadata JSON
);

-- Issue implementation logs
CREATE TABLE issue_logs (
    log_id VARCHAR(36) PRIMARY KEY,
    project_id VARCHAR(36) NOT NULL,
    issue_number INT NOT NULL,
    title VARCHAR(500),
    content LONGTEXT NOT NULL,
    content_hash CHAR(64) NOT NULL,  -- SHA-256 for change detection
    log_type ENUM('investigation', 'implementation', 'resolution', 'postmortem') DEFAULT 'implementation',
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    metadata JSON,  -- {author, related_files, tags, etc.}
    
    FOREIGN KEY (project_id) REFERENCES projects(project_id),
    UNIQUE KEY (project_id, issue_number, log_type),
    INDEX idx_content_hash (content_hash),
    INDEX idx_project_issue (project_id, issue_number)
);

-- Knowledge base documents (tool/API documentation)
CREATE TABLE knowledge_docs (
    doc_id VARCHAR(36) PRIMARY KEY,
    category VARCHAR(100) NOT NULL,  -- e.g., 'api', 'framework', 'tooling'
    tool_name VARCHAR(255) NOT NULL,  -- e.g., 'EntityFramework', 'Azure.Storage'
    tool_version VARCHAR(50),
    title VARCHAR(500) NOT NULL,
    content LONGTEXT NOT NULL,
    content_hash CHAR(64) NOT NULL,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    metadata JSON,  -- {source_url, author, tags, etc.}
    
    INDEX idx_content_hash (content_hash),
    INDEX idx_tool (tool_name, tool_version),
    INDEX idx_category (category)
);

-- ============================================
-- SYNC STATE TRACKING
-- ============================================

-- Tracks what's been synced to ChromaDB
CREATE TABLE chroma_sync_state (
    collection_name VARCHAR(255) PRIMARY KEY,
    last_sync_commit VARCHAR(40),  -- Dolt commit hash
    last_sync_at DATETIME,
    document_count INT DEFAULT 0,
    chunk_count INT DEFAULT 0,
    sync_status ENUM('synced', 'pending', 'error') DEFAULT 'pending',
    error_message TEXT,
    metadata JSON  -- {embedding_model, chunk_size, etc.}
);

-- Individual document sync tracking (for incremental updates)
CREATE TABLE document_sync_log (
    id INT AUTO_INCREMENT PRIMARY KEY,
    source_table ENUM('issue_logs', 'knowledge_docs') NOT NULL,
    source_id VARCHAR(36) NOT NULL,
    content_hash CHAR(64) NOT NULL,
    chroma_collection VARCHAR(255) NOT NULL,
    chunk_ids JSON,  -- Array of ChromaDB chunk IDs for this document
    synced_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    embedding_model VARCHAR(100),
    
    UNIQUE KEY (source_table, source_id, chroma_collection),
    INDEX idx_content_hash (content_hash)
);
```

### Generating Content Hashes

Use SHA-256 for deterministic change detection:

sql

```sql
-- When inserting/updating, compute hash from content
-- In your application code or stored procedure:

-- Example insert with hash
INSERT INTO issue_logs (log_id, project_id, issue_number, title, content, content_hash, log_type, metadata)
VALUES (
    UUID(),
    @project_id,
    @issue_number,
    @title,
    @content,
    SHA2(@content, 256),
    'implementation',
    @metadata
);

-- Example update that recalculates hash
UPDATE issue_logs 
SET content = @new_content,
    content_hash = SHA2(@new_content, 256),
    updated_at = CURRENT_TIMESTAMP
WHERE log_id = @log_id;
```

---

## 2. ChromaDB Collection Structure

### Collection Naming Convention

```
{database_type}_{identifier}_{branch}

Examples:
- issues_projectalpha_main
- issues_projectalpha_feature-123
- knowledge_entityframework_main
- knowledge_azurestorage_main
```

### Collection Schema (via metadata)

python

```python
# ChromaDB collection creation
import chromadb

client = chromadb.PersistentClient(path="./chroma_db")

# Issue logs collection
issues_collection = client.get_or_create_collection(
    name="issues_projectalpha_main",
    metadata={
        "source": "dolt",
        "source_table": "issue_logs",
        "project_id": "project-alpha-uuid",
        "dolt_branch": "main",
        "embedding_model": "text-embedding-3-small",
        "chunk_size": 512,
        "chunk_overlap": 50
    }
)

# Knowledge base collection
knowledge_collection = client.get_or_create_collection(
    name="knowledge_entityframework_main",
    metadata={
        "source": "dolt",
        "source_table": "knowledge_docs",
        "tool_name": "EntityFramework",
        "dolt_branch": "main",
        "embedding_model": "text-embedding-3-small",
        "chunk_size": 512,
        "chunk_overlap": 50
    }
)
```

### Document/Chunk Metadata

python

```python
# When adding chunks to ChromaDB, include tracking metadata
collection.add(
    ids=["chunk_001", "chunk_002"],
    documents=["First chunk text...", "Second chunk text..."],
    metadatas=[
        {
            "source_table": "issue_logs",
            "source_id": "log-uuid-here",
            "content_hash": "sha256-hash-here",
            "project_id": "project-alpha-uuid",
            "issue_number": 42,
            "log_type": "implementation",
            "chunk_index": 0,
            "total_chunks": 2,
            "dolt_commit": "abc123def456"
        },
        {
            "source_table": "issue_logs",
            "source_id": "log-uuid-here",
            "content_hash": "sha256-hash-here",
            "project_id": "project-alpha-uuid",
            "issue_number": 42,
            "log_type": "implementation",
            "chunk_index": 1,
            "total_chunks": 2,
            "dolt_commit": "abc123def456"
        }
    ]
)
```

---

## 3. Sync Workflow

### Sync Decision Logic

```
┌─────────────────────────────────────────────────────────────────┐
│                     SYNC TRIGGER                                 │
│  (manual, post-commit hook, or periodic)                        │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  1. Get current Dolt branch and commit hash                     │
│     SELECT DOLT_BRANCH(), DOLT_HASHOF('HEAD')                   │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  2. Compare with last synced commit                             │
│     SELECT last_sync_commit FROM chroma_sync_state              │
│     WHERE collection_name = '{branch_collection}'               │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
              ┌───────────────┴───────────────┐
              │ Same commit?                   │
              └───────────────┬───────────────┘
                    │                   │
                   YES                  NO
                    │                   │
                    ▼                   ▼
            ┌───────────┐    ┌─────────────────────┐
            │ No sync   │    │ 3. Find changed     │
            │ needed    │    │    documents        │
            └───────────┘    └─────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────┐
│  4. For each changed document:                                  │
│     a. Delete old chunks from ChromaDB (by source_id)           │
│     b. Chunk the new content                                    │
│     c. Generate embeddings                                      │
│     d. Insert new chunks to ChromaDB                            │
│     e. Update document_sync_log                                 │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  5. Update chroma_sync_state with new commit hash               │
└─────────────────────────────────────────────────────────────────┘
```

### Finding Changed Documents

sql

```sql
-- Find documents changed since last sync
-- Option A: Using content_hash comparison
SELECT 
    il.log_id as source_id,
    'issue_logs' as source_table,
    il.content,
    il.content_hash,
    il.metadata,
    CASE 
        WHEN dsl.content_hash IS NULL THEN 'new'
        WHEN dsl.content_hash != il.content_hash THEN 'modified'
    END as change_type
FROM issue_logs il
LEFT JOIN document_sync_log dsl 
    ON dsl.source_table = 'issue_logs' 
    AND dsl.source_id = il.log_id
    AND dsl.chroma_collection = @collection_name
WHERE dsl.content_hash IS NULL 
   OR dsl.content_hash != il.content_hash;

-- Option B: Using Dolt's diff (more efficient for large datasets)
SELECT * FROM DOLT_DIFF(@last_sync_commit, 'HEAD', 'issue_logs')
WHERE diff_type IN ('added', 'modified');

-- Find deleted documents
SELECT source_id 
FROM document_sync_log 
WHERE source_table = 'issue_logs'
  AND chroma_collection = @collection_name
  AND source_id NOT IN (SELECT log_id FROM issue_logs);
```

### Python Sync Implementation

python

```python
import hashlib
import uuid
from typing import List, Dict, Any
import mysql.connector
import chromadb
from chromadb.utils import embedding_functions

class DoltChromaSync:
    def __init__(self, dolt_config: dict, chroma_path: str, embedding_model: str = "text-embedding-3-small"):
        self.dolt = mysql.connector.connect(**dolt_config)
        self.chroma = chromadb.PersistentClient(path=chroma_path)
        self.embedding_fn = embedding_functions.OpenAIEmbeddingFunction(
            model_name=embedding_model
        )
        self.embedding_model = embedding_model
        self.chunk_size = 512
        self.chunk_overlap = 50
    
    def get_current_branch_and_commit(self) -> tuple[str, str]:
        cursor = self.dolt.cursor()
        cursor.execute("SELECT active_branch()")
        branch = cursor.fetchone()[0]
        cursor.execute("SELECT DOLT_HASHOF('HEAD')")
        commit = cursor.fetchone()[0]
        return branch, commit
    
    def get_collection_name(self, source_table: str, identifier: str, branch: str) -> str:
        # Sanitize for ChromaDB naming rules
        safe_id = identifier.replace("-", "").replace("_", "")[:20]
        safe_branch = branch.replace("/", "-").replace("_", "-")[:20]
        return f"{source_table}_{safe_id}_{safe_branch}"
    
    def get_or_create_collection(self, name: str, metadata: dict):
        return self.chroma.get_or_create_collection(
            name=name,
            embedding_function=self.embedding_fn,
            metadata=metadata
        )
    
    def chunk_text(self, text: str) -> List[str]:
        """Simple chunking - replace with your preferred strategy"""
        chunks = []
        start = 0
        while start < len(text):
            end = start + self.chunk_size
            chunk = text[start:end]
            chunks.append(chunk)
            start = end - self.chunk_overlap
        return chunks
    
    def find_changed_documents(self, source_table: str, collection_name: str) -> List[Dict[str, Any]]:
        cursor = self.dolt.cursor(dictionary=True)
        
        if source_table == "issue_logs":
            query = """
                SELECT 
                    il.log_id as source_id,
                    il.content,
                    il.content_hash,
                    il.project_id,
                    il.issue_number,
                    il.log_type,
                    il.title,
                    il.metadata,
                    CASE 
                        WHEN dsl.content_hash IS NULL THEN 'new'
                        WHEN dsl.content_hash != il.content_hash THEN 'modified'
                    END as change_type
                FROM issue_logs il
                LEFT JOIN document_sync_log dsl 
                    ON dsl.source_table = 'issue_logs' 
                    AND dsl.source_id = il.log_id
                    AND dsl.chroma_collection = %s
                WHERE dsl.content_hash IS NULL 
                   OR dsl.content_hash != il.content_hash
            """
        else:  # knowledge_docs
            query = """
                SELECT 
                    kd.doc_id as source_id,
                    kd.content,
                    kd.content_hash,
                    kd.category,
                    kd.tool_name,
                    kd.tool_version,
                    kd.title,
                    kd.metadata,
                    CASE 
                        WHEN dsl.content_hash IS NULL THEN 'new'
                        WHEN dsl.content_hash != kd.content_hash THEN 'modified'
                    END as change_type
                FROM knowledge_docs kd
                LEFT JOIN document_sync_log dsl 
                    ON dsl.source_table = 'knowledge_docs' 
                    AND dsl.source_id = kd.doc_id
                    AND dsl.chroma_collection = %s
                WHERE dsl.content_hash IS NULL 
                   OR dsl.content_hash != kd.content_hash
            """
        
        cursor.execute(query, (collection_name,))
        return cursor.fetchall()
    
    def find_deleted_documents(self, source_table: str, collection_name: str) -> List[str]:
        cursor = self.dolt.cursor()
        
        id_column = "log_id" if source_table == "issue_logs" else "doc_id"
        query = f"""
            SELECT source_id 
            FROM document_sync_log 
            WHERE source_table = %s
              AND chroma_collection = %s
              AND source_id NOT IN (SELECT {id_column} FROM {source_table})
        """
        cursor.execute(query, (source_table, collection_name))
        return [row[0] for row in cursor.fetchall()]
    
    def sync_collection(self, source_table: str, identifier: str) -> dict:
        """
        Main sync method. Call this after Dolt commits or branch switches.
        Returns sync statistics.
        """
        branch, commit = self.get_current_branch_and_commit()
        collection_name = self.get_collection_name(source_table, identifier, branch)
        
        # Get or create ChromaDB collection
        collection = self.get_or_create_collection(collection_name, {
            "source": "dolt",
            "source_table": source_table,
            "identifier": identifier,
            "dolt_branch": branch,
            "embedding_model": self.embedding_model
        })
        
        stats = {"added": 0, "modified": 0, "deleted": 0, "chunks": 0}
        
        # Handle deletions first
        deleted_ids = self.find_deleted_documents(source_table, collection_name)
        for source_id in deleted_ids:
            # Get chunk IDs from sync log
            cursor = self.dolt.cursor()
            cursor.execute(
                "SELECT chunk_ids FROM document_sync_log WHERE source_table=%s AND source_id=%s AND chroma_collection=%s",
                (source_table, source_id, collection_name)
            )
            row = cursor.fetchone()
            if row and row[0]:
                chunk_ids = json.loads(row[0])
                collection.delete(ids=chunk_ids)
            
            # Remove from sync log
            cursor.execute(
                "DELETE FROM document_sync_log WHERE source_table=%s AND source_id=%s AND chroma_collection=%s",
                (source_table, source_id, collection_name)
            )
            stats["deleted"] += 1
        
        # Handle new and modified documents
        changed_docs = self.find_changed_documents(source_table, collection_name)
        
        for doc in changed_docs:
            source_id = doc["source_id"]
            
            # If modified, delete old chunks first
            if doc["change_type"] == "modified":
                cursor = self.dolt.cursor()
                cursor.execute(
                    "SELECT chunk_ids FROM document_sync_log WHERE source_table=%s AND source_id=%s AND chroma_collection=%s",
                    (source_table, source_id, collection_name)
                )
                row = cursor.fetchone()
                if row and row[0]:
                    old_chunk_ids = json.loads(row[0])
                    collection.delete(ids=old_chunk_ids)
                stats["modified"] += 1
            else:
                stats["added"] += 1
            
            # Chunk and embed
            chunks = self.chunk_text(doc["content"])
            chunk_ids = [f"{source_id}_chunk_{i}" for i in range(len(chunks))]
            
            # Build metadata for each chunk
            base_metadata = {
                "source_table": source_table,
                "source_id": source_id,
                "content_hash": doc["content_hash"],
                "dolt_commit": commit,
                "dolt_branch": branch
            }
            
            # Add source-specific metadata
            if source_table == "issue_logs":
                base_metadata.update({
                    "project_id": doc["project_id"],
                    "issue_number": doc["issue_number"],
                    "log_type": doc["log_type"],
                    "title": doc["title"]
                })
            else:
                base_metadata.update({
                    "category": doc["category"],
                    "tool_name": doc["tool_name"],
                    "tool_version": doc.get("tool_version", ""),
                    "title": doc["title"]
                })
            
            chunk_metadatas = []
            for i, chunk in enumerate(chunks):
                meta = base_metadata.copy()
                meta["chunk_index"] = i
                meta["total_chunks"] = len(chunks)
                chunk_metadatas.append(meta)
            
            # Add to ChromaDB
            collection.add(
                ids=chunk_ids,
                documents=chunks,
                metadatas=chunk_metadatas
            )
            stats["chunks"] += len(chunks)
            
            # Update sync log
            cursor = self.dolt.cursor()
            cursor.execute("""
                INSERT INTO document_sync_log 
                    (source_table, source_id, content_hash, chroma_collection, chunk_ids, embedding_model)
                VALUES (%s, %s, %s, %s, %s, %s)
                ON DUPLICATE KEY UPDATE 
                    content_hash = VALUES(content_hash),
                    chunk_ids = VALUES(chunk_ids),
                    synced_at = CURRENT_TIMESTAMP
            """, (
                source_table,
                source_id,
                doc["content_hash"],
                collection_name,
                json.dumps(chunk_ids),
                self.embedding_model
            ))
        
        # Update sync state
        cursor = self.dolt.cursor()
        cursor.execute("""
            INSERT INTO chroma_sync_state 
                (collection_name, last_sync_commit, last_sync_at, document_count, chunk_count, sync_status)
            VALUES (%s, %s, NOW(), %s, %s, 'synced')
            ON DUPLICATE KEY UPDATE
                last_sync_commit = VALUES(last_sync_commit),
                last_sync_at = VALUES(last_sync_at),
                document_count = document_count + %s - %s,
                chunk_count = VALUES(chunk_count),
                sync_status = 'synced'
        """, (
            collection_name,
            commit,
            stats["added"],
            stats["chunks"],
            stats["added"],
            stats["deleted"]
        ))
        
        self.dolt.commit()
        return stats
```

---

## 4. Git Flow Integration

### Branch Switching Workflow

```
┌─────────────────────────────────────────────────────────────────┐
│  Developer: git checkout feature/issue-123                      │
│             dolt checkout feature/issue-123                     │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  MCP Server detects branch change                               │
│  Checks if ChromaDB collection exists for this branch           │
└─────────────────────────────────────────────────────────────────┘
                              │
              ┌───────────────┴───────────────┐
              │ Collection exists?             │
              └───────────────┬───────────────┘
                    │                   │
                   YES                  NO
                    │                   │
                    ▼                   ▼
    ┌─────────────────────┐  ┌─────────────────────────┐
    │ Check if sync needed│  │ Create new collection   │
    │ (commit comparison) │  │ Full sync from Dolt     │
    └─────────────────────┘  └─────────────────────────┘
                    │                   │
                    └───────────┬───────┘
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│  Point MCP queries to branch-specific collection                │
│  issues_projectalpha_feature-issue-123                          │
└─────────────────────────────────────────────────────────────────┘
```

### Branch Management Commands

sql

```sql
-- Create feature branch (Dolt)
CALL DOLT_CHECKOUT('-b', 'feature/issue-123');

-- Work on branch, make commits
INSERT INTO issue_logs (...) VALUES (...);
CALL DOLT_ADD('issue_logs');
CALL DOLT_COMMIT('-m', 'Added investigation log for issue 123');

-- Sync to branch-specific ChromaDB collection
-- (triggered by MCP server or manual call)

-- Merge back to main
CALL DOLT_CHECKOUT('main');
CALL DOLT_MERGE('feature/issue-123');

-- Post-merge: sync main collection
-- Optionally: delete feature branch collection to save space
```

### Handling Merge Conflicts

Dolt handles document-level merge conflicts in SQL:

sql

```sql
-- Check for conflicts after merge attempt
SELECT * FROM dolt_conflicts_issue_logs;

-- Resolve by choosing a version
CALL DOLT_CONFLICTS_RESOLVE('--theirs', 'issue_logs');
-- or
CALL DOLT_CONFLICTS_RESOLVE('--ours', 'issue_logs');
-- or manually edit and resolve

-- After resolution, commit and sync
CALL DOLT_COMMIT('-m', 'Resolved merge conflicts');
-- Trigger ChromaDB sync for affected documents
```

---

## 5. MCP Server Design Outline

### Tool Categories

```
┌─────────────────────────────────────────────────────────────────┐
│                    MCP SERVER TOOLS                              │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  DOCUMENT MANAGEMENT (Dolt)                                      │
│  ├── add_issue_log(project, issue, content, type)               │
│  ├── update_issue_log(log_id, content)                          │
│  ├── get_issue_log(log_id)                                      │
│  ├── list_issue_logs(project, filters)                          │
│  ├── add_knowledge_doc(tool, title, content, category)          │
│  ├── update_knowledge_doc(doc_id, content)                      │
│  └── delete_document(table, id)                                 │
│                                                                  │
│  VERSION CONTROL (Dolt)                                          │
│  ├── commit(message)                                            │
│  ├── create_branch(name)                                        │
│  ├── switch_branch(name)                                        │
│  ├── merge_branch(source, target)                               │
│  ├── get_branch_status()                                        │
│  ├── get_diff(from_ref, to_ref, table)                          │
│  ├── get_log(limit)                                             │
│  └── resolve_conflicts(table, strategy)                         │
│                                                                  │
│  VECTOR SEARCH (ChromaDB)                                        │
│  ├── search_issues(query, project, filters, limit)              │
│  ├── search_knowledge(query, tool, category, limit)             │
│  └── search_all(query, limit)                                   │
│                                                                  │
│  SYNC MANAGEMENT                                                 │
│  ├── sync_collection(source_table, identifier)                  │
│  ├── sync_all()                                                 │
│  ├── get_sync_status()                                          │
│  └── force_full_resync(collection)                              │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### MCP Server Skeleton (TypeScript)

typescript

```typescript
// mcp-server/src/index.ts
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import mysql from "mysql2/promise";
import { ChromaClient } from "chromadb";

interface ServerConfig {
  dolt: {
    host: string;
    port: number;
    user: string;
    database: string;
  };
  chroma: {
    path: string;
  };
  embedding: {
    model: string;
    apiKey: string;
  };
}

class VersionedRagServer {
  private server: Server;
  private dolt: mysql.Connection;
  private chroma: ChromaClient;
  private currentBranch: string = "main";

  constructor(config: ServerConfig) {
    this.server = new Server(
      { name: "versioned-rag", version: "1.0.0" },
      { capabilities: { tools: {} } }
    );
    
    this.setupTools();
  }

  private setupTools() {
    this.server.setRequestHandler("tools/list", async () => ({
      tools: [
        // Document Management
        {
          name: "add_issue_log",
          description: "Add a new issue implementation log",
          inputSchema: {
            type: "object",
            properties: {
              project_id: { type: "string" },
              issue_number: { type: "number" },
              title: { type: "string" },
              content: { type: "string" },
              log_type: { 
                type: "string", 
                enum: ["investigation", "implementation", "resolution", "postmortem"]
              }
            },
            required: ["project_id", "issue_number", "content"]
          }
        },
        {
          name: "search_issues",
          description: "Semantic search across issue logs",
          inputSchema: {
            type: "object",
            properties: {
              query: { type: "string" },
              project_id: { type: "string" },
              limit: { type: "number", default: 5 }
            },
            required: ["query"]
          }
        },
        {
          name: "switch_branch",
          description: "Switch to a different branch (affects both Dolt and ChromaDB collection)",
          inputSchema: {
            type: "object",
            properties: {
              branch: { type: "string" },
              create: { type: "boolean", default: false }
            },
            required: ["branch"]
          }
        },
        {
          name: "commit",
          description: "Commit current changes to Dolt and sync to ChromaDB",
          inputSchema: {
            type: "object",
            properties: {
              message: { type: "string" },
              sync: { type: "boolean", default: true }
            },
            required: ["message"]
          }
        },
        // ... more tools
      ]
    }));

    this.server.setRequestHandler("tools/call", async (request) => {
      const { name, arguments: args } = request.params;
      
      switch (name) {
        case "add_issue_log":
          return this.addIssueLog(args);
        case "search_issues":
          return this.searchIssues(args);
        case "switch_branch":
          return this.switchBranch(args);
        case "commit":
          return this.commit(args);
        // ... more handlers
      }
    });
  }

  private async addIssueLog(args: any) {
    const { project_id, issue_number, title, content, log_type } = args;
    const contentHash = this.sha256(content);
    const logId = this.uuid();

    await this.dolt.execute(
      `INSERT INTO issue_logs 
       (log_id, project_id, issue_number, title, content, content_hash, log_type)
       VALUES (?, ?, ?, ?, ?, ?, ?)`,
      [logId, project_id, issue_number, title || "", content, contentHash, log_type || "implementation"]
    );

    return {
      content: [{ 
        type: "text", 
        text: `Created issue log ${logId}. Run 'commit' to save and sync.` 
      }]
    };
  }

  private async searchIssues(args: any) {
    const { query, project_id, limit = 5 } = args;
    const collectionName = this.getCollectionName("issue_logs", project_id);
    
    const collection = await this.chroma.getCollection({ name: collectionName });
    const results = await collection.query({
      queryTexts: [query],
      nResults: limit,
      where: project_id ? { project_id } : undefined
    });

    // Format results for Claude
    const formatted = results.documents[0].map((doc, i) => ({
      content: doc,
      metadata: results.metadatas[0][i],
      distance: results.distances?.[0][i]
    }));

    return {
      content: [{ 
        type: "text", 
        text: JSON.stringify(formatted, null, 2) 
      }]
    };
  }

  private async switchBranch(args: any) {
    const { branch, create } = args;
    
    if (create) {
      await this.dolt.execute(`CALL DOLT_CHECKOUT('-b', ?)`, [branch]);
    } else {
      await this.dolt.execute(`CALL DOLT_CHECKOUT(?)`, [branch]);
    }
    
    this.currentBranch = branch;
    
    // Ensure ChromaDB collection exists and is synced
    await this.ensureCollectionSynced("issue_logs", branch);
    await this.ensureCollectionSynced("knowledge_docs", branch);

    return {
      content: [{ 
        type: "text", 
        text: `Switched to branch '${branch}'. ChromaDB collections synced.` 
      }]
    };
  }

  private async commit(args: any) {
    const { message, sync = true } = args;
    
    await this.dolt.execute(`CALL DOLT_ADD('-A')`);
    await this.dolt.execute(`CALL DOLT_COMMIT('-m', ?)`, [message]);
    
    let syncResult = "";
    if (sync) {
      const stats = await this.syncCurrentBranch();
      syncResult = ` Synced: ${stats.added} added, ${stats.modified} modified, ${stats.deleted} deleted.`;
    }

    return {
      content: [{ 
        type: "text", 
        text: `Committed: "${message}"${syncResult}` 
      }]
    };
  }

  // Helper methods
  private sha256(content: string): string {
    return require("crypto").createHash("sha256").update(content).digest("hex");
  }

  private uuid(): string {
    return require("crypto").randomUUID();
  }

  private getCollectionName(table: string, identifier: string): string {
    const safeBranch = this.currentBranch.replace(/[\/\_]/g, "-").substring(0, 20);
    const safeId = (identifier || "default").replace(/[\-\_]/g, "").substring(0, 20);
    return `${table}_${safeId}_${safeBranch}`;
  }
}

// Start server
const server = new VersionedRagServer(config);
const transport = new StdioServerTransport();
server.connect(transport);
```

### Claude CLI Configuration

json

```json
// claude_desktop_config.json or .claude.json
{
  "mcpServers": {
    "versioned-rag": {
      "command": "node",
      "args": ["/path/to/mcp-server/dist/index.js"],
      "env": {
        "DOLT_HOST": "localhost",
        "DOLT_PORT": "3306",
        "DOLT_USER": "root",
        "DOLT_DATABASE": "rag_memory",
        "CHROMA_PATH": "/path/to/chroma_db",
        "OPENAI_API_KEY": "sk-..."
      }
    }
  }
}
```

---

## 6. Usage Example: Complete Workflow

```
Developer: Starting work on feature/auth-refactor

Claude CLI:
> switch_branch branch="feature/auth-refactor" create=true
Switched to branch 'feature/auth-refactor'. ChromaDB collections synced.

Developer: After investigating the auth issue...

> add_issue_log project_id="proj-123" issue_number=456 content="Investigation: The JWT validation is failing because..." log_type="investigation"
Created issue log abc-123. Run 'commit' to save and sync.

> commit message="Added investigation notes for auth token issue"
Committed: "Added investigation notes for auth token issue" Synced: 1 added, 0 modified, 0 deleted.

Developer: Later, searching for related issues...

> search_issues query="JWT token validation expiration" project_id="proj-123"
[
  {
    "content": "Investigation: The JWT validation is failing because...",
    "metadata": {
      "issue_number": 456,
      "log_type": "investigation"
    },
    "distance": 0.23
  },
  {
    "content": "Resolution: Fixed token refresh by implementing...",
    "metadata": {
      "issue_number": 389,
      "log_type": "resolution"  
    },
    "distance": 0.41
  }
]

Developer: Ready to merge...

> switch_branch branch="main"
> merge_branch source="feature/auth-refactor"
Merged feature/auth-refactor into main. ChromaDB main collection synced.
```

---

## 7. Deployment Checklist

- [ ]  Install Dolt and initialize database with schema
- [ ]  Configure Dolt as Windows service (NSSM) or Docker
- [ ]  Set up ChromaDB persistent storage location
- [ ]  Configure embedding API (OpenAI/local model)
- [ ]  Build and deploy MCP server
- [ ]  Add MCP server to Claude CLI configuration
- [ ]  Create initial projects and sync
- [ ]  Document team workflow (when to commit, branch naming)
- [ ]  Set up backup strategy for both Dolt data directory and ChromaDB storage