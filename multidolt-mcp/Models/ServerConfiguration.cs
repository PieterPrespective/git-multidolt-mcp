namespace Embranch.Models;

/// <summary>
/// Configuration settings for the Embranch MCP server
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

    /// <summary>
    /// General data directory path for Embranch data files (deletion tracking, etc.)
    /// </summary>
    public string DataPath { get; set; } = "./data";

    // ==================== PP13-79: Project Root Detection ====================

    /// <summary>
    /// PP13-79: Explicit project root path.
    /// If specified, this path is used instead of auto-detection.
    /// Environment variable: EMBRANCH_PROJECT_ROOT
    /// </summary>
    public string? ProjectRoot { get; set; }

    /// <summary>
    /// PP13-79: Whether to automatically detect the project root from Git repository.
    /// When true, Embranch will search up the directory tree for a Git repository root.
    /// Environment variable: EMBRANCH_AUTO_DETECT_PROJECT_ROOT
    /// </summary>
    public bool AutoDetectProjectRoot { get; set; } = true;

    /// <summary>
    /// PP13-79: Embranch initialization mode on startup.
    /// Values: auto, prompt, manual, disabled
    /// - auto: Automatically sync on startup if manifest differs from local state
    /// - prompt: Ask user before syncing on startup (not applicable in MCP context)
    /// - manual: Only sync when explicitly requested via tools
    /// - disabled: Never auto-sync; use local state only
    /// Environment variable: EMBRANCH_INIT_MODE
    /// </summary>
    public string InitMode { get; set; } = "auto";
}