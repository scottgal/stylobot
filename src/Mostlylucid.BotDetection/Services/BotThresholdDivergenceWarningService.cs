using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.SiteProfiles;

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
    private readonly IServiceProvider? _services;

    public BotThresholdDivergenceWarningService(
        IOptions<BotDetectionOptions> options,
        ILogger<BotThresholdDivergenceWarningService> logger,
        IServiceProvider services)
    {
        _options = options.Value;
        _logger = logger;
        _services = services;
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

        ReportSiteOverrides();

        return Task.CompletedTask;
    }

    /// <summary>
    ///     The same rule one level down: a site profile may override the VALUE but must not introduce
    ///     a second KEY. A profile that names the obsolete field has it normalised into that site's
    ///     floor, and is reported here so the operator who wrote it learns why their site's number is
    ///     what it is instead of discovering it on a dashboard. Read defensively: a report must never
    ///     be able to fail the boot it is reporting on.
    /// </summary>
    private void ReportSiteOverrides()
    {
        ISiteProfileCatalog? catalog;
        try
        {
            catalog = _services?.GetService<ISiteProfileCatalog>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "site profile catalog unavailable for the one-key report");
            return;
        }

        if (catalog is null) return;

        var globalFloor = _options.Classification?.BotFloor;

        foreach (var (id, profile) in catalog.Profiles)
        {
            var thresholds = profile.Thresholds;
            if (thresholds is null) continue;

            if (thresholds.BotThreshold is { } obsolete)
            {
                if (thresholds.BotFloor is { } declared && Math.Abs(declared - obsolete) > Tolerance)
                    _logger.LogWarning(
                        "Site profile '{ProfileId}' declares BOTH BotThreshold ({Obsolete}) and BotFloor ({Floor}). " +
                        "The bot/human cut has ONE key: BotFloor wins and the obsolete value is ignored.",
                        id, Format(obsolete), Format(declared));
                else
                    _logger.LogWarning(
                        "Site profile '{ProfileId}' sets BotThreshold ({Obsolete}), which is obsolete: for this site it " +
                        "is normalised into Classification.BotFloor so the site keeps ONE cut. Rename it to BotFloor.",
                        id, Format(obsolete));
            }

            ReportFloorOverride(id, thresholds, globalFloor);
        }
    }

    /// <summary>
    ///     The same rule one level further in: a site may override the cut, but neither a malformed
    ///     value nor a divergence from the global one may pass unannounced.
    ///     <para>
    ///     Both halves matter and for different reasons. A value outside 0..1 is not a probability at
    ///     all — the resolver ignores it, and without this line the operator would see their number
    ///     vanish with no explanation. A well-formed divergence is a legitimate scoped decision, but
    ///     it moves CLASSIFICATION, not just refusal, so it is the one that most needs to be visible.
    ///     </para>
    ///     <para>
    ///     The DIRECTION is stated because it is the actionable half: "3 profiles diverge" tells an
    ///     operator nothing, while "widens this site's cut (0.40 &lt; global 0.70)" tells them exactly
    ///     what they did and lets them own it. Widening is deliberate and allowed — this reports, it
    ///     does not clamp.
    ///     </para>
    /// </summary>
    private void ReportFloorOverride(string profileId, SiteThresholdOverrides thresholds, double? globalFloor)
    {
        if (thresholds.BotFloor is not { } floor) return;

        if (floor < 0 || floor > 1 || double.IsNaN(floor))
        {
            _logger.LogWarning(
                "Site profile '{ProfileId}' sets BotFloor = {Floor}, which is not a probability. A bot/human cut " +
                "must be within 0..1, so this value is IGNORED and the inherited floor stands for that site.",
                profileId, Format(floor));
            return;
        }

        if (globalFloor is not { } global || Math.Abs(floor - global) <= Tolerance) return;

        var direction = floor < global
            ? $"WIDENS this site's cut ({Format(floor)} < global {Format(global)}) -- more traffic counts as a bot here"
            : $"NARROWS this site's cut ({Format(floor)} > global {Format(global)}) -- less traffic counts as a bot here";

        _logger.LogWarning(
            "Site profile '{ProfileId}' overrides the bot/human cut at {Floor}, which {Direction}. A scoped site " +
            "override is a legitimate decision and is NOT clamped; it is reported so that it is never silent.",
            profileId, Format(floor), direction);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string Format(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
}
