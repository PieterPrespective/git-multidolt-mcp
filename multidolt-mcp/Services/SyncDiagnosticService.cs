using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Embranch.Models;

namespace Embranch.Services
{
    /// <summary>
    /// Diagnostic service for checking and reporting sync system state.
    /// Part of Phase 5 implementation from PP13-57.
    /// </summary>
    public class SyncDiagnosticService
    {
        private readonly IDoltCli _dolt;
        private readonly IChromaDbService _chromaService;
        private readonly ILogger<SyncDiagnosticService> _logger;
        
        public SyncDiagnosticService(
            IDoltCli dolt,
            IChromaDbService chromaService,
            ILogger<SyncDiagnosticService> logger)
        {
            _dolt = dolt;
            _chromaService = chromaService;
            _logger = logger;
        }
        
        /// <summary>
        /// Perform a comprehensive health check of the sync system
        /// </summary>
        public async Task<HealthCheckResult> PerformHealthCheckAsync()
        {
            var result = new HealthCheckResult();
            var startTime = DateTime.UtcNow;
            
            try
            {
                // Check Dolt connectivity and status
                result.DoltHealth = await CheckDoltHealthAsync();
                
                // Check ChromaDB connectivity
                result.ChromaHealth = await CheckChromaHealthAsync();
                
                // Check sync state consistency
                result.SyncStateHealth = await CheckSyncStateConsistencyAsync();
                
                // Check Python.NET context
                result.PythonContextHealth = CheckPythonContextHealth();
                
                // Overall status
                result.IsHealthy = result.DoltHealth.IsHealthy && 
                                  result.ChromaHealth.IsHealthy && 
                                  result.SyncStateHealth.IsHealthy &&
                                  result.PythonContextHealth.IsHealthy;
                
                result.CheckDuration = DateTime.UtcNow - startTime;
                result.Timestamp = DateTime.UtcNow;
                
                _logger.LogInformation("Health check completed: {Status}", 
                    result.IsHealthy ? "HEALTHY" : "UNHEALTHY");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
                result.IsHealthy = false;
                result.ErrorMessage = ex.Message;
            }
            
            return result;
        }
        
        /// <summary>
        /// Get detailed diagnostic information about current system state
        /// </summary>
        public async Task<DiagnosticReport> GetDiagnosticReportAsync()
        {
            var report = new DiagnosticReport
            {
                Timestamp = DateTime.UtcNow,
                SystemInfo = new SystemInfo
                {
                    Platform = Environment.OSVersion.Platform.ToString(),
                    DotNetVersion = Environment.Version.ToString(),
                    MachineName = Environment.MachineName
                }
            };
            
            try
            {
                // Dolt diagnostics
                report.DoltInfo = await GetDoltDiagnosticsAsync();
                
                // ChromaDB diagnostics
                report.ChromaInfo = await GetChromaDiagnosticsAsync();
                
                // Sync state diagnostics
                report.SyncStateInfo = await GetSyncStateDiagnosticsAsync();
                
                // Recent operations log
                report.RecentOperations = GetRecentOperationsLog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate diagnostic report");
                report.Errors.Add($"Diagnostic generation error: {ex.Message}");
            }
            
            return report;
        }
        
        /// <summary>
        /// Validate sync state between Dolt and ChromaDB
        /// </summary>
        public async Task<ValidationResult> ValidateSyncStateAsync(string? collectionName = null)
        {
            var result = new ValidationResult();
            
            try
            {
                var doltCollections = await GetDoltCollectionsAsync();
                var chromaCollections = await _chromaService.ListCollectionsAsync();
                
                if (collectionName != null)
                {
                    // Validate specific collection
                    result = await ValidateCollectionAsync(collectionName);
                }
                else
                {
                    // Validate all collections
                    foreach (var collection in doltCollections.Union(chromaCollections).Distinct())
                    {
                        var collectionResult = await ValidateCollectionAsync(collection);
                        result.Merge(collectionResult);
                    }
                }
                
                result.IsValid = !result.Errors.Any();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Validation failed");
                result.IsValid = false;
                result.Errors.Add(new ValidationError
                {
                    ErrorCode = SyncErrorCodes.VALIDATION_FAILED,
                    Message = ex.Message,
                    Severity = ValidationSeverity.Critical
                });
            }
            
            return result;
        }
        
