using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Services;
using DMMS.Models;

namespace DMMSTesting.UnitTests;

/// <summary>
/// PP13-79-C1: Unit tests for SyncStateChecker service.
/// Tests sync state comparison, safety checks, and warning generation.
/// </summary>
[TestFixture]
public class SyncStateCheckerTests
{
    private Mock<ILogger<SyncStateChecker>> _loggerMock = null!;
    private Mock<IDoltCli> _doltCliMock = null!;
    private Mock<ISyncManagerV2> _syncManagerMock = null!;
    private Mock<IDmmsStateManifest> _manifestServiceMock = null!;
    private Mock<IGitIntegration> _gitIntegrationMock = null!;
    private Mock<IOptions<ServerConfiguration>> _serverConfigMock = null!;
    private SyncStateChecker _syncStateChecker = null!;

    [SetUp]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<SyncStateChecker>>();
        _doltCliMock = new Mock<IDoltCli>();
        _syncManagerMock = new Mock<ISyncManagerV2>();
        _manifestServiceMock = new Mock<IDmmsStateManifest>();
        _gitIntegrationMock = new Mock<IGitIntegration>();
        _serverConfigMock = new Mock<IOptions<ServerConfiguration>>();

        var serverConfig = new ServerConfiguration
        {
            ProjectRoot = "C:\\TestProject",
            AutoDetectProjectRoot = false
        };
        _serverConfigMock.Setup(x => x.Value).Returns(serverConfig);

