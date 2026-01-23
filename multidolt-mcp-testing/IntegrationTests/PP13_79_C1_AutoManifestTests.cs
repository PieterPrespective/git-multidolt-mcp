using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Embranch.Services;
using Embranch.Models;
using System.Text.Json;

namespace EmbranchTesting.IntegrationTests;

/// <summary>
/// PP13-79-C1: Integration tests for automatic manifest creation.
/// Tests that manifests are auto-created on first run and properly configured.
/// </summary>
[TestFixture]
public class PP13_79_C1_AutoManifestTests
{
    private string _testProjectRoot = null!;
    private Mock<ILogger<EmbranchStateManifest>> _loggerMock = null!;
    private EmbranchStateManifest _manifestService = null!;

    [SetUp]
    public void Setup()
    {
        _testProjectRoot = Path.Combine(Path.GetTempPath(), $"PP13_79_C1_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testProjectRoot);

        _loggerMock = new Mock<ILogger<EmbranchStateManifest>>();

        _manifestService = new EmbranchStateManifest(_loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_testProjectRoot))
            {
                Directory.Delete(_testProjectRoot, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    #region Manifest Creation Tests

    [Test]
    [Description("PP13-79-C1: CreateDefaultManifest creates manifest with default values")]
    public void CreateDefaultManifest_NoParameters_CreatesDefaults()
    {
        // Act
        var manifest = _manifestService.CreateDefaultManifest();

        // Assert
        Assert.That(manifest, Is.Not.Null);
        Assert.That(manifest.Version, Is.EqualTo("1.0"));
        Assert.That(manifest.Dolt.DefaultBranch, Is.EqualTo("main"));
        Assert.That(manifest.Initialization.Mode, Is.EqualTo("auto"));
    }

    [Test]
    [Description("PP13-79-C1: CreateDefaultManifest uses provided remote URL")]
    public void CreateDefaultManifest_WithRemoteUrl_SetsRemoteUrl()
    {
        // Act
        var manifest = _manifestService.CreateDefaultManifest(
            remoteUrl: "https://doltremoteapi.dolthub.com/org/repo"
        );

        // Assert
        Assert.That(manifest.Dolt.RemoteUrl, Is.EqualTo("https://doltremoteapi.dolthub.com/org/repo"));
    }

    [Test]
    [Description("PP13-79-C1: CreateDefaultManifest uses provided default branch")]
    public void CreateDefaultManifest_WithDefaultBranch_SetsBranch()
    {
        // Act
        var manifest = _manifestService.CreateDefaultManifest(
            defaultBranch: "develop"
        );

        // Assert
        Assert.That(manifest.Dolt.DefaultBranch, Is.EqualTo("develop"));
    }

    [Test]
    [Description("PP13-79-C1: CreateDefaultManifest uses provided init mode")]
    public void CreateDefaultManifest_WithInitMode_SetsMode()
    {
        // Act
        var manifest = _manifestService.CreateDefaultManifest(
            initMode: "manual"
        );

        // Assert
        Assert.That(manifest.Initialization.Mode, Is.EqualTo("manual"));
    }

    [Test]
    [Description("PP13-79-C1: CreateDefaultManifest with all parameters sets all values")]
    public void CreateDefaultManifest_AllParameters_SetsAllValues()
    {
        // Act
        var manifest = _manifestService.CreateDefaultManifest(
            remoteUrl: "https://example.com/repo",
            defaultBranch: "production",
            initMode: "disabled"
        );

        // Assert
        Assert.That(manifest.Dolt.RemoteUrl, Is.EqualTo("https://example.com/repo"));
        Assert.That(manifest.Dolt.DefaultBranch, Is.EqualTo("production"));
        Assert.That(manifest.Initialization.Mode, Is.EqualTo("disabled"));
    }

    #endregion

    #region Manifest Write/Read Tests

    [Test]
    [Description("PP13-79-C1: WriteManifestAsync creates .dmms directory and state.json")]
    public async Task WriteManifestAsync_CreatesDirectoryAndFile()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest();

        // Act
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Assert
        var dmmsPath = Path.Combine(_testProjectRoot, ".dmms");
        var manifestPath = Path.Combine(dmmsPath, "state.json");

        Assert.That(Directory.Exists(dmmsPath), Is.True, ".dmms directory should exist");
        Assert.That(File.Exists(manifestPath), Is.True, "state.json should exist");
    }

    [Test]
    [Description("PP13-79-C1: WriteManifestAsync writes valid JSON")]
    public async Task WriteManifestAsync_WritesValidJson()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest(
            remoteUrl: "https://test.com/repo"
        );

        // Act
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Assert
        var manifestPath = Path.Combine(_testProjectRoot, ".dmms", "state.json");
        var json = await File.ReadAllTextAsync(manifestPath);

        // Should not throw
        var parsed = JsonSerializer.Deserialize<DmmsManifest>(json);
        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.Dolt.RemoteUrl, Is.EqualTo("https://test.com/repo"));
    }

    [Test]
    [Description("PP13-79-C1: ReadManifestAsync reads existing manifest")]
    public async Task ReadManifestAsync_ExistingManifest_ReadsCorrectly()
    {
        // Arrange
        var originalManifest = _manifestService.CreateDefaultManifest(
            remoteUrl: "https://read.test/repo",
            defaultBranch: "develop"
        );
        await _manifestService.WriteManifestAsync(_testProjectRoot, originalManifest);

        // Act
        var readManifest = await _manifestService.ReadManifestAsync(_testProjectRoot);

        // Assert
        Assert.That(readManifest, Is.Not.Null);
        Assert.That(readManifest!.Dolt.RemoteUrl, Is.EqualTo("https://read.test/repo"));
        Assert.That(readManifest.Dolt.DefaultBranch, Is.EqualTo("develop"));
    }

    [Test]
    [Description("PP13-79-C1: ReadManifestAsync returns null when no manifest exists")]
    public async Task ReadManifestAsync_NoManifest_ReturnsNull()
    {
        // Act
        var result = await _manifestService.ReadManifestAsync(_testProjectRoot);

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region ManifestExistsAsync Tests

    [Test]
    [Description("PP13-79-C1: ManifestExistsAsync returns false when no manifest")]
    public async Task ManifestExistsAsync_NoManifest_ReturnsFalse()
    {
        // Act
        var result = await _manifestService.ManifestExistsAsync(_testProjectRoot);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    [Description("PP13-79-C1: ManifestExistsAsync returns true when manifest exists")]
    public async Task ManifestExistsAsync_ManifestExists_ReturnsTrue()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest();
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act
        var result = await _manifestService.ManifestExistsAsync(_testProjectRoot);

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region UpdateDoltCommitAsync Tests

    [Test]
    [Description("PP13-79-C1: UpdateDoltCommitAsync updates commit in existing manifest")]
    public async Task UpdateDoltCommitAsync_ExistingManifest_UpdatesCommit()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest();
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "newcommit123", "main");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Dolt.CurrentCommit, Is.EqualTo("newcommit123"));
        Assert.That(updated.Dolt.CurrentBranch, Is.EqualTo("main"));
    }

    [Test]
    [Description("PP13-79-C1: UpdateDoltCommitAsync preserves other manifest values")]
    public async Task UpdateDoltCommitAsync_PreservesOtherValues()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest(
            remoteUrl: "https://preserve.test/repo"
        );
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "newcommit", "feature");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Dolt.RemoteUrl, Is.EqualTo("https://preserve.test/repo"));
        Assert.That(updated.Dolt.CurrentCommit, Is.EqualTo("newcommit"));
    }

    [Test]
    [Description("PP13-79-C1: UpdateDoltCommitAsync updates UpdatedAt timestamp")]
    public async Task UpdateDoltCommitAsync_UpdatesTimestamp()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest();
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);
        var original = await _manifestService.ReadManifestAsync(_testProjectRoot);
        var originalTime = original!.UpdatedAt;

        await Task.Delay(100); // Ensure time passes

        // Act
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "commit", "main");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.UpdatedAt, Is.GreaterThan(originalTime));
    }

    #endregion

    #region GetManifestPath Tests

    [Test]
    [Description("PP13-79-C1: GetManifestPath returns correct path")]
    public void GetManifestPath_ReturnsCorrectPath()
    {
        // Act
        var path = _manifestService.GetManifestPath(_testProjectRoot);

        // Assert
        var expected = Path.Combine(_testProjectRoot, ".dmms", "state.json");
        Assert.That(path, Is.EqualTo(expected));
    }

    #endregion

    #region Edge Cases

    [Test]
    [Description("PP13-79-C1: Manifest survives round-trip serialization")]
    public async Task Manifest_RoundTrip_PreservesAllData()
    {
        // Arrange
        var original = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = "https://roundtrip.test/repo",
                DefaultBranch = "main",
                CurrentCommit = "abc123def456",
                CurrentBranch = "feature"
            },
            GitMapping = new GitMappingConfig
            {
                Enabled = true,
                LastGitCommit = "gitcommit123"
            },
            Initialization = new InitializationConfig
            {
                Mode = "auto",
                OnClone = "sync_to_manifest"
            },
            Collections = new CollectionTrackingConfig
            {
                Tracked = new List<string> { "collection1", "collection2" },
                Excluded = new List<string> { "temp_*" }
            }
        };

        // Act
        await _manifestService.WriteManifestAsync(_testProjectRoot, original);
        var roundTripped = await _manifestService.ReadManifestAsync(_testProjectRoot);

        // Assert
        Assert.That(roundTripped!.Version, Is.EqualTo(original.Version));
        Assert.That(roundTripped.Dolt.RemoteUrl, Is.EqualTo(original.Dolt.RemoteUrl));
        Assert.That(roundTripped.Dolt.CurrentCommit, Is.EqualTo(original.Dolt.CurrentCommit));
        Assert.That(roundTripped.GitMapping.Enabled, Is.EqualTo(original.GitMapping.Enabled));
        Assert.That(roundTripped.Collections.Tracked, Is.EquivalentTo(original.Collections.Tracked));
    }

    [Test]
    [Description("PP13-79-C1: Multiple writes update correctly")]
    public async Task MultipleWrites_UpdateCorrectly()
    {
        // Arrange
        var manifest1 = _manifestService.CreateDefaultManifest();
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest1);

        // Act - Update multiple times
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "commit1", "branch1");
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "commit2", "branch2");
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "commit3", "branch3");

        // Assert
        var final = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(final!.Dolt.CurrentCommit, Is.EqualTo("commit3"));
        Assert.That(final.Dolt.CurrentBranch, Is.EqualTo("branch3"));
    }

    #endregion
}
