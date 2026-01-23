using Embranch.Models;
using Embranch.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace EmbranchTesting.Services
{
    /// <summary>
    /// Unit tests for DoltCli remote parsing functionality.
    /// Tests the static parsing logic that processes 'dolt remote -v' output to ensure robust parsing.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    [Category("DoltCli")]
    public class DoltCliRemoteParsingTests
    {
        /// <summary>
        /// Test the original parsing logic (before fix) to demonstrate the bug.
        /// This simulates the exact parsing logic that was used in the DoltCli.ListRemotesAsync method before the fix.
        /// </summary>
        private static IEnumerable<RemoteInfo> ParseRemoteOutputOriginal(string output, bool success)
        {
            if (!success)
                return Enumerable.Empty<RemoteInfo>();

            var remotes = new Dictionary<string, string>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t'); // OLD implementation - only splits on TAB
                if (parts.Length >= 2)
                {
                    var name = parts[0];
                    var urlParts = parts[1].Split(' ');
                    var url = urlParts[0];
                    remotes[name] = url; // De-duplicate fetch/push entries
                }
            }

            return remotes.Select(kvp => new RemoteInfo(kvp.Key, kvp.Value));
        }

        /// <summary>
        /// Test the new enhanced parsing logic that mimics the fixed implementation.
        /// This simulates the enhanced parsing logic now used in the DoltCli.ListRemotesAsync method.
        /// </summary>
        private static IEnumerable<RemoteInfo> ParseRemoteOutputFixed(string output, bool success)
        {
            if (!success)
                return Enumerable.Empty<RemoteInfo>();

            var remotes = new Dictionary<string, string>();
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    // Enhanced parsing: handle both TAB and multiple spaces using regex split
                    var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s+", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
                    if (parts.Length >= 2)
                    {
                        var name = parts[0];
                        var urlWithDirection = parts[1];
                        
                        // Extract URL (remove direction like "(fetch)" or "(push)")
                        var url = urlWithDirection.Split(' ')[0];
                        
                        // Validate URL format
                        if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(name))
                        {
                            remotes[name] = url; // De-duplicate fetch/push entries
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Text.RegularExpressions.RegexMatchTimeoutException || ex is ArgumentException)
                {
                    // Continue processing other lines
                }
            }

            return remotes.Select(kvp => new RemoteInfo(kvp.Key, kvp.Value));
        }

        /// <summary>
        /// Test that demonstrates the original parsing issue with space-separated output.
        /// This test will FAIL with the original implementation, demonstrating the bug.
        /// </summary>
        [Test]
        public void ParseRemoteOutputOriginal_Should_Fail_On_Space_Separated_Output()
        {
            // Arrange
            // Mock the output format that is likely causing issues (space-separated instead of tab-separated)
            var mockSpaceSeparatedOutput = "origin  https://www.dolthub.com/test-repo (fetch)\norigin  https://www.dolthub.com/test-repo (push)";

            // Act
            var remotes = ParseRemoteOutputOriginal(mockSpaceSeparatedOutput, true);

            // Assert - With the original implementation, this should fail (return 0 remotes)
            Assert.That(remotes, Is.Not.Null, "Remotes should not be null");
            Assert.That(remotes.Count(), Is.EqualTo(0), "Original implementation fails to parse space-separated output");
        }

        /// <summary>
        /// Test that verifies the original implementation works with tab-separated output.
        /// This should PASS, showing the original implementation worked with tabs.
        /// </summary>
        [Test]
        public void ParseRemoteOutputOriginal_Should_Parse_Tab_Separated_Output()
        {
            // Arrange
            // Mock the output format that the original implementation expects (tab-separated)
            var mockTabSeparatedOutput = "origin\thttps://www.dolthub.com/test-repo (fetch)\norigin\thttps://www.dolthub.com/test-repo (push)";

            // Act
            var remotes = ParseRemoteOutputOriginal(mockTabSeparatedOutput, true);

            // Assert
            Assert.That(remotes, Is.Not.Null, "Remotes should not be null");
            Assert.That(remotes.Count(), Is.EqualTo(1), "Should parse exactly one remote entry (deduplicated)");
            
            var remote = remotes.First();
            Assert.That(remote.Name, Is.EqualTo("origin"), "Remote name should be 'origin'");
            Assert.That(remote.Url, Is.EqualTo("https://www.dolthub.com/test-repo"), "Remote URL should be parsed correctly");
        }

        /// <summary>
        /// Test that the FIXED implementation handles space-separated output correctly.
        /// This test should PASS, demonstrating the fix works.
        /// </summary>
        [Test]
        public void ParseRemoteOutputFixed_Should_Parse_Space_Separated_Output()
        {
            // Arrange
            // Mock the output format that was causing issues (space-separated)
            var mockSpaceSeparatedOutput = "origin  https://www.dolthub.com/test-repo (fetch)\norigin  https://www.dolthub.com/test-repo (push)";

            // Act
            var remotes = ParseRemoteOutputFixed(mockSpaceSeparatedOutput, true);

            // Assert
            Assert.That(remotes, Is.Not.Null, "Remotes should not be null");
            Assert.That(remotes.Count(), Is.EqualTo(1), "Fixed implementation should parse exactly one remote entry (deduplicated)");
            
            var remote = remotes.First();
            Assert.That(remote.Name, Is.EqualTo("origin"), "Remote name should be 'origin'");
            Assert.That(remote.Url, Is.EqualTo("https://www.dolthub.com/test-repo"), "Remote URL should be parsed correctly");
        }

        /// <summary>
        /// Test that the FIXED implementation maintains backward compatibility with tab-separated output.
        /// This should PASS, showing backward compatibility is maintained.
        /// </summary>
        [Test]
        public void ParseRemoteOutputFixed_Should_Parse_Tab_Separated_Output()
        {
            // Arrange
            // Mock the output format that the original implementation expected (tab-separated)
            var mockTabSeparatedOutput = "origin\thttps://www.dolthub.com/test-repo (fetch)\norigin\thttps://www.dolthub.com/test-repo (push)";

            // Act
            var remotes = ParseRemoteOutputFixed(mockTabSeparatedOutput, true);

            // Assert
            Assert.That(remotes, Is.Not.Null, "Remotes should not be null");
            Assert.That(remotes.Count(), Is.EqualTo(1), "Should parse exactly one remote entry (deduplicated)");
            
            var remote = remotes.First();
            Assert.That(remote.Name, Is.EqualTo("origin"), "Remote name should be 'origin'");
            Assert.That(remote.Url, Is.EqualTo("https://www.dolthub.com/test-repo"), "Remote URL should be parsed correctly");
        }

        /// <summary>
        /// Test edge case with mixed whitespace (multiple spaces, some tabs).
        /// The fixed implementation should handle all cases correctly.
        /// </summary>
        [Test]
        public void ParseRemoteOutputFixed_Should_Parse_Mixed_Whitespace_Output()
        {
            // Arrange
            // Mock output with inconsistent whitespace
            var mockMixedOutput = "origin    https://www.dolthub.com/test-repo (fetch)\nbackup\t\thttps://www.dolthub.com/backup-repo  (push)";

            // Act
            var remotes = ParseRemoteOutputFixed(mockMixedOutput, true);

            // Assert
            Assert.That(remotes, Is.Not.Null, "Remotes should not be null");
            Assert.That(remotes.Count(), Is.EqualTo(2), "Should parse both remote entries");
            
            var originRemote = remotes.FirstOrDefault(r => r.Name == "origin");
            var backupRemote = remotes.FirstOrDefault(r => r.Name == "backup");
            
            Assert.That(originRemote, Is.Not.Null, "Origin remote should be found");
            Assert.That(originRemote.Url, Is.EqualTo("https://www.dolthub.com/test-repo"), "Origin URL should be correct");
            
            Assert.That(backupRemote, Is.Not.Null, "Backup remote should be found");
            Assert.That(backupRemote.Url, Is.EqualTo("https://www.dolthub.com/backup-repo"), "Backup URL should be correct");
        }

        /// <summary>
        /// Test with empty output (no remotes configured).
        /// Should return empty collection without errors.
        /// </summary>
        [Test]
        public void ParseRemoteOutputFixed_Should_Handle_Empty_Output()
        {
            // Arrange
            var mockEmptyOutput = "";

            // Act
            var remotes = ParseRemoteOutputFixed(mockEmptyOutput, true);

            // Assert
            Assert.That(remotes, Is.Not.Null, "Remotes should not be null");
            Assert.That(remotes.Count(), Is.EqualTo(0), "Should return empty collection for no remotes");
        }

        /// <summary>
        /// Test with command failure.
        /// Should return empty collection without throwing exceptions.
        /// </summary>
        [Test]
        public void ParseRemoteOutputFixed_Should_Handle_Command_Failure()
        {
            // Arrange
            var mockFailureOutput = "Command failed";

            // Act
            var remotes = ParseRemoteOutputFixed(mockFailureOutput, false);

            // Assert
            Assert.That(remotes, Is.Not.Null, "Remotes should not be null");
            Assert.That(remotes.Count(), Is.EqualTo(0), "Should return empty collection for command failure");
        }

        /// <summary>
        /// Test with malformed output (incomplete lines).
        /// Should gracefully skip malformed entries and continue parsing.
        /// </summary>
        [Test]
        public void ParseRemoteOutputFixed_Should_Handle_Malformed_Output()
        {
            // Arrange
            // Mock output with some malformed lines
            var mockMalformedOutput = "origin\thttps://www.dolthub.com/test-repo (fetch)\nincomplete-line\nbackup\thttps://www.dolthub.com/backup-repo (push)";

            // Act
            var remotes = ParseRemoteOutputFixed(mockMalformedOutput, true);

            // Assert
            Assert.That(remotes, Is.Not.Null, "Remotes should not be null");
            Assert.That(remotes.Count(), Is.EqualTo(2), "Should parse valid entries and skip malformed ones");
            
            var remoteNames = remotes.Select(r => r.Name).ToList();
            Assert.That(remoteNames, Contains.Item("origin"), "Should contain origin remote");
            Assert.That(remoteNames, Contains.Item("backup"), "Should contain backup remote");
        }

        /// <summary>
        /// Comprehensive test comparing original vs fixed implementations.
        /// This demonstrates that the fix resolves all the identified issues.
        /// </summary>
        [Test]
        public void CompareOriginalVsFixed_Implementation_Behavior()
        {
            // Test cases that demonstrate the difference
            var testCases = new[]
            {
                ("origin  https://www.dolthub.com/test-repo (fetch)\norigin  https://www.dolthub.com/test-repo (push)", "Space-separated"),
                ("origin\thttps://www.dolthub.com/test-repo (fetch)\norigin\thttps://www.dolthub.com/test-repo (push)", "Tab-separated"),
                ("origin    https://www.dolthub.com/test-repo (fetch)\nbackup\t\thttps://www.dolthub.com/backup-repo  (push)", "Mixed whitespace")
            };

            foreach (var (output, description) in testCases)
            {
                Console.WriteLine($"Testing {description}:");
                Console.WriteLine($"Input: '{output.Replace("\n", "\\n").Replace("\t", "\\t")}'");

                var originalRemotes = ParseRemoteOutputOriginal(output, true).ToList();
                var fixedRemotes = ParseRemoteOutputFixed(output, true).ToList();
                
                Console.WriteLine($"Original implementation: {originalRemotes.Count} remotes");
                Console.WriteLine($"Fixed implementation: {fixedRemotes.Count} remotes");
                
                // For space-separated, original should fail (0 remotes), fixed should work (1+ remotes)
                if (description == "Space-separated")
                {
                    Assert.That(originalRemotes.Count, Is.EqualTo(0), "Original implementation should fail on space-separated");
                    Assert.That(fixedRemotes.Count, Is.EqualTo(1), "Fixed implementation should work on space-separated");
                }
                // For tab-separated, both should work (backward compatibility)
                else if (description == "Tab-separated")
                {
                    Assert.That(originalRemotes.Count, Is.EqualTo(1), "Original implementation should work on tab-separated");
                    Assert.That(fixedRemotes.Count, Is.EqualTo(1), "Fixed implementation should work on tab-separated");
                }
                // For mixed, original should only parse tab entries, fixed should parse all
                else if (description == "Mixed whitespace")
                {
                    Assert.That(originalRemotes.Count, Is.EqualTo(1), "Original implementation should only parse tab entries");
                    Assert.That(fixedRemotes.Count, Is.EqualTo(2), "Fixed implementation should parse both entries");
                }
                
                Console.WriteLine();
            }
        }
    }
}