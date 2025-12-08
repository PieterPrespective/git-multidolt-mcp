namespace DMMS.Models;

/// <summary>
/// Configuration settings for the DMMS MCP server
/// </summary>
public class ServerConfiguration
{
    /// <summary>
    /// Port for the MCP server to listen on
    /// </summary>
    public int McpPort { get; set; } = 6500;

    /// <summary>
    /// Connection timeout in seconds
    /// </summary>
    public double ConnectionTimeoutSeconds { get; set; } = 86400.0;

    /// <summary>
    /// Buffer size for data transfers
    /// </summary>
    public int BufferSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Maximum number of retry attempts for operations
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Delay in seconds between retry attempts
    /// </summary>
    public double RetryDelaySeconds { get; set; } = 1.0;

    /// <summary>
    /// Host address for the Chroma database (used in server mode)
    /// </summary>
    public string ChromaHost { get; set; } = "localhost";

    /// <summary>
    /// Port for the Chroma database (used in server mode)
    /// </summary>
    public int ChromaPort { get; set; } = 8000;

    /// <summary>
    /// ChromaDB connection mode: "persistent" for local data directory, "server" for HTTP API
    /// </summary>
    public string ChromaMode { get; set; } = "persistent";

    /// <summary>
    /// Local data directory path for persistent ChromaDB client
    /// </summary>
    public string ChromaDataPath { get; set; } = "./chroma_data";
}