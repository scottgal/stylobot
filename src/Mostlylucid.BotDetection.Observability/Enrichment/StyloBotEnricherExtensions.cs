using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Configuration;

namespace Mostlylucid.BotDetection.Observability.Enrichment;

/// <summary>
///     <see cref="LoggerEnrichmentConfiguration"/> helpers for wiring the
///     <see cref="StyloBotLogEnricher"/> from a customer's <c>UseSerilog(...)</c> call.
/// </summary>
public static class StyloBotEnricherExtensions
{
    /// <summary>
    ///     Adds the StyloBot Serilog enricher so every log line emitted during a
    ///     request gets <c>StyloBot_*</c> properties stamped onto it. Requires
    ///     <see cref="IHttpContextAccessor"/> in DI (the standard ASP.NET Core
    ///     registration); if it's missing a freshly-constructed accessor is used as
    ///     a last-resort fallback so the enricher still loads cleanly.
    /// </summary>
    public static LoggerConfiguration WithStyloBot(
        this LoggerEnrichmentConfiguration enrich,
        IServiceProvider services)
    {
        if (enrich is null) throw new ArgumentNullException(nameof(enrich));
        if (services is null) throw new ArgumentNullException(nameof(services));

        var accessor = services.GetService(typeof(IHttpContextAccessor)) as IHttpContextAccessor
                       ?? new HttpContextAccessor();
        return enrich.With(new StyloBotLogEnricher(accessor));
    }
}
