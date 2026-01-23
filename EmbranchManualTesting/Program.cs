using System;
using System.Threading.Tasks;

namespace EmbranchManualTesting;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("Embranch Testing Console");
        Console.WriteLine("===================");
        Console.WriteLine();
        Console.WriteLine("Available tests:");
        Console.WriteLine("1. VM RAG Test - Simple (Native Dolt Login)");
        Console.WriteLine("2. Sync Manager Manual Test (PP13-34) - Full Sync Validation");
        Console.WriteLine();
        Console.Write("Select test (1-2) or press Enter for credential test: ");
        
        var choice = Console.ReadLine()?.Trim();
        
        switch (choice)
        {
            case "1":
                await VMRAGTestSimple.Run();
                break;
                
            case "2":
                var syncTest = new SyncManagerManualTest();
                await syncTest.RunAsync();
                break;
            
        }
    }
}