using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Embranch.Models;
using Embranch.Tools;
using EmbranchTesting.Utilities;

namespace EmbranchTesting.IntegrationTests;

/// <summary>
/// Integration tests for the DMMS MCP server
/// </summary>
[TestFixture]
public class ServerIntegrationTests
{
    private IHost? _host;
    private IServiceProvider? _serviceProvider;

    [SetUp]
    public async Task Setup()
    {
        var builder = Host.CreateApplicationBuilder();
        
        builder.Logging.ClearProviders();
        
        builder.Services.Configure<ServerConfiguration>(options =>
        {
            options.McpPort = 6501;
            options.ConnectionTimeoutSeconds = 86400.0;
            options.BufferSize = 16 * 1024 * 1024;
            options.MaxRetries = 3;
            options.RetryDelaySeconds = 1.0;
        });

        builder.Services.AddSingleton<GetServerVersionTool>();

        _host = builder.Build();
        _serviceProvider = _host.Services;
        await TestUtilities.ExecuteWithTimeoutAsync(
            _host.StartAsync(),
            operationName: "Start host");
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_host != null)
        {
            await TestUtilities.ExecuteWithTimeoutAsync(
                _host.StopAsync(),
                operationName: "Stop host");
            _host.Dispose();
        }
    }

    /// <summary>
    /// Tests that the server can start and resolve GetServerVersionTool service
    /// </summary>
    [Test]
    public void Server_CanResolveGetServerVersionTool()
    {
        var tool = _serviceProvider!.GetService<GetServerVersionTool>();
        
        Assert.That(tool, Is.Not.Null, "GetServerVersionTool should be resolvable from DI container");
    }

    /// <summary>
    /// Tests that GetServerVersionTool can be executed through dependency injection
    /// </summary>
    [Test]
    public async Task GetServerVersionTool_ExecutesSuccessfullyThroughDI()
    {
        var tool = _serviceProvider!.GetRequiredService<GetServerVersionTool>();
        
        var result = await TestUtilities.ExecuteWithTimeoutAsync(
            tool.GetServerVersion(),
            operationName: "Get server version");
        
        Assert.That(result, Is.Not.Null, "Tool should return a result");
        
        dynamic dynamicResult = result;
        Assert.That(dynamicResult.success, Is.True, "Tool should execute successfully");
    }

    /// <summary>
    /// Tests that server configuration is properly injected
    /// </summary>
    [Test]
    public async Task ServerConfiguration_IsProperlyInjected()
    {
        var tool = _serviceProvider!.GetRequiredService<GetServerVersionTool>();
        
        var result = await TestUtilities.ExecuteWithTimeoutAsync(
            tool.GetServerVersion(),
            operationName: "Get server version for configuration test");
        
        dynamic dynamicResult = result;
        var version = dynamicResult.version;
        
        Assert.That(version.mcpPort, Is.EqualTo(6501), "Custom MCP port should be reflected in version info");
    }
}