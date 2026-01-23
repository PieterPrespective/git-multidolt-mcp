namespace Embranch.Services
{
    /// <summary>
    /// Exception thrown when Dolt operations fail
    /// </summary>
    public class DoltException : Exception
    {
        /// <summary>
        /// Exit code from the Dolt CLI command
        /// </summary>
        public int? ExitCode { get; }

        /// <summary>
        /// Standard error output from the command
        /// </summary>
        public string? StandardError { get; }

        /// <summary>
        /// Standard output from the command
        /// </summary>
        public string? StandardOutput { get; }

        /// <summary>
        /// Create a new DoltException with a message
        /// </summary>
        public DoltException(string message) : base(message)
        {
        }

        /// <summary>
        /// Create a new DoltException with a message and inner exception
        /// </summary>
        public DoltException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Create a new DoltException with command execution details
        /// </summary>
        public DoltException(string message, int exitCode, string standardError, string? standardOutput = null) 
            : base(message)
        {
            ExitCode = exitCode;
            StandardError = standardError;
            StandardOutput = standardOutput;
        }
    }
}