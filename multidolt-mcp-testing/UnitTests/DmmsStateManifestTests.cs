using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Moq;
using DMMS.Models;
using DMMS.Services;
using System.Text.Json;

namespace DMMSTesting.UnitTests;

/// <summary>
/// PP13-79: Unit tests for DmmsStateManifest service
/// </summary>
[TestFixture]
[Category("Unit")]
public class DmmsStateManifestTests
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
        _testDirectory = Path.Combine(Path.GetTempPath(), "DmmsManifestTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up test directory
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
    public void GetManifestPath_ReturnsCorrectPath()
    {
        // Arrange
        var projectPath = "/test/project";

        // Act
        var result = _manifestService.GetManifestPath(projectPath);

        // Assert
        var expected = Path.Combine(projectPath, ".dmms", "state.json");
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void GetDmmsDirectoryPath_ReturnsCorrectPath()
    {
        // Arrange
        var projectPath = "/test/project";

        // Act
        var result = _manifestService.GetDmmsDirectoryPath(projectPath);

        // Assert
        var expected = Path.Combine(projectPath, ".dmms");
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public async Task ReadManifestAsync_MissingFile_ReturnsNull()
    {
        // Arrange - test directory has no manifest

        // Act
        var result = await _manifestService.ReadManifestAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ReadManifestAsync_ValidFile_ReturnsManifest()
    {
        // Arrange
        var manifest = CreateTestManifest();
        await WriteTestManifest(manifest);

        // Act
        var result = await _manifestService.ReadManifestAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Version, Is.EqualTo("1.0"));
        Assert.That(result.Dolt.RemoteUrl, Is.EqualTo("dolthub.com/test/repo"));
    }

    [Test]
    public async Task ReadManifestAsync_InvalidJson_ReturnsNull()
    {
        // Arrange
        var dmmsDir = Path.Combine(_testDirectory, ".dmms");
        Directory.CreateDirectory(dmmsDir);
        await File.WriteAllTextAsync(Path.Combine(dmmsDir, "state.json"), "{ invalid json }");

        // Act
        var result = await _manifestService.ReadManifestAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ReadManifestAsync_EmptyFile_ReturnsNull()
    {
        // Arrange
        var dmmsDir = Path.Combine(_testDirectory, ".dmms");
        Directory.CreateDirectory(dmmsDir);
        await File.WriteAllTextAsync(Path.Combine(dmmsDir, "state.json"), "");

        // Act
        var result = await _manifestService.ReadManifestAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task WriteManifestAsync_CreatesDirectory_AndFile()
    {
        // Arrange
        var manifest = CreateTestManifest();

        // Act
        await _manifestService.WriteManifestAsync(_testDirectory, manifest);

        // Assert
        var manifestPath = _manifestService.GetManifestPath(_testDirectory);
        Assert.That(File.Exists(manifestPath), Is.True);

        var content = await File.ReadAllTextAsync(manifestPath);
        Assert.That(content, Does.Contain("1.0"));
    }

    [Test]
    public async Task WriteManifestAsync_OverwritesExisting()
    {
        // Arrange
        var originalManifest = CreateTestManifest();
        await _manifestService.WriteManifestAsync(_testDirectory, originalManifest);

        var updatedManifest = originalManifest with
        {
            Dolt = originalManifest.Dolt with { CurrentCommit = "newcommit123" }
        };

        // Act
        await _manifestService.WriteManifestAsync(_testDirectory, updatedManifest);

        // Assert
        var result = await _manifestService.ReadManifestAsync(_testDirectory);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Dolt.CurrentCommit, Is.EqualTo("newcommit123"));
    }

    [Test]
    public async Task ManifestExistsAsync_NoManifest_ReturnsFalse()
    {
        // Act
        var result = await _manifestService.ManifestExistsAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task ManifestExistsAsync_ManifestExists_ReturnsTrue()
    {
        // Arrange
        await _manifestService.WriteManifestAsync(_testDirectory, CreateTestManifest());

        // Act
        var result = await _manifestService.ManifestExistsAsync(_testDirectory);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task UpdateDoltCommitAsync_UpdatesCorrectFields()
    {
        // Arrange
        var manifest = CreateTestManifest();
        await _manifestService.WriteManifestAsync(_testDirectory, manifest);

        // Act
        await _manifestService.UpdateDoltCommitAsync(_testDirectory, "newcommit456", "develop");

        // Assert
        var result = await _manifestService.ReadManifestAsync(_testDirectory);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Dolt.CurrentCommit, Is.EqualTo("newcommit456"));
        Assert.That(result.Dolt.CurrentBranch, Is.EqualTo("develop"));
    }

    [Test]
    public async Task UpdateDoltCommitAsync_NoManifest_DoesNotThrow()
    {
        // Act & Assert - should not throw
        await _manifestService.UpdateDoltCommitAsync(_testDirectory, "commit123", "main");
    }

    [Test]
    public async Task RecordGitMappingAsync_UpdatesMapping()
    {
        // Arrange
        var manifest = CreateTestManifest() with
        {
            GitMapping = new GitMappingConfig { Enabled = true }
        };
        await _manifestService.WriteManifestAsync(_testDirectory, manifest);

        // Act
        await _manifestService.RecordGitMappingAsync(_testDirectory, "gitcommit123", "doltcommit456");

        // Assert
        var result = await _manifestService.ReadManifestAsync(_testDirectory);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.GitMapping.LastGitCommit, Is.EqualTo("gitcommit123"));
        Assert.That(result.GitMapping.DoltCommitAtGitCommit, Is.EqualTo("doltcommit456"));
    }

    [Test]
    public async Task RecordGitMappingAsync_WhenDisabled_DoesNotUpdate()
    {
        // Arrange
        var manifest = CreateTestManifest() with
        {
            GitMapping = new GitMappingConfig { Enabled = false }
        };
        await _manifestService.WriteManifestAsync(_testDirectory, manifest);

        // Act
        await _manifestService.RecordGitMappingAsync(_testDirectory, "gitcommit123", "doltcommit456");

        // Assert
        var result = await _manifestService.ReadManifestAsync(_testDirectory);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.GitMapping.LastGitCommit, Is.Null);
    }

    [Test]
    public void ValidateManifest_ValidManifest_ReturnsTrue()
    {
        // Arrange
        var manifest = CreateTestManifest();

        // Act
        var result = _manifestService.ValidateManifest(manifest);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void ValidateManifest_InvalidVersion_ReturnsFalse()
    {
        // Arrange
        var manifest = new DmmsManifest { Version = "2.0" };

        // Act
        var result = _manifestService.ValidateManifest(manifest);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void ValidateManifest_EmptyVersion_ReturnsFalse()
    {
        // Arrange
        var manifest = new DmmsManifest { Version = "" };

        // Act
        var result = _manifestService.ValidateManifest(manifest);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void ValidateManifest_InvalidInitMode_ReturnsFalse()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Initialization = new InitializationConfig { Mode = "invalid" }
        };

        // Act
        var result = _manifestService.ValidateManifest(manifest);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void ValidateManifest_InvalidOnClone_ReturnsFalse()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Initialization = new InitializationConfig { OnClone = "invalid" }
        };

        // Act
        var result = _manifestService.ValidateManifest(manifest);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void CreateDefaultManifest_WithDefaults_ReturnsValidManifest()
    {
        // Act
        var manifest = _manifestService.CreateDefaultManifest();

        // Assert
        Assert.That(manifest.Version, Is.EqualTo("1.0"));
        Assert.That(manifest.Dolt.DefaultBranch, Is.EqualTo("main"));
        Assert.That(manifest.Dolt.RemoteUrl, Is.Null);
        Assert.That(manifest.Initialization.Mode, Is.EqualTo("auto"));
        Assert.That(manifest.GitMapping.Enabled, Is.True);
        Assert.That(_manifestService.ValidateManifest(manifest), Is.True);
    }

    [Test]
    public void CreateDefaultManifest_WithParameters_ReturnsConfiguredManifest()
    {
        // Act
        var manifest = _manifestService.CreateDefaultManifest(
            remoteUrl: "dolthub.com/org/repo",
            defaultBranch: "develop",
            initMode: "manual");

        // Assert
        Assert.That(manifest.Dolt.RemoteUrl, Is.EqualTo("dolthub.com/org/repo"));
        Assert.That(manifest.Dolt.DefaultBranch, Is.EqualTo("develop"));
        Assert.That(manifest.Initialization.Mode, Is.EqualTo("manual"));
    }

    private DmmsManifest CreateTestManifest()
    {
        return new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = "dolthub.com/test/repo",
                DefaultBranch = "main",
                CurrentCommit = "abc123",
                CurrentBranch = "main"
            },
            GitMapping = new GitMappingConfig
            {
                Enabled = true
            },
            Initialization = new InitializationConfig
            {
                Mode = "auto",
                OnClone = "sync_to_manifest",
                OnBranchChange = "preserve_local"
            },
            Collections = new CollectionTrackingConfig
            {
                Tracked = new List<string> { "*" },
                Excluded = new List<string>()
            },
            UpdatedAt = DateTime.UtcNow
        };
    }

    private async Task WriteTestManifest(DmmsManifest manifest)
    {
        var dmmsDir = Path.Combine(_testDirectory, ".dmms");
        Directory.CreateDirectory(dmmsDir);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        var json = JsonSerializer.Serialize(manifest, options);
        await File.WriteAllTextAsync(Path.Combine(dmmsDir, "state.json"), json);
    }
}
