using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using DMMS.Services;

namespace DMMS.Tools;

/// <summary>
/// MCP tool that pushes local commits to the remote repository
/// </summary>
[McpServerToolType]
public class DoltPushTool
{
    private readonly ILogger<DoltPushTool> _logger;
    private readonly IDoltCli _doltCli;
    private readonly ISyncManagerV2 _syncManager;

    /// <summary>
    /// Initializes a new instance of the DoltPushTool class
    /// </summary>
    public DoltPushTool(ILogger<DoltPushTool> logger, IDoltCli doltCli, ISyncManagerV2 syncManager)
    {
        _logger = logger;
        _doltCli = doltCli;
        _syncManager = syncManager;
    }

    /// <summary>
    /// Push local commits to the remote Dolt repository (DoltHub). Only committed changes are pushed - uncommitted local changes are not affected
    /// </summary>
    [McpServerTool]
    [Description("Push local commits to the remote Dolt repository (DoltHub). Only committed changes are pushed - uncommitted local changes are not affected.")]
    public virtual async Task<object> DoltPush(
        string remote = "origin",
        string? branch = null,
        bool set_upstream = true,
        bool force = false)
    {
        try
        {
            _logger.LogInformation($"[DoltPushTool.DoltPush] Pushing to remote={remote}, branch={branch}, force={force}");

            // First check if Dolt is available
            var doltCheck = await _doltCli.CheckDoltAvailableAsync();
            if (!doltCheck.Success)
            {
                return new
                {
                    success = false,
                    error = "DOLT_EXECUTABLE_NOT_FOUND",
                    message = doltCheck.Error
                };
            }

            // Check if repository is initialized
            var isInitialized = await _doltCli.IsInitializedAsync();
            if (!isInitialized)
            {
                return new
                {
                    success = false,
                    error = "NOT_INITIALIZED",
                    message = "No Dolt repository configured. Use dolt_init or dolt_clone first."
                };
            }

            // Get current branch if not specified
            if (string.IsNullOrEmpty(branch))
            {
                branch = await _doltCli.GetCurrentBranchAsync();
            }

            // Check if remote exists
            var remotes = await _doltCli.ListRemotesAsync();
            var targetRemote = remotes?.FirstOrDefault(r => r.Name == remote);
            if (targetRemote == null)
            {
                // Enhanced error reporting with diagnostic information
                var availableRemotes = remotes?.Select(r => r.Name).ToList() ?? new List<string>();
                var diagnosticMessage = availableRemotes.Any() 
                    ? $"Remote '{remote}' not found. Available remotes: {string.Join(", ", availableRemotes)}"
                    : $"Remote '{remote}' not found. No remotes are currently configured.";

                _logger.LogWarning("[DoltPushTool] {DiagnosticMessage}", diagnosticMessage);

                return new
                {
                    success = false,
                    error = "REMOTE_NOT_FOUND",
                    message = diagnosticMessage,
                    availableRemotes = availableRemotes
                };
            }

            // Get current commit
            var localCommit = await _doltCli.GetHeadCommitHashAsync();

            // Check for uncommitted changes (informational only)
            var localChanges = await _syncManager.GetLocalChangesAsync();
            bool hasUncommittedChanges = localChanges?.HasChanges ?? false;

            // Perform the push using sync manager
            var pushResult = await _syncManager.ProcessPushAsync(remote, branch);

            if (!pushResult.Success)
            {
                string errorCode = "OPERATION_FAILED";
                if (pushResult.ErrorMessage?.Contains("rejected", StringComparison.OrdinalIgnoreCase) ?? false)
                    errorCode = "REMOTE_REJECTED";
                else if (pushResult.ErrorMessage?.Contains("authentication", StringComparison.OrdinalIgnoreCase) ?? false)
                    errorCode = "AUTHENTICATION_FAILED";
                
                return new
                {
                    success = false,
                    error = errorCode,
                    message = pushResult.ErrorMessage ?? "Push failed",
                    suggestions = errorCode == "REMOTE_REJECTED" 
                        ? new[] { "Pull first to get remote changes", "Use force=true to override (dangerous)" }
                        : null
                };
            }

            // TODO: Calculate actual commits pushed
            int commitsPushed = 0;

            return new
            {
                success = true,
                push_result = new
                {
                    remote = remote,
                    branch = branch,
                    commits_pushed = commitsPushed,
                    from_commit = localCommit ?? "",
                    to_url = targetRemote.Url
                },
                remote_state = new
                {
                    remote_branch = $"{remote}/{branch}",
                    remote_commit = localCommit ?? ""
                },
                message = commitsPushed > 0 
                    ? $"Pushed {commitsPushed} commits to {remote}/{branch}."
                    : "Already up to date.",
                warning = hasUncommittedChanges 
                    ? $"Note: You have uncommitted changes that were not pushed."
                    : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error pushing to remote '{remote}'");
            return new
            {
                success = false,
                error = "OPERATION_FAILED",
                message = $"Failed to push to remote: {ex.Message}"
            };
        }
    }
}