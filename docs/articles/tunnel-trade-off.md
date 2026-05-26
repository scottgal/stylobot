# The Tunnel Trade-off: What StyloBot Can (and Can't) See Behind a Reverse Proxy

*StyloBot still works behind Cloudflare Tunnel, Caddy, nginx, or any reverse proxy. It just sees less. This is which signals survive the tunnel hop, which die at the edge, and what to do about it.*

<!--category-- Architecture, Operations, StyloBot, TLS -->

# Why I run StyloBot behind a tunnel anyway

`www.stylobot.net` runs behind Cloudflare Tunnel because when the original colo box died, cloudflared on a small VPS was the path of least resistance: no public IP exposure, no firewall rules, no inbound port to defend, no DNS dance. That tradeoff is the right one for a small site and I'd make it again. The cost is what this article is about.

Behind any tunnel-shaped topology - cloudflared, Caddy doing TLS in front of YARP, nginx in front of Kestrel, AWS ALB, anything that terminates TLS - the gateway no longer sees the client's TLS handshake, TCP options, or HTTP/2 frame sequence. The dashboard's signature cards reflect that honestly: behind cloudflared without header forwarding, every visitor's TLS Version is blank and HTTP Protocol reads `HTTP/1.1`, because that's literally what the cloudflared↔Kestrel hop is. The detection engine still classifies correctly (the behavioural waveform doesn't care what protocol the bytes arrived on), but a meaningful chunk of the fingerprint surface is dark.

This isn't a bug. It's the shape of the deployment. Knowing what's lost - and which of those losses can be patched with header forwarding - is the difference between "running StyloBot behind a tunnel" and "running StyloBot behind a tunnel correctly."

# Why the tunnel breaks fingerprinting

A reverse proxy isn't a transparent pipe. It terminates the client's TCP connection at the edge, decrypts the TLS handshake, parses the HTTP request, then opens a *separate* TCP connection to the origin and replays a *new* HTTP request over it. Two distinct sessions, glued together by the proxy.

```
┌─────────┐    TLS 1.3 + H2     ┌────────────┐   HTTP/1.1 cleartext   ┌──────────┐
│ Browser │ ◄──────────────────► │ cloudflared│ ◄────────────────────► │ Gateway  │
└─────────┘   JA3, JA4, TCP      └────────────┘    Kestrel sees this   └──────────┘
              opts, H2 frames                     (nothing interesting)
```

By the time the bytes hit the gateway's Kestrel socket, every fingerprint-shaped artefact has been stripped off. The TLS handshake happened at cloudflared. The TCP three-way handshake happened at cloudflared. The HTTP/2 frame ordering was reassembled and replayed by cloudflared as plain HTTP/1.1 (or H2C if you enabled it, which still won't preserve the original frame sequence). What the gateway sees is the cloudflared↔Kestrel hop, not the browser↔edge one. So when it reads `HttpContext.Request.Protocol` it's reading honestly. It's just reading a protocol that has nothing to do with the visitor.

This is true of every tunnel-or-proxy topology, not just Cloudflare. Caddy in front of YARP does it. nginx in front of Kestrel does it. AWS ALB does it. Anything that terminates TLS does it. The proxy is doing its job correctly. The fingerprint just isn't there to be read on the inside.

# The "still works" part

Here's the comfort: StyloBot doesn't rely on these signals to make a verdict. The fast-path detectors that do the bulk of the work read things that *do* survive the hop:

- **User-Agent** travels in headers
- **Request paths** travel in the URL
- **Behavioural sequence** (which assets were fetched in what order, with what timing) is captured at the application layer, after the proxy hop
- **Markov chain transitions** between page types are derived from path classification, not from any TLS detail
- **Honeypot hits** depend on which paths the client tried, not how it got there

Run the demo behind a tunnel and Bot vs Human classification is barely affected. The 49 detectors degrade gracefully: ones that need TLS detail (TlsFingerprint, TcpIpFingerprint, Http2Fingerprint) write empty signals; the orchestrator merges what it has; the verdict still gets made. The big behavioural detectors (SessionVector, Periodicity, ContentSequence, Heuristic, AiScraper) don't even notice the tunnel exists.

