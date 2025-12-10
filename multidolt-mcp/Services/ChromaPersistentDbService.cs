using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;

namespace DMMS.Services;

/// <summary>
/// Persistent ChromaDB service using Python.NET with chromadb PersistentClient
/// This implementation uses the official ChromaDB Python library for robust functionality
/// </summary>
public class ChromaPersistentDbService : ChromaPythonService
{
    /// <summary>
    /// Initializes a new instance of ChromaPersistentDbService
    /// </summary>
    public ChromaPersistentDbService(ILogger<ChromaPersistentDbService> logger, IOptions<ServerConfiguration> configuration)
        : base(logger, configuration)
    {
        // Ensure ChromaDataPath is set for persistent storage
        if (string.IsNullOrEmpty(configuration.Value.ChromaDataPath))
        {
            throw new InvalidOperationException("ChromaDataPath must be configured for persistent storage");
        }
    }

}