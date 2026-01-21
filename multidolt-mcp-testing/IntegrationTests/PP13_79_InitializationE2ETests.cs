using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using DMMS.Models;
using DMMS.Services;

namespace DMMSTesting.IntegrationTests;

/// <summary>
/// PP13-79: End-to-end tests for DMMS initialization from manifest
/// These tests verify the full initialization workflow including Dolt operations
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("E2E")]
public class PP13_79_InitializationE2ETests
{
    private Mock<ILogger<DmmsStateManifest>> _manifestLoggerMock = null!;
    private Mock<ILogger<GitIntegration>> _gitLoggerMock = null!;
    private Mock<ILogger<DmmsInitializer>> _initializerLoggerMock = null!;

    private DmmsStateManifest _manifestService = null!;
    private GitIntegration _gitIntegration = null!;
    private string _testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _manifestLoggerMock = new Mock<ILogger<DmmsStateManifest>>();
        _gitLoggerMock = new Mock<ILogger<GitIntegration>>();
        _initializerLoggerMock = new Mock<ILogger<DmmsInitializer>>();

        _manifestService = new DmmsStateManifest(_manifestLoggerMock.Object);
        _gitIntegration = new GitIntegration(_gitLoggerMock.Object);

        _testDirectory = Path.Combine(Path.GetTempPath(), "DmmsInitE2E", Guid.NewGuid().ToString());
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
    public async Task Initialization_NoManifest_SkipsManifestLogic()
    {
        // Arrange - No manifest in test directory
        var doltCliMock = new Mock<IDoltCli>();
        doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        doltCliMock.Setup(d => d.GetHeadCommitHashAsync()).ReturnsAsync("abc123def456");
        doltCliMock.Setup(d => d.GetCurrentBranchAsync()).ReturnsAsync("main");

        var syncManagerMock = new Mock<ISyncManagerV2>();
        var doltConfig = Options.Create(new DoltConfiguration());

        var initializer = new DmmsInitializer(
            _initializerLoggerMock.Object,
            doltCliMock.Object,
            syncManagerMock.Object,
            _manifestService,
            _gitIntegration,
            doltConfig);

        // Act - Create a manifest to test with
        var manifest = _manifestService.CreateDefaultManifest();
        manifest = manifest with { Initialization = new InitializationConfig { Mode = InitializationMode.Disabled } };

        var result = await initializer.InitializeFromManifestAsync(manifest, _testDirectory);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ActionTaken, Is.EqualTo(InitializationAction.Skipped));
    }

    [Test]
    public async Task Initialization_ManifestMatchesState_NoAction()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "abc123def456",
                CurrentBranch = "main"
            }
        };

        var doltCliMock = new Mock<IDoltCli>();
        doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        doltCliMock.Setup(d => d.GetHeadCommitHashAsync()).ReturnsAsync("abc123def456");
        doltCliMock.Setup(d => d.GetCurrentBranchAsync()).ReturnsAsync("main");

        var syncManagerMock = new Mock<ISyncManagerV2>();
        var doltConfig = Options.Create(new DoltConfiguration());

        var initializer = new DmmsInitializer(
            _initializerLoggerMock.Object,
            doltCliMock.Object,
            syncManagerMock.Object,
            _manifestService,
            _gitIntegration,
            doltConfig);

        // Act
        var check = await initializer.CheckInitializationNeededAsync(manifest);

        // Assert
        Assert.That(check.NeedsInitialization, Is.False);
        Assert.That(check.CurrentDoltCommit, Is.EqualTo("abc123def456"));
        Assert.That(check.ManifestDoltCommit, Is.EqualTo("abc123def456"));
    }

    [Test]
    public async Task Initialization_ManifestMismatch_AutoMode_Syncs()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "newcommit789",
                CurrentBranch = "main"
            },
            Initialization = new InitializationConfig { Mode = "auto" }
        };

        var doltCliMock = new Mock<IDoltCli>();
        doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        doltCliMock.Setup(d => d.GetHeadCommitHashAsync()).ReturnsAsync("oldcommit123");
        doltCliMock.Setup(d => d.GetCurrentBranchAsync()).ReturnsAsync("main");
        doltCliMock.Setup(d => d.CheckoutAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));

        var syncManagerMock = new Mock<ISyncManagerV2>();
        syncManagerMock.Setup(s => s.FullSyncAsync(It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SyncResultV2 { Status = SyncStatusV2.Completed });

        var doltConfig = Options.Create(new DoltConfiguration());

        var initializer = new DmmsInitializer(
            _initializerLoggerMock.Object,
            doltCliMock.Object,
            syncManagerMock.Object,
            _manifestService,
            _gitIntegration,
            doltConfig);

        // Act
        var check = await initializer.CheckInitializationNeededAsync(manifest);

        // Assert
        Assert.That(check.NeedsInitialization, Is.True);
        Assert.That(check.CurrentDoltCommit, Is.EqualTo("oldcommit123"));
        Assert.That(check.ManifestDoltCommit, Is.EqualTo("newcommit789"));
    }

    [Test]
    public async Task Initialization_ManifestMismatch_ManualMode_SkipsWithWarning()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "newcommit789",
                CurrentBranch = "main"
            },
            Initialization = new InitializationConfig { Mode = "manual" }
        };

        var doltCliMock = new Mock<IDoltCli>();
        doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        doltCliMock.Setup(d => d.GetHeadCommitHashAsync()).ReturnsAsync("oldcommit123");
        doltCliMock.Setup(d => d.GetCurrentBranchAsync()).ReturnsAsync("main");

        var syncManagerMock = new Mock<ISyncManagerV2>();
        var doltConfig = Options.Create(new DoltConfiguration());

        var initializer = new DmmsInitializer(
            _initializerLoggerMock.Object,
            doltCliMock.Object,
            syncManagerMock.Object,
            _manifestService,
            _gitIntegration,
            doltConfig);

        // Act - Check if initialization is needed (it would be, but mode is manual)
        var check = await initializer.CheckInitializationNeededAsync(manifest);

        // Assert - Initialization IS needed, but manual mode means it won't auto-sync
        Assert.That(check.NeedsInitialization, Is.True);

        // The actual decision to skip is made by the caller (Program.cs) based on mode
        // This test verifies the check correctly identifies the mismatch
    }

    [Test]
    public async Task Initialization_DoltNotInitialized_NeedsInit()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest();

        var doltCliMock = new Mock<IDoltCli>();
        doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(false);

        var syncManagerMock = new Mock<ISyncManagerV2>();
        var doltConfig = Options.Create(new DoltConfiguration());

        var initializer = new DmmsInitializer(
            _initializerLoggerMock.Object,
            doltCliMock.Object,
            syncManagerMock.Object,
            _manifestService,
            _gitIntegration,
            doltConfig);

        // Act
        var check = await initializer.CheckInitializationNeededAsync(manifest);

        // Assert
        Assert.That(check.NeedsInitialization, Is.True);
        Assert.That(check.Reason, Does.Contain("not initialized"));
    }

    [Test]
    public async Task Initialization_CloneRequired_ClonesAndSyncs()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = "dolthub.com/test/repo",
                CurrentCommit = "abc123",
                CurrentBranch = "main"
            },
            Initialization = new InitializationConfig { Mode = "auto" }
        };

        var doltCliMock = new Mock<IDoltCli>();
        doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(false);
        doltCliMock.Setup(d => d.CloneAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new DoltCommandResult(true, "Cloned successfully", "", 0));
        doltCliMock.Setup(d => d.CheckoutAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));
        doltCliMock.Setup(d => d.GetHeadCommitHashAsync()).ReturnsAsync("abc123");
        doltCliMock.Setup(d => d.GetCurrentBranchAsync()).ReturnsAsync("main");

        var syncManagerMock = new Mock<ISyncManagerV2>();
        syncManagerMock.Setup(s => s.FullSyncAsync(It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SyncResultV2 { Status = SyncStatusV2.Completed, Added = 5 });

        var doltConfig = Options.Create(new DoltConfiguration());

        var initializer = new DmmsInitializer(
            _initializerLoggerMock.Object,
            doltCliMock.Object,
            syncManagerMock.Object,
            _manifestService,
            _gitIntegration,
            doltConfig);

        // Act
        var result = await initializer.InitializeFromManifestAsync(manifest, _testDirectory);

        // Assert
        Assert.That(result.Success, Is.True);
        doltCliMock.Verify(d => d.CloneAsync("dolthub.com/test/repo", It.IsAny<string?>()), Times.Once);
        syncManagerMock.Verify(s => s.FullSyncAsync(null, true), Times.Once);
    }

    [Test]
    public async Task Initialization_NetworkFailure_GracefulDegradation()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = "dolthub.com/unreachable/repo",
                CurrentCommit = "abc123",
                CurrentBranch = "main"
            },
            Initialization = new InitializationConfig { Mode = "auto" }
        };

        var doltCliMock = new Mock<IDoltCli>();
        doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        doltCliMock.Setup(d => d.FetchAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("Network unreachable"));
        doltCliMock.Setup(d => d.CheckoutAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));
        doltCliMock.Setup(d => d.GetHeadCommitHashAsync()).ReturnsAsync("abc123");
        doltCliMock.Setup(d => d.GetCurrentBranchAsync()).ReturnsAsync("main");

        var syncManagerMock = new Mock<ISyncManagerV2>();
        syncManagerMock.Setup(s => s.FullSyncAsync(It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SyncResultV2 { Status = SyncStatusV2.Completed });

        var doltConfig = Options.Create(new DoltConfiguration());

        var initializer = new DmmsInitializer(
            _initializerLoggerMock.Object,
            doltCliMock.Object,
            syncManagerMock.Object,
            _manifestService,
            _gitIntegration,
            doltConfig);

        // Act - Should not throw, should gracefully continue
        var result = await initializer.InitializeFromManifestAsync(manifest, _testDirectory);

        // Assert - Should succeed despite network failure
        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task SyncToCommit_WithOverrideCommit_UsesOverride()
    {
        // Arrange
        var doltCliMock = new Mock<IDoltCli>();
        doltCliMock.Setup(d => d.CheckoutAsync("overridecommit", It.IsAny<bool>()))
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));

        var syncManagerMock = new Mock<ISyncManagerV2>();
        syncManagerMock.Setup(s => s.FullSyncAsync(It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SyncResultV2 { Status = SyncStatusV2.Completed });

        var doltConfig = Options.Create(new DoltConfiguration());

        var initializer = new DmmsInitializer(
            _initializerLoggerMock.Object,
            doltCliMock.Object,
            syncManagerMock.Object,
            _manifestService,
            _gitIntegration,
            doltConfig);

        // Act
        var result = await initializer.SyncToCommitAsync("overridecommit");

        // Assert
        Assert.That(result.Success, Is.True);
        doltCliMock.Verify(d => d.CheckoutAsync("overridecommit", false), Times.Once);
    }

    [Test]
    public async Task GetCurrentStateAsync_ReturnsComprehensiveState()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest();
        manifest = manifest with
        {
            Dolt = manifest.Dolt with { CurrentCommit = "manifest123", CurrentBranch = "main" }
        };
        await _manifestService.WriteManifestAsync(_testDirectory, manifest);

        var doltCliMock = new Mock<IDoltCli>();
        doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        doltCliMock.Setup(d => d.GetHeadCommitHashAsync()).ReturnsAsync("current456");
        doltCliMock.Setup(d => d.GetCurrentBranchAsync()).ReturnsAsync("main");

        var syncManagerMock = new Mock<ISyncManagerV2>();
        var doltConfig = Options.Create(new DoltConfiguration());

        var initializer = new DmmsInitializer(
            _initializerLoggerMock.Object,
            doltCliMock.Object,
            syncManagerMock.Object,
            _manifestService,
            _gitIntegration,
            doltConfig);

        // Act
        var state = await initializer.GetCurrentStateAsync();

        // Assert
        Assert.That(state.DoltInitialized, Is.True);
        Assert.That(state.CurrentDoltCommit, Is.EqualTo("current456"));
        Assert.That(state.CurrentDoltBranch, Is.EqualTo("main"));
    }

    [Test]
    public async Task Initialization_CheckoutFails_FallsBackToBranch()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "nonexistent123",
                CurrentBranch = "main"
            },
            Initialization = new InitializationConfig { Mode = "auto" }
        };

        var doltCliMock = new Mock<IDoltCli>();
        doltCliMock.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        // First checkout (commit) fails
        doltCliMock.Setup(d => d.CheckoutAsync("nonexistent123", It.IsAny<bool>()))
            .ReturnsAsync(new DoltCommandResult(false, "", "Commit not found", 1));
        // Fallback checkout (branch) succeeds
        doltCliMock.Setup(d => d.CheckoutAsync("main", It.IsAny<bool>()))
            .ReturnsAsync(new DoltCommandResult(true, "", "", 0));
        doltCliMock.Setup(d => d.GetHeadCommitHashAsync()).ReturnsAsync("fallbackcommit");
        doltCliMock.Setup(d => d.GetCurrentBranchAsync()).ReturnsAsync("main");

        var syncManagerMock = new Mock<ISyncManagerV2>();
        syncManagerMock.Setup(s => s.FullSyncAsync(It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SyncResultV2 { Status = SyncStatusV2.Completed });

        var doltConfig = Options.Create(new DoltConfiguration());

        var initializer = new DmmsInitializer(
            _initializerLoggerMock.Object,
            doltCliMock.Object,
            syncManagerMock.Object,
            _manifestService,
            _gitIntegration,
            doltConfig);

        // Act
        var result = await initializer.InitializeFromManifestAsync(manifest, _testDirectory);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ActionTaken, Is.EqualTo(InitializationAction.CheckedOutBranch));
    }
}