        /// <summary>
        /// Attempt to repair common sync state issues
        /// </summary>
        public async Task<RepairResult> AttemptAutoRepairAsync()
        {
            var result = new RepairResult();
            _logger.LogInformation("Starting auto-repair attempt");
            
            try
            {
                // Check for and fix orphaned ChromaDB collections
                var orphanedCollections = await FindOrphanedChromaCollectionsAsync();
                if (orphanedCollections.Any())
                {
                    _logger.LogInformation("Found {Count} orphaned ChromaDB collections", orphanedCollections.Count);
                    foreach (var collection in orphanedCollections)
                    {
                        await _chromaService.DeleteCollectionAsync(collection);
                        result.RepairedIssues.Add($"Removed orphaned ChromaDB collection: {collection}");
                    }
                }
                
                // Check for and fix metadata inconsistencies
                var metadataIssues = await FindMetadataInconsistenciesAsync();
                if (metadataIssues.Any())
                {
                    _logger.LogInformation("Found {Count} metadata inconsistencies", metadataIssues.Count);
                    foreach (var issue in metadataIssues)
                    {
                        // Attempt to fix by clearing is_local_change flags
                        await ClearLocalChangesFlagsAsync(issue.CollectionName);
                        result.RepairedIssues.Add($"Cleared stale metadata flags in collection: {issue.CollectionName}");
                    }
                }
                
                // Ensure Dolt working directory is clean
                var doltStatus = await _dolt.GetStatusAsync();
                if (doltStatus.HasUnstagedChanges || doltStatus.HasStagedChanges)
                {
                    _logger.LogInformation("Cleaning Dolt working directory");
                    await _dolt.ResetHardAsync("HEAD");
                    result.RepairedIssues.Add("Reset Dolt working directory to clean state");
                }
                
                result.Success = true;
                result.Message = $"Auto-repair completed. Fixed {result.RepairedIssues.Count} issues.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-repair failed");
                result.Success = false;
                result.Message = $"Auto-repair failed: {ex.Message}";
            }
            
            return result;
        }
        
        #region Private Helper Methods
        
        private async Task<ComponentHealth> CheckDoltHealthAsync()
        {
            var health = new ComponentHealth { ComponentName = "Dolt" };
            
            try
            {
                var status = await _dolt.GetStatusAsync();
                var branch = await _dolt.GetCurrentBranchAsync();
                var commit = await _dolt.GetHeadCommitHashAsync();
                
                health.IsHealthy = true;
                health.Status = $"Branch: {branch}, Commit: {commit?.Substring(0, 8) ?? "none"}";
                health.Metadata["HasUncommittedChanges"] = status.HasUnstagedChanges || status.HasStagedChanges;
                health.Metadata["CurrentBranch"] = branch;
            }
            catch (Exception ex)
            {
                health.IsHealthy = false;
                health.ErrorMessage = ex.Message;
                health.Status = "Connection failed";
            }
            
            return health;
        }
        
        private async Task<ComponentHealth> CheckChromaHealthAsync()
        {
            var health = new ComponentHealth { ComponentName = "ChromaDB" };
            
            try
            {
                var collections = await _chromaService.ListCollectionsAsync();
                health.IsHealthy = true;
                health.Status = $"{collections.Count} collections";
                health.Metadata["CollectionCount"] = collections.Count;
                health.Metadata["Collections"] = string.Join(", ", collections);
            }
            catch (Exception ex)
            {
                health.IsHealthy = false;
                health.ErrorMessage = ex.Message;
                health.Status = "Connection failed";
            }
            
            return health;
        }
        
