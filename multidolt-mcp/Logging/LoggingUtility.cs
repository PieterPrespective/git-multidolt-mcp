using Microsoft.Extensions.Logging;

namespace Embranch.Logging;

/// <summary>
/// Utility class for logging configuration and management
/// </summary>
public static class LoggingUtility
{
    /// <summary>
    /// Determines if logging is enabled based on environment variables or development settings
    /// </summary>
    public static bool IsLoggingEnabled
    {
        get
        {
            // Check if LOG_FILE_NAME is explicitly set
            var logFileName = Environment.GetEnvironmentVariable("LOG_FILE_NAME");
            if (!string.IsNullOrWhiteSpace(logFileName))
            {
                return true;
            }

            // Check if LOG_LEVEL is explicitly set
            var logLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
            if (!string.IsNullOrWhiteSpace(logLevel))
            {
                return true;
            }

            // Check if ENABLE_LOGGING is explicitly set
            var enableLogging = Environment.GetEnvironmentVariable("ENABLE_LOGGING");
            if (!string.IsNullOrWhiteSpace(enableLogging))
            {
                return bool.TryParse(enableLogging, out var enabled) && enabled;
            }

            // Default to disabled for production, enabled for debug builds
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Gets the configured log level from environment variables
    /// </summary>
    public static LogLevel GetConfiguredLogLevel()
    {
        var logLevelEnv = Environment.GetEnvironmentVariable("LOG_LEVEL");
        if (Enum.TryParse<LogLevel>(logLevelEnv, true, out var level))
        {
            return level;
        }

#if DEBUG
        return LogLevel.Debug;
#else
        return LogLevel.Information;
#endif
    }

    /// <summary>
    /// Gets the configured log file name from environment variables
    /// </summary>
    public static string? GetConfiguredLogFileName()
    {
        return Environment.GetEnvironmentVariable("LOG_FILE_NAME");
    }
}