using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Embranch.Services;
using System.Threading.Tasks;

namespace EmbranchTesting.Services;

/// <summary>
/// Simple unit tests for the PythonContext class to verify basic functionality
/// </summary>
[TestFixture]
public class SimplePythonContextTests
{
    private ILogger<SimplePythonContextTests>? _logger;

    /// <summary>
    /// Set up test environment before each test
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        // Initialize logger for tests
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = loggerFactory.CreateLogger<SimplePythonContextTests>();
        
        // PythonContext is managed by GlobalTestSetup - just verify it's available
        if (!PythonContext.IsInitialized)
        {
            Assert.Fail("PythonContext should be initialized by GlobalTestSetup");
        }
    }

    /// <summary>
    /// Test that PythonContext initializes successfully
    /// </summary>
    [Test]
    public async Task Initialize_WithValidConfiguration_ShouldInitializeSuccessfully()
    {
        // Act - PythonContext is already initialized by GlobalTestSetup
        // We just verify it's working

        // Assert
        Assert.That(PythonContext.IsInitialized, Is.True);
    }

    /// <summary>
    /// Test that double initialization doesn't cause issues
    /// </summary>
    [Test]
    public async Task Initialize_CalledTwice_ShouldNotThrow()
    {
        // Arrange - PythonContext is already initialized by GlobalTestSetup

        // Act & Assert - Double initialization should not throw
        Assert.DoesNotThrow(() => PythonContext.Initialize(_logger));
        Assert.That(PythonContext.IsInitialized, Is.True);
    }

    /// <summary>
    /// Test simple Python operation execution
    /// </summary>
    [Test]
    public async Task ExecuteAsync_SimpleOperation_ShouldReturnCorrectResult()
    {
        // Arrange - PythonContext is already initialized by GlobalTestSetup
        const int expectedResult = 42;

        // Act
        var result = await PythonContext.ExecuteAsync(() => expectedResult, operationName: "SimpleTest");

        // Assert
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    /// <summary>
    /// Test Python string operation execution
    /// </summary>
    [Test]
    public async Task ExecuteAsync_StringOperation_ShouldReturnCorrectResult()
    {
        // Arrange - PythonContext is already initialized by GlobalTestSetup
        const string expectedResult = "Hello from Python!";

        // Act
        var result = await PythonContext.ExecuteAsync(() => expectedResult, operationName: "StringTest");

        // Assert
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    /// <summary>
    /// Test synchronous Execute method
    /// </summary>
    [Test]
    public async Task Execute_SimpleOperation_ShouldReturnCorrectResult()
    {
        // Arrange - PythonContext is already initialized by GlobalTestSetup
        const string expectedResult = "Sync result";

        // Act
        var result = PythonContext.Execute(() => expectedResult, operationName: "SyncTest");

        // Assert
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    /// <summary>
    /// Test PythonContextUtility.FindPythonDll method
    /// </summary>
    [Test]
    public void FindPythonDll_ShouldReturnValidPathOrNull()
    {
        // Act
        var pythonDll = PythonContextUtility.FindPythonDll(_logger);

        // Assert
        if (pythonDll != null)
        {
            Assert.That(File.Exists(pythonDll), Is.True, $"Python DLL path {pythonDll} should exist");
        }
        else
        {
            Assert.Pass("No Python DLL found, which is acceptable on some systems");
        }
    }

    ///// <summary>
    ///(We simply can't test this without messing up the global test setup)
    ///// Test that context can be safely shutdown and reinitialized
    ///// </summary>
    //[Test]
    //public async Task Shutdown_AndReinitialize_ShouldFailSinceNotSupported()
    //{
    //    // Arrange - PythonContext is already initialized by GlobalTestSetup
    //    await PythonContext.ExecuteAsync(() => 42, operationName: "BeforeShutdown");

    //    // This test verifies that shutdown/reinitialize is not supported
    //    // However, we can't actually test shutdown here because GlobalTestSetup manages the lifecycle
    //    // We'll just test that the context is initialized and working
    //    Assert.That(PythonContext.IsInitialized, Is.True);

    //    // Test that operations work normally
    //    var result = await PythonContext.ExecuteAsync(() => 24, operationName: "TestOperation");
    //    Assert.That(result, Is.EqualTo(24));

    //    /*
    //    PythonContext.Initialize(_logger);
    //    var result = await PythonContext.ExecuteAsync(() => 24, operationName: "AfterReinitialize");

    //    // Assert




    //    Assert.That(PythonContext.IsInitialized, Is.True);
    //    Assert.That(result, Is.EqualTo(24));
    //    */
    //}

    /// <summary>
    /// Test that the dedicated Python thread is different from the main thread
    /// </summary>
    [Test]
    public async Task ExecuteAsync_ThreadIsolation_ShouldExecuteOnDifferentThread()
    {
        // Arrange - PythonContext is already initialized by GlobalTestSetup
        var mainThreadId = Thread.CurrentThread.ManagedThreadId;

        // Act
        var pythonThreadId = await PythonContext.ExecuteAsync(() =>
        {
            return Thread.CurrentThread.ManagedThreadId;
        }, operationName: "ThreadIsolationTest");

        // Assert
        Assert.That(pythonThreadId, Is.Not.EqualTo(mainThreadId), 
            "Python operations should execute on a dedicated thread, not the main thread");
        
        _logger?.LogInformation($"Main thread ID: {mainThreadId}, Python thread ID: {pythonThreadId}");
    }
}