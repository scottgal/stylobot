using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     ONE key for the bot/human cut: <see cref="ClassificationOptions.BotFloor"/> is the value of
///     record, and the obsolete <c>BotDetection:BotThreshold</c> is a read-through to it
///     (<see cref="BotDetectionOptions.BotThreshold"/>), so the two cannot disagree at runtime and
///     "counted as a bot" cannot disagree with "acted on as a bot".
///     <para>
///     The read-through leaves exactly one case it cannot express: an operator who sets
///     <c>BotDetection:BotThreshold</c> explicitly to a different number, whose value is then
///     ignored. Ignoring it silently would be the worst outcome — a config knob that appears to
///     work and does not — so this logs it once, at boot, loudly, and names the winner.
///     </para>
///     <para>
///     Registered unconditionally by <c>AddBotDetection</c>: it is a single log line in the
///     no-divergence case and the only signal in the divergent one.
///     </para>
/// </summary>
public sealed class BotThresholdDivergenceWarningService : IHostedService
{
    private const double Tolerance = 1e-9;

    private readonly BotDetectionOptions _options;
    private readonly ILogger<BotThresholdDivergenceWarningService> _logger;

    public BotThresholdDivergenceWarningService(
        IOptions<BotDetectionOptions> options,
        ILogger<BotThresholdDivergenceWarningService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.ConfiguredBotThreshold is { } configured)
        {
            var winner = _options.Classification?.BotFloor ?? configured;
            if (Math.Abs(configured - winner) > Tolerance)
                _logger.LogWarning(
                    "BotDetection:BotThreshold is set to {ConfiguredBotThreshold} but the bot/human cut has ONE key: " +
                    "BotDetection:Classification:BotFloor = {BotFloor}. BotFloor WINS; the explicitly-set " +
                    "BotThreshold is IGNORED. Every surface (classification, dashboard, sessions, enforcement gates) " +
                    "uses {BotFloor} so they cannot disagree. Set BotDetection:Classification:BotFloor and delete " +
                    "BotDetection:BotThreshold.",
                    Format(configured), Format(winner), Format(winner));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string Format(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
}
