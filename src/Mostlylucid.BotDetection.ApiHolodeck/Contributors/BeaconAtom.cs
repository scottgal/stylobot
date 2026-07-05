using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.ApiHolodeck.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.ApiHolodeck.Contributors;

/// <summary>
///     Scans incoming requests for beacon canary values from previous holodeck
///     responses. On a match, raises <c>beacon.matched</c> +
///     <c>beacon.original_fingerprint</c> signals so downstream entity
///     resolution can link a rotated fingerprint back to its holodeck origin.
///     Native <see cref="IDetectorAtom"/> replacement for
///     <c>BeaconContributor</c>. Priority 2, Wave 0.
/// </summary>
public sealed class BeaconAtom : DetectorAtomBase
{
    private readonly BeaconStore _store;
    private readonly ILogger<BeaconAtom> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly int _canaryLength;

    public BeaconAtom(
        ILogger<BeaconAtom> logger,
        BeaconStore store,
        IOptions<HolodeckOptions> options,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "Beacon", category: "Beacon")
    {
        _logger = logger;
        _store = store;
        _httpContextAccessor = httpContextAccessor;
        _canaryLength = options.Value.BeaconCanaryLength;
    }

    public override int Priority => 2;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
            return None();

        try
        {
            var candidates = ExtractCandidates(context);
            if (candidates.Count == 0)
                return None();

            var matches = await _store.BatchLookupAsync(candidates);
            if (matches.Count == 0)
                return None();

            var (canary, record) = matches.First();

            var ageSeconds = (DateTime.UtcNow - record.CreatedAt).TotalSeconds
                .ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

            sink.Raise("beacon.matched:true", sessionId);
            sink.Raise($"beacon.original_fingerprint:{record.Fingerprint}", sessionId);
            sink.Raise($"beacon.canary:{canary}", sessionId);
            sink.Raise($"beacon.path:{record.Path}", sessionId);
            sink.Raise($"beacon.age_seconds:{ageSeconds}", sessionId);

            if (record.PackId != null)
                sink.Raise($"beacon.pack_id:{record.PackId}", sessionId);

            _logger.LogInformation(
                "Beacon matched: canary={Canary} links current request to fingerprint={OriginalFp} from path={Path}",
                canary, record.Fingerprint, record.Path);

            var fpPreview = record.Fingerprint[..Math.Min(8, record.Fingerprint.Length)];
            return Single(DetectionContribution.Info(
                Name,
                Category,
                $"Beacon match: canary {canary} -> fingerprint {fpPreview}"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Beacon scan failed");
            return None();
        }
    }

    private List<string> ExtractCandidates(HttpContext context)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, values) in context.Request.Query)
        foreach (var v in values)
            if (v != null && v.Length == _canaryLength)
                candidates.Add(v);

        var path = context.Request.Path.Value ?? "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (segment.Length == _canaryLength)
                candidates.Add(segment);

        foreach (var cookie in context.Request.Cookies)
            if (cookie.Value.Length == _canaryLength)
                candidates.Add(cookie.Value);

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
}