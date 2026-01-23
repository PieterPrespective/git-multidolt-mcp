using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Models;
using DMMS.Services;
using DMMS.Utilities;

namespace DMMS.Tools;

/// <summary>
/// PP13-81: MCP tool to update the remote URL in the DMMS manifest.
/// Allows configuration of remote repository without requiring restart.
/// This enables recovery from the empty repository initialization scenario.
/// </summary>
[McpServerToolType]
public class ManifestSetRemoteTool
{
    private readonly ILogger<ManifestSetRemoteTool> _logger;
    private readonly IDmmsStateManifest _manifestService;
    private readonly ISyncStateChecker _syncStateChecker;
    private readonly IGitIntegration _gitIntegration;

    /// <summary>
    /// Initializes a new instance of the ManifestSetRemoteTool class
    /// </summary>
    public ManifestSetRemoteTool(
        ILogger<ManifestSetRemoteTool> logger,
        IDmmsStateManifest manifestService,
        ISyncStateChecker syncStateChecker,
        IGitIntegration gitIntegration)
    {
        _logger = logger;
        _manifestService = manifestService;
        _syncStateChecker = syncStateChecker;
        _gitIntegration = gitIntegration;
    }

    /// <summary>
    /// Update the remote URL in the DMMS manifest. After setting, use DoltClone to clone from the remote.
    /// This tool enables recovery when DMMS started without a configured remote URL.
    /// </summary>
    [McpServerTool]
    [Description("Update the remote URL in the DMMS manifest. After setting, use DoltClone (with force=true if needed) to clone from the remote. This enables configuration of remote repository after initial startup.")]
    public virtual async Task<object> ManifestSetRemote(
        string remote_url,
        string? default_branch = null,
        string? project_root = null)
    {
        const string toolName = nameof(ManifestSetRemoteTool);
        const string methodName = nameof(ManifestSetRemote);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"remote_url: {remote_url}, default_branch: {default_branch}");

        try
        {
            // Validate remote URL
            if (string.IsNullOrWhiteSpace(remote_url))
            {
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, "Remote URL is required");
                return new
                {
                    success = false,
                    error = "REMOTE_URL_REQUIRED",
                    message = "Remote URL is required"
                };
            }

            // Determine project root
            string resolvedProjectRoot;
            if (!string.IsNullOrEmpty(project_root))
            {
                resolvedProjectRoot = project_root;
            }
            else
            {
                // Try to get from sync state checker first
                var checkerRoot = await _syncStateChecker.GetProjectRootAsync();
                if (!string.IsNullOrEmpty(checkerRoot))
                {
                    resolvedProjectRoot = checkerRoot;
                }
                else
                {
                    // Fall back to Git root detection
                    var gitRoot = await _gitIntegration.GetGitRootAsync(Directory.GetCurrentDirectory());
                    resolvedProjectRoot = gitRoot ?? Directory.GetCurrentDirectory();
                }
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Using project root: {resolvedProjectRoot}");

            // Read existing manifest
            var existingManifest = await _manifestService.ReadManifestAsync(resolvedProjectRoot);
            var previousRemoteUrl = existingManifest?.Dolt.RemoteUrl;

            DmmsManifest updatedManifest;

            if (existingManifest != null)
            {
                // Update existing manifest
                ToolLoggingUtility.LogToolInfo(_logger, toolName,
                    $"Updating existing manifest. Previous remote: {previousRemoteUrl ?? "(none)"}");

                var updatedDolt = existingManifest.Dolt with
                {
                    RemoteUrl = remote_url,
                    DefaultBranch = default_branch ?? existingManifest.Dolt.DefaultBranch
                };

                updatedManifest = existingManifest with
                {
                    Dolt = updatedDolt,
                    UpdatedAt = DateTime.UtcNow,
                    UpdatedBy = "ManifestSetRemoteTool"
                };
            }
            else
            {
                // Create new manifest with remote URL
                ToolLoggingUtility.LogToolInfo(_logger, toolName, "No manifest found, creating new manifest with remote URL");

                updatedManifest = _manifestService.CreateDefaultManifest(
                    remoteUrl: remote_url,
                    defaultBranch: default_branch ?? "main",
                    initMode: "auto"
                );

                // Set the updated_by field
                updatedManifest = updatedManifest with
                {
                    UpdatedBy = "ManifestSetRemoteTool"
                };
            }

            // Write updated manifest
            await _manifestService.WriteManifestAsync(resolvedProjectRoot, updatedManifest);

            // Invalidate sync state cache so next check reflects the new remote URL
            _syncStateChecker.InvalidateCache();

            var manifestPath = _manifestService.GetManifestPath(resolvedProjectRoot);

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName,
                $"Remote URL set to: {remote_url}");

            return new
            {
                success = true,
                message = $"Remote URL updated to: {remote_url}",
                manifest = new
                {
                    path = manifestPath,
                    remote_url = remote_url,
                    default_branch = updatedManifest.Dolt.DefaultBranch,
                    previous_remote_url = previousRemoteUrl,
                    updated_at = updatedManifest.UpdatedAt.ToString("O")
                },
                next_steps = new[]
                {
                    "Use DoltClone to clone from the configured remote",
                    "If a local repository already exists, use DoltClone with force=true to overwrite it"
                }
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to update manifest: {ex.Message}"
            };
        }
    }
}
