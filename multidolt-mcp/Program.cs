using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using DMMS.Logging;
using DMMS.Models;
using DMMS.Services;
using DMMS.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
bool enableLogging = LoggingUtility.IsLoggingEnabled;

if (enableLogging)
{
    var logFileName = Environment.GetEnvironmentVariable("LOG_FILE_NAME");
    var logLevel = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("LOG_LEVEL"), out var level) 
        ? level 
        : LogLevel.Information;
    
    builder.Logging.AddFileLogging(logFileName, logLevel);
    builder.Logging.SetMinimumLevel(logLevel);
}

builder.Services.Configure<ServerConfiguration>(options => ConfigurationUtility.GetServerConfiguration(options));

// Register both implementations
builder.Services.AddSingleton<ChromaDbService>();

// Register the appropriate service based on configuration
builder.Services.AddSingleton<IChromaDbService>(serviceProvider =>
    ChromaDbServiceFactory.CreateService(serviceProvider));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<GetServerVersionTool>()
    .WithTools<ChromaListCollectionsTool>()
    .WithTools<ChromaCreateCollectionTool>()
    .WithTools<ChromaDeleteCollectionTool>()
    .WithTools<ChromaAddDocumentsTool>()
    .WithTools<ChromaQueryDocumentsTool>()
    .WithTools<ChromaGetCollectionCountTool>()
    .WithTools<ChromaDeleteDocumentsTool>();

var host = builder.Build();

if (enableLogging)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    
    var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    
    logger.LogInformation("DMMS (Dolt Multi-Database MCP Server) v{Version} starting up", version);
    logger.LogInformation("This server provides MCP access to multiple Dolt databases via terminal commands");
}

await host.RunAsync();

/// <summary>
/// Utility class for managing server configuration settings
/// </summary>
public static class ConfigurationUtility
{
    /// <summary>
    /// Populates the server configuration from environment variables
    /// </summary>
    /// <param name="options">The server configuration to populate</param>
    /// <returns>The populated server configuration</returns>
    public static ServerConfiguration GetServerConfiguration(ServerConfiguration options)
    {
        options.McpPort = int.TryParse(Environment.GetEnvironmentVariable("MCP_PORT"), out var mcpPort) ? mcpPort : 6500;
        options.ConnectionTimeoutSeconds = double.TryParse(Environment.GetEnvironmentVariable("CONNECTION_TIMEOUT"), out var timeout) ? timeout : 86400.0;
        options.BufferSize = int.TryParse(Environment.GetEnvironmentVariable("BUFFER_SIZE"), out var bufferSize) ? bufferSize : 16 * 1024 * 1024;
        options.MaxRetries = int.TryParse(Environment.GetEnvironmentVariable("MAX_RETRIES"), out var retries) ? retries : 3;
        options.RetryDelaySeconds = double.TryParse(Environment.GetEnvironmentVariable("RETRY_DELAY"), out var delay) ? delay : 1.0;
        options.ChromaHost = Environment.GetEnvironmentVariable("CHROMA_HOST") ?? "localhost";
        options.ChromaPort = int.TryParse(Environment.GetEnvironmentVariable("CHROMA_PORT"), out var chromaPort) ? chromaPort : 8000;
        options.ChromaMode = Environment.GetEnvironmentVariable("CHROMA_MODE") ?? "persistent";
        options.ChromaDataPath = Environment.GetEnvironmentVariable("CHROMA_DATA_PATH") ?? "./chroma_data";
        
        return options;
    }
}

/// <summary>
/// Utility class for managing logging configuration
/// </summary>
public static class LoggingUtility
{
    /// <summary>
    /// Default logging setting - can be overridden by ENABLE_LOGGING environment variable
    /// </summary>
    public const bool EnableLogging = false;
    
    /// <summary>
    /// Determines if logging is enabled based on environment variable or default setting
    /// </summary>
    public static bool IsLoggingEnabled => bool.TryParse(Environment.GetEnvironmentVariable("ENABLE_LOGGING"), out var envLogging) 
        ? envLogging 
        : EnableLogging;
}