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
    Providers:
      spamhaus-drop:
        Enabled: false            # offline, but still outbound - opt-in
        RefreshHours: 12
      tor-exit:
        Enabled: false
        RefreshMinutes: 30
      cisa-kev:
        Enabled: false
        RefreshHours: 1
      cloud-ranges:
        Enabled: false            # AWS + Azure + GCP + Cloudflare + Fastly bundled
        RefreshHours: 24
      greynoise:
        Enabled: false            # live: sends raw IP, requires opt-in
        ApiKey: ${GREYNOISE_API_KEY}
        QuotaPerDay: 1000
      abuseipdb:
        Enabled: false
        ApiKey: ${ABUSEIPDB_API_KEY}
        QuotaPerDay: 1000
```

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

## Open questions (call out before implementing)

1. **CVE extraction**: `threatintel.kev_match` requires `CveProbeContributor` to write a `cveprobe.cve` signal first. Confirm that contributor already extracts CVE ids from probe paths (e.g. log4shell CVE-2021-44228 from `/?x=${jndi:...}`). If not, the KEV provider is decoupled from CveProbe and only matches when an explicit CVE shows up in the URL.
2. **CISA KEV format**: ships as JSON Schema'd `vulnerabilities[]` array. Parsing via the existing `JsonSerializerContext` source generator pattern (AOT-clean).
3. **Cloud ranges aggregation**: each vendor has its own format (AWS `ip-ranges.json`, Azure XML, GCP `cloud.json`, Cloudflare text). Need per-vendor parsers under one provider, or one provider per vendor? Recommend one provider per vendor for refresh-independence and metric attribution, fronted by a `CloudRangesContributor` umbrella.
4. **Live provider response normalisation**: GreyNoise returns `classification: malicious|benign|unknown`, AbuseIPDB returns `abuseConfidenceScore: 0-100`. Map both into `ThreatIntelVerdict.Classification` + `.Confidence` via per-provider adapters; expose the raw response in `Metadata` for dashboards / debugging.
5. **Bootstrap latency**: first run downloads ~10MB across the offline pack. Block service startup, or background-fetch + return empty cache until ready? Recommend background-fetch with empty cache (matches existing `BotListFetcher` behaviour — fallback patterns until first sync completes).
