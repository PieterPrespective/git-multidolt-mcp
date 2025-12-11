using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Python.Runtime;

namespace DMMS.Services;

/// <summary>
/// Static context manager for all Python operations, ensuring thread-safe execution on a dedicated thread
/// </summary>
public static class PythonContext
{
    private static readonly ConcurrentQueue<PythonOperation> _operationQueue = new();
    private static readonly ManualResetEventSlim _queueSignal = new(false);
    private static Thread? _pythonThread;
    private static bool _isRunning;
    private static bool _isInitialized;
    private static ILogger? _logger;
    private static readonly object _initLock = new();
    private static CancellationTokenSource? _shutdownTokenSource;

    /// <summary>
    /// Gets whether the Python context is initialized and running
    /// </summary>
    public static bool IsInitialized => _isInitialized;

    /// <summary>
    /// Initializes the Python context with a dedicated thread for Python operations
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic output</param>
    /// <param name="pythonDllPath">Optional path to Python DLL</param>
    public static void Initialize(ILogger? logger = null, string? pythonDllPath = null)
    {
        logger?.LogInformation($"Attempting to initialize PythonContext : is initialized {_isInitialized}");

        lock (_initLock)
        {
            if (_isInitialized)
            {
                logger?.LogWarning("PythonContext is already initialized");
                return;
            }

            _logger = logger;
            _shutdownTokenSource = new CancellationTokenSource();
            _isRunning = true;

            // Set Python DLL path if provided
            string targetPythonPath = "";
            if (!string.IsNullOrEmpty(pythonDllPath))
            {
                targetPythonPath = pythonDllPath;
            }
            else
            {
                // Attempt to find Python DLL automatically
                var foundPath = PythonContextUtility.FindPythonDll(_logger);
                if (!string.IsNullOrEmpty(foundPath))
                {
                    targetPythonPath = foundPath;
                }
                else
                {
                    _logger?.LogWarning("Using default Python DLL path");
                }
            }

            if (!string.IsNullOrEmpty(targetPythonPath))
            {
                //CASE : the pythonengine may have already been initialized by another component
                if (!PythonEngine.IsInitialized)
                {
                    Runtime.PythonDLL = targetPythonPath;
                }
                _logger?.LogInformation($"Set Python DLL path to: {pythonDllPath}");
            }

            // Create and start the dedicated Python thread
            _pythonThread = new Thread(PythonThreadWorker)
            {
                Name = "PythonContextThread",
                IsBackground = false
            };

            _pythonThread.Start();

            if(!_pythonThread.IsAlive)
                {
                throw new InvalidOperationException("Failed to start Python thread");
                }


            // Wait for thread to initialize Python
            var initComplete = new ManualResetEventSlim(false);
            var initOperation = new PythonOperation<bool>(
                operation: () => true,
                onResult: _ => initComplete.Set(),
                onError: ex =>
                {
                    _logger?.LogError(ex, "Failed to initialize Python runtime");
                    initComplete.Set();
                },
                timeoutMs: 30000,
                operationName: "PythonInitialization"
            );

            _operationQueue.Enqueue(initOperation);
            _queueSignal.Set();

            if (!initComplete.Wait(10000))
            {
                throw new InvalidOperationException("Python initialization timed out");
            }

            _isInitialized = true;
            _logger?.LogInformation("PythonContext initialized successfully");
        }
    }

    /// <summary>
    /// Shuts down the Python context and cleans up resources
    /// </summary>
    public static void Shutdown()
    {
        lock (_initLock)
        {
            if (!_isInitialized)
                return;

            _logger?.LogInformation("Shutting down PythonContext");
            _isRunning = false;
            _shutdownTokenSource?.Cancel();
            _queueSignal.Set();

            // Wait for thread to complete with timeout
            if (_pythonThread?.Join(5000) == false)
            {
                _logger?.LogWarning("Python thread did not shutdown gracefully, aborting");
                // Don't abort in production, let it finish
            }

            _shutdownTokenSource?.Dispose();
            _shutdownTokenSource = null;
            _pythonThread = null;
            _isInitialized = false;

            _logger?.LogInformation("PythonContext shutdown complete");

        }
    }

