using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Reflection;
using Embranch.Models;
using Embranch.Utilities;

namespace Embranch.Tools;

/// <summary>
/// MCP tool that provides server version information
/// </summary>
[McpServerToolType]
public class GetServerVersionTool
{
    private readonly ILogger<GetServerVersionTool> _logger;
    private readonly ServerConfiguration _config;
    
    /// <summary>
    /// Initializes a new instance of the GetServerVersionTool class
    /// </summary>
    /// <param name="logger">Logger instance for logging operations</param>
    /// <param name="config">Server configuration settings</param>
    public GetServerVersionTool(ILogger<GetServerVersionTool> logger, IOptions<ServerConfiguration> config)
    {
        _logger = logger;
        _config = config.Value;
    }
    
    /// <summary>
    /// Retrieves the current version and configuration of the Embranch MCP Server
    /// </summary>
    /// <returns>An object containing server version and configuration information</returns>
    [McpServerTool]
    [Description("Retrieves the current version of the Embranch MCP Server.")]
    public virtual Task<object> GetServerVersion()
    {
        const string toolName = nameof(GetServerVersionTool);
        const string methodName = nameof(GetServerVersion);
        ToolLoggingUtility.LogToolStart(_logger, toolName, methodName, "Retrieving server version information");

        try
        {
            ToolLoggingUtility.LogToolInfo(_logger, toolName, "Getting server version information");
            
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var assemblyVersion = assembly.GetName().Version?.ToString();
            var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            var loggingEnabled = Environment.GetEnvironmentVariable("ENABLE_LOGGING") ?? "false";

            ToolLoggingUtility.LogToolSuccess(_logger, toolName, methodName, "Server version retrieved successfully");
            return Task.FromResult<object>(new
            {
                success = true,
                message = "Server version retrieved successfully",
                version = new
                {
                    informationalVersion,
                    assemblyVersion,
                    fileVersion,
                    loggingEnabled,
                    serverType = "Embranch MCP Server",
                    mcpPort = _config.McpPort,
                    connectionTimeout = _config.ConnectionTimeoutSeconds,
                    bufferSize = _config.BufferSize,
                    maxRetries = _config.MaxRetries,
                    retryDelay = _config.RetryDelaySeconds
                }
            });
        }
        catch (Exception ex)
        {
            ToolLoggingUtility.LogToolException(_logger, toolName, methodName, ex);
            return Task.FromResult<object>(new
            {
                success = false,
                error = $"Failed to get server version: {ex.Message}"
            });
        }
    }
}