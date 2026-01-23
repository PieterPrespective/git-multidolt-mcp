using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace EmbranchTesting;

/// <summary>
/// Logger that writes directly to a file, ensuring all logs are captured during tests
/// </summary>
public class TestFileLogger : ILogger
{
    private readonly string _filePath;
    private readonly object _lock = new object();

    public TestFileLogger(string filePath)
    {
        _filePath = filePath;
        // Clear the file at start
        lock (_lock)
        {
            File.WriteAllText(_filePath, $"=== Test Log Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        }
    }

    public IDisposable BeginScope<TState>(TState state) => new NoOpDisposable();

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = $"[{DateTime.Now:HH:mm:ss.fff}] [{logLevel}] {formatter(state, exception)}";
        if (exception != null)
        {
            message += $"\nException: {exception}";
        }

        lock (_lock)
        {
            File.AppendAllText(_filePath, message + "\n");
        }

        // Also write to console for immediate feedback
        Console.WriteLine(message);
    }

    private class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

public class TestFileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;

    public TestFileLoggerProvider(string filePath)
    {
        _filePath = filePath;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new TestFileLogger(_filePath);
    }

    public void Dispose() { }
}