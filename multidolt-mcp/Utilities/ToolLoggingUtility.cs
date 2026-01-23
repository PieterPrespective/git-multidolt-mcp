using Microsoft.Extensions.Logging;

namespace Embranch.Utilities;

/// <summary>
/// Utility class providing safe logging methods for MCP tools with consistent formatting
/// Ensures logging operations never cause null reference exceptions and provides
/// standardized tool entry/exit logging patterns
/// </summary>
public static class ToolLoggingUtility
{
    /// <summary>
    /// Logs tool call start with standardized format
    /// Safe to call even if logger is null
    /// </summary>
    /// <param name="logger">The logger instance (can be null)</param>
    /// <param name="toolName">Name of the tool being called</param>
    /// <param name="methodName">Name of the method being executed</param>
    /// <param name="parameters">Optional parameter summary for context</param>
    public static void LogToolStart(ILogger? logger, string toolName, string methodName, string? parameters = null)
    {
        if (logger == null) return;
        
        var paramInfo = !string.IsNullOrEmpty(parameters) ? $" - {parameters}" : "";
        logger.LogInformation($"[{toolName}] +++++++++++++++++++++++++ TOOLCALL START : {methodName}{paramInfo} ++++++++++++++++++++++++++++++++++");
    }

    /// <summary>
    /// Logs tool call successful completion with standardized format
    /// Safe to call even if logger is null
    /// </summary>
    /// <param name="logger">The logger instance (can be null)</param>
    /// <param name="toolName">Name of the tool that completed</param>
    /// <param name="methodName">Name of the method that completed</param>
    /// <param name="result">Optional result summary</param>
    public static void LogToolSuccess(ILogger? logger, string toolName, string methodName, string? result = null)
    {
        if (logger == null) return;
        
        var resultInfo = !string.IsNullOrEmpty(result) ? $" - {result}" : "";
        logger.LogInformation($"[{toolName}] +++++++++++++++++++++++++ TOOLCALL EXIT : {methodName} - SUCCESS{resultInfo} ++++++++++++++++++++++++++++++++++");
    }

    /// <summary>
    /// Logs tool call failure with standardized format
    /// Safe to call even if logger is null
    /// </summary>
    /// <param name="logger">The logger instance (can be null)</param>
    /// <param name="toolName">Name of the tool that failed</param>
    /// <param name="methodName">Name of the method that failed</param>
    /// <param name="error">Error description</param>
    public static void LogToolFailure(ILogger? logger, string toolName, string methodName, string error)
    {
        if (logger == null) return;
        
        logger.LogInformation($"[{toolName}] +++++++++++++++++++++++++ TOOLCALL EXIT : {methodName} - FAILED: {error} ++++++++++++++++++++++++++++++++++");
    }

    /// <summary>
    /// Logs tool call exception with standardized format
    /// Safe to call even if logger is null
    /// </summary>
    /// <param name="logger">The logger instance (can be null)</param>
    /// <param name="toolName">Name of the tool that encountered exception</param>
    /// <param name="methodName">Name of the method that encountered exception</param>
    /// <param name="exception">The exception that occurred</param>
    public static void LogToolException(ILogger? logger, string toolName, string methodName, Exception exception)
    {
        if (logger == null) return;
        
        logger.LogError(exception, $"[{toolName}] +++++++++++++++++++++++++ TOOLCALL EXIT : {methodName} - EXCEPTION ++++++++++++++++++++++++++++++++++");
    }

    /// <summary>
    /// Logs general tool information with standardized format and null safety
    /// Safe to call even if logger is null
    /// </summary>
    /// <param name="logger">The logger instance (can be null)</param>
    /// <param name="toolName">Name of the tool</param>
    /// <param name="message">Information message to log</param>
    public static void LogToolInfo(ILogger? logger, string toolName, string message)
    {
        if (logger == null) return;
        
        logger.LogInformation($"[{toolName}] {message}");
    }

    /// <summary>
    /// Logs general tool warning with standardized format and null safety
    /// Safe to call even if logger is null
    /// </summary>
    /// <param name="logger">The logger instance (can be null)</param>
    /// <param name="toolName">Name of the tool</param>
    /// <param name="message">Warning message to log</param>
    public static void LogToolWarning(ILogger? logger, string toolName, string message)
    {
        if (logger == null) return;
        
        logger.LogWarning($"[{toolName}] {message}");
    }

    /// <summary>
    /// Logs general tool error with standardized format and null safety
    /// Safe to call even if logger is null
    /// </summary>
    /// <param name="logger">The logger instance (can be null)</param>
    /// <param name="toolName">Name of the tool</param>
    /// <param name="message">Error message to log</param>
    public static void LogToolError(ILogger? logger, string toolName, string message)
    {
        if (logger == null) return;
        
        logger.LogError($"[{toolName}] {message}");
    }

    /// <summary>
    /// Logs debug information for tools with standardized format and null safety
    /// Safe to call even if logger is null
    /// </summary>
    /// <param name="logger">The logger instance (can be null)</param>
    /// <param name="toolName">Name of the tool</param>
    /// <param name="message">Debug message to log</param>
    public static void LogToolDebug(ILogger? logger, string toolName, string message)
    {
        if (logger == null) return;
        
        logger.LogDebug($"[{toolName}] {message}");
    }
}