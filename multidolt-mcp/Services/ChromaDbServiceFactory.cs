using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DMMS.Models;

namespace DMMS.Services;

/// <summary>
/// Factory for creating appropriate ChromaDB service based on configuration
/// </summary>
public class ChromaDbServiceFactory
{
    /// <summary>
    /// Creates ChromaDB service instance based on configuration mode
    /// </summary>
    public static IChromaDbService CreateService(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IOptions<ServerConfiguration>>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        return configuration.Value.ChromaMode.ToLowerInvariant() switch
        {
            "persistent" => new ChromaPersistentDbService(
                loggerFactory.CreateLogger<ChromaPersistentDbService>(), 
                configuration),
            "server" => serviceProvider.GetRequiredService<ChromaDbService>(),
            _ => throw new InvalidOperationException($"Unknown ChromaMode: {configuration.Value.ChromaMode}")
        };
    }
}