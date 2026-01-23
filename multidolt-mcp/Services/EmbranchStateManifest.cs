using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Embranch.Models;

namespace Embranch.Services;

/// <summary>
/// PP13-79: Implementation of Embranch state manifest operations.
/// Manages the .dmms/state.json file that tracks Dolt repository state
/// and Git-Dolt commit mappings for project synchronization.
/// </summary>
public class EmbranchStateManifest : IEmbranchStateManifest
{
    private readonly ILogger<EmbranchStateManifest> _logger;
    private const string DmmsDirectoryName = ".dmms";
    private const string ManifestFileName = "state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public EmbranchStateManifest(ILogger<EmbranchStateManifest> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DmmsManifest?> ReadManifestAsync(string projectPath)
    {
        try
        {
            var manifestPath = GetManifestPath(projectPath);

            if (!File.Exists(manifestPath))
            {
                _logger.LogDebug("[EmbranchStateManifest.ReadManifestAsync] Manifest not found at: {Path}", manifestPath);
                return null;
            }

            var json = await File.ReadAllTextAsync(manifestPath);

            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("[EmbranchStateManifest.ReadManifestAsync] Manifest file is empty at: {Path}", manifestPath);
                return null;
            }

            var manifest = JsonSerializer.Deserialize<DmmsManifest>(json, JsonOptions);

            if (manifest == null)
            {
                _logger.LogWarning("[EmbranchStateManifest.ReadManifestAsync] Failed to deserialize manifest at: {Path}", manifestPath);
                return null;
            }

            if (!ValidateManifest(manifest))
            {
                _logger.LogWarning("[EmbranchStateManifest.ReadManifestAsync] Invalid manifest structure at: {Path}", manifestPath);
                return null;
            }

            _logger.LogDebug("[EmbranchStateManifest.ReadManifestAsync] Successfully read manifest from: {Path}", manifestPath);
            return manifest;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[EmbranchStateManifest.ReadManifestAsync] JSON parse error reading manifest at: {Path}",
                GetManifestPath(projectPath));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EmbranchStateManifest.ReadManifestAsync] Error reading manifest from: {Path}",
                GetManifestPath(projectPath));
            return null;
        }
    }

    /// <inheritdoc />
    public async Task WriteManifestAsync(string projectPath, DmmsManifest manifest)
    {
        try
        {
            var dmmsDir = GetDmmsDirectoryPath(projectPath);
            var manifestPath = GetManifestPath(projectPath);

            // Ensure .dmms directory exists
            if (!Directory.Exists(dmmsDir))
            {
                Directory.CreateDirectory(dmmsDir);
                _logger.LogDebug("[EmbranchStateManifest.WriteManifestAsync] Created .dmms directory at: {Path}", dmmsDir);
            }

            // Update timestamp
            var updatedManifest = manifest with { UpdatedAt = DateTime.UtcNow };

            var json = JsonSerializer.Serialize(updatedManifest, JsonOptions);
            await File.WriteAllTextAsync(manifestPath, json);

            _logger.LogInformation("[EmbranchStateManifest.WriteManifestAsync] Successfully wrote manifest to: {Path}", manifestPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EmbranchStateManifest.WriteManifestAsync] Error writing manifest to: {Path}",
                GetManifestPath(projectPath));
            throw;
        }
    }

    /// <inheritdoc />
    public Task<bool> ManifestExistsAsync(string projectPath)
    {
        var manifestPath = GetManifestPath(projectPath);
        var exists = File.Exists(manifestPath);
        _logger.LogDebug("[EmbranchStateManifest.ManifestExistsAsync] Manifest exists check at {Path}: {Exists}", manifestPath, exists);
        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public async Task UpdateDoltCommitAsync(string projectPath, string commitHash, string branch)
    {
        try
        {
            var manifest = await ReadManifestAsync(projectPath);

            if (manifest == null)
            {
                _logger.LogWarning("[EmbranchStateManifest.UpdateDoltCommitAsync] No manifest found at: {Path}, cannot update Dolt commit", projectPath);
                return;
            }

            var updatedDolt = manifest.Dolt with
            {
                CurrentCommit = commitHash,
                CurrentBranch = branch
            };

            var updatedManifest = manifest with
            {
                Dolt = updatedDolt,
                UpdatedAt = DateTime.UtcNow
            };

            await WriteManifestAsync(projectPath, updatedManifest);

            _logger.LogInformation("[EmbranchStateManifest.UpdateDoltCommitAsync] Updated Dolt commit to {Commit} on branch {Branch}",
                commitHash.Substring(0, Math.Min(7, commitHash.Length)), branch);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EmbranchStateManifest.UpdateDoltCommitAsync] Error updating Dolt commit in manifest");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RecordGitMappingAsync(string projectPath, string gitCommit, string doltCommit)
    {
        try
        {
            var manifest = await ReadManifestAsync(projectPath);

            if (manifest == null)
            {
                _logger.LogWarning("[EmbranchStateManifest.RecordGitMappingAsync] No manifest found at: {Path}, cannot record Git mapping", projectPath);
                return;
            }

            if (!manifest.GitMapping.Enabled)
            {
                _logger.LogDebug("[EmbranchStateManifest.RecordGitMappingAsync] Git mapping is disabled, skipping");
                return;
            }

            var updatedGitMapping = manifest.GitMapping with
            {
                LastGitCommit = gitCommit,
                DoltCommitAtGitCommit = doltCommit
            };

            var updatedManifest = manifest with
            {
                GitMapping = updatedGitMapping,
                UpdatedAt = DateTime.UtcNow
            };

            await WriteManifestAsync(projectPath, updatedManifest);

            _logger.LogInformation("[EmbranchStateManifest.RecordGitMappingAsync] Recorded Git mapping: Git {GitCommit} -> Dolt {DoltCommit}",
                gitCommit.Substring(0, Math.Min(7, gitCommit.Length)),
                doltCommit.Substring(0, Math.Min(7, doltCommit.Length)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EmbranchStateManifest.RecordGitMappingAsync] Error recording Git mapping in manifest");
            throw;
        }
    }

    /// <inheritdoc />
    public string GetManifestPath(string projectPath)
    {
        return Path.Combine(projectPath, DmmsDirectoryName, ManifestFileName);
    }

    /// <inheritdoc />
    public string GetDmmsDirectoryPath(string projectPath)
    {
        return Path.Combine(projectPath, DmmsDirectoryName);
    }

    /// <inheritdoc />
    public bool ValidateManifest(DmmsManifest manifest)
    {
        // Check version is supported
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            _logger.LogDebug("[EmbranchStateManifest.ValidateManifest] Invalid: missing version");
            return false;
        }

        // Currently only support version 1.0
        if (manifest.Version != "1.0")
        {
            _logger.LogDebug("[EmbranchStateManifest.ValidateManifest] Invalid: unsupported version {Version}", manifest.Version);
            return false;
        }

        // Validate initialization mode if specified
        if (!string.IsNullOrEmpty(manifest.Initialization.Mode) &&
            !InitializationMode.IsValid(manifest.Initialization.Mode))
        {
            _logger.LogDebug("[EmbranchStateManifest.ValidateManifest] Invalid: unsupported initialization mode {Mode}",
                manifest.Initialization.Mode);
            return false;
        }

        // Validate on_clone behavior if specified
        if (!string.IsNullOrEmpty(manifest.Initialization.OnClone) &&
            !OnCloneBehavior.IsValid(manifest.Initialization.OnClone))
        {
            _logger.LogDebug("[EmbranchStateManifest.ValidateManifest] Invalid: unsupported on_clone behavior {Behavior}",
                manifest.Initialization.OnClone);
            return false;
        }

        // Validate on_branch_change behavior if specified
        if (!string.IsNullOrEmpty(manifest.Initialization.OnBranchChange) &&
            !OnBranchChangeBehavior.IsValid(manifest.Initialization.OnBranchChange))
        {
            _logger.LogDebug("[EmbranchStateManifest.ValidateManifest] Invalid: unsupported on_branch_change behavior {Behavior}",
                manifest.Initialization.OnBranchChange);
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public DmmsManifest CreateDefaultManifest(string? remoteUrl = null, string defaultBranch = "main", string initMode = "auto")
    {
        return new DmmsManifest
        {
            Version = "1.0",
            Dolt = new DoltManifestConfig
            {
                RemoteUrl = remoteUrl,
                DefaultBranch = defaultBranch,
                CurrentBranch = defaultBranch
            },
            GitMapping = new GitMappingConfig
            {
                Enabled = true
            },
            Initialization = new InitializationConfig
            {
                Mode = initMode,
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
    }
}
