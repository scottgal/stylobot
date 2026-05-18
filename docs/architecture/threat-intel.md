# Threat-intel enrichment

Spec for one abstraction that covers both **offline downloadable feeds** (Spamhaus DROP, Tor exit list, CISA KEV, cloud-provider IP ranges) and **live API providers** (GreyNoise, AbuseIPDB, Shodan). Bolts onto the existing `BackgroundEnrichmentService` so nothing blocks the &lt;1ms hot path.

## Goals

- Hot path reads cached verdicts only. Never call out from the detection pipeline.
- Same shape for offline + live providers; FOSS ships the abstraction + the offline pack, commercial bundles managed live providers.
- Opt-in per provider; respects the zero-PII posture (sending raw IPs externally is off by default).
- AOT-clean: typed signals, no reflective JSON for live response shapes.

## Core types

```csharp
// What kind of thing we're asking about. Most providers are IP-keyed; CISA KEV is
// CVE-keyed; future TLS-fingerprint feeds will be JA3/JA4-keyed.
public enum ThreatSubjectType { Ip, Asn, Domain, Cidr, Cve, JA3, JA4 }
public sealed record ThreatSubject(ThreatSubjectType Type, string Value);

// Lookup mode is the load-bearing distinction. Offline providers are pure cache
// hits after sync; live providers can either be cache-hit (already enriched
// background) or cache-miss (→ pipeline ignores them this request, the
// BackgroundEnrichmentService kicks off a fetch for next time).
public enum ThreatIntelMode { Offline, Live }

public sealed record ThreatIntelVerdict
{
    public required string Provider { get; init; }       // "spamhaus-drop", "greynoise", ...
    public required string Classification { get; init; } // "malicious" | "benign" | "noise" | "scanner" | "tor" | "cloud" | "kev"
    public double Confidence { get; init; }              // 0..1, provider-normalised
    public DateTime ObservedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }            // cache validity; past = ignore
    public IReadOnlyDictionary<string, string>? Metadata { get; init; } // ASN, country, last-reported, etc.
}

public interface IThreatIntelProvider
{
    string Name { get; }
    ThreatIntelMode Mode { get; }
    IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; }

    // Hot-path safe. Returns null if not cached; NEVER calls out synchronously.
    ThreatIntelVerdict? TryLookup(ThreatSubject subject);

    // Background. Offline providers refresh the in-memory cache from upstream;
    // live providers fetch + cache a single subject. Idempotent / re-entrant.
    Task RefreshAsync(ThreatSubject? subject, CancellationToken ct);
}

public interface IThreatIntelCoordinator
{
    // Aggregates TryLookup across all enabled providers. Sync, allocation-light.
    IReadOnlyList<ThreatIntelVerdict> Lookup(ThreatSubject subject);

    // Enqueues a background enrich for live providers + triggers offline refresh
    // if any provider's cache is past its staleness threshold.
    Task EnrichAsync(ThreatSubject subject, CancellationToken ct);
}
```

## Detector

`ThreatIntelContributor` runs at priority 7 (after `IpContributor` at 4 gives us ASN, before `Heuristic`). Reads via `IThreatIntelCoordinator.Lookup`, writes blackboard signals:

| Signal | Type | Meaning |
|---|---|---|
| `threatintel.score` | double | max Confidence across all hit providers |
| `threatintel.classifications` | string | semicolon-joined unique classifications |
| `threatintel.providers_hit` | string | semicolon-joined provider names |
| `threatintel.<provider>` | string | per-provider classification (e.g. `threatintel.greynoise = "scanner"`) |
| `threatintel.tor` | bool | shortcut: any provider classified as tor |
| `threatintel.kev_match` | string | CVE id when CveProbe extracted a CVE and KEV matched |

The Heuristic + AI contributors downstream consume `threatintel.score` as a feature.

## Background enrichment

Existing `BackgroundEnrichmentService` gains a new responsibility: when a fingerprint allocates for the first time AND live providers are configured, post `IThreatIntelCoordinator.EnrichAsync(new ThreatSubject(Ip, requestIp))`. Result lands in the cache by the next request from that fingerprint. Coalesces duplicate in-flight requests per (provider, subject).

## Offline feed lifecycle

Each offline provider derives from `ThreatIntelOfflineProviderBase`:

```csharp
protected abstract Task<byte[]> FetchAsync(HttpClient http, CancellationToken ct);
protected abstract IThreatIntelCache Parse(byte[] body);
protected abstract TimeSpan RefreshInterval { get; }
```

