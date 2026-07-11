# Forwarding client signals from reverse proxies / CDNs

When StyloBot's gateway sits behind a reverse proxy (Cloudflare Tunnel, Caddy, nginx, AWS ALB, Fastly, etc.), the proxy terminates the client's TLS + HTTP connection and opens a *new* connection to the gateway. By default the gateway only sees the proxy↔gateway hop's protocol, TLS, and IP - not the client's actual values.

That makes signature cards permanently report `HTTP/1.1`, blank TLS version, etc., because that's what the cloudflared↔Kestrel hop genuinely is.

The fix is to inject the client-side values as request headers at the proxy. StyloBot reads the following headers in priority order and falls back to `HttpContext.Request.*` only when none is present. **No code changes needed on the gateway** - set up the headers at your edge and they show up automatically.

## What survives a tunnel, what doesn't

Behind cloudflared / Caddy / nginx / any reverse proxy, the gateway's connection to the proxy is a *separate* TCP + TLS + HTTP session from the client's connection to the edge. By the time bytes hit the gateway the client's TLS handshake, TCP options, and HTTP/2 frame ordering are long gone, so anything fingerprint-shaped has to be forwarded by the edge or it stays invisible.

| Signal | Behind a tunnel | Recovery path |
|---|---|---|
| Client IP | sees proxy IP | proxy header (`CF-Connecting-IP`, `X-Real-IP`, `X-Forwarded-For`); auto-detected, see [`proxy-topologies.md`](../src/Mostlylucid.BotDetection/docs/proxy-topologies.md) |
| HTTP version | proxy↔origin hop only | inject `Sb-Http-Version` (preferred) or `X-Client-HTTP-Version` at the edge |
| TLS version / cipher | proxy↔origin TLS (or plaintext) | inject `X-Client-TLS-Version` / `X-Client-TLS-Cipher` |
| Client extensions hash | not visible | inject `X-Client-TLS-Ext-Sha1` (CF free tier exposes this as `cf.tls_client_extensions_sha1`) |
| JA3 / JA4 | not computable at origin | requires the edge to compute and forward it: CF Bot Management Enterprise (exposes `cf.bot_management.ja3_hash`), nginx `ssl_ja3` module, Caddy `ja3` plugin, or HAProxy Lua. Inject as `X-JA3-Hash` / `X-JA3-String` and the gateway reads them directly. |
| ASN | needs GeoIP DB at origin | inject `X-Client-ASN` from the edge (e.g. CF's `ip.geoip.asnum`) to skip the lookup |
| TCP / IP fingerprint (p0f) | gone for good | not recoverable behind a tunnel; needs direct TLS termination at the gateway |
| HTTP/2 frame fingerprint (AKAMAI) | gone for good | not recoverable behind a tunnel; needs direct HTTP/2 termination at the gateway |
| HTTP/3 fingerprint (QUIC) | gone for good | not recoverable; needs QUIC termination at the gateway |

For most deployments the first six recover enough. If you specifically need TCP-, H2-, or H3-frame fingerprints (rare; mostly enterprise threat hunting), terminate TLS at the gateway directly via `Stylobot.Gateway`'s built-in Kestrel TLS metadata capture (see [`TLS_FINGERPRINTING_SETUP.md`](../src/Stylobot.Gateway/docs/TLS_FINGERPRINTING_SETUP.md)) instead of fronting with a tunnel.

## Header read order

The gateway reads the protocol header from these names in order, taking the first non-empty value before falling back to `HttpContext.Request.Protocol` (the proxy↔origin hop):

1. `Sb-Http-Version` (preferred; bare name avoids `X-` filters some CDNs apply to dynamic headers)
2. `X-Client-HTTP-Version`
3. `X-Forwarded-Proto-Version`
4. `X-Client-Protocol` (Caddy idiom)

TLS, ASN, and JA3/JA4 headers have a single canonical name each (table below). If you're hand-rolling proxy config, pick `Sb-Http-Version` for HTTP version; everything else uses the `X-Client-*` / `X-JA3-*` / `X-JA4` names.

> **Security note - trusted-proxy gate:** The transport fingerprint headers (`X-JA3-*`, `X-JA4*`, `X-Client-TLS-*`, `X-HTTP2-*`, `X-QUIC-*`, `X-TCP-*`) are only honoured when they arrive from a trusted reverse proxy. A client reaching the origin directly and sending these headers earns a bot signal, not a human bias. See the [Trusted-proxy gate](#trusted-proxy-gate-transport-fingerprint-headers) section below before deploying. If your edge has a public IP (Cloudflare, AWS ALB, Fastly), you **must** add it to `BotDetection:TransportTrust:TrustedProxyIps`.

## Cloudflare Tunnel (free tier)

Configure these in **Rules → Transform Rules → Modify Request Header** on the zone serving your traffic. Each rule sets one dynamic header from a `cf.*` / `http.*` / `ip.*` expression.

| Header name | CF expression | What it tells you |
|---|---|---|
| `Sb-Http-Version` *(preferred)* or `X-Client-HTTP-Version` | `http.request.version` | Real client HTTP version (HTTP/1.1, HTTP/2, HTTP/3) - bypasses the `HTTP/1.1` you'd see at the cloudflared↔origin hop. `Sb-Http-Version` is preferred because some CF setups silently strip dynamic `X-`-prefixed headers. |
| `X-Client-TLS-Version` | `cf.tls_version` | TLSv1.2 / TLSv1.3. Lights up the TLS Version card on signature detail |
| `X-Client-TLS-Cipher` | `cf.tls_cipher` | Negotiated cipher suite (e.g. `AEAD-AES128-GCM-SHA256`) |
| `X-Client-TLS-Ext-Sha1` | `cf.tls_client_extensions_sha1` | SHA1 of the TLS client extensions - a stable handshake fingerprint, useful for grouping clients that share the same TLS stack |
| `X-Client-ASN` | `ip.geoip.asnum` | Source ASN (datacenter detection). Provided by Cloudflare's IP intelligence at the edge |

Setup:

1. Cloudflare dashboard → your zone → **Rules** → **Transform Rules** → **Modify Request Header**
2. Click **Create rule**
3. **Rule name**: descriptive (e.g. `Forward client HTTP version`)
4. **When incoming requests match**: usually "All incoming requests"; filter to staging hostnames if you only want it on some
5. **Modify request header** → **Set dynamic**
   - Header name: as in table above
   - Value: the matching `http.*` / `cf.*` / `ip.*` expression
6. **Deploy**

You can put all the headers into a single rule - under "Modify request header" click the **+** to add additional headers. Five headers, one rule.

After deploy, hit any page from a modern browser. The signature detail card's TLS Version, HTTP Protocol etc. will populate from the new headers. Existing persisted signatures show historical (pre-rule) data; they update on next detection event for that signature.

### If you have Cloudflare Bot Management (Enterprise SKU)

Bot Management Enterprise exposes a JA3 hash (`cf.bot_management.ja3_hash`) and a JA4 fingerprint (`cf.bot_management.ja4`) as Transform Rule fields. Map them into the canonical header names that the gateway already reads:

| Header name | CF Enterprise expression | What it tells you |
|---|---|---|
| `X-JA3-Hash` | `cf.bot_management.ja3_hash` | JA3 TLS handshake fingerprint - read by `TlsFingerprintAtom` |
| `X-JA4` | `cf.bot_management.ja4` | JA4 fingerprint - read by `TlsFingerprintAtom` (also accepts `X-JA4-Fingerprint` or `X-JA4-Hash`) |
| `X-Client-TLS-Ext-Sha1` | `cf.tls_client_extensions_sha1` | (already in the free-tier table above; included here for completeness) |

That's it - no extra middleware or plugin needed. The same `X-JA3-Hash` / `X-JA4` headers that nginx-with-ssl_ja3 or a Caddy JA4 plugin sends are what the gateway expects from CF Enterprise too. Pick the single header name per signal and the gateway doesn't care which edge produced it.

(If you also want to forward CF's own `cf.bot_management.score` or `cf.bot_management.verified_bot` for use in your own gateway / WAF rules, you can - the gateway doesn't read them today.)

## Caddy

Caddy sets `X-Forwarded-Proto` natively. For HTTP version + TLS info you can add a header transformation:

```caddyfile
example.com {
    reverse_proxy gateway:8080 {
        header_up X-Client-HTTP-Version {http.request.proto}
        header_up X-Client-TLS-Version {http.request.tls.version}
        header_up X-Client-TLS-Cipher {http.request.tls.cipher_suite}
        header_up X-Forwarded-Proto {http.request.scheme}
    }
}
```

### Caddy + StyloBot sidecar plugin (no header forwarding needed)

If your topology is Caddy → StyloBot sidecar (gRPC) → upstream app, the [`stylobot` Caddy plugin](../sdk/caddy/) does the TLS extraction for you. It reads `r.TLS.Version`, `r.TLS.CipherSuite`, and optional `X-JA3-Hash` / `X-JA4` headers (set by a co-loaded JA3/JA4 Caddy module if you have one) and forwards them to the sidecar over gRPC via the `TlsInfo` proto. The sidecar's `SyntheticHttpContext` projects them back into the canonical `X-Client-TLS-*` / `X-JA3-Hash` / `X-JA4` headers before invoking the detection pipeline, so contributors fire the same way they would behind a direct-gateway TLS termination.

```caddyfile
example.com {
    stylobot {
        endpoint localhost:5090
        timeout 50ms
    }
    reverse_proxy app:3000
}
```

No `header_up` directives required. This is the recommended shape for Caddy + sidecar deployments. See [`sdk/caddy/README.md`](../sdk/caddy/README.md).

## nginx

Base headers (any nginx build):

```nginx
proxy_set_header Sb-Http-Version       $server_protocol;
proxy_set_header X-Client-TLS-Version  $ssl_protocol;
proxy_set_header X-Client-TLS-Cipher   $ssl_cipher;
```

With the [`nginx-ssl-ja3`](https://github.com/fooinha/nginx-ssl-ja3) module (or any module exposing `$ssl_ja3` / `$ssl_ja3_hash`), forward the JA3 fingerprint as well:

```nginx
proxy_set_header X-JA3-Hash    $ssl_ja3_hash;
proxy_set_header X-JA3-String  $ssl_ja3;
```

`TlsFingerprintAtom` reads `X-JA3-Hash` (priority) or computes the MD5 from `X-JA3-String` when only the raw string is available. With this in place the TLS Fingerprint card on signature detail shows a real JA3 hash, not the `cf.tls_client_extensions_sha1` partial substitute.

## HAProxy

With Lua-based JA3 (e.g. the `haproxy-ja3` library):

```haproxy
frontend https_front
    bind *:443 ssl crt /etc/haproxy/certs/
    http-request lua.ja3
    http-request set-header X-JA3-Hash      %[var(txn.ja3_hash)]
    http-request set-header X-JA3-String    %[var(txn.ja3)]
    http-request set-header X-Client-TLS-Version %[ssl_fc_protocol]
    http-request set-header X-Client-TLS-Cipher  %[ssl_fc_cipher]
    http-request set-header Sb-Http-Version $HTTP_VERSION
    default_backend stylobot_gateway
```

## AWS ALB / CloudFront

ALB doesn't have native header injection but you can attach a Lambda@Edge / CloudFront Function that copies `cloudfront-viewer-tls` + `cloudfront-viewer-http-version` into the target header names. CloudFront Functions are billed per million requests.

## Verifying

After deploy, on any signature detail page (`/dashboard/signature/{id}`) check the **Fingerprint Profile** card:

- **TLS Version** should show `TLSv1.3` (or `TLSv1.2`)
- **HTTP Protocol** should show `HTTP/2` or `HTTP/3` for modern browsers, `HTTP/1.1` only for legacy clients / curl without `--http2`

If those still show `--` or `HTTP/1.1` for everything:

1. Confirm the rule deployed in the CF dashboard (Status column = `On`)
2. Hit a fresh URL with `?_t=<timestamp>` to skip cache
3. The signature page shows persisted data - wait for the next detection event for that signature, or check a freshly-created signature

## Why headers and not just `Request.Protocol`?

StyloBot's `EnrichProtocol` middleware (`Mostlylucid.BotDetection.UI/Middleware/DetectionBroadcastMiddleware.cs`) reads the headers above first and only falls back to `context.Request.Protocol` when none are present. So:

- Direct deploys (no proxy in front): everything reads from `Request.Protocol` / `ConnectionInfo.Transport` natively - no setup needed.
- Behind any proxy: configure the headers above and you get accurate client-side values.

## Trusted-proxy gate (transport fingerprint headers)

The transport fingerprint headers documented above (`X-JA3-*`, `X-Client-TLS-*`,
`X-HTTP2-*`, `X-QUIC-*`, `X-TCP-*`) are only trusted when the request demonstrably
arrived via a trusted edge. This prevents a client reaching the origin directly
from spoofing a known-browser fingerprint to earn a human bias.

Configured at `BotDetection:TransportTrust`:

- `Mode` : `Auto` (default), `Strict`, or `Off`.
  - **Auto** trusts these headers when the immediate peer is loopback/private or on
    `TrustedProxyIps`. This matches the canonical `cloudflared -> Caddy -> gateway`
    topology, where the gateway's peer is loopback. A public-IP edge such as Cloudflare
    or an AWS ALB MUST be added to `TrustedProxyIps`; the gate never infers trust from
    forwarded headers (X-Forwarded-For, CF-Connecting-IP, etc.), which are client-forgeable.
  - **Strict** trusts only peers in `TrustedProxyIps`.
  - **Off** restores the legacy behaviour (trust all; logs a startup warning).
- `TrustedProxyIps` : CIDRs/IPs of your reverse proxies (a bare IP is treated as a
  /32 or /128 host route). **Required** for any public-IP edge (Cloudflare, AWS ALB,
  Fastly, etc.) sitting in front of the gateway. The gate never trusts a public-IP peer
  by inferring topology from forwarded headers alone.

When headers are distrusted, the gateway ignores them, falls back to live Kestrel
TLS/protocol metadata, and emits a weak `transport.spoofed_edge_headers` bot signal
only when such headers were actually present.