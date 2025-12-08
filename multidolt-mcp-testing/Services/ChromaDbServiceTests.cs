using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text;
using DMMS.Models;
using DMMS.Services;

namespace DMMSTesting.Services;

/// <summary>
/// Unit tests for ChromaDbService
/// </summary>
[TestFixture]
public class ChromaDbServiceTests
{
    private Mock<ILogger<ChromaDbService>> _mockLogger;
    private Mock<IOptions<ServerConfiguration>> _mockOptions;
    private ServerConfiguration _configuration;
    private Mock<HttpMessageHandler> _mockHttpHandler;
    private HttpClient _httpClient;
    private ChromaDbService _service;

    /// <summary>
    /// Sets up test environment before each test
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<ChromaDbService>>();
        _mockOptions = new Mock<IOptions<ServerConfiguration>>();
        _configuration = new ServerConfiguration
        {
            ChromaHost = "localhost",
            ChromaPort = 8000
        };
        _mockOptions.Setup(o => o.Value).Returns(_configuration);

        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpHandler.Object)
        {
            BaseAddress = new Uri($"http://{_configuration.ChromaHost}:{_configuration.ChromaPort}")
        };
        
        _service = new ChromaDbService(_mockLogger.Object, _mockOptions.Object, _httpClient);
    }

    /// <summary>
    /// Cleans up resources after each test
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
    }

    /// <summary>
    /// Tests successful listing of collections
    /// </summary>
    [Test]
    public async Task ListCollectionsAsync_WithValidResponse_ReturnsCollectionNames()
    {
        // Arrange
        var responseJson = @"[{""name"": ""collection1""}, {""name"": ""collection2""}]";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };

        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.ListCollectionsAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0], Is.EqualTo("collection1"));
        Assert.That(result[1], Is.EqualTo("collection2"));
    }

    /// <summary>
    /// Tests listing collections with limit and offset parameters
    /// </summary>
    [Test]
    public async Task ListCollectionsAsync_WithLimitAndOffset_ReturnsFilteredResults()
    {
        // Arrange
        var responseJson = @"[{""name"": ""collection1""}, {""name"": ""collection2""}, {""name"": ""collection3""}]";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };

        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.ListCollectionsAsync(limit: 1, offset: 1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0], Is.EqualTo("collection2"));
    }

    /// <summary>
    /// Tests successful creation of a collection
    /// </summary>
    [Test]
    public async Task CreateCollectionAsync_WithValidName_ReturnsTrue()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.CreateCollectionAsync("test_collection");

        // Assert
        Assert.That(result, Is.True);
    }

    /// <summary>
    /// Tests creation of a collection with metadata
    /// </summary>
    [Test]
    public async Task CreateCollectionAsync_WithMetadata_SendsCorrectPayload()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        HttpRequestMessage capturedRequest = null!;

        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(response);

        var metadata = new Dictionary<string, object>
        {
            { "description", "Test collection" },
            { "version", 1 }
        };

        // Act
        await _service.CreateCollectionAsync("test_collection", metadata);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(capturedRequest.RequestUri?.AbsolutePath, Does.EndWith("/api/v1/collections"));
    }

    /// <summary>
    /// Tests successful deletion of a collection
    /// </summary>
    [Test]
    public async Task DeleteCollectionAsync_WithValidName_ReturnsTrue()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.DeleteCollectionAsync("test_collection");

        // Assert
        Assert.That(result, Is.True);
    }

    /// <summary>
    /// Tests successful addition of documents
    /// </summary>
    [Test]
    public async Task AddDocumentsAsync_WithValidData_ReturnsTrue()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var documents = new List<string> { "Document 1", "Document 2" };
        var ids = new List<string> { "id1", "id2" };

        // Act
        var result = await _service.AddDocumentsAsync("test_collection", documents, ids);

        // Assert
        Assert.That(result, Is.True);
    }

    /// <summary>
    /// Tests HTTP error handling
    /// </summary>
    [Test]
    public void ListCollectionsAsync_WithHttpError_ThrowsException()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act & Assert
        Assert.ThrowsAsync<HttpRequestException>(async () => 
            await _service.ListCollectionsAsync());
    }

    /// <summary>
    /// Tests getting collection count
    /// </summary>
    [Test]
    public async Task GetCollectionCountAsync_WithValidResponse_ReturnsCount()
    {
        // Arrange
        var responseJson = @"{""count"": 42}";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };

        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.GetCollectionCountAsync("test_collection");

        // Assert
        Assert.That(result, Is.EqualTo(42));
    }

    /// <summary>
    /// Tests getting collection count when count property is missing
    /// </summary>
    [Test]
    public async Task GetCollectionCountAsync_WithMissingCountProperty_ReturnsZero()
    {
        // Arrange
        var responseJson = @"{""status"": ""ok""}";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };

        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.GetCollectionCountAsync("test_collection");

        // Assert
        Assert.That(result, Is.EqualTo(0));
    }
}