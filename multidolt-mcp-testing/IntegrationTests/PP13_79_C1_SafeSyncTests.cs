using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Services;
using DMMS.Models;

namespace DMMSTesting.IntegrationTests;

/// <summary>
/// PP13-79-C1: Integration tests for safe sync logic.
/// Tests that sync operations respect local state and don't lose uncommitted changes.
/// </summary>
[TestFixture]
public class PP13_79_C1_SafeSyncTests
{
    private Mock<ILogger<SyncStateChecker>> _loggerMock = null!;
    private Mock<IDoltCli> _doltCliMock = null!;
    private Mock<ISyncManagerV2> _syncManagerMock = null!;
    private Mock<IDmmsStateManifest> _manifestMock = null!;
    private Mock<IGitIntegration> _gitIntegrationMock = null!;
    private IOptions<ServerConfiguration> _serverConfigOptions = null!;
    private SyncStateChecker _syncStateChecker = null!;
    private const string TestProjectRoot = "D:\\TestProject";

    [SetUp]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<SyncStateChecker>>();
        _doltCliMock = new Mock<IDoltCli>();
        _syncManagerMock = new Mock<ISyncManagerV2>();
        _manifestMock = new Mock<IDmmsStateManifest>();
        _gitIntegrationMock = new Mock<IGitIntegration>();

        var serverConfig = new ServerConfiguration { ProjectRoot = TestProjectRoot };
        _serverConfigOptions = Options.Create(serverConfig);

        _syncStateChecker = new SyncStateChecker(
            _loggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _manifestMock.Object,
            _gitIntegrationMock.Object,
            _serverConfigOptions
        );

