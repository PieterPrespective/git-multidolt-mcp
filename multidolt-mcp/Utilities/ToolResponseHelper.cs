using DMMS.Models;
using DMMS.Services;
using Microsoft.Extensions.Logging;

namespace DMMS.Utilities;

/// <summary>
/// PP13-79-C1: Helper utility for attaching out-of-sync warnings to tool responses.
/// Checks manifest sync state and adds warning object if out of sync.
/// </summary>
public static class ToolResponseHelper
{
    /// <summary>
    /// Attaches an out-of-sync warning to a tool response if the local state
    /// differs from the manifest. Returns the original response if in sync.
    /// </summary>
    /// <param name="response">The original tool response object</param>
    /// <param name="syncStateChecker">The sync state checker service</param>
    /// <param name="logger">Optional logger for diagnostic output</param>
    /// <returns>Response with optional warning attached</returns>
    public static async Task<object> AttachWarningIfOutOfSyncAsync(
        object response,
        ISyncStateChecker syncStateChecker,
        ILogger? logger = null)
    {
        try
        {
            // Check if there's an out-of-sync warning
            var warning = await syncStateChecker.GetOutOfSyncWarningAsync();

            if (warning == null)
            {
                // In sync - return original response
                return response;
            }

            logger?.LogDebug("[ToolResponseHelper] Out-of-sync warning detected, attaching to response");

            // Create enhanced response with warning
            return new
            {
                // Include all original response properties via dynamic
                response = response,
                // Add warning object
                manifest_warning = warning
            };
        }
        catch (Exception ex)
        {
            // Don't fail the tool response due to warning check failure
            logger?.LogDebug(ex, "[ToolResponseHelper] Failed to check sync state for warning attachment");
            return response;
        }
    }

    /// <summary>
    /// Creates a standard out-of-sync warning response when sync is blocked.
    /// Use this when an operation cannot proceed due to sync state issues.
    /// </summary>
    /// <param name="operation">Name of the blocked operation</param>
    /// <param name="syncState">The sync state check result</param>
    /// <returns>Error response with sync state details</returns>
    public static object CreateSyncBlockedResponse(string operation, SyncStateCheckResult syncState)
    {
        string reason;
        string action;

        if (syncState.HasLocalChanges)
        {
            reason = "You have uncommitted local changes that would be lost.";
            action = "Commit your local changes first, then retry the operation.";
        }
        else if (syncState.LocalAheadOfManifest)
        {
            reason = "Your local Dolt has commits not recorded in the manifest.";
            action = "Call update_manifest to record your state, or use force=true to override.";
        }
        else
        {
            reason = syncState.Reason ?? "Local state differs from manifest.";
            action = "Call sync_to_manifest to synchronize, or use force=true to override.";
        }

        return new
        {
            success = false,
            error = "SYNC_BLOCKED",
            message = $"Operation '{operation}' blocked: {reason}",
            sync_state = new
            {
                local_branch = syncState.LocalBranch,
                local_commit = syncState.LocalCommit?.Substring(0, Math.Min(7, syncState.LocalCommit?.Length ?? 0)),
                manifest_branch = syncState.ManifestBranch,
                manifest_commit = syncState.ManifestCommit?.Substring(0, Math.Min(7, syncState.ManifestCommit?.Length ?? 0)),
                has_local_changes = syncState.HasLocalChanges,
                local_ahead = syncState.LocalAheadOfManifest
            },
            action_required = action
        };
    }

    /// <summary>
    /// Wraps a successful response with sync state info for transparency.
    /// Useful for operations that should inform the user about sync state.
    /// </summary>
    /// <param name="response">The original successful response</param>
    /// <param name="syncState">Current sync state</param>
    /// <returns>Response with sync info attached</returns>
    public static object AttachSyncStateInfo(object response, SyncStateCheckResult syncState)
    {
        if (syncState.IsInSync)
        {
            return new
            {
                response = response,
                sync_status = "in_sync"
            };
        }

        return new
        {
            response = response,
            sync_status = "out_of_sync",
            sync_info = new
            {
                reason = syncState.Reason,
                local_branch = syncState.LocalBranch,
                local_commit = syncState.LocalCommit?.Substring(0, Math.Min(7, syncState.LocalCommit?.Length ?? 0)),
                manifest_branch = syncState.ManifestBranch,
                manifest_commit = syncState.ManifestCommit?.Substring(0, Math.Min(7, syncState.ManifestCommit?.Length ?? 0)),
                has_local_changes = syncState.HasLocalChanges
            }
        };
    }
}