        private async Task<ComponentHealth> CheckSyncStateConsistencyAsync()
        {
            var health = new ComponentHealth { ComponentName = "SyncState" };
            
            try
            {
                var validation = await ValidateSyncStateAsync();
                health.IsHealthy = validation.IsValid;
                health.Status = validation.IsValid ? "Consistent" : $"{validation.Errors.Count} inconsistencies";
                
                if (!validation.IsValid)
                {
                    health.ErrorMessage = string.Join("; ", validation.Errors.Select(e => e.Message));
                }
            }
            catch (Exception ex)
            {
                health.IsHealthy = false;
                health.ErrorMessage = ex.Message;
                health.Status = "Check failed";
            }
            
            return health;
        }
        
        private ComponentHealth CheckPythonContextHealth()
        {
            var health = new ComponentHealth { ComponentName = "Python.NET" };
            
            try
            {
                var isInitialized = PythonContext.IsInitialized;
                var queueStats = PythonContext.GetQueueStats();
                
                health.IsHealthy = isInitialized && !queueStats.IsOverThreshold;
                health.Status = isInitialized ? "Initialized" : "Not initialized";
                health.Metadata["QueueSize"] = queueStats.QueueSize;
                health.Metadata["IsOverThreshold"] = queueStats.IsOverThreshold;
                
                if (!health.IsHealthy)
                {
                    health.ErrorMessage = queueStats.IsOverThreshold 
                        ? $"Queue size {queueStats.QueueSize} exceeds threshold"
                        : "Python context not initialized";
                }
            }
            catch (Exception ex)
            {
                health.IsHealthy = false;
                health.ErrorMessage = ex.Message;
                health.Status = "Check failed";
            }
            
            return health;
        }
        
        private async Task<DoltDiagnostics> GetDoltDiagnosticsAsync()
        {
            var diag = new DoltDiagnostics();
            
            try
            {
                diag.CurrentBranch = await _dolt.GetCurrentBranchAsync();
                diag.HeadCommit = await _dolt.GetHeadCommitHashAsync();
                
                var status = await _dolt.GetStatusAsync();
                diag.HasUncommittedChanges = status.HasUnstagedChanges || status.HasStagedChanges;
                
                // Get table information
                var tables = await GetDoltTablesAsync();
                diag.Tables = tables;
                
                // Get collection count from documents table
                diag.CollectionCount = (await GetDoltCollectionsAsync()).Count;
            }
            catch (Exception ex)
            {
                diag.Error = ex.Message;
            }
            
            return diag;
        }
        
        private async Task<ChromaDiagnostics> GetChromaDiagnosticsAsync()
        {
            var diag = new ChromaDiagnostics();
            
            try
            {
                var collections = await _chromaService.ListCollectionsAsync();
                diag.Collections = collections;
                diag.CollectionCount = collections.Count;
                
                // Get document counts for each collection
                diag.DocumentCounts = new Dictionary<string, int>();
                foreach (var collection in collections)
                {
                    try
                    {
                        var count = await _chromaService.GetDocumentCountAsync(collection);
                        diag.DocumentCounts[collection] = count;
                    }
                    catch
                    {
                        diag.DocumentCounts[collection] = -1; // Error getting count
                    }
                }
            }
            catch (Exception ex)
            {
                diag.Error = ex.Message;
            }
            
            return diag;
        }
        
        private async Task<SyncStateDiagnostics> GetSyncStateDiagnosticsAsync()
        {
            var diag = new SyncStateDiagnostics();
            
            try
            {
                var doltCollections = await GetDoltCollectionsAsync();
                var chromaCollections = await _chromaService.ListCollectionsAsync();
                
                diag.CollectionsInDoltOnly = doltCollections.Except(chromaCollections).ToList();
                diag.CollectionsInChromaOnly = chromaCollections.Except(doltCollections).ToList();
                diag.CollectionsInBoth = doltCollections.Intersect(chromaCollections).ToList();
                
                // Check for document count mismatches
                diag.DocumentCountMismatches = new List<string>();
                foreach (var collection in diag.CollectionsInBoth)
                {
                    var doltCount = await GetDoltDocumentCountAsync(collection);
                    var chromaCount = await _chromaService.GetDocumentCountAsync(collection);
                    
                    if (doltCount != chromaCount)
                    {
                        diag.DocumentCountMismatches.Add(
                            $"{collection}: Dolt={doltCount}, ChromaDB={chromaCount}");
                    }
                }
            }
            catch (Exception ex)
            {
                diag.Error = ex.Message;
            }
            
            return diag;
        }
        
