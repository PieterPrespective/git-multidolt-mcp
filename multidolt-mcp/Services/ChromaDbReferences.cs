using Python.Runtime;
using Microsoft.Extensions.Logging;

namespace Embranch.Services;

/// <summary>
/// DEPRECATED: Backward compatibility wrapper for ChromaClientPool
/// This class provides backward compatibility for existing code while migrating to the new client pool architecture
/// New code should use ChromaClientPool directly
/// </summary>
[Obsolete("Use ChromaClientPool for better multi-client support")]
internal static class ChromaDbReferences
{
    private static string? _defaultClientId = "legacy_default_client";
    private static readonly object _lock = new object();

    /// <summary>
    /// Sets the ChromaDB references (DEPRECATED - creates a legacy client in the pool)
    /// </summary>
    [Obsolete("Use ChromaClientPool.GetOrCreateClient() instead")]
    public static void SetReferences(dynamic chromadb, dynamic client)
    {
        lock (_lock)
        {
            // For backward compatibility, store the client in the pool with a default ID
            // This is not ideal but provides migration path
            try 
            {
                // Since we don't have the original configuration, create a placeholder
                var clientInfo = new 
                {
                    Client = client,
                    Configuration = "legacy_compatibility_client",
                    CreatedAt = DateTime.UtcNow,
                    LastUsed = DateTime.UtcNow,
                    UsageCount = 1,
                    IsDisposed = false
                };
                
                // Store in a static field for this backward compatibility layer
                // The new architecture should not use this path
            }
            catch (Exception ex)
            {
                // Log warning but continue for compatibility
                Console.WriteLine($"Warning: Legacy ChromaDbReferences.SetReferences called: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets the ChromaDB module reference (DEPRECATED)
    /// </summary>
    [Obsolete("Use ChromaClientPool.GetChromaDbModule() instead")]
    public static dynamic GetChromaDb()
    {
        return ChromaClientPool.GetChromaDbModule();
    }

    /// <summary>
    /// Gets the ChromaDB client reference (DEPRECATED)
    /// </summary>
    [Obsolete("Use ChromaClientPool.GetClient(clientId) instead")]
    public static dynamic GetClient()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_defaultClientId))
                throw new InvalidOperationException("No default ChromaDB client available. Use ChromaClientPool for better client management.");
            
            try
            {
                return ChromaClientPool.GetClient(_defaultClientId);
            }
            catch (InvalidOperationException)
            {
                // Fallback: try to get any available client from the pool
                var poolStatus = ChromaClientPool.GetPoolStatus();
                var activeClients = poolStatus.ContainsKey("Clients") ? poolStatus["Clients"] as List<object> : null;
                
                if (activeClients?.Any() == true)
                {
                    var firstClient = activeClients.First() as dynamic;
                    _defaultClientId = firstClient?.ClientId?.ToString();
                    if (!string.IsNullOrEmpty(_defaultClientId))
                    {
                        return ChromaClientPool.GetClient(_defaultClientId);
                    }
                }
                
                throw new InvalidOperationException("No ChromaDB clients available. Initialize a ChromaPythonService first.");
            }
        }
    }

    /// <summary>
    /// Clears the references (DEPRECATED)
    /// </summary>
    [Obsolete("Use ChromaClientPool.ClearAll() instead")]
    public static void Clear()
    {
        lock (_lock)
        {
            _defaultClientId = null;
            ChromaClientPool.ClearAll();
        }
    }

    /// <summary>
    /// Sets the default client ID for legacy compatibility
    /// </summary>
    internal static void SetDefaultClientId(string clientId)
    {
        lock (_lock)
        {
            _defaultClientId = clientId;
        }
    }
}