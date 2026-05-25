# Forwarding client signals from reverse proxies / CDNs

When StyloBot's gateway sits behind a reverse proxy (Cloudflare Tunnel, Caddy, nginx, AWS ALB, Fastly, etc.), the proxy terminates the client's TLS + HTTP connection and opens a *new* connection to the gateway. By default the gateway only sees the proxy↔gateway hop's protocol, TLS, and IP - not the client's actual values.

That makes signature cards permanently report `HTTP/1.1`, blank TLS version, etc., because that's what the cloudflared↔Kestrel hop genuinely is.

The fix is to inject the client-side values as request headers at the proxy. StyloBot reads the following headers in priority order and falls back to `HttpContext.Request.*` only when none is present. **No code changes needed on the gateway** - set up the headers at your edge and they show up automatically.

## Cloudflare Tunnel (free tier)

Configure these in **Rules → Transform Rules → Modify Request Header** on the zone serving your traffic. Each rule sets one dynamic header from a `cf.*` / `http.*` / `ip.*` expression.

| Header name | CF expression | What it tells you |
|---|---|---|
| `X-Client-HTTP-Version` | `http.request.version` | Real client HTTP version (HTTP/1.1, HTTP/2, HTTP/3) - bypasses the `HTTP/1.1` you'd see at the cloudflared↔origin hop |
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

### Enterprise CF fields (commercial stylobot only)

Cloudflare Bot Management (Enterprise SKU) exposes signals the free tier cannot. The commercial stylobot plugin (`Stylobot.Commercial.GatewayPlugin`) registers a `CloudflareEnterpriseSignalEnricher` middleware that reads four additional headers and surfaces them as `HttpContext.Items` keys (`sb.cf.bot_score`, `sb.cf.verified_bot`, `sb.cf.ja3`, `sb.cf.ja4`) for downstream contributors.

Configure four more dynamic-header Transform Rules in the same CF zone:

| Header name | CF Enterprise expression | What it tells you |
|---|---|---|
| `X-Client-Bot-Score` | `cf.bot_management.score` | 1-99 (lower = more bot-like) - CF's own bot score |
| `X-Client-Verified-Bot` | `cf.bot_management.verified_bot` | `true` for IP-verified vendor bots (Googlebot etc.) |
| `X-Client-JA3` | `cf.bot_management.ja3_hash` | JA3 TLS handshake fingerprint |
| `X-Client-JA4` | `cf.bot_management.ja4` | Newer JA4 fingerprint |

These have no effect without the commercial plugin (`AddStyloBotCommercialPlugin`) - the FOSS gateway ignores them. With the commercial plugin in the pipeline, the values are exposed for detection contributors to consume (e.g. inflating bot probability when CF's score is ≤ 30, pinning to friendly when `verified_bot` is true).

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

## nginx

```nginx
proxy_set_header X-Client-HTTP-Version $server_protocol;
proxy_set_header X-Client-TLS-Version  $ssl_protocol;
proxy_set_header X-Client-TLS-Cipher   $ssl_cipher;
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