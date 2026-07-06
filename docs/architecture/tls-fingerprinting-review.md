# TLS / JA3 fingerprinting — review + roadmap

**Status:** research / review (2026-07-06). Implementation items route to the FOSS detection pipeline.
**Scope:** how StyloBot captures and uses TLS transport fingerprinting today, how good it can get, and what we should (and should NOT) build.

## Executive summary

We are positioned to do transport fingerprinting best-in-class and are currently doing almost none of it. On direct-from-VPS prod the gateway is the **TLS-terminating edge** — it physically receives the raw ClientHello — but the code reads only Kestrel's `ITlsHandshakeFeature` (the *negotiated* cipher + TLS version), never the ClientHello itself. Real JA3/JA4 can therefore only arrive as `X-JA3-*` headers from an upstream proxy, and **there is no upstream on direct-VPS prod** → `TlsFingerprintAtom`, the cipher-subset check, and the version-delta check all run with **no input**. That is why the fingerprint-profile fields render blank: not a display bug, no data.

The single highest-value change is to **parse the ClientHello at the gateway** (a pre-TLS connection middleware, mirroring `ProxyProtocolConnectionMiddleware`), which turns the whole apparatus on.

## Current implementation (as of this review)

| Area | State | File |
|---|---|---|
| ClientHello capture | **Not done.** Only negotiated cipher + version via `ITlsHandshakeFeature`. | `Stylobot.Gateway/Configuration/KestrelTlsOptions.cs` |
| JA3 | Not computed — MD5 of an *upstream-supplied* JA3 string only. | `Orchestration/Atoms/TlsFingerprintAtom.cs` (`GetJa3Fingerprint`) |
| JA4 | Hash consumed from a header only; no computation. | same |
| Known-bot / known-browser sets | **Hardcoded MD5 lists** (7 bot + 4 browser) with a `TODO: move to YAML`. Anti-pattern (violates centroids-not-rules + JA3-is-dead). | `TlsFingerprintAtom` |
| Cipher-subset check | Good idea (anti-Multilogin/Kameleo: forked-Chromium cipher lists lag Chrome). Needs real capture. | `IsStrictCipherSubset` |
| Version-delta check | Good idea (UA claims Chrome 138, TLS matches 136 → bot). Needs real capture. | `TlsFingerprintAtom` |
| Identity vector | 4-dim `transport.tls_ja4` LSH slot in the 129-D vector; `transport.h2_settings_hash`, `transport.alpn`, `transport.tcp_p0f` slots exist but are **unpopulated** (no computation). | `IdentityVectorLayout.cs` |
| Environmental | `TunnelEnvironmentInspector` correctly classifies "behind proxy / no TLS metadata / partial". But the atom still treats missing-TLS as a weak bot signal (+0.05) rather than a clean data-quality "unavailable". | `Proxy/TunnelEnvironmentInspector.cs` |

## State of the art (2026, researched)

- **JA3 is dead for the browser that matters.** Chrome randomizes ClientHello extension order per connection (anti-ossification); JA3 is order-sensitive, so the same Chrome yields a moving hash.
- **JA3N (normalized JA3)** sorts the fields before hashing → stable against that randomization, while remaining an **open** (Salesforce lineage, non-patented) variant.
- **JA4** (FoxIO, 2023) is the modern successor: sorted, SHA-256, GREASE-ignoring, human-readable `a_b_c`, QUIC-aware; the `_ac` slice tracks actors across variation. Part of the **JA4+** suite (JA4 TLS, JA4H HTTP/2, JA4T TCP, JA4S server, JA4X cert).
- Fingerprint **alone** is bypassable — curl-impersonate / uTLS replicate the exact ClientHello (incl. GREASE + extension order) *and* the HTTP/2 Akamai fingerprint. The 2026 winning move is **cross-layer consistency** (TLS vs UA vs HTTP/2 vs TCP) + behavioral — which is the *shape* of our cipher-subset / version-delta checks.

Sources: FoxIO-LLC/ja4 (github.com/FoxIO-LLC/ja4), Stamus (ja3 fade / extension randomization), proxies.sx (ja3-vs-ja4-2026), browserless (bypassing), curl-impersonate (deepwiki 5.3).

## ⚠️ JA4+ is DEFERRED — future, patented code

**Decision (operator, 2026-07-06): we do NOT implement the JA4+ suite yet.** JA4+ (JA4/JA4H/JA4T/JA4S/JA4X) is **FoxIO License 1.1 — permissive for internal/academic use but NOT for monetization, and patent-pending.** StyloBot Commercial *sells* detection, so shipping JA4 in the paid product is a legal question, not just an engineering one. **Treat JA4+ as a future / commercial-exception item; do not write JA4-format code until the patent/license position is cleared.**

This is a scoping decision, not a capability loss: the *underlying* signals are reachable via **open** methods that don't touch the JA4 spec —
- TLS client → **JA3 / JA3N** (Salesforce, open). JA3N gives Chrome-stable fingerprinting without JA4.
- HTTP/2 client → the **Akamai HTTP/2 fingerprint** (SETTINGS · WINDOW_UPDATE · PRIORITY · pseudo-header order) — an open technique, not JA4H.
- TCP client → **p0f-style** TCP/IP fingerprint — open, not JA4T.

## Recommendations

### Viable now (open, non-patented) — hand to the FOSS agent as a spec
1. **Parse the ClientHello at the gateway.** Add a pre-TLS connection middleware alongside `ProxyProtocolConnectionMiddleware` that peeks the ClientHello TLS record, extracts version + cipher list + extensions + curves + EC-point-formats, and stashes them as signals. Note: .NET's `SslClientHelloInfo` exposes only SNI — the full ClientHello must be read from the raw bytes. **This is the unlock; everything else depends on it.**
2. **Compute JA3N** (sorted-field normalized JA3, open) as the primary TLS fingerprint — survives Chrome randomization. Keep raw JA3 only for legacy-corpus interop.
3. **Replace the hardcoded MD5 sets with a JA3N centroid/corpus** seeded from YAML + drift learning (this is `feedback_centroids_not_rules`; the existing `TODO` already concedes it).
4. **Populate the H2 (Akamai) + TCP (p0f) signals** that already have LSH slots — the cross-layer consistency layer that beats curl-impersonate. (Open methods; not JA4H/JA4T.)
5. **Signal-assay the absence** (`docs/architecture/signal-assay.md`): "TLS unavailable (environment)" = neutral/UNAVAILABLE, not +0.05 bot. The inspector already classifies it; wire the atom to it.

### Deferred (patented)
6. **JA4 / JA4+ suite** — future, gated on a legal read of the FoxIO patent + License 1.1 for a monetized product. Do not implement until cleared.

## Routing
Items 1–5 are FOSS detection-pipeline work — this doc is the spec; hand to the `foss-` agent. Item 6 is a commercial/legal decision, not engineering.