    /// <summary>
    /// Executes a Python operation asynchronously on the dedicated Python thread
    /// </summary>
    /// <typeparam name="T">The type of the operation result</typeparam>
    /// <param name="operation">The Python operation to execute</param>
    /// <param name="timeoutMs">Timeout in milliseconds (default: 30000)</param>
    /// <param name="operationName">Optional name for logging</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>The result of the Python operation</returns>
    public static Task<T> ExecuteAsync<T>(
        Func<T> operation, 
        int timeoutMs = 30000, 
        string? operationName = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
            throw new InvalidOperationException("PythonContext is not initialized. Call Initialize() first.");

        // If already on the Python thread, execute directly (if we were to enqueue, it would deadlock)
        if (Thread.CurrentThread.ManagedThreadId == PythonContext._pythonThread?.ManagedThreadId)
        {
            T result = operation();
            return Task.FromResult(result);
        }

        var tcs = new TaskCompletionSource<T>();
        var pythonOp = new PythonOperation<T>(
            operation: operation,
            onResult: result => tcs.TrySetResult(result),
            onError: ex => tcs.TrySetException(ex),
            timeoutMs: timeoutMs,
            operationName: operationName ?? operation.Method?.Name ?? "UnnamedOperation",
            cancellationToken: cancellationToken
        );

        _operationQueue.Enqueue(pythonOp);
        _queueSignal.Set();

        return tcs.Task;
    }

    /// <summary>
    /// Executes a Python operation synchronously on the dedicated Python thread
    /// </summary>
    /// <typeparam name="T">The type of the operation result</typeparam>
    /// <param name="operation">The Python operation to execute</param>
    /// <param name="timeoutMs">Timeout in milliseconds (default: 30000)</param>
    /// <param name="operationName">Optional name for logging</param>
    /// <returns>The result of the Python operation</returns>
    public static T Execute<T>(
        Func<T> operation,
        int timeoutMs = 30000,
        string? operationName = null)
    {
        return ExecuteAsync(operation, timeoutMs, operationName).GetAwaiter().GetResult();
    }


    /// <summary>
    /// The worker method that runs on the dedicated Python thread
    /// </summary>
    private static void PythonThreadWorker()
    {
        try
        {
            _logger?.LogInformation("Python thread starting, initializing Python runtime");

            // Initialize Python on this thread

            if (PythonEngine.IsInitialized)
            {
                _logger?.LogError("Python thread was already started on different thread, cannot initialize here");
                return;
            }
            PythonEngine.Initialize();

            _logger?.LogInformation("Python runtime initialized on dedicated thread");

            // Process operations until shutdown
            while (_isRunning)
            {
                // Wait for signal or timeout every 100ms to check shutdown
                _queueSignal.Wait(100);
                _queueSignal.Reset();

                // Process all pending operations
                while (_operationQueue.TryDequeue(out var operation))
                {
                    if (!_isRunning)
                        break;

                    operation.Execute(_logger);
                }
            }

            _logger?.LogInformation("Python thread shutting down, cleaning up Python runtime");
            
            // Cleanup Python resources
            using (Py.GIL())
            {
                // Force Python garbage collection before shutdown
                using (dynamic gc = Py.Import("gc"))
                {
                    gc.collect();
                    _logger?.LogInformation("Python garbage collection completed");
                }
            }

            // Note: We don't call PythonEngine.Shutdown() as it can cause issues
            // The runtime will be cleaned up on process exit
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Fatal error in Python thread");
        }
    }
}

/// <summary>
/// Base class for Python operations
/// </summary>
public abstract class PythonOperation
{
    /// <summary>
    /// Gets the operation name for logging
    /// </summary>
    public string OperationName { get; }

    /// <summary>
    /// Gets the timeout in milliseconds
    /// </summary>
    public int TimeoutMs { get; }

