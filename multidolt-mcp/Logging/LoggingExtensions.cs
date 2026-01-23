using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Embranch.Logging;

/// <summary>
/// Extension methods for configuring file-based logging
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Adds file logging to the logging builder
    /// </summary>
    /// <param name="builder">The logging builder to configure</param>
    /// <param name="logFileName">Optional log file name (defaults to Embranch_[timestamp].log)</param>
    /// <param name="minimumLevel">Minimum log level (defaults to Information)</param>
    /// <returns>The logging builder for chaining</returns>
    public static ILoggingBuilder AddFileLogging(this ILoggingBuilder builder, string? logFileName = null, LogLevel minimumLevel = LogLevel.Information)
    {
        var logPath = GetLogFilePath(logFileName);
        builder.AddProvider(new FileLoggerProvider(logPath, minimumLevel));
        
        // Write startup message
        WriteStartupMessage(logPath);
        
        return builder;
    }

    /// <summary>
    /// Gets the full path for the log file
    /// </summary>
    private static string GetLogFilePath(string? logFileName)
    {
        if (string.IsNullOrWhiteSpace(logFileName))
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            logFileName = $"Embranch_{timestamp}.log";
        }

        // Get the directory where the executable is located
        var exeLocation = Assembly.GetExecutingAssembly().Location;
        var exeDirectory = Path.GetDirectoryName(exeLocation) ?? Directory.GetCurrentDirectory();
        
        return Path.Combine(exeDirectory, logFileName);
    }

    /// <summary>
    /// Writes initial startup information to the log file
    /// </summary>
    private static void WriteStartupMessage(string logPath)
    {
        try
        {
            var startupMessage = $@"
================================================================================
Embranch Log
Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
Process ID: {Environment.ProcessId}
Machine: {Environment.MachineName}
OS: {Environment.OSVersion}
.NET Version: {Environment.Version}
Log File: {logPath}
================================================================================
";
            File.AppendAllText(logPath, startupMessage);
        }
        catch
        {
            // Ignore startup message write failures
        }
    }
}