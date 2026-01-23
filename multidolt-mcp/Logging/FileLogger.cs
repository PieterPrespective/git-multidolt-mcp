using Microsoft.Extensions.Logging;

namespace Embranch.Logging;

/// <summary>
/// Logger implementation that writes to a file
/// </summary>
public sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLogWriter _logWriter;
    private readonly LogLevel _minimumLevel;

    /// <summary>
    /// Initializes a new instance of the FileLogger class
    /// </summary>
    /// <param name="categoryName">The category name for this logger</param>
    /// <param name="logWriter">The file writer to use for logging</param>
    /// <param name="minimumLevel">The minimum log level to write</param>
    public FileLogger(string categoryName, FileLogWriter logWriter, LogLevel minimumLevel)
    {
        _categoryName = categoryName;
        _logWriter = logWriter;
        _minimumLevel = minimumLevel;
    }

    /// <summary>
    /// Begins a logical operation scope
    /// </summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default;

    /// <summary>
    /// Checks if the given log level is enabled
    /// </summary>
    /// <param name="logLevel">The log level to check</param>
    /// <returns>True if the log level is enabled</returns>
    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= _minimumLevel;
    }

    /// <summary>
    /// Writes a log entry
    /// </summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null)
        {
            return;
        }

        var logEntry = FormatLogEntry(logLevel, eventId, message, exception);
        _logWriter.WriteLine(logEntry);
    }

    /// <summary>
    /// Formats a log entry with timestamp, level, category, and message
    /// </summary>
    private string FormatLogEntry(LogLevel logLevel, EventId eventId, string message, Exception? exception)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var level = GetLogLevelString(logLevel);
        var category = GetShortCategoryName(_categoryName);
        
        var logMessage = $"[{timestamp}] [{level}] [{category}]";
        
        if (eventId.Id != 0)
        {
            logMessage += $" [{eventId.Id}]";
        }
        
        logMessage += $" {message}";
        
        if (exception != null)
        {
            logMessage += Environment.NewLine + exception.ToString();
        }
        
        return logMessage;
    }

    /// <summary>
    /// Gets a shortened category name for cleaner logs
    /// </summary>
    private static string GetShortCategoryName(string categoryName)
    {
        var lastDotIndex = categoryName.LastIndexOf('.');
        return lastDotIndex >= 0 ? categoryName.Substring(lastDotIndex + 1) : categoryName;
    }

    /// <summary>
    /// Converts LogLevel to a fixed-width string representation
    /// </summary>
    private static string GetLogLevelString(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERRR",
            LogLevel.Critical => "CRIT",
            LogLevel.None => "NONE",
            _ => "UNKN"
        };
    }
}