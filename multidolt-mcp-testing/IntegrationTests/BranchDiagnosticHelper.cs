using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Embranch.Services;
using Microsoft.Extensions.Logging;

namespace EmbranchTesting.IntegrationTests
{
    /// <summary>
    /// Helper class to diagnose branch state issues
    /// </summary>
    public static class BranchDiagnosticHelper
    {
        /// <summary>
        /// Logs the exact documents in Dolt for a given collection on the current branch
        /// </summary>
        public static async Task LogDoltDocuments(IDoltCli dolt, string collectionName, ILogger logger)
        {
            try
            {
                var currentBranch = await dolt.GetCurrentBranchAsync();
                logger.LogInformation("=== DOLT DIAGNOSTIC: Branch '{Branch}', Collection '{Collection}' ===", 
                    currentBranch, collectionName);
                
                // Get document IDs and content preview
                var docs = await dolt.QueryAsync<dynamic>($@"
                    SELECT doc_id, SUBSTRING(content, 1, 50) as content_preview
                    FROM documents 
                    WHERE collection_name = '{collectionName}'
                    ORDER BY doc_id");
                
                logger.LogInformation("Found {Count} documents in Dolt", docs.Count());
                foreach (dynamic doc in docs)
                {
                    var jsonElement = (System.Text.Json.JsonElement)doc;
                    var docId = jsonElement.GetProperty("doc_id").GetString() ?? "";
                    var contentPreview = jsonElement.GetProperty("content_preview").GetString() ?? "";
                    Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "  - {DocId}: {Preview}...", docId, contentPreview);
                }
                
                // Get commit info
                var commits = await dolt.QueryAsync<dynamic>("SELECT commit_hash, message FROM dolt_log LIMIT 1");
                if (commits.Count() > 0)
                {
                    dynamic commit = commits.First();
                    var commitElement = (System.Text.Json.JsonElement)commit;
                    var commitHash = commitElement.GetProperty("commit_hash").GetString() ?? "";
                    var commitMessage = commitElement.GetProperty("message").GetString() ?? "";
                    Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Current HEAD: {Hash} - {Message}", 
                        commitHash.Substring(0, Math.Min(7, commitHash.Length)), commitMessage);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to log Dolt documents for collection '{Collection}'", collectionName);
            }
        }
        
        /// <summary>
        /// Verifies branch ancestry to ensure it was created from the expected parent
        /// </summary>
        public static async Task VerifyBranchAncestry(IDoltCli dolt, string branchName, string expectedParent, ILogger logger)
        {
            try
            {
                var currentBranch = await dolt.GetCurrentBranchAsync();
                if (currentBranch != branchName)
                {
                    await dolt.CheckoutAsync(branchName);
                }
                
                // Get merge base to find common ancestor
                var exitCode = await dolt.ExecuteAsync($"merge-base {branchName} {expectedParent}");
                if (exitCode == 0)
                {
                    logger.LogInformation("Branch '{Branch}' and '{Parent}' share common ancestor", 
                        branchName, expectedParent);
                }
                else
                {
                    logger.LogWarning("Could not determine merge base between '{Branch}' and '{Parent}' (exit code: {ExitCode})", 
                        branchName, expectedParent, exitCode);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to verify branch ancestry");
            }
        }
    }
}