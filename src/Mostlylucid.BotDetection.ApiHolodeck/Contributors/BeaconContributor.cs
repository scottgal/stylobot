using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.ApiHolodeck.Services;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.ApiHolodeck.Contributors;

/// <summary>
///     Scans incoming requests for beacon canary values from previous holodeck responses.
///     When a canary matches, writes beacon.matched and beacon.original_fingerprint signals
///     for entity resolution to link rotated fingerprints.
///     Runs at priority 2 (before FastPathReputation at 3).
/// </summary>
public class BeaconContributor : ContributingDetectorBase
{
    private readonly BeaconStore _store;
    private readonly ILogger<BeaconContributor> _logger;
    private readonly int _canaryLength;

    public BeaconContributor(
        ILogger<BeaconContributor> logger,
        BeaconStore store,
        IOptions<HolodeckOptions> options)
    {
        _logger = logger;
        _store = store;
        _canaryLength = options.Value.BeaconCanaryLength;
    }

    public override string Name => "Beacon";
    public override int Priority => 2;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => [];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var candidates = ExtractCandidates(state);
            if (candidates.Count == 0)
                return [Neutral("No canary candidates in request")];

            var matches = await _store.BatchLookupAsync(candidates);
            if (matches.Count == 0)
                return [Neutral($"Scanned {candidates.Count} candidates, no match")];

            var (canary, record) = matches.First();

            state.WriteSignal("beacon.matched", true);
            state.WriteSignal("beacon.original_fingerprint", record.Fingerprint);
            state.WriteSignal("beacon.canary", canary);
            state.WriteSignal("beacon.path", record.Path);
            state.WriteSignal("beacon.age_seconds",
                (DateTime.UtcNow - record.CreatedAt).TotalSeconds);

            if (record.PackId != null)
                state.WriteSignal("beacon.pack_id", record.PackId);

            _logger.LogInformation(
                "Beacon matched: canary={Canary} links current request to fingerprint={OriginalFp} from path={Path}",
                canary, record.Fingerprint, record.Path);

            return [Neutral($"Beacon match: canary {canary} -> fingerprint {record.Fingerprint[..Math.Min(8, record.Fingerprint.Length)]}")];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Beacon scan failed");
            return [Neutral("Beacon scan error")];
        }
    }

    private List<string> ExtractCandidates(BlackboardState state)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var context = state.HttpContext;

        // Query string values
        foreach (var (_, values) in context.Request.Query)
        foreach (var v in values)
            if (v != null && v.Length == _canaryLength)
                candidates.Add(v);

        // Path segments
        var path = context.Request.Path.Value ?? "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (segment.Length == _canaryLength)
                candidates.Add(segment);

        // Cookie values
        foreach (var cookie in context.Request.Cookies)
            if (cookie.Value.Length == _canaryLength)
                candidates.Add(cookie.Value);

        // Referer query params
        var referer = context.Request.Headers.Referer.FirstOrDefault();
        if (referer != null)
        {
            var qIdx = referer.IndexOf('?');
            if (qIdx >= 0)
            {
                var qs = referer[(qIdx + 1)..];
                foreach (var pair in qs.Split('&'))
                {
                    var eqIdx = pair.IndexOf('=');
                    if (eqIdx >= 0)
                    {
                        var val = pair[(eqIdx + 1)..];
                        if (val.Length == _canaryLength)
                            candidates.Add(Uri.UnescapeDataString(val));
                    }
                }
            }
        }

        return candidates.ToList();
    }

    private DetectionContribution Neutral(string reason) =>
        new()
        {
            DetectorName = Name,
            Category = "Beacon",
            ConfidenceDelta = 0,
            Weight = 0,
            Reason = reason
        };
}
