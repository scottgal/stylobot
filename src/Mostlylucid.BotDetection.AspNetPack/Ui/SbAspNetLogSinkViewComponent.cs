using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.AspNetPack.Configuration;
using Mostlylucid.BotDetection.AspNetPack.Logging;

namespace Mostlylucid.BotDetection.AspNetPack.Ui;

/// <summary>
///     "Log sink" sub-row. Renders current drainer state (queue depth, batch
///     size, flush tick, gateway endpoint) when the provider is registered.
///     <para>
///         <paramref name="provider"/> is optional: the drainer is only registered
///         in DI when the log sink is enabled, so the VC accepts null and returns
///         a zeroed view model.
///     </para>
///     <para>
///         The commercial gate check that was here has been removed in this FOSS
///         move. Task 1.2 will remove the LicensedAndEnabled flag entirely once
///         the commercial sub-feature flag is deleted.
///         // TODO removed in Task 1.2
///     </para>
/// </summary>
public sealed class SbAspNetLogSinkViewComponent : ViewComponent
{
    private readonly StyloBotGatewayLoggerProvider? _provider;
    private readonly IOptions<LogSinkOptions> _opts;

    public SbAspNetLogSinkViewComponent(
        IOptions<LogSinkOptions> opts,
        StyloBotGatewayLoggerProvider? provider = null)
    {
        _opts     = opts;
        _provider = provider;
    }

    public IViewComponentResult Invoke()
    {
        if (_provider is null)
        {
            return View(new LogSinkViewModel(
                LicensedAndEnabled: false,
                QueueDepth: 0,
                BatchSize: 0,
                FlushTick: "",
                GatewayUnreachableAge: TimeSpan.Zero,
                GatewayEndpoint: ""));
        }

        var sinkOpts = _opts.Value;
        var queueDepth = _provider.Channel.Reader.CanCount ? _provider.Channel.Reader.Count : 0L;

        return View(new LogSinkViewModel(
            LicensedAndEnabled: true,
            QueueDepth: queueDepth,
            BatchSize: sinkOpts.BatchSize,
            FlushTick: sinkOpts.FlushTick,
            GatewayUnreachableAge: TimeSpan.Zero,
            GatewayEndpoint: sinkOpts.GatewayEndpoint));
    }
}