So the gotcha isn't "StyloBot is broken behind a tunnel." It's "StyloBot is **less informed** behind a tunnel, and the information you lose is the kind that catches the *interesting* bots."

# What you lose, specifically

The bots a TLS-blind setup struggles with are the ones that look perfect at the application layer. Headless Chrome with a real user-agent, a realistic Accept-Encoding chain, plausible navigation timing, even a believable Markov chain. To the behavioural layer it looks human. To the TLS layer it looks like Go's `crypto/tls` library with a non-Chrome JA3. To the TCP layer it looks like Linux on a datacentre IP, not macOS on residential.

Without TLS termination at the gateway, you can't tell those apart from a real Chrome session. The behavioural signal alone has to carry it. That works most of the time, but the lift is heavier than it should be, and the early-detection windows close.

Concretely, behind a tunnel you lose:

| Signal | What it would have caught | What replaces it |
|---|---|---|
| **JA3 / JA4** | curl, Go scrapers, headless libraries with non-browser TLS stacks | Behavioural signature mismatch (slower to converge) |
| **TCP/IP fingerprint** | OS family lies (Linux UA on a macOS TCP stack) | Inconsistency detector via header correlation (less precise) |
| **HTTP/2 frame fingerprint** | Akamai-style frame-order detection of bots that fake the UA but speak HTTP/2 wrong | Mostly nothing |
| **HTTP/3 fingerprint** | QUIC-based detection of clients masquerading as HTTP/3 capable | Nothing - QUIC dies at the edge |
| **Client-extensions hash** | Browser-stack grouping (handshake-level clustering) | Header correlation (coarser) |

That last one is worth a paragraph. Cloudflare's free tier exposes `cf.tls_client_extensions_sha1`, a SHA1 of the TLS client hello extensions. It's not JA3 (no cipher list, no curves, no extension values) but it does group browsers by their TLS stack. Chrome on macOS and Chrome on Windows produce the same hash; Go's `crypto/tls` produces a different one. It's a worse fingerprint than JA3 but a real one, free, and StyloBot reads it via the `X-Client-TLS-Ext-Sha1` header if you forward it. For most operators, this is the right answer: 80% of the JA3 value at zero infrastructure cost.

# The header-forwarding fix

The proxy knows everything the gateway doesn't. The fix is to make the proxy say so out loud, as HTTP headers, on the inside hop. StyloBot's middleware reads these headers before falling back to the request's own values, so no code change at the gateway is needed.

The full list (now documented in `docs/REVERSE_PROXY_SIGNALS.md`):

```
Sb-Http-Version           preferred over X-Client-HTTP-Version because some
                          CDNs strip dynamic X-prefixed headers
X-Client-HTTP-Version     HTTP/1.1 | HTTP/2 | HTTP/3 - the *real* one
X-Client-TLS-Version      TLSv1.2 | TLSv1.3
X-Client-TLS-Cipher       e.g. AEAD-AES128-GCM-SHA256
X-Client-TLS-Ext-Sha1     CF's tls_client_extensions_sha1 - partial fingerprint
X-Client-ASN              numeric ASN of the source IP
X-JA3-Hash                MD5 hash of the JA3 string (the real fingerprint)
X-JA3-String              raw JA3 string (gateway computes the hash if needed)
```

Cloudflare's free tier gives you all of those except JA3/JA4 via a single Transform Rule. CF's Bot Management Enterprise SKU additionally exposes `cf.bot_management.ja3_hash` and `cf.bot_management.ja4` as Transform Rule fields; map those into `X-JA3-Hash` / `X-JA3-String` and the gateway reads them with no extra wiring.

JA3 isn't gated behind Cloudflare at all. Three CF-free routes:

