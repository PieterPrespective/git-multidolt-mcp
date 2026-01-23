using EmbranchTesting.Utilities;

namespace EmbranchTesting.Utilities;

/// <summary>
/// Unit tests for TestUtilities timeout functionality
/// </summary>
[TestFixture]
public class TestUtilitiesTests
{
    /// <summary>
    /// Tests that ExecuteWithTimeoutAsync completes successfully for fast operations
    /// </summary>
    [Test]
    public async Task ExecuteWithTimeoutAsync_WithFastOperation_CompletesSuccessfully()
    {
        // Arrange
        var fastTask = Task.FromResult("success");

        // Act
        var result = await TestUtilities.ExecuteWithTimeoutAsync(fastTask, 5, "Fast operation test");

        // Assert
        Assert.That(result, Is.EqualTo("success"));
    }

    /// <summary>
    /// Tests that ExecuteWithTimeoutAsync throws TimeoutException for slow operations
    /// </summary>
    [Test]
    public void ExecuteWithTimeoutAsync_WithSlowOperation_ThrowsTimeoutException()
    {
        // Arrange
        var slowTask = Task.Delay(3000); // 3 second delay

        // Act & Assert
        var ex = Assert.ThrowsAsync<TimeoutException>(async () =>
            await TestUtilities.ExecuteWithTimeoutAsync(slowTask, 1, "Slow operation test"));

        Assert.That(ex.Message, Does.Contain("Slow operation test"));
        Assert.That(ex.Message, Does.Contain("timed out after 1 seconds"));
    }

    /// <summary>
    /// Tests that ExecuteWithTimeoutAsync with return value works for fast operations
    /// </summary>
    [Test]
    public async Task ExecuteWithTimeoutAsync_WithReturnValue_ReturnsCorrectValue()
    {
        // Arrange
        var taskWithReturnValue = Task.FromResult(42);

        // Act
        var result = await TestUtilities.ExecuteWithTimeoutAsync(taskWithReturnValue, 5, "Return value test");

        // Assert
        Assert.That(result, Is.EqualTo(42));
    }

    /// <summary>
    /// Tests that TryExecuteWithTimeoutAsync returns success for fast operations
    /// </summary>
    [Test]
    public async Task TryExecuteWithTimeoutAsync_WithFastOperation_ReturnsSuccess()
    {
        // Arrange
        var fastTask = Task.FromResult("success");

        // Act
        var (success, result) = await TestUtilities.TryExecuteWithTimeoutAsync(fastTask, 5);

        // Assert
        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo("success"));
    }

    /// <summary>
    /// Tests that TryExecuteWithTimeoutAsync returns failure for slow operations
    /// </summary>
    [Test]
    public async Task TryExecuteWithTimeoutAsync_WithSlowOperation_ReturnsFailure()
    {
        // Arrange
        var slowTask = Task.Delay(3000); // 3 second delay

        // Act
        var success = await TestUtilities.TryExecuteWithTimeoutAsync(slowTask, 1);

        // Assert
        Assert.That(success, Is.False);
    }

    /// <summary>
    /// Tests that MeasureExecutionTimeAsync correctly measures time
    /// </summary>
    [Test]
    public async Task MeasureExecutionTimeAsync_MeasuresTimeCorrectly()
    {
        // Arrange
        var delayTask = Task.Delay(100); // 100ms delay

        // Act
        var elapsedTime = await TestUtilities.MeasureExecutionTimeAsync(delayTask);

        // Assert
        Assert.That(elapsedTime.TotalMilliseconds, Is.GreaterThan(80)); // Allow some variance
        Assert.That(elapsedTime.TotalMilliseconds, Is.LessThan(200)); // But not too much
    }

    /// <summary>
    /// Tests that MeasureExecutionTimeAsync with return value works correctly
    /// </summary>
    [Test]
    public async Task MeasureExecutionTimeAsync_WithReturnValue_ReturnsCorrectValues()
    {
        // Arrange
        var delayTask = Task.Delay(100).ContinueWith(_ => "completed");

        // Act
        var (result, elapsedTime) = await TestUtilities.MeasureExecutionTimeAsync(delayTask);

        // Assert
        Assert.That(result, Is.EqualTo("completed"));
        Assert.That(elapsedTime.TotalMilliseconds, Is.GreaterThan(80)); // Allow some variance
        Assert.That(elapsedTime.TotalMilliseconds, Is.LessThan(200)); // But not too much
    }
}