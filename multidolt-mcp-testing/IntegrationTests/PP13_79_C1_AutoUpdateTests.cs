using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using DMMS.Services;
using DMMS.Models;
using DMMS.Utilities;

namespace DMMSTesting.IntegrationTests;

/// <summary>
/// PP13-79-C1: Integration tests for automatic manifest update after Dolt operations.
/// Tests that manifest is correctly updated after commit, checkout, pull, merge, etc.
/// </summary>
[TestFixture]
public class PP13_79_C1_AutoUpdateTests
{
    private string _testProjectRoot = null!;
    private Mock<ILogger<DmmsStateManifest>> _loggerMock = null!;
    private DmmsStateManifest _manifestService = null!;

    [SetUp]
    public void Setup()
    {
        _testProjectRoot = Path.Combine(Path.GetTempPath(), $"PP13_79_C1_AutoUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testProjectRoot);

        _loggerMock = new Mock<ILogger<DmmsStateManifest>>();
        _manifestService = new DmmsStateManifest(_loggerMock.Object);
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

    #region UpdateDoltCommitAsync Tests (Simulating Post-Operation Updates)

    [Test]
    [Description("PP13-79-C1: After commit - manifest is updated with new commit hash")]
    public async Task AfterCommit_ManifestUpdatedWithNewCommit()
    {
        // Arrange - Simulate initial state
        var manifest = _manifestService.CreateDefaultManifest();
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act - Simulate what happens after a dolt commit
        var newCommitHash = "abc123def456789";
        var branch = "main";
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, newCommitHash, branch);

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Dolt.CurrentCommit, Is.EqualTo(newCommitHash));
        Assert.That(updated.Dolt.CurrentBranch, Is.EqualTo(branch));
    }

