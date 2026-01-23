using Microsoft.Extensions.Logging;
using Moq;
using Embranch.Models;
using Embranch.Tools;
using Embranch.Services;

namespace EmbranchTesting.Tools;

/// <summary>
/// Unit tests for the DoltPushTool class focusing on push result reporting accuracy
/// </summary>
[TestFixture]
public class DoltPushToolTests
{
    private Mock<ILogger<DoltPushTool>>? _mockLogger;
    private Mock<IDoltCli>? _mockDoltCli;
    private Mock<ISyncManagerV2>? _mockSyncManager;
    private DoltPushTool? _tool;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<DoltPushTool>>();
        _mockDoltCli = new Mock<IDoltCli>();
        _mockSyncManager = new Mock<ISyncManagerV2>();
        _tool = new DoltPushTool(_mockLogger.Object, _mockDoltCli.Object, _mockSyncManager.Object);
    }

    /// <summary>
    /// Tests that the tool reports "Already up to date" correctly when no commits are pushed
    /// </summary>
    [Test]
    public async Task DoltPush_WhenUpToDate_ReportsCorrectStatus()
    {
        // Arrange
        var upToDatePushResult = new PushResult(
            Success: true,
            Message: "Already up to date",
            CommitsPushed: 0,
            FromCommitHash: null,
            ToCommitHash: null,
            IsUpToDate: true,
            IsNewBranch: false,
            IsRejected: false,
            ErrorType: null,
            RemoteUrl: "https://dolthub.com/user/repo"
        );

        var successfulSyncResult = new SyncResultV2 
        { 
            Status = SyncStatusV2.Completed,
            Data = upToDatePushResult
        };

        SetupSuccessfulDoltChecks();
        _mockSyncManager!.Setup(x => x.ProcessPushAsync("origin", "main"))
                        .ReturnsAsync(successfulSyncResult);

        // Act
        var result = await _tool!.DoltPush();

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic dynamicResult = result;
        
        Assert.That(dynamicResult.success, Is.True);
        Assert.That(dynamicResult.message, Is.EqualTo("Already up to date."));
        Assert.That(dynamicResult.push_result.commits_pushed, Is.EqualTo(0));
        Assert.That(dynamicResult.push_result.is_up_to_date, Is.True);
        Assert.That(dynamicResult.push_result.is_new_branch, Is.False);
    }

    /// <summary>
    /// Tests that the tool reports actual commit count when commits are pushed
    /// </summary>
    [Test]
    public async Task DoltPush_WhenCommitsPushed_ReportsCorrectCommitCount()
    {
        // Arrange
        var commitsPushedResult = new PushResult(
            Success: true,
            Message: "Pushed commits to main",
            CommitsPushed: 3,
            FromCommitHash: "abc1234",
            ToCommitHash: "def5678", 
            IsUpToDate: false,
            IsNewBranch: false,
            IsRejected: false,
            ErrorType: null,
            RemoteUrl: "https://dolthub.com/user/repo"
        );

        var successfulSyncResult = new SyncResultV2 
        { 
            Status = SyncStatusV2.Completed,
            Data = commitsPushedResult
        };

        SetupSuccessfulDoltChecks();
        _mockSyncManager!.Setup(x => x.ProcessPushAsync("origin", "main"))
                        .ReturnsAsync(successfulSyncResult);

        // Act
        var result = await _tool!.DoltPush();

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic dynamicResult = result;
        
        Assert.That(dynamicResult.success, Is.True);
        Assert.That(dynamicResult.message, Is.EqualTo("Pushed 3 commits to origin/main."));
        Assert.That(dynamicResult.push_result.commits_pushed, Is.EqualTo(3));
        Assert.That(dynamicResult.push_result.from_commit, Is.EqualTo("abc1234"));
        Assert.That(dynamicResult.push_result.to_commit, Is.EqualTo("def5678"));
        Assert.That(dynamicResult.push_result.is_up_to_date, Is.False);
    }

    /// <summary>
    /// Tests that the tool reports new branch creation correctly
    /// </summary>
    [Test]
    public async Task DoltPush_WhenNewBranch_ReportsCorrectStatus()
    {
        // Arrange
        var newBranchResult = new PushResult(
            Success: true,
            Message: "Created new branch feature/auth",
            CommitsPushed: 5,
            FromCommitHash: null,
            ToCommitHash: "xyz9876",
            IsUpToDate: false,
            IsNewBranch: true,
            IsRejected: false,
            ErrorType: null,
            RemoteUrl: "https://dolthub.com/user/repo"
        );

        var successfulSyncResult = new SyncResultV2 
        { 
            Status = SyncStatusV2.Completed,
            Data = newBranchResult
        };

        SetupSuccessfulDoltChecks("feature/auth");
        _mockSyncManager!.Setup(x => x.ProcessPushAsync("origin", "feature/auth"))
                        .ReturnsAsync(successfulSyncResult);

        // Act
        var result = await _tool!.DoltPush(branch: "feature/auth");

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic dynamicResult = result;
        
        Assert.That(dynamicResult.success, Is.True);
        Assert.That(dynamicResult.message, Is.EqualTo("Created new branch feature/auth with 5 commits."));
        Assert.That(dynamicResult.push_result.commits_pushed, Is.EqualTo(5));
        Assert.That(dynamicResult.push_result.is_new_branch, Is.True);
        Assert.That(dynamicResult.push_result.is_up_to_date, Is.False);
    }

    /// <summary>
    /// Tests that the tool handles rejection with enhanced error reporting
    /// </summary>
    [Test]
    public async Task DoltPush_WhenRejected_ReportsCorrectError()
    {
        // Arrange
        var rejectedResult = new PushResult(
            Success: false,
            Message: "Push rejected. Pull remote changes first or use force push.",
            CommitsPushed: 0,
            FromCommitHash: null,
            ToCommitHash: null,
            IsUpToDate: false,
            IsNewBranch: false,
            IsRejected: true,
            ErrorType: "REMOTE_REJECTED",
            RemoteUrl: "https://dolthub.com/user/repo"
        );

        var failedSyncResult = new SyncResultV2 
        { 
            Status = SyncStatusV2.Failed,
            ErrorMessage = "Push rejected",
            Data = rejectedResult
        };

        SetupSuccessfulDoltChecks();
        _mockSyncManager!.Setup(x => x.ProcessPushAsync("origin", "main"))
                        .ReturnsAsync(failedSyncResult);

        // Act
        var result = await _tool!.DoltPush();

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic dynamicResult = result;
        
        Assert.That(dynamicResult.success, Is.False);
        Assert.That(dynamicResult.error, Is.EqualTo("REMOTE_REJECTED"));
        Assert.That(dynamicResult.message, Is.EqualTo("Push rejected"));
        Assert.That(dynamicResult.suggestions, Is.Not.Null);
        Assert.That(dynamicResult.suggestions[0], Is.EqualTo("Pull first to get remote changes"));
    }

    /// <summary>
    /// Tests that the tool correctly reports remote state with actual remote commit hash
    /// </summary>
    [Test]
    public async Task DoltPush_SuccessfulPush_ReportsCorrectRemoteState()
    {
        // Arrange
        var pushResult = new PushResult(
            Success: true,
            Message: "Pushed commits to main",
            CommitsPushed: 2,
            FromCommitHash: "abc1234",
            ToCommitHash: "def5678",
            IsUpToDate: false,
            IsNewBranch: false,
            IsRejected: false,
            ErrorType: null,
            RemoteUrl: "https://dolthub.com/user/repo"
        );

        var successfulSyncResult = new SyncResultV2 
        { 
            Status = SyncStatusV2.Completed,
            Data = pushResult
        };

        SetupSuccessfulDoltChecks();
        _mockSyncManager!.Setup(x => x.ProcessPushAsync("origin", "main"))
                        .ReturnsAsync(successfulSyncResult);

        // Act
        var result = await _tool!.DoltPush();

        // Assert
        Assert.That(result, Is.Not.Null);
        dynamic dynamicResult = result;
        
        Assert.That(dynamicResult.success, Is.True);
        Assert.That(dynamicResult.remote_state.remote_branch, Is.EqualTo("origin/main"));
        Assert.That(dynamicResult.remote_state.remote_commit, Is.EqualTo("def5678")); // Should now show actual remote commit
    }

    /// <summary>
    /// Helper method to setup common successful Dolt CLI checks
    /// </summary>
    private void SetupSuccessfulDoltChecks(string branch = "main")
    {
        _mockDoltCli!.Setup(x => x.CheckDoltAvailableAsync())
                    .ReturnsAsync(new DoltCommandResult(true, "dolt version 1.0.0", "", 0));
        
        _mockDoltCli!.Setup(x => x.IsInitializedAsync())
                    .ReturnsAsync(true);
        
        _mockDoltCli!.Setup(x => x.GetCurrentBranchAsync())
                    .ReturnsAsync(branch);
        
        _mockDoltCli!.Setup(x => x.ListRemotesAsync())
                    .ReturnsAsync(new[] { new RemoteInfo("origin", "https://dolthub.com/user/repo") });
        
        _mockDoltCli!.Setup(x => x.GetHeadCommitHashAsync())
                    .ReturnsAsync("local123456");

        _mockSyncManager!.Setup(x => x.GetLocalChangesAsync())
                        .ReturnsAsync(new LocalChanges(new List<ChromaDocument>(), new List<ChromaDocument>(), new List<DeletedDocumentV2>()));
    }
}