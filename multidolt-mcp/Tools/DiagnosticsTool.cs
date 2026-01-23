using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Embranch.Models;
using Embranch.Services;
using Embranch.Utilities;

namespace Embranch.Tools
{
    /// <summary>
    /// MCP Tool for system diagnostics and health checks.
    /// Part of Phase 5 implementation from PP13-57.
    /// </summary>
    [McpServerToolType]
    public class DiagnosticsTool
    {
        private readonly SyncDiagnosticService _diagnosticService;
        private readonly ILogger<DiagnosticsTool> _logger;
        
        public DiagnosticsTool(
            SyncDiagnosticService diagnosticService,
            ILogger<DiagnosticsTool> logger)
        {
            _diagnosticService = diagnosticService;
            _logger = logger;
        }
        
        /// <summary>
        /// Perform diagnostics and health checks on the Dolt-ChromaDB sync system
        /// </summary>
        [McpServerTool]
        [Description("Perform diagnostics and health checks on the Dolt-ChromaDB sync system")]
        public async Task<object> Diagnostics(
            string operation = "health_check",
            string? collection = null,
            bool verbose = false)
        {
            const string toolName = nameof(DiagnosticsTool);
            const string methodName = nameof(Diagnostics);
            ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, $"operation: {operation}, collection: {collection}, verbose: {verbose}");

            try
            {
                ToolLoggingUtility.LogToolInfo(_logger, toolName, $"Executing diagnostic operation: {operation}");
                
                var result = operation.ToLower() switch
                {
                    "health_check" => await PerformHealthCheckAsync(verbose),
                    "full_report" => await GenerateFullReportAsync(),
                    "validate_sync" => await ValidateSyncStateAsync(collection),
                    "auto_repair" => await AttemptAutoRepairAsync(),
                    "check_collection" => await CheckCollectionAsync(collection ?? throw new ArgumentException("Collection name required")),
                    _ => new { success = false, error = $"Unknown operation: {operation}" }
                };

                // Check if the result has a success property and log accordingly
                var resultType = result.GetType();
                var successProperty = resultType.GetProperty("success");
                if (successProperty != null && successProperty.GetValue(result) is bool success)
                {
                    if (!success)
                    {
                        ToolLoggingUtility.LogToolFailure(_logger, toolName, methodName, $"Diagnostic operation {operation} failed");
                    }
                    else
                    {
                        ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Diagnostic operation {operation} completed successfully");
                    }
                }
                else
                {
                    ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, $"Diagnostic operation {operation} completed");
                }

