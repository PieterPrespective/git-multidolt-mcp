using System.Text.Json;
using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for DoltCli implementation
    /// Tests all Phase 1 operations from section 3.1 of the implementation plan
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    [Category("Dolt")]
    public class DoltCliIntegrationTests
    {
        private DoltCli _doltCli;
        private string _testRepoPath;
        private ILogger<DoltCli> _logger;

        [SetUp]
        public void Setup()
        {
            // Create a temporary directory for test repository
            _testRepoPath = Path.Combine(Path.GetTempPath(), "dolt_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testRepoPath);

            // Setup configuration
            var config = Options.Create(new DoltConfiguration
            {
                DoltExecutablePath = Environment.OSVersion.Platform == PlatformID.Win32NT 
                    ? @"C:\Program Files\Dolt\bin\dolt.exe" 
                    : "dolt",
                RepositoryPath = _testRepoPath,
                CommandTimeoutMs = 30000,
                EnableDebugLogging = true
            });

            // Create logger
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = loggerFactory.CreateLogger<DoltCli>();

            _doltCli = new DoltCli(config, _logger);
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test repository
            if (Directory.Exists(_testRepoPath))
            {
                try
                {
                    Directory.Delete(_testRepoPath, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }

        // ==================== Repository Management Tests ====================

        [Test]
        public async Task InitAsync_Should_Initialize_New_Repository()
        {
            // Act
            var result = await _doltCli.InitAsync();

            // Assert
            Assert.That(result.Success, Is.True, $"Init failed: {result.Error}");
            Assert.That(Directory.Exists(Path.Combine(_testRepoPath, ".dolt")), Is.True, "Dolt directory not created");
        }

        [Test]
        [Ignore("Requires DoltHub credentials and network access")]
        public async Task CloneAsync_Should_Clone_Remote_Repository()
        {
            // Arrange
            var remoteUrl = "dolthub.com/dolthub/us-baby-names"; // Public test repo

            // Act
            var result = await _doltCli.CloneAsync(remoteUrl);

            // Assert
            Assert.That(result.Success, Is.True, $"Clone failed: {result.Error}");
        }

        [Test]
        public async Task GetStatusAsync_Should_Return_Repository_Status()
        {
            // Arrange
            await _doltCli.InitAsync();

            // Act
            var status = await _doltCli.GetStatusAsync();

            // Assert
            Assert.That(status, Is.Not.Null);
            Assert.That(status.Branch, Is.Not.Empty);
            Assert.That(status.HasStagedChanges, Is.False);
            Assert.That(status.HasUnstagedChanges, Is.False);
        }

        // ==================== Branch Operation Tests ====================

        [Test]
        public async Task GetCurrentBranchAsync_Should_Return_Current_Branch()
        {
            // Arrange
            await _doltCli.InitAsync();

            // Act
            var branch = await _doltCli.GetCurrentBranchAsync();

            // Assert
            Assert.That(branch, Is.Not.Null.And.Not.Empty);
            Assert.That(branch, Is.EqualTo("main").Or.EqualTo("master"));
        }

        [Test]
        public async Task ListBranchesAsync_Should_Return_All_Branches()
        {
            // Arrange
            await _doltCli.InitAsync();

            // Act
            var branches = await _doltCli.ListBranchesAsync();

            // Assert
            Assert.That(branches, Is.Not.Null);
            Assert.That(branches.Count(), Is.GreaterThan(0));
            Assert.That(branches.Any(b => b.IsCurrent), Is.True);
        }

        [Test]
        public async Task CreateBranchAsync_Should_Create_New_Branch()
        {
            // Arrange
            await _doltCli.InitAsync();
            var branchName = "test-branch";

            // Act
            var result = await _doltCli.CreateBranchAsync(branchName);

            // Assert
            Assert.That(result.Success, Is.True, $"Create branch failed: {result.Error}");
            
            var branches = await _doltCli.ListBranchesAsync();
            Assert.That(branches.Any(b => b.Name == branchName), Is.True);
        }

        [Test]
        public async Task CheckoutAsync_Should_Switch_To_Existing_Branch()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.CreateBranchAsync("test-branch");

            // Act
            var result = await _doltCli.CheckoutAsync("test-branch");

            // Assert
            Assert.That(result.Success, Is.True, $"Checkout failed: {result.Error}");
            
            var currentBranch = await _doltCli.GetCurrentBranchAsync();
            Assert.That(currentBranch, Is.EqualTo("test-branch"));
        }

        [Test]
        public async Task CheckoutAsync_Should_Create_And_Switch_To_New_Branch()
        {
            // Arrange
            await _doltCli.InitAsync();

            // Act
            var result = await _doltCli.CheckoutAsync("new-branch", createNew: true);

            // Assert
            Assert.That(result.Success, Is.True, $"Checkout with create failed: {result.Error}");
            
            var currentBranch = await _doltCli.GetCurrentBranchAsync();
            Assert.That(currentBranch, Is.EqualTo("new-branch"));
        }

        [Test]
        public async Task DeleteBranchAsync_Should_Delete_Branch()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.CreateBranchAsync("temp-branch");

            // Act
            var result = await _doltCli.DeleteBranchAsync("temp-branch");

            // Assert
            Assert.That(result.Success, Is.True, $"Delete branch failed: {result.Error}");
            
            var branches = await _doltCli.ListBranchesAsync();
            Assert.That(branches.Any(b => b.Name == "temp-branch"), Is.False);
        }

        // ==================== SQL Operation Tests ====================

        [Test]
        public async Task ExecuteAsync_Should_Create_Table()
        {
            // Arrange
            await _doltCli.InitAsync();
            var createTableSql = @"
                CREATE TABLE test_table (
                    id INT PRIMARY KEY,
                    name VARCHAR(100),
                    created_at DATETIME
                )";

            // Act
            var rowsAffected = await _doltCli.ExecuteAsync(createTableSql);

            // Assert
            Assert.That(rowsAffected, Is.GreaterThanOrEqualTo(0));
            
            // Verify table exists
            var tables = await _doltCli.QueryJsonAsync("SHOW TABLES");
            Assert.That(tables, Does.Contain("test_table"));
        }

        [Test]
        public async Task ExecuteAsync_Should_Insert_Data()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY, name VARCHAR(100))");

            // Act
            var insertSql = "INSERT INTO test_table (id, name) VALUES (1, 'Test Record')";
            var rowsAffected = await _doltCli.ExecuteAsync(insertSql);

            // Assert
            Assert.That(rowsAffected, Is.EqualTo(1));
        }

        [Test]
        public async Task QueryJsonAsync_Should_Return_JSON_Results()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY, name VARCHAR(100))");
            await _doltCli.ExecuteAsync("INSERT INTO test_table VALUES (1, 'Alice'), (2, 'Bob')");

            // Act
            var json = await _doltCli.QueryJsonAsync("SELECT * FROM test_table ORDER BY id");

            // Assert
            Assert.That(json, Is.Not.Null.And.Not.Empty);
            
            using var doc = JsonDocument.Parse(json);
            Assert.That(doc.RootElement.TryGetProperty("rows", out var rows), Is.True);
            Assert.That(rows.GetArrayLength(), Is.EqualTo(2));
        }

        [Test]
        public async Task ExecuteScalarAsync_Should_Return_Single_Value()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY, name VARCHAR(100))");
            await _doltCli.ExecuteAsync("INSERT INTO test_table VALUES (1, 'Alice'), (2, 'Bob')");

            // Act
            var count = await _doltCli.ExecuteScalarAsync<int>("SELECT COUNT(*) as count FROM test_table");

            // Assert
            Assert.That(count, Is.EqualTo(2));
        }

        // ==================== Commit Operation Tests ====================

        [Test]
        public async Task CommitAsync_Should_Commit_Changes()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY)");
            await _doltCli.AddAllAsync();

            // Act
            var result = await _doltCli.CommitAsync("Initial commit");

            // Assert
            Assert.That(result.Success, Is.True, $"Commit failed: {result.Message}");
            Assert.That(result.CommitHash, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task GetHeadCommitHashAsync_Should_Return_HEAD_Hash()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY)");
            await _doltCli.AddAllAsync();
            var commitResult = await _doltCli.CommitAsync("Test commit");

            // Act
            var headHash = await _doltCli.GetHeadCommitHashAsync();

            // Assert
            Assert.That(headHash, Is.Not.Null.And.Not.Empty);
            Assert.That(headHash, Is.EqualTo(commitResult.CommitHash));
        }

        [Test]
        public async Task GetLogAsync_Should_Return_Commit_History()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY)");
            await _doltCli.AddAllAsync();
            await _doltCli.CommitAsync("First commit");
            
            await _doltCli.ExecuteAsync("INSERT INTO test_table VALUES (1)");
            await _doltCli.AddAllAsync();
            await _doltCli.CommitAsync("Second commit");

            // Act
            var log = await _doltCli.GetLogAsync(5);

            // Assert
            Assert.That(log, Is.Not.Null);
            Assert.That(log.Count(), Is.GreaterThanOrEqualTo(2));
            Assert.That(log.Any(c => c.Message.Contains("Second commit")), Is.True);
            Assert.That(log.Any(c => c.Message.Contains("First commit")), Is.True);
        }

        // ==================== Reset Operation Tests ====================

        [Test]
        public async Task ResetSoftAsync_Should_Reset_To_Previous_Commit()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY)");
            await _doltCli.AddAllAsync();
            await _doltCli.CommitAsync("First commit");
            
            await _doltCli.ExecuteAsync("INSERT INTO test_table VALUES (1)");
            await _doltCli.AddAllAsync();
            var secondCommit = await _doltCli.CommitAsync("Second commit");

            // Act
            var result = await _doltCli.ResetSoftAsync();

            // Assert
            Assert.That(result.Success, Is.True, $"Reset soft failed: {result.Error}");
            
            var headHash = await _doltCli.GetHeadCommitHashAsync();
            Assert.That(headHash, Is.Not.EqualTo(secondCommit.CommitHash));
        }

        [Test]
        public async Task ResetHardAsync_Should_Reset_To_Specific_Commit()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY)");
            await _doltCli.AddAllAsync();
            var firstCommit = await _doltCli.CommitAsync("First commit");
            
            await _doltCli.ExecuteAsync("INSERT INTO test_table VALUES (1)");
            await _doltCli.AddAllAsync();
            await _doltCli.CommitAsync("Second commit");

            // Act
            var result = await _doltCli.ResetHardAsync(firstCommit.CommitHash);

            // Assert
            Assert.That(result.Success, Is.True, $"Reset hard failed: {result.Error}");
            
            var headHash = await _doltCli.GetHeadCommitHashAsync();
            Assert.That(headHash, Is.EqualTo(firstCommit.CommitHash));
        }

        // ==================== Diff Operation Tests ====================

        [Test]
        public async Task GetWorkingDiffAsync_Should_Return_Uncommitted_Changes()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY)");
            await _doltCli.AddAllAsync();
            await _doltCli.CommitAsync("Initial commit");
            
            await _doltCli.ExecuteAsync("INSERT INTO test_table VALUES (1)");

            // Act
            var diff = await _doltCli.GetWorkingDiffAsync();

            // Assert
            Assert.That(diff, Is.Not.Null);
            Assert.That(diff.TablesChanged, Is.GreaterThan(0));
        }

        [Test]
        public async Task GetTableDiffAsync_Should_Return_Changes_Between_Commits()
        {
            // Arrange
            await _doltCli.InitAsync();
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY, name VARCHAR(100))");
            await _doltCli.AddAllAsync();
            var commit1 = await _doltCli.CommitAsync("Create table");
            
            await _doltCli.ExecuteAsync("INSERT INTO test_table VALUES (1, 'Alice')");
            await _doltCli.AddAllAsync();
            var commit2 = await _doltCli.CommitAsync("Add data");

            // Act
            var diffs = await _doltCli.GetTableDiffAsync(commit1.CommitHash, commit2.CommitHash, "test_table");

            // Assert
            Assert.That(diffs, Is.Not.Null);
            Assert.That(diffs.Count(), Is.GreaterThan(0));
        }

        // ==================== Remote Operation Tests ====================

        [Test]
        [Ignore("Requires DoltHub credentials")]
        public async Task AddRemoteAsync_Should_Add_Remote_Repository()
        {
            // Arrange
            await _doltCli.InitAsync();
            var remoteName = "origin";
            var remoteUrl = "dolthub.com/test/test-repo";

            // Act
            var result = await _doltCli.AddRemoteAsync(remoteName, remoteUrl);

            // Assert
            Assert.That(result.Success, Is.True, $"Add remote failed: {result.Error}");
            
            var remotes = await _doltCli.ListRemotesAsync();
            Assert.That(remotes.Any(r => r.Name == remoteName), Is.True);
        }

        [Test]
        public async Task ListRemotesAsync_Should_Return_Empty_For_New_Repo()
        {
            // Arrange
            await _doltCli.InitAsync();

            // Act
            var remotes = await _doltCli.ListRemotesAsync();

            // Assert
            Assert.That(remotes, Is.Not.Null);
            Assert.That(remotes.Count(), Is.EqualTo(0));
        }

        // ==================== Conflict Resolution Tests ====================

        [Test]
        public async Task HasConflictsAsync_Should_Return_False_For_No_Conflicts()
        {
            // Arrange
            await _doltCli.InitAsync();

            // Act
            var hasConflicts = await _doltCli.HasConflictsAsync();

            // Assert
            Assert.That(hasConflicts, Is.False);
        }
    }
}