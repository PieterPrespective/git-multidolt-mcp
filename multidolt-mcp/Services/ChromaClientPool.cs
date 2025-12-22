using Python.Runtime;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DMMS.Services;

/// <summary>
/// Thread-safe client pool for managing multiple ChromaDB Python client instances
/// Replaces the global static pattern to enable multiple concurrent clients
/// </summary>
internal static class ChromaClientPool
{
    private static dynamic? _chromadbModule;
    private static readonly ConcurrentDictionary<string, ChromaClientInfo> _clients = new();
    private static readonly object _moduleLock = new object();
    private static ILogger? _logger;

    /// <summary>
    /// Information about a ChromaDB client instance
    /// </summary>
    private class ChromaClientInfo
    {
        public dynamic Client { get; set; } = null!;
        public string Configuration { get; set; } = null!;
        public DateTime CreatedAt { get; set; }
        public DateTime LastUsed { get; set; }
        public int UsageCount { get; set; }
        public bool IsDisposed { get; set; }
    }

    /// <summary>
    /// Sets the logger for the client pool
    /// </summary>
    public static void SetLogger(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initializes the ChromaDB module (called once per application)
    /// Must be called from Python thread
    /// </summary>
    public static void InitializeModule()
    {
        lock (_moduleLock)
        {
            if (_chromadbModule == null)
            {
                _chromadbModule = Py.Import("chromadb");
                _logger?.LogInformation("ChromaDB module initialized in client pool");
            }
        }
    }

    /// <summary>
    /// Gets the ChromaDB module reference
    /// Must be called from Python thread
    /// </summary>
    public static dynamic GetChromaDbModule()
    {
        lock (_moduleLock)
        {
            if (_chromadbModule == null)
                throw new InvalidOperationException("ChromaDB module not initialized. Call InitializeModule() first.");
            return _chromadbModule;
        }
    }

    /// <summary>
    /// Creates or gets a ChromaDB client for the specified client ID and configuration
    /// Must be called from Python thread
    /// </summary>
    public static dynamic GetOrCreateClient(string clientId, string configuration)
    {
        if (string.IsNullOrEmpty(clientId))
            throw new ArgumentException("Client ID cannot be null or empty", nameof(clientId));

        if (_clients.TryGetValue(clientId, out var existingInfo))
        {
            if (existingInfo.IsDisposed)
            {
                _logger?.LogWarning("Attempting to use disposed client {ClientId}, creating new one", clientId);
                _clients.TryRemove(clientId, out _);
            }
            else
            {
                // Update usage tracking
                existingInfo.LastUsed = DateTime.UtcNow;
                existingInfo.UsageCount++;
                _logger?.LogDebug("Reusing existing client {ClientId} (usage: {Usage})", clientId, existingInfo.UsageCount);
                return existingInfo.Client;
            }
        }

        // Create new client
        InitializeModule();
        dynamic chromadb = GetChromaDbModule();
        dynamic client;

        try
        {
            // Parse configuration to determine client type
            var config = ParseConfiguration(configuration);
            
            if (!string.IsNullOrEmpty(config.DataPath))
            {
                // PersistentClient
                client = chromadb.PersistentClient(path: config.DataPath);
                _logger?.LogInformation("Created PersistentClient for {ClientId} at path: {Path}", clientId, config.DataPath);
            }
            else if (!string.IsNullOrEmpty(config.Host))
            {
                // HttpClient  
                client = chromadb.HttpClient(host: config.Host, port: config.Port);
                _logger?.LogInformation("Created HttpClient for {ClientId} at {Host}:{Port}", clientId, config.Host, config.Port);
            }
            else
            {
                throw new ArgumentException($"Invalid configuration for client {clientId}: {configuration}");
            }

            var clientInfo = new ChromaClientInfo
            {
                Client = client,
                Configuration = configuration,
                CreatedAt = DateTime.UtcNow,
                LastUsed = DateTime.UtcNow,
                UsageCount = 1,
                IsDisposed = false
            };

            _clients[clientId] = clientInfo;
            _logger?.LogInformation("Created new ChromaDB client {ClientId}", clientId);
            return client;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to create ChromaDB client {ClientId} with config: {Config}", clientId, configuration);
            throw;
        }
    }

    /// <summary>
    /// Gets an existing client by ID
    /// Must be called from Python thread
    /// </summary>
    public static dynamic GetClient(string clientId)
    {
        if (!_clients.TryGetValue(clientId, out var clientInfo) || clientInfo.IsDisposed)
        {
            throw new InvalidOperationException($"ChromaDB client '{clientId}' not found or disposed");
        }

        clientInfo.LastUsed = DateTime.UtcNow;
        clientInfo.UsageCount++;
        return clientInfo.Client;
    }

    /// <summary>
    /// Disposes a specific client
    /// Must be called from Python thread  
    /// </summary>
    public static void DisposeClient(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var clientInfo))
        {
            clientInfo.IsDisposed = true;
            _logger?.LogInformation("Disposed ChromaDB client {ClientId} (usage: {Usage})", clientId, clientInfo.UsageCount);
            
            // Remove from pool after a delay to allow any pending operations to complete
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000); // 5 second grace period
                _clients.TryRemove(clientId, out _);
                _logger?.LogDebug("Removed client {ClientId} from pool", clientId);
            });
        }
    }

    /// <summary>
    /// Disposes a specific client immediately for testing scenarios
    /// This bypasses the 5-second grace period for immediate cleanup
    /// Must be called from Python thread  
    /// </summary>
    public static void DisposeClientImmediately(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var clientInfo))
        {
            clientInfo.IsDisposed = true;
            _logger?.LogInformation("Disposed ChromaDB client {ClientId} immediately (usage: {Usage})", clientId, clientInfo.UsageCount);
            
            // Force Python garbage collection to release file handles
            try
            {
                using (Py.GIL())
                {
                    using (dynamic gc = Py.Import("gc"))
                    {
                        gc.collect();
                        _logger?.LogDebug("Forced Python garbage collection for client {ClientId}", clientId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to force Python garbage collection for client {ClientId}", clientId);
            }
            
            // Remove immediately
            _clients.TryRemove(clientId, out _);
            _logger?.LogDebug("Removed client {ClientId} from pool immediately", clientId);
        }
    }

    /// <summary>
    /// Gets information about all active clients
    /// </summary>
    public static Dictionary<string, object> GetPoolStatus()
    {
        var status = new Dictionary<string, object>
        {
            ["TotalClients"] = _clients.Count,
            ["ActiveClients"] = _clients.Count(kvp => !kvp.Value.IsDisposed),
            ["ModuleInitialized"] = _chromadbModule != null
        };

        var clients = new List<object>();
        foreach (var kvp in _clients)
        {
            if (!kvp.Value.IsDisposed)
            {
                clients.Add(new
                {
                    ClientId = kvp.Key,
                    Configuration = kvp.Value.Configuration,
                    CreatedAt = kvp.Value.CreatedAt,
                    LastUsed = kvp.Value.LastUsed,
                    UsageCount = kvp.Value.UsageCount
                });
            }
        }
        status["Clients"] = clients;

        return status;
    }

    /// <summary>
    /// Clears all clients (for cleanup)
    /// </summary>
    public static void ClearAll()
    {
        _logger?.LogInformation("Clearing all ChromaDB clients from pool");
        foreach (var kvp in _clients)
        {
            kvp.Value.IsDisposed = true;
        }
        _clients.Clear();
    }

    /// <summary>
    /// Parses configuration string to extract client parameters
    /// </summary>
    private static (string? DataPath, string? Host, int Port) ParseConfiguration(string configuration)
    {
        // Simple configuration parsing - can be enhanced for more complex scenarios
        // Format examples: 
        // - "persistent:C:\path\to\data"
        // - "http:localhost:8000"
        
        var parts = configuration.Split(':');
        if (parts.Length < 2)
            throw new ArgumentException($"Invalid configuration format: {configuration}");

        var type = parts[0].ToLowerInvariant();
        if (type == "persistent")
        {
            return (DataPath: string.Join(":", parts.Skip(1)), Host: null, Port: 0);
        }
        else if (type == "http")
        {
            var host = parts[1];
            var port = parts.Length > 2 ? int.Parse(parts[2]) : 8000;
            return (DataPath: null, Host: host, Port: port);
        }
        else
        {
            throw new ArgumentException($"Unknown configuration type: {type}");
        }
    }
}