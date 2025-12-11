using Python.Runtime;

namespace DMMS.Services;

/// <summary>
/// Thread-safe storage for ChromaDB Python object references
/// These must be accessed only from the Python thread via PythonContext
/// </summary>
internal static class ChromaDbReferences
{
    private static dynamic? _chromadb;
    private static dynamic? _client;
    private static readonly object _lock = new object();

    /// <summary>
    /// Sets the ChromaDB references (must be called from Python thread)
    /// </summary>
    public static void SetReferences(dynamic chromadb, dynamic client)
    {
        lock (_lock)
        {
            _chromadb = chromadb;
            _client = client;
        }
    }

    /// <summary>
    /// Gets the ChromaDB module reference (must be called from Python thread)
    /// </summary>
    public static dynamic GetChromaDb()
    {
        lock (_lock)
        {
            if (_chromadb == null)
                throw new InvalidOperationException("ChromaDB module not initialized");
            return _chromadb;
        }
    }

    /// <summary>
    /// Gets the ChromaDB client reference (must be called from Python thread)
    /// </summary>
    public static dynamic GetClient()
    {
        lock (_lock)
        {
            if (_client == null)
                throw new InvalidOperationException("ChromaDB client not initialized");
            return _client;
        }
    }

    /// <summary>
    /// Clears the references (for cleanup)
    /// </summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _chromadb = null;
            _client = null;
        }
    }
}