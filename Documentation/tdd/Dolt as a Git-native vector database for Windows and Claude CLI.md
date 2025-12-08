Dolt offers a unique proposition for LLM memory: **version-controlled vector storage with full SQL capabilities**. The database now has an official MCP server [GitHub](https://github.com/dolthub/dolt-mcp) for Claude CLI integration and alpha vector support (v1.47.0+), [DoltHub](https://www.dolthub.com/blog/2025-01-16-announcing-vector-indexes/) but remains less mature than purpose-built vector databases like ChromaDB. For applications requiring embedding versioning, audit trails, or collaborative development workflows, Dolt provides capabilities no other vector database offers.

## Windows installation is straightforward with multiple options

Dolt is a single ~100MB executable [GitHub](https://github.com/dolthub/dolt) requiring minimal configuration. [Dolthub](https://docs.dolthub.com/introduction/installation) **Winget** provides the simplest installation on Windows 11:

powershell

```powershell
winget install dolt
```

[dolthub](https://docs.dolthub.com/introduction/installation/windows)

For enterprise environments, **Chocolatey** offers automation-friendly package management: [ComputingForGeeks](https://computingforgeeks.com/install-and-use-dolt-git-for-sql-database/)

powershell

```powershell
choco install dolt
```

[dolthub](https://docs.dolthub.com/introduction/installation/windows)

Developers without admin rights can use **Scoop**, which installs to user directories: [DEV Community](https://dev.to/bowmanjd/chocolatey-vs-scoop-package-managers-for-windows-2kik)

powershell

```powershell
scoop install dolt
```

After installation, verify and configure:

powershell

```powershell
dolt version
dolt config --global --add user.email "your@email.com"
dolt config --global --add user.name "Your Name"
```

[GitHub](https://github.com/dolthub/dolt)

To initialize a database and start the SQL server:

powershell

```powershell
mkdir C:\dolt\databases\mydb
cd C:\dolt\databases\mydb
dolt init
dolt sql-server -H localhost -P 3306
```

Windows-specific considerations include the lack of Unix socket support (use TCP connections only) and potential port 3306 conflicts with MySQL installations.

### Running Dolt as a Windows service

Use **NSSM (Non-Sucking Service Manager)** for reliable service deployment: [XDA Developers](https://www.xda-developers.com/nssm-service-automation-windows-pc/)

powershell

```powershell
choco install nssm
nssm install DoltServer "C:\path\to\dolt.exe" "sql-server --config=C:\dolt\config.yaml"
nssm set DoltServer AppDirectory "C:\dolt\databases"
nssm set DoltServer Start SERVICE_AUTO_START
nssm start DoltServer
```

## Official MCP server enables Claude CLI integration

An **official Dolt MCP server** exists [GitHub](https://github.com/modelcontextprotocol/servers) at `github.com/dolthub/dolt-mcp`, maintained by DoltHub. It exposes **40+ tools** covering database management, table operations, version control, and remote repository operations. [GitHub](https://github.com/dolthub/dolt-mcp)[github](https://github.com/dolthub/dolt-mcp)

### Connecting Dolt to Claude CLI

Build from source or use Docker:

bash

```bash
git clone https://github.com/dolthub/dolt-mcp
cd dolt-mcp
go build -o dolt-mcp-server ./mcp/cmd/dolt-mcp-server
```

Add to your Claude configuration (`.claude.json` or `claude_desktop_config.json`):

json

```json
{
  "mcpServers": {
    "dolt-mcp": {
      "command": "/path/to/dolt-mcp-server",
      "args": [
        "--stdio",
        "--dolt-host", "localhost",
        "--dolt-port", "3306",
        "--dolt-user", "root",
        "--dolt-database", "your_database"
      ]
    }
  }
}
```

[github](https://github.com/dolthub/dolt-mcp)

For HTTP mode (remote access), start the server with `--http --mcp-port 8080`, then register with:

bash

```bash
claude mcp add -t http dolt_db http://localhost:8080/mcp
```

[dolthub](https://www.dolthub.com/blog/2025-08-20-does-dolt-need-mcp/)

### Configuring multiple databases

Add separate MCP server entries for each database:

json

```json
{
  "mcpServers": {
    "dolt-production": {
      "command": "/path/to/dolt-mcp-server",
      "args": ["--stdio", "--dolt-host", "prod.example.com", "--dolt-database", "prod_db"]
    },
    "dolt-development": {
      "command": "/path/to/dolt-mcp-server",
      "args": ["--stdio", "--dolt-host", "localhost", "--dolt-database", "dev_db"]
    }
  }
}
```

### Alternative integration methods

If MCP isn't suitable, Dolt supports standard MySQL connections: [Dolthub](https://docs.dolthub.com/introduction/getting-started/database)

python

```python
import mysql.connector
conn = mysql.connector.connect(host="127.0.0.1", port=3306, user="root", database="mydb")
cursor = conn.cursor()
cursor.execute("SELECT * FROM my_table")
```

The `doltpy` Python library exists but is **currently inactive** (no updates in 12+ months)—direct MySQL connectors are recommended for production. [Snyk](https://snyk.io/advisor/python/doltpy)

## Vector support comparison with ChromaDB

Dolt added **vector indexes as an alpha feature in v1.47.0** (January 2025). [dolthub](https://www.dolthub.com/blog/2025-01-16-announcing-vector-indexes/) Vectors are stored in JSON columns with a dedicated `VECTOR(N)` type under development. [dolthub](https://www.dolthub.com/blog/2025-08-08-vector-type/)

|Capability|Dolt|ChromaDB|
|---|---|---|
|**Maturity**|Alpha (2025)|Production-ready|
|**Query interface**|SQL|Python/JS SDK|
|**Distance metrics**|Euclidean (cosine planned)|Cosine, L2, inner product|
|**Auto-embedding**|No|Yes (sentence-transformers)|
|**LangChain/LlamaIndex**|No native support|First-class integration|
|**Version control**|Full Git-style|None|
|**Metadata filtering**|SQL WHERE clauses|Key-value filters|

### Creating vector tables in Dolt

sql

```sql
CREATE TABLE embeddings (
    id INT PRIMARY KEY,
    content TEXT,
    embedding JSON,
    metadata JSON,
    VECTOR INDEX emb_idx(embedding)
);

-- Similarity search
SELECT content, VEC_DISTANCE(embedding, @query_vector) as distance
FROM embeddings
ORDER BY VEC_DISTANCE(embedding, @query_vector)
LIMIT 10;
```

[dolthub](https://www.dolthub.com/blog/2025-01-16-announcing-vector-indexes/)

**Performance characteristics**: Index building is slow (~2.5 hours for 139K × 768-dimensional vectors), [dolthub](https://www.dolthub.com/blog/2025-01-16-announcing-vector-indexes/) with queries taking ~5 seconds on similar datasets. [DoltHub](https://www.dolthub.com/blog/2025-01-16-announcing-vector-indexes/) ChromaDB delivers sub-second queries due to optimized HNSWLib implementation and SIMD acceleration. [Medium](https://dev523.medium.com/chromadb-vs-pgvector-the-epic-battle-of-vector-databases-a43216772b34)

### When to choose each database

**Choose Dolt** when version control is critical—comparing embedding models across branches, maintaining audit trails for compliance, or collaboratively developing knowledge bases with review workflows. [dolthub](https://www.dolthub.com/blog/2025-01-16-announcing-vector-indexes/)

**Choose ChromaDB** for rapid prototyping, LangChain/LlamaIndex projects, or production RAG pipelines requiring auto-embedding and sub-second retrieval. ChromaDB's 4-function API gets you running in minutes:

python

```python
collection = client.create_collection("memory")
collection.add(documents=["text"], ids=["id1"])
results = collection.query(query_texts=["search"], n_results=5)
```

[GitHub](https://github.com/Byadab/chromadb)

## Schema design for LLM memory and knowledge bases

### Conversation memory schema

sql

```sql
CREATE TABLE conversations (
    conversation_id VARCHAR(36) PRIMARY KEY,
    user_id VARCHAR(36),
    created_at DATETIME,
    metadata JSON
);

CREATE TABLE messages (
    message_id VARCHAR(36) PRIMARY KEY,
    conversation_id VARCHAR(36),
    role ENUM('user', 'assistant', 'system'),
    content LONGTEXT,
    embedding JSON,
    created_at DATETIME,
    FOREIGN KEY (conversation_id) REFERENCES conversations(conversation_id),
    VECTOR INDEX msg_emb_idx(embedding)
);
```

### Knowledge base with RAG support

sql

```sql
CREATE TABLE documents (
    doc_id VARCHAR(36) PRIMARY KEY,
    title VARCHAR(255),
    source_url VARCHAR(500),
    content LONGTEXT,
    metadata JSON
);

CREATE TABLE chunks (
    chunk_id VARCHAR(36) PRIMARY KEY,
    doc_id VARCHAR(36),
    content TEXT,
    embedding JSON,
    VECTOR INDEX chunk_idx(embedding)
);

-- RAG query with metadata filtering
SELECT c.content, d.title
FROM chunks c JOIN documents d ON c.doc_id = d.doc_id
WHERE d.metadata->>'$.category' = 'technical'
ORDER BY VEC_DISTANCE(c.embedding, @query) LIMIT 5;
```

## Branching workflows enable unique LLM development patterns

Dolt's Git-like features provide capabilities unavailable in any other vector database. [GitHub](https://github.com/dolthub/dolt)

### Agent sandbox pattern

Isolate AI agent modifications for human review before merging to production: [DoltHub](https://www.dolthub.com/blog/2025-10-31-agentic-systems-need-version-control/)[DoltHub](https://www.dolthub.com/blog/2025-03-17-dolt-agentic-workflows/)

sql

```sql
-- Agent operates on isolated branch
CALL DOLT_CHECKOUT('-b', 'agent_session_001');
INSERT INTO knowledge_base (content, embedding) VALUES (@new_content, @embedding);

-- Review changes
SELECT * FROM DOLT_DIFF('main', 'agent_session_001', 'knowledge_base');

-- Approve and merge
CALL DOLT_CHECKOUT('main');
CALL DOLT_MERGE('agent_session_001');
```

### Embedding model comparison

Test different embedding models side-by-side:

bash

```bash
# Branch for each embedding model
dolt checkout -b openai-embeddings
python generate_embeddings.py --model text-embedding-3-large

dolt checkout -b cohere-embeddings  
python generate_embeddings.py --model embed-english-v3

# Compare retrieval quality
dolt diff openai-embeddings cohere-embeddings
```

[dolthub](https://www.dolthub.com/blog/2025-02-06-getting-started-dolt-vectors/)

### Training data versioning

Tag exact dataset versions for reproducibility:

bash

```bash
dolt tag v1.0-training-data
MODEL_DATA_VERSION=$(dolt log -n1 --format="%h")
python train_model.py --data-version $MODEL_DATA_VERSION
```

## Practical recommendations for Claude CLI memory

For most **quick prototyping and RAG applications**, ChromaDB remains more practical—native framework integrations, auto-embedding, and faster queries outweigh Dolt's version control benefits. [Kali Linux](https://kalilinuxtutorials.com/chroma/)

Dolt becomes compelling for **production LLM applications requiring**:

- **Audit trails** for regulated industries (finance, healthcare)
- **Collaborative development** with pull request workflows for knowledge bases
- **Embedding experimentation** comparing models across branches
- **Hybrid workloads** mixing vector search with complex SQL joins
- **Instant rollback** when AI agents make mistakes [dolthub](https://www.dolthub.com/blog/2025-02-06-getting-started-dolt-vectors/)[DoltHub](https://www.dolthub.com/blog/2025-10-31-agentic-systems-need-version-control/)

The official MCP server makes Dolt practical for Claude CLI integration today. [dolthub](https://www.dolthub.com/blog/2025-08-20-does-dolt-need-mcp/) While vector performance lags behind purpose-built databases, Dolt's unique version control capabilities solve problems no other vector database addresses—the ability to branch, diff, and merge your LLM's memory is genuinely novel. [dolthub](https://www.dolthub.com/blog/2025-01-16-announcing-vector-indexes/)

## Key limitations to consider

- Vector indexes remain **alpha quality** with slow index builds
- **No dedicated VECTOR data type** yet (uses JSON serialization)
- **No LangChain or LlamaIndex integration**—requires custom implementation
- Only **Euclidean distance** currently supported (cosine coming)
- Write operations serialize, limiting high-throughput scenarios [LakeFS](https://lakefs.io/blog/dvc-vs-git-vs-dolt-vs-lakefs/)[Dolthub](https://docs.dolthub.com/sql-reference/server/troubleshooting)

For production vector workloads at scale, consider Dolt for versioning alongside a dedicated vector database for retrieval—use Dolt as the source of truth with ChromaDB or Pinecone as a synchronized retrieval layer.