        _syncStateChecker = new SyncStateChecker(
            _loggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _manifestServiceMock.Object,
            _gitIntegrationMock.Object,
            _serverConfigMock.Object
        );
    }

    #region CheckSyncStateAsync Tests

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync returns IsInSync=true when no manifest exists")]
    public async Task CheckSyncStateAsync_NoManifest_ReturnsInSync()
    {
        // Arrange
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync((DmmsManifest?)null);

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.IsInSync, Is.True);
        Assert.That(result.ManifestExists, Is.False);
        Assert.That(result.Reason, Does.Contain("No manifest"));
    }

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync returns IsInSync=false when Dolt not initialized")]
    public async Task CheckSyncStateAsync_DoltNotInitialized_ReturnsNotInSync()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = "abc1234", CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(false);

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.IsInSync, Is.False);
        Assert.That(result.DoltInitialized, Is.False);
        Assert.That(result.Reason, Does.Contain("not initialized"));
    }

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync returns IsInSync=true when commits match")]
    public async Task CheckSyncStateAsync_CommitsMatch_ReturnsInSync()
    {
        // Arrange
        var commitHash = "abc123def456";
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = commitHash, CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync(commitHash);
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.IsInSync, Is.True);
        Assert.That(result.LocalCommit, Is.EqualTo(commitHash));
        Assert.That(result.ManifestCommit, Is.EqualTo(commitHash));
    }

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync returns IsInSync=false when commits differ")]
    public async Task CheckSyncStateAsync_CommitsDiffer_ReturnsNotInSync()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = "abc123", CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync("def456");
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.IsInSync, Is.False);
        Assert.That(result.LocalCommit, Is.EqualTo("def456"));
        Assert.That(result.ManifestCommit, Is.EqualTo("abc123"));
        Assert.That(result.Reason, Does.Contain("Commit differs"));
    }

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync returns IsInSync=false when branches differ")]
    public async Task CheckSyncStateAsync_BranchesDiffer_ReturnsNotInSync()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync("abc123");
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("feature");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.IsInSync, Is.False);
        Assert.That(result.LocalBranch, Is.EqualTo("feature"));
        Assert.That(result.ManifestBranch, Is.EqualTo("main"));
        Assert.That(result.Reason, Does.Contain("Branch differs"));
    }

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync correctly detects local changes")]
    public async Task CheckSyncStateAsync_HasLocalChanges_SetsFlag()
    {
        // Arrange
        var commitHash = "abc123";
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = commitHash, CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync(commitHash);
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument> { new ChromaDocument("doc1", "col1", "Test content", "hash1", new Dictionary<string, object>(), null) }, new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.HasLocalChanges, Is.True);
    }

    [Test]
    [Description("PP13-79-C1: CheckSyncStateAsync handles empty manifest commit gracefully")]
    public async Task CheckSyncStateAsync_EmptyManifestCommit_HandlesGracefully()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = null, CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync("abc123");
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // Act
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert - Should be in sync if manifest has no commit specified
        Assert.That(result.IsInSync, Is.True);
    }

    #endregion

    #region IsSafeToSyncAsync Tests

    [Test]
    [Description("PP13-79-C1: IsSafeToSyncAsync returns true when already in sync")]
    public async Task IsSafeToSyncAsync_AlreadyInSync_ReturnsTrue()
    {
        // Arrange
        var commitHash = "abc123";
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = commitHash, CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync(commitHash);
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // Act
        var result = await _syncStateChecker.IsSafeToSyncAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    [Description("PP13-79-C1: IsSafeToSyncAsync returns false when has local changes")]
    public async Task IsSafeToSyncAsync_HasLocalChanges_ReturnsFalse()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = "abc123", CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync("def456");
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument> { new ChromaDocument("doc1", "col1", "Test content", "hash1", new Dictionary<string, object>(), null) }, new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // Act
        var result = await _syncStateChecker.IsSafeToSyncAsync();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    [Description("PP13-79-C1: IsSafeToSyncAsync returns true when no local changes and not ahead")]
    public async Task IsSafeToSyncAsync_NoLocalChangesNotAhead_ReturnsTrue()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = "abc123", CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync("def456");
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));
        _doltCliMock.Setup(x => x.GetLogAsync(It.IsAny<int>())).ReturnsAsync(new List<CommitInfo>());

        // Act
        var result = await _syncStateChecker.IsSafeToSyncAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region GetOutOfSyncWarningAsync Tests

    [Test]
    [Description("PP13-79-C1: GetOutOfSyncWarningAsync returns null when in sync")]
    public async Task GetOutOfSyncWarningAsync_InSync_ReturnsNull()
    {
        // Arrange
        var commitHash = "abc123";
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = commitHash, CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync(commitHash);
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // Act
        var result = await _syncStateChecker.GetOutOfSyncWarningAsync();

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    [Description("PP13-79-C1: GetOutOfSyncWarningAsync returns warning when out of sync")]
    public async Task GetOutOfSyncWarningAsync_OutOfSync_ReturnsWarning()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = "abc123", CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync("def456");
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));
        _doltCliMock.Setup(x => x.GetLogAsync(It.IsAny<int>())).ReturnsAsync(new List<CommitInfo>());

        // Act
        var result = await _syncStateChecker.GetOutOfSyncWarningAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo("out_of_sync"));
        Assert.That(result.Message, Is.Not.Empty);
        Assert.That(result.ActionRequired, Is.Not.Null);
    }

    [Test]
    [Description("PP13-79-C1: GetOutOfSyncWarningAsync returns appropriate message for local changes")]
    public async Task GetOutOfSyncWarningAsync_HasLocalChanges_ReturnsCommitMessage()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = "abc123", CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync("def456");
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument> { new ChromaDocument("doc1", "col1", "Test content", "hash1", new Dictionary<string, object>(), null) }, new List<ChromaDocument>(), new List<DeletedDocumentV2>()));
        _doltCliMock.Setup(x => x.GetLogAsync(It.IsAny<int>())).ReturnsAsync(new List<CommitInfo>());

        // Act
        var result = await _syncStateChecker.GetOutOfSyncWarningAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Message, Does.Contain("uncommitted"));
        Assert.That(result.ActionRequired, Does.Contain("Commit"));
    }

    #endregion

    #region InvalidateCache Tests

    [Test]
    [Description("PP13-79-C1: InvalidateCache clears cached results")]
    public async Task InvalidateCache_ClearsCache_NextCallQueriesAgain()
    {
        // Arrange
        var commitHash = "abc123";
        var manifest = new DmmsManifest
        {
            Dolt = new DoltManifestConfig { CurrentCommit = commitHash, CurrentBranch = "main" }
        };
        _manifestServiceMock.Setup(x => x.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(manifest);
        _doltCliMock.Setup(x => x.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync(commitHash);
        _doltCliMock.Setup(x => x.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(x => x.GetLocalChangesAsync()).ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));

        // First call - populates cache
        await _syncStateChecker.CheckSyncStateAsync();

        // Invalidate
        _syncStateChecker.InvalidateCache();

        // Change mock return value
        _doltCliMock.Setup(x => x.GetHeadCommitHashAsync()).ReturnsAsync("newcommit");

        // Act - should get fresh data
        var result = await _syncStateChecker.CheckSyncStateAsync();

        // Assert
        Assert.That(result.LocalCommit, Is.EqualTo("newcommit"));
    }

    #endregion

    #region GetProjectRootAsync Tests

    [Test]
    [Description("PP13-79-C1: GetProjectRootAsync returns configured project root")]
    public async Task GetProjectRootAsync_ConfiguredProjectRoot_ReturnsConfigured()
    {
        // Act
        var result = await _syncStateChecker.GetProjectRootAsync();

        // Assert
        Assert.That(result, Is.EqualTo("C:\\TestProject"));
    }

    [Test]
    [Description("PP13-79-C1: GetProjectRootAsync auto-detects from Git when enabled")]
    public async Task GetProjectRootAsync_AutoDetectEnabled_QueriesGit()
    {
        // Arrange
        var serverConfig = new ServerConfiguration
        {
            ProjectRoot = null,
            AutoDetectProjectRoot = true
        };
        _serverConfigMock.Setup(x => x.Value).Returns(serverConfig);
        _gitIntegrationMock.Setup(x => x.GetGitRootAsync(It.IsAny<string>()))
            .ReturnsAsync("C:\\GitRoot");

        var checker = new SyncStateChecker(
            _loggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _manifestServiceMock.Object,
            _gitIntegrationMock.Object,
            _serverConfigMock.Object
        );

        // Act
        var result = await checker.GetProjectRootAsync();

        // Assert
        Assert.That(result, Is.EqualTo("C:\\GitRoot"));
    }

    #endregion
}
