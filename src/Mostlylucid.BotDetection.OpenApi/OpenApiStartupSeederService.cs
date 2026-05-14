using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.OpenApi;

/// <summary>
///     Runs once at host startup. Loads every source listed in
///     <see cref="OpenApiSeedOptions.GetEffectiveSources"/> via the document loader and
///     populates <see cref="IOpenApiCatalog"/> for the rest of the host to consume.
/// </summary>
public sealed class OpenApiStartupSeederService(
    IOptions<OpenApiSeedOptions> options,
    IOpenApiDocumentLoader loader,
    IOpenApiCatalog catalog,
    ILogger<OpenApiStartupSeederService> logger)
    : IHostedService
{
    private readonly OpenApiSeedOptions _options = options.Value;

    public async Task StartAsync(CancellationToken ct)
    {
        var sources = _options.GetEffectiveSources();
        if (sources.Count == 0)
        {
            catalog.Replace(Array.Empty<LoadedOpenApiDocument>());
            logger.LogDebug("No OpenAPI sources configured; catalog remains empty.");
            return;
        }

        var loaded = new List<LoadedOpenApiDocument>(sources.Count);
        foreach (var source in sources)
        {
            try
            {
                var doc = await loader.LoadAsync(source, _options.ContinueOnFailure, ct);
                if (doc != null) loaded.Add(doc);
            }
            catch (Exception ex) when (_options.ContinueOnFailure)
            {
                logger.LogWarning(ex, "OpenAPI seed failed for {Source}; continuing", source);
            }
        }

        catalog.Replace(loaded);
        logger.LogInformation(
            "OpenAPI seeder loaded {DocCount} document(s); {OpCount} operations indexed",
            loaded.Count, catalog.OperationCount);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
