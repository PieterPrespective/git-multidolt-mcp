using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Embranch.Models;

namespace Embranch.Services;

/// <summary>
/// ChromaDB service using Python.NET with chromadb HttpClient
/// This implementation uses the official ChromaDB Python library for robust functionality
/// </summary>
public class ChromaDbService : ChromaPythonService
{
    /// <summary>
    /// Initializes a new instance of the ChromaDbService
    /// </summary>
    public ChromaDbService(ILogger<ChromaDbService> logger, IOptions<ServerConfiguration> configuration, IDocumentIdResolver? idResolver = null)
        : base(logger, configuration, idResolver)
    {
        // Base class will automatically use HttpClient when ChromaDataPath is empty
        // and PersistentClient when ChromaDataPath is set
    }
}