using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.IO;

namespace EmbranchTesting.UnitTests;

/// <summary>
/// Unit tests for the LegacyDbMigrator service.
/// Tests compatibility checking, error detection, and basic functionality.
/// Note: Full migration tests are in integration tests as they require PythonContext.
/// </summary>
[TestFixture]
public class LegacyDbMigratorTests
{
    private ILogger<LegacyDbMigrator>? _logger;
    private LegacyDbMigrator? _migrator;
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _logger = loggerFactory.CreateLogger<LegacyDbMigrator>();
        _migrator = new LegacyDbMigrator(_logger);

        // Create temp directory for tests
        _tempDir = Path.Combine(Path.GetTempPath(), $"LegacyDbMigratorTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up temp directory
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #region IsLegacyVersionError Tests

    /// <summary>
    /// Tests that IsLegacyVersionError returns true for _type errors
    /// </summary>
    [Test]
    public void IsLegacyVersionError_TypeMissing_ReturnsTrue()
    {
        // Arrange
        var exception = new Exception("could not find _type");

        // Act
        var result = _migrator!.IsLegacyVersionError(exception);

        // Assert
        Assert.That(result, Is.True, "Should detect _type error as legacy version error");
    }

    /// <summary>
    /// Tests that IsLegacyVersionError returns true for KeyError type
    /// </summary>
    [Test]
    public void IsLegacyVersionError_KeyErrorType_ReturnsTrue()
    {
        // Arrange
        var exception = new Exception("KeyError: 'type'");

        // Act
        var result = _migrator!.IsLegacyVersionError(exception);

        // Assert
        Assert.That(result, Is.True, "Should detect KeyError type as legacy version error");
    }

    /// <summary>
    /// Tests that IsLegacyVersionError returns true for configuration type errors
    /// </summary>
    [Test]
    public void IsLegacyVersionError_ConfigurationType_ReturnsTrue()
    {
        // Arrange
        var exception = new Exception("configuration missing type field");

        // Act
        var result = _migrator!.IsLegacyVersionError(exception);

        // Assert
        Assert.That(result, Is.True, "Should detect configuration type error as legacy version error");
    }

    /// <summary>
    /// Tests that IsLegacyVersionError returns false for unrelated errors
    /// </summary>
    [Test]
    public void IsLegacyVersionError_UnrelatedError_ReturnsFalse()
    {
        // Arrange
        var exception = new Exception("Network connection failed");

        // Act
        var result = _migrator!.IsLegacyVersionError(exception);

        // Assert
        Assert.That(result, Is.False, "Should not detect network error as legacy version error");
    }

    /// <summary>
    /// Tests that IsLegacyVersionError returns false for null exception
    /// </summary>
    [Test]
    public void IsLegacyVersionError_NullException_ReturnsFalse()
    {
        // Act
        var result = _migrator!.IsLegacyVersionError(null!);

        // Assert
        Assert.That(result, Is.False, "Should return false for null exception");
    }

    #endregion

    #region CheckCompatibilityAsync Basic Tests

    /// <summary>
    /// Tests that CheckCompatibilityAsync returns path_not_found for non-existent path
    /// </summary>
    [Test]
    public async Task CheckCompatibilityAsync_NonExistentPath_ReturnsPathNotFound()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir, "does_not_exist");

        // Act
        var result = await _migrator!.CheckCompatibilityAsync(nonExistentPath);

        // Assert
        Assert.That(result.RequiresMigration, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo("path_not_found"));
        Assert.That(result.DbPath, Is.EqualTo(nonExistentPath));
    }

    /// <summary>
    /// Tests that CheckCompatibilityAsync returns not_chromadb for directory without sqlite file
    /// </summary>
    [Test]
    public async Task CheckCompatibilityAsync_MissingSqlite_ReturnsNotChromaDb()
    {
        // Arrange - create empty directory
        var emptyDir = Path.Combine(_tempDir, "empty_db");
        Directory.CreateDirectory(emptyDir);

        // Act
        var result = await _migrator!.CheckCompatibilityAsync(emptyDir);

        // Assert
        Assert.That(result.RequiresMigration, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo("not_chromadb"));
        Assert.That(result.ErrorMessage, Does.Contain("chroma.sqlite3"));
    }

    #endregion

    #region DisposeMigratedCopyAsync Tests

    /// <summary>
    /// Tests that DisposeMigratedCopyAsync handles non-existent directory gracefully
    /// </summary>
    [Test]
    public async Task DisposeMigratedCopyAsync_NonExistentDirectory_CompletesWithoutError()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir, "does_not_exist_for_dispose");

        // Act & Assert - should not throw
        await _migrator!.DisposeMigratedCopyAsync(nonExistentPath);
    }

    /// <summary>
    /// Tests that DisposeMigratedCopyAsync handles null/empty path gracefully
    /// </summary>
    [Test]
    public async Task DisposeMigratedCopyAsync_NullPath_CompletesWithoutError()
    {
        // Act & Assert - should not throw
        await _migrator!.DisposeMigratedCopyAsync(null!);
        await _migrator!.DisposeMigratedCopyAsync(string.Empty);
    }

    #endregion
}