- **nginx with [`nginx-ssl-ja3`](https://github.com/fooinha/nginx-ssl-ja3)**: a one-line `proxy_set_header X-JA3-Hash $ssl_ja3_hash;` and you're done
- **HAProxy with the JA3 Lua module**: same pattern, different syntax
- **Caddy with a JA3 plugin**: same again

The gateway reads `X-JA3-Hash` first, then falls back to computing MD5 from `X-JA3-String` if only the raw string is forwarded. With either one in place, the TLS Fingerprint card on signature detail pages goes from empty to a real hash, the TLS fingerprint detector starts contributing, and the early-detection window for headless-library bots reopens.

# The Caddy plugin path (no Transform Rules required)

If your edge is Caddy and your detection runs in a StyloBot sidecar, the `stylobot` Caddy plugin removes the entire header-forwarding ceremony. Caddy is doing TLS termination, so it has `r.TLS` natively (Go's `*tls.ConnectionState`). The plugin reads version + cipher from there, picks up JA3/JA4 from headers a co-loaded JA3/JA4 Caddy module exposes, and ships the whole bundle to the sidecar over gRPC. The sidecar projects those fields back into the canonical `X-Client-TLS-*` / `X-JA3-Hash` / `X-JA4` headers before invoking the detection pipeline, so the same contributors fire as in the direct-gateway path.

```
Internet → Caddy (TLS termination + stylobot plugin) ──gRPC──→ stylobot-sidecar (detection)
                                                                       │
                                                                       ↓
                                                            same detection pipeline,
                                                            same signal output
```

`Caddyfile`:

```caddyfile
example.com {
    stylobot {
        endpoint localhost:5090
        timeout 50ms
        on_block 403
    }
    reverse_proxy app:3000
}
```

That's the whole setup. Add a JA3 Caddy module (community plugin) and JA3 starts flowing too, no `header_up` lines needed. Behind the scenes the plugin populates `DetectRequest.TLS = { Version, Cipher, JA3, JA4 }` over the gRPC proto; the sidecar's `SyntheticHttpContext` materialises that into the headers `TlsFingerprintContributor` already reads. Same code path, fewer moving parts.

This is the cleanest tunnel-shape deployment: TLS termination and detection sit one process apart but on the same host, with structured TLS metadata flowing as proto fields rather than header strings. For Caddy + sidecar deployments, this is the recommended shape.

# When to skip the tunnel entirely

For TCP/IP fingerprinting and HTTP/2 frame fingerprinting, there is no header workaround. The signal exists only at the raw socket layer, and no proxy I know of forwards it (you can't really; "the third TCP option byte was zero" isn't a thing you put in an HTTP header). If you specifically need those signals - typically because you're running threat-hunting against custom malware that bypasses headless-library detection - you need direct TLS termination at the gateway.

The `Stylobot.Gateway` binary supports this natively via Kestrel's TLS metadata capture. The deployment shape changes from:

```
Internet → Cloudflare Tunnel → Caddy (TLS) → Gateway (no TLS) → Origin
```

to:

```
Internet → Cloudflare Tunnel (TCP mode) → Gateway (TLS termination + detection) → Origin
```

This is rare. For about 95% of operators, header forwarding from the existing proxy is the right answer: it recovers JA3, TLS, HTTP version, and ASN, leaves you with one degraded layer (raw TCP/H2), and doesn't require rebuilding your edge. Setup is one Cloudflare Transform Rule or three `proxy_set_header` lines.

# The pattern

Anything that terminates TLS gets to see the fingerprint. Anything downstream of TLS termination gets a redacted view. The detection engine has to be told what was redacted - via headers - or it can't reconstruct what the visitor really looked like.

StyloBot's design choice is to read those headers when present and degrade gracefully when not. The "gotcha" is that running it behind a tunnel without forwarding any of them gives you maybe 70% of the fingerprint surface, and you'll see it in slower convergence on the bots that look most human at the application layer. The fix is small. The cost of not knowing about it is silent.

If your dashboard's signature cards have empty TLS Version / HTTP Protocol columns, that's the symptom. The recipe is in `docs/REVERSE_PROXY_SIGNALS.md`. Five minutes at the edge, no gateway redeploy, and you get most of the fingerprint surface back.

The tunnel still works. It just needs to be told what it saw.
