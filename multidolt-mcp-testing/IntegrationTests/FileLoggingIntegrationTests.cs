using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DMMS.Logging;
using DMMS.Models;
using DMMS.Tools;
using DMMSTesting.Utilities;

namespace DMMSTesting.IntegrationTests;

/// <summary>
/// Integration tests for file-based logging functionality
/// </summary>
[TestFixture]
public class FileLoggingIntegrationTests
{
    private string? _testLogPath;
    private IHost? _host;
    private IServiceProvider? _serviceProvider;

    /// <summary>
    /// Reads the log file with proper file sharing to avoid lock conflicts
    /// </summary>
    private async Task<string> ReadLogFileAsync()
    {
        return await TestUtilities.ExecuteWithTimeoutAsync(
            ReadLogFileInternalAsync(),
            operationName: "Read log file");
    }

    /// <summary>
    /// Internal method to read the log file without timeout wrapper
    /// </summary>
    private async Task<string> ReadLogFileInternalAsync()
    {
        if (string.IsNullOrEmpty(_testLogPath))
            return string.Empty;

        // Retry a few times in case the file is momentarily locked
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                using var stream = new FileStream(_testLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (IOException) when (retry < 2)
            {
                await Task.Delay(100);
            }
        }

        // Final attempt
        using var finalStream = new FileStream(_testLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var finalReader = new StreamReader(finalStream);
        return await finalReader.ReadToEndAsync();
    }

    [SetUp]
    public async Task Setup()
    {
        // Create a unique log file for this test
        var testId = Guid.NewGuid().ToString("N").Substring(0, 8);
        var testLogFileName = $"DMMS_Test_{testId}.log";
        
        // The log will be created next to the test assembly
        var testAssemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var testDirectory = Path.GetDirectoryName(testAssemblyLocation) ?? Directory.GetCurrentDirectory();
        _testLogPath = Path.Combine(testDirectory, testLogFileName);

        var builder = Host.CreateApplicationBuilder();
        
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFileLogging(testLogFileName, LogLevel.Trace);
        
        builder.Services.Configure<ServerConfiguration>(options =>
        {
            options.McpPort = 6502;
            options.ConnectionTimeoutSeconds = 86400.0;
            options.BufferSize = 16 * 1024 * 1024;
            options.MaxRetries = 3;
            options.RetryDelaySeconds = 1.0;
        });

        builder.Services.AddSingleton<GetServerVersionTool>();

        _host = builder.Build();
        _serviceProvider = _host.Services;
        await TestUtilities.ExecuteWithTimeoutAsync(
            _host.StartAsync(),
            operationName: "Start host for file logging test");
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_host != null)
        {
            await TestUtilities.ExecuteWithTimeoutAsync(
                _host.StopAsync(),
                operationName: "Stop host for file logging test");
            _host.Dispose();
            _host = null;
        }

        // Clean up test log file
        await Task.Delay(500); // Give time for file to be released
        
        if (!string.IsNullOrEmpty(_testLogPath) && File.Exists(_testLogPath))
        {
            try
            {
                File.Delete(_testLogPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Tests that log file is created when logging is enabled
    /// </summary>
    [Test]
    public async Task FileLogging_CreatesLogFile()
    {
        // Give the logger time to initialize and write startup message
        await Task.Delay(100);
        
        Assert.That(File.Exists(_testLogPath), Is.True, $"Log file should be created at {_testLogPath}");
    }

    /// <summary>
    /// Tests that log entries are written to the file
    /// </summary>
    [Test]
    public async Task FileLogging_WritesLogEntries()
    {
        var logger = _serviceProvider!.GetRequiredService<ILogger<FileLoggingIntegrationTests>>();
        
        var testMessage = $"Test log message {Guid.NewGuid()}";
        logger.LogInformation(testMessage);
        
        // Give time for async write to complete
        await Task.Delay(500);
        
        var logContent = await ReadLogFileAsync();
        
        Assert.That(logContent, Does.Contain(testMessage), "Log file should contain the test message");
        Assert.That(logContent, Does.Contain("[INFO]"), "Log file should contain INFO level indicator");
    }

    /// <summary>
    /// Tests that different log levels are properly written
    /// </summary>
    [Test]
    public async Task FileLogging_WritesMultipleLogLevels()
    {
        var logger = _serviceProvider!.GetRequiredService<ILogger<FileLoggingIntegrationTests>>();
        
        var traceMsg = $"Trace message {Guid.NewGuid()}";
        var debugMsg = $"Debug message {Guid.NewGuid()}";
        var infoMsg = $"Info message {Guid.NewGuid()}";
        var warnMsg = $"Warning message {Guid.NewGuid()}";
        var errorMsg = $"Error message {Guid.NewGuid()}";
        
        logger.LogTrace(traceMsg);
        logger.LogDebug(debugMsg);
        logger.LogInformation(infoMsg);
        logger.LogWarning(warnMsg);
        logger.LogError(errorMsg);
        
        // Give time for async writes to complete
        await Task.Delay(500);
        
        var logContent = await ReadLogFileAsync();
        
        Assert.That(logContent, Does.Contain(traceMsg), "Should contain trace message");
        Assert.That(logContent, Does.Contain(debugMsg), "Should contain debug message");
        Assert.That(logContent, Does.Contain(infoMsg), "Should contain info message");
        Assert.That(logContent, Does.Contain(warnMsg), "Should contain warning message");
        Assert.That(logContent, Does.Contain(errorMsg), "Should contain error message");
        
        Assert.That(logContent, Does.Contain("[TRCE]"), "Should contain TRACE level");
        Assert.That(logContent, Does.Contain("[DBUG]"), "Should contain DEBUG level");
        Assert.That(logContent, Does.Contain("[INFO]"), "Should contain INFO level");
        Assert.That(logContent, Does.Contain("[WARN]"), "Should contain WARN level");
        Assert.That(logContent, Does.Contain("[ERRR]"), "Should contain ERROR level");
    }

    /// <summary>
    /// Tests that exception details are logged properly
    /// </summary>
    [Test]
    public async Task FileLogging_LogsExceptions()
    {
        var logger = _serviceProvider!.GetRequiredService<ILogger<FileLoggingIntegrationTests>>();
        
        try
        {
            throw new InvalidOperationException("Test exception");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during testing");
        }
        
        // Give time for async write to complete
        await Task.Delay(500);
        
        var logContent = await ReadLogFileAsync();
        
        Assert.That(logContent, Does.Contain("An error occurred during testing"), "Should contain error message");
        Assert.That(logContent, Does.Contain("InvalidOperationException"), "Should contain exception type");
        Assert.That(logContent, Does.Contain("Test exception"), "Should contain exception message");
    }

    /// <summary>
    /// Tests that GetServerVersionTool logs to file when executed
    /// </summary>
    [Test]
    public async Task GetServerVersionTool_LogsToFile()
    {
        var tool = _serviceProvider!.GetRequiredService<GetServerVersionTool>();
        
        await TestUtilities.ExecuteWithTimeoutAsync(
            tool.GetServerVersion(),
            operationName: "Execute GetServerVersion for logging test");
        
        // Give time for async write to complete
        await Task.Delay(500);
        
        var logContent = await ReadLogFileAsync();
        
        Assert.That(logContent, Does.Contain("Getting server version information"), 
            "Log file should contain GetServerVersionTool log message");
    }

    /// <summary>
    /// Tests that log file contains proper formatting with timestamps
    /// </summary>
    [Test]
    public async Task FileLogging_IncludesTimestamps()
    {
        var logger = _serviceProvider!.GetRequiredService<ILogger<FileLoggingIntegrationTests>>();
        
        logger.LogInformation("Timestamp test message");
        
        // Give time for async write to complete
        await Task.Delay(500);
        
        var logContent = await ReadLogFileAsync();
        var lines = logContent.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        
        var logLine = lines.FirstOrDefault(l => l.Contains("Timestamp test message"));
        
        Assert.That(logLine, Is.Not.Null, "Should find the test message");
        Assert.That(logLine, Does.Match(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]"), 
            "Log line should contain properly formatted timestamp");
    }

    /// <summary>
    /// Tests thread safety of file logging with concurrent writes
    /// </summary>
    [Test]
    public async Task FileLogging_HandlesConcurrentWrites()
    {
        var logger = _serviceProvider!.GetRequiredService<ILogger<FileLoggingIntegrationTests>>();
        var messages = new List<string>();
        var tasks = new List<Task>();
        
        // Create multiple tasks that log concurrently
        for (int i = 0; i < 10; i++)
        {
            var message = $"Concurrent message {i}";
            messages.Add(message);
            
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 5; j++)
                {
                    logger.LogInformation($"{message} - iteration {j}");
                }
            }));
        }
        
        await TestUtilities.ExecuteWithTimeoutAsync(
            Task.WhenAll(tasks),
            timeoutSeconds: 30,
            operationName: "Concurrent logging tasks");
        
        // Give time for all async writes to complete
        await Task.Delay(1000);
        
        var logContent = await ReadLogFileAsync();
        
        // Verify all messages were written
        foreach (var message in messages)
        {
            Assert.That(logContent, Does.Contain(message), $"Log should contain message: {message}");
        }
        
        // Count total lines to ensure all were written
        var logLines = logContent.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("Concurrent message"))
            .Count();
        
        Assert.That(logLines, Is.EqualTo(50), "Should have all 50 log entries (10 messages × 5 iterations)");
    }
}