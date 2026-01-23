using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EmbranchTesting.Services
{
    /// <summary>
    /// Tests for PushResultAnalyzer utility class that analyzes dolt push command output
    /// </summary>
    [TestFixture]
    public class PushResultAnalyzerTests
    {
        [Test]
        [TestCase("Everything up-to-date", true, 0, true, false, false)]
        [TestCase("everything up-to-date", true, 0, true, false, false)] // Case insensitive
        public void AnalyzePushOutput_UpToDatePush_ReturnsCorrectResult(string output, bool expectedSuccess, int expectedCommits, bool expectedUpToDate, bool expectedNewBranch, bool expectedRejected)
        {
            // Arrange
            var commandResult = new DoltCommandResult(Success: true, Output: output, Error: "", ExitCode: 0);
            
            // Act
            var result = PushResultAnalyzer.AnalyzePushOutput(commandResult, NullLogger.Instance);
            
            // Assert
            Assert.That(result.Success, Is.EqualTo(expectedSuccess));
            Assert.That(result.CommitsPushed, Is.EqualTo(expectedCommits));
            Assert.That(result.IsUpToDate, Is.EqualTo(expectedUpToDate));
            Assert.That(result.IsNewBranch, Is.EqualTo(expectedNewBranch));
            Assert.That(result.IsRejected, Is.EqualTo(expectedRejected));
            Assert.That(result.Message, Is.EqualTo("Already up to date"));
        }

        [Test]
        [TestCase("* [new branch]      main -> main", "main", "main")]
        [TestCase("* [new branch]      feature/auth -> feature/auth", "feature/auth", "feature/auth")]
        public void AnalyzePushOutput_NewBranchPush_ReturnsCorrectResult(string output, string expectedSourceBranch, string expectedTargetBranch)
        {
            // Arrange
            var commandResult = new DoltCommandResult(Success: true, Output: output, Error: "", ExitCode: 0);
            
            // Act
            var result = PushResultAnalyzer.AnalyzePushOutput(commandResult, NullLogger.Instance);
            
            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.IsNewBranch, Is.True);
            Assert.That(result.IsUpToDate, Is.False);
            Assert.That(result.IsRejected, Is.False);
            Assert.That(result.CommitsPushed, Is.EqualTo(-1)); // Will be calculated separately
            Assert.That(result.Message, Is.EqualTo($"Created new branch {expectedTargetBranch}"));
        }

        [Test]
        [TestCase("   abc1234..def5678  main -> main", "abc1234", "def5678", "main", "main")]
        [TestCase("   1a2b3c4..9x8y7z6  feature/test -> feature/test", "1a2b3c4", "9x8y7z6", "feature/test", "feature/test")]
        public void AnalyzePushOutput_CommitRangePush_ReturnsCorrectResult(string output, string expectedFromCommit, string expectedToCommit, string expectedSourceBranch, string expectedTargetBranch)
        {
            // Arrange
            var commandResult = new DoltCommandResult(Success: true, Output: output, Error: "", ExitCode: 0);
            
            // Act
            var result = PushResultAnalyzer.AnalyzePushOutput(commandResult, NullLogger.Instance);
            
            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.IsNewBranch, Is.False);
            Assert.That(result.IsUpToDate, Is.False);
            Assert.That(result.IsRejected, Is.False);
            Assert.That(result.FromCommitHash, Is.EqualTo(expectedFromCommit));
            Assert.That(result.ToCommitHash, Is.EqualTo(expectedToCommit));
            Assert.That(result.CommitsPushed, Is.EqualTo(-1)); // Will be calculated separately
            Assert.That(result.Message, Is.EqualTo($"Pushed commits to {expectedTargetBranch}"));
        }

        [Test]
        [TestCase("To https://dolthub.com/user/repo\n   abc1234..def5678  main -> main", "https://dolthub.com/user/repo")]
        [TestCase("To git@github.com:user/repo.git\n* [new branch]      main -> main", "git@github.com:user/repo.git")]
        public void AnalyzePushOutput_ExtractsRemoteUrl_Correctly(string output, string expectedUrl)
        {
            // Arrange
            var commandResult = new DoltCommandResult(Success: true, Output: output, Error: "", ExitCode: 0);
            
            // Act
            var result = PushResultAnalyzer.AnalyzePushOutput(commandResult, NullLogger.Instance);
            
            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.RemoteUrl, Is.EqualTo(expectedUrl));
        }

        [Test]
        [TestCase("+ abc1234...def5678 main -> main (forced update)", true)]
        [TestCase("forced update", true)]
        [TestCase("normal push output", false)]
        public void AnalyzePushOutput_ForcePush_DetectedCorrectly(string output, bool expectedForceDetected)
        {
            // Arrange
            var commandResult = new DoltCommandResult(Success: true, Output: output, Error: "", ExitCode: 0);
            
            // Act
            var result = PushResultAnalyzer.AnalyzePushOutput(commandResult, NullLogger.Instance);
            
            // Assert
            Assert.That(result.Success, Is.True);
            if (expectedForceDetected)
            {
                Assert.That(result.Message, Is.EqualTo("Force pushed successfully"));
            }
        }

        [Test]
        [TestCase("authentication failed", "AUTHENTICATION_FAILED", "Authentication failed. Check your credentials.")]
        [TestCase("401 unauthorized", "AUTHENTICATION_FAILED", "Authentication failed. Check your credentials.")]
        [TestCase("credentials invalid", "AUTHENTICATION_FAILED", "Authentication failed. Check your credentials.")]
        public void AnalyzePushOutput_AuthenticationErrors_ClassifiedCorrectly(string errorOutput, string expectedErrorType, string expectedMessage)
        {
            // Arrange
            var commandResult = new DoltCommandResult(Success: false, Output: "", Error: errorOutput, ExitCode: 1);
            
            // Act
            var result = PushResultAnalyzer.AnalyzePushOutput(commandResult, NullLogger.Instance);
            
            // Assert
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorType, Is.EqualTo(expectedErrorType));
            Assert.That(result.Message, Is.EqualTo(expectedMessage));
            Assert.That(result.IsRejected, Is.False);
        }

        [Test]
        [TestCase("rejected", "REMOTE_REJECTED", "Push rejected. Pull remote changes first or use force push.", true)]
        [TestCase("non-fast-forward", "REMOTE_REJECTED", "Push rejected. Pull remote changes first or use force push.", true)]
        [TestCase("fetch first", "REMOTE_REJECTED", "Push rejected. Pull remote changes first or use force push.", true)]
        public void AnalyzePushOutput_RejectedPush_ClassifiedCorrectly(string errorOutput, string expectedErrorType, string expectedMessage, bool expectedIsRejected)
        {
            // Arrange
            var commandResult = new DoltCommandResult(Success: false, Output: "", Error: errorOutput, ExitCode: 1);
            
            // Act
            var result = PushResultAnalyzer.AnalyzePushOutput(commandResult, NullLogger.Instance);
            
            // Assert
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorType, Is.EqualTo(expectedErrorType));
            Assert.That(result.Message, Is.EqualTo(expectedMessage));
            Assert.That(result.IsRejected, Is.EqualTo(expectedIsRejected));
        }

        [Test]
        [TestCase("could not resolve host", "NETWORK_ERROR")]
        [TestCase("connection timeout", "NETWORK_ERROR")]
        [TestCase("network unreachable", "NETWORK_ERROR")]
        [TestCase("permission denied", "PERMISSION_DENIED")]
        [TestCase("403 forbidden", "PERMISSION_DENIED")]
        [TestCase("repository not found", "REPOSITORY_NOT_FOUND")]
        [TestCase("404 not found", "REPOSITORY_NOT_FOUND")]
        [TestCase("unknown error", "OPERATION_FAILED")]
        public void AnalyzePushOutput_VariousFailures_ClassifiedCorrectly(string errorOutput, string expectedErrorType)
        {
            // Arrange
            var commandResult = new DoltCommandResult(Success: false, Output: "", Error: errorOutput, ExitCode: 1);
            
            // Act
            var result = PushResultAnalyzer.AnalyzePushOutput(commandResult, NullLogger.Instance);
            
            // Assert
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorType, Is.EqualTo(expectedErrorType));
            Assert.That(result.CommitsPushed, Is.EqualTo(0));
            Assert.That(result.IsUpToDate, Is.False);
            Assert.That(result.IsNewBranch, Is.False);
        }

        [Test]
        public void AnalyzePushOutput_UnrecognizedSuccessPattern_ReturnsGenericSuccess()
        {
            // Arrange
            var commandResult = new DoltCommandResult(Success: true, Output: "Some unknown success output", Error: "", ExitCode: 0);
            
            // Act
            var result = PushResultAnalyzer.AnalyzePushOutput(commandResult, NullLogger.Instance);
            
            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.Message, Is.EqualTo("Push completed successfully"));
            Assert.That(result.CommitsPushed, Is.EqualTo(-1)); // Unknown, needs calculation
            Assert.That(result.IsUpToDate, Is.False);
            Assert.That(result.IsNewBranch, Is.False);
            Assert.That(result.IsRejected, Is.False);
        }
    }
}