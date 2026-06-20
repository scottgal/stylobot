using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    ///     Adds the <see cref="StyloBotGatewayLoggerProvider"/> to the logging pipeline.
    ///     The <paramref name="configuration"/> parameter is kept on the signature for
    ///     future extensions (per-category filter config, endpoint override).
    /// </summary>
    public static ILoggingBuilder AddStyloBotGateway(
        this ILoggingBuilder builder,
        IConfiguration configuration)
    {
        builder.Services.AddSingleton<StyloBotGatewayLoggerProvider>();
        builder.Services.AddSingleton<ILoggerProvider>(sp =>
            sp.GetRequiredService<StyloBotGatewayLoggerProvider>());

        return builder;
    }
}