        // Default setup - Dolt is initialized
        _doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);

        // Default setup - no local changes
        _syncManagerMock.Setup(s => s.GetLocalChangesAsync())
            .ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));
    }

    #region IsSafeToSyncAsync Tests

    [Test]
    [Description("PP13-79-C1: Safe to sync when in sync with no local changes")]
    public async Task IsSafeToSyncAsync_InSyncNoChanges_ReturnsTrue()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc123",
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");

        // Act
        var result = await _syncStateChecker.IsSafeToSyncAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    [Description("PP13-79-C1: Not safe to sync when local has uncommitted changes and out of sync")]
    public async Task IsSafeToSyncAsync_HasUncommittedChanges_ReturnsFalse()
    {
        // Arrange - commits differ (out of sync) AND has local changes
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "manifestcommit",  // Different from local
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("localcommit");  // Different from manifest
        // Set up local changes - with a new document
        var newDoc = new ChromaDocument("doc1", "collection1", "Test content", "hash1", new Dictionary<string, object>(), null);
        _syncManagerMock.Setup(s => s.GetLocalChangesAsync())
            .ReturnsAsync(new LocalChanges(new List<ChromaDocument> { newDoc }, new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // Act
        var result = await _syncStateChecker.IsSafeToSyncAsync();

        // Assert - Not safe because out of sync AND has local changes
        Assert.That(result, Is.False);
    }

    [Test]
    [Description("PP13-79-C1: Not safe to sync when local is ahead of manifest")]
    public async Task IsSafeToSyncAsync_LocalAheadOfManifest_ReturnsFalse()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "oldcommit",
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("newcommit");
        _doltCliMock.Setup(d => d.GetLogAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<CommitInfo>
            {
                new CommitInfo("newcommit", "New commit", "Test Author", DateTime.UtcNow),
                new CommitInfo("oldcommit", "Old commit", "Test Author", DateTime.UtcNow.AddMinutes(-1))
            });

        // Act
        var result = await _syncStateChecker.IsSafeToSyncAsync();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    [Description("PP13-79-C1: Safe to sync when no manifest exists (first run)")]
    public async Task IsSafeToSyncAsync_NoManifest_ReturnsTrue()
    {
        // Arrange
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync((DmmsManifest?)null);

        // Act
        var result = await _syncStateChecker.IsSafeToSyncAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    [Description("PP13-79-C1: Safe to sync when manifest has null commit (new manifest)")]
    public async Task IsSafeToSyncAsync_ManifestNullCommit_ReturnsTrue()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = null,
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);

        // Act
        var result = await _syncStateChecker.IsSafeToSyncAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region CheckSyncStateAsync Detailed Tests

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync detects branch mismatch")]
    public async Task CheckSyncStateAsync_BranchMismatch_DetectsMismatch()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc123",
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("feature");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.IsInSync, Is.False);
        Assert.That(result.LocalBranch, Is.EqualTo("feature"));
        Assert.That(result.ManifestBranch, Is.EqualTo("main"));
    }

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync detects commit mismatch")]
    public async Task CheckSyncStateAsync_CommitMismatch_DetectsMismatch()
    {
        // Arrange - set up mocks with different commit values
        // Note: Commits must be 7+ characters for Substring(0, 7) in SyncStateChecker
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc1234567890", // 13 chars
                CurrentBranch = "main"
            }
        };

        _manifestMock.Setup(m => m.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("def4567890123"); // 13 chars, different from manifest

        // Create fresh instance with updated mocks
        var syncStateChecker = new SyncStateChecker(
            _loggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _manifestMock.Object,
            _gitIntegrationMock.Object,
            _serverConfigOptions
        );

        // Act
        var result = await syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.IsInSync, Is.False);
        Assert.That(result.LocalCommit, Is.EqualTo("def4567890123"));
        Assert.That(result.ManifestCommit, Is.EqualTo("abc1234567890"));
    }

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync reports uncommitted changes")]
    public async Task CheckSyncStateAsync_UncommittedChanges_ReportsChanges()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc123",
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");
        var newDoc = new ChromaDocument("doc2", "collection1", "Test content", "hash2", new Dictionary<string, object>(), null);
        _syncManagerMock.Setup(s => s.GetLocalChangesAsync())
            .ReturnsAsync(new LocalChanges(new List<ChromaDocument> { newDoc }, new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.HasLocalChanges, Is.True);
    }

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync handles exception gracefully")]
    public async Task CheckSyncStateAsync_DoltException_ReturnsErrorState()
    {
        // Arrange
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ThrowsAsync(new Exception("Manifest read failed"));

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.IsInSync, Is.True); // Returns true on error per implementation
        Assert.That(result.Reason, Does.Contain("Error"));
    }

    #endregion

    #region Cache Behavior Tests

    [Test]
    [Description("PP13-79-C1: Repeated calls use cached result")]
    public async Task CheckSyncStateAsync_RepeatedCalls_UsesCachedResult()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc123",
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");

        // Act - Call twice
        await _syncStateChecker.CheckSyncStateAsync();
        await _syncStateChecker.CheckSyncStateAsync();

        // Assert - Should only call manifest once due to caching
        _manifestMock.Verify(m => m.ReadManifestAsync(TestProjectRoot), Times.Once);
    }

    [Test]
    [Description("PP13-79-C1: InvalidateCache forces fresh check")]
    public async Task InvalidateCache_ThenCheck_FetchesFreshData()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc123",
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");

        // Act
        await _syncStateChecker.CheckSyncStateAsync();
        _syncStateChecker.InvalidateCache();
        await _syncStateChecker.CheckSyncStateAsync();

        // Assert - Should call manifest twice (once before invalidate, once after)
        _manifestMock.Verify(m => m.ReadManifestAsync(TestProjectRoot), Times.Exactly(2));
    }

    #endregion

    #region GetOutOfSyncWarningAsync Tests

    [Test]
    [Description("PP13-79-C1: GetOutOfSyncWarningAsync returns null when in sync")]
    public async Task GetOutOfSyncWarningAsync_InSync_ReturnsNull()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc123",
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");

        // Act
        var warning = await _syncStateChecker.GetOutOfSyncWarningAsync();

        // Assert
        Assert.That(warning, Is.Null);
    }

    [Test]
    [Description("PP13-79-C1: GetOutOfSyncWarningAsync returns warning when out of sync")]
    public async Task GetOutOfSyncWarningAsync_OutOfSync_ReturnsWarning()
    {
        // Arrange - set up out of sync state (different branch and commit)
        // Note: Commits must be 7+ characters for Substring(0, 7) in SyncStateChecker
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc1234567890",
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("feature");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("def4567890123");

        // Create fresh instance with updated mocks
        var syncStateChecker = new SyncStateChecker(
            _loggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _manifestMock.Object,
            _gitIntegrationMock.Object,
            _serverConfigOptions
        );

        // Act
        var warning = await syncStateChecker.GetOutOfSyncWarningAsync();

        // Assert
        Assert.That(warning, Is.Not.Null);
        Assert.That(warning!.Message, Is.Not.Empty);
        Assert.That(warning.LocalState, Is.Not.Null);
    }

    [Test]
    [Description("PP13-79-C1: GetOutOfSyncWarningAsync includes action suggestion")]
    public async Task GetOutOfSyncWarningAsync_OutOfSync_IncludesActionSuggestion()
    {
        // Arrange - set up out of sync state (different commit)
        // Note: Commits must be 7+ characters for Substring(0, 7) in SyncStateChecker
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc1234567890",
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("different1234567"); // 15 chars, different from manifest

        // Create fresh instance with updated mocks
        var syncStateChecker = new SyncStateChecker(
            _loggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _manifestMock.Object,
            _gitIntegrationMock.Object,
            _serverConfigOptions
        );

        // Act
        var warning = await syncStateChecker.GetOutOfSyncWarningAsync();

        // Assert
        Assert.That(warning, Is.Not.Null);
        Assert.That(warning!.ActionRequired, Is.Not.Null.And.Not.Empty);
    }

    #endregion

    #region Edge Cases

    [Test]
    [Description("PP13-79-C1: Handles empty project root gracefully")]
    public async Task CheckSyncStateAsync_EmptyProjectRoot_HandlesGracefully()
    {
        // Arrange - Create a new checker with empty project root
        var emptyConfig = new ServerConfiguration { ProjectRoot = "" };
        var emptyConfigOptions = Options.Create(emptyConfig);
        var checkerWithEmptyRoot = new SyncStateChecker(
            _loggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _manifestMock.Object,
            _gitIntegrationMock.Object,
            emptyConfigOptions
        );

        // Act
        var result = await checkerWithEmptyRoot.CheckSyncStateAsync();

        // Assert - Should handle gracefully (falls back to current directory)
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    [Description("PP13-79-C1: Handles empty branch name")]
    public async Task CheckSyncStateAsync_EmptyBranch_HandlesGracefully()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc123",
                CurrentBranch = ""
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync())
            .ReturnsAsync("main");
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync())
            .ReturnsAsync("abc123");

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert - Empty manifest branch means "match any", so should be in sync
        Assert.That(result.IsInSync, Is.True);
    }

    [Test]
    [Description("PP13-79-C1: Handles Dolt not initialized")]
    public async Task CheckSyncStateAsync_DoltNotInitialized_ReturnsNotInSync()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc123",
                CurrentBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.ReadManifestAsync(TestProjectRoot))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(d => d.IsInitializedAsync())
            .ReturnsAsync(false);

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.IsInSync, Is.False);
        Assert.That(result.DoltInitialized, Is.False);
    }

    #endregion
}
