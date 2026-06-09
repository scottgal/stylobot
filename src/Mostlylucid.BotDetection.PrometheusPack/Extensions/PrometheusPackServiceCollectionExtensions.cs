using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;

namespace Mostlylucid.BotDetection.PrometheusPack.Extensions;

/// <summary>
///     DI extensions for the Prometheus pack. Exposes the in-gateway
///     <see cref="LocalMeterStream" /> and the viewer-host
///     <see cref="RemoteMeterStream" /> behind a single static class so each
///     mode has exactly one entry point.
/// </summary>
/// <remarks>
///     These two methods MUST NOT both be called against the same container --
///     they both register <see cref="IMeterStream" /> as a singleton and the
///     last registration wins. A future mode-switch extension picks one
///     based on configuration.
/// </remarks>
public static class PrometheusPackServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the in-gateway MeterListener-backed meter stream
    ///     (LFU summary atoms, signal emission hook). Idempotent default sink.
    ///     Call from the gateway composition root.
    /// </summary>
    public static IServiceCollection AddLocalMeterStream(
        this IServiceCollection services,
        Action<LocalMeterStreamOptions>? configure = null)
    {
        services.AddOptions<LocalMeterStreamOptions>();
        if (configure is not null) services.Configure(configure);

        services.TryAddSingleton<IMeterSignalSink, NullMeterSignalSink>();

        services.AddSingleton<LocalMeterStream>();
        services.AddSingleton<IMeterStream>(sp => sp.GetRequiredService<LocalMeterStream>());
        services.AddHostedService(sp => sp.GetRequiredService<LocalMeterStream>());

        return services;
    }

    /// <summary>
    ///     Registers <see cref="RemoteMeterStream" /> as a singleton
    ///     <see cref="IMeterStream" /> + <see cref="Microsoft.Extensions.Hosting.IHostedService" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This MUST NOT be combined with <see cref="AddLocalMeterStream" />
    ///         (which registers <c>LocalMeterStream</c>) or any other
    ///         <see cref="IMeterStream" /> registration in the same container.
    ///         Whichever is registered last wins, which is bug-prone; A6
    ///         introduces a clean mode-switch extension. This method exists as
    ///         a building block.
    ///     </para>
    ///     <para>
    ///         <see cref="IMeterSignalSink" /> is registered as
    ///         <see cref="NullMeterSignalSink" /> when no implementation is
    ///         already registered, so the polling loop has a non-null sink.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddRemoteMeterStream(
        this IServiceCollection services,
        Action<RemoteMeterStreamOptions>? configure = null)
    {
        services.AddOptions<RemoteMeterStreamOptions>()
            .BindConfiguration("BotDetection:RemoteMeterStream")
            .Configure(opts => configure?.Invoke(opts));

        services.TryAddSingleton<IMeterSignalSink, NullMeterSignalSink>();

        services.AddSingleton<RemoteMeterStream>();
        services.AddSingleton<IMeterStream>(sp => sp.GetRequiredService<RemoteMeterStream>());
        services.AddHostedService(sp => sp.GetRequiredService<RemoteMeterStream>());

        return services;
    }
}