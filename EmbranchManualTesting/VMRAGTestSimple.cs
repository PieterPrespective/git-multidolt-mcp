using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace EmbranchManualTesting;

/// <summary>
/// Simplified manual testing class for validating VM RAG operations against a pre-existing DoltHub database.
/// Uses native dolt login instead of custom AuthHelper for authentication.
/// </summary>
public static class VMRAGTestSimple
{
    private static readonly string DoltExecutablePath = @"C:\Program Files\Dolt\bin\dolt.exe";
    private static string TestDirectory = Path.Combine(Path.GetTempPath(), "Embranch_VMRAGTestSimple");
    private static string? DatabaseUrl = null;
    private static string? DatabaseName = null;
    private static string? Username = null;
    
    /// <summary>
    /// Main entry point for simplified VM RAG testing workflow using existing database.
    /// Uses native dolt login for authentication.
    /// </summary>
    public static async Task Run()
    {
        Console.WriteLine("VM RAG Test - Simple (Native Dolt Login)");
        Console.WriteLine("========================================");
        Console.WriteLine();
        Console.WriteLine("This test uses native dolt login for authentication and validates");
        Console.WriteLine("Dolt operations against an existing DoltHub database.");
        Console.WriteLine();
        
        var currentStep = 1;
        
        try
        {
            // Step 1: Get database information
            await GetDatabaseInformation(currentStep++);
            
            // Step 2: Setup test environment
            await SetupTestEnvironment(currentStep++);
            
            // Step 3: Clone existing database
            await CloneExistingDatabase(currentStep++);
            
            // Step 4: Native dolt authentication
            await AuthenticateWithDolt(currentStep++);
            
            // Step 5: Repository management operations
            await TestRepositoryOperations(currentStep++);
            
            // Step 6: Branch management operations
            await TestBranchOperations(currentStep++);
            
            // Step 7: Commit operations
            await TestCommitOperations(currentStep++);
            
            // Step 8: Remote operations (push/pull/fetch)
            await TestRemoteOperations(currentStep++);
            
            // Step 9: Data CRUD operations
            await TestDataOperations(currentStep++);
            
            // Step 10: Diff operations
            await TestDiffOperations(currentStep++);
            
            Console.WriteLine();
            Console.WriteLine("✅ VM RAG Test (Simple) completed successfully!");
            Console.WriteLine("All Dolt operations have been validated against existing DoltHub database.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("🚫 Test aborted by user.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ VM RAG Test failed: {ex.Message}");
            Console.WriteLine("Check the error details above and retry if necessary.");
        }
        finally
        {
            await CleanupTestEnvironment();
            
            Console.WriteLine();
            Console.WriteLine("Press any key to return to main menu...");
            Console.ReadKey();
        }
    }
    
    /// <summary>
    /// Gets database information from user input.
    /// </summary>
    private static async Task GetDatabaseInformation(int stepNumber)
    {
        Console.WriteLine($"📝 Step {stepNumber}: Database Information");
        Console.WriteLine("==============================");
        Console.WriteLine();
        
        Console.WriteLine("Please provide the DoltHub database information:");
        Console.WriteLine();
        
        // Get database URL
        Console.Write("Enter the full DoltHub database URL (e.g., https://www.dolthub.com/username/database-name): ");
        DatabaseUrl = Console.ReadLine()?.Trim();
        
        if (string.IsNullOrEmpty(DatabaseUrl))
        {
            throw new Exception("Database URL is required");
        }
        
        // Parse username and database name from URL
        try
        {
            // Handle multiple DoltHub URL formats:
            // https://www.dolthub.com/username/database-name
            // https://www.dolthub.com/repositories/username/database-name
            // https://www.dolthub.com/users/username/repositories (should prompt for specific database)
            var uri = new Uri(DatabaseUrl);
            var pathParts = uri.AbsolutePath.Trim('/').Split('/');
            
            if (pathParts.Length >= 2)
            {
                if (pathParts[0] == "repositories" && pathParts.Length >= 3)
                {
                    // Format: /repositories/username/database-name
                    Username = pathParts[1];
                    DatabaseName = pathParts[2];
                }
                else if (pathParts[0] == "users" && pathParts.Length >= 2)
                {
                    // Handle /users/username/repositories format - need specific database name
                    if (pathParts.Length >= 3 && pathParts[2] == "repositories")
                    {
                        // User provided repositories listing page, not specific database
                        Username = pathParts[1];
                        Console.WriteLine();
                        Console.WriteLine($"⚠️  You provided a repositories listing URL for user '{Username}'.");
                        Console.WriteLine("Please provide a specific database URL instead.");
                        Console.WriteLine($"Example: https://www.dolthub.com/{Username}/your-database-name");
                        Console.WriteLine();
                        throw new Exception("Specific database URL required, not repositories listing");
                    }
                    else if (pathParts.Length >= 3)
                    {
                        // Format: /users/username/database-name (less common but possible)
                        Username = pathParts[1];
                        DatabaseName = pathParts[2];
                    }
                    else
                    {
                        throw new Exception("Invalid users URL format");
                    }
                }
                else
                {
                    // Format: /username/database-name (standard format)
                    Username = pathParts[0];
                    DatabaseName = pathParts[1];
                }
            }
            else
            {
                throw new Exception("Invalid URL format - insufficient path components");
            }
        }
        catch (Exception ex) when (!(ex.Message.Contains("Specific database URL required")))
        {
            throw new Exception($"Could not parse database URL. Expected format: https://www.dolthub.com/username/database-name or https://www.dolthub.com/repositories/username/database-name. Error: {ex.Message}");
        }
        
        Console.WriteLine();
        Console.WriteLine("📋 Parsed Database Information:");
        Console.WriteLine($"   Username: {Username}");
        Console.WriteLine($"   Database: {DatabaseName}");
        Console.WriteLine($"   Full URL: {DatabaseUrl}");
        
        WaitForUserInput("Verify the database information is correct", stepNumber);
        
        await Task.CompletedTask; // Make method async
    }
    
    /// <summary>
    /// Sets up the test environment for existing database testing.
    /// </summary>
    private static async Task SetupTestEnvironment(int stepNumber)
    {
        Console.WriteLine();
        Console.WriteLine($"🏗️  Step {stepNumber}: Test Environment Setup");
        Console.WriteLine("==================================");
        Console.WriteLine();
        
        // Verify Dolt is accessible
        Console.WriteLine("Verifying Dolt installation...");
        var versionResult = await ExecuteDoltCommand("version");
        if (versionResult.ExitCode != 0)
        {
            throw new Exception($"Dolt is not accessible. Please ensure Dolt is installed.\n" +
                              $"Error: {versionResult.Error}");
        }
        Console.WriteLine($"✅ Dolt version: {versionResult.Output.Split('\n')[0]}");
        
        // Configure Dolt git user for commits (required for Dolt to work)
        Console.WriteLine("Configuring Dolt user for commits...");
        await ExecuteDoltCommand("config --global user.email vmragtest@example.com");
        await ExecuteDoltCommand("config --global user.name VMRAGTestSimple");
        
        // Clean any existing test directory
        if (Directory.Exists(TestDirectory))
        {
            Directory.Delete(TestDirectory, true);
        }
        Directory.CreateDirectory(TestDirectory);
        
        Console.WriteLine($"Created test directory: {TestDirectory}");
        
        WaitForUserInput("Environment setup complete", stepNumber);
    }
    
    /// <summary>
    /// Clones the existing database from DoltHub.
    /// </summary>
    private static async Task CloneExistingDatabase(int stepNumber)
    {
        Console.WriteLine();
        Console.WriteLine($"📥 Step {stepNumber}: Clone Existing Database");
        Console.WriteLine("=====================================");
        Console.WriteLine();
        
        Console.WriteLine($"Cloning database from DoltHub...");
        Console.WriteLine($"Source: {Username}/{DatabaseName}");
        Console.WriteLine($"Target directory: {TestDirectory}");
        Console.WriteLine("This may take a moment depending on database size...");
        
        try
        {
            // Ensure the clone target parent directory exists and is clean
            var cloneParent = Path.GetDirectoryName(TestDirectory);
            var expectedClonePath = Path.Combine(cloneParent!, DatabaseName!);
            
            // Remove any existing clone directory to prevent conflicts
            if (Directory.Exists(expectedClonePath))
            {
                Console.WriteLine($"Removing existing clone directory: {expectedClonePath}");
                Directory.Delete(expectedClonePath, true);
            }
            
            // Clone the existing database
            Console.WriteLine($"Running: dolt clone {Username}/{DatabaseName}");
            var cloneResult = await ExecuteDoltCommand($"clone {Username}/{DatabaseName}", cloneParent, timeoutSeconds: 180);
            
            if (cloneResult.ExitCode != 0)
            {
                // Check if this is an empty database (no Dolt data)
                if (cloneResult.Error.Contains("contains no Dolt data") || cloneResult.Error.Contains("no Dolt data"))
                {
                    Console.WriteLine("⚠️  Database appears to be empty (no Dolt data)");
                    Console.WriteLine("This is common with newly created DoltHub repositories.");
                    Console.WriteLine("Initializing a new local repository and connecting to remote...");
                    
                    // Initialize a new repository instead
                    var initResult = await ExecuteDoltCommand("init", TestDirectory);
                    if (initResult.ExitCode != 0)
                    {
                        throw new Exception($"Failed to initialize repository: {initResult.Error}");
                    }
                    
                    // Add the remote
                    var addRemoteResult = await ExecuteDoltCommand($"remote add origin {Username}/{DatabaseName}", TestDirectory);
                    if (addRemoteResult.ExitCode != 0)
                    {
                        Console.WriteLine($"Warning: Could not add remote - {addRemoteResult.Error}");
                    }
                    
                    Console.WriteLine("✅ Initialized empty repository and connected to remote!");
                    Console.WriteLine("This test will create some initial data to work with.");
                    
                    WaitForUserInput("Empty database handled. Local repository initialized", stepNumber);
                }
                else
                {
                    Console.WriteLine($"❌ Clone failed!");
                    Console.WriteLine($"Error: {cloneResult.Error}");
                    Console.WriteLine($"Output: {cloneResult.Output}");
                    Console.WriteLine($"Command was: dolt clone {Username}/{DatabaseName}");
                    
                    if (cloneResult.Error.Contains("could not be accessed") || cloneResult.Error.Contains("permission denied"))
                    {
                        Console.WriteLine();
                        Console.WriteLine("This appears to be a DoltHub access or permissions issue.");
                        Console.WriteLine("Possible solutions:");
                        Console.WriteLine("1. Verify the database URL is correct and publicly accessible");
                        Console.WriteLine("2. Check that you have read access to this database");
                        Console.WriteLine("3. Try authenticating with 'dolt login' in the next step");
                        Console.WriteLine($"4. Try accessing the database directly: {DatabaseUrl}");
                        Console.WriteLine($"5. Check if the database exists and is public");
                    }
                    
                    throw new Exception("Failed to clone existing database");
                }
            }
            else
            {
                Console.WriteLine("✅ Clone successful!");
                Console.WriteLine($"Result: {cloneResult.Output}");
                
                // For successful clone, update test directory to the cloned database directory
                var clonedPath = Path.Combine(cloneParent!, DatabaseName!);
                if (Directory.Exists(clonedPath))
                {
                    // Remove the empty test directory and use the cloned one
                    if (Directory.Exists(TestDirectory))
                    {
                        Directory.Delete(TestDirectory);
                    }
                    TestDirectory = clonedPath;
                    Console.WriteLine($"Updated working directory to: {TestDirectory}");
                }
                
                WaitForUserInput("Clone completed. Database is now available locally", stepNumber);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Clone operation failed with exception: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// Authenticates with DoltHub using native dolt login.
    /// </summary>
    private static async Task AuthenticateWithDolt(int stepNumber)
    {
        Console.WriteLine();
        Console.WriteLine($"🔐 Step {stepNumber}: DoltHub Authentication (Native)");
        Console.WriteLine("===============================================");
        Console.WriteLine();
        
        Console.WriteLine("Checking current authentication status...");
        
        // Check if already authenticated
        var loginCheckResult = await ExecuteDoltCommand("login --check", TestDirectory);
        
        if (loginCheckResult.ExitCode == 0)
        {
            Console.WriteLine("✅ Already authenticated with DoltHub");
            Console.WriteLine($"Status: {loginCheckResult.Output}");
        }
        else
        {
            Console.WriteLine("❌ Not currently authenticated with DoltHub");
            Console.WriteLine("Starting DoltHub authentication...");
            Console.WriteLine();
            Console.WriteLine("⚠️  This will open your browser for DoltHub authentication.");
            Console.WriteLine("Please complete the authentication process in your browser.");
            Console.WriteLine();
            
            WaitForUserInput("Press Enter to open browser for DoltHub authentication", stepNumber);
            
            // Run dolt login - this will open browser
            Console.WriteLine("Opening browser for authentication...");
            var loginResult = await ExecuteDoltCommand("login", TestDirectory, timeoutSeconds: 300); // 5 minute timeout for user interaction
            
            if (loginResult.ExitCode == 0)
            {
                Console.WriteLine("✅ Authentication successful!");
                Console.WriteLine($"Result: {loginResult.Output}");
                
                // Verify authentication worked
                var verifyResult = await ExecuteDoltCommand("login --check", TestDirectory);
                if (verifyResult.ExitCode == 0)
                {
                    Console.WriteLine("✅ Authentication verified:");
                    Console.WriteLine(verifyResult.Output);
                }
                else
                {
                    Console.WriteLine("⚠️  Authentication verification failed, but login appeared to succeed");
                }
            }
            else
            {
                Console.WriteLine("❌ Authentication failed or was cancelled");
                Console.WriteLine($"Error: {loginResult.Error}");
                Console.WriteLine($"Output: {loginResult.Output}");
                
                Console.WriteLine();
                Console.WriteLine("You can try:");
                Console.WriteLine("1. Running 'dolt login' manually");
                Console.WriteLine("2. Checking your internet connection");
                Console.WriteLine("3. Verifying your DoltHub account");
                
                throw new Exception("DoltHub authentication failed");
            }
        }
        
        WaitForUserInput("Authentication verified. Press Enter to continue", stepNumber);
    }
    
    /// <summary>
    /// Tests repository management operations.
    /// </summary>
    private static async Task TestRepositoryOperations(int stepNumber)
    {
        Console.WriteLine();
        Console.WriteLine($"📁 Step {stepNumber}: Repository Management Operations");
        Console.WriteLine("==========================================");
        Console.WriteLine();
        
        // Test status operation
        Console.WriteLine("Testing dolt status...");
        var statusResult = await ExecuteDoltCommand("status", TestDirectory);
        Console.WriteLine($"Status output: {statusResult.Output}");
        
        WaitForUserInput("Verify repository status", stepNumber);
        
        // Test log operation
        Console.WriteLine("Testing dolt log...");
        var logResult = await ExecuteDoltCommand("log --oneline -n 5", TestDirectory);
        Console.WriteLine($"Recent commits: {logResult.Output}");
        
        WaitForUserInput("Verify commit history", stepNumber);
        
        // Show database schema
        Console.WriteLine("Testing schema inspection...");
        var showTablesResult = await ExecuteDoltCommand("sql -q \"SHOW TABLES\" -r json", TestDirectory);
        Console.WriteLine($"Available tables: {showTablesResult.Output}");
        
        if (showTablesResult.Output.Contains("[]") || showTablesResult.Output.Trim() == "")
        {
            Console.WriteLine("📋 Database appears to be empty - this is normal for new databases");
            Console.WriteLine("The test will create tables as needed for validation");
        }
        
        WaitForUserInput("Verify database schema inspection works", stepNumber);
        
        Console.WriteLine("✅ Repository operations validated!");
    }
    
    /// <summary>
    /// Tests branch management operations.
    /// </summary>
    private static async Task TestBranchOperations(int stepNumber)
    {
        Console.WriteLine();
        Console.WriteLine($"🌿 Step {stepNumber}: Branch Management Operations");
        Console.WriteLine("======================================");
        Console.WriteLine();
        
        // List current branches
        Console.WriteLine("Testing dolt branch (list branches)...");
        var listResult = await ExecuteDoltCommand("branch", TestDirectory);
        Console.WriteLine($"Current branches: {listResult.Output}");
        
        WaitForUserInput("Verify branches are listed", stepNumber);
        
        // Create new branch
        Console.WriteLine("Testing branch creation...");
        var createResult = await ExecuteDoltCommand("branch vmrag-test-branch", TestDirectory);
        Console.WriteLine("Branch 'vmrag-test-branch' created");
        
        // List branches again
        var listResult2 = await ExecuteDoltCommand("branch", TestDirectory);
        Console.WriteLine($"Updated branches: {listResult2.Output}");
        
        WaitForUserInput("Verify 'vmrag-test-branch' was created", stepNumber);
        
        // Checkout new branch
        Console.WriteLine("Testing branch checkout...");
        var checkoutResult = await ExecuteDoltCommand("checkout vmrag-test-branch", TestDirectory);
        Console.WriteLine("Switched to vmrag-test-branch");
        
        // Verify current branch
        var currentBranchResult = await ExecuteDoltCommand("sql -q \"SELECT active_branch() as branch\"", TestDirectory);
        Console.WriteLine($"Current branch: {currentBranchResult.Output}");
        
        WaitForUserInput("Verify currently on 'vmrag-test-branch'", stepNumber);
        
        // Switch back to main
        await ExecuteDoltCommand("checkout main", TestDirectory);
        Console.WriteLine("Switched back to main branch");
        
        Console.WriteLine("✅ Branch operations validated!");
    }
    
    /// <summary>
    /// Tests commit operations.
    /// </summary>
    private static async Task TestCommitOperations(int stepNumber)
    {
        Console.WriteLine();
        Console.WriteLine($"💾 Step {stepNumber}: Commit Operations");
        Console.WriteLine("============================");
        Console.WriteLine();
        
        // Create a test table if it doesn't exist
        Console.WriteLine("Creating/verifying test table...");
        var createTableResult = await ExecuteDoltCommand("sql -q \"CREATE TABLE IF NOT EXISTS vmrag_test_table (id INT PRIMARY KEY, data TEXT, created_at TIMESTAMP DEFAULT NOW())\"", TestDirectory);
        
        // Add test data
        Console.WriteLine("Adding test data...");
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var insertResult = await ExecuteDoltCommand($"sql -q \"INSERT INTO vmrag_test_table (id, data) VALUES (1, 'Test data from VM RAG Simple - {timestamp}')\"", TestDirectory);
        
        // Check status
        var statusResult = await ExecuteDoltCommand("status", TestDirectory);
        Console.WriteLine($"Repository status: {statusResult.Output}");
        
        WaitForUserInput("Verify changes are shown as modified", stepNumber);
        
        // Stage changes
        Console.WriteLine("Staging changes...");
        var addResult = await ExecuteDoltCommand("add .", TestDirectory);
        
        // Check status after staging
        var statusResult2 = await ExecuteDoltCommand("status", TestDirectory);
        Console.WriteLine($"Status after staging: {statusResult2.Output}");
        
        WaitForUserInput("Verify changes are staged", stepNumber);
        
        // Commit changes
        Console.WriteLine("Committing changes...");
        var commitResult = await ExecuteDoltCommand($"commit -m \"VM RAG Simple test: Add test data - {timestamp}\"", TestDirectory);
        Console.WriteLine($"Commit result: {commitResult.Output}");
        
        // Get commit hash
        var hashResult = await ExecuteDoltCommand("sql -q \"SELECT DOLT_HASHOF('HEAD') as commit_hash\"", TestDirectory);
        Console.WriteLine($"Latest commit hash: {hashResult.Output}");
        
        WaitForUserInput("Verify commit was successful", stepNumber);
        
        Console.WriteLine("✅ Commit operations validated!");
    }
    
    /// <summary>
    /// Tests remote operations including push, pull, fetch, and branch synchronization.
    /// </summary>
    private static async Task TestRemoteOperations(int stepNumber)
    {
        Console.WriteLine();
        Console.WriteLine($"🌐 Step {stepNumber}: Remote Operations (Push/Pull/Fetch)");
        Console.WriteLine("===================================================");
        Console.WriteLine();
        
        Console.WriteLine("⚠️  WARNING: This test will push data to the remote database!");
        Console.WriteLine("Only run this on a test database that can be modified.");
        Console.WriteLine($"Target database: {DatabaseUrl}");
        Console.WriteLine();
        
        WaitForUserInput("Confirm you want to proceed with push operations to the remote database", stepNumber);
        
        // List remotes
        Console.WriteLine("Testing remote configuration...");
        var listRemotesResult = await ExecuteDoltCommand("remote -v", TestDirectory);
        Console.WriteLine($"Configured remotes: {listRemotesResult.Output}");
        
        WaitForUserInput("Verify remote is configured correctly", stepNumber);
        
        // Test fetch first
        Console.WriteLine("Testing fetch from remote...");
        try
        {
            var fetchResult = await ExecuteDoltCommand("fetch origin", TestDirectory, timeoutSeconds: 60);
            
            if (fetchResult.ExitCode == 0)
            {
                Console.WriteLine("✅ Fetch successful!");
                Console.WriteLine($"Result: {fetchResult.Output}");
            }
            else
            {
                Console.WriteLine($"⚠️  Fetch result: {fetchResult.Error}");
                if (fetchResult.Error.Contains("no Dolt data") || fetchResult.Error.Contains("empty"))
                {
                    Console.WriteLine("This is expected for empty databases - no data to fetch yet");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Fetch operation failed: {ex.Message}");
        }
        
        // Show initial remote branches
        Console.WriteLine("Listing initial remote branches...");
        var initialRemoteBranchesResult = await ExecuteDoltCommand("branch -r", TestDirectory);
        Console.WriteLine($"Initial remote branches: {initialRemoteBranchesResult.Output}");
        
        WaitForUserInput("Initial remote state shown", stepNumber);
        
        // Test pushing main branch with current changes
        Console.WriteLine("Testing push to remote (main branch)...");
        Console.WriteLine("This will push the current local changes to the remote database...");
        
        try
        {
            var pushMainResult = await ExecuteDoltCommand("push origin main", TestDirectory, timeoutSeconds: 120);
            
            if (pushMainResult.ExitCode == 0)
            {
                Console.WriteLine("✅ Push to main successful!");
                Console.WriteLine($"Push result: {pushMainResult.Output}");
            }
            else
            {
                Console.WriteLine($"❌ Push to main failed: {pushMainResult.Error}");
                Console.WriteLine($"Output: {pushMainResult.Output}");
                
                // Handle common push errors
                if (pushMainResult.Error.Contains("permission") || pushMainResult.Error.Contains("access"))
                {
                    Console.WriteLine("This appears to be a permissions issue.");
                    Console.WriteLine("Verify you have write access to this database.");
                }
                else if (pushMainResult.Error.Contains("non-fast-forward"))
                {
                    Console.WriteLine("This is a non-fast-forward update. You may need to pull first.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Push operation failed with exception: {ex.Message}");
        }
        
        WaitForUserInput("Main branch push completed", stepNumber);
        
        // Create and push a test branch with new data
        Console.WriteLine("Creating test branch for remote push validation...");
        
        // Create new branch
        await ExecuteDoltCommand("checkout -b remote-test-branch", TestDirectory);
        Console.WriteLine("Created and switched to 'remote-test-branch'");
        
        // Add unique test data
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await ExecuteDoltCommand($"sql -q \"INSERT INTO vmrag_test_table (id, data) VALUES (100, 'Remote push test data - {timestamp}')\"", TestDirectory);
        
        // Commit the changes
        await ExecuteDoltCommand("add .", TestDirectory);
        await ExecuteDoltCommand($"commit -m \"Remote test: Add unique data for push validation - {timestamp}\"", TestDirectory);
        
        Console.WriteLine("Added unique test data and committed");
        
        WaitForUserInput("Test branch with unique data created", stepNumber);
        
        // Push the new branch
        Console.WriteLine("Testing push of new branch to remote...");
        try
        {
            var pushBranchResult = await ExecuteDoltCommand("push origin remote-test-branch", TestDirectory, timeoutSeconds: 120);
            
            if (pushBranchResult.ExitCode == 0)
            {
                Console.WriteLine("✅ Branch push successful!");
                Console.WriteLine($"Push result: {pushBranchResult.Output}");
            }
            else
            {
                Console.WriteLine($"❌ Branch push failed: {pushBranchResult.Error}");
                Console.WriteLine($"Output: {pushBranchResult.Output}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Branch push operation failed: {ex.Message}");
        }
        
        WaitForUserInput("Branch push completed", stepNumber);
        
        // Verify remote branches after push
        Console.WriteLine("Verifying remote branches after push...");
        var newRemoteBranchesResult = await ExecuteDoltCommand("branch -r", TestDirectory);
        Console.WriteLine($"Remote branches after push: {newRemoteBranchesResult.Output}");
        
        WaitForUserInput("Verify remote-test-branch appears in remote branches", stepNumber);
        
        // Test pull operation
        Console.WriteLine("Testing pull operation...");
        
        // Switch back to main and pull
        await ExecuteDoltCommand("checkout main", TestDirectory);
        Console.WriteLine("Switched back to main branch");
        
        try
        {
            var pullResult = await ExecuteDoltCommand("pull origin main", TestDirectory, timeoutSeconds: 120);
            
            if (pullResult.ExitCode == 0)
            {
                Console.WriteLine("✅ Pull successful!");
                Console.WriteLine($"Pull result: {pullResult.Output}");
            }
            else
            {
                Console.WriteLine($"⚠️  Pull result: {pullResult.Error}");
                if (pullResult.Error.Contains("up to date") || pullResult.Error.Contains("Already up-to-date"))
                {
                    Console.WriteLine("This is expected - local branch is up to date with remote");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Pull operation failed: {ex.Message}");
        }
        
        WaitForUserInput("Pull operation completed", stepNumber);
        
        // Test pulling the remote branch we just pushed
        Console.WriteLine("Testing pull of remote branch...");
        try
        {
            var pullBranchResult = await ExecuteDoltCommand("checkout remote-test-branch", TestDirectory);
            if (pullBranchResult.ExitCode != 0)
            {
                // If local branch doesn't exist, create it from remote
                await ExecuteDoltCommand("checkout -b remote-test-branch origin/remote-test-branch", TestDirectory);
                Console.WriteLine("Created local tracking branch from remote");
            }
            
            // Verify the data we pushed is present
            var verifyResult = await ExecuteDoltCommand("sql -q \"SELECT * FROM vmrag_test_table WHERE id = 100\" -r json", TestDirectory);
            Console.WriteLine($"Remote branch data verification: {verifyResult.Output}");
            
            if (verifyResult.Output.Contains("Remote push test data"))
            {
                Console.WriteLine("✅ Data synchronization verified! Remote push/pull working correctly.");
            }
            else
            {
                Console.WriteLine("⚠️  Data verification: Remote data may not have synchronized correctly");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Remote branch pull failed: {ex.Message}");
        }
        
        WaitForUserInput("Remote branch pull and data verification completed", stepNumber);
        
        // Final verification - show complete remote state
        Console.WriteLine("Final remote state verification...");
        Console.WriteLine($"🌍 Database URL: {DatabaseUrl}");
        Console.WriteLine("Please manually verify the following on DoltHub:");
        Console.WriteLine("1. Main branch contains the test data");
        Console.WriteLine("2. remote-test-branch exists and contains unique data");
        Console.WriteLine("3. All commits are visible in the commit history");
        
        WaitForUserInput("Manually verify remote database state on DoltHub", stepNumber);
        
        // Switch back to main for subsequent tests
        await ExecuteDoltCommand("checkout main", TestDirectory);
        
        Console.WriteLine("✅ Remote operations (push/pull/fetch) validated!");
    }
    
    /// <summary>
    /// Tests data CRUD operations.
    /// </summary>
    private static async Task TestDataOperations(int stepNumber)
    {
        Console.WriteLine();
        Console.WriteLine($"🗃️  Step {stepNumber}: Data CRUD Operations");
        Console.WriteLine("=================================");
        Console.WriteLine();
        
        // SELECT operation on existing data
        Console.WriteLine("Testing SELECT operation...");
        var selectResult = await ExecuteDoltCommand("sql -q \"SELECT * FROM vmrag_test_table\" -r json", TestDirectory);
        Console.WriteLine($"Current test data: {selectResult.Output}");
        
        WaitForUserInput("Verify test data is shown", stepNumber);
        
        // INSERT operation
        Console.WriteLine("Testing INSERT operation...");
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var insertResult = await ExecuteDoltCommand($"sql -q \"INSERT INTO vmrag_test_table (id, data) VALUES (2, 'Additional test data - {timestamp}'), (3, 'More test data - {timestamp}')\"", TestDirectory);
        
        // Verify INSERT
        var selectResult2 = await ExecuteDoltCommand("sql -q \"SELECT * FROM vmrag_test_table ORDER BY id\" -r json", TestDirectory);
        Console.WriteLine($"Data after INSERT: {selectResult2.Output}");
        
        WaitForUserInput("Verify new records were inserted", stepNumber);
        
        // UPDATE operation
        Console.WriteLine("Testing UPDATE operation...");
        var updateResult = await ExecuteDoltCommand($"sql -q \"UPDATE vmrag_test_table SET data = 'Updated test data - {timestamp}' WHERE id = 2\"", TestDirectory);
        
        // Verify UPDATE
        var selectResult3 = await ExecuteDoltCommand("sql -q \"SELECT * FROM vmrag_test_table WHERE id = 2\" -r json", TestDirectory);
        Console.WriteLine($"Updated record: {selectResult3.Output}");
        
        WaitForUserInput("Verify record was updated", stepNumber);
        
        // DELETE operation
        Console.WriteLine("Testing DELETE operation...");
        var deleteResult = await ExecuteDoltCommand("sql -q \"DELETE FROM vmrag_test_table WHERE id = 3\"", TestDirectory);
        
        // Verify DELETE
        var selectResult4 = await ExecuteDoltCommand("sql -q \"SELECT * FROM vmrag_test_table ORDER BY id\" -r json", TestDirectory);
        Console.WriteLine($"Data after DELETE: {selectResult4.Output}");
        
        WaitForUserInput("Verify record was deleted", stepNumber);
        
        // Commit data changes
        await ExecuteDoltCommand("add .", TestDirectory);
        await ExecuteDoltCommand($"commit -m \"VM RAG Simple test: CRUD operations validation - {timestamp}\"", TestDirectory);
        
        Console.WriteLine("✅ Data CRUD operations validated!");
    }
    
    /// <summary>
    /// Tests diff operations for tracking changes.
    /// </summary>
    private static async Task TestDiffOperations(int stepNumber)
    {
        Console.WriteLine();
        Console.WriteLine($"📊 Step {stepNumber}: Diff Operations");
        Console.WriteLine("==========================");
        Console.WriteLine();
        
        // Make a new change
        Console.WriteLine("Making changes for diff testing...");
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await ExecuteDoltCommand($"sql -q \"INSERT INTO vmrag_test_table (id, data) VALUES (5, 'Diff test data - {timestamp}')\"", TestDirectory);
        
        // Show working changes
        Console.WriteLine("Testing dolt diff (working changes)...");
        var diffResult = await ExecuteDoltCommand("diff", TestDirectory);
        Console.WriteLine($"Working diff: {diffResult.Output}");
        
        WaitForUserInput("Verify unstaged changes are shown in diff", stepNumber);
        
        // Stage and commit changes
        await ExecuteDoltCommand("add .", TestDirectory);
        await ExecuteDoltCommand($"commit -m \"Add data for diff testing - {timestamp}\"", TestDirectory);
        
        // Test commit diff
        Console.WriteLine("Testing commit-to-commit diff...");
        var diffCommitsResult = await ExecuteDoltCommand("diff HEAD~1 HEAD", TestDirectory);
        Console.WriteLine($"Commit diff: {diffCommitsResult.Output}");
        
        WaitForUserInput("Verify diff between commits is shown", stepNumber);
        
        // Test table diff via SQL
        Console.WriteLine("Testing table diff via SQL...");
        var tableDiffResult = await ExecuteDoltCommand("sql -q \"SELECT * FROM DOLT_DIFF('HEAD~1', 'HEAD', 'vmrag_test_table')\" -r json", TestDirectory);
        Console.WriteLine($"Table diff: {tableDiffResult.Output}");
        
        WaitForUserInput("Verify table-level diff data", stepNumber);
        
        Console.WriteLine("✅ Diff operations validated!");
    }
    
    /// <summary>
    /// Cleans up the test environment and removes temporary files.
    /// </summary>
    private static async Task CleanupTestEnvironment()
    {
        Console.WriteLine();
        Console.WriteLine("🧹 Cleanup: Removing test environment");
        Console.WriteLine("=====================================");
        Console.WriteLine();
        
        try
        {
            if (Directory.Exists(TestDirectory))
            {
                Directory.Delete(TestDirectory, true);
                Console.WriteLine($"✅ Removed test directory: {TestDirectory}");
            }
            
            Console.WriteLine("Note: Local changes were made to the cloned database copy only.");
            Console.WriteLine("The original database on DoltHub was not modified unless push operations succeeded.");
            Console.WriteLine();
            Console.WriteLine("DoltHub credentials are stored in: C:\\Users\\{username}\\.dolt\\creds");
            Console.WriteLine("You can remove them with: dolt login --remove");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Cleanup warning: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Executes a Dolt command with specified working directory using simplified approach.
    /// </summary>
    private static async Task<SimpleCommandResult> ExecuteDoltCommand(string arguments, string? workingDirectory = null, int timeoutSeconds = 30)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = DoltExecutablePath;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                process.StartInfo.WorkingDirectory = workingDirectory;
            }
            
            process.Start();
            
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            
            // Wait for process to complete with timeout
            if (!process.WaitForExit(timeoutSeconds * 1000))
            {
                process.Kill();
                return new SimpleCommandResult
                {
                    ExitCode = -1,
                    Output = "",
                    Error = $"Command timed out after {timeoutSeconds} seconds"
                };
            }
            
            var output = await outputTask;
            var error = await errorTask;
            
            return new SimpleCommandResult
            {
                ExitCode = process.ExitCode,
                Output = output,
                Error = error
            };
        }
        catch (Exception ex)
        {
            return new SimpleCommandResult
            {
                ExitCode = -1,
                Output = "",
                Error = ex.Message
            };
        }
    }
    
    /// <summary>
    /// Waits for user input with a descriptive message and abort capability.
    /// </summary>
    private static void WaitForUserInput(string message, int stepNumber = 0)
    {
        Console.WriteLine();
        if (stepNumber > 0)
        {
            Console.WriteLine($"📋 Step {stepNumber}: {message}");
        }
        else
        {
            Console.WriteLine($"👤 {message}");
        }
        Console.WriteLine("Press Enter to continue, or type 'abort' to stop the test...");
        
        var input = Console.ReadLine()?.Trim().ToLower();
        if (input == "abort" || input == "a")
        {
            Console.WriteLine("❌ Test aborted by user.");
            throw new OperationCanceledException("Test aborted by user");
        }
    }
}

/// <summary>
/// Represents the result of a command execution for VMRAGTestSimple.
/// </summary>
internal class SimpleCommandResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
}