                return result;
            }
            catch (Exception ex)
            {
                ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
                return new { success = false, error = $"Diagnostic operation failed: {ex.Message}" };
            }
        }
        
        private async Task<object> PerformHealthCheckAsync(bool verbose)
        {
            var healthCheck = await _diagnosticService.PerformHealthCheckAsync();
            
            var response = new JsonObject
            {
                ["is_healthy"] = healthCheck.IsHealthy,
                ["timestamp"] = healthCheck.Timestamp.ToString("O"),
                ["check_duration_ms"] = healthCheck.CheckDuration.TotalMilliseconds,
                ["components"] = new JsonObject
                {
                    ["dolt"] = SerializeComponentHealth(healthCheck.DoltHealth),
                    ["chroma"] = SerializeComponentHealth(healthCheck.ChromaHealth),
                    ["sync_state"] = SerializeComponentHealth(healthCheck.SyncStateHealth),
                    ["python_context"] = SerializeComponentHealth(healthCheck.PythonContextHealth)
                }
            };
            
            if (!string.IsNullOrEmpty(healthCheck.ErrorMessage))
            {
                response["error"] = healthCheck.ErrorMessage;
            }
            
            if (verbose)
            {
                response["detailed_status"] = GetDetailedStatus(healthCheck);
            }
            
            var resultText = healthCheck.IsHealthy 
                ? "✅ System is healthy" 
                : "❌ System health check failed";
            
            if (!healthCheck.IsHealthy)
            {
                var unhealthyComponents = new List<string>();
                if (!healthCheck.DoltHealth.IsHealthy) unhealthyComponents.Add("Dolt");
                if (!healthCheck.ChromaHealth.IsHealthy) unhealthyComponents.Add("ChromaDB");
                if (!healthCheck.SyncStateHealth.IsHealthy) unhealthyComponents.Add("SyncState");
                if (!healthCheck.PythonContextHealth.IsHealthy) unhealthyComponents.Add("Python.NET");
                
                resultText += $"\nUnhealthy components: {string.Join(", ", unhealthyComponents)}";
            }
            
            return new { success = true, data = response, message = resultText };
        }
        
        private async Task<object> GenerateFullReportAsync()
        {
            var report = await _diagnosticService.GetDiagnosticReportAsync();
            
            var response = new JsonObject
            {
                ["timestamp"] = report.Timestamp.ToString("O"),
                ["system"] = new JsonObject
                {
                    ["platform"] = report.SystemInfo.Platform,
                    ["dotnet_version"] = report.SystemInfo.DotNetVersion,
                    ["machine_name"] = report.SystemInfo.MachineName
                },
                ["dolt"] = new JsonObject
                {
                    ["current_branch"] = report.DoltInfo.CurrentBranch,
                    ["head_commit"] = report.DoltInfo.HeadCommit ?? "none",
                    ["has_uncommitted_changes"] = report.DoltInfo.HasUncommittedChanges,
                    ["tables"] = new JsonArray(report.DoltInfo.Tables.Select(t => JsonValue.Create(t)).ToArray()),
                    ["collection_count"] = report.DoltInfo.CollectionCount,
                    ["error"] = report.DoltInfo.Error
                },
                ["chroma"] = new JsonObject
                {
                    ["collection_count"] = report.ChromaInfo.CollectionCount,
                    ["collections"] = new JsonArray(report.ChromaInfo.Collections.Select(c => JsonValue.Create(c)).ToArray()),
                    ["document_counts"] = JsonSerializer.SerializeToNode(report.ChromaInfo.DocumentCounts),
                    ["error"] = report.ChromaInfo.Error
                },
                ["sync_state"] = new JsonObject
                {
                    ["collections_in_dolt_only"] = new JsonArray(report.SyncStateInfo.CollectionsInDoltOnly.Select(c => JsonValue.Create(c)).ToArray()),
                    ["collections_in_chroma_only"] = new JsonArray(report.SyncStateInfo.CollectionsInChromaOnly.Select(c => JsonValue.Create(c)).ToArray()),
                    ["collections_in_both"] = new JsonArray(report.SyncStateInfo.CollectionsInBoth.Select(c => JsonValue.Create(c)).ToArray()),
                    ["document_count_mismatches"] = new JsonArray(report.SyncStateInfo.DocumentCountMismatches.Select(m => JsonValue.Create(m)).ToArray()),
                    ["error"] = report.SyncStateInfo.Error
                }
            };
            
            if (report.Errors.Any())
            {
                response["errors"] = new JsonArray(report.Errors.Select(e => JsonValue.Create(e)).ToArray());
            }
            
            var summary = GenerateReportSummary(report);
            return new { success = true, data = response, message = summary };
        }
        
        private async Task<object> ValidateSyncStateAsync(string? collection)
        {
            var validation = await _diagnosticService.ValidateSyncStateAsync(collection);
            
            var response = new JsonObject
            {
                ["is_valid"] = validation.IsValid,
                ["error_count"] = validation.Errors.Count,
                ["warning_count"] = validation.Warnings.Count
            };
            
            if (validation.Errors.Any())
            {
                response["errors"] = new JsonArray(validation.Errors.Select(e => (JsonNode)new JsonObject
                {
                    ["code"] = e.ErrorCode,
                    ["message"] = e.Message,
                    ["collection"] = e.CollectionName,
                    ["severity"] = e.Severity.ToString()
                }).ToArray());
            }
            
            if (validation.Warnings.Any())
            {
                response["warnings"] = new JsonArray(validation.Warnings.Select(w => (JsonNode)new JsonObject
                {
                    ["code"] = w.ErrorCode,
                    ["message"] = w.Message,
                    ["collection"] = w.CollectionName,
                    ["severity"] = w.Severity.ToString()
                }).ToArray());
            }
            
            var resultText = validation.IsValid
                ? $"✅ Sync state is valid{(collection != null ? $" for collection '{collection}'" : "")}"
                : $"❌ Sync state validation failed with {validation.Errors.Count} errors, {validation.Warnings.Count} warnings";
            
            return new { success = true, data = response, message = resultText };
        }
        
        private async Task<object> AttemptAutoRepairAsync()
        {
            var repair = await _diagnosticService.AttemptAutoRepairAsync();
            
            var response = new JsonObject
            {
                ["success"] = repair.Success,
                ["message"] = repair.Message,
                ["repaired_issues"] = new JsonArray(repair.RepairedIssues.Select(i => JsonValue.Create(i)).ToArray())
            };
            
            var resultText = repair.Success
                ? $"✅ Auto-repair completed: {repair.RepairedIssues.Count} issues fixed"
                : $"❌ Auto-repair failed: {repair.Message}";
            
            return new { success = true, data = response, message = resultText };
        }
        
        private async Task<object> CheckCollectionAsync(string collectionName)
        {
            var validation = await _diagnosticService.ValidateSyncStateAsync(collectionName);
            var healthCheck = await _diagnosticService.PerformHealthCheckAsync();
            
            var response = new JsonObject
            {
                ["collection"] = collectionName,
                ["exists_in_dolt"] = false, // Will be updated
                ["exists_in_chroma"] = false, // Will be updated
                ["is_synced"] = validation.IsValid,
                ["issues"] = new JsonArray()
            };
            
            // Check existence and gather stats
            try
            {
                var report = await _diagnosticService.GetDiagnosticReportAsync();
                var inDolt = report.DoltInfo.CollectionCount > 0; // Simplified check
                var inChroma = report.ChromaInfo.Collections.Contains(collectionName);
                
                response["exists_in_dolt"] = inDolt;
                response["exists_in_chroma"] = inChroma;
                
                if (inChroma && report.ChromaInfo.DocumentCounts.TryGetValue(collectionName, out var count))
                {
                    response["document_count"] = count;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get collection details");
            }
            
            // Add any issues
            var issues = response["issues"] as JsonArray;
            foreach (var error in validation.Errors.Where(e => e.CollectionName == collectionName))
            {
                issues!.Add(new JsonObject
                {
                    ["type"] = "error",
                    ["code"] = error.ErrorCode,
                    ["message"] = error.Message
                });
            }
            
            foreach (var warning in validation.Warnings.Where(w => w.CollectionName == collectionName))
            {
                issues!.Add(new JsonObject
                {
                    ["type"] = "warning",
                    ["code"] = warning.ErrorCode,
                    ["message"] = warning.Message
                });
            }
            
            var resultText = validation.IsValid
                ? $"✅ Collection '{collectionName}' is properly synced"
                : $"⚠️ Collection '{collectionName}' has {validation.Errors.Count + validation.Warnings.Count} sync issues";
            
            return new { success = true, data = response, message = resultText };
        }
        
        private JsonObject SerializeComponentHealth(ComponentHealth health)
        {
            var obj = new JsonObject
            {
                ["name"] = health.ComponentName,
                ["is_healthy"] = health.IsHealthy,
                ["status"] = health.Status
            };
            
            if (!string.IsNullOrEmpty(health.ErrorMessage))
            {
                obj["error"] = health.ErrorMessage;
            }
            
            if (health.Metadata.Any())
            {
                obj["metadata"] = JsonSerializer.SerializeToNode(health.Metadata);
            }
            
            return obj;
        }
        
        private JsonObject GetDetailedStatus(HealthCheckResult healthCheck)
        {
            var details = new JsonObject();
            
            // Add component-specific details
            if (healthCheck.DoltHealth.Metadata.Any())
            {
                details["dolt_details"] = JsonSerializer.SerializeToNode(healthCheck.DoltHealth.Metadata);
            }
            
            if (healthCheck.ChromaHealth.Metadata.Any())
            {
                details["chroma_details"] = JsonSerializer.SerializeToNode(healthCheck.ChromaHealth.Metadata);
            }
            
            if (healthCheck.PythonContextHealth.Metadata.Any())
            {
                details["python_details"] = JsonSerializer.SerializeToNode(healthCheck.PythonContextHealth.Metadata);
            }
            
            return details;
        }
        
        private string GenerateReportSummary(DiagnosticReport report)
        {
            var summary = new List<string>();
            summary.Add("📊 Diagnostic Report Summary");
            summary.Add($"Generated: {report.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
            
            summary.Add("\n🔹 Dolt Status:");
            summary.Add($"  Branch: {report.DoltInfo.CurrentBranch}");
            summary.Add($"  Commit: {report.DoltInfo.HeadCommit?.Substring(0, 8) ?? "none"}");
            summary.Add($"  Collections: {report.DoltInfo.CollectionCount}");
            if (report.DoltInfo.HasUncommittedChanges)
            {
                summary.Add("  ⚠️ Has uncommitted changes");
            }
            
            summary.Add("\n🔹 ChromaDB Status:");
            summary.Add($"  Collections: {report.ChromaInfo.CollectionCount}");
            if (report.ChromaInfo.DocumentCounts.Any())
            {
                var totalDocs = report.ChromaInfo.DocumentCounts.Values.Where(v => v >= 0).Sum();
                summary.Add($"  Total Documents: {totalDocs}");
            }
            
            summary.Add("\n🔹 Sync State:");
            if (report.SyncStateInfo.CollectionsInDoltOnly.Any())
            {
                summary.Add($"  ⚠️ {report.SyncStateInfo.CollectionsInDoltOnly.Count} collections in Dolt only");
            }
            if (report.SyncStateInfo.CollectionsInChromaOnly.Any())
            {
                summary.Add($"  ⚠️ {report.SyncStateInfo.CollectionsInChromaOnly.Count} collections in ChromaDB only");
            }
            if (report.SyncStateInfo.DocumentCountMismatches.Any())
            {
                summary.Add($"  ⚠️ {report.SyncStateInfo.DocumentCountMismatches.Count} collections with document count mismatches");
            }
            
            if (report.Errors.Any())
            {
                summary.Add($"\n❌ {report.Errors.Count} errors encountered during diagnostics");
            }
            
            return string.Join("\n", summary);
        }
    }
}