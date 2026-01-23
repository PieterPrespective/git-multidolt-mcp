using System.Diagnostics;

namespace EmbranchTesting.Utilities;

/// <summary>
/// Utility functions for test operations
/// </summary>
public static class TestUtilities
{
    /// <summary>
    /// Default timeout for test operations in seconds
    /// </summary>
    public const int DefaultTimeoutSeconds = 15;

    /// <summary>
    /// Executes an async task with a timeout and throws TimeoutException if the task doesn't complete in time
    /// </summary>
    /// <typeparam name="T">The return type of the task</typeparam>
    /// <param name="taskToExecute">The task to execute</param>
    /// <param name="timeoutSeconds">Timeout in seconds (defaults to 15 seconds)</param>
    /// <param name="operationName">Name of the operation for error messages</param>
    /// <returns>The result of the completed task</returns>
    /// <exception cref="TimeoutException">Thrown when the task doesn't complete within the specified timeout</exception>
    public static async Task<T> ExecuteWithTimeoutAsync<T>(Task<T> taskToExecute, int timeoutSeconds = DefaultTimeoutSeconds, string operationName = "Test operation")
    {
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
        var completedTask = await Task.WhenAny(taskToExecute, timeoutTask);
        
        if (completedTask == timeoutTask)
        {
            throw new TimeoutException($"{operationName} timed out after {timeoutSeconds} seconds");
        }
        
        return await taskToExecute;
    }

    /// <summary>
    /// Executes an async task with a timeout and throws TimeoutException if the task doesn't complete in time
    /// </summary>
    /// <param name="taskToExecute">The task to execute</param>
    /// <param name="timeoutSeconds">Timeout in seconds (defaults to 15 seconds)</param>
    /// <param name="operationName">Name of the operation for error messages</param>
    /// <exception cref="TimeoutException">Thrown when the task doesn't complete within the specified timeout</exception>
    public static async Task ExecuteWithTimeoutAsync(Task taskToExecute, int timeoutSeconds = DefaultTimeoutSeconds, string operationName = "Test operation")
    {
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
        var completedTask = await Task.WhenAny(taskToExecute, timeoutTask);
        
        if (completedTask == timeoutTask)
        {
            throw new TimeoutException($"{operationName} timed out after {timeoutSeconds} seconds");
        }
        
        await taskToExecute;
    }

    /// <summary>
    /// Executes an async task with a timeout using the pattern from the provided code
    /// Returns true if the task completed successfully, false if it timed out
    /// </summary>
    /// <typeparam name="T">The return type of the task</typeparam>
    /// <param name="taskToExecute">The task to execute</param>
    /// <param name="timeoutSeconds">Timeout in seconds (defaults to 15 seconds)</param>
    /// <returns>A tuple with success status and the result (if successful)</returns>
    public static async Task<(bool Success, T? Result)> TryExecuteWithTimeoutAsync<T>(Task<T> taskToExecute, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
        var completedTask = await Task.WhenAny(taskToExecute, timeoutTask);
        
        if (completedTask == timeoutTask)
        {
            return (false, default(T));
        }
        
        var result = await taskToExecute;
        return (true, result);
    }

    /// <summary>
    /// Executes an async task with a timeout using the pattern from the provided code
    /// Returns true if the task completed successfully, false if it timed out
    /// </summary>
    /// <param name="taskToExecute">The task to execute</param>
    /// <param name="timeoutSeconds">Timeout in seconds (defaults to 15 seconds)</param>
    /// <returns>True if the task completed successfully, false if it timed out</returns>
    public static async Task<bool> TryExecuteWithTimeoutAsync(Task taskToExecute, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
        var completedTask = await Task.WhenAny(taskToExecute, timeoutTask);
        
        if (completedTask == timeoutTask)
        {
            return false;
        }
        
        await taskToExecute;
        return true;
    }

    /// <summary>
    /// Measures the execution time of a task
    /// </summary>
    /// <typeparam name="T">The return type of the task</typeparam>
    /// <param name="taskToExecute">The task to execute and measure</param>
    /// <returns>A tuple with the result and elapsed time</returns>
    public static async Task<(T Result, TimeSpan ElapsedTime)> MeasureExecutionTimeAsync<T>(Task<T> taskToExecute)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await taskToExecute;
        stopwatch.Stop();
        return (result, stopwatch.Elapsed);
    }

    /// <summary>
    /// Measures the execution time of a task
    /// </summary>
    /// <param name="taskToExecute">The task to execute and measure</param>
    /// <returns>The elapsed time</returns>
    public static async Task<TimeSpan> MeasureExecutionTimeAsync(Task taskToExecute)
    {
        var stopwatch = Stopwatch.StartNew();
        await taskToExecute;
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }
}