`IThreatIntelCache` is a tiny interface with a typed `TryLookup(subject)` returning `ThreatIntelVerdict?`. Per-provider implementations choose the right data structure:

- IP CIDR feeds → radix tree (existing `IpRangeRadixTree` in `Mostlylucid.GeoDetection`)
- CVE / fingerprint exact-match feeds → `FrozenSet<string>`
- Tor exit list → `FrozenSet<IPAddress>`

A hosted refresh service ticks each provider on its interval; on successful fetch+parse, the cache is atomic-swapped. Failed refresh keeps the previous cache (staleness logged + exposed via `threatintel.<provider>_age_hours` for the dashboard).

## Configuration

**FOSS default: every provider disabled.** The master switch is off and each provider's `Enabled` flag is off too. Operators opt in per-provider. Reasons:

- Even offline providers fetch from external URLs (Spamhaus, Tor exit list, CISA, AWS/Azure/GCP/Cloudflare). That's outbound traffic some deployments don't want (air-gapped installs, audit-restricted networks).
- Matches the zero-PII / no-surprise-network-traffic posture already documented in the README.
- One config knob to flip when an operator decides they want intel, instead of "this version started phoning home".

Commercial may ship a curated default-on bundle, gated on its own opt-in.

```yaml
BotDetection:
  ThreatIntel:
    Enabled: false                # master switch; FOSS default off
    PrivacyMode: ip               # ip | redacted-ip | hash | offline-only
    BlockStartupOnFirstFetch: true     # when Enabled=true, wait for first fetch of each provider
    StartupFetchTimeoutSeconds: 60     # per-provider; fail-fast on slow upstream
    StaggerWindowSeconds: 300          # background refreshes spread across this window
    Providers:
      spamhaus-drop:
        Enabled: false            # offline, but still outbound - opt-in
        Url: https://www.spamhaus.org/drop/drop.txt
        EdropUrl: https://www.spamhaus.org/drop/edrop.txt   # change to an internal mirror if required
        RefreshHours: 12
      tor-exit:
        Enabled: false
        Url: https://check.torproject.org/torbulkexitlist
        RefreshMinutes: 30
      cisa-kev:
        Enabled: false
        Url: https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json
        RefreshHours: 1
      cloud-ranges:
        Enabled: false
        RefreshHours: 24
        Sources:                  # one provider, per-vendor sources + URLs
          aws:
            Enabled: true
            Url: https://ip-ranges.amazonaws.com/ip-ranges.json
            Format: aws-json
          azure:
            Enabled: true
            Url: https://download.microsoft.com/download/7/1/D/71D86715-5596-4529-9B13-DA13A5DE5B63/ServiceTags_Public_20260518.json
            Format: azure-json
          gcp:
            Enabled: true
            Url: https://www.gstatic.com/ipranges/cloud.json
            Format: gcp-json
          cloudflare:
            Enabled: true
            Url: https://www.cloudflare.com/ips-v4
            Format: cidr-text
          fastly:
            Enabled: true
            Url: https://api.fastly.com/public-ip-list
            Format: fastly-json
      greynoise:
        Enabled: false            # live: sends raw IP, requires opt-in
        ApiKey: ${GREYNOISE_API_KEY}
        Url: https://api.greynoise.io/v3/community
        QuotaPerDay: 1000
      abuseipdb:
        Enabled: false
        ApiKey: ${ABUSEIPDB_API_KEY}
        Url: https://api.abuseipdb.com/api/v2/check
        QuotaPerDay: 1000
```

**Every URL is overridable.** Air-gapped / audit-restricted deployments can point each provider at an internal mirror. The defaults are the vendors' own canonical URLs.

Coordinator behaviour when `ThreatIntel:Enabled = false`: the contributor short-circuits (`triggers` evaluate but `TryLookup` returns empty); offline feed refresh services don't start; no HTTP clients registered; the dashboard's threat-intel tab shows a one-line "Threat intel disabled - enable in config to start".

`PrivacyMode` options for live providers:

- `ip` — send the raw IP (provider expectation)
- `redacted-ip` — /24 truncation for IPv4 (preserves netblock signal, fuzzes the host)
- `hash` — HMAC-SHA256 the IP (only useful for providers that accept hashed lookups; rare)
- `offline-only` — never call live providers; the live-provider block silently no-ops

## Quota + circuit breaker (live providers only)

`ThreatIntelLiveProviderBase` enforces:

