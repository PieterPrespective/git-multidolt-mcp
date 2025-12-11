using Microsoft.Extensions.Logging;
using DMMS.Services;

namespace DMMS;

class TestPython
{
    static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<TestPython>();
        
        try
        {
            logger.LogInformation("Testing PythonContext initialization...");
            
            // Find Python DLL
            var pythonDll = PythonContextUtility.FindPythonDll(logger);
            
            // Initialize PythonContext
            PythonContext.Initialize(logger, pythonDll);
            
            logger.LogInformation("PythonContext initialized successfully");
            
            // Test basic operation
            var result = await PythonContext.ExecuteAsync(() => 
            {
                return 42;
            }, operationName: "BasicTest");
            
            logger.LogInformation($"Basic test result: {result}");
            
            // Test string operation
            var stringResult = await PythonContext.ExecuteAsync(() => 
            {
                return "Hello from Python thread!";
            }, operationName: "StringTest");
            
            logger.LogInformation($"String test result: {stringResult}");
            
            logger.LogInformation("All tests passed!");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Test failed");
        }
        finally
        {
            if (PythonContext.IsInitialized)
            {
                PythonContext.Shutdown();
                logger.LogInformation("PythonContext shutdown complete");
            }
        }
    }
}