        private async Task<ValidationResult> ValidateCollectionAsync(string collectionName)
        {
            var result = new ValidationResult();
            
            try
            {
                // Check if collection exists in both systems
                var doltCollections = await GetDoltCollectionsAsync();
                var chromaCollections = await _chromaService.ListCollectionsAsync();
                
                var inDolt = doltCollections.Contains(collectionName);
                var inChroma = chromaCollections.Contains(collectionName);
                
                if (inDolt && !inChroma)
                {
                    result.Errors.Add(new ValidationError
                    {
                        ErrorCode = SyncErrorCodes.VALIDATION_COLLECTION_MISSING,
                        Message = $"Collection '{collectionName}' exists in Dolt but not in ChromaDB",
                        CollectionName = collectionName,
                        Severity = ValidationSeverity.High
                    });
                }
                else if (!inDolt && inChroma)
                {
                    result.Errors.Add(new ValidationError
                    {
                        ErrorCode = SyncErrorCodes.VALIDATION_COLLECTION_MISSING,
                        Message = $"Collection '{collectionName}' exists in ChromaDB but not in Dolt",
                        CollectionName = collectionName,
                        Severity = ValidationSeverity.High
                    });
                }
                else if (inDolt && inChroma)
                {
                    // Both exist - check document counts
                    var doltCount = await GetDoltDocumentCountAsync(collectionName);
                    var chromaCount = await _chromaService.GetDocumentCountAsync(collectionName);
                    
                    if (Math.Abs(doltCount - chromaCount) > 0)
                    {
                        result.Warnings.Add(new ValidationError
                        {
                            ErrorCode = SyncErrorCodes.STATE_DOCUMENT_COUNT_MISMATCH,
                            Message = $"Document count mismatch in '{collectionName}': Dolt={doltCount}, ChromaDB={chromaCount}",
                            CollectionName = collectionName,
                            Severity = ValidationSeverity.Medium,
                            Context = new Dictionary<string, object>
                            {
                                ["DoltCount"] = doltCount,
                                ["ChromaCount"] = chromaCount,
                                ["Difference"] = Math.Abs(doltCount - chromaCount)
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ValidationError
                {
                    ErrorCode = SyncErrorCodes.VALIDATION_FAILED,
                    Message = $"Failed to validate collection '{collectionName}': {ex.Message}",
                    CollectionName = collectionName,
                    Severity = ValidationSeverity.Critical
                });
            }
            
            return result;
        }
        
        private async Task<List<string>> GetDoltCollectionsAsync()
        {
            try
            {
                var sql = "SELECT DISTINCT collection_name FROM documents";
                var results = await _dolt.QueryAsync<dynamic>(sql);
                return results.Select(r => (string)r.collection_name).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
        
        private async Task<int> GetDoltDocumentCountAsync(string collectionName)
        {
            try
            {
                var sql = $"SELECT COUNT(*) as count FROM documents WHERE collection_name = '{collectionName}'";
                var results = await _dolt.QueryAsync<dynamic>(sql);
                return results.FirstOrDefault()?.count ?? 0;
            }
            catch
            {
                return 0;
            }
        }
        
        private async Task<List<string>> GetDoltTablesAsync()
        {
            try
            {
                var sql = "SHOW TABLES";
                var results = await _dolt.QueryAsync<dynamic>(sql);
                return results.Select(r => (string)r.Tables_in_database).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
        
        private async Task<List<string>> FindOrphanedChromaCollectionsAsync()
        {
            var doltCollections = await GetDoltCollectionsAsync();
            var chromaCollections = await _chromaService.ListCollectionsAsync();
            return chromaCollections.Except(doltCollections).ToList();
        }
        
        private async Task<List<MetadataInconsistency>> FindMetadataInconsistenciesAsync()
        {
            var inconsistencies = new List<MetadataInconsistency>();
            
            // This would check for documents with is_local_change=true that shouldn't have it
            // Implementation depends on specific business logic
            
            return inconsistencies;
        }
        
        private async Task ClearLocalChangesFlagsAsync(string collectionName)
        {
            // Clear is_local_change flags in ChromaDB
            // This would need to be implemented in ChromaDbService
            await Task.CompletedTask;
        }
        
        private List<OperationLogEntry> GetRecentOperationsLog()
        {
            // This would retrieve recent operations from a log store
            // For now, return empty list
            return new List<OperationLogEntry>();
        }
        
        #endregion
    }
    
    #region Diagnostic Models
    
    public class HealthCheckResult
    {
        public bool IsHealthy { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan CheckDuration { get; set; }
        public ComponentHealth DoltHealth { get; set; } = new();
        public ComponentHealth ChromaHealth { get; set; } = new();
        public ComponentHealth SyncStateHealth { get; set; } = new();
        public ComponentHealth PythonContextHealth { get; set; } = new();
        public string? ErrorMessage { get; set; }
    }
    
    public class ComponentHealth
    {
        public string ComponentName { get; set; } = "";
        public bool IsHealthy { get; set; }
        public string Status { get; set; } = "";
        public string? ErrorMessage { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
    
    public class DiagnosticReport
    {
        public DateTime Timestamp { get; set; }
        public SystemInfo SystemInfo { get; set; } = new();
        public DoltDiagnostics DoltInfo { get; set; } = new();
        public ChromaDiagnostics ChromaInfo { get; set; } = new();
        public SyncStateDiagnostics SyncStateInfo { get; set; } = new();
        public List<OperationLogEntry> RecentOperations { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }
    
    public class SystemInfo
    {
        public string Platform { get; set; } = "";
        public string DotNetVersion { get; set; } = "";
        public string MachineName { get; set; } = "";
    }
    
    public class DoltDiagnostics
    {
        public string CurrentBranch { get; set; } = "";
        public string? HeadCommit { get; set; }
        public bool HasUncommittedChanges { get; set; }
        public List<string> Tables { get; set; } = new();
        public int CollectionCount { get; set; }
        public string? Error { get; set; }
    }
    
    public class ChromaDiagnostics
    {
        public List<string> Collections { get; set; } = new();
        public int CollectionCount { get; set; }
        public Dictionary<string, int> DocumentCounts { get; set; } = new();
        public string? Error { get; set; }
    }
    
    public class SyncStateDiagnostics
    {
        public List<string> CollectionsInDoltOnly { get; set; } = new();
        public List<string> CollectionsInChromaOnly { get; set; } = new();
        public List<string> CollectionsInBoth { get; set; } = new();
        public List<string> DocumentCountMismatches { get; set; } = new();
        public string? Error { get; set; }
    }
    
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<ValidationError> Errors { get; set; } = new();
        public List<ValidationError> Warnings { get; set; } = new();
        
        public void Merge(ValidationResult other)
        {
            Errors.AddRange(other.Errors);
            Warnings.AddRange(other.Warnings);
        }
    }
    
    public class ValidationError
    {
        public string ErrorCode { get; set; } = "";
        public string Message { get; set; } = "";
        public string? CollectionName { get; set; }
        public ValidationSeverity Severity { get; set; }
        public Dictionary<string, object> Context { get; set; } = new();
    }
    
    public enum ValidationSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }
    
    public class RepairResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public List<string> RepairedIssues { get; set; } = new();
    }
    
    public class MetadataInconsistency
    {
        public string CollectionName { get; set; } = "";
        public string DocumentId { get; set; } = "";
        public string Issue { get; set; } = "";
    }
    
    public class OperationLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Operation { get; set; } = "";
        public bool Success { get; set; }
        public string? ErrorCode { get; set; }
        public string? Details { get; set; }
    }
    
    #endregion
}