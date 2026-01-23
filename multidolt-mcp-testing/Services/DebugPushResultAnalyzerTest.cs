using System.Text.RegularExpressions;
using NUnit.Framework;

namespace EmbranchTesting.Services
{
    [TestFixture]
    public class DebugPushResultAnalyzerTest
    {
        [Test]
        public void TestRegexPattern()
        {
            var output1 = "   abc1234..def5678  main -> main";
            var output2 = "   1a2b3c4..9x8y7z6  feature/test -> feature/test";
            
            var pattern = @"\s+([a-zA-Z0-9]+)\.\.([a-zA-Z0-9]+)\s+(\S+)\s+->\s+(\S+)";
            
            var match1 = Regex.Match(output1, pattern, RegexOptions.Multiline);
            var match2 = Regex.Match(output2, pattern, RegexOptions.Multiline);
            
            Console.WriteLine($"Output 1: '{output1}'");
            Console.WriteLine($"Match 1 Success: {match1.Success}");
            if (match1.Success)
            {
                Console.WriteLine($"Groups: {match1.Groups[1].Value}, {match1.Groups[2].Value}, {match1.Groups[3].Value}, {match1.Groups[4].Value}");
            }
            
            Console.WriteLine($"\nOutput 2: '{output2}'");
            Console.WriteLine($"Match 2 Success: {match2.Success}");
            if (match2.Success)
            {
                Console.WriteLine($"Groups: {match2.Groups[1].Value}, {match2.Groups[2].Value}, {match2.Groups[3].Value}, {match2.Groups[4].Value}");
            }

            Assert.That(match1.Success, Is.True, "First pattern should match");
            Assert.That(match2.Success, Is.True, "Second pattern should match");
        }
        
        [Test]
        public void TestFullAnalyzer()
        {
            var output1 = "   abc1234..def5678  main -> main";
            var commandResult1 = new Embranch.Models.DoltCommandResult(Success: true, Output: output1, Error: "", ExitCode: 0);
            
            var result1 = Embranch.Services.PushResultAnalyzer.AnalyzePushOutput(commandResult1);
            
            Console.WriteLine($"Analyzer result 1 - FromCommit: {result1.FromCommitHash}, ToCommit: {result1.ToCommitHash}");
            Console.WriteLine($"Success: {result1.Success}, Message: {result1.Message}");
            Console.WriteLine($"IsUpToDate: {result1.IsUpToDate}, IsNewBranch: {result1.IsNewBranch}");
            
            Assert.That(result1.FromCommitHash, Is.EqualTo("abc1234"));
            Assert.That(result1.ToCommitHash, Is.EqualTo("def5678"));
        }
    }
}