- Daily quota per provider; once exhausted, `TryLookup` returns cached-only and `EnrichAsync` no-ops until midnight UTC
- Circuit breaker: if error rate over the trailing 1-minute window exceeds 20%, pause for 5 min
- Per-subject in-flight coalescing: two concurrent `EnrichAsync(sameIp)` calls share one HTTP request

## FOSS vs commercial split

**FOSS** ships:

- `IThreatIntelProvider`, `IThreatIntelCoordinator`, `ThreatIntelContributor`
- `ThreatIntelOfflineProviderBase` + the offline pack:
  - `SpamhausDropProvider` (DROP + EDROP combined; ~3MB total)
  - `TorExitProvider`
  - `CisaKevProvider`
  - `CloudRangesProvider` (AWS / Azure / GCP / Cloudflare / Fastly aggregated)
- `ThreatIntelLiveProviderBase` (the abstraction only)
- Hook for users to register their own providers via DI

**Commercial** adds:

- Managed `GreyNoise` / `AbuseIPDB` / `Shodan` providers with bundled API keys (no per-customer key juggling)
- Cross-deployment reputation cache: verdicts seen at any commercial customer pre-warm everyone's lookup
- Passive DNS provider (CIRCL pdns / Farsight DNSDB)
- CT log subscription per customer domain
- Threat-intel dashboard tab

## File layout

```
src/Mostlylucid.BotDetection/ThreatIntel/
  IThreatIntelProvider.cs
  IThreatIntelCoordinator.cs
  ThreatIntelCoordinator.cs
  ThreatIntelVerdict.cs
  ThreatIntelOptions.cs
  IThreatIntelCache.cs
  ThreatIntelOfflineProviderBase.cs
  ThreatIntelLiveProviderBase.cs
  Providers/
    SpamhausDropProvider.cs
    TorExitProvider.cs
    CisaKevProvider.cs
    CloudRangesProvider.cs
src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/
  ThreatIntelContributor.cs
src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/
  threatintel.detector.yaml
src/Mostlylucid.BotDetection.Test/ThreatIntel/
  ThreatIntelCoordinatorTests.cs
  SpamhausDropProviderTests.cs
  TorExitProviderTests.cs
  CisaKevProviderTests.cs
  ThreatIntelContributorTests.cs
```

## Detector manifest

```yaml
name: threatintel
priority: 7        # after IpContributor (4); before Heuristic (10)
enabled: true
scope: request
taxonomy:
  family: enrichment
  intent: identity
triggers:
  - { signal: ip.address_hash }
defaults:
  parameters:
    threat_score_weight: 0.6      # how much threatintel.score feeds into BotProbability
    kev_match_threat_floor: 0.7   # CVE probe + KEV match → threat score floor
emits:
  - threatintel.score
  - threatintel.classifications
  - threatintel.providers_hit
  - threatintel.tor
  - threatintel.kev_match
```

## Resolved design questions

### CVE extraction for KEV matching

Two existing signals feed the KEV provider; no new CVE-extraction work needed:

- `cve.probe.id` (e.g. `"CVE-2024-6386"`) — written by `CveProbeContributor` when a request matches a simulation-pack honeypot path. High confidence: the requester explicitly probed a known CVE path.
- `cve.top_advisory_id` (e.g. `"CVE-2026-1234"` or `"GHSA-xxxx"`) — written by `CveFingerprintContributor` when the session shape matches a CVE-derived fingerprint. Lower confidence but earlier signal.

KEV provider does an exact lookup against either signal; on match, sets `threatintel.kev_match = <id>` and bumps `threatintel.score` to at least `kev_match_threat_floor` (default 0.7). GHSA-prefixed advisory ids are skipped (KEV is CVE-only).

### Cloud-ranges provider: one for all, per-vendor config

Single `CloudRangesProvider` with a per-vendor source list. Each vendor entry carries its own URL, parser kind, and enable flag. URLs are configurable so an operator running an internal mirror can point at it instead of fetching from the vendor directly.

```yaml
ThreatIntel:
  Providers:
    cloud-ranges:
      Enabled: false
      RefreshHours: 24
      Sources:
        aws:
          Enabled: true
          Url: https://ip-ranges.amazonaws.com/ip-ranges.json
          Format: aws-json
        azure:
          Enabled: true
          Url: https://download.microsoft.com/download/7/1/D/71D86715-5596-4529-9B13-DA13A5DE5B63/ServiceTags_Public_20260518.json
          Format: azure-json
        gcp:
          Enabled: true
          Url: https://www.gstatic.com/ipranges/cloud.json
          Format: gcp-json
        cloudflare:
          Enabled: true
          Url: https://www.cloudflare.com/ips-v4
          Format: cidr-text
        fastly:
          Enabled: true
          Url: https://api.fastly.com/public-ip-list
          Format: fastly-json
```

