using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Embranch.Logging;

/// <summary>
/// Logger provider that writes log messages to a file
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logFilePath;
    private readonly LogLevel _minimumLevel;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly FileLogWriter _logWriter;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the FileLoggerProvider class
    /// </summary>
    /// <param name="logFilePath">Path to the log file</param>
    /// <param name="minimumLevel">Minimum log level to write</param>
    public FileLoggerProvider(string logFilePath, LogLevel minimumLevel = LogLevel.Information)
    {
        _logFilePath = logFilePath;
        _minimumLevel = minimumLevel;
        _logWriter = new FileLogWriter(logFilePath);
    }

    /// <summary>
    /// Creates a new logger instance for the specified category
    /// </summary>
    /// <param name="categoryName">The category name for the logger</param>
    /// <returns>A new FileLogger instance</returns>
    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _logWriter, _minimumLevel));
    }

    /// <summary>
    /// Disposes the provider and all associated resources
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _logWriter.Dispose();
        _loggers.Clear();
    }
}