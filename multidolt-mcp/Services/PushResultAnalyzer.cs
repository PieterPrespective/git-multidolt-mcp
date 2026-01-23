using System.Text.RegularExpressions;
using Embranch.Models;
using Microsoft.Extensions.Logging;

namespace Embranch.Services
{
    /// <summary>
    /// Utility for analyzing Dolt push command output to determine actual push results
    /// </summary>
    public static class PushResultAnalyzer
    {
        /// <summary>
        /// Analyzes dolt push command output to create a structured PushResult
        /// </summary>
        /// <param name="commandResult">Raw command result from dolt push</param>
        /// <param name="logger">Logger for debugging output analysis</param>
        /// <returns>Structured PushResult with parsed information</returns>
        public static PushResult AnalyzePushOutput(DoltCommandResult commandResult, ILogger? logger = null)
        {
            var output = commandResult.Output ?? "";
            var error = commandResult.Error ?? "";
            var combinedOutput = $"{output}\n{error}".Trim();

            logger?.LogDebug("[PushResultAnalyzer] Analyzing push output: {Output}", combinedOutput);

            // Handle failure cases first
            if (!commandResult.Success)
            {
                return AnalyzeFailure(combinedOutput, logger);
            }

            // Handle success cases
            return AnalyzeSuccess(combinedOutput, logger);
        }

