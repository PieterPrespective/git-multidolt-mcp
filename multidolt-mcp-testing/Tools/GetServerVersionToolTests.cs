using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Embranch.Models;
using Embranch.Tools;
using System.Dynamic;

namespace EmbranchTesting.Tools;

/// <summary>
/// Unit tests for the GetServerVersionTool class
/// </summary>
[TestFixture]
public class GetServerVersionToolTests
{
    private Mock<ILogger<GetServerVersionTool>>? _mockLogger;
    private Mock<IOptions<ServerConfiguration>>? _mockConfig;
    private GetServerVersionTool? _tool;
    private ServerConfiguration? _configuration;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<GetServerVersionTool>>();
        _configuration = new ServerConfiguration
        {
            McpPort = 6500,
            ConnectionTimeoutSeconds = 86400.0,
            BufferSize = 16 * 1024 * 1024,
            MaxRetries = 3,
            RetryDelaySeconds = 1.0
        };
        _mockConfig = new Mock<IOptions<ServerConfiguration>>();
        _mockConfig.Setup(x => x.Value).Returns(_configuration);
        _tool = new GetServerVersionTool(_mockLogger.Object, _mockConfig.Object);
    }

    /// <summary>
    /// Tests that GetServerVersion returns a successful result with all expected version information
    /// </summary>
    [Test]
    public async Task GetServerVersion_ReturnsSuccessfulResult()
    {
        var result = await _tool!.GetServerVersion();

        Assert.That(result, Is.Not.Null);
        
        dynamic dynamicResult = result;
        
        Assert.That(dynamicResult.success, Is.True, "Result should indicate success");
        Assert.That(dynamicResult.message, Is.EqualTo("Server version retrieved successfully"));
        Assert.That(dynamicResult.version, Is.Not.Null, "Version object should not be null");
    }

    /// <summary>
    /// Tests that GetServerVersion includes all expected version fields
    /// </summary>
    [Test]
    public async Task GetServerVersion_IncludesAllVersionFields()
    {
        var result = await _tool!.GetServerVersion();
        
        dynamic dynamicResult = result;
        var version = dynamicResult.version;

        Assert.That(version.informationalVersion, Is.Not.Null.Or.Empty, "Informational version should be present");
        Assert.That(version.assemblyVersion, Is.Not.Null.Or.Empty, "Assembly version should be present");
        Assert.That(version.fileVersion, Is.Not.Null.Or.Empty, "File version should be present");
        Assert.That(version.loggingEnabled, Is.Not.Null, "Logging enabled flag should be present");
        Assert.That(version.serverType, Is.EqualTo("Embranch MCP Server"));
    }

    /// <summary>
    /// Tests that GetServerVersion includes correct configuration values
    /// </summary>
    [Test]
    public async Task GetServerVersion_IncludesCorrectConfiguration()
    {
        var result = await _tool!.GetServerVersion();
        
        dynamic dynamicResult = result;
        var version = dynamicResult.version;

        Assert.That(version.mcpPort, Is.EqualTo(_configuration!.McpPort), "MCP port should match configuration");
        Assert.That(version.connectionTimeout, Is.EqualTo(_configuration.ConnectionTimeoutSeconds), "Connection timeout should match configuration");
        Assert.That(version.bufferSize, Is.EqualTo(_configuration.BufferSize), "Buffer size should match configuration");
        Assert.That(version.maxRetries, Is.EqualTo(_configuration.MaxRetries), "Max retries should match configuration");
        Assert.That(version.retryDelay, Is.EqualTo(_configuration.RetryDelaySeconds), "Retry delay should match configuration");
    }

    /// <summary>
    /// Tests that GetServerVersion logs information message when called
    /// </summary>
    [Test]
    public async Task GetServerVersion_LogsInformationMessage()
    {
        await _tool!.GetServerVersion();

        _mockLogger!.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Getting server version information")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Tests that GetServerVersion handles custom configuration values correctly
    /// </summary>
    [Test]
    public async Task GetServerVersion_HandlesCustomConfiguration()
    {
        var customConfig = new ServerConfiguration
        {
            McpPort = 7500,
            ConnectionTimeoutSeconds = 3600.0,
            BufferSize = 8 * 1024 * 1024,
            MaxRetries = 5,
            RetryDelaySeconds = 2.0
        };

        var mockCustomConfig = new Mock<IOptions<ServerConfiguration>>();
        mockCustomConfig.Setup(x => x.Value).Returns(customConfig);

        var toolWithCustomConfig = new GetServerVersionTool(_mockLogger!.Object, mockCustomConfig.Object);
        var result = await toolWithCustomConfig.GetServerVersion();

        dynamic dynamicResult = result;
        var version = dynamicResult.version;

        Assert.That(version.mcpPort, Is.EqualTo(customConfig.McpPort));
        Assert.That(version.connectionTimeout, Is.EqualTo(customConfig.ConnectionTimeoutSeconds));
        Assert.That(version.bufferSize, Is.EqualTo(customConfig.BufferSize));
        Assert.That(version.maxRetries, Is.EqualTo(customConfig.MaxRetries));
        Assert.That(version.retryDelay, Is.EqualTo(customConfig.RetryDelaySeconds));
    }
}