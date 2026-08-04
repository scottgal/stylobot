using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Version-age RankerAtom (per Taxonomy.md) — detects outdated browser/OS
///     combinations by delegating to the shared <see cref="VersionAgeDetector"/>.
///     Runs in Wave 1 after a UserAgent atom has raised the UA signal.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>VersionAgeContributor</c>. Same detector service, same manifest
///         name so appsettings overrides on
///         <c>BotDetection:Detectors:VersionAge:*</c> apply unchanged.
///     </para>
///     <para>
///         Takes <see cref="IHttpContextAccessor"/> because
///         <see cref="VersionAgeDetector.DetectAsync"/> accepts a raw
///         <see cref="HttpContext"/>.
///     </para>
/// </remarks>
public sealed class VersionAgeAtom : DetectorAtomBase
{
    private readonly ILogger<VersionAgeAtom> _logger;
    private readonly VersionAgeDetector _detector;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public VersionAgeAtom(
        ILogger<VersionAgeAtom> logger,        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "VersionAge", category: "VersionAge")
    {
        _logger = logger;
        _detector = new VersionAgeDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<VersionAgeDetector>.Instance, Microsoft.Extensions.Options.Options.Create(new Mostlylucid.BotDetection.Models.BotDetectionOptions()), null!);
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 25;

    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.UserAgent };

    private double VersionAgeWeight =>
        _configProvider.GetParameter(Name, "version_age_weight", 1.2);

    private double CurrentVersionConfidence =>
        _configProvider.GetParameter(Name, "current_version_confidence", -0.05);

    private double CurrentVersionWeight =>
        _configProvider.GetParameter(Name, "current_version_weight", 0.8);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
            return None();

        var contributions = new List<DetectionContribution>();

        try
        {
            var result = await _detector.DetectAsync(context, ct);

            sink.Raise(SignalKeys.VersionAgeAnalyzed, sessionId);

            if (result.Reasons.Count == 0)
            {
                // No version-age issues detected -- add negative signal (human indicator)
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = CurrentVersionConfidence,
                    Weight = CurrentVersionWeight,
                    Reason = "Browser/OS versions appear current"
                });
            }
            else
            {
                foreach (var reason in result.Reasons)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = reason.Category,
                        ConfidenceDelta = reason.ConfidenceImpact,
                        Weight = VersionAgeWeight,
                        Reason = reason.Detail,
                        BotType = result.BotType?.ToString()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VersionAge detection failed");
        }

        return contributions;
    }
}