        /// <summary>
        /// Analyzes successful push command output
        /// </summary>
        private static PushResult AnalyzeSuccess(string output, ILogger? logger)
        {
            // Pattern 1: Everything up-to-date
            if (output.Contains("Everything up-to-date", StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogDebug("[PushResultAnalyzer] Detected up-to-date push");
                return new PushResult(
                    Success: true,
                    Message: "Already up to date",
                    CommitsPushed: 0,
                    FromCommitHash: null,
                    ToCommitHash: null,
                    IsUpToDate: true,
                    IsNewBranch: false,
                    IsRejected: false,
                    ErrorType: null,
                    RemoteUrl: ExtractRemoteUrl(output)
                );
            }

            // Pattern 2: New branch push - "* [new branch]      main -> main"
            var newBranchMatch = Regex.Match(output, @"\*\s*\[new branch\]\s+(\S+)\s+->\s+(\S+)", RegexOptions.Multiline);
            if (newBranchMatch.Success)
            {
                logger?.LogDebug("[PushResultAnalyzer] Detected new branch push: {Branch}", newBranchMatch.Groups[1].Value);
                return new PushResult(
                    Success: true,
                    Message: $"Created new branch {newBranchMatch.Groups[2].Value}",
                    CommitsPushed: -1, // Will be calculated separately
                    FromCommitHash: null,
                    ToCommitHash: null,
                    IsUpToDate: false,
                    IsNewBranch: true,
                    IsRejected: false,
                    ErrorType: null,
                    RemoteUrl: ExtractRemoteUrl(output)
                );
            }

            // Pattern 3: Normal push with commit range - "   abc1234..def5678  main -> main"
            var commitRangeMatch = Regex.Match(output, @"\s*([a-zA-Z0-9]+)\.\.([a-zA-Z0-9]+)\s+(\S+)\s+->\s+(\S+)", RegexOptions.Multiline);
            if (commitRangeMatch.Success)
            {
                var fromCommit = commitRangeMatch.Groups[1].Value;
                var toCommit = commitRangeMatch.Groups[2].Value;
                var sourceBranch = commitRangeMatch.Groups[3].Value;
                var targetBranch = commitRangeMatch.Groups[4].Value;

                logger?.LogDebug("[PushResultAnalyzer] Detected commit range push: {FromCommit}..{ToCommit}", fromCommit, toCommit);

                return new PushResult(
                    Success: true,
                    Message: $"Pushed commits to {targetBranch}",
                    CommitsPushed: -1, // Will be calculated using commit range
                    FromCommitHash: fromCommit,
                    ToCommitHash: toCommit,
                    IsUpToDate: false,
                    IsNewBranch: false,
                    IsRejected: false,
                    ErrorType: null,
                    RemoteUrl: ExtractRemoteUrl(output)
                );
            }

            // Pattern 4: Force push or other success indicators
            if (output.Contains("+ [force]", StringComparison.OrdinalIgnoreCase) || 
                output.Contains("forced update", StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogDebug("[PushResultAnalyzer] Detected force push");
                return new PushResult(
                    Success: true,
                    Message: "Force pushed successfully",
                    CommitsPushed: -1, // Will need to be calculated
                    FromCommitHash: null,
                    ToCommitHash: null,
                    IsUpToDate: false,
                    IsNewBranch: false,
                    IsRejected: false,
                    ErrorType: null,
                    RemoteUrl: ExtractRemoteUrl(output)
                );
            }

            // Default success case - couldn't parse specific pattern
            logger?.LogWarning("[PushResultAnalyzer] Success detected but couldn't parse specific pattern from: {Output}", output);
            return new PushResult(
                Success: true,
                Message: "Push completed successfully",
                CommitsPushed: -1, // Unknown, will need calculation
                FromCommitHash: null,
                ToCommitHash: null,
                IsUpToDate: false,
                IsNewBranch: false,
                IsRejected: false,
                ErrorType: null,
                RemoteUrl: ExtractRemoteUrl(output)
            );
        }

        /// <summary>
        /// Analyzes failed push command output
        /// </summary>
        private static PushResult AnalyzeFailure(string output, ILogger? logger)
        {
            string errorType;
            string message;

            // Authentication errors
            if (ContainsAnyIgnoreCase(output, "authentication", "credentials", "401", "unauthorized"))
            {
                errorType = "AUTHENTICATION_FAILED";
                message = "Authentication failed. Check your credentials.";
            }
            // Rejected push (non-fast-forward, hooks, etc.)
            else if (ContainsAnyIgnoreCase(output, "rejected", "non-fast-forward", "fetch first"))
            {
                errorType = "REMOTE_REJECTED";
                message = "Push rejected. Pull remote changes first or use force push.";
            }
            // Network/connectivity errors
            else if (ContainsAnyIgnoreCase(output, "could not resolve", "connection", "timeout", "network"))
            {
                errorType = "NETWORK_ERROR";
                message = "Network error. Check your internet connection and remote URL.";
            }
            // Permission/access errors
            else if (ContainsAnyIgnoreCase(output, "permission denied", "access denied", "forbidden", "403"))
            {
                errorType = "PERMISSION_DENIED";
                message = "Permission denied. Check your access rights to the repository.";
            }
            // Repository not found
            else if (ContainsAnyIgnoreCase(output, "repository not found", "does not exist", "404"))
            {
                errorType = "REPOSITORY_NOT_FOUND";
                message = "Repository not found. Check the remote URL.";
            }
            else
            {
                errorType = "OPERATION_FAILED";
                message = $"Push failed: {output}";
            }

            logger?.LogDebug("[PushResultAnalyzer] Detected failure type: {ErrorType}, Message: {Message}", errorType, message);

            return new PushResult(
                Success: false,
                Message: message,
                CommitsPushed: 0,
                FromCommitHash: null,
                ToCommitHash: null,
                IsUpToDate: false,
                IsNewBranch: false,
                IsRejected: errorType == "REMOTE_REJECTED",
                ErrorType: errorType,
                RemoteUrl: ExtractRemoteUrl(output)
            );
        }

        /// <summary>
        /// Extracts the remote URL from push output
        /// </summary>
        private static string? ExtractRemoteUrl(string output)
        {
            // Look for "To <url>" pattern
            var urlMatch = Regex.Match(output, @"To\s+(.+)", RegexOptions.Multiline);
            return urlMatch.Success ? urlMatch.Groups[1].Value.Trim() : null;
        }

        /// <summary>
        /// Helper method to check if text contains any of the given terms (case insensitive)
        /// </summary>
        private static bool ContainsAnyIgnoreCase(string text, params string[] terms)
        {
            return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Calculates the number of commits in a given range using git log approach
        /// </summary>
        /// <param name="doltCli">Dolt CLI instance for executing commands</param>
        /// <param name="fromCommit">Starting commit hash</param>
        /// <param name="toCommit">Ending commit hash</param>
        /// <returns>Number of commits between the two commits</returns>
        public static async Task<int> CalculateCommitCount(IDoltCli doltCli, string fromCommit, string toCommit)
        {
            try
            {
                // Use dolt log --oneline to count commits in range
                var logResult = await doltCli.ExecuteRawCommandAsync("log", "--oneline", $"{fromCommit}..{toCommit}");
                
                if (!logResult.Success || string.IsNullOrWhiteSpace(logResult.Output))
                {
                    return 0;
                }

                // Count non-empty lines in the log output
                var lines = logResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                return lines.Length;
            }
            catch
            {
                return 0;
            }
        }
    }
}