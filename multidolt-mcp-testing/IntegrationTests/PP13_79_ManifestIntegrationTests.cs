using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Moq;
using DMMS.Models;
using DMMS.Services;

namespace DMMSTesting.IntegrationTests;

/// <summary>
/// PP13-79: Integration tests for DMMS manifest operations
/// </summary>
[TestFixture]
[Category("Integration")]
public class PP13_79_ManifestIntegrationTests
{
    private Mock<ILogger<DmmsStateManifest>> _loggerMock = null!;
    private DmmsStateManifest _manifestService = null!;
    private string _testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<DmmsStateManifest>>();
        _manifestService = new DmmsStateManifest(_loggerMock.Object);

        // Create a unique test directory for each test
        _testDirectory = Path.Combine(Path.GetTempPath(), "DmmsManifestIntegration", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Test]
    public async Task Manifest_CreateAndRead_RoundTrips()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = "dolthub.com/integration/test",
                DefaultBranch = "main",
                CurrentCommit = "integration123abc",
                CurrentBranch = "develop"
            },
            GitMapping = new GitMappingConfig
            {
                Enabled = true,
                LastGitCommit = "gitabc123",
                DoltCommitAtGitCommit = "integration123abc"
            },
            Initialization = new InitializationConfig
            {
                Mode = "manual",
                OnClone = "sync_to_latest",
                OnBranchChange = "prompt"
            },
            Collections = new CollectionTrackingConfig
            {
                Tracked = new List<string> { "MyCollection", "Test*" },
                Excluded = new List<string> { "temp-*", "debug-*" }
            },
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = "integration@test.com"
        };

        // Act
        await _manifestService.WriteManifestAsync(_testDirectory, manifest);
        var result = await _manifestService.ReadManifestAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Version, Is.EqualTo("1.0"));
        Assert.That(result.Dolt.RemoteUrl, Is.EqualTo("dolthub.com/integration/test"));
        Assert.That(result.Dolt.DefaultBranch, Is.EqualTo("main"));
        Assert.That(result.Dolt.CurrentCommit, Is.EqualTo("integration123abc"));
        Assert.That(result.Dolt.CurrentBranch, Is.EqualTo("develop"));
        Assert.That(result.GitMapping.Enabled, Is.True);
        Assert.That(result.GitMapping.LastGitCommit, Is.EqualTo("gitabc123"));
        Assert.That(result.Initialization.Mode, Is.EqualTo("manual"));
        Assert.That(result.Initialization.OnClone, Is.EqualTo("sync_to_latest"));
        Assert.That(result.Collections.Tracked, Has.Count.EqualTo(2));
        Assert.That(result.Collections.Excluded, Has.Count.EqualTo(2));
        Assert.That(result.UpdatedBy, Is.EqualTo("integration@test.com"));
    }

    [Test]
    public async Task Manifest_UpdateDoltCommit_PersistsCorrectly()
    {
        // Arrange
        var originalManifest = _manifestService.CreateDefaultManifest(
            remoteUrl: "dolthub.com/test/repo",
            defaultBranch: "main",
            initMode: "auto");

        await _manifestService.WriteManifestAsync(_testDirectory, originalManifest);

        // Act
        await _manifestService.UpdateDoltCommitAsync(_testDirectory, "newcommit999", "feature-branch");
        var result = await _manifestService.ReadManifestAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Dolt.CurrentCommit, Is.EqualTo("newcommit999"));
        Assert.That(result.Dolt.CurrentBranch, Is.EqualTo("feature-branch"));
        // Other fields should remain unchanged
        Assert.That(result.Dolt.RemoteUrl, Is.EqualTo("dolthub.com/test/repo"));
        Assert.That(result.Dolt.DefaultBranch, Is.EqualTo("main"));
    }

    [Test]
    public async Task Manifest_WithGitMapping_UpdatesOnCommit()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest() with
        {
            GitMapping = new GitMappingConfig { Enabled = true }
        };
        await _manifestService.WriteManifestAsync(_testDirectory, manifest);

        // Act
        await _manifestService.RecordGitMappingAsync(_testDirectory, "gitcommit789", "doltcommit123");
        var result = await _manifestService.ReadManifestAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.GitMapping.LastGitCommit, Is.EqualTo("gitcommit789"));
        Assert.That(result.GitMapping.DoltCommitAtGitCommit, Is.EqualTo("doltcommit123"));
    }

    [Test]
    public async Task Manifest_MultipleUpdates_PreservesOtherFields()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = "dolthub.com/preserve/test",
                DefaultBranch = "main",
                CurrentCommit = "original123",
                CurrentBranch = "main"
            },
            Initialization = new InitializationConfig
            {
                Mode = "manual",
                OnClone = "prompt",
                OnBranchChange = "preserve_local"
            }
        };
        await _manifestService.WriteManifestAsync(_testDirectory, manifest);

        // Act - Perform multiple updates
        await _manifestService.UpdateDoltCommitAsync(_testDirectory, "commit1", "branch1");
        await _manifestService.UpdateDoltCommitAsync(_testDirectory, "commit2", "branch2");
        await _manifestService.UpdateDoltCommitAsync(_testDirectory, "commit3", "branch3");

        var result = await _manifestService.ReadManifestAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Dolt.CurrentCommit, Is.EqualTo("commit3"));
        Assert.That(result.Dolt.CurrentBranch, Is.EqualTo("branch3"));
        // These should be preserved
        Assert.That(result.Dolt.RemoteUrl, Is.EqualTo("dolthub.com/preserve/test"));
        Assert.That(result.Dolt.DefaultBranch, Is.EqualTo("main"));
        Assert.That(result.Initialization.Mode, Is.EqualTo("manual"));
        Assert.That(result.Initialization.OnClone, Is.EqualTo("prompt"));
    }

    [Test]
    public async Task Manifest_InvalidVersion_HandledGracefully()
    {
        // Arrange - Write invalid manifest directly
        var dmmsDir = Path.Combine(_testDirectory, ".dmms");
        Directory.CreateDirectory(dmmsDir);
        var invalidJson = @"{
            ""version"": ""99.0"",
            ""dolt"": {
                ""remote_url"": ""test""
            }
        }";
        await File.WriteAllTextAsync(Path.Combine(dmmsDir, "state.json"), invalidJson);

        // Act
        var result = await _manifestService.ReadManifestAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.Null); // Should return null for invalid version
    }

    [Test]
    public async Task Manifest_FileLocking_ConcurrentReads()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest("dolthub.com/concurrent/test");
        await _manifestService.WriteManifestAsync(_testDirectory, manifest);

        // Act - Multiple concurrent reads
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _manifestService.ReadManifestAsync(_testDirectory))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.That(results, Has.All.Not.Null);
        Assert.That(results.Select(r => r!.Dolt.RemoteUrl).Distinct().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Manifest_ClearPath_CreatesNestedDirectories()
    {
        // Arrange
        var nestedPath = Path.Combine(_testDirectory, "level1", "level2", "level3");

        // Act
        await _manifestService.WriteManifestAsync(nestedPath, _manifestService.CreateDefaultManifest());

        // Assert
        var manifestPath = _manifestService.GetManifestPath(nestedPath);
        Assert.That(File.Exists(manifestPath), Is.True);
    }

    [Test]
    public async Task Manifest_UpdatedAt_GetsUpdatedOnWrite()
    {
        // Arrange
        var originalManifest = _manifestService.CreateDefaultManifest();
        await _manifestService.WriteManifestAsync(_testDirectory, originalManifest);
        var firstRead = await _manifestService.ReadManifestAsync(_testDirectory);
        var firstTimestamp = firstRead!.UpdatedAt;

        // Small delay to ensure timestamp difference
        await Task.Delay(100);

        // Act
        await _manifestService.UpdateDoltCommitAsync(_testDirectory, "newcommit", "main");
        var secondRead = await _manifestService.ReadManifestAsync(_testDirectory);
        var secondTimestamp = secondRead!.UpdatedAt;

        // Assert
        Assert.That(secondTimestamp, Is.GreaterThan(firstTimestamp));
    }
}
