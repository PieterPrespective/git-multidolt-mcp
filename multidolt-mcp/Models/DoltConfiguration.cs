using System.ComponentModel.DataAnnotations;

namespace Embranch.Models
{
    /// <summary>
    /// Configuration settings for Dolt database integration
    /// </summary>
    public class DoltConfiguration
    {
        /// <summary>
        /// Path to the Dolt executable. Defaults to "dolt" which assumes it's in PATH.
        /// On Windows, this might be something like "C:\Program Files\Dolt\bin\dolt.exe"
        /// </summary>
        public string DoltExecutablePath { get; set; } = "dolt";

        /// <summary>
        /// Path to the local Dolt repository directory
        /// </summary>
        [Required]
        public string RepositoryPath { get; set; } = "./data/dolt-repo";

        /// <summary>
        /// Name of the remote repository (typically "origin")
        /// </summary>
        public string RemoteName { get; set; } = "origin";

        /// <summary>
        /// URL of the remote repository (e.g., "dolthub.com/username/repo")
        /// </summary>
        public string? RemoteUrl { get; set; }

        /// <summary>
        /// Timeout in milliseconds for CLI command execution
        /// </summary>
        public int CommandTimeoutMs { get; set; } = 30000; // 30 seconds default

        /// <summary>
        /// Whether to enable debug logging for Dolt commands
        /// </summary>
        public bool EnableDebugLogging { get; set; } = false;

        // ==================== PP13-79: Manifest-Based Initialization ====================

        /// <summary>
        /// PP13-79: Whether to read and use .dmms/state.json manifest for initialization.
        /// When true, Embranch will look for a manifest file and use it to initialize state.
        /// Environment variable: EMBRANCH_USE_MANIFEST
        /// </summary>
        public bool UseManifest { get; set; } = true;
    }
}