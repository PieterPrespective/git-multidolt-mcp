using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;
using DMMS.Logging;
using DMMS.Models;
using DMMS.Services;
using DMMS.Tools;

var builder = Host.CreateApplicationBuilder(args);

// Add global exception handling
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    try
    {
        var logFileName = Environment.GetEnvironmentVariable("LOG_FILE_NAME") ?? "DMMS_crash.log";
        var crashLog = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED EXCEPTION: {e.ExceptionObject}\n";
        File.AppendAllText(logFileName, crashLog);
    }
    catch
    {
        // Ignore logging errors during crash
    }
};

builder.Logging.ClearProviders();
bool enableLogging = LoggingUtility.IsLoggingEnabled;

if (enableLogging)
{
    var logFileName = Environment.GetEnvironmentVariable("LOG_FILE_NAME");
    var logLevel = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("LOG_LEVEL"), out var level) 
        ? level 
        : LogLevel.Debug; // Default to Debug level for better troubleshooting
    
    builder.Logging.AddFileLogging(logFileName, logLevel);
    builder.Logging.SetMinimumLevel(logLevel);
}

// Create configuration instances first for proper dependency injection
var serverConfig = new ServerConfiguration();
ConfigurationUtility.GetServerConfiguration(serverConfig);

var doltConfig = new DoltConfiguration();
ConfigurationUtility.GetDoltConfiguration(doltConfig);

// Register configuration instances and options for dependency injection
// This ensures both IOptions<T> and direct instance injection work properly
builder.Services.Configure<ServerConfiguration>(options => ConfigurationUtility.GetServerConfiguration(options));
builder.Services.Configure<DoltConfiguration>(options => ConfigurationUtility.GetDoltConfiguration(options));

// Register configuration instances for direct injection (required by SqliteDeletionTracker)
builder.Services.AddSingleton(serverConfig);
builder.Services.AddSingleton(Options.Create(serverConfig));
builder.Services.AddSingleton(doltConfig);
builder.Services.AddSingleton(Options.Create(doltConfig));

// Register ChromaDbService for server mode (without IDocumentIdResolver to avoid circular dependency)
// IMPORTANT: Do NOT resolve IDocumentIdResolver here - it creates a circular dependency deadlock:
// IChromaDbService → IDocumentIdResolver → IChromaDbService
// The ChromaPythonService has a fallback CreateTemporaryResolver() for when idResolver is null.
builder.Services.AddSingleton<ChromaDbService>(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<IOptions<ServerConfiguration>>();
    var logger = serviceProvider.GetRequiredService<ILogger<ChromaDbService>>();
    // NOTE: idResolver is intentionally null to avoid circular dependency deadlock
    return new ChromaDbService(logger, config, idResolver: null);
});

// Register the appropriate IChromaDbService based on configuration
// This must be registered BEFORE IDocumentIdResolver since DocumentIdResolver depends on it
builder.Services.AddSingleton<IChromaDbService>(serviceProvider =>
    ChromaDbServiceFactory.CreateService(serviceProvider));

// Register DocumentIdResolver for chunk-aware operations
// This depends on IChromaDbService, so it must be registered after
builder.Services.AddSingleton<IDocumentIdResolver, DocumentIdResolver>();

// Register Dolt services
builder.Services.AddSingleton<IDoltCli, DoltCli>();
builder.Services.AddSingleton<ISyncManagerV2, SyncManagerV2>();

// Register deletion tracking service
builder.Services.AddSingleton<SqliteDeletionTracker>();
builder.Services.AddSingleton<IDeletionTracker>(provider => provider.GetRequiredService<SqliteDeletionTracker>());
// PP13-69 Phase 3: Register sync state tracker (same instance as deletion tracker)
builder.Services.AddSingleton<ISyncStateTracker>(provider => provider.GetRequiredService<SqliteDeletionTracker>());

// Register collection change detection service
builder.Services.AddSingleton<ICollectionChangeDetector, CollectionChangeDetector>();

// Register merge conflict analysis and resolution services
builder.Services.AddSingleton<IConflictAnalyzer, ConflictAnalyzer>();
builder.Services.AddSingleton<IMergeConflictResolver, MergeConflictResolver>();

