using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Verifies that a domain claimed by a fediverse User-Agent (the
///     <c>+https://instance/</c> field in Mastodon / Pleroma / Misskey UAs) is a
///     real ActivityPub instance by performing a NodeInfo lookup.
///
///     NodeInfo is the protocol-defined discovery mechanism for the fediverse:
///     every conformant instance exposes <c>/.well-known/nodeinfo</c> with a
///     pointer to a machine-readable software descriptor (name, version). If a UA
///     claims to be Mastodon but the corresponding domain returns no NodeInfo (or
///     a NodeInfo naming non-fediverse software), the UA is treated as spoofed.
///
///     This is the cross-corroboration analogue to vendor-IP verification for
///     bots that intentionally don't have fixed IP ranges (every fediverse
///     instance runs on arbitrary cloud IPs). The returned signal feeds the
///     friendly-pin gate in <c>DetectionLedgerExtensions.DetermineRiskBand</c>.
/// </summary>
public interface IFediverseDomainVerifier
{
    /// <summary>
    ///     Returns <c>true</c> if the domain hosts a recognised fediverse stack
    ///     (Mastodon, Pleroma, Misskey, Akkoma, GoToSocial, Firefish, PeerTube,
    ///     Lemmy, etc.); <c>false</c> if the lookup ran and the domain does not
    ///     identify as fediverse software (likely spoofed UA); <c>null</c> when
    ///     verification was not attempted (SSRF guard rejected the domain or the
    ///     verifier is disabled).
    /// </summary>
    Task<bool?> VerifyAsync(string domain, CancellationToken cancellationToken = default);
}

internal sealed partial class FediverseDomainVerifier : IFediverseDomainVerifier
{
    // Software names returned by NodeInfo for legitimate fediverse stacks. Lowercased.
    // Source: https://nodeinfo.diaspora.software/ + observed deployments. Conservative
    // -- a name not on this list returns false (failed verification) rather than null,
    // so we don't accidentally pin "VeryHigh" risk traffic claiming to be Mastodon but
    // actually pointing at a random Express server.
    private static readonly HashSet<string> FediverseSoftware = new(StringComparer.OrdinalIgnoreCase)
    {
        "mastodon", "pleroma", "misskey", "akkoma", "firefish", "iceshrimp",
        "gotosocial", "peertube", "lemmy", "kbin", "mbin", "friendica",
        "hubzilla", "pixelfed", "writefreely", "bookwyrm", "funkwhale",
        "snac", "sharkey", "calckey", "owncast", "castopod"
    };

    // Strict DNS hostname (must have at least one dot; no IP literals; no trailing dot).
    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+$", RegexOptions.Compiled)]
    private static partial Regex StrictHostnameRegex();

    // Cache: 24h positive (instance won't change software overnight), 1h negative
    // (handle transient instance outages without burying them forever).
    private static readonly BoundedCache<string, bool> Cache =
        new(maxSize: 10_000, defaultTtl: TimeSpan.FromHours(24));
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(1);

    // One in-flight lookup per domain; prevents stampedes when N visitors all
    // share a link on mastodon.social and 50 instances probe us simultaneously.
    private static readonly ConcurrentInflight<string, bool?> InFlight = new();

    private readonly HttpClient _http;
    private readonly ILogger<FediverseDomainVerifier> _logger;

    public FediverseDomainVerifier(HttpClient http, ILogger<FediverseDomainVerifier> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<bool?> VerifyAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (!PassesSsrfGuard(domain, out var normalized))
            return null;

        if (Cache.TryGet(normalized, out var cached))
            return cached;

        return await InFlight.GetOrCreateAsync(normalized,
            async _ =>
            {
                try
                {
                    var verified = await LookupNodeInfoAsync(normalized, cancellationToken);
                    Cache.Set(normalized, verified, verified ? TimeSpan.FromHours(24) : NegativeTtl);
                    return verified;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return (bool?)null;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "NodeInfo lookup failed for {Domain}", normalized);
                    Cache.Set(normalized, false, NegativeTtl);
                    return false;
                }
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Rejects domains the verifier must never resolve: IP literals, link-local
    ///     names, .invalid/.localhost/.internal/.arpa, private/internal TLDs, and
    ///     malformed hostnames. Lowercases for cache-key normalisation.
    /// </summary>
    internal static bool PassesSsrfGuard(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var d = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (d.Length is 0 or > 253) return false;
        if (IPAddress.TryParse(d, out _)) return false;  // no IP literals
        if (d.EndsWith(".local") || d.EndsWith(".localhost") || d == "localhost") return false;
        if (d.EndsWith(".internal") || d.EndsWith(".invalid")) return false;
        if (d.EndsWith(".arpa") || d.EndsWith(".test") || d.EndsWith(".example")) return false;
        if (!StrictHostnameRegex().IsMatch(d)) return false;
        normalized = d;
        return true;
    }

    private async Task<bool> LookupNodeInfoAsync(string domain, CancellationToken ct)
    {
        // Step 1: discovery doc at /.well-known/nodeinfo lists the real NodeInfo URLs.
        using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        discoveryCts.CancelAfter(TimeSpan.FromSeconds(3));
        var discoveryUri = new Uri($"https://{domain}/.well-known/nodeinfo");
        var discovery = await FetchJsonAsync(discoveryUri, discoveryCts.Token);
        if (discovery is null) return false;

        // Discovery payload: { "links": [ { "rel": "...", "href": "..." } ] }
        if (!discovery.Value.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
            return false;

        // Pick a link whose href is on the SAME host as the claimed domain --
        // never follow a NodeInfo pointer to a third-party server.
        string? nodeInfoHref = null;
        foreach (var link in links.EnumerateArray())
        {
            if (!link.TryGetProperty("href", out var hrefEl) || hrefEl.ValueKind != JsonValueKind.String) continue;
            var href = hrefEl.GetString();
            if (string.IsNullOrEmpty(href)) continue;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var hrefUri)) continue;
            if (hrefUri.Scheme != "https") continue;
            if (!string.Equals(hrefUri.Host, domain, StringComparison.OrdinalIgnoreCase)) continue;
            nodeInfoHref = href;
            break;
        }
        if (nodeInfoHref is null) return false;

        // Step 2: real NodeInfo document with software.name field.
        using var docCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        docCts.CancelAfter(TimeSpan.FromSeconds(3));
        var doc = await FetchJsonAsync(new Uri(nodeInfoHref), docCts.Token);
        if (doc is null) return false;

        if (!doc.Value.TryGetProperty("software", out var software)) return false;
        if (!software.TryGetProperty("name", out var nameEl)) return false;
        if (nameEl.ValueKind != JsonValueKind.String) return false;

        var name = nameEl.GetString();
        return !string.IsNullOrEmpty(name) && FediverseSoftware.Contains(name);
    }

    private async Task<JsonElement?> FetchJsonAsync(Uri uri, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.UserAgent.ParseAdd("stylobot-nodeinfo-verifier/1.0");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        // Cap body at 32KB -- NodeInfo docs are <2KB in practice.
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var limited = new MaxBytesStream(stream, maxBytes: 32 * 1024);
        try
        {
            using var doc = await JsonDocument.ParseAsync(limited, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Wraps a stream so reads beyond <c>maxBytes</c> throw, capping body size
    ///     to prevent slow-loris / payload-bomb DoS via the verifier.
    /// </summary>
    private sealed class MaxBytesStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _read;

        public MaxBytesStream(Stream inner, long maxBytes) { _inner = inner; _maxBytes = maxBytes; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= _maxBytes) return 0;
            var take = (int)Math.Min(buffer.Length, _maxBytes - _read);
            var got = await _inner.ReadAsync(buffer[..take], cancellationToken).ConfigureAwait(false);
            _read += got;
            return got;
        }
    }
}

/// <summary>
///     De-duplicates concurrent calls for the same key so N simultaneous lookups
///     for the same domain produce ONE outbound HTTP request.
/// </summary>
internal sealed class ConcurrentInflight<TKey, TValue> where TKey : notnull
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _inflight = new();

    public Task<TValue> GetOrCreateAsync(TKey key, Func<TKey, Task<TValue>> factory, CancellationToken ct)
    {
        var lazy = _inflight.GetOrAdd(key, k =>
            new Lazy<Task<TValue>>(() => Run(k, factory)));
        return lazy.Value;

        async Task<TValue> Run(TKey k, Func<TKey, Task<TValue>> f)
        {
            try { return await f(k).ConfigureAwait(false); }
            finally { _inflight.TryRemove(k, out _); }
        }
    }
}
