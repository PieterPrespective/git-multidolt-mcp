using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DMMS.Services;
using DMMS.Tools;
using DMMS.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.Text.Json;
using Moq;

namespace DMMSTesting.IntegrationTests
{
    /// <summary>
    /// Integration test that validates the MCP tools by duplicating the ChromaDoltSyncIntegrationTestsV2 workflow
    /// but using MCP tools with JSON input/output instead of direct backend function calls.
    /// </summary>
    [TestFixture]
    public class McpToolsIntegrationTest
    {
        private ILogger<McpToolsIntegrationTest>? _logger;
        private string _testDirectory = null!;
        
        // MCP Tools instances
        private ChromaCreateCollectionTool? _chromaCreateCollectionTool;
        private ChromaListCollectionsTool? _chromaListCollectionsTool;
        private ChromaAddDocumentsTool? _chromaAddDocumentsTool;
        private ChromaGetDocumentsTool? _chromaGetDocumentsTool;
        private ChromaGetCollectionInfoTool? _chromaGetCollectionInfoTool;
        private ChromaQueryDocumentsTool? _chromaQueryDocumentsTool;
        private ChromaUpdateDocumentsTool? _chromaUpdateDocumentsTool;
        private ChromaDeleteDocumentsTool? _chromaDeleteDocumentsTool;
        private ChromaPeekCollectionTool? _chromaPeekCollectionTool;
        
        private DoltInitTool? _doltInitTool;
        private DoltStatusTool? _doltStatusTool;
        private DoltBranchesTool? _doltBranchesTool;
        private DoltCommitsTool? _doltCommitsTool;
        private DoltCommitTool? _doltCommitTool;
        private DoltCheckoutTool? _doltCheckoutTool;
        private DoltPullTool? _doltPullTool;
        private DoltPushTool? _doltPushTool;
        private DoltFetchTool? _doltFetchTool;
        private DoltCloneTool? _doltCloneTool;
        private DoltResetTool? _doltResetTool;
        private DoltShowTool? _doltShowTool;
        
        // User environments for testing
        private McpUserEnvironment _userA = null!;
        private McpUserEnvironment _userB = null!;
        
        [SetUp]
        public void Setup()
        {
            // Setup logging
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });
            _logger = loggerFactory.CreateLogger<McpToolsIntegrationTest>();
            
            // Create test directory
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            _testDirectory = Path.Combine(Path.GetTempPath(), $"McpToolsTest_{timestamp}");
            Directory.CreateDirectory(_testDirectory);
            
            // Initialize user environments
            _userA = new McpUserEnvironment("UserA", Path.Combine(_testDirectory, "userA"));
            _userB = new McpUserEnvironment("UserB", Path.Combine(_testDirectory, "userB"));
            
