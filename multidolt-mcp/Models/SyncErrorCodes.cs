namespace Embranch.Models
{
    /// <summary>
    /// Standardized error codes for sync operations to enable better diagnostics and error handling.
    /// Part of Phase 5 implementation from PP13-57.
    /// </summary>
    public static class SyncErrorCodes
    {
        // General sync errors (1000-1099)
        public const string SYNC_GENERAL_FAILURE = "SYNC-1000";
        public const string SYNC_TIMEOUT = "SYNC-1001";
        public const string SYNC_CANCELLED = "SYNC-1002";
        
        // Dolt-specific errors (2000-2099)
        public const string DOLT_REPOSITORY_NOT_FOUND = "DOLT-2000";
        public const string DOLT_BRANCH_NOT_FOUND = "DOLT-2001";
        public const string DOLT_COMMIT_FAILED = "DOLT-2002";
        public const string DOLT_CHECKOUT_FAILED = "DOLT-2003";
        public const string DOLT_MERGE_CONFLICT = "DOLT-2004";
        public const string DOLT_PUSH_FAILED = "DOLT-2005";
        public const string DOLT_PULL_FAILED = "DOLT-2006";
        public const string DOLT_UNCOMMITTED_CHANGES = "DOLT-2007";
        public const string DOLT_WORKING_DIR_DIRTY = "DOLT-2008";
        public const string DOLT_TABLE_NOT_FOUND = "DOLT-2009";
        
        // ChromaDB-specific errors (3000-3099)
        public const string CHROMA_CONNECTION_FAILED = "CHROMA-3000";
        public const string CHROMA_COLLECTION_NOT_FOUND = "CHROMA-3001";
        public const string CHROMA_COLLECTION_EXISTS = "CHROMA-3002";
        public const string CHROMA_DOCUMENT_NOT_FOUND = "CHROMA-3003";
        public const string CHROMA_PYTHON_CONTEXT_ERROR = "CHROMA-3004";
        public const string CHROMA_QUERY_FAILED = "CHROMA-3005";
        public const string CHROMA_ADD_FAILED = "CHROMA-3006";
        
        // Sync state errors (4000-4099)
        public const string STATE_INCONSISTENT = "STATE-4000";
        public const string STATE_LOCAL_CHANGES_EXIST = "STATE-4001";
        public const string STATE_COLLECTION_MISMATCH = "STATE-4002";
        public const string STATE_DOCUMENT_COUNT_MISMATCH = "STATE-4003";
        public const string STATE_HASH_MISMATCH = "STATE-4004";
        public const string STATE_METADATA_CORRUPT = "STATE-4005";
        
        // Validation errors (5000-5099)
        public const string VALIDATION_FAILED = "VALID-5000";
        public const string VALIDATION_COLLECTION_MISSING = "VALID-5001";
        public const string VALIDATION_DOCUMENT_MISSING = "VALID-5002";
        public const string VALIDATION_CONTENT_MISMATCH = "VALID-5003";
        
        /// <summary>
        /// Get human-readable description for an error code
        /// </summary>
        public static string GetDescription(string errorCode)
        {
            return errorCode switch
            {
                SYNC_GENERAL_FAILURE => "General sync operation failure",
                SYNC_TIMEOUT => "Sync operation timed out",
                SYNC_CANCELLED => "Sync operation was cancelled",
                
                DOLT_REPOSITORY_NOT_FOUND => "Dolt repository not found or not initialized",
                DOLT_BRANCH_NOT_FOUND => "Specified Dolt branch does not exist",
                DOLT_COMMIT_FAILED => "Failed to commit changes to Dolt",
                DOLT_CHECKOUT_FAILED => "Failed to checkout Dolt branch",
                DOLT_MERGE_CONFLICT => "Merge conflict detected in Dolt",
                DOLT_PUSH_FAILED => "Failed to push to remote Dolt repository",
                DOLT_PULL_FAILED => "Failed to pull from remote Dolt repository",
                DOLT_UNCOMMITTED_CHANGES => "Uncommitted changes exist in Dolt working directory",
                DOLT_WORKING_DIR_DIRTY => "Dolt working directory is not clean",
                DOLT_TABLE_NOT_FOUND => "Required Dolt table does not exist",
                
                CHROMA_CONNECTION_FAILED => "Failed to connect to ChromaDB",
                CHROMA_COLLECTION_NOT_FOUND => "ChromaDB collection not found",
                CHROMA_COLLECTION_EXISTS => "ChromaDB collection already exists",
                CHROMA_DOCUMENT_NOT_FOUND => "Document not found in ChromaDB",
                CHROMA_PYTHON_CONTEXT_ERROR => "Python.NET context error",
                CHROMA_QUERY_FAILED => "ChromaDB query operation failed",
                CHROMA_ADD_FAILED => "Failed to add documents to ChromaDB",
                
                STATE_INCONSISTENT => "System state is inconsistent between Dolt and ChromaDB",
                STATE_LOCAL_CHANGES_EXIST => "Local changes detected that need to be committed or discarded",
                STATE_COLLECTION_MISMATCH => "Collection state mismatch between Dolt and ChromaDB",
                STATE_DOCUMENT_COUNT_MISMATCH => "Document count mismatch between systems",
                STATE_HASH_MISMATCH => "Content hash mismatch detected",
                STATE_METADATA_CORRUPT => "Metadata appears to be corrupted",
                
                VALIDATION_FAILED => "Validation check failed",
                VALIDATION_COLLECTION_MISSING => "Expected collection is missing",
                VALIDATION_DOCUMENT_MISSING => "Expected document is missing",
                VALIDATION_CONTENT_MISMATCH => "Document content does not match expected value",
                
                _ => "Unknown error code"
            };
        }
    }
    
    /// <summary>
    /// Enhanced sync exception with error codes and structured context
    /// </summary>
    public class SyncException : Exception
    {
        public string ErrorCode { get; }
        public Dictionary<string, object> Context { get; }
        
        public SyncException(string errorCode, string message, Dictionary<string, object>? context = null, Exception? innerException = null)
            : base($"[{errorCode}] {message}", innerException)
        {
            ErrorCode = errorCode;
            Context = context ?? new Dictionary<string, object>();
        }
        
        public override string ToString()
        {
            var contextStr = Context.Any() 
                ? $"\nContext: {string.Join(", ", Context.Select(kvp => $"{kvp.Key}={kvp.Value}"))}" 
                : "";
            return $"{base.ToString()}{contextStr}";
        }
    }
}