    /// <summary>
    /// Gets the cancellation token
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets the timestamp when the operation was created
    /// </summary>
    public DateTime CreatedAt { get; }

    protected PythonOperation(string operationName, int timeoutMs, CancellationToken cancellationToken = default)
    {
        OperationName = operationName;
        TimeoutMs = timeoutMs;
        CancellationToken = cancellationToken;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Executes the Python operation
    /// </summary>
    public abstract void Execute(ILogger? logger);
}

/// <summary>
/// Represents a Python operation with a typed result
/// </summary>
/// <typeparam name="T">The type of the operation result</typeparam>
public class PythonOperation<T> : PythonOperation
{
    private readonly Func<T> _operation;
    private readonly Action<T> _onResult;
    private readonly Action<Exception> _onError;

    /// <summary>
    /// Creates a new Python operation
    /// </summary>
    /// <param name="operation">The operation to execute within the Python GIL</param>
    /// <param name="onResult">Callback for successful result</param>
    /// <param name="onError">Callback for errors</param>
    /// <param name="timeoutMs">Timeout in milliseconds</param>
    /// <param name="operationName">Name for logging</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    public PythonOperation(
        Func<T> operation,
        Action<T> onResult,
        Action<Exception> onError,
        int timeoutMs,
        string operationName,
        CancellationToken cancellationToken = default)
        : base(operationName, timeoutMs, cancellationToken)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _onResult = onResult ?? throw new ArgumentNullException(nameof(onResult));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
    }

    /// <summary>
    /// Executes the Python operation within the GIL
    /// </summary>
    public override void Execute(ILogger? logger)
    {
        var elapsed = (DateTime.UtcNow - CreatedAt).TotalMilliseconds;
        if (elapsed > TimeoutMs)
        {
            logger?.LogWarning($"Operation '{OperationName}' timed out before execution (waited {elapsed:F0}ms)");
            _onError(new TimeoutException($"Operation '{OperationName}' timed out before execution"));
            return;
        }

        if (CancellationToken.IsCancellationRequested)
        {
            logger?.LogInformation($"Operation '{OperationName}' was cancelled");
            _onError(new OperationCanceledException($"Operation '{OperationName}' was cancelled"));
            return;
        }

        try
        {
            logger?.LogDebug($"Executing Python operation '{OperationName}'");
            T result;

            // All Python operations must happen within the GIL
            using (Py.GIL())
            {
                result = _operation();
            }

            logger?.LogDebug($"Python operation '{OperationName}' completed successfully");
            _onResult(result);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, $"Python operation '{OperationName}' failed");
            _onError(ex);
        }
    }
}

/// <summary>
/// Utility class for Python context operations
/// </summary>
public static class PythonContextUtility
{
    /// <summary>
    /// Attempts to find the Python DLL on the system
    /// </summary>
    /// <returns>Path to Python DLL if found, null otherwise</returns>
    public static string? FindPythonDll(ILogger? logger = null)
    {
        if (OperatingSystem.IsWindows())
        {
            // Common Python installation paths on Windows
            string[] possiblePaths = 
            {
                @"C:\ProgramData\anaconda3\python311.dll",
                @"C:\ProgramData\anaconda3\python312.dll",
                @"C:\Python311\python311.dll",
                @"C:\Python312\python312.dll",
                @"C:\Python310\python310.dll",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python311\python311.dll",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python312\python312.dll",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python310\python310.dll"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    logger?.LogInformation($"Found Python DLL at: {path}");
                    return path;
                }
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            // Common Python paths on Linux
            string[] possiblePaths =
            {
                "/usr/lib/x86_64-linux-gnu/libpython3.11.so",
                "/usr/lib/x86_64-linux-gnu/libpython3.10.so",
                "/usr/lib/x86_64-linux-gnu/libpython3.9.so",
                "/usr/lib/libpython3.11.so",
                "/usr/lib/libpython3.10.so"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    logger?.LogInformation($"Found Python library at: {path}");
                    return path;
                }
            }
        }

        logger?.LogWarning("Could not find Python DLL/library, will use system default");
        return null;
    }
}