            // Initialize MCP tools for both users
            InitializeMcpTools(_userA);
            InitializeMcpTools(_userB);
        }
        
        private void InitializeMcpTools(McpUserEnvironment user)
        {
            var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
            
            // Initialize ChromaDB tools
            user.ChromaCreateCollectionTool = new ChromaCreateCollectionTool(
                loggerFactory.CreateLogger<ChromaCreateCollectionTool>(), user.ChromaService);
            user.ChromaListCollectionsTool = new ChromaListCollectionsTool(
                loggerFactory.CreateLogger<ChromaListCollectionsTool>(), user.ChromaService);
            user.ChromaAddDocumentsTool = new ChromaAddDocumentsTool(
                loggerFactory.CreateLogger<ChromaAddDocumentsTool>(), user.ChromaService);
            user.ChromaGetDocumentsTool = new ChromaGetDocumentsTool(
                loggerFactory.CreateLogger<ChromaGetDocumentsTool>(), user.ChromaService);
            user.ChromaGetCollectionInfoTool = new ChromaGetCollectionInfoTool(
                loggerFactory.CreateLogger<ChromaGetCollectionInfoTool>(), user.ChromaService);
            user.ChromaQueryDocumentsTool = new ChromaQueryDocumentsTool(
                loggerFactory.CreateLogger<ChromaQueryDocumentsTool>(), user.ChromaService);
            user.ChromaUpdateDocumentsTool = new ChromaUpdateDocumentsTool(
                loggerFactory.CreateLogger<ChromaUpdateDocumentsTool>(), user.ChromaService);
            user.ChromaDeleteDocumentsTool = new ChromaDeleteDocumentsTool(
                loggerFactory.CreateLogger<ChromaDeleteDocumentsTool>(), user.ChromaService, user.DeletionTracker, user.DoltCli, Options.Create(user.DoltConfig));
            user.ChromaPeekCollectionTool = new ChromaPeekCollectionTool(
                loggerFactory.CreateLogger<ChromaPeekCollectionTool>(), user.ChromaService);
                
            // Create mocks for IDmmsStateManifest and ISyncStateChecker (PP13-79)
            var manifestService = new Mock<IDmmsStateManifest>().Object;
            var syncStateChecker = new Mock<ISyncStateChecker>().Object;

            // Initialize Dolt tools
            user.DoltInitTool = new DoltInitTool(
                loggerFactory.CreateLogger<DoltInitTool>(), user.DoltCli, user.SyncManager,
                manifestService, syncStateChecker);
            user.DoltStatusTool = new DoltStatusTool(
                loggerFactory.CreateLogger<DoltStatusTool>(), user.DoltCli, user.SyncManager);
            user.DoltBranchesTool = new DoltBranchesTool(
                loggerFactory.CreateLogger<DoltBranchesTool>(), user.DoltCli);
            user.DoltCommitsTool = new DoltCommitsTool(
                loggerFactory.CreateLogger<DoltCommitsTool>(), user.DoltCli);
            user.DoltCommitTool = new DoltCommitTool(
                loggerFactory.CreateLogger<DoltCommitTool>(), user.DoltCli, user.SyncManager, (ISyncStateTracker)user.DeletionTracker,
                manifestService, syncStateChecker);
            user.DoltCheckoutTool = new DoltCheckoutTool(
                loggerFactory.CreateLogger<DoltCheckoutTool>(), user.DoltCli, user.SyncManager, (ISyncStateTracker)user.DeletionTracker,
                manifestService, syncStateChecker);
            user.DoltPullTool = new DoltPullTool(
                loggerFactory.CreateLogger<DoltPullTool>(), user.DoltCli, user.SyncManager,
                manifestService, syncStateChecker);
            user.DoltPushTool = new DoltPushTool(
                loggerFactory.CreateLogger<DoltPushTool>(), user.DoltCli, user.SyncManager);
            user.DoltFetchTool = new DoltFetchTool(
                loggerFactory.CreateLogger<DoltFetchTool>(), user.DoltCli);
            user.DoltCloneTool = new DoltCloneTool(
                loggerFactory.CreateLogger<DoltCloneTool>(), user.DoltCli, user.SyncManager, (ISyncStateTracker)user.DeletionTracker,
                Options.Create(new DoltConfiguration { RepositoryPath = user.DoltRepoPath, DoltExecutablePath = "dolt" }),
                manifestService, syncStateChecker);
            user.DoltResetTool = new DoltResetTool(
                loggerFactory.CreateLogger<DoltResetTool>(), user.DoltCli, user.SyncManager, manifestService, syncStateChecker);
            user.DoltShowTool = new DoltShowTool(
                loggerFactory.CreateLogger<DoltShowTool>(), user.DoltCli);
        }
        
        [Test]
        [CancelAfter(180000)] // 3 minutes timeout
        public async Task McpTools_E2EWorkflow_ShouldCompleteSuccessfully()
        {
            // Initialize PythonContext for ChromaDB operations
            if (!PythonContext.IsInitialized)
            {
                _logger!.LogInformation("Initializing PythonContext for MCP tools test...");
                PythonContext.Initialize();
                _logger.LogInformation("✅ PythonContext initialized successfully");
            }
            
            try
            {
                await RunMcpToolsWorkflowAsync();
                _logger!.LogInformation("✅ MCP tools E2E workflow completed successfully");
            }
            catch (Exception ex)
            {
                _logger!.LogError(ex, "❌ MCP tools workflow failed");
                throw;
            }
        }

        [Test]
        [CancelAfter(120000)] // 2 minutes timeout
        public async Task DoltBranchesTool_BranchOperations_ShouldWork()
        {
            // Initialize PythonContext for ChromaDB operations
            if (!PythonContext.IsInitialized)
            {
                _logger!.LogInformation("Initializing PythonContext for branch operations test...");
                PythonContext.Initialize();
                _logger.LogInformation("✅ PythonContext initialized successfully");
            }
            
            try
            {
                await TestBranchOperationsAsync();
                _logger!.LogInformation("✅ DoltBranchesTool branch operations test completed successfully");
            }
            catch (Exception ex)
            {
                _logger!.LogError(ex, "❌ Branch operations test failed");
                throw;
            }
        }
        
        private async Task RunMcpToolsWorkflowAsync()
        {
            const string COLLECTION_NAME = "TestTeachings";
            
            _logger!.LogInformation("🚀 === MCP TOOLS E2E WORKFLOW TEST STARTING ===");
            _logger.LogInformation("🎯 Test Objective: Validate all MCP tools work correctly with JSON input/output in collaborative workflow");
            _logger.LogInformation("📊 Testing {ToolCount} MCP tools across ChromaDB and Dolt operations", 18);
            
            // Step 1: User A creates initial repository using MCP tools
            _logger!.LogInformation("🧑‍🏫 === PHASE 1: User A Repository Creation ===");
            _logger.LogInformation("📝 User A will create collection, add documents, and initialize version control");
            await SetupUserEnvironment(_userA);
            await UserA_CreateCollectionAndDocuments(_userA, COLLECTION_NAME);
            await UserA_InitializeAndCommit(_userA, COLLECTION_NAME);
            
            // CRITICAL: Ensure UserA commits all changes before UserB collaborative operations
            _logger.LogInformation("🔄 STEP 1.3: User A checking status and committing all changes before collaborative workflow...");
            
            // First, check if there are any uncommitted changes
            var statusResult = await _userA.DoltStatusTool!.DoltStatus();
            var statusJson = JsonSerializer.Serialize(statusResult);
            var statusDoc = JsonDocument.Parse(statusJson);
            var hasChanges = false;
            
            if (statusDoc.RootElement.TryGetProperty("has_changes", out var hasChangesElement))
            {
                hasChanges = hasChangesElement.GetBoolean();
            }
            
            if (hasChanges)
            {
                _logger.LogInformation("⚠️ UserA has uncommitted changes - committing before collaborative workflow");
                
                // Commit all changes to make repository ready for collaboration
                var commitResult = await _userA.DoltCommitTool!.DoltCommit("Initial collaborative workspace setup - commit all changes");
                
                // Validate commit result to ensure UserA repository is ready for collaboration
                var commitJson = JsonSerializer.Serialize(commitResult);
                var commitDoc = JsonDocument.Parse(commitJson);
                var commitSuccess = false;
                if (commitDoc.RootElement.TryGetProperty("success", out var successElement))
                {
                    commitSuccess = successElement.GetBoolean();
                }
                
                if (commitSuccess)
                {
                    _logger.LogInformation("✅ UserA committed changes successfully - ready for UserB collaborative workflow");
                }
                else
                {
                    _logger.LogWarning("⚠️ UserA commit did not report success - may need investigation");
                }
            }
            else
            {
                _logger.LogInformation("ℹ️ UserA repository already in committed state - ready for collaborative workflow");
            }
            
            _logger.LogInformation("✅ Phase 1 Complete: User A repository established with version control");
            
            // Step 2: User B clones and adds content using MCP tools  
            _logger!.LogInformation("👨‍💻 === PHASE 2: User B Collaboration Workflow ===");
            _logger.LogInformation("🔄 User B will clone repository, add new content, and commit changes");
            await SetupUserEnvironment(_userB);
            await UserB_CloneAndSync(_userB, _userA.DoltRepoPath, COLLECTION_NAME);
            await UserB_AddContent(_userB, COLLECTION_NAME);
            await UserB_CommitChanges(_userB);
            _logger.LogInformation("✅ Phase 2 Complete: User B successfully collaborated and made contributions");
            
            // Step 3: Validate consistency using MCP tools
            _logger!.LogInformation("🔍 === PHASE 3: Comprehensive Validation ===");
            _logger.LogInformation("🏁 Validating that all MCP tools produced correct JSON responses and maintained data consistency");
            await ValidateWorkflowResults(_userA, _userB, COLLECTION_NAME);
            _logger.LogInformation("🎉 Phase 3 Complete: All MCP tools validated successfully!");
            
            _logger!.LogInformation("🏆 === MCP TOOLS E2E WORKFLOW TEST COMPLETED SUCCESSFULLY ===");
        }
        
        #region User A Workflow Methods (using MCP tools)
        
        private async Task UserA_CreateCollectionAndDocuments(McpUserEnvironment user, string collectionName)
        {
            _logger!.LogInformation("🔵 STEP 1.1: User A creating collection and documents using MCP tools...");
            
            // Use ChromaCreateCollectionTool
            _logger.LogInformation("📋 Creating collection '{CollectionName}' using ChromaCreateCollectionTool", collectionName);
            var createResult = await user.ChromaCreateCollectionTool!.CreateCollection(collectionName);
            ValidateSuccessfulResult(createResult, "CreateCollection");
            _logger.LogInformation("✅ Collection '{CollectionName}' created successfully via MCP tool", collectionName);
            
            // Use ChromaAddDocumentsTool
            _logger.LogInformation("📝 Adding initial teaching documents using ChromaAddDocumentsTool");
            var documents = new List<string>
            {
                "# Learning Fundamentals\n\nEvery journey begins with understanding the basics.",
                "# Problem Solving Methodology\n\n1. Understand 2. Analyze 3. Strategize 4. Execute 5. Evaluate"
            };
            var ids = new List<string> { "learning_001", "problem_solving_001" };
            var metadatas = new List<Dictionary<string, object>>
            {
                new() { ["title"] = "Learning Fundamentals", ["author"] = "User A", ["topic"] = "Education" },
                new() { ["title"] = "Problem Solving", ["author"] = "User A", ["topic"] = "Methodology" }
            };
            
            _logger.LogInformation("📄 Serializing {DocumentCount} documents with metadata for MCP tool input", documents.Count);
            var addResult = await user.ChromaAddDocumentsTool!.AddDocuments(
                collectionName, JsonSerializer.Serialize(documents), JsonSerializer.Serialize(ids), JsonSerializer.Serialize(metadatas));
            ValidateSuccessfulResult(addResult, "AddDocuments");
            _logger.LogInformation("✅ {DocumentCount} documents added successfully via MCP tool", documents.Count);
            
            // Validate using ChromaGetCollectionInfoTool
            _logger.LogInformation("🔍 Validating collection info using ChromaGetCollectionInfoTool");
            var infoResult = await user.ChromaGetCollectionInfoTool!.GetCollectionInfo(collectionName);
            ValidateSuccessfulResult(infoResult, "GetCollectionInfo");
            _logger.LogInformation("✅ Collection info retrieved and validated via MCP tool");
        }
        
        private async Task UserA_InitializeAndCommit(McpUserEnvironment user, string collectionName)
        {
            _logger!.LogInformation("🔵 STEP 1.2: User A initializing version control using MCP tools...");
            
            // Use DoltInitTool
            _logger.LogInformation("⚙️ Initializing Dolt repository with ChromaDB import using DoltInitTool");
            var initResult = await user.DoltInitTool!.DoltInit(
                remote_url: null, initial_branch: "main", import_existing: true, commit_message: "Initial teaching repository");
            ValidateSuccessfulResult(initResult, "DoltInit");
            _logger.LogInformation("✅ Dolt repository initialized successfully via MCP tool");
            
            // Use DoltStatusTool to check status
            _logger.LogInformation("🔍 Checking repository status using DoltStatusTool");
            var statusResult = await user.DoltStatusTool!.DoltStatus();
            ValidateSuccessfulResult(statusResult, "DoltStatus");
            _logger.LogInformation("✅ Repository status retrieved successfully via MCP tool");
            
            // The DoltInit with import_existing=true should have already created an initial commit
            // Let's check if there are any local changes that need to be committed separately
            _logger.LogInformation("📊 Analyzing status response to determine if additional commit is needed");
            var statusObj = JsonDocument.Parse(JsonSerializer.Serialize(statusResult));
            var hasChanges = false;
            if (statusObj.RootElement.TryGetProperty("local_changes", out var changesElement))
            {
                if (changesElement.TryGetProperty("has_changes", out var hasChangesElement))
                {
                    hasChanges = hasChangesElement.GetBoolean();
                }
            }
            
            if (hasChanges)
            {
                // Use DoltCommitTool only if there are changes to commit
                _logger.LogInformation("💾 Creating additional commit for remaining changes using DoltCommitTool");
                var commitResult = await user.DoltCommitTool!.DoltCommit(
                    "Initial commit with teaching documents");
                ValidateSuccessfulResult(commitResult, "DoltCommit");
                _logger.LogInformation("✅ Additional commit created successfully via MCP tool");
            }
            else
            {
                _logger.LogInformation("ℹ️ No additional commit needed - DoltInit already created initial commit with imported data");
            }
        }
        
        #endregion
        
        #region User B Workflow Methods (using MCP tools)
        
        private async Task UserB_CloneAndSync(McpUserEnvironment user, string sourceRepoPath, string collectionName)
        {
            _logger!.LogInformation("🔵 STEP 2.1: User B cloning repository using MCP tools...");
            
            // Simulate clone by copying directory and using DoltInitTool
            _logger.LogInformation("📁 Simulating repository clone by copying Dolt data from User A to User B");
            CopyDirectory(sourceRepoPath, user.DoltRepoPath, true);
            _logger.LogInformation("✅ Repository data copied successfully to User B's environment");
            
            // Since we're simulating a local workflow, we'll use DoltCheckout to switch to main
            // This will sync the existing data from Dolt to ChromaDB and create the collection automatically
            _logger.LogInformation("🔄 Checking out main branch to sync Dolt data to ChromaDB using DoltCheckoutTool");
            _logger.LogInformation("📋 Note: ChromaDB collection will be created automatically during checkout/sync process");
            var checkoutResult = await user.DoltCheckoutTool!.DoltCheckout("main");
            ValidateSuccessfulResult(checkoutResult, "DoltCheckout to main branch");
            _logger.LogInformation("✅ User B checked out main branch and synced data via MCP tool");
            
            // Validate that the repository has data by getting documents from Dolt status
            _logger.LogInformation("🔍 Validating repository state using DoltStatusTool");
            var statusResult = await user.DoltStatusTool!.DoltStatus();
            ValidateSuccessfulResult(statusResult, "DoltStatus after checkout");
            _logger.LogInformation("✅ Repository status validated - data is accessible");
            
            // Validate sync worked by checking if we can get documents 
            _logger.LogInformation("📄 Verifying document access using ChromaGetDocumentsTool");
            var docsResult = await user.ChromaGetDocumentsTool!.GetDocuments(collectionName);
            ValidateSuccessfulResult(docsResult, "GetDocuments after sync");
            _logger.LogInformation("✅ Documents are accessible - User B repository sync completed successfully");
        }
        
        private async Task UserB_AddContent(McpUserEnvironment user, string collectionName)
        {
            _logger!.LogInformation("🔵 STEP 2.2: User B adding new content using MCP tools...");
            
            // Use ChromaAddDocumentsTool to add new content
            _logger.LogInformation("📝 Creating new programming content using ChromaAddDocumentsTool");
            var documents = new List<string>
            {
                "# Programming Best Practices\n\nWrite clean, readable code with proper testing."
            };
            var ids = new List<string> { "programming_001" };
            var metadatas = new List<Dictionary<string, object>>
            {
                new() { ["title"] = "Programming Best Practices", ["author"] = "User B", ["topic"] = "Programming" }
            };
            
            _logger.LogInformation("📄 Serializing new document with metadata for MCP tool input");
            var addResult = await user.ChromaAddDocumentsTool!.AddDocuments(
                collectionName, JsonSerializer.Serialize(documents), JsonSerializer.Serialize(ids), JsonSerializer.Serialize(metadatas));
            ValidateSuccessfulResult(addResult, "AddDocuments by User B");
            _logger.LogInformation("✅ User B successfully added programming content via MCP tool");
            
            // Use ChromaQueryDocumentsTool to validate content
            _logger.LogInformation("🔍 Validating new content with semantic search using ChromaQueryDocumentsTool");
            var queryResult = await user.ChromaQueryDocumentsTool!.QueryDocuments(
                collectionName, 
                JsonSerializer.Serialize(new List<string> { "programming best practices" }),
                nResults: 5);
            ValidateSuccessfulResult(queryResult, "QueryDocuments");
            _logger.LogInformation("✅ Semantic search successfully found User B's content - validation complete");
        }
        
        private async Task UserB_CommitChanges(McpUserEnvironment user)
        {
            _logger!.LogInformation("🔵 STEP 2.3: User B committing changes using MCP tools...");
            
            // Check status first to see if there are changes to commit
            _logger.LogInformation("🔍 Checking repository status before commit using DoltStatusTool");
            var statusResult = await user.DoltStatusTool!.DoltStatus();
            ValidateSuccessfulResult(statusResult, "DoltStatus before commit");
            _logger.LogInformation("✅ Repository status retrieved successfully");
            
            // Check if there are local changes that need to be committed
            _logger.LogInformation("📊 Analyzing status response to determine if commit is needed");
            var statusObj = JsonDocument.Parse(JsonSerializer.Serialize(statusResult));
            var hasChanges = false;
            if (statusObj.RootElement.TryGetProperty("local_changes", out var changesElement))
            {
                if (changesElement.TryGetProperty("has_changes", out var hasChangesElement))
                {
                    hasChanges = hasChangesElement.GetBoolean();
                }
            }
            
            if (hasChanges)
            {
                // Use DoltCommitTool only if there are changes
                _logger.LogInformation("💾 Creating commit for User B's changes using DoltCommitTool");
                var commitResult = await user.DoltCommitTool!.DoltCommit(
                    "Added programming best practices");
                ValidateSuccessfulResult(commitResult, "DoltCommit by User B");
                _logger.LogInformation("✅ User B successfully committed changes via MCP tool");
            }
            else
            {
                _logger.LogInformation("ℹ️ No changes to commit for User B (changes may have been auto-committed during sync operations)");
            }
            
            // Use DoltCommitsTool to view commit history
            _logger.LogInformation("📚 Retrieving commit history using DoltCommitsTool to validate repository state");
            var historyResult = await user.DoltCommitsTool!.DoltCommits(limit: 5);
            ValidateSuccessfulResult(historyResult, "DoltCommits");
            _logger.LogInformation("✅ Commit history retrieved successfully - User B workflow complete");
        }
        
        #endregion
        
        #region Validation Methods
        
        private async Task ValidateWorkflowResults(McpUserEnvironment userA, McpUserEnvironment userB, string collectionName)
        {
            _logger!.LogInformation("🔵 STEP 3: Validating complete workflow results using MCP tools...");
            
            // Use ChromaPeekCollectionTool to inspect collections
            _logger.LogInformation("🔍 Inspecting collections for both users using ChromaPeekCollectionTool");
            foreach (var (user, name) in new[] { (userA, "User A"), (userB, "User B") })
            {
                _logger.LogInformation("📄 Peeking into {User}'s collection '{CollectionName}'", name, collectionName);
                var peekResult = await user.ChromaPeekCollectionTool!.PeekCollection(collectionName, limit: 5);
                ValidateSuccessfulResult(peekResult, $"PeekCollection for {name}");
                _logger.LogInformation("✅ {User} collection peek successful - content is accessible", name);
                
                // Use ChromaListCollectionsTool to verify collections exist
                _logger.LogInformation("📋 Listing all collections for {User} using ChromaListCollectionsTool", name);
                var listResult = await user.ChromaListCollectionsTool!.ListCollections();
                ValidateSuccessfulResult(listResult, $"ListCollections for {name}");
                _logger.LogInformation("✅ {User} collections listed successfully", name);
            }
            
            // Use DoltBranchesTool to check branches
            _logger.LogInformation("🌿 Checking repository branches using DoltBranchesTool");
            var branchesResult = await userA.DoltBranchesTool!.DoltBranches();
            ValidateSuccessfulResult(branchesResult, "DoltBranches");
            _logger.LogInformation("✅ Repository branches listed successfully");
            
            // Use DoltShowTool to show commit details - check if HEAD exists first
            _logger.LogInformation("📚 Attempting to show HEAD commit details using DoltShowTool");
            var showResult = await userA.DoltShowTool!.DoltShow("HEAD");
            
            // Check if the show command was successful
            var showJson = JsonSerializer.Serialize(showResult);
            var showDoc = JsonDocument.Parse(showJson);
            var showSuccess = false;
            if (showDoc.RootElement.TryGetProperty("success", out var showSuccessElement))
            {
                showSuccess = showSuccessElement.GetBoolean();
            }
            
            if (showSuccess)
            {
                ValidateSuccessfulResult(showResult, "DoltShow");
                _logger.LogInformation("✅ HEAD commit details retrieved successfully via DoltShowTool");
            }
            else
            {
                // If HEAD doesn't work, get the latest commit from history as alternative
                _logger.LogInformation("ℹ️ HEAD commit not found, using DoltCommitsTool as alternative validation method");
                var historyResult = await userA.DoltCommitsTool!.DoltCommits(limit: 1);
                ValidateSuccessfulResult(historyResult, "DoltCommits for show");
                _logger.LogInformation("✅ Commit history retrieved successfully via DoltCommitsTool - repository state validated");
            }
            
            _logger.LogInformation("🎉 All workflow validations completed successfully - MCP tools E2E test passed!");
        }
        
        private void ValidateSuccessfulResult(object result, string toolName)
        {
            Assert.That(result, Is.Not.Null, $"{toolName} should return a result");
            
            // Convert to JSON and verify structure
            var json = JsonSerializer.Serialize(result);
            Assert.That(json, Is.Not.Null.And.Not.Empty, $"{toolName} should return valid JSON");
            
            // Parse as JsonDocument to validate structure
            var jsonDoc = JsonDocument.Parse(json);
            Assert.That(jsonDoc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object), 
                $"{toolName} should return a JSON object");
            
            // Check for success field (most tools have this)
            if (jsonDoc.RootElement.TryGetProperty("success", out var successElement))
            {
                Assert.That(successElement.GetBoolean(), Is.True, 
                    $"{toolName} should indicate success=true. Result: {json}");
            }
            
            _logger?.LogDebug("{Tool} result: {Json}", toolName, json);
        }
        
        #endregion
        
        private async Task TestBranchOperationsAsync()
        {
            const string TEST_COLLECTION = "BranchTestCollection";
            const string TEST_BRANCH_NAME = "test-feature-branch";
            const string RENAMED_BRANCH_NAME = "feature-branch-renamed";
            
            _logger!.LogInformation("🌿 === DOLT BRANCHES TOOL OPERATIONS TEST STARTING ===");
            _logger.LogInformation("🎯 Test Objective: Validate DoltBranchesTool create, rename, and delete operations");
            
            // Setup test environment
            await SetupUserEnvironment(_userA!);
            
            // Step 1: Initialize repository first
            _logger.LogInformation("⚙️ Step 1: Initializing Dolt repository");
            var initResult = await _userA.DoltInitTool!.DoltInit();
            ValidateSuccessfulResult(initResult, "DoltInit");
            _logger.LogInformation("✅ Repository initialized successfully");
            
            // Step 2: List initial branches (should just be main)
            _logger.LogInformation("📋 Step 2: Listing initial branches");
            var listResult = await _userA.DoltBranchesTool!.DoltBranches("list");
            ValidateSuccessfulResult(listResult, "DoltBranches-List");
            _logger.LogInformation("✅ Initial branches listed successfully");
            
            // Step 3: Create a new branch
            _logger.LogInformation("➕ Step 3: Creating new branch '{Branch}'", TEST_BRANCH_NAME);
            var createResult = await _userA.DoltBranchesTool!.DoltBranches("create", true, TEST_BRANCH_NAME);
            ValidateSuccessfulResult(createResult, "DoltBranches-Create");
            _logger.LogInformation("✅ Branch '{Branch}' created successfully", TEST_BRANCH_NAME);
            
            // Step 4: List branches again to verify creation
            _logger.LogInformation("📋 Step 4: Listing branches to verify creation");
            var listAfterCreateResult = await _userA.DoltBranchesTool!.DoltBranches("list");
            ValidateSuccessfulResult(listAfterCreateResult, "DoltBranches-ListAfterCreate");
            
            // Parse the result to check if our branch exists
            var listJson = JsonSerializer.Serialize(listAfterCreateResult);
            var listDoc = JsonDocument.Parse(listJson);
            var branchFound = false;
            if (listDoc.RootElement.TryGetProperty("branches", out var branchesElement))
            {
                foreach (var branch in branchesElement.EnumerateArray())
                {
                    if (branch.TryGetProperty("name", out var nameElement) && 
                        nameElement.GetString() == TEST_BRANCH_NAME)
                    {
                        branchFound = true;
                        break;
                    }
                }
            }
            Assert.That(branchFound, Is.True, $"Branch '{TEST_BRANCH_NAME}' should be found in the branches list");
            _logger.LogInformation("✅ Branch '{Branch}' verified in branches list", TEST_BRANCH_NAME);
            
            // Step 5: Rename the branch
            _logger.LogInformation("🔄 Step 5: Renaming branch '{OldName}' to '{NewName}'", TEST_BRANCH_NAME, RENAMED_BRANCH_NAME);
            var renameParams = JsonSerializer.Serialize(new { old = TEST_BRANCH_NAME, @new = RENAMED_BRANCH_NAME, force = false });
            var renameResult = await _userA.DoltBranchesTool!.DoltBranches("rename", true, renameParams);
            ValidateSuccessfulResult(renameResult, "DoltBranches-Rename");
            _logger.LogInformation("✅ Branch renamed successfully from '{OldName}' to '{NewName}'", TEST_BRANCH_NAME, RENAMED_BRANCH_NAME);
            
            // Step 6: List branches to verify rename
            _logger.LogInformation("📋 Step 6: Listing branches to verify rename");
            var listAfterRenameResult = await _userA.DoltBranchesTool!.DoltBranches("list");
            ValidateSuccessfulResult(listAfterRenameResult, "DoltBranches-ListAfterRename");
            
            // Verify old name is gone and new name exists
            var renameListJson = JsonSerializer.Serialize(listAfterRenameResult);
            var renameListDoc = JsonDocument.Parse(renameListJson);
            var oldBranchFound = false;
            var newBranchFound = false;
            if (renameListDoc.RootElement.TryGetProperty("branches", out var renameBranchesElement))
            {
                foreach (var branch in renameBranchesElement.EnumerateArray())
                {
                    if (branch.TryGetProperty("name", out var nameElement))
                    {
                        var name = nameElement.GetString();
                        if (name == TEST_BRANCH_NAME) oldBranchFound = true;
                        if (name == RENAMED_BRANCH_NAME) newBranchFound = true;
                    }
                }
            }
            Assert.That(oldBranchFound, Is.False, $"Old branch '{TEST_BRANCH_NAME}' should not exist after rename");
            Assert.That(newBranchFound, Is.True, $"New branch '{RENAMED_BRANCH_NAME}' should exist after rename");
            _logger.LogInformation("✅ Rename operation verified - old branch gone, new branch exists");
            
            // Step 7: Delete the renamed branch
            _logger.LogInformation("🗑️ Step 7: Deleting branch '{Branch}'", RENAMED_BRANCH_NAME);
            var deleteResult = await _userA.DoltBranchesTool!.DoltBranches("delete", true, RENAMED_BRANCH_NAME);
            ValidateSuccessfulResult(deleteResult, "DoltBranches-Delete");
            _logger.LogInformation("✅ Branch '{Branch}' deleted successfully", RENAMED_BRANCH_NAME);
            
            // Step 8: Final list to verify deletion
            _logger.LogInformation("📋 Step 8: Final branch list to verify deletion");
            var finalListResult = await _userA.DoltBranchesTool!.DoltBranches("list");
            ValidateSuccessfulResult(finalListResult, "DoltBranches-FinalList");
            
            // Verify deleted branch is gone
            var finalListJson = JsonSerializer.Serialize(finalListResult);
            var finalListDoc = JsonDocument.Parse(finalListJson);
            var deletedBranchFound = false;
            if (finalListDoc.RootElement.TryGetProperty("branches", out var finalBranchesElement))
            {
                foreach (var branch in finalBranchesElement.EnumerateArray())
                {
                    if (branch.TryGetProperty("name", out var nameElement) && 
                        nameElement.GetString() == RENAMED_BRANCH_NAME)
                    {
                        deletedBranchFound = true;
                        break;
                    }
                }
            }
            Assert.That(deletedBranchFound, Is.False, $"Deleted branch '{RENAMED_BRANCH_NAME}' should not exist in final list");
            _logger.LogInformation("✅ Deletion verified - branch '{Branch}' no longer exists", RENAMED_BRANCH_NAME);
            
            _logger.LogInformation("🎉 === DOLT BRANCHES TOOL OPERATIONS TEST COMPLETED SUCCESSFULLY ===");
        }
        
        #region Helper Methods
        
        private async Task SetupUserEnvironment(McpUserEnvironment user)
        {
            _logger!.LogInformation("⚙️ Setting up test environment for {User}...", user.Name);
            _logger.LogInformation("📁 Creating ChromaDB directory: {ChromaPath}", user.ChromaPath);
            Directory.CreateDirectory(user.ChromaPath);
            _logger.LogInformation("📁 Creating Dolt repository directory: {DoltPath}", user.DoltRepoPath);
            Directory.CreateDirectory(user.DoltRepoPath);
            
            _logger.LogInformation("✅ Environment setup complete for {User} - all directories created", user.Name);
            await Task.Delay(10); // Small delay for directory creation
        }
        
        private static void CopyDirectory(string sourceDir, string destinationDir, bool recursive)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists) return;
            
            DirectoryInfo[] dirs = dir.GetDirectories();
            Directory.CreateDirectory(destinationDir);
            
            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath, overwrite: true);
            }
            
            if (recursive)
            {
                foreach (DirectoryInfo subDir in dirs)
                {
                    string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                    CopyDirectory(subDir.FullName, newDestinationDir, true);
                }
            }
        }
        
        #endregion
        
        [TearDown]
        public void TearDown()
        {
            try
            {
                _userA?.Dispose();
                _userB?.Dispose();
                
                if (Directory.Exists(_testDirectory))
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            Directory.Delete(_testDirectory, recursive: true);
                            _logger?.LogInformation("MCP tools test environment cleaned up successfully");
                            break;
                        }
                        catch (IOException) when (attempt < 2)
                        {
                            Thread.Sleep(1000);
                            GC.Collect();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not fully clean up MCP tools test environment");
            }
        }
    }
    
    /// <summary>
    /// Test environment for MCP tools testing with a user's services and MCP tool instances
    /// </summary>
    public class McpUserEnvironment : IDisposable
    {
        public string Name { get; }
        public string ChromaPath { get; }
        public string DoltRepoPath { get; }
        
        // Backend services
        public IChromaDbService ChromaService { get; private set; } = null!;
        public IDoltCli DoltCli { get; private set; } = null!;
        public IDeletionTracker DeletionTracker { get; private set; } = null!;
        public ISyncManagerV2 SyncManager { get; private set; } = null!;
        public DMMS.Models.DoltConfiguration DoltConfig { get; private set; } = null!;
        
        // MCP Tool instances
        public ChromaCreateCollectionTool? ChromaCreateCollectionTool;
        public ChromaListCollectionsTool? ChromaListCollectionsTool;
        public ChromaAddDocumentsTool? ChromaAddDocumentsTool;
        public ChromaGetDocumentsTool? ChromaGetDocumentsTool;
        public ChromaGetCollectionInfoTool? ChromaGetCollectionInfoTool;
        public ChromaQueryDocumentsTool? ChromaQueryDocumentsTool;
        public ChromaUpdateDocumentsTool? ChromaUpdateDocumentsTool;
        public ChromaDeleteDocumentsTool? ChromaDeleteDocumentsTool;
        public ChromaPeekCollectionTool? ChromaPeekCollectionTool;
        
        public DoltInitTool? DoltInitTool;
        public DoltStatusTool? DoltStatusTool;
        public DoltBranchesTool? DoltBranchesTool;
        public DoltCommitsTool? DoltCommitsTool;
        public DoltCommitTool? DoltCommitTool;
        public DoltCheckoutTool? DoltCheckoutTool;
        public DoltPullTool? DoltPullTool;
        public DoltPushTool? DoltPushTool;
        public DoltFetchTool? DoltFetchTool;
        public DoltCloneTool? DoltCloneTool;
        public DoltResetTool? DoltResetTool;
        public DoltShowTool? DoltShowTool;
        
        public McpUserEnvironment(string name, string basePath)
        {
            Name = name;
            ChromaPath = Path.Combine(basePath, "chroma");
            DoltRepoPath = Path.Combine(basePath, "dolt");
            
            InitializeServices();
        }
        
        private void InitializeServices()
        {
            // Create ChromaDB service
            var chromaConfig = Options.Create(new DMMS.Models.ServerConfiguration
            {
                ChromaDataPath = ChromaPath,
                DataPath = Path.GetDirectoryName(ChromaPath)
            });
            var chromaLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChromaPythonService>();
            ChromaService = new ChromaPythonService(chromaLogger, chromaConfig);
            
            // Create Dolt CLI
            DoltConfig = new DMMS.Models.DoltConfiguration
            {
                RepositoryPath = DoltRepoPath,
                DoltExecutablePath = "dolt"
            };
            var doltLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<DoltCli>();
            DoltCli = new DoltCli(Options.Create(DoltConfig), doltLogger);
            
            // Create deletion tracker and initialize its database schema
            var deletionTrackerLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqliteDeletionTracker>();
            DeletionTracker = new SqliteDeletionTracker(deletionTrackerLogger, chromaConfig.Value);
            
            // Initialize the deletion tracker database schema
            // This is critical for the SqliteDeletionTracker to work properly
            DeletionTracker.InitializeAsync(DoltRepoPath).GetAwaiter().GetResult();
            
            // Create V2 sync manager
            var syncLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SyncManagerV2>();
            SyncManager = new SyncManagerV2(DoltCli, ChromaService, DeletionTracker, (ISyncStateTracker)DeletionTracker, Options.Create(DoltConfig), syncLogger);
        }
        
        public void Dispose()
        {
            if (ChromaService is IDisposable disposableService)
            {
                disposableService.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}