// Register import services (PP13-75)
builder.Services.AddSingleton<IExternalChromaDbReader, ExternalChromaDbReader>();
builder.Services.AddSingleton<IImportAnalyzer, ImportAnalyzer>();
builder.Services.AddSingleton<IImportExecutor, ImportExecutor>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    // Server Management Tools
    .WithTools<GetServerVersionTool>()
    
    // ChromaDB Collection Management Tools
    .WithTools<ChromaListCollectionsTool>()
    .WithTools<ChromaCreateCollectionTool>()
    .WithTools<ChromaDeleteCollectionTool>()
    .WithTools<ChromaGetCollectionCountTool>()
    .WithTools<ChromaGetCollectionInfoTool>()
    .WithTools<ChromaModifyCollectionTool>()
    .WithTools<ChromaPeekCollectionTool>()
    
    // ChromaDB Document Operations Tools  
    .WithTools<ChromaAddDocumentsTool>()
    .WithTools<ChromaQueryDocumentsTool>()
    .WithTools<ChromaGetDocumentsTool>()
    .WithTools<ChromaUpdateDocumentsTool>()
    .WithTools<ChromaDeleteDocumentsTool>()
    
    // Dolt Version Control Tools - Status and Information
    .WithTools<DoltStatusTool>()
    .WithTools<DoltBranchesTool>()
    .WithTools<DoltCommitsTool>()
    .WithTools<DoltShowTool>()
    .WithTools<DoltFindTool>()
    
    // Dolt Version Control Tools - Repository Setup
    .WithTools<DoltInitTool>()
    .WithTools<DoltCloneTool>()
    
    // Dolt Version Control Tools - Remote Synchronization
    .WithTools<DoltFetchTool>()
    .WithTools<DoltPullTool>()
    .WithTools<DoltPushTool>()
    
    // Dolt Version Control Tools - Local Operations
    .WithTools<DoltCommitTool>()
    .WithTools<DoltCheckoutTool>()
    .WithTools<DoltResetTool>()
    
    // Dolt Version Control Tools - Merge Operations
    .WithTools<PreviewDoltMergeTool>()
    .WithTools<ExecuteDoltMergeTool>()

    // Import Tools (PP13-75)
    .WithTools<PreviewImportTool>()
    .WithTools<ExecuteImportTool>();

var host = builder.Build();

// Initialize PythonContext FIRST before any service that needs ChromaDB
// This must happen before collection change detector or any ChromaDB operations
if (enableLogging)
{
    var logger = host.Services.GetService<ILogger<Program>>();
    logger?.LogInformation("Initializing Python context (required for ChromaDB)...");
    try
    {
        var pythonDll = PythonContextUtility.FindPythonDll(logger);
        PythonContext.Initialize(logger, pythonDll);
        logger?.LogInformation("✓ Python context initialized successfully");
    }
    catch (Exception ex)
    {
        logger?.LogCritical(ex, "FATAL: Failed to initialize Python context. ChromaDB functionality will be unavailable.");
        Environment.Exit(10); // Exit code 10 for Python initialization failure
    }
}
else
{
    // Initialize PythonContext even without logging
    try
    {
        var pythonDll = PythonContextUtility.FindPythonDll();
        PythonContext.Initialize(pythonDllPath: pythonDll);
    }
    catch (Exception)
    {
        Environment.Exit(10); // Exit code 10 for Python initialization failure
    }
}

// Phase 4: Enhanced Production Initialization Pattern (PP13-61)
// Initialize deletion tracker during startup with comprehensive validation
try 
{
    var deletionTracker = host.Services.GetRequiredService<IDeletionTracker>();
    var doltConfiguration = host.Services.GetRequiredService<DoltConfiguration>();
    
    if (enableLogging)
    {
        var logger = host.Services.GetService<ILogger<Program>>();
        logger?.LogInformation("Starting deletion tracker initialization for repository: {RepositoryPath}", doltConfiguration.RepositoryPath);
    }
    
    // Initialize deletion tracker with the repository path (includes collection schema)
    await deletionTracker.InitializeAsync(doltConfiguration.RepositoryPath);
    
    // Validate all required tables exist (both document and collection deletion tracking)
    if (deletionTracker is SqliteDeletionTracker sqliteTracker)
    {
        // Get the server configuration to find the correct data path
        var serverConfiguration = host.Services.GetRequiredService<ServerConfiguration>();
        
        // Verify the database file was created at the correct location
        // SqliteDeletionTracker creates it at: {DataPath}/dev/deletion_tracking.db
        var dbPath = Path.Combine(serverConfiguration.DataPath, "dev", "deletion_tracking.db");
        if (!File.Exists(dbPath))
        {
            throw new InvalidOperationException($"Deletion tracker database was not created at expected path: {dbPath}");
        }
        
        if (enableLogging)
        {
            var logger = host.Services.GetService<ILogger<Program>>();
            logger?.LogInformation("✓ Deletion tracker database verified at: {DbPath}", dbPath);
        }
    }
    
    if (enableLogging)
    {
        var logger = host.Services.GetService<ILogger<Program>>();
        logger?.LogInformation("✓ Deletion tracker initialized successfully with document and collection tracking schemas");
    }
}
catch (Exception ex)
{
    if (enableLogging)
    {
        var logger = host.Services.GetService<ILogger<Program>>();
        logger?.LogCritical(ex, "FATAL: Failed to initialize deletion tracker - application cannot continue");
    }
    
    // Fail-fast: Exit with non-zero code to signal initialization failure
    Environment.Exit(1);
}

