using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Models;
using DMMS.Services;
using DMMS.Utilities;

namespace DMMS.Tools;

/// <summary>
/// PP13-79: MCP tool to initialize a DMMS manifest file (.dmms/state.json)
/// Creates the manifest based on current Dolt state for Git-tracked projects
/// </summary>
[McpServerToolType]
public class InitManifestTool
{
    private readonly ILogger<InitManifestTool> _logger;
    private readonly IDmmsStateManifest _manifestService;
    private readonly IDoltCli _doltCli;
    private readonly IGitIntegration _gitIntegration;

    public InitManifestTool(
        ILogger<InitManifestTool> logger,
        IDmmsStateManifest manifestService,
        IDoltCli doltCli,
        IGitIntegration gitIntegration)
    {
        _logger = logger;
        _manifestService = manifestService;
        _doltCli = doltCli;
        _gitIntegration = gitIntegration;
    }

    /// <summary>
    /// Initialize a DMMS manifest file (.dmms/state.json) in the project root.
    /// This creates a Git-trackable manifest that records the current Dolt state
    /// and enables automatic state synchronization on clone/checkout.
    /// </summary>
    [McpServerTool]
    [Description("Initialize a DMMS manifest file (.dmms/state.json) in the project root. This creates a Git-trackable manifest that records the current Dolt state and enables automatic state synchronization on clone/checkout.")]
    public virtual async Task<object> InitManifest(
        string? remote_url = null,
        string? default_branch = "main",
        string? init_mode = "auto",
        string? project_root = null)
    {
        const string toolName = nameof(InitManifestTool);
        const string methodName = nameof(InitManifest);

        try
        {
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName,
                $"RemoteUrl: {remote_url ?? "none"}, DefaultBranch: {default_branch}, InitMode: {init_mode}");

            // Validate init_mode
            if (!string.IsNullOrEmpty(init_mode) && !InitializationMode.IsValid(init_mode))
            {
                var error = $"Invalid init_mode: {init_mode}. Must be one of: auto, prompt, manual, disabled";
                ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, error);
                return new
                {
                    success = false,
                    error = "INVALID_INIT_MODE",
                    message = error
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
                var gitRoot = await _gitIntegration.GetGitRootAsync(Directory.GetCurrentDirectory());
                resolvedProjectRoot = gitRoot ?? Directory.GetCurrentDirectory();
            }

            ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Using project root: {resolvedProjectRoot}");

            // Check if manifest already exists
            if (await _manifestService.ManifestExistsAsync(resolvedProjectRoot))
            {
                var existingManifest = await _manifestService.ReadManifestAsync(resolvedProjectRoot);
                ToolLoggingUtility.LogToolWarning(_logger, toolName, "Manifest already exists");
                return new
                {
                    success = false,
                    error = "MANIFEST_EXISTS",
                    message = "A manifest file already exists at this location. Use update_manifest to modify it.",
                    existing_manifest = new
                    {
                        path = _manifestService.GetManifestPath(resolvedProjectRoot),
                        version = existingManifest?.Version,
                        remote_url = existingManifest?.Dolt.RemoteUrl,
                        current_branch = existingManifest?.Dolt.CurrentBranch,
                        current_commit = existingManifest?.Dolt.CurrentCommit?.Substring(0, Math.Min(7, existingManifest?.Dolt.CurrentCommit?.Length ?? 0))
                    }
                };
            }

            // Get current Dolt state if available
            string? currentCommit = null;
            string? currentBranch = null;
            var doltInitialized = await _doltCli.IsInitializedAsync();

            if (doltInitialized)
            {
                currentCommit = await _doltCli.GetHeadCommitHashAsync();
                currentBranch = await _doltCli.GetCurrentBranchAsync();
                ToolLoggingUtility.LogToolInfo(_logger, toolName,
                    $"Current Dolt state - Branch: {currentBranch}, Commit: {currentCommit?.Substring(0, Math.Min(7, currentCommit?.Length ?? 0))}");
            }

            // Get current Git commit for mapping
            string? gitCommit = null;
            var isGitRepo = await _gitIntegration.IsGitRepositoryAsync(resolvedProjectRoot);
            if (isGitRepo)
            {
                gitCommit = await _gitIntegration.GetCurrentGitCommitAsync(resolvedProjectRoot);
            }

            // Create manifest
            var manifest = new DmmsManifest
            {
                Version = "1.0",
                Dolt = new DoltManifestConfig
                {
                    RemoteUrl = remote_url,
                    DefaultBranch = default_branch ?? "main",
                    CurrentCommit = currentCommit,
                    CurrentBranch = currentBranch ?? default_branch ?? "main"
                },
                GitMapping = new GitMappingConfig
                {
                    Enabled = isGitRepo,
                    LastGitCommit = gitCommit,
                    DoltCommitAtGitCommit = currentCommit
                },
                Initialization = new InitializationConfig
                {
                    Mode = init_mode ?? "auto",
                    OnClone = OnCloneBehavior.SyncToManifest,
                    OnBranchChange = OnBranchChangeBehavior.PreserveLocal
                },
                Collections = new CollectionTrackingConfig
                {
                    Tracked = new List<string> { "*" },
                    Excluded = new List<string>()
                },
                UpdatedAt = DateTime.UtcNow
            };

            await _manifestService.WriteManifestAsync(resolvedProjectRoot, manifest);

            var manifestPath = _manifestService.GetManifestPath(resolvedProjectRoot);
            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Created manifest at {manifestPath}");

            return new
            {
                success = true,
                message = $"Successfully created DMMS manifest at {manifestPath}",
                manifest = new
                {
                    path = manifestPath,
                    version = manifest.Version,
                    dolt = new
                    {
                        remote_url = manifest.Dolt.RemoteUrl,
                        default_branch = manifest.Dolt.DefaultBranch,
                        current_branch = manifest.Dolt.CurrentBranch,
                        current_commit = manifest.Dolt.CurrentCommit?.Substring(0, Math.Min(7, manifest.Dolt.CurrentCommit?.Length ?? 0))
                    },
                    initialization = new
                    {
                        mode = manifest.Initialization.Mode,
                        on_clone = manifest.Initialization.OnClone,
                        on_branch_change = manifest.Initialization.OnBranchChange
                    },
                    git_mapping = new
                    {
                        enabled = manifest.GitMapping.Enabled,
                        last_git_commit = manifest.GitMapping.LastGitCommit?.Substring(0, Math.Min(7, manifest.GitMapping.LastGitCommit?.Length ?? 0))
                    }
                },
                next_steps = isGitRepo
                    ? "The manifest has been created. Add and commit .dmms/state.json to Git to enable state synchronization on clone."
                    : "The manifest has been created. Note: This project is not a Git repository, so Git-based features are disabled."
            };
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to create manifest: {ex.Message}"
            };
        }
    }
}
