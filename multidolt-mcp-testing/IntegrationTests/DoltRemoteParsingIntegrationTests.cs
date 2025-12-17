using DMMS.Models;
using DMMS.Services;
using DMMS.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DMMSTesting.IntegrationTests
{
    /// <summary>
    /// Integration tests for Dolt remote parsing functionality in the context of push operations.
    /// Tests the complete workflow from empty repository setup through remote configuration to push operations.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    [Category("Dolt")]
    [Category("RemoteParsing")]
    public class DoltRemoteParsingIntegrationTests
    {
        private DoltCli _doltCli;
        private DoltPushTool _pushTool;
        private string _testRepoPath;
        private ILogger<DoltCli> _doltLogger;
        private ILogger<DoltPushTool> _pushLogger;

        [SetUp]
        public void Setup()
        {
            // Create a temporary directory for test repository
            _testRepoPath = Path.Combine(Path.GetTempPath(), "dolt_remote_integration_test_" + Guid.NewGuid().ToString("N"));
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

            // Create loggers
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _doltLogger = loggerFactory.CreateLogger<DoltCli>();
            _pushLogger = loggerFactory.CreateLogger<DoltPushTool>();

            _doltCli = new DoltCli(config, _doltLogger);
            _pushTool = new DoltPushTool(_pushLogger, _doltCli, null); // Minimal setup for testing
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

        /// <summary>
        /// Test that the enhanced remote parsing works in a real repository context.
        /// This simulates the workflow from PP13-44: empty repo → setup → remote add → list remotes → push attempt.
        /// </summary>
        [Test]
        [Ignore("Requires network access and valid remote URL for actual testing")]
        public async Task CompleteWorkflow_Should_ParseRemotes_And_AllowPushOperations()
        {
            // Step 1: Initialize repository (simulating empty repository setup)
            var initResult = await _doltCli.InitAsync();
            Assert.That(initResult.Success, Is.True, $"Repository initialization failed: {initResult.Error}");

            // Step 2: Create some content so we have something to push
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY, name VARCHAR(100))");
            await _doltCli.ExecuteAsync("INSERT INTO test_table VALUES (1, 'Test Data')");
            await _doltCli.AddAllAsync();
            var commitResult = await _doltCli.CommitAsync("Initial commit with test data");
            Assert.That(commitResult.Success, Is.True, "Commit should succeed");

            // Step 3: Add remote (this would output either TAB or space-separated format)
            var remoteUrl = "dolthub.com/test/remote-parsing-test"; // Use a test URL
            var addRemoteResult = await _doltCli.AddRemoteAsync("origin", remoteUrl);
            Assert.That(addRemoteResult.Success, Is.True, $"Adding remote failed: {addRemoteResult.Error}");

            // Step 4: List remotes using our enhanced parsing
            var remotes = await _doltCli.ListRemotesAsync();
            
            // Assert that the enhanced parsing correctly identifies the remote
            Assert.That(remotes, Is.Not.Null, "Remotes should not be null");
            Assert.That(remotes.Count(), Is.EqualTo(1), "Should find exactly one remote");
            
            var originRemote = remotes.First();
            Assert.That(originRemote.Name, Is.EqualTo("origin"), "Remote name should be 'origin'");
            Assert.That(originRemote.Url, Is.EqualTo(remoteUrl), "Remote URL should match what we added");

            // Step 5: Attempt push using DoltPushTool (this would previously fail with REMOTE_NOT_FOUND)
            // Note: We expect this to fail due to authentication, but it should NOT fail with REMOTE_NOT_FOUND
            var pushResult = await _pushTool.DoltPush("origin", "main");
            
            // The key test: it should NOT fail with REMOTE_NOT_FOUND
            // It may fail for other reasons (auth, network, etc.) but remote should be found
            var resultObj = pushResult as dynamic;
            if (resultObj?.success == false)
            {
                Assert.That(resultObj?.error?.ToString(), Is.Not.EqualTo("REMOTE_NOT_FOUND"), 
                    "Push should not fail with REMOTE_NOT_FOUND - remote should be found by enhanced parsing");
                
                // Log the actual error for debugging
                Console.WriteLine($"Push failed with: {resultObj?.error} - {resultObj?.message}");
            }
        }

        /// <summary>
        /// Test that enhanced remote parsing handles real dolt command output correctly.
        /// This test creates a real repository and examines the actual output format.
        /// </summary>
        [Test]
        public async Task RealDoltOutput_Should_BeParsedCorrectly()
        {
            // Initialize repository
            var initResult = await _doltCli.InitAsync();
            Assert.That(initResult.Success, Is.True, $"Repository initialization failed: {initResult.Error}");

            // Initially should have no remotes
            var initialRemotes = await _doltCli.ListRemotesAsync();
            Assert.That(initialRemotes.Count(), Is.EqualTo(0), "New repository should have no remotes");

            // Add a remote with a test URL
            var testRemoteUrl = "https://example.com/test-repo.git";
            var addRemoteResult = await _doltCli.AddRemoteAsync("origin", testRemoteUrl);
            Assert.That(addRemoteResult.Success, Is.True, $"Adding remote failed: {addRemoteResult.Error}");

            // List remotes and verify parsing works
            var remotesAfterAdd = await _doltCli.ListRemotesAsync();
            Assert.That(remotesAfterAdd.Count(), Is.EqualTo(1), "Should have exactly one remote after adding");
            
            var remote = remotesAfterAdd.First();
            Assert.That(remote.Name, Is.EqualTo("origin"), "Remote name should be 'origin'");
            Assert.That(remote.Url, Is.EqualTo(testRemoteUrl), "Remote URL should match what we added");

            // Add a second remote to test multiple remotes
            var backupRemoteUrl = "https://backup.example.com/test-repo.git";
            var addBackupResult = await _doltCli.AddRemoteAsync("backup", backupRemoteUrl);
            Assert.That(addBackupResult.Success, Is.True, $"Adding backup remote failed: {addBackupResult.Error}");

            // Verify both remotes are parsed correctly
            var allRemotes = await _doltCli.ListRemotesAsync();
            Assert.That(allRemotes.Count(), Is.EqualTo(2), "Should have two remotes");
            
            var remoteNames = allRemotes.Select(r => r.Name).ToList();
            Assert.That(remoteNames, Contains.Item("origin"), "Should contain origin remote");
            Assert.That(remoteNames, Contains.Item("backup"), "Should contain backup remote");

            var originRemote = allRemotes.First(r => r.Name == "origin");
            var backupRemote = allRemotes.First(r => r.Name == "backup");
            
            Assert.That(originRemote.Url, Is.EqualTo(testRemoteUrl), "Origin URL should be correct");
            Assert.That(backupRemote.Url, Is.EqualTo(backupRemoteUrl), "Backup URL should be correct");
        }

        /// <summary>
        /// Test that the enhanced error reporting in DoltPushTool provides useful diagnostics.
        /// </summary>
        [Test]
        public async Task DoltPushTool_Should_ProvideDetailedErrorDiagnostics_WhenNoRemotesConfigured()
        {
            // Initialize repository without any remotes
            var initResult = await _doltCli.InitAsync();
            Assert.That(initResult.Success, Is.True, $"Repository initialization failed: {initResult.Error}");

            // Create some content so push would be valid if remotes existed
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY)");
            await _doltCli.AddAllAsync();
            await _doltCli.CommitAsync("Test commit");

            // Attempt push to non-existent remote
            var pushResult = await _pushTool.DoltPush("origin");
            
            // Should fail with enhanced error message
            var resultObj = pushResult as dynamic;
            Assert.That(resultObj?.success, Is.False, "Push should fail when no remotes are configured");
            Assert.That(resultObj?.error?.ToString(), Is.EqualTo("REMOTE_NOT_FOUND"), "Error type should be REMOTE_NOT_FOUND");
            
            // Check that the enhanced error message provides useful information
            string message = resultObj?.message?.ToString() ?? "";
            Assert.That(message, Does.Contain("No remotes are currently configured"), 
                "Error message should indicate no remotes are configured");
        }

        /// <summary>
        /// Test that the enhanced error reporting lists available remotes when target remote is not found.
        /// </summary>
        [Test]
        public async Task DoltPushTool_Should_ListAvailableRemotes_WhenTargetRemoteNotFound()
        {
            // Initialize repository
            var initResult = await _doltCli.InitAsync();
            Assert.That(initResult.Success, Is.True, $"Repository initialization failed: {initResult.Error}");

            // Add some remotes, but not the one we'll try to push to
            await _doltCli.AddRemoteAsync("backup", "https://backup.example.com/repo.git");
            await _doltCli.AddRemoteAsync("upstream", "https://upstream.example.com/repo.git");

            // Create some content
            await _doltCli.ExecuteAsync("CREATE TABLE test_table (id INT PRIMARY KEY)");
            await _doltCli.AddAllAsync();
            await _doltCli.CommitAsync("Test commit");

            // Attempt push to non-existent remote "origin"
            var pushResult = await _pushTool.DoltPush("origin");
            
            // Should fail with enhanced error message
            var resultObj = pushResult as dynamic;
            Assert.That(resultObj?.success, Is.False, "Push should fail when target remote doesn't exist");
            Assert.That(resultObj?.error?.ToString(), Is.EqualTo("REMOTE_NOT_FOUND"), "Error type should be REMOTE_NOT_FOUND");
            
            // Check that available remotes are listed
            string message = resultObj?.message?.ToString() ?? "";
            Assert.That(message, Does.Contain("Available remotes"), "Error message should list available remotes");
            Assert.That(message, Does.Contain("backup"), "Error message should mention 'backup' remote");
            Assert.That(message, Does.Contain("upstream"), "Error message should mention 'upstream' remote");
        }
    }
}