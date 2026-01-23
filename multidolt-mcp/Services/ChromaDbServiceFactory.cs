using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Embranch.Models;

namespace Embranch.Services;

/// <summary>
/// Factory for creating appropriate ChromaDB service based on configuration
/// </summary>
public class ChromaDbServiceFactory
{
    /// <summary>
    /// Creates ChromaDB service instance based on configuration mode
    /// </summary>
    /// <remarks>
    /// IMPORTANT: Do NOT resolve IDocumentIdResolver here - it creates a circular dependency deadlock:
    /// IChromaDbService → IDocumentIdResolver → IChromaDbService
    /// The ChromaPythonService has a fallback CreateTemporaryResolver() for when idResolver is null.
    /// </remarks>
    public static IChromaDbService CreateService(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IOptions<ServerConfiguration>>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        // NOTE: idResolver is intentionally null to avoid circular dependency deadlock
        // ChromaPythonService.CreateTemporaryResolver() provides fallback functionality

        return configuration.Value.ChromaMode.ToLowerInvariant() switch
        {
            "persistent" => new ChromaPersistentDbService(
                loggerFactory.CreateLogger<ChromaPersistentDbService>(),
                configuration,
                idResolver: null),  // Avoid circular dependency
            "server" => serviceProvider.GetRequiredService<ChromaDbService>(),
            _ => throw new InvalidOperationException($"Unknown ChromaMode: {configuration.Value.ChromaMode}")
        };
    }
}