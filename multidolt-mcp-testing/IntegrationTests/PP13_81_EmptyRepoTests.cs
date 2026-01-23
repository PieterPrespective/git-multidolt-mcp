using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Embranch.Services;
using Embranch.Models;
using Embranch.Tools;

namespace EmbranchTesting.IntegrationTests;

/// <summary>
/// PP13-81: Integration tests for empty repository initialization blocking clone operations.
/// Tests the four-part fix:
/// 1. Prevent empty init when no remote URL configured
/// 2. Force clone option to overwrite existing empty repos
/// 3. ManifestSetRemote tool functionality
/// 4. E2E workflows for recovery from empty repo state
/// </summary>
[TestFixture]
public class PP13_81_EmptyRepoTests
{
    private Mock<ILogger<EmbranchInitializer>> _initializerLoggerMock = null!;
    private Mock<IDoltCli> _doltCliMock = null!;
    private Mock<ISyncManagerV2> _syncManagerMock = null!;
    private Mock<IEmbranchStateManifest> _manifestMock = null!;
    private Mock<IGitIntegration> _gitIntegrationMock = null!;
    private Mock<ISyncStateTracker> _syncStateTrackerMock = null!;
    private Mock<ISyncStateChecker> _syncStateCheckerMock = null!;
    private IOptions<DoltConfiguration> _doltConfigOptions = null!;
    private const string TestProjectRoot = "D:\\TestProject";
    private const string TestRepoPath = "D:\\TestProject\\data\\dolt-repo";

    [SetUp]
    public void Setup()
    {
        _initializerLoggerMock = new Mock<ILogger<EmbranchInitializer>>();
        _doltCliMock = new Mock<IDoltCli>();
        _syncManagerMock = new Mock<ISyncManagerV2>();
        _manifestMock = new Mock<IEmbranchStateManifest>();
        _gitIntegrationMock = new Mock<IGitIntegration>();
        _syncStateTrackerMock = new Mock<ISyncStateTracker>();
        _syncStateCheckerMock = new Mock<ISyncStateChecker>();

        var doltConfig = new DoltConfiguration { RepositoryPath = TestRepoPath };
        _doltConfigOptions = Options.Create(doltConfig);

        // Default setup - Dolt is NOT initialized (empty state)
        _doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(false);
    }

    #region Phase 1: EmbranchInitializer - PendingConfiguration Tests

    [Test]
    [Description("PP13-81: When no remote URL and no local repo, return PendingConfiguration")]
    public async Task InitializeFromManifestAsync_NoRemoteNoRepo_ReturnsPendingConfiguration()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = null,  // No remote configured
                CurrentBranch = "main"
            }
        };

        _doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(false);

        var initializer = new EmbranchInitializer(
            _initializerLoggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _manifestMock.Object,
            _gitIntegrationMock.Object,
            _doltConfigOptions
        );

        // Act
        var result = await initializer.InitializeFromManifestAsync(manifest, TestProjectRoot);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ActionTaken, Is.EqualTo(InitializationAction.PendingConfiguration));

        // Verify dolt init was NOT called
        _doltCliMock.Verify(d => d.InitAsync(), Times.Never);
    }

    [Test]
    [Description("PP13-81: When remote URL is configured and no local repo, clone from remote")]
    public async Task InitializeFromManifestAsync_WithRemoteNoRepo_ClonesFromRemote()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = "dolthub.com/test/repo",  // Remote configured
                CurrentBranch = "main"
            }
        };

        _doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(false);
        _doltCliMock.Setup(d => d.CloneAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));
        _doltCliMock.Setup(d => d.FetchAsync(It.IsAny<string>()))
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));
        _doltCliMock.Setup(d => d.CheckoutAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));
        _doltCliMock.Setup(d => d.GetHeadCommitHashAsync()).ReturnsAsync("abc123");
        _doltCliMock.Setup(d => d.GetCurrentBranchAsync()).ReturnsAsync("main");
        _syncManagerMock.Setup(s => s.FullSyncAsync(It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SyncResultV2 { Status = SyncStatusV2.Completed });

        var initializer = new EmbranchInitializer(
            _initializerLoggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _manifestMock.Object,
            _gitIntegrationMock.Object,
            _doltConfigOptions
        );

        // Act
        var result = await initializer.InitializeFromManifestAsync(manifest, TestProjectRoot);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ActionTaken, Is.Not.EqualTo(InitializationAction.PendingConfiguration));

        // Verify clone was called
        _doltCliMock.Verify(d => d.CloneAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    [Test]
    [Description("PP13-81: When initialization mode is disabled, return Skipped")]
    public async Task InitializeFromManifestAsync_ModeDisabled_ReturnsSkipped()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = null,
                CurrentBranch = "main"
            },
            Initialization = new InitializationConfig
            {
                Mode = InitializationMode.Disabled
            }
        };

        var initializer = new EmbranchInitializer(
            _initializerLoggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _manifestMock.Object,
            _gitIntegrationMock.Object,
            _doltConfigOptions
        );

        // Act
        var result = await initializer.InitializeFromManifestAsync(manifest, TestProjectRoot);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ActionTaken, Is.EqualTo(InitializationAction.Skipped));

        // Verify no Dolt operations were called
        _doltCliMock.Verify(d => d.InitAsync(), Times.Never);
        _doltCliMock.Verify(d => d.CloneAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region Phase 2: DoltCloneTool - Force Clone Tests

    [Test]
    [Description("PP13-81: DoltClone with force=false on existing repo returns helpful error")]
    public async Task DoltClone_ExistingRepoNoForce_ReturnsAlreadyInitializedWithSuggestion()
    {
        // Arrange
        var cloneLoggerMock = new Mock<ILogger<DoltCloneTool>>();
        _doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(d => d.CheckDoltAvailableAsync())
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));

        // Setup for IsRepositoryEmptyAsync - no commits = empty
        _doltCliMock.Setup(d => d.GetLogAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<CommitInfo>());
        _doltCliMock.Setup(d => d.QueryAsync<dynamic>(It.Is<string>(s => s.StartsWith("SHOW TABLES"))))
            .ReturnsAsync(new List<dynamic>());

        var serverConfig = new ServerConfiguration { DataPath = TestProjectRoot };

        var cloneTool = new DoltCloneTool(
            cloneLoggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _syncStateTrackerMock.Object,
            _doltConfigOptions,
            _manifestMock.Object,
            _syncStateCheckerMock.Object
        );

        // Act
        var result = await cloneTool.DoltClone("dolthub.com/test/repo", force: false);

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic response = result;
        Assert.That((bool)response.success, Is.False);
        Assert.That((string)response.error, Is.EqualTo("ALREADY_INITIALIZED"));
        Assert.That((bool)response.is_empty, Is.True);
        Assert.That((string)response.suggestion, Does.Contain("force=true"));
    }

    [Test]
    [Description("PP13-81: DoltClone with force=true on repo with data returns ForceRequiresEmpty error")]
    public async Task DoltClone_RepoWithDataForce_ReturnsForceRequiresEmpty()
    {
        // Arrange
        var cloneLoggerMock = new Mock<ILogger<DoltCloneTool>>();
        _doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(d => d.CheckDoltAvailableAsync())
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));

        // Setup for IsRepositoryEmptyAsync - multiple commits = has data
        _doltCliMock.Setup(d => d.GetLogAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<CommitInfo>
            {
                new CommitInfo("commit1", "First commit", "Author", DateTime.UtcNow),
                new CommitInfo("commit2", "Second commit", "Author", DateTime.UtcNow),
                new CommitInfo("commit3", "Third commit", "Author", DateTime.UtcNow),
                new CommitInfo("commit4", "Fourth commit", "Author", DateTime.UtcNow)
            });

        var cloneTool = new DoltCloneTool(
            cloneLoggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _syncStateTrackerMock.Object,
            _doltConfigOptions,
            _manifestMock.Object,
            _syncStateCheckerMock.Object
        );

        // Act
        var result = await cloneTool.DoltClone("dolthub.com/test/repo", force: true);

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic response = result;
        Assert.That((bool)response.success, Is.False);
        Assert.That((string)response.error, Is.EqualTo("FORCE_REQUIRES_EMPTY"));
        Assert.That((string)response.suggestion, Does.Contain("dolt_reset"));
    }

    [Test]
    [Description("PP13-81: DoltClone with empty remote_url returns error")]
    public async Task DoltClone_EmptyRemoteUrl_ReturnsRemoteUrlRequired()
    {
        // Arrange
        var cloneLoggerMock = new Mock<ILogger<DoltCloneTool>>();
        _doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(false);

        var cloneTool = new DoltCloneTool(
            cloneLoggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _syncStateTrackerMock.Object,
            _doltConfigOptions,
            _manifestMock.Object,
            _syncStateCheckerMock.Object
        );

        // Act
        var result = await cloneTool.DoltClone("", force: false);

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic response = result;
        Assert.That((bool)response.success, Is.False);
        Assert.That((string)response.error, Is.EqualTo("REMOTE_URL_REQUIRED"));
    }

    #endregion

    #region Phase 3: ManifestSetRemoteTool Tests

    [Test]
    [Description("PP13-81: ManifestSetRemote with valid URL updates manifest")]
    public async Task ManifestSetRemote_ValidUrl_UpdatesManifest()
    {
        // Arrange
        var toolLoggerMock = new Mock<ILogger<ManifestSetRemoteTool>>();
        var existingManifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = null,
                DefaultBranch = "main"
            }
        };

        _manifestMock.Setup(m => m.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(existingManifest);
        _manifestMock.Setup(m => m.WriteManifestAsync(It.IsAny<string>(), It.IsAny<DmmsManifest>()))
            .Returns(Task.CompletedTask);
        _manifestMock.Setup(m => m.GetManifestPath(It.IsAny<string>()))
            .Returns(".dmms/state.json");
        _syncStateCheckerMock.Setup(s => s.GetProjectRootAsync())
            .ReturnsAsync(TestProjectRoot);

        var tool = new ManifestSetRemoteTool(
            toolLoggerMock.Object,
            _manifestMock.Object,
            _syncStateCheckerMock.Object,
            _gitIntegrationMock.Object
        );

        // Act
        var result = await tool.ManifestSetRemote("dolthub.com/test/repo");

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic response = result;
        Assert.That((bool)response.success, Is.True);
        Assert.That((string)response.message, Does.Contain("dolthub.com/test/repo"));

        // Verify manifest was written
        _manifestMock.Verify(m => m.WriteManifestAsync(
            It.IsAny<string>(),
            It.Is<DmmsManifest>(manifest => manifest.Dolt.RemoteUrl == "dolthub.com/test/repo")),
            Times.Once);
    }

    [Test]
    [Description("PP13-81: ManifestSetRemote with empty URL returns error")]
    public async Task ManifestSetRemote_EmptyUrl_ReturnsError()
    {
        // Arrange
        var toolLoggerMock = new Mock<ILogger<ManifestSetRemoteTool>>();

        var tool = new ManifestSetRemoteTool(
            toolLoggerMock.Object,
            _manifestMock.Object,
            _syncStateCheckerMock.Object,
            _gitIntegrationMock.Object
        );

        // Act
        var result = await tool.ManifestSetRemote("");

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic response = result;
        Assert.That((bool)response.success, Is.False);
        Assert.That((string)response.error, Is.EqualTo("REMOTE_URL_REQUIRED"));
    }

    [Test]
    [Description("PP13-81: ManifestSetRemote without existing manifest creates new one")]
    public async Task ManifestSetRemote_NoExistingManifest_CreatesNew()
    {
        // Arrange
        var toolLoggerMock = new Mock<ILogger<ManifestSetRemoteTool>>();

        _manifestMock.Setup(m => m.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync((DmmsManifest?)null);

        var defaultManifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = "dolthub.com/test/repo",
                DefaultBranch = "main"
            }
        };
        _manifestMock.Setup(m => m.CreateDefaultManifest(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(defaultManifest);
        _manifestMock.Setup(m => m.WriteManifestAsync(It.IsAny<string>(), It.IsAny<DmmsManifest>()))
            .Returns(Task.CompletedTask);
        _manifestMock.Setup(m => m.GetManifestPath(It.IsAny<string>()))
            .Returns(".dmms/state.json");
        _syncStateCheckerMock.Setup(s => s.GetProjectRootAsync())
            .ReturnsAsync(TestProjectRoot);

        var tool = new ManifestSetRemoteTool(
            toolLoggerMock.Object,
            _manifestMock.Object,
            _syncStateCheckerMock.Object,
            _gitIntegrationMock.Object
        );

        // Act
        var result = await tool.ManifestSetRemote("dolthub.com/test/repo");

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic response = result;
        Assert.That((bool)response.success, Is.True);

        // Verify CreateDefaultManifest was called
        _manifestMock.Verify(m => m.CreateDefaultManifest(
            It.Is<string>(url => url == "dolthub.com/test/repo"),
            It.IsAny<string>(),
            It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    [Description("PP13-81: ManifestSetRemote with default_branch updates branch")]
    public async Task ManifestSetRemote_WithDefaultBranch_UpdatesBranch()
    {
        // Arrange
        var toolLoggerMock = new Mock<ILogger<ManifestSetRemoteTool>>();
        var existingManifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = null,
                DefaultBranch = "main"
            }
        };

        _manifestMock.Setup(m => m.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(existingManifest);
        _manifestMock.Setup(m => m.WriteManifestAsync(It.IsAny<string>(), It.IsAny<DmmsManifest>()))
            .Returns(Task.CompletedTask);
        _manifestMock.Setup(m => m.GetManifestPath(It.IsAny<string>()))
            .Returns(".dmms/state.json");
        _syncStateCheckerMock.Setup(s => s.GetProjectRootAsync())
            .ReturnsAsync(TestProjectRoot);

        var tool = new ManifestSetRemoteTool(
            toolLoggerMock.Object,
            _manifestMock.Object,
            _syncStateCheckerMock.Object,
            _gitIntegrationMock.Object
        );

        // Act
        var result = await tool.ManifestSetRemote("dolthub.com/test/repo", default_branch: "develop");

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic response = result;
        Assert.That((bool)response.success, Is.True);

        // Verify manifest was written with develop branch
        _manifestMock.Verify(m => m.WriteManifestAsync(
            It.IsAny<string>(),
            It.Is<DmmsManifest>(manifest => manifest.Dolt.DefaultBranch == "develop")),
            Times.Once);
    }

    [Test]
    [Description("PP13-81: ManifestSetRemote invalidates sync state cache")]
    public async Task ManifestSetRemote_ValidUrl_InvalidatesCache()
    {
        // Arrange
        var toolLoggerMock = new Mock<ILogger<ManifestSetRemoteTool>>();
        var existingManifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig { RemoteUrl = null, DefaultBranch = "main" }
        };

        _manifestMock.Setup(m => m.ReadManifestAsync(It.IsAny<string>()))
            .ReturnsAsync(existingManifest);
        _manifestMock.Setup(m => m.WriteManifestAsync(It.IsAny<string>(), It.IsAny<DmmsManifest>()))
            .Returns(Task.CompletedTask);
        _manifestMock.Setup(m => m.GetManifestPath(It.IsAny<string>()))
            .Returns(".dmms/state.json");
        _syncStateCheckerMock.Setup(s => s.GetProjectRootAsync())
            .ReturnsAsync(TestProjectRoot);

        var tool = new ManifestSetRemoteTool(
            toolLoggerMock.Object,
            _manifestMock.Object,
            _syncStateCheckerMock.Object,
            _gitIntegrationMock.Object
        );

        // Act
        await tool.ManifestSetRemote("dolthub.com/test/repo");

        // Assert - Verify cache was invalidated
        _syncStateCheckerMock.Verify(s => s.InvalidateCache(), Times.Once);
    }

    #endregion

    #region Enum Values Tests

    [Test]
    [Description("PP13-81: InitializationAction.PendingConfiguration enum value exists")]
    public void InitializationAction_PendingConfiguration_EnumValueExists()
    {
        // Arrange & Act
        var action = InitializationAction.PendingConfiguration;

        // Assert
        Assert.That(action, Is.EqualTo(InitializationAction.PendingConfiguration));
        Assert.That((int)action, Is.GreaterThan(0));
    }

    [Test]
    [Description("PP13-81: All InitializationAction enum values are distinct")]
    public void InitializationAction_AllValues_AreDistinct()
    {
        // Arrange
        var values = Enum.GetValues<InitializationAction>();

        // Act
        var distinctCount = values.Distinct().Count();

        // Assert
        Assert.That(distinctCount, Is.EqualTo(values.Length));
        Assert.That(values, Does.Contain(InitializationAction.PendingConfiguration));
    }

    #endregion

    #region IsRepositoryEmptyAsync Logic Tests (via DoltClone behavior)

    [Test]
    [Description("PP13-81: Repository with no commits is considered empty")]
    public async Task DoltClone_NoCommits_RepoIsEmpty()
    {
        // Arrange
        var cloneLoggerMock = new Mock<ILogger<DoltCloneTool>>();
        _doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(d => d.CheckDoltAvailableAsync())
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));

        // Empty repo - no commits
        _doltCliMock.Setup(d => d.GetLogAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<CommitInfo>());
        _doltCliMock.Setup(d => d.QueryAsync<dynamic>(It.Is<string>(s => s.StartsWith("SHOW TABLES"))))
            .ReturnsAsync(new List<dynamic>());

        var cloneTool = new DoltCloneTool(
            cloneLoggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _syncStateTrackerMock.Object,
            _doltConfigOptions,
            _manifestMock.Object,
            _syncStateCheckerMock.Object
        );

        // Act
        var result = await cloneTool.DoltClone("dolthub.com/test/repo", force: false);

        // Assert - should get is_empty = true in response
        Assert.That(result, Is.Not.Null);
        dynamic response = result;
        Assert.That((bool)response.is_empty, Is.True);
    }

    [Test]
    [Description("PP13-81: Repository with many commits is not considered empty")]
    public async Task DoltClone_ManyCommits_RepoIsNotEmpty()
    {
        // Arrange
        var cloneLoggerMock = new Mock<ILogger<DoltCloneTool>>();
        _doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        _doltCliMock.Setup(d => d.CheckDoltAvailableAsync())
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));

        // Not empty - many commits
        _doltCliMock.Setup(d => d.GetLogAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<CommitInfo>
            {
                new CommitInfo("c1", "Commit 1", "Author", DateTime.UtcNow),
                new CommitInfo("c2", "Commit 2", "Author", DateTime.UtcNow),
                new CommitInfo("c3", "Commit 3", "Author", DateTime.UtcNow),
                new CommitInfo("c4", "Commit 4", "Author", DateTime.UtcNow),
                new CommitInfo("c5", "Commit 5", "Author", DateTime.UtcNow)
            });

        var cloneTool = new DoltCloneTool(
            cloneLoggerMock.Object,
            _doltCliMock.Object,
            _syncManagerMock.Object,
            _syncStateTrackerMock.Object,
            _doltConfigOptions,
            _manifestMock.Object,
            _syncStateCheckerMock.Object
        );

        // Act
        var result = await cloneTool.DoltClone("dolthub.com/test/repo", force: false);

        // Assert - should get is_empty = false in response
        Assert.That(result, Is.Not.Null);
        dynamic response = result;
        Assert.That((bool)response.is_empty, Is.False);
    }

    #endregion
}
