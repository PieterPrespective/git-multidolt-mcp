using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Embranch.Models;

namespace EmbranchTesting.UnitTests
{
    /// <summary>
    /// Simple unit tests for PP13-68 content hash verification logic
    /// Tests the core hash computation logic that fixes the count-based sync bug
    /// </summary>
    [TestFixture]
    public class PP13_68_SimpleContentHashTests
    {
        /// <summary>
        /// Helper method to compute content hash (same as implemented in SyncManagerV2)
        /// </summary>
        private string ComputeContentHash(string content)
        {
            if (string.IsNullOrEmpty(content))
                return string.Empty;
                
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Test that content hash computation works correctly for different content
        /// </summary>
        [Test]
        public void ComputeContentHash_DifferentContent_ShouldProduceDifferentHashes()
        {
            // This is the core test that would have prevented PP13-68 bug
            
            var contentA1 = "User A added a document and then added some text";
            var contentA2 = "User B added a document too, and changed it after and then added some text";
            
            var contentB1 = "User A added a document and then added some different text";
            var contentB2 = "User B added a document too, and changed it after and then added some different text";
            
            // Compute hashes for both sets
            var hashA1 = ComputeContentHash(contentA1);
            var hashA2 = ComputeContentHash(contentA2);
            var hashB1 = ComputeContentHash(contentB1);
            var hashB2 = ComputeContentHash(contentB2);
            
            // Verify that different content produces different hashes
            Assert.That(hashA1, Is.Not.EqualTo(hashB1), "Different content should produce different hashes");
            Assert.That(hashA2, Is.Not.EqualTo(hashB2), "Different content should produce different hashes");
            
            // Verify that same content produces same hash
            var hashA1_duplicate = ComputeContentHash(contentA1);
            Assert.That(hashA1, Is.EqualTo(hashA1_duplicate), "Same content should produce same hash");
            
            Console.WriteLine($"Content A1 hash: {hashA1}");
            Console.WriteLine($"Content B1 hash: {hashB1}");
            Console.WriteLine("✓ Content hash computation correctly differentiates content");
        }

        /// <summary>
        /// Test content hash collection comparison logic
        /// This simulates what the new CompareCollectionContentHashesAsync method does
        /// </summary>
        [Test]
        public void ContentHashCollectionComparison_SameCountDifferentContent_ShouldDetectDifference()
        {
            // Simulate the PP13-68 scenario: same document count, different content
            var chromaDocuments = new List<string>
            {
                "Original content 1",
                "Original content 2"
            };
            var chromaIds = new List<string> { "id1", "id2" };
            
            var doltDocuments = new List<DoltDocumentV2>
            {
                new DoltDocumentV2("id1", "test", "Modified content 1", "hash1"),
                new DoltDocumentV2("id2", "test", "Modified content 2", "hash2")
            };
            
            // Compute hash maps (simulating the logic in CompareCollectionContentHashesAsync)
            var chromaContentHashes = new Dictionary<string, string>();
            for (int i = 0; i < chromaIds.Count && i < chromaDocuments.Count; i++)
            {
                var contentHash = ComputeContentHash(chromaDocuments[i]);
                chromaContentHashes[chromaIds[i]] = contentHash;
            }
            
            var doltContentHashes = new Dictionary<string, string>();
            foreach (var doc in doltDocuments)
            {
                var contentHash = ComputeContentHash(doc.Content);
                doltContentHashes[doc.DocId] = contentHash;
            }
            
            // Compare hash sets (this is the core fix for PP13-68)
            bool contentMatches = true;
            
            if (chromaContentHashes.Count != doltContentHashes.Count)
            {
                contentMatches = false;
            }
            else
            {
                foreach (var kvp in doltContentHashes)
                {
                    if (!chromaContentHashes.TryGetValue(kvp.Key, out var chromaHash) || chromaHash != kvp.Value)
                    {
                        contentMatches = false;
                        break;
                    }
                }
            }
            
            // Assert: Content should NOT match despite same count
            Assert.That(contentMatches, Is.False, 
                "Content hash comparison should detect difference when content differs despite same count");
            
            Console.WriteLine("✓ Content hash collection comparison correctly detects content differences");
        }

        /// <summary>
        /// Test that identical content is correctly identified as matching
        /// </summary>
        [Test]
        public void ContentHashCollectionComparison_IdenticalContent_ShouldMatch()
        {
            var documents = new List<string>
            {
                "Same content 1",
                "Same content 2"
            };
            var ids = new List<string> { "id1", "id2" };
            
            // Simulate both Chroma and Dolt having identical content
            var chromaContentHashes = new Dictionary<string, string>();
            var doltContentHashes = new Dictionary<string, string>();
            
            for (int i = 0; i < ids.Count; i++)
            {
                var contentHash = ComputeContentHash(documents[i]);
                chromaContentHashes[ids[i]] = contentHash;
                doltContentHashes[ids[i]] = contentHash;
            }
            
            // Compare hash sets
            bool contentMatches = chromaContentHashes.Count == doltContentHashes.Count;
            if (contentMatches)
            {
                foreach (var kvp in doltContentHashes)
                {
                    if (!chromaContentHashes.TryGetValue(kvp.Key, out var chromaHash) || chromaHash != kvp.Value)
                    {
                        contentMatches = false;
                        break;
                    }
                }
            }
            
            // Assert: Identical content should match
            Assert.That(contentMatches, Is.True, "Identical content should be detected as matching");
            
            Console.WriteLine("✓ Content hash collection comparison correctly identifies identical content");
        }

        /// <summary>
        /// Test edge cases for content hash computation
        /// </summary>
        [Test]
        public void ComputeContentHash_EdgeCases_ShouldHandleCorrectly()
        {
            // Test empty string
            var emptyHash = ComputeContentHash("");
            Assert.That(emptyHash, Is.EqualTo(""), "Empty content should return empty hash");
            
            // Test null string
            var nullHash = ComputeContentHash(null!);
            Assert.That(nullHash, Is.EqualTo(""), "Null content should return empty hash");
            
            // Test whitespace differences
            var content1 = "test content";
            var content2 = "test content "; // extra space
            var hash1 = ComputeContentHash(content1);
            var hash2 = ComputeContentHash(content2);
            
            Assert.That(hash1, Is.Not.EqualTo(hash2), "Whitespace differences should produce different hashes");
            
            Console.WriteLine("✓ Content hash computation handles edge cases correctly");
        }
    }
}