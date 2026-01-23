using NUnit.Framework;
using Embranch.Models;
using System.Text.Json;

namespace EmbranchTesting.UnitTests;

/// <summary>
/// PP13-79: Unit tests for manifest model classes
/// </summary>
[TestFixture]
[Category("Unit")]
public class ManifestModelsTests
{
    [Test]
    public void DmmsManifest_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var manifest = new DmmsManifest();

        // Assert
        Assert.That(manifest.Version, Is.EqualTo("1.0"));
        Assert.That(manifest.Dolt, Is.Not.Null);
        Assert.That(manifest.GitMapping, Is.Not.Null);
        Assert.That(manifest.Initialization, Is.Not.Null);
        Assert.That(manifest.Collections, Is.Not.Null);
    }

    [Test]
    public void DoltManifestConfig_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new DoltManifestConfig();

        // Assert
        Assert.That(config.RemoteUrl, Is.Null);
        Assert.That(config.DefaultBranch, Is.EqualTo("main"));
        Assert.That(config.CurrentCommit, Is.Null);
        Assert.That(config.CurrentBranch, Is.Null);
    }

    [Test]
    public void GitMappingConfig_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new GitMappingConfig();

        // Assert
        Assert.That(config.Enabled, Is.True);
        Assert.That(config.LastGitCommit, Is.Null);
        Assert.That(config.DoltCommitAtGitCommit, Is.Null);
    }

    [Test]
    public void InitializationConfig_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new InitializationConfig();

        // Assert
        Assert.That(config.Mode, Is.EqualTo("auto"));
        Assert.That(config.OnClone, Is.EqualTo("sync_to_manifest"));
        Assert.That(config.OnBranchChange, Is.EqualTo("preserve_local"));
    }

    [Test]
    public void CollectionTrackingConfig_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new CollectionTrackingConfig();

        // Assert
        Assert.That(config.Tracked, Is.Not.Null);
        Assert.That(config.Tracked, Has.Count.EqualTo(1));
        Assert.That(config.Tracked[0], Is.EqualTo("*"));
        Assert.That(config.Excluded, Is.Not.Null);
        Assert.That(config.Excluded, Is.Empty);
    }

    [Test]
    public void DmmsManifest_Serialization_RoundTrips()
    {
        // Arrange
        var original = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = "dolthub.com/test/repo",
                DefaultBranch = "main",
                CurrentCommit = "abc123def456",
                CurrentBranch = "feature"
            },
            GitMapping = new GitMappingConfig
            {
                Enabled = true,
                LastGitCommit = "fedcba987654",
                DoltCommitAtGitCommit = "abc123def456"
            },
            Initialization = new InitializationConfig
            {
                Mode = "manual",
                OnClone = "prompt",
                OnBranchChange = "preserve_local"
            },
            Collections = new CollectionTrackingConfig
            {
                Tracked = new List<string> { "MyCollection", "Test*" },
                Excluded = new List<string> { "temp-*" }
            },
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = "test@example.com"
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        // Act
        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<DmmsManifest>(json, options);

        // Assert
        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.Version, Is.EqualTo(original.Version));
        Assert.That(deserialized.Dolt.RemoteUrl, Is.EqualTo(original.Dolt.RemoteUrl));
        Assert.That(deserialized.Dolt.DefaultBranch, Is.EqualTo(original.Dolt.DefaultBranch));
        Assert.That(deserialized.Dolt.CurrentCommit, Is.EqualTo(original.Dolt.CurrentCommit));
        Assert.That(deserialized.Dolt.CurrentBranch, Is.EqualTo(original.Dolt.CurrentBranch));
        Assert.That(deserialized.GitMapping.Enabled, Is.EqualTo(original.GitMapping.Enabled));
        Assert.That(deserialized.GitMapping.LastGitCommit, Is.EqualTo(original.GitMapping.LastGitCommit));
        Assert.That(deserialized.Initialization.Mode, Is.EqualTo(original.Initialization.Mode));
        Assert.That(deserialized.Initialization.OnClone, Is.EqualTo(original.Initialization.OnClone));
        Assert.That(deserialized.Collections.Tracked, Is.EqualTo(original.Collections.Tracked));
        Assert.That(deserialized.Collections.Excluded, Is.EqualTo(original.Collections.Excluded));
    }

    [Test]
    [TestCase("auto", true)]
    [TestCase("prompt", true)]
    [TestCase("manual", true)]
    [TestCase("disabled", true)]
    [TestCase("invalid", false)]
    [TestCase("AUTO", false)]
    [TestCase("", false)]
    public void InitializationMode_IsValid_ReturnsCorrectly(string mode, bool expected)
    {
        // Act
        var result = InitializationMode.IsValid(mode);

        // Assert
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    [TestCase("sync_to_manifest", true)]
    [TestCase("sync_to_latest", true)]
    [TestCase("empty", true)]
    [TestCase("prompt", true)]
    [TestCase("invalid", false)]
    [TestCase("", false)]
    public void OnCloneBehavior_IsValid_ReturnsCorrectly(string behavior, bool expected)
    {
        // Act
        var result = OnCloneBehavior.IsValid(behavior);

        // Assert
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    [TestCase("preserve_local", true)]
    [TestCase("sync_to_manifest", true)]
    [TestCase("prompt", true)]
    [TestCase("invalid", false)]
    [TestCase("", false)]
    public void OnBranchChangeBehavior_IsValid_ReturnsCorrectly(string behavior, bool expected)
    {
        // Act
        var result = OnBranchChangeBehavior.IsValid(behavior);

        // Assert
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void InitializationCheck_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var check = new InitializationCheck();

        // Assert
        Assert.That(check.NeedsInitialization, Is.False);
        Assert.That(check.Reason, Is.EqualTo(""));
        Assert.That(check.CurrentDoltCommit, Is.Null);
        Assert.That(check.ManifestDoltCommit, Is.Null);
        Assert.That(check.CurrentBranch, Is.Null);
        Assert.That(check.ManifestBranch, Is.Null);
    }

    [Test]
    public void InitializationResult_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var result = new InitializationResult();

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ActionTaken, Is.EqualTo(InitializationAction.None));
        Assert.That(result.DoltCommit, Is.Null);
        Assert.That(result.DoltBranch, Is.Null);
        Assert.That(result.CollectionsSynced, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.Null);
    }

    [Test]
    public void InitializationAction_AllValues_AreDefined()
    {
        // Arrange
        var expectedValues = new[]
        {
            InitializationAction.None,
            InitializationAction.ClonedAndSynced,
            InitializationAction.FetchedAndSynced,
            InitializationAction.CheckedOutBranch,
            InitializationAction.CheckedOutCommit,
            InitializationAction.SyncedExisting,
            InitializationAction.Skipped,
            InitializationAction.Failed
        };

        // Act
        var actualValues = Enum.GetValues<InitializationAction>();

        // Assert
        Assert.That(actualValues, Is.EquivalentTo(expectedValues));
    }

    [Test]
    public void DmmsManifest_WithInitializer_SetsAllFields()
    {
        // Arrange & Act
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = "test-url",
                DefaultBranch = "develop",
                CurrentCommit = "commit123",
                CurrentBranch = "feature"
            }
        };

        // Assert
        Assert.That(manifest.Dolt.RemoteUrl, Is.EqualTo("test-url"));
        Assert.That(manifest.Dolt.DefaultBranch, Is.EqualTo("develop"));
        Assert.That(manifest.Dolt.CurrentCommit, Is.EqualTo("commit123"));
        Assert.That(manifest.Dolt.CurrentBranch, Is.EqualTo("feature"));
    }

    [Test]
    public void DmmsManifest_RecordWith_CreatesModifiedCopy()
    {
        // Arrange
        var original = new DmmsManifest
        {
            Version = "1.0",
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };

        // Act
        var modified = original with
        {
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = "modifier@example.com"
        };

        // Assert
        Assert.That(modified.Version, Is.EqualTo(original.Version));
        Assert.That(modified.UpdatedAt, Is.GreaterThan(original.UpdatedAt));
        Assert.That(modified.UpdatedBy, Is.EqualTo("modifier@example.com"));
        Assert.That(original.UpdatedBy, Is.Null); // Original unchanged
    }
}
