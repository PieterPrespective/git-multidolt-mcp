using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Moq;
using DMMS.Services;

namespace DMMSTesting.UnitTests;

/// <summary>
/// PP13-79: Unit tests for GitIntegration service
/// </summary>
[TestFixture]
[Category("Unit")]
public class GitIntegrationTests
{
    private Mock<ILogger<GitIntegration>> _loggerMock = null!;
    private GitIntegration _gitIntegration = null!;

    [SetUp]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<GitIntegration>>();
        _gitIntegration = new GitIntegration(_loggerMock.Object);
    }

    [Test]
    public async Task IsGitAvailableAsync_WhenGitInstalled_ReturnsTrue()
    {
        // Act
        var result = await _gitIntegration.IsGitAvailableAsync();

        // Assert
        // This test assumes Git is installed on the test machine
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task GetGitVersionAsync_WhenGitInstalled_ReturnsVersion()
    {
        // Act
        var result = await _gitIntegration.GetGitVersionAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("git version"));
    }

    [Test]
    public async Task IsGitRepositoryAsync_InsideRepo_ReturnsTrue()
    {
        // Arrange - use the current project directory which is a Git repo
        var repoPath = GetGitRepoRoot();
        if (repoPath == null)
        {
            Assert.Ignore("Test requires running from within a Git repository");
            return;
        }

        // Act
        var result = await _gitIntegration.IsGitRepositoryAsync(repoPath);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsGitRepositoryAsync_OutsideRepo_ReturnsFalse()
    {
        // Arrange - use temp directory which should not be a Git repo
        var tempPath = Path.GetTempPath();

        // Act
        var result = await _gitIntegration.IsGitRepositoryAsync(tempPath);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsGitRepositoryAsync_NonExistentPath_ReturnsFalse()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        // Act
        var result = await _gitIntegration.IsGitRepositoryAsync(nonExistentPath);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task GetGitRootAsync_InsideRepo_ReturnsRoot()
    {
        // Arrange
        var repoPath = GetGitRepoRoot();
        if (repoPath == null)
        {
            Assert.Ignore("Test requires running from within a Git repository");
            return;
        }

        // Act
        var result = await _gitIntegration.GetGitRootAsync(repoPath);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(Directory.Exists(result), Is.True);
        Assert.That(Directory.Exists(Path.Combine(result!, ".git")), Is.True);
    }

    [Test]
    public async Task GetGitRootAsync_OutsideRepo_ReturnsNull()
    {
        // Arrange
        var tempPath = Path.GetTempPath();

        // Act
        var result = await _gitIntegration.GetGitRootAsync(tempPath);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetGitRootAsync_CachesResult()
    {
        // Arrange
        var repoPath = GetGitRepoRoot();
        if (repoPath == null)
        {
            Assert.Ignore("Test requires running from within a Git repository");
            return;
        }

        // Act - call twice
        var result1 = await _gitIntegration.GetGitRootAsync(repoPath);
        var result2 = await _gitIntegration.GetGitRootAsync(repoPath);

        // Assert
        Assert.That(result1, Is.EqualTo(result2));
    }

    [Test]
    public async Task GetCurrentGitCommitAsync_InsideRepo_ReturnsHash()
    {
        // Arrange
        var repoPath = GetGitRepoRoot();
        if (repoPath == null)
        {
            Assert.Ignore("Test requires running from within a Git repository");
            return;
        }

        // Act
        var result = await _gitIntegration.GetCurrentGitCommitAsync(repoPath);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Length, Is.EqualTo(40)); // Full SHA-1 hash
        Assert.That(result, Does.Match("^[a-f0-9]{40}$"));
    }

    [Test]
    public async Task GetCurrentGitCommitAsync_OutsideRepo_ReturnsNull()
    {
        // Arrange
        var tempPath = Path.GetTempPath();

        // Act
        var result = await _gitIntegration.GetCurrentGitCommitAsync(tempPath);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetCurrentBranchAsync_InsideRepo_ReturnsBranchName()
    {
        // Arrange
        var repoPath = GetGitRepoRoot();
        if (repoPath == null)
        {
            Assert.Ignore("Test requires running from within a Git repository");
            return;
        }

        // Act
        var result = await _gitIntegration.GetCurrentBranchAsync(repoPath);

        // Assert
        // May be null if in detached HEAD state, but should not throw
        // In most cases it should return a branch name
        if (result != null)
        {
            Assert.That(result, Is.Not.Empty);
        }
    }

    [Test]
    public async Task GetCurrentBranchAsync_OutsideRepo_ReturnsNull()
    {
        // Arrange
        var tempPath = Path.GetTempPath();

        // Act
        var result = await _gitIntegration.GetCurrentBranchAsync(tempPath);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task IsFileTrackedAsync_TrackedFile_ReturnsTrue()
    {
        // Arrange
        var repoPath = GetGitRepoRoot();
        if (repoPath == null)
        {
            Assert.Ignore("Test requires running from within a Git repository");
            return;
        }

        // Find a file that should be tracked (e.g., README or a .cs file)
        var csprojFile = Directory.GetFiles(repoPath, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();
        if (csprojFile == null)
        {
            Assert.Ignore("No .csproj file found for testing");
            return;
        }

        // Act
        var result = await _gitIntegration.IsFileTrackedAsync(repoPath, csprojFile);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsFileTrackedAsync_UntrackedFile_ReturnsFalse()
    {
        // Arrange
        var repoPath = GetGitRepoRoot();
        if (repoPath == null)
        {
            Assert.Ignore("Test requires running from within a Git repository");
            return;
        }

        var untrackedFile = Path.Combine(repoPath, "nonexistent_test_file_12345.txt");

        // Act
        var result = await _gitIntegration.IsFileTrackedAsync(repoPath, untrackedFile);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsFileTrackedAsync_OutsideRepo_ReturnsFalse()
    {
        // Arrange
        var tempPath = Path.GetTempPath();
        var somePath = Path.Combine(tempPath, "somefile.txt");

        // Act
        var result = await _gitIntegration.IsFileTrackedAsync(tempPath, somePath);

        // Assert
        Assert.That(result, Is.False);
    }

    private string? GetGitRepoRoot()
    {
        // Navigate up from current directory to find Git root
        var current = Directory.GetCurrentDirectory();

        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        return null;
    }
}