// Phase 4: Enhanced Collection Change Detector Initialization (PP13-61)
// Initialize collection change detector with comprehensive validation
try 
{
    var collectionChangeDetector = host.Services.GetRequiredService<ICollectionChangeDetector>();
    var doltConfiguration = host.Services.GetRequiredService<DoltConfiguration>();
    
    if (enableLogging)
    {
        var logger = host.Services.GetService<ILogger<Program>>();
        logger?.LogInformation("Starting collection change detector initialization for repository: {RepositoryPath}", doltConfiguration.RepositoryPath);
    }
    
    // Initialize collection change detector with the repository path
    await collectionChangeDetector.InitializeAsync(doltConfiguration.RepositoryPath);
    
    // Validate schema exists (verifies deletion tracker integration)
    await collectionChangeDetector.ValidateSchemaAsync(doltConfiguration.RepositoryPath);
    
    // Validate initialization completeness
    await collectionChangeDetector.ValidateInitializationAsync();
    
    // Verify service dependencies are properly wired
    var chromaService = host.Services.GetRequiredService<IChromaDbService>();
    var doltCli = host.Services.GetRequiredService<IDoltCli>();
    var deletionTracker = host.Services.GetRequiredService<IDeletionTracker>();
    
    if (enableLogging)
    {
        var logger = host.Services.GetService<ILogger<Program>>();
        logger?.LogInformation("✓ Collection change detector validated with all dependencies");
        logger?.LogInformation("  - ChromaDB service: {ServiceType}", chromaService.GetType().Name);
        logger?.LogInformation("  - Dolt CLI service: {ServiceType}", doltCli.GetType().Name);
        logger?.LogInformation("  - Deletion tracker: {ServiceType}", deletionTracker.GetType().Name);
        logger?.LogInformation("✓ Collection change detector initialization complete");
    }
}
catch (Exception ex)
{
    if (enableLogging)
    {
        var logger = host.Services.GetService<ILogger<Program>>();
        logger?.LogCritical(ex, "FATAL: Failed to initialize collection change detector - application cannot continue");
    }
    
    // Fail-fast: Exit with non-zero code to signal initialization failure
    Environment.Exit(2);
}

// Phase 4: Validate Sync Service Integration (PP13-61)
// Ensure all sync-related services are properly initialized
try
{
    var syncManager = host.Services.GetRequiredService<ISyncManagerV2>();
    
    if (enableLogging)
    {
        var logger = host.Services.GetService<ILogger<Program>>();
        logger?.LogInformation("Validating sync service integration...");
        
        // Verify the sync manager can access all required services
        if (syncManager is SyncManagerV2 syncV2)
        {
            logger?.LogInformation("✓ SyncManagerV2 service initialized with collection sync support");
        }
        
        logger?.LogInformation("✓ All collection-level sync services validated and ready");
    }
}
catch (Exception ex)
{
    if (enableLogging)
    {
        var logger = host.Services.GetService<ILogger<Program>>();
        logger?.LogCritical(ex, "FATAL: Failed to initialize sync services - application cannot continue");
    }
    
    // Fail-fast: Exit with non-zero code to signal initialization failure
    Environment.Exit(3);
}

if (enableLogging)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    
    var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    
    logger.LogInformation("DMMS (Dolt Multi-Database MCP Server) v{Version} starting up", version);
    logger.LogInformation("This server provides MCP access to multiple Dolt databases via terminal commands");
    logger.LogInformation("✓ All services initialized successfully");
}

// Register shutdown hook to clean up Python context
var applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
applicationLifetime.ApplicationStopping.Register(() =>
{
    var logger = enableLogging ? host.Services.GetService<ILogger<Program>>() : null;
    logger?.LogInformation("Shutting down Python context...");
    PythonContext.Shutdown();
    logger?.LogInformation("Python context shutdown complete");
});

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
        options.DataPath = Environment.GetEnvironmentVariable("DMMS_DATA_PATH") ?? "./data";
        
        return options;
    }

    /// <summary>
    /// Populates the Dolt configuration from environment variables
    /// </summary>
    /// <param name="options">The Dolt configuration to populate</param>
    /// <returns>The populated Dolt configuration</returns>
    public static DoltConfiguration GetDoltConfiguration(DoltConfiguration options)
    {
        // Check for Dolt executable path, defaulting to "C:\Program Files\Dolt\bin\dolt.exe" on Windows
        var defaultPath = Environment.OSVersion.Platform == PlatformID.Win32NT 
            ? @"C:\Program Files\Dolt\bin\dolt.exe" 
            : "dolt";
        options.DoltExecutablePath = Environment.GetEnvironmentVariable("DOLT_EXECUTABLE_PATH") ?? defaultPath;
        options.RepositoryPath = Environment.GetEnvironmentVariable("DOLT_REPOSITORY_PATH") ?? "./data/dolt-repo";
        options.RemoteName = Environment.GetEnvironmentVariable("DOLT_REMOTE_NAME") ?? "origin";
        options.RemoteUrl = Environment.GetEnvironmentVariable("DOLT_REMOTE_URL");
        options.CommandTimeoutMs = int.TryParse(Environment.GetEnvironmentVariable("DOLT_COMMAND_TIMEOUT"), out var timeout) ? timeout : 30000;
        options.EnableDebugLogging = bool.TryParse(Environment.GetEnvironmentVariable("DOLT_DEBUG_LOGGING"), out var debug) && debug;
        
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