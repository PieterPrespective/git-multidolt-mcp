using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Models;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// PP13-79: MCP tool to update the Embranch manifest file (.dmms/state.json)
/// Updates the manifest with current Dolt state and optionally Git mapping
/// </summary>
[McpServerToolType]
public class UpdateManifestTool
{
    private readonly ILogger<UpdateManifestTool> _logger;
    private readonly IEmbranchStateManifest _manifestService;
    private readonly IDoltCli _doltCli;
    private readonly IGitIntegration _gitIntegration;

    public UpdateManifestTool(
        ILogger<UpdateManifestTool> logger,
        IEmbranchStateManifest manifestService,
        IDoltCli doltCli,
        IGitIntegration gitIntegration)
    {
        _logger = logger;
        _manifestService = manifestService;
        _doltCli = doltCli;
        _gitIntegration = gitIntegration;
    }

    /// <summary>
    /// Update the Embranch manifest (.dmms/state.json) with the current Dolt state.
    /// This records the current Dolt commit and branch in the manifest, and optionally
    /// records the Git-Dolt commit mapping for precise state reconstruction.
    /// </summary>
    [McpServerTool]
    [Description("Update the Embranch manifest (.dmms/state.json) with the current Dolt state. This records the current Dolt commit and branch, and optionally records the Git-Dolt commit mapping.")]
    public virtual async Task<object> UpdateManifest(
        bool? include_git_mapping = true,
        string? note = null,
        string? project_root = null)
    {
        const string toolName = nameof(UpdateManifestTool);
        const string methodName = nameof(UpdateManifest);

        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName,
                $"IncludeGitMapping: {include_git_mapping}, Note: {note ?? "none"}");

            // Determine project root
            string resolvedProjectRoot;
            if (!string.IsNullOrEmpty(project_root))
            {
                resolvedProjectRoot = project_root;
            }
            else
            {
                var gitRoot = await _gitIntegration.GetGitRootAsync(Directory.GetCurrentDirectory());
                resolvedProjectRoot = gitRoot ?? Directory.GetCurrentDirectory();
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Using project root: {resolvedProjectRoot}");

            // Check if manifest exists
            var existingManifest = await _manifestService.ReadManifestAsync(resolvedProjectRoot);
            if (existingManifest == null)
            {
                var error = "No manifest found. Use init_manifest to create one first.";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = "MANIFEST_NOT_FOUND",
                    message = error
                };
            }

            // Check if Dolt is initialized
            var doltInitialized = await _doltCli.IsInitializedAsync();
            if (!doltInitialized)
            {
                var error = "Dolt repository not initialized. Cannot update manifest.";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = "DOLT_NOT_INITIALIZED",
                    message = error
                };
            }

            // Get current Dolt state
            var currentCommit = await _doltCli.GetHeadCommitHashAsync();
            var currentBranch = await _doltCli.GetCurrentBranchAsync();

            ToolLoggingUtility.LogToolInfo(_logger, toolName,
                $"Current Dolt state - Branch: {currentBranch}, Commit: {currentCommit?.Substring(0, Math.Min(7, currentCommit?.Length ?? 0))}");

            // Update Dolt state in manifest
            var updatedDolt = existingManifest.Dolt with
            {
                CurrentCommit = currentCommit,
                CurrentBranch = currentBranch
            };

            // Prepare Git mapping update
            var updatedGitMapping = existingManifest.GitMapping;

            if (include_git_mapping == true && existingManifest.GitMapping.Enabled)
            {
                var isGitRepo = await _gitIntegration.IsGitRepositoryAsync(resolvedProjectRoot);
                if (isGitRepo)
                {
                    var gitCommit = await _gitIntegration.GetCurrentGitCommitAsync(resolvedProjectRoot);
                    if (!string.IsNullOrEmpty(gitCommit) && !string.IsNullOrEmpty(currentCommit))
                    {
                        updatedGitMapping = existingManifest.GitMapping with
                        {
                            LastGitCommit = gitCommit,
                            DoltCommitAtGitCommit = currentCommit
                        };

                        ToolLoggingUtility.LogToolInfo(_logger, toolName,
                            $"Updated Git mapping - Git: {gitCommit.Substring(0, 7)} -> Dolt: {currentCommit.Substring(0, 7)}");
                    }
                }
            }

            // Create updated manifest
            var updatedManifest = existingManifest with
            {
                Dolt = updatedDolt,
                GitMapping = updatedGitMapping,
                UpdatedAt = DateTime.UtcNow
            };

            // Write updated manifest
            await _manifestService.WriteManifestAsync(resolvedProjectRoot, updatedManifest);

            var manifestPath = _manifestService.GetManifestPath(resolvedProjectRoot);
            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Updated manifest at {manifestPath}");

            // Build response
            var response = new
            {
                success = true,
                message = $"Successfully updated Embranch manifest at {manifestPath}",
                changes = new
                {
                    previous_commit = existingManifest.Dolt.CurrentCommit?.Substring(0, Math.Min(7, existingManifest.Dolt.CurrentCommit?.Length ?? 0)),
                    new_commit = updatedManifest.Dolt.CurrentCommit?.Substring(0, Math.Min(7, updatedManifest.Dolt.CurrentCommit?.Length ?? 0)),
                    previous_branch = existingManifest.Dolt.CurrentBranch,
                    new_branch = updatedManifest.Dolt.CurrentBranch,
                    git_mapping_updated = updatedGitMapping != existingManifest.GitMapping
                },
                manifest = new
                {
                    path = manifestPath,
                    version = updatedManifest.Version,
                    dolt = new
                    {
                        current_branch = updatedManifest.Dolt.CurrentBranch,
                        current_commit = updatedManifest.Dolt.CurrentCommit?.Substring(0, Math.Min(7, updatedManifest.Dolt.CurrentCommit?.Length ?? 0))
                    },
                    git_mapping = new
                    {
                        enabled = updatedManifest.GitMapping.Enabled,
                        last_git_commit = updatedManifest.GitMapping.LastGitCommit?.Substring(0, Math.Min(7, updatedManifest.GitMapping.LastGitCommit?.Length ?? 0)),
                        dolt_commit_at_git_commit = updatedManifest.GitMapping.DoltCommitAtGitCommit?.Substring(0, Math.Min(7, updatedManifest.GitMapping.DoltCommitAtGitCommit?.Length ?? 0))
                    },
                    updated_at = updatedManifest.UpdatedAt.ToString("O")
                },
                note = note
            };

            return response;
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
