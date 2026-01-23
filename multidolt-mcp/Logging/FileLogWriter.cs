using System.Collections.Concurrent;

namespace Embranch.Logging;

/// <summary>
/// Thread-safe file writer for logging
/// </summary>
public sealed class FileLogWriter : IDisposable
{
    private readonly BlockingCollection<string> _messageQueue;
    private readonly Thread _writerThread;
    private readonly string _logFilePath;
    private readonly object _lock = new();
    private StreamWriter? _streamWriter;
    private bool _disposed;

    /// <summary>
    /// Gets the path to the current log file
    /// </summary>
    public string LogFilePath => _logFilePath;

    /// <summary>
    /// Initializes a new instance of the FileLogWriter class
    /// </summary>
    /// <param name="logFilePath">Path to the log file</param>
    public FileLogWriter(string logFilePath)
    {
        _logFilePath = logFilePath;
        _messageQueue = new BlockingCollection<string>();
        
        EnsureLogFileExists();
        
        _writerThread = new Thread(ProcessLogQueue)
        {
            IsBackground = true,
            Name = "Embranch-FileLogWriter"
        };
        _writerThread.Start();
    }

    /// <summary>
    /// Writes a line to the log file
    /// </summary>
    /// <param name="message">The message to write</param>
    public void WriteLine(string message)
    {
        if (!_disposed)
        {
            _messageQueue.TryAdd(message);
        }
    }

    /// <summary>
    /// Ensures the log file and its directory exist
    /// </summary>
    private void EnsureLogFileExists()
    {
        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            lock (_lock)
            {
                // Open file stream with shared read access so tests can read while writing
                var fileStream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                _streamWriter = new StreamWriter(fileStream)
                {
                    AutoFlush = true
                };
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create log file at {_logFilePath}", ex);
        }
    }

    /// <summary>
    /// Processes the log message queue in a background thread
    /// </summary>
    private void ProcessLogQueue()
    {
        try
        {
            foreach (var message in _messageQueue.GetConsumingEnumerable())
            {
                WriteToFile(message);
            }
        }
        catch (InvalidOperationException)
        {
            // Queue was marked as complete for adding
        }
    }

    /// <summary>
    /// Writes a message to the file
    /// </summary>
    private void WriteToFile(string message)
    {
        lock (_lock)
        {
            try
            {
                _streamWriter?.WriteLine(message);
            }
            catch (Exception)
            {
                // Swallow exceptions to prevent crashing the logging thread
                // In production, might want to handle this differently
            }
        }
    }

    /// <summary>
    /// Disposes the writer and associated resources
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _messageQueue.CompleteAdding();
        
        // Give the writer thread time to finish
        if (!_writerThread.Join(TimeSpan.FromSeconds(5)))
        {
            // Force abort if it doesn't finish in time
            try
            {
                _writerThread.Interrupt();
            }
            catch
            {
                // Ignore interruption errors
            }
        }
        
        lock (_lock)
        {
            _streamWriter?.Dispose();
            _streamWriter = null;
        }
        
        _messageQueue.Dispose();
    }
}