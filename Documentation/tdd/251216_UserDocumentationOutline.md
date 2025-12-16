# VM RAG MCP Server - User Documentation Outline

**Document Version**: 1.0  
**Date**: December 13, 2025  
**Purpose**: Guide for users and LLMs on interacting with the version-controlled knowledge base

---

## Table of Contents

1. [Introduction](#1-introduction)
2. [Quick Start Guide](#2-quick-start-guide)
3. [Tool Reference](#3-tool-reference)
4. [LLM Interaction Guidelines](#4-llm-interaction-guidelines)
5. [Common Workflows](#5-common-workflows)
6. [Limitations and Gotchas](#6-limitations-and-gotchas)
7. [Project Setup](#7-project-setup)
8. [Troubleshooting](#8-troubleshooting)

---

## 1. Introduction

### 1.1 What is the VM RAG MCP Server?

The VM RAG (Version Managed Retrieval-Augmented Generation) MCP Server provides a **version-controlled knowledge base** that LLMs can read from and write to. Think of it as "Git for your AI's knowledge."

**Key Concepts**:
- **ChromaDB**: Where documents live and are searchable (the "working copy")
- **Dolt**: Version control system that tracks changes (like Git for databases)
- **DoltHub**: Cloud hosting for sharing knowledge bases (like GitHub)

### 1.2 Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                         YOUR PROJECT                             │
│                                                                 │
│   ┌─────────────┐     ┌─────────────┐     ┌─────────────────┐  │
│   │    LLM      │────►│  MCP Server │────►│    ChromaDB     │  │
│   │  (Claude)   │     │             │     │  (documents)    │  │
│   └─────────────┘     └──────┬──────┘     └─────────────────┘  │
│                              │                                  │
│                              │ dolt_* tools                     │
│                              ▼                                  │
│                       ┌─────────────┐                          │
│                       │    Dolt     │                          │
│                       │  (versions) │                          │
│                       └──────┬──────┘                          │
│                              │                                  │
└──────────────────────────────┼──────────────────────────────────┘
                               │
                               ▼
                        ┌─────────────┐
                        │   DoltHub   │
                        │  (sharing)  │
                        └─────────────┘
```

### 1.3 Who This Documentation Is For

- **Users**: Setting up and managing their knowledge base
- **LLMs**: Understanding how to use the tools correctly
- **Developers**: Integrating the MCP server into their projects

---

## 2. Quick Start Guide

### 2.1 First-Time Setup

#### Option A: Start Fresh (New Knowledge Base)
```
User: "Initialize a new knowledge base for my project"

LLM Action: 
1. Call dolt_init(remote_url="myorg/my-knowledge-base", import_existing=true)
2. Confirm success
3. Knowledge base is ready for use
```

#### Option B: Join Existing (Team Knowledge Base)
```
User: "Connect to our team's knowledge base"

LLM Action:
1. Call dolt_clone(remote_url="team/shared-knowledge")
2. Confirm documents are loaded
3. Knowledge base is ready for use
```

### 2.2 Basic Workflow

```
1. Check status:       dolt_status()
2. Make changes:       [Use chroma_* tools to add/edit documents]
3. Save changes:       dolt_commit(message="Added API docs")
4. Share changes:      dolt_push()
5. Get team changes:   dolt_pull()
```

---

## 3. Tool Reference

### 3.1 Document Operations (ChromaDB Tools)

These tools are the LLM's primary interface for reading and writing documents.

#### Collection Tools

| Tool | Purpose | When to Use |
|------|---------|-------------|
| `chroma_list_collections` | List all collections | To discover what exists |
| `chroma_get_collection_info` | Get collection details | To understand configuration |
| `chroma_get_collection_count` | Count documents | Quick size check |
| `chroma_peek_collection` | View sample documents | To explore content |
| `chroma_create_collection` | Create new collection | Setting up new knowledge base |
| `chroma_modify_collection` | Update collection metadata | Renaming or updating settings |
| `chroma_delete_collection` | Delete a collection | ⚠️ Destructive - removes all docs |

#### Document Tools

| Tool | Purpose | When to Use |
|------|---------|-------------|
| `chroma_add_documents` | Add new documents | Adding knowledge |
| `chroma_query_documents` | Semantic search | **Primary RAG retrieval** |
| `chroma_get_documents` | Get docs by ID/filter | Retrieving specific documents |
| `chroma_update_documents` | Update existing docs | Correcting or refreshing content |
| `chroma_delete_documents` | Delete documents | Removing outdated content |

### 3.2 Version Control Information (Dolt Tools)

| Tool | Purpose | When to Use |
|------|---------|-------------|
| `dolt_status` | Current branch, commit, and local changes | Before any operation |
| `dolt_branches` | List available branches | Before checkout |
| `dolt_commits` | History of a branch | To review changes |
| `dolt_show` | Details of a specific commit | To inspect a version |
| `dolt_find` | Search for commits | When you know partial info |

### 3.3 Version Control Setup

| Tool | Purpose | When to Use |
|------|---------|-------------|
| `dolt_init` | Create new repository | Starting fresh |
| `dolt_clone` | Clone existing repository | Joining a project |

### 3.4 Version Control Sync

| Tool | Purpose | When to Use |
|------|---------|-------------|
| `dolt_fetch` | Check for remote updates | Before pulling |
| `dolt_pull` | Get and merge remote changes | To update from team |
| `dolt_push` | Upload local commits | To share your work |

### 3.5 Version Control Local Operations

| Tool | Purpose | When to Use |
|------|---------|-------------|
| `dolt_commit` | Save current state | After making changes |
| `dolt_checkout` | Switch branch or version | To change context |
| `dolt_reset` | Discard local changes | To undo uncommitted work |

---

## 4. LLM Interaction Guidelines

### 4.1 When to Use ChromaDB Tools (Document Operations)

**For reading/searching documents**:
- "What do we know about X?" → `chroma_query_documents`
- "Find documents about..." → `chroma_query_documents`
- "Show me the document with ID..." → `chroma_get_documents`
- "What's in the knowledge base?" → `chroma_peek_collection` or `chroma_list_collections`
- "How many documents do we have?" → `chroma_get_collection_count`

**For modifying documents**:
- "Add this to the knowledge base" → `chroma_add_documents`
- "Update the document about..." → `chroma_update_documents`
- "Delete the old documentation" → `chroma_delete_documents`
- "Mark this as deprecated" → `chroma_update_documents` (update metadata)

**Key Principle**: All document read/write goes through ChromaDB tools. These changes become "local changes" that can be committed with `dolt_commit`.

### 4.2 When to Use Dolt Tools (Version Control)

**ALWAYS call `dolt_status` first** when:
- User mentions branches, commits, or versions
- User asks about "changes" or "what's different"
- Before any push, pull, or checkout operation
- When unsure about current state

**Call version control tools when user says**:
- "Save my work" → `dolt_commit`
- "Share with team" → `dolt_push`
- "Get latest" / "Update" → `dolt_pull`
- "Switch to..." / "Use the ... branch" → `dolt_checkout`
- "What branches exist?" → `dolt_branches`
- "What changed?" → `dolt_status` then possibly `dolt_show`
- "Undo my changes" → `dolt_reset`

### 4.3 Decision Flow: ChromaDB vs Dolt

```
User Request
    │
    ├─── About document CONTENT? ──────────► ChromaDB tools
    │    (search, read, add, update, delete)
    │
    ├─── About VERSIONS/HISTORY? ──────────► Dolt tools
    │    (commits, branches, saving, sharing)
    │
    └─── Both? ────────────────────────────► ChromaDB first, then Dolt
         (e.g., "Add this doc and save it")
```

**Examples**:

| User Request | Tool(s) to Use |
|--------------|----------------|
| "What do we know about authentication?" | `chroma_query_documents` |
| "Add this REST API documentation" | `chroma_add_documents` |
| "Save my changes" | `dolt_commit` |
| "Add this doc and share with the team" | `chroma_add_documents` → `dolt_commit` → `dolt_push` |
| "What changed since yesterday?" | `dolt_commits` → `dolt_show` |
| "Get the latest from the team" | `dolt_pull` |
| "Find that commit where we added SSL docs" | `dolt_find` |

### 4.4 Handling Uncommitted Changes

When a user requests `pull`, `checkout`, or `reset` and there are uncommitted changes:

```
LLM Decision Tree:
1. Call the tool with default (if_uncommitted="abort")
2. If UNCOMMITTED_CHANGES error returned:
   a. Inform user: "You have X uncommitted changes"
   b. Ask: "Would you like me to:
      - Commit them first (recommended)
      - Discard them
      - Cancel the operation"
   c. Based on response, re-call with appropriate if_uncommitted value
```

**Example Dialogue**:
```
User: "Switch to the feature branch"
LLM: [calls dolt_checkout(target="feature/x")]
     [receives UNCOMMITTED_CHANGES error]

LLM: "You have 3 uncommitted changes (2 added, 1 modified). 
     Would you like me to:
     1. Commit these changes first with a message
     2. Discard these changes and switch
     3. Cancel and stay on the current branch"

User: "Commit them first"
LLM: [calls dolt_checkout(target="feature/x", if_uncommitted="commit_first", 
      commit_message="WIP: Changes before switching to feature/x")]
```

### 4.5 Commit Message Guidelines

When creating commit messages, the LLM should:

**DO**:
- Summarize what was changed: "Added documentation for REST API endpoints"
- Be specific: "Fixed typo in authentication guide"
- Use present tense: "Add", "Update", "Remove", "Fix"

**DON'T**:
- Be vague: "Made some changes"
- Be too long: Keep under 72 characters
- Include technical details the user didn't mention

**Auto-generating Commit Messages**:
```
When user says "save my work" without a message:

LLM should:
1. Call dolt_status(verbose=true) to see what changed
2. Generate appropriate message based on changes
3. Ask user to confirm or modify: 
   "I'll commit with message: 'Added 3 new API documentation pages'. 
    Would you like to change this?"
```

### 4.6 Error Handling

When tools return errors, the LLM should:

1. **Explain the error in plain language**
2. **Suggest solutions** from the `suggestions` array
3. **Offer to help** with the suggested action

**Example**:
```
Tool returns: { "error": "REMOTE_REJECTED", "message": "Push rejected..." }

LLM response: "The push was rejected because the remote has changes you 
don't have locally. Let me pull those changes first and then try pushing 
again. Shall I do that?"
```

### 4.7 When NOT to Use Version Control Tools

The LLM should **NOT** call version control tools when:

- User is just asking questions about document content (use chroma_query)
- User wants to read a document (use chroma_get)
- User wants to add/edit documents (use chroma_add, chroma_update)
- The question is about the content, not the versioning

**Document Operations vs Version Control**:
```
"What documents do we have about APIs?"     → chroma_query (NOT dolt_*)
"Add this new document about REST"          → chroma_add (NOT dolt_*)
"What changed since yesterday?"             → dolt_commits + dolt_show
"Who added the API docs?"                   → dolt_find or dolt_commits
"Save the document I just added"            → dolt_commit
```

---

## 5. Common Workflows

### 5.1 Daily Work Routine

```
Morning:
1. dolt_status()              # Check current state
2. dolt_pull()                # Get overnight changes from team

During day:
3. [Work with documents via chroma_* tools]

End of day:
4. dolt_status()              # See what you changed
5. dolt_commit(message="...")  # Save your work
6. dolt_push()                # Share with team
```

### 5.2 Feature Branch Workflow

```
Starting new feature:
1. dolt_checkout(target="feature/my-feature", create_branch=true)

Working on feature:
2. [Make document changes]
3. dolt_commit(message="WIP: Feature progress")

Finishing feature:
4. dolt_push()                           # Push feature branch
5. dolt_checkout(target="main")          # Switch back to main
6. dolt_pull()                           # Get latest main
```

### 5.3 Reviewing History

```
"What changed recently?"
1. dolt_commits(limit=10)     # See recent commits
2. dolt_show(commit="abc123") # Inspect specific commit

"Find when we added the API docs"
1. dolt_find(query="API", search_type="message")
2. dolt_show(commit=<found_hash>)
```

### 5.4 Recovering from Mistakes

```
"Undo everything I did today"
1. dolt_status()                              # Confirm local changes
2. dolt_reset(target="HEAD", confirm_discard=true)  # Discard uncommitted

"Go back to yesterday's version"
1. dolt_commits(limit=20)                     # Find yesterday's commit
2. dolt_checkout(target="abc123")             # Checkout that commit

"Reset to what's on the server"
1. dolt_fetch()                               # Get remote state
2. dolt_reset(target="origin/main", confirm_discard=true)
```

---

## 6. Limitations and Gotchas

### 6.1 Current Limitations

| Limitation | Description | Workaround |
|------------|-------------|------------|
| **No merge conflict resolution** | If pull/merge creates conflicts, manual intervention needed | Commit frequently, pull before starting new work |
| **Single collection per branch** | Each branch maps to one ChromaDB collection | Use naming conventions for organization |
| **No partial commits** | All changes are committed together | Commit frequently with descriptive messages |
| **Remote required for push/pull** | Need DoltHub account for sharing | Can work locally without remote |
| **No document-level history** | History is at commit level, not per-document | Use dolt_show to see document changes per commit |

### 6.2 Important Gotchas

#### Gotcha 1: Uncommitted Changes Block Operations
```
❌ Problem: Trying to pull/checkout with local changes
✅ Solution: Always check dolt_status() first, commit or discard changes
```

#### Gotcha 2: Push Requires Pull First
```
❌ Problem: Push rejected because remote has new commits
✅ Solution: Always dolt_pull() before dolt_push()
```

#### Gotcha 3: Checkout Switches Data
```
⚠️ Warning: dolt_checkout changes what documents are in ChromaDB
   Documents from other branches are not visible until you checkout that branch
```

#### Gotcha 4: Reset is Destructive
```
⚠️ Warning: dolt_reset discards uncommitted changes permanently
   Always confirm with user before calling with confirm_discard=true
```

#### Gotcha 5: Embeddings are Regenerated
```
ℹ️ Note: When switching branches or pulling, embeddings are regenerated
   This may take time for large document sets
```

### 6.3 Best Practices

1. **Commit Early, Commit Often**: Small, frequent commits are better than large, infrequent ones
2. **Pull Before Push**: Always get latest changes before pushing
3. **Descriptive Messages**: Future you will thank present you
4. **Check Status First**: Always know your current state before operations
5. **Use Branches**: Keep experimental work on branches, main should be stable

---

## 7. Project Setup

### 7.1 .gitignore Configuration

Add the following to your project's `.gitignore` to exclude the MCP server's data directories:

```gitignore
# ===========================================
# VM RAG MCP Server - Data Directories
# ===========================================

# Dolt repository (version control data)
# This is managed by Dolt, not Git
data/dolt-repo/
.dolt/

# ChromaDB persistence directory
# This is derived from Dolt and regenerated on sync
data/chroma-db/
chroma_data/

# Alternative common paths
.chroma/
*.chroma/

# ===========================================
# Dolt-specific files
# ===========================================

# Dolt configuration (may contain credentials)
.dolt/config.json
.dolt/creds/

# Dolt noms data
.dolt/noms/

# ===========================================
# Optional: If you want to track .dolt-version
# ===========================================
# Uncomment if using Git-Dolt linking and want to track the link file:
# !.dolt-version

# ===========================================
# MCP Server configuration
# ===========================================

# Local configuration overrides
appsettings.Local.json
appsettings.Development.json

# User secrets
secrets.json
*.secrets.json

# Environment files
.env
.env.local
.env.*.local
```

### 7.2 Directory Structure

Recommended project structure:
```
my-project/
├── .git/                      # Git repository (your code)
├── .gitignore                 # Includes Dolt/Chroma exclusions
├── src/                       # Your application code
├── data/                      # Data directory (gitignored)
│   ├── dolt-repo/            # Dolt repository
│   │   ├── .dolt/            # Dolt internals
│   │   └── ...               # Dolt data files
│   └── chroma-db/            # ChromaDB persistence
│       └── ...               # ChromaDB data files
├── .dolt-version              # Optional: Git-Dolt link file
└── README.md
```

### 7.3 Initial Setup Commands

**For a new project**:
```bash
# 1. Create data directory
mkdir -p data

# 2. Add to .gitignore (copy from section 7.1)
echo "data/" >> .gitignore

# 3. Start MCP server and initialize
# (via your MCP client/Claude)
> Initialize a new knowledge base at myorg/my-project-kb
```

**For joining existing project**:
```bash
# 1. Clone the code repository
git clone https://github.com/myorg/my-project.git
cd my-project

# 2. Create data directory
mkdir -p data

# 3. Start MCP server and clone knowledge base
# (via your MCP client/Claude)
> Clone the knowledge base from myorg/my-project-kb
```

### 7.4 Git-Dolt Linking Setup

To keep your knowledge base version in sync with your code version:

**Option 1: Manual Linking**
```
After each significant code commit:
> Link the current knowledge base to this Git commit

LLM: [calls dolt_link_git(bidirectional=true)]
```

**Option 2: Track Link File in Git**
```bash
# Allow .dolt-version to be tracked
echo "!.dolt-version" >> .gitignore

# After linking, commit the link file
git add .dolt-version
git commit -m "Link knowledge base version"
```

**Option 3: CI/CD Integration**
```yaml
# In your CI/CD pipeline (e.g., GitHub Actions)
- name: Verify Knowledge Base Version
  run: |
    # Check that .dolt-version matches expected
    EXPECTED_DOLT_COMMIT=$(cat .dolt-version | jq -r .dolt_commit)
    # ... verification logic
```

---

## 8. Troubleshooting

### 8.1 Common Issues

#### Issue: "NOT_INITIALIZED" Error
```
Symptom: Tools return NOT_INITIALIZED error
Cause: No Dolt repository configured
Solution: Run dolt_init or dolt_clone first
```

#### Issue: "UNCOMMITTED_CHANGES" Blocking Operations
```
Symptom: Can't pull, checkout, or reset
Cause: Local changes haven't been committed
Solution: 
  - dolt_commit() to save changes, OR
  - dolt_reset(confirm_discard=true) to discard
```

#### Issue: "REMOTE_REJECTED" on Push
```
Symptom: Push fails with rejection message
Cause: Remote has commits you don't have
Solution: dolt_pull() first, then dolt_push()
```

#### Issue: "AUTHENTICATION_FAILED"
```
Symptom: Can't push/pull/clone from DoltHub
Cause: Dolt credentials not configured
Solution: Run 'dolt login' in terminal, or check credential configuration
```

#### Issue: Slow Checkout/Pull
```
Symptom: Branch switching takes a long time
Cause: Large number of documents requires embedding regeneration
Solution: 
  - This is expected for large document sets
  - Consider smaller, more focused collections
```

### 8.2 Recovery Procedures

#### Procedure: Complete Reset
```
If everything is broken and you want to start over:

1. Delete data directory:
   rm -rf data/dolt-repo data/chroma-db

2. Re-clone:
   > Clone the knowledge base from myorg/my-project-kb
```

#### Procedure: Recover Lost Commits
```
If you need to find a commit that seems lost:

1. dolt_commits(limit=100)  # Check if it's in history
2. dolt_find(query="<partial message or hash>")
3. If found: dolt_checkout(target="<hash>")
```

#### Procedure: Fix Diverged Branches
```
If local and remote have diverged:

1. dolt_fetch()                    # Get remote state
2. dolt_status()                   # See divergence
3. Option A: dolt_reset(target="origin/main", confirm_discard=true)
             # Discard local, match remote
4. Option B: dolt_pull()           # Merge (may have conflicts)
```

### 8.3 Getting Help

- **DoltHub Documentation**: https://docs.dolthub.com
- **Dolt CLI Reference**: https://docs.dolthub.com/cli-reference
- **MCP Server Issues**: [Your project's issue tracker]

---

## Appendix A: Quick Reference Card

### Document Operations (ChromaDB)
```
# Reading/Searching
chroma_query_documents(collection, queries, n_results)  # Semantic search (RAG)
chroma_get_documents(collection, ids, where)            # Get by ID/filter
chroma_peek_collection(collection, limit)               # Sample documents
chroma_list_collections()                               # List all collections
chroma_get_collection_count(collection)                 # Document count

# Writing
chroma_add_documents(collection, documents, metadatas)  # Add new docs
chroma_update_documents(collection, ids, documents)     # Update existing
chroma_delete_documents(collection, ids)                # Delete docs

# Collection Management
chroma_create_collection(name, metadata)                # Create collection
chroma_modify_collection(name, new_name)                # Rename/update
chroma_delete_collection(name, confirm)                 # Delete collection
```

### Version Control - Status & Information
```
dolt_status()                    # Current state + local changes
dolt_branches()                  # List branches
dolt_commits(branch, limit)      # Branch history
dolt_show(commit)                # Commit details
dolt_find(query)                 # Search commits
```

### Version Control - Setup
```
dolt_init(remote_url)            # New repository
dolt_clone(remote_url, branch)   # Clone existing
```

### Version Control - Syncing
```
dolt_fetch()                     # Check for updates
dolt_pull(if_uncommitted)        # Get & merge
dolt_push()                      # Upload commits
```

### Version Control - Local Operations
```
dolt_commit(message)             # Save changes
dolt_checkout(target, create)    # Switch branch
dolt_reset(target, confirm)      # Discard changes
```

### Emergency Commands
```
dolt_reset("HEAD", true)         # Discard uncommitted
dolt_reset("origin/main", true)  # Match remote exactly
```

### Common Workflows
```
# RAG Query
chroma_query_documents("vmrag_main", ["how to authenticate"])

# Add & Save
chroma_add_documents("vmrag_main", ["New content..."], [{title: "..."}])
dolt_commit("Added new documentation")
dolt_push()

# Get Updates
dolt_pull()

# Full Daily Workflow
dolt_status() → dolt_pull() → [work with chroma_*] → dolt_commit() → dolt_push()
```
