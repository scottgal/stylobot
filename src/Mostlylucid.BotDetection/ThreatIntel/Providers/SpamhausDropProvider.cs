using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ThreatIntel.Providers;

/// <summary>
///     Spamhaus DROP + EDROP. Free, low-volume CIDR lists of "allocated but
///     criminal" netblocks - perfect signal for "this whole /24 is bad news".
///     ~600 entries combined, refreshed every 12h by default.
///
///     <para>Both URLs are configurable so air-gapped deployments can point at
///     an internal mirror. Provider name is <c>spamhaus-drop</c>; classification
///     is <c>malicious</c>.</para>
/// </summary>
internal sealed class SpamhausDropProvider : ThreatIntelOfflineProviderBase
{
    private readonly SpamhausDropOptions _options;

    public SpamhausDropProvider(
        HttpClient http,
        IOptions<BotDetectionOptions> options,
        ILogger<SpamhausDropProvider> logger)
        : base(http, logger)
    {
        _options = options.Value.ThreatIntel.Providers.SpamhausDrop;
    }

    public override string Name => "spamhaus-drop";
    public override TimeSpan RefreshInterval => TimeSpan.FromHours(_options.RefreshHours);
    protected override string Classification => "malicious";
    protected override double HitConfidence => 0.95;
    protected override IntelligenceSignalClass IntelClass => IntelligenceSignalClass.SuspiciousNetworkRange;
    protected override bool IsConfiguredEnabled => _options.Enabled;

    protected override async Task<IReadOnlyList<string>> FetchCidrsAsync(HttpClient http, CancellationToken ct)
    {
        var all = new List<string>(capacity: 1024);
        if (!string.IsNullOrEmpty(_options.Url))
            all.AddRange(ParseDropFile(await http.GetStringAsync(_options.Url, ct)));
        if (!string.IsNullOrEmpty(_options.EdropUrl))
            all.AddRange(ParseDropFile(await http.GetStringAsync(_options.EdropUrl, ct)));
        return all;
    }

    /// <summary>
    ///     Spamhaus DROP / EDROP format: one CIDR per line, optional <c>;</c> comment
    ///     suffix carrying SBL reference (we discard the comment).
    ///     <code>
    ///     192.0.2.0/24 ; SBL12345
    ///     203.0.113.0/24 ; SBL67890
    ///     </code>
    /// </summary>
    internal static IEnumerable<string> ParseDropFile(string body)
    {
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            var semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi].Trim();
            if (line.Length > 0) yield return line;
        }
    }
}

/// <summary>
///     Per-vendor config for the Spamhaus DROP provider. URLs are overridable so
///     internal mirrors are first-class. Defaults are Spamhaus' canonical free
///     URLs as of 2026.
/// </summary>
public sealed class SpamhausDropOptions
{
    /// <summary>Master enable flag for this provider. FOSS default: off.</summary>
    public bool Enabled { get; set; }

    /// <summary>DROP list URL. Point at an internal mirror if outbound is restricted.</summary>
    public string Url { get; set; } = "https://www.spamhaus.org/drop/drop.txt";

    /// <summary>EDROP (extended) list URL. Set to empty string to skip EDROP.</summary>
    public string EdropUrl { get; set; } = "https://www.spamhaus.org/drop/edrop.txt";

    /// <summary>How often to refresh both lists. Spamhaus updates daily; 12h is conservative.</summary>
    public double RefreshHours { get; set; } = 12;
}
