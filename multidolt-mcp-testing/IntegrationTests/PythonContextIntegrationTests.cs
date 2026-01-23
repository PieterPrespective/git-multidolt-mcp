using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Python.Runtime;
using Embranch.Services;

namespace EmbranchTesting.IntegrationTests;

/// <summary>
/// Integration tests for PythonContext that test actual Python.NET functionality
/// These tests require Python to be installed and available on the system
/// </summary>
[TestFixture]
public class PythonContextIntegrationTests
{
    private ILogger<PythonContextIntegrationTests>? _logger;

    /// <summary>
    /// Set up test environment before each test
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        // Initialize logger for tests
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = loggerFactory.CreateLogger<PythonContextIntegrationTests>();
        
        // PythonContext is managed by GlobalTestSetup - just verify it's available
        if (!PythonContext.IsInitialized)
        {
            Assert.Fail("PythonContext should be initialized by GlobalTestSetup");
        }
    }

    /// <summary>
    /// Test that Python operations actually execute within the GIL
    /// </summary>
    [Test]
    public async Task ExecuteAsync_PythonOperation_ShouldExecuteWithinGIL()
    {
        // Arrange - PythonContext is already initialized in OneTimeSetUp

        // Act
        var result = await PythonContext.ExecuteAsync(() =>
        {
            // This should execute within Py.GIL()
            using var _ = Py.GIL();
            dynamic sys = Py.Import("sys");
            string version = sys.version.ToString();
            return version;
        }, operationName: "PythonVersionTest");

        // Assert
        Assert.That(result, Is.Not.Null);

        //String doesn't always contain python!
        //Assert.That(result, Does.Contain("Python"), $"Expected Python version string, got: {result}");
        _logger?.LogInformation($"Python version: {result}");
    }

    /// <summary>
    /// Test importing Python modules and executing Python code
    /// </summary>
    [Test]
    public async Task ExecuteAsync_ImportModule_ShouldWorkCorrectly()
    {
        // Arrange - PythonContext is already initialized in OneTimeSetUp

        // Act
        var result = await PythonContext.ExecuteAsync(() =>
        {
            using var _ = Py.GIL();
            dynamic math = Py.Import("math");
            double piValue = math.pi;
            return piValue;
        }, operationName: "MathPiTest");

        // Assert
        Assert.That(result, Is.EqualTo(Math.PI).Within(0.0001));
    }

    /// <summary>
    /// Test executing Python code that creates objects
    /// </summary>
    [Test]
    public async Task ExecuteAsync_CreatePythonObjects_ShouldWorkCorrectly()
    {
        // Arrange - PythonContext is already initialized in OneTimeSetUp

        // Act
        var result = await PythonContext.ExecuteAsync(() =>
        {
            using var _ = Py.GIL();
            
            // Create a Python list and add items
            dynamic pyList = PythonEngine.Eval("[]");
            pyList.append("Hello");
            pyList.append("World");
            
            // Convert back to C#
            var csharpList = new List<string>();
            foreach (dynamic item in pyList)
            {
                csharpList.Add(item.ToString());
            }
            
            return csharpList;
        }, operationName: "CreateObjectsTest");

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0], Is.EqualTo("Hello"));
        Assert.That(result[1], Is.EqualTo("World"));
    }

    /// <summary>
    /// Test that multiple Python operations don't interfere with each other
    /// </summary>
    [Test]
    public async Task ExecuteAsync_MultiplePythonOperations_ShouldNotInterfere()
    {
        // Arrange - PythonContext is already initialized in OneTimeSetUp

        // Act - Execute multiple operations concurrently
        var tasks = new List<Task<string>>();
        for (int i = 0; i < 5; i++)
        {
            int capturedI = i;
            var task = PythonContext.ExecuteAsync(() =>
            {
                using var _ = Py.GIL();
                dynamic builtins = Py.Import("builtins");
                string result = builtins.str(capturedI).ToString();
                Thread.Sleep(10); // Small delay to test concurrency
                return result;
            }, operationName: $"ConcurrentPythonTest_{capturedI}");
            
            tasks.Add(task);
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.That(results, Has.Length.EqualTo(5));
        for (int i = 0; i < 5; i++)
        {
            Assert.That(results[i], Is.EqualTo(i.ToString()));
        }
    }

    /// <summary>
    /// Test ChromaDB import and basic functionality
    /// </summary>
    [Test]
    public async Task ExecuteAsync_ChromaDBImport_ShouldSucceed()
    {
        // Arrange - PythonContext is already initialized in OneTimeSetUp

        // Act & Assert
        try
        {
            var result = await PythonContext.ExecuteAsync(() =>
            {
                using var _ = Py.GIL();
                dynamic chromadb = Py.Import("chromadb");
                string version = chromadb.__version__.ToString();
                return version;
            }, timeoutMs: 15000, operationName: "ChromaDBImportTest");

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Not.Empty);
            _logger?.LogInformation($"ChromaDB version: {result}");
        }
        catch (PythonException ex)
        {
            if (ex.Message.Contains("No module named 'chromadb'"))
            {
                Assert.Ignore("ChromaDB not installed - skipping test");
            }
            throw;
        }
    }

    /// <summary>
    /// Test error handling in Python operations
    /// </summary>
    [Test]
    public void ExecuteAsync_PythonError_ShouldPropagateException()
    {
        // Arrange - PythonContext is already initialized in OneTimeSetUp

        // Act & Assert
        var ex = Assert.ThrowsAsync<PythonException>(async () =>
            await PythonContext.ExecuteAsync(() =>
            {
                using var _ = Py.GIL();
                // This should cause a Python error
                PythonEngine.Exec("1/0");  // Division by zero
                return "Should not reach here";
            }, operationName: "PythonErrorTest"));

        Assert.That(ex, Is.Not.Null);
        _logger?.LogInformation($"Caught expected Python exception: {ex.Message}");
    }

    /// <summary>
    /// Test garbage collection in Python context
    /// </summary>
    [Test]
    public async Task ExecuteAsync_GarbageCollection_ShouldNotCauseCrash()
    {
        // Arrange - PythonContext is already initialized in OneTimeSetUp

        // Act
        for (int i = 0; i < 10; i++)
        {
            await PythonContext.ExecuteAsync(() =>
            {
                using var _ = Py.GIL();
                
                // Create some objects that will need garbage collection
                dynamic tempList = PythonEngine.Eval("[]");
                for (int j = 0; j < 100; j++)
                {
                    tempList.append(j);
                }
                
                // Force garbage collection
                dynamic gc = Py.Import("gc");
                gc.collect();
                
                // Get the length of the Python list using len()
                dynamic builtins = Py.Import("builtins");
                return (int)builtins.len(tempList);
            }, operationName: $"GCTest_{i}");
        }

        // Assert - If we get here without crashing, the test passed
        Assert.Pass("Garbage collection test completed successfully");
    }

    /// <summary>
    /// Performance test to ensure operations complete in reasonable time
    /// </summary>
    [Test]
    public async Task ExecuteAsync_PerformanceTest_ShouldCompleteQuickly()
    {
        // Arrange - PythonContext is already initialized in OneTimeSetUp
        var stopwatch = Stopwatch.StartNew();

        // Act
        var tasks = new List<Task<int>>();
        for (int i = 0; i < 20; i++)
        {
            int capturedI = i;
            var task = PythonContext.ExecuteAsync(() =>
            {
                using var _ = Py.GIL();
                // Simple operation that should be fast
                dynamic builtins = Py.Import("builtins");
                int result = builtins.sum(new[] { 1, 2, 3, capturedI });
                return result;
            }, operationName: $"PerfTest_{capturedI}");
            
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Assert
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(5000), 
            $"20 operations took {stopwatch.ElapsedMilliseconds}ms, expected < 5000ms");
        
        _logger?.LogInformation($"20 Python operations completed in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Test that the dedicated Python thread is different from the main thread
    /// </summary>
    [Test]
    public async Task ExecuteAsync_ThreadIsolation_ShouldExecuteOnDifferentThread()
    {
        // Arrange - PythonContext is already initialized in OneTimeSetUp
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