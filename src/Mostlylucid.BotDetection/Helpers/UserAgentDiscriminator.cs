using System.Reflection;
using System.Text.RegularExpressions;
using Mostlylucid.BotDetection.Definitions.VendorHomeHosts;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Pulls a per-deployment discriminator out of a friendly-bot User-Agent so the
///     dashboard can distinguish individual instances of clustering bots (the canonical
///     case: a fediverse link-preview stampede, where 50 Mastodon instances all hit the
///     same URL within a second).
///
///     <para>
///     Three structural classes of discriminator exist in the wild (see research notes
///     attached to commit history); this extractor handles the dominant one - the
///     <c>+https://hostname/</c> comment convention that traces to RFC 7231 §5.5.3
///     "product comment" syntax and has been adopted by every ActivityPub server
///     (Mastodon, Pleroma, Misskey, Calckey, Akkoma, Pixelfed, Lemmy, Friendica,
///     Hubzilla, PeerTube) plus several AI scrapers.
///     </para>
///
///     <para>
///     Returns null for vendor-home URLs (openai.com, facebook.com, etc.) where the
///     URL is a constant reference back to the bot's documentation rather than a
///     per-deployment identifier. The vendor-home skiplist is the load-bearing piece;
///     without it GPTBot, FacebookExternalHit, and similar would all be labelled with
///     their vendor's own domain and the discriminator would be meaningless.
///     </para>
/// </summary>
public static class UserAgentDiscriminator
{
    // RFC 7231 product-comment URL. Matches both `+https://host/` (mastodon, lemmy)
    // and `https://host/` without the plus (friendica). Stops at whitespace, `)`,
    // `;`, or `>` so the trailing comment punctuation isn't dragged in.
    private static readonly Regex UrlRegex = new(
        @"\+?(https?://[^\s);>]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Loaded once at first use from Definitions/VendorHomeHosts/vendor-home-hosts.yaml
    // (embedded resource). Editing the list is a YAML change, not a code change - the
    // YAML file's description block explains the membership criteria.
    private static readonly Lazy<HashSet<string>> VendorHomeHosts = new(LoadHosts);

    private static HashSet<string> LoadHosts()
    {
        var assembly = typeof(UserAgentDiscriminator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("vendor-home-hosts.yaml", StringComparison.Ordinal));

        if (resourceName is null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var file = YamlSerializer.Deserialize<VendorHomeHostsFile>(ms.ToArray());

        return file?.Hosts is { Count: > 0 }
            ? new HashSet<string>(file.Hosts, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Extracts the per-instance discriminator hostname from a User-Agent, or returns
    ///     null when the UA carries no URL, the URL points at a known vendor-home
    ///     reference, or the UA is empty.
    /// </summary>
    public static string? ExtractDiscriminator(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;

        var match = UrlRegex.Match(userAgent);
        if (!match.Success) return null;

        if (!Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return null;

        // Normalise: strip "www." prefix so "www.mastodon.social" matches "mastodon.social"
        // both in the skiplist and on the dashboard display.
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];

        // Drop the discriminator when the host is a known vendor home OR any subdomain of
        // one: Meta-ExternalAgent's UA carries developers.facebook.com, Googlebot's carries
        // developers.google.com, etc. Those are the vendor's own docs domains, not
        // per-deployment identifiers, so appending them just bloats the display name
        // ("Meta-ExternalAgent developers.facebook.com"). Suffix-match on a dot boundary so
        // facebook.com covers developers.facebook.com without matching evil-facebook.com.
        return IsVendorHomeOrSubdomain(host) ? null : host;
    }

    private static bool IsVendorHomeOrSubdomain(string host)
    {
        var set = VendorHomeHosts.Value;
        if (set.Contains(host)) return true;
        foreach (var vendor in set)
        {
            if (host.Length > vendor.Length + 1 &&
                host[host.Length - vendor.Length - 1] == '.' &&
                host.EndsWith(vendor, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
