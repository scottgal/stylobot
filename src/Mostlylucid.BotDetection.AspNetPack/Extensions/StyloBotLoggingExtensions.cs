using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.AspNetPack.Configuration;
using Mostlylucid.BotDetection.AspNetPack.Logging;

namespace Mostlylucid.BotDetection.AspNetPack.Extensions;

/// <summary>
///     <see cref="ILoggingBuilder"/> extensions for the ASP.NET Pack log sink.
///     Customers call <c>.AddStyloBotGateway(config)</c> inside
///     <c>builder.Logging.AddProvider(...)</c> or a <c>AddLogging(b => ...)</c> lambda.
///     Log sink is free with the pack umbrella; no sub-feature gate.
/// </summary>
public static class StyloBotLoggingExtensions
{
    /// <summary>
    ///     Wires the full FOSS log-sink stack into the host:
    ///     <list type="bullet">
    ///         <item><see cref="StyloBotGatewayLoggerProvider"/> registered as an
    ///             <see cref="ILoggerProvider"/> so emit on every category in
    ///             <see cref="LogSinkOptions.AllowedCategories"/> drops into the
    ///             provider's bounded channel.</item>
    ///         <item><see cref="StyloBotGatewayLogExporter"/> + a named HttpClient
    ///             pointed at the gateway's OTLP/HTTP logs endpoint
    ///             (<see cref="LogSinkOptions.GatewayEndpoint"/>).</item>
    ///         <item><see cref="LogSinkDrainer"/> subscribed to ScheduleCoordinator
    ///             Tick10s by its constructor.</item>
    ///         <item><see cref="LogSinkOptions"/> bound from
    ///             <c>BotDetection:AspNetPack:LogSink</c> (FOSS config root, no
    ///             Commercial: prefix).</item>
    ///     </list>
    ///     Hosts that already register their own instrumentation supply an
    ///     <see cref="ILogSinkInstrumentation"/> singleton BEFORE calling this
    ///     extension; the logger and drainer pick it up automatically. Hosts
    ///     that don't get a no-op default.
    /// </summary>
    public static ILoggingBuilder AddStyloBotGateway(
        this ILoggingBuilder builder,
        IConfiguration configuration)
    {
        builder.Services.Configure<LogSinkOptions>(
            configuration.GetSection("BotDetection:AspNetPack:LogSink"));

        builder.Services.AddSingleton<StyloBotGatewayLoggerProvider>();
        builder.Services.AddSingleton<ILoggerProvider>(sp =>
            sp.GetRequiredService<StyloBotGatewayLoggerProvider>());

        // Named HttpClient so DefaultHttpClientFactory's handler chain still
        // applies but a single exporter instance is shared for the process
        // lifetime. The exporter reads the gateway endpoint from options.
        builder.Services.AddHttpClient(nameof(StyloBotGatewayLogExporter));
        builder.Services.AddSingleton<StyloBotGatewayLogExporter>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LogSinkOptions>>();
            var http = sp.GetRequiredService<IHttpClientFactory>()
                         .CreateClient(nameof(StyloBotGatewayLogExporter));
            var log = sp.GetRequiredService<ILogger<StyloBotGatewayLogExporter>>();
            return new StyloBotGatewayLogExporter(http, opts, log);
        });

        // The drainer subscribes to ScheduleCoordinator Tick10s at construction.
        // It's eagerly resolved by the host's bootstrap step so the subscription
        // fires before the first tick. Listed here as a singleton; hosts that
        // resolve it from DI get the subscription wired.
        builder.Services.AddSingleton<LogSinkDrainer>();

        return builder;
    }
}