`Format` selects the right parser internally; one provider class dispatches to the right format-handler per source. Lookup returns `cloud:<vendor>` (e.g. `cloud:aws`) as the classification so downstream signals can distinguish.

Same shape applies to other multi-source providers (Spamhaus has DROP + EDROP URLs that should be configurable too).

### Live-provider response shape (the "Huh?" question)

Each vendor returns data in its own format — GreyNoise sends `{classification: "malicious"|"benign"|"unknown", riot: bool, noise: bool}`, AbuseIPDB sends `{abuseConfidenceScore: 0-100, totalReports: int, ...}`, Shodan sends ports + tags. We need one common `ThreatIntelVerdict` shape regardless of provider.

Resolution: each live provider class owns its own adapter — fetch, parse, project into the common verdict. Raw response goes into `ThreatIntelVerdict.Metadata` as string key/values so dashboards / debug views can show the vendor-native fields without the provider abstraction having to know about them.

Worked example:

```csharp
// GreyNoise adapter projection
var raw = await JsonSerializer.DeserializeAsync<GreyNoiseResponse>(stream, ...);
return new ThreatIntelVerdict {
    Provider = "greynoise",
    Classification = raw.Classification switch {     // their term → our term
        "malicious" => "malicious",
        "benign"    => "benign",
        _           => "noise"                       // unknown + RIOT both map here
    },
    Confidence = raw.Classification == "malicious" ? 0.9 :
                 raw.Classification == "benign"    ? 0.1 : 0.5,
    Metadata = new Dictionary<string, string> {
        ["riot"] = raw.Riot.ToString(),
        ["noise"] = raw.Noise.ToString(),
        ["last_seen"] = raw.LastSeen,
        // ... raw fields preserved for the dashboard
    }
};
```

### Bootstrap behaviour

When `ThreatIntel:Enabled = true` AND at least one provider is enabled: **block startup until the first refresh of each enabled provider completes** (with a configurable per-provider timeout). Operator explicitly opted in, so a partial / empty intel cache at request 1 would be a footgun — they'd silently get worse detection than they expect.

After bootstrap, refreshes run on a **staggered** schedule to avoid concurrent fetch spikes. Each provider's first post-bootstrap refresh fires at `now + Random(0..StaggerWindow)` then ticks at `RefreshInterval` from there. Default stagger window is 5 minutes, configurable.

```yaml
ThreatIntel:
  BlockStartupOnFirstFetch: true            # FOSS default when ThreatIntel:Enabled = true
  StartupFetchTimeoutSeconds: 60            # per provider; fail-fast on slow upstream
  StaggerWindowSeconds: 300                 # background refresh spreads across this window
  Providers:
    spamhaus-drop:
      Enabled: false
      Url: https://www.spamhaus.org/drop/drop.txt
      EdropUrl: https://www.spamhaus.org/drop/edrop.txt
      RefreshHours: 12
```

Failure modes:

- **Startup-fetch timeout**: log fatal + exit if `BlockStartupOnFirstFetch` is true (the operator asked for intel and we can't deliver it; don't lie about coverage). Override with `BlockStartupOnFirstFetch: false` to start anyway and let the cache populate eventually.
- **Background-refresh failure**: log warning, keep previous cache, expose age via `threatintel.<provider>_age_hours` signal so the dashboard can flag stale intel.

### CISA KEV format

JSON at `https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json`. Shape:

```json
{
  "title": "...",
  "catalogVersion": "2026.05.18",
  "dateReleased": "2026-05-18T...",
  "count": 1234,
  "vulnerabilities": [
    {
      "cveID": "CVE-2021-44228",
      "vendorProject": "Apache",
      "product": "Log4j2",
      "vulnerabilityName": "Apache Log4j2 Remote Code Execution Vulnerability",
      "dateAdded": "2021-12-10",
      "shortDescription": "...",
      "requiredAction": "...",
      "dueDate": "2021-12-24",
      "knownRansomwareCampaignUse": "Known",
      "notes": "...",
      "cwes": ["CWE-20", "CWE-400", "CWE-502"]
    }
  ]
}
```

Parser: source-generated `JsonSerializerContext` over `KevCatalog` + `KevVulnerability` records. The lookup cache is a `FrozenDictionary<string, KevVulnerability>` keyed on `cveID` (uppercase normalised). `knownRansomwareCampaignUse == "Known"` lifts the verdict confidence from 0.7 to 0.95.
