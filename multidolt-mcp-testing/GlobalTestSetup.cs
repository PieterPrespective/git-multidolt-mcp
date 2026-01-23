using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Embranch.Services;

namespace EmbranchTesting;

/// <summary>
/// Global test setup that initializes PythonContext once for all tests in the assembly
/// This solves the issue where multiple test fixtures need PythonContext but it can only be initialized once
/// </summary>
[SetUpFixture]
public class GlobalTestSetup
{
    private static ILogger<GlobalTestSetup>? _logger;
    
    /// <summary>
    /// Run once before any tests in the assembly
    /// </summary>
    [OneTimeSetUp]
    public void RunBeforeAnyTests()
    {
        // Create logger for the global setup
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = loggerFactory.CreateLogger<GlobalTestSetup>();
        
        _logger.LogInformation("=== Starting Global Test Setup ===");
        
        // Initialize PythonContext once for all tests
        var pythonDll = PythonContextUtility.FindPythonDll(_logger);
        PythonContext.Initialize(_logger, pythonDll);
        
        _logger.LogInformation("PythonContext initialized for all tests");
    }
    
    /// <summary>
    /// Run once after all tests in the assembly
    /// </summary>
    [OneTimeTearDown]
    public void RunAfterAllTests()
    {
        _logger?.LogInformation("=== Starting Global Test Teardown ===");
        
        // Shutdown PythonContext after all tests
        if (PythonContext.IsInitialized)
        {
            PythonContext.Shutdown();
            _logger?.LogInformation("PythonContext shutdown completed");
        }
        
        _logger?.LogInformation("=== Global Test Teardown Complete ===");
    }
}