    [Test]
    [Description("PP13-79-C1: After checkout - manifest is updated with new branch")]
    public async Task AfterCheckout_ManifestUpdatedWithNewBranch()
    {
        // Arrange - Create manifest with initial state using object initializer
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentBranch = "main",
                CurrentCommit = "oldcommit",
                DefaultBranch = "main"
            }
        };
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act - Simulate checkout to feature branch
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "featurecommit", "feature-branch");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Dolt.CurrentBranch, Is.EqualTo("feature-branch"));
        Assert.That(updated.Dolt.CurrentCommit, Is.EqualTo("featurecommit"));
    }

    [Test]
    [Description("PP13-79-C1: After pull - manifest is updated with latest commit")]
    public async Task AfterPull_ManifestUpdatedWithLatestCommit()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "localcommit",
                DefaultBranch = "main"
            }
        };
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act - Simulate pull bringing new commits
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "remotecommit123", "main");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Dolt.CurrentCommit, Is.EqualTo("remotecommit123"));
    }

    [Test]
    [Description("PP13-79-C1: After merge - manifest is updated with merge commit")]
    public async Task AfterMerge_ManifestUpdatedWithMergeCommit()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentBranch = "main",
                CurrentCommit = "beforemerge",
                DefaultBranch = "main"
            }
        };
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act - Simulate merge creating new commit
        var mergeCommitHash = "mergecommit789";
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, mergeCommitHash, "main");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Dolt.CurrentCommit, Is.EqualTo(mergeCommitHash));
    }

    [Test]
    [Description("PP13-79-C1: After reset - manifest is updated with reset target")]
    public async Task AfterReset_ManifestUpdatedWithResetTarget()
    {
        // Arrange
        var manifest = new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                CurrentCommit = "headcommit",
                DefaultBranch = "main"
            }
        };
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act - Simulate reset to earlier commit
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "earliercommit", "main");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Dolt.CurrentCommit, Is.EqualTo("earliercommit"));
    }

    #endregion

    #region Timestamp Update Tests

    [Test]
    [Description("PP13-79-C1: Each update advances UpdatedAt timestamp")]
    public async Task MultipleUpdates_AdvancesTimestamp()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest();
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);
        var times = new List<DateTime>();

        // Act - Multiple updates
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(50); // Ensure time passes
            await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, $"commit{i}", "main");
            var m = await _manifestService.ReadManifestAsync(_testProjectRoot);
            times.Add(m!.UpdatedAt);
        }

        // Assert - Each timestamp should be later than the previous
        Assert.That(times[1], Is.GreaterThan(times[0]));
        Assert.That(times[2], Is.GreaterThan(times[1]));
    }

    #endregion

    #region Preservation Tests

    [Test]
    [Description("PP13-79-C1: Update preserves remote URL")]
    public async Task Update_PreservesRemoteUrl()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest(
            remoteUrl: "https://dolthub.com/org/important-repo"
        );
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "newcommit", "main");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Dolt.RemoteUrl, Is.EqualTo("https://dolthub.com/org/important-repo"));
    }

    [Test]
    [Description("PP13-79-C1: Update preserves default branch setting")]
    public async Task Update_PreservesDefaultBranch()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest(
            defaultBranch: "develop"
        );
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "newcommit", "feature");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Dolt.DefaultBranch, Is.EqualTo("develop"));
    }

    [Test]
    [Description("PP13-79-C1: Update preserves initialization mode")]
    public async Task Update_PreservesInitMode()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest(
            initMode: "manual"
        );
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "newcommit", "main");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Initialization.Mode, Is.EqualTo("manual"));
    }

    [Test]
    [Description("PP13-79-C1: Update preserves version number")]
    public async Task Update_PreservesVersion()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest();
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act
        await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, "newcommit", "main");

        // Assert
        var updated = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(updated!.Version, Is.EqualTo("1.0"));
    }

    #endregion

    #region ToolResponseHelper Tests

    [Test]
    [Description("PP13-79-C1: ToolResponseHelper attaches warning when out of sync")]
    public async Task AttachWarningIfOutOfSync_OutOfSync_AttachesWarning()
    {
        // Arrange
        var syncStateCheckerMock = new Mock<ISyncStateChecker>();
        var warning = new OutOfSyncWarning
        {
            Message = "Local state differs from manifest",
            ActionRequired = "Call sync_to_manifest"
        };
        syncStateCheckerMock.Setup(s => s.GetOutOfSyncWarningAsync())
            .ReturnsAsync(warning);

        var originalResponse = new { success = true, message = "Operation completed" };

        // Act
        var result = await ToolResponseHelper.AttachWarningIfOutOfSyncAsync(
            originalResponse,
            syncStateCheckerMock.Object
        );

        // Assert - Result should be an anonymous object with response and manifest_warning
        var resultType = result.GetType();
        var warningProp = resultType.GetProperty("manifest_warning");
        Assert.That(warningProp, Is.Not.Null);
    }

    [Test]
    [Description("PP13-79-C1: ToolResponseHelper returns original when in sync")]
    public async Task AttachWarningIfOutOfSync_InSync_ReturnsOriginal()
    {
        // Arrange
        var syncStateCheckerMock = new Mock<ISyncStateChecker>();
        syncStateCheckerMock.Setup(s => s.GetOutOfSyncWarningAsync())
            .ReturnsAsync((OutOfSyncWarning?)null);

        var originalResponse = new { success = true, message = "Operation completed" };

        // Act
        var result = await ToolResponseHelper.AttachWarningIfOutOfSyncAsync(
            originalResponse,
            syncStateCheckerMock.Object
        );

        // Assert - Should return the same object
        Assert.That(result, Is.SameAs(originalResponse));
    }

    [Test]
    [Description("PP13-79-C1: CreateSyncBlockedResponse creates proper error structure")]
    public void CreateSyncBlockedResponse_LocalChanges_CreatesProperResponse()
    {
        // Arrange
        var syncState = new SyncStateCheckResult
        {
            IsInSync = false,
            HasLocalChanges = true,
            LocalBranch = "main",
            LocalCommit = "abc123def",
            ManifestBranch = "main",
            ManifestCommit = "abc123def"
        };

        // Act
        var response = ToolResponseHelper.CreateSyncBlockedResponse("checkout", syncState);

        // Assert
        var responseType = response.GetType();
        var successProp = responseType.GetProperty("success");
        var errorProp = responseType.GetProperty("error");

        Assert.That(successProp?.GetValue(response), Is.False);
        Assert.That(errorProp?.GetValue(response), Is.EqualTo("SYNC_BLOCKED"));
    }

    [Test]
    [Description("PP13-79-C1: AttachSyncStateInfo adds sync_status field")]
    public void AttachSyncStateInfo_InSync_AddsSyncStatus()
    {
        // Arrange
        var originalResponse = new { data = "test" };
        var syncState = new SyncStateCheckResult { IsInSync = true };

        // Act
        var result = ToolResponseHelper.AttachSyncStateInfo(originalResponse, syncState);

        // Assert
        var resultType = result.GetType();
        var syncStatusProp = resultType.GetProperty("sync_status");
        Assert.That(syncStatusProp?.GetValue(result), Is.EqualTo("in_sync"));
    }

    [Test]
    [Description("PP13-79-C1: AttachSyncStateInfo includes sync_info when out of sync")]
    public void AttachSyncStateInfo_OutOfSync_IncludesSyncInfo()
    {
        // Arrange
        var originalResponse = new { data = "test" };
        var syncState = new SyncStateCheckResult
        {
            IsInSync = false,
            Reason = "Commit mismatch",
            LocalBranch = "main",
            LocalCommit = "abc123",
            ManifestBranch = "main",
            ManifestCommit = "def456"
        };

        // Act
        var result = ToolResponseHelper.AttachSyncStateInfo(originalResponse, syncState);

        // Assert
        var resultType = result.GetType();
        var syncStatusProp = resultType.GetProperty("sync_status");
        var syncInfoProp = resultType.GetProperty("sync_info");

        Assert.That(syncStatusProp?.GetValue(result), Is.EqualTo("out_of_sync"));
        Assert.That(syncInfoProp, Is.Not.Null);
    }

    #endregion

    #region Concurrent Update Tests

    [Test]
    [Description("PP13-79-C1: Sequential updates don't corrupt manifest")]
    public async Task SequentialUpdates_NoCorruption()
    {
        // Arrange
        var manifest = _manifestService.CreateDefaultManifest(
            remoteUrl: "https://test.com/repo"
        );
        await _manifestService.WriteManifestAsync(_testProjectRoot, manifest);

        // Act - Rapid sequential updates
        for (int i = 0; i < 10; i++)
        {
            await _manifestService.UpdateDoltCommitAsync(_testProjectRoot, $"commit{i:D4}", $"branch{i % 3}");
        }

        // Assert - Final state is valid and represents last update
        var final = await _manifestService.ReadManifestAsync(_testProjectRoot);
        Assert.That(final, Is.Not.Null);
        Assert.That(final!.Dolt.CurrentCommit, Is.EqualTo("commit0009"));
        Assert.That(final.Dolt.RemoteUrl, Is.EqualTo("https://test.com/repo"));
        Assert.That(final.Version, Is.EqualTo("1.0"));
    }

    #endregion
}
