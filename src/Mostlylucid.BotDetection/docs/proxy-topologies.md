# Proxy Topology Sensing

StyloBot auto-detects the proxy layer in front of your application and reads the real client IP from the correct header. No configuration is required for standard setups.

## How it works

On the first request, `ProxyEnvironmentDetector` inspects the incoming headers and classifies the topology as one of:

| Topology | Detection headers | Real IP header |
|----------|------------------|----------------|
| `Cloudflare` | `CF-Connecting-IP` or `CF-Ray` | `CF-Connecting-IP` |
| `CloudFront` | `CloudFront-Viewer-Address` or `X-Amz-Cf-Id` | `CloudFront-Viewer-Address` (strips port) |
| `Fastly` | `Fastly-Client-IP` | `Fastly-Client-IP` |
| `Nginx` | `X-Real-IP` | `X-Real-IP` |
| `Generic` | `X-Forwarded-For` (only) | `X-Forwarded-For[0]` |
| `Direct` | none of the above | `Connection.RemoteIpAddress` |

The detected topology is cached for the process lifetime. If the first request comes from the CDN, all subsequent requests use the same extraction logic.

## Production topologies

### StyloBot production (Cloudflare Tunnel + Caddy + YARP)

```
Internet → Cloudflare Tunnel → Caddy (TLS) → Stylobot.Gateway (YARP + bot detection) → Origin
```

Auto-detected as `Cloudflare`. No config needed. Scheme is always `https`.

### Behind nginx/Caddy only

```
Internet → nginx/Caddy → your app
```

Auto-detected as `Nginx` (if `X-Real-IP` is forwarded) or `Generic` (if only `X-Forwarded-For`).

### AWS CloudFront

```
Internet → CloudFront → your app
```

Auto-detected as `CloudFront`. `CloudFront-Viewer-Address` contains `ip:port` - the port is stripped automatically.

### Direct (no proxy)

```
Internet → your app
```

Auto-detected as `Direct`. Uses `Connection.RemoteIpAddress`.

## Configuration override

Override auto-detection when the first request cannot be trusted (e.g., during startup probes from a different path):

```json
{
  "BotDetection": {
    "ProxyEnvironment": {
      "Mode": "Cloudflare"
    }
  }
}
```

Valid values: `Auto` (default), `Direct`, `Cloudflare`, `CloudFront`, `Fastly`, `Nginx`, `Generic`. Case-insensitive.

## H2C (HTTP/2 cleartext) for Cloudflare Tunnel

When `http2Origin: true` is enabled in the Cloudflare Tunnel dashboard, cloudflared speaks H2C to the origin. The gateway is configured to accept both H1 and H2C:

```csharp
// Stylobot.Gateway/Program.cs - already configured
options.ConfigureEndpointDefaults(listenOptions =>
{
    listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
});
```

To enable on the Cloudflare side: Dashboard → Zero Trust → Networks → Tunnels → your tunnel → Edit → Ingress → Advanced → `http2Origin: true`.

---

## Troubleshooting

### Wrong IP detected (shows proxy IP instead of client IP)

**Symptom:** Dashboard shows all traffic from a single IP (your Caddy/nginx IP).

**Cause:** Topology detected as `Direct` when there is a proxy in front.

**Check:** Look at the startup log for:
```
[INF] Proxy topology auto-detected: Direct
```

**Fix options:**

1. If behind nginx/Caddy: ensure `X-Real-IP` or `X-Forwarded-For` is forwarded. For Caddy:
   ```
   header_up X-Real-IP {remote_host}
   ```

2. Force the mode in config:
   ```json
   { "BotDetection": { "ProxyEnvironment": { "Mode": "Nginx" } } }
   ```

3. Ensure `UseForwardedHeaders()` runs before `UseBotDetection()` in your middleware pipeline.

---

### Topology detected as `Generic` instead of `Cloudflare`

**Symptom:** Log shows `Proxy topology auto-detected: Generic` but you are behind Cloudflare.

**Cause:** First request hit the app before Cloudflare's connection was established (e.g., a health check directly from the Docker host) and the cached result is `Generic`.

**Fix:** Set `Mode = "Cloudflare"` explicitly in config to skip auto-detection.

---

### Cloudflare scheme reported as `http` internally

This is expected. Cloudflare terminates TLS and connects to the origin over HTTP (or H2C). `GetRealScheme()` always returns `https` for `Cloudflare` topology regardless of the internal connection scheme.

---

### `CF-Connecting-IP` missing in Cloudflare Tunnel

Cloudflare Tunnel does not inject `CF-Connecting-IP` by default for all routes. Ensure:

1. In the Cloudflare Tunnel ingress config, the route points to the right service.
2. Cloudflare's "Authenticated Origin Pulls" or WAF is not stripping the header.
3. If using a Cloudflare Worker in front, the worker must forward `CF-Connecting-IP`.

If the header is stripped, fall back to overriding `Mode = "Generic"` and rely on `X-Forwarded-For[0]`.

---

### AWS CloudFront: IP shows as IPv6 when IPv4 expected

`CloudFront-Viewer-Address` contains the exact client address CloudFront received, which may be IPv6 (`::ffff:1.2.3.4` mapped form). The port is stripped, but the IP is not normalised. Downstream IP range checks handle both forms.

---

### Topology changes between deployments (Docker recreate)

Since the topology is cached per process, a container restart re-detects on the first request. If you use rolling deploys and health-check probes run before real traffic arrives, the probe (often from `localhost` or an internal Docker network) may cause `Direct` to be cached.

**Fix:** Set `Mode` explicitly in config for production deployments.

---

### Port 80 / plaintext connections fail after enabling H2C

Kestrel with `Http1AndHttp2` still accepts H1 connections. If plaintext connections fail, the issue is usually the Cloudflare Tunnel configuration, not Kestrel:

- Ensure `http2Origin: true` is set in the tunnel ingress config.
- Restart the cloudflared process after changing tunnel config.
- Verify with: `curl -v --http2-prior-knowledge http://localhost:5000/` (should respond with HTTP/2).
