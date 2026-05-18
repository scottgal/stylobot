using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ThreatIntel.Providers;

/// <summary>
///     Tor exit node list. Free, ~1500 lines, refreshed every ~30 minutes by
///     check.torproject.org. Authoritative answer for "is this an exit relay?" -
///     better than inferring from datacenter ASN because legitimate exit nodes
///     can run from cloud or residential IPs.
///
///     <para>Format: plain text, one IPv4 (occasionally IPv6) per line. Comments
///     and blank lines are ignored. Provider name is <c>tor-exit</c>; classification
///     is <c>tor</c>.</para>
/// </summary>
internal sealed class TorExitProvider : ThreatIntelOfflineProviderBase
{
    private readonly TorExitOptions _options;

    public TorExitProvider(
        HttpClient http,
        IOptions<BotDetectionOptions> options,
        ILogger<TorExitProvider> logger)
        : base(http, logger)
    {
        _options = options.Value.ThreatIntel.Providers.TorExit;
    }

    public override string Name => "tor-exit";
    public override TimeSpan RefreshInterval => TimeSpan.FromMinutes(_options.RefreshMinutes);
    protected override string Classification => "tor";
    protected override double HitConfidence => 0.85;
    protected override IntelligenceSignalClass IntelClass => IntelligenceSignalClass.TorExit;
    protected override bool IsConfiguredEnabled => _options.Enabled;

    protected override async Task<IReadOnlyList<string>> FetchCidrsAsync(HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.Url))
            return Array.Empty<string>();
        var body = await http.GetStringAsync(_options.Url, ct);
        return ParseExitList(body).ToList();
    }

    /// <summary>
    ///     Tor exit list: one IP per line, optional comments. Each IP is treated as
    ///     a /32 (or /128 for IPv6) so the shared <see cref="IpCidrCache"/> handles
    ///     lookup without a separate code path. Lines that don't parse as IPs are
    ///     silently skipped (defensive; the feed shouldn't contain garbage).
    /// </summary>
    internal static IEnumerable<string> ParseExitList(string body)
    {
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // Some mirrors include trailing whitespace or annotations - take the
            // first token only.
            var space = line.IndexOf(' ');
            if (space > 0) line = line[..space];
            if (line.Length == 0) continue;
            // /32 for IPv4, /128 for IPv6 - simple presence-of-colon test.
            yield return line.Contains(':') ? line + "/128" : line + "/32";
        }
    }
}

/// <summary>Per-vendor config for the Tor exit provider.</summary>
public sealed class TorExitOptions
{
    /// <summary>Master enable flag. FOSS default: off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Exit-list URL. Override for internal mirrors.</summary>
    public string Url { get; set; } = "https://check.torproject.org/torbulkexitlist";

    /// <summary>Refresh cadence in minutes. The Tor project updates this list every ~30 minutes.</summary>
    public double RefreshMinutes { get; set; } = 30;
}
