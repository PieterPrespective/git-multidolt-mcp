# Manual Dolt Remote Test Flow

This document provides step-by-step terminal commands to manually reproduce the VMRAGTestExisting test flow (Option 4) from EmbranchManualTesting. Use this to debug remote operations and verify expected behavior.

## Prerequisites

1. **Dolt installed**: Ensure `dolt` command is available in PATH
2. **DoltHub account**: You need a DoltHub account with a test database
3. **Authentication**: DoltHub credentials configured (via `dolt login` or credential manager)
4. **Test database**: An existing test database on DoltHub (can be empty)

## Environment Setup

Replace these variables with your actual values:

```bash
# Set your variables
USERNAME="your-dolthub-username"
DATABASE_NAME="your-test-database-name"
TEST_DIR="C:\temp\manual_dolt_test"
```

## Step 1: Authentication & Environment Setup

### 1.1 Verify Dolt Installation
```bash
dolt version
```
**Expected**: Dolt version information

### 1.2 Configure Dolt User (Required for commits)
```bash
dolt config --global user.email "test@example.com"
dolt config --global user.name "ManualTestUser"
```

### 1.3 Verify Authentication
```bash
dolt login --check
```
**Expected**: Authentication status or prompt to login

**Note**: If not authenticated, you'll need to run `dolt login` after adding the remote in Step 3.3

### 1.4 Setup Test Directory
```bash
# Clean any existing test directory
rmdir /s "%TEST_DIR%" 2>nul
mkdir "%TEST_DIR%"
cd /d "%TEST_DIR%"
```

## Step 2: Database Information

### 2.1 Set Database URL
Set your target database URL:
```
https://www.dolthub.com/your-username/your-database-name
```

Parse username and database name from the URL.

## Step 3: Clone Existing Database

### 3.1 Clean Previous Clones (Important!)
```bash
# Remove any existing clone directory
rmdir /s "%DATABASE_NAME%" 2>nul
```

### 3.2 Clone Database
```bash
dolt clone %USERNAME%/%DATABASE_NAME%
```

**Expected Results:**
- **Success**: Directory created with database name
- **Empty Database**: Error "contains no Dolt data" - this is handled in next step

### 3.3 Handle Empty Database (If Clone Failed)
If clone failed with "no Dolt data":
```bash
mkdir "%DATABASE_NAME%"
cd "%DATABASE_NAME%"
dolt init
dolt remote add origin %USERNAME%/%DATABASE_NAME%

# CRITICAL: Authenticate after adding remote
dolt login
cd ..
```

**Important**: `dolt login` will open your browser for DoltHub authentication. This stores credentials in `C:\Users\{username}\.dolt\creds` automatically.

### 3.4 Navigate to Database Directory
```bash
cd "%DATABASE_NAME%"
```

## Step 4: Repository Operations

### 4.1 Check Status
```bash
dolt status
```
**Expected**: Clean working tree or file status

### 4.2 View Commit History
```bash
dolt log --oneline -n 5
```
**Expected**: Recent commits or empty if new database

### 4.3 List Tables
```bash
dolt sql -q "SHOW TABLES" -r json
```
**Expected**: JSON array of tables (may be empty)

## Step 5: Branch Operations

### 5.1 List Current Branches
```bash
dolt branch
```
**Expected**: List of branches with current branch marked

### 5.2 Create Test Branch
```bash
dolt branch vmrag-test-branch
```

### 5.3 List Branches Again
```bash
dolt branch
```
**Expected**: Both main and vmrag-test-branch listed

### 5.4 Checkout Test Branch
```bash
dolt checkout vmrag-test-branch
```

### 5.5 Verify Current Branch
```bash
dolt sql -q "SELECT active_branch() as branch"
```
**Expected**: Shows vmrag-test-branch

### 5.6 Return to Main
```bash
dolt checkout main
```

## Step 6: Commit Operations

### 6.1 Create Test Table
```bash
dolt sql -q "CREATE TABLE IF NOT EXISTS vmrag_test_table (id INT PRIMARY KEY, data TEXT, created_at TIMESTAMP DEFAULT NOW())"
```

### 6.2 Add Test Data
```bash
dolt sql -q "INSERT INTO vmrag_test_table (id, data) VALUES (1, 'Test data from manual test - 2024-12-12 14:30:00')"
```

### 6.3 Check Status
```bash
dolt status
```
**Expected**: Shows modified tables

### 6.4 Stage Changes
```bash
dolt add .
```

### 6.5 Check Status After Staging
```bash
dolt status
```
**Expected**: Shows staged changes

### 6.6 Commit Changes
```bash
dolt commit -m "Manual test: Add test data - 2024-12-12 14:30:00"
```

### 6.7 Get Commit Hash
```bash
dolt sql -q "SELECT DOLT_HASHOF('HEAD') as commit_hash"
```
**Expected**: Current commit hash

## Step 7: Remote Operations (Critical Section)

### 7.1 List Remotes
```bash
dolt remote -v
```
**Expected**: Shows origin remote configuration

### 7.2 Fetch from Remote
```bash
dolt fetch origin
```
**Expected**: Success or "up to date" message

### 7.3 List Initial Remote Branches
```bash
dolt branch -r
```
**Expected**: Remote branch listing

### 7.4 Authenticate for Remote Operations
```bash
# If you haven't authenticated yet, do it now
dolt login --check

# If authentication failed, run:
dolt login
```
**Expected**: Browser opens for DoltHub authentication, credentials stored automatically

### 7.5 Push Main Branch
```bash
dolt push origin main
```
**Expected Results:**
- **Success**: Confirmation of push
- **Error**: Permission denied, repository doesn't exist, or authentication issue

**Common Errors & Solutions:**
- `permission denied`: Check write access to database
- `repository doesn't exist`: Database may need to be created on DoltHub first
- `non-fast-forward`: Need to pull first

### 7.6 Create Branch with Unique Data
```bash
dolt checkout -b remote-test-branch
dolt sql -q "INSERT INTO vmrag_test_table (id, data) VALUES (100, 'Remote push test data - 2024-12-12 14:35:00')"
dolt add .
dolt commit -m "Remote test: Add unique data for push validation - 2024-12-12 14:35:00"
```

### 7.7 Push New Branch
```bash
dolt push origin remote-test-branch
```
**Expected**: New branch pushed to remote

### 7.8 Verify Remote Branches
```bash
dolt branch -r
```
**Expected**: Should now include origin/remote-test-branch

### 7.9 Test Pull Operations
```bash
dolt checkout main
dolt pull origin main
```
**Expected**: "up to date" or pulled changes

### 7.10 Test Remote Branch Pull
```bash
# Try to checkout existing remote branch
dolt checkout remote-test-branch

# If that fails, create tracking branch
dolt checkout -b remote-test-branch origin/remote-test-branch
```

### 7.11 Verify Remote Data
```bash
dolt sql -q "SELECT * FROM vmrag_test_table WHERE id = 100" -r json
```
**Expected**: Should show the unique remote test data

## Step 8: Data CRUD Operations

### 8.1 SELECT Operation
```bash
dolt sql -q "SELECT * FROM vmrag_test_table" -r json
```

### 8.2 INSERT Operation
```bash
dolt sql -q "INSERT INTO vmrag_test_table VALUES (2, 'Additional test data'), (3, 'More test data')"
dolt sql -q "SELECT * FROM vmrag_test_table ORDER BY id" -r json
```

### 8.3 UPDATE Operation
```bash
dolt sql -q "UPDATE vmrag_test_table SET data = 'Updated test data' WHERE id = 2"
dolt sql -q "SELECT * FROM vmrag_test_table WHERE id = 2" -r json
```

### 8.4 DELETE Operation
```bash
dolt sql -q "DELETE FROM vmrag_test_table WHERE id = 3"
dolt sql -q "SELECT * FROM vmrag_test_table ORDER BY id" -r json
```

### 8.5 Commit Data Changes
```bash
dolt add .
dolt commit -m "Manual test: CRUD operations validation"
```

## Step 9: Diff Operations

### 9.1 Make Changes for Diff
```bash
dolt sql -q "INSERT INTO vmrag_test_table (id, data) VALUES (5, 'Diff test data')"
```

### 9.2 Show Working Diff
```bash
dolt diff
```

### 9.3 Commit and Show Commit Diff
```bash
dolt add .
dolt commit -m "Add data for diff testing"
dolt diff HEAD~1 HEAD
```

### 9.4 Table Diff via SQL
```bash
dolt sql -q "SELECT * FROM DOLT_DIFF('HEAD~1', 'HEAD', 'vmrag_test_table')" -r json
```

## Step 10: Cleanup

### 10.1 Remove Test Directory
```bash
cd ..
rmdir /s "%DATABASE_NAME%"
```

### 10.2 Verify Remote State
Visit DoltHub web interface and verify:
1. Main branch contains test data
2. remote-test-branch exists with unique data
3. All commits are visible in commit history

## Troubleshooting Common Issues

### Clone Fails with "data repository already exists"
**Solution**: Remove existing directory first
```bash
rmdir /s "%DATABASE_NAME%" 2>nul
```

### Clone Fails with "no Dolt data"
**Solution**: Database is empty, use init + remote add instead
```bash
mkdir "%DATABASE_NAME%"
cd "%DATABASE_NAME%"
dolt init
dolt remote add origin %USERNAME%/%DATABASE_NAME%
```

### Push Fails with Permission Denied
**Solutions**:
1. Check authentication: `dolt login --check`
2. Verify write access to database on DoltHub
3. Ensure database exists on DoltHub

### Push Fails with Non-Fast-Forward
**Solution**: Pull first, then push
```bash
dolt pull origin main
dolt push origin main
```

### Remote Branch Not Visible
**Solution**: Fetch remotes first
```bash
dolt fetch origin
dolt branch -r
```

## Expected Final State

After successful completion:

1. **Local Repository**: Clean working directory with test data committed
2. **Remote Repository**: 
   - Main branch with initial test data
   - remote-test-branch with unique test data
   - All commits visible on DoltHub
3. **Verification**: Manual check on DoltHub web interface confirms all data synchronized

## Notes for Debugging

- Use `dolt status` frequently to check working tree state
- Use `dolt log --oneline` to verify commit progression
- Use `dolt branch -r` to check remote branch visibility
- Use `dolt remote -v` to verify remote configuration
- Check DoltHub web interface for actual remote state

This manual flow should exactly reproduce the behavior of VMRAGTestExisting Option 4, allowing you to identify where automated test diverges from expected behavior.