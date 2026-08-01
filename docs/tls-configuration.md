# TLS Configuration

This covers TLS on the `stylobot` gateway binary (Console) — cert/key setup, HTTPS
listener config, multi-domain/SNI limits, upstream TLS, and the common SSL-error
gotchas operators hit. Verified against `Mostlylucid.BotDetection.Console/Program.cs`.

If you're running the Docker `Stylobot.Gateway` image instead of the apt-installed
CLI, the TLS mechanics below (Kestrel `UseHttps`, no SNI support, upstream cert
verification) are the same — only the flag surface differs (`--cert`/`--key` become
container env vars / Kestrel config in `appsettings.json`).

## Two ways to terminate TLS

**1. Let stylobot terminate TLS directly** (`--cert`/`--key`) — stylobot listens on
your chosen port with HTTPS, forwards decrypted requests upstream.

**2. Terminate TLS in front of stylobot** (Caddy / nginx / Cloudflare Tunnel) and run
stylobot in plain HTTP behind it — the recommended setup for multi-domain / SNI, since
stylobot's own listener does not support it (see below). The front proxy forwards
`X-Client-TLS-Version`, `X-Client-TLS-Cipher`, `X-JA3-Hash` etc. so bot detection still
sees the real client's TLS fingerprint; see [`REVERSE_PROXY_SIGNALS.md`](REVERSE_PROXY_SIGNALS.md)
for the exact header recipes per proxy.

## Direct TLS termination

```bash
# PFX (PKCS#12) — single file, optionally password-protected
stylobot 443 https://api.example.com --cert cert.pfx --cert-password mypassword

# PEM — cert and key are separate files, both required together
stylobot 443 https://api.example.com --cert cert.pem --key key.pem
```

| Flag | Required | Notes |
|------|----------|-------|
| `--cert <path>` | to enable TLS at all | `.pfx` or `.pem`. TLS is **off** unless `--cert` is passed — plain HTTP otherwise, no default cert generated. |
| `--key <path>` | only with a `.pem` cert | Startup fails fast (`--key is required when using a .pem certificate`) if omitted. Ignored/not needed for `.pfx`. |
| `--cert-password <pass>` | only for a password-protected `.pfx` | Ignored for `.pem`. |

Passing `--cert` switches the port to HTTPS end-to-end: `stylobot 443 ... --cert
cert.pfx` serves `https://` on 443, not `http://`. There's no separate "listen on
both 80 and 443" mode — run a second `stylobot` process (or a front proxy) for an
HTTP→HTTPS redirect listener.

## No SNI / no multi-domain certs

`stylobot`'s HTTPS listener presents **one certificate for every connection**,
regardless of the SNI hostname the client requested (`ServerCertificateSelector`
ignores the incoming server-name argument and always returns the configured cert).
This means:

- One `stylobot` process = one certificate. To serve `a.example.com` and
  `b.example.com` with **different** certs from the same listener, you need a SAN
  (multi-domain) or wildcard cert, or two separate `stylobot` processes on different
  ports/interfaces.
- If you need per-domain certs from a single listener (classic SNI switching), put
  Caddy or nginx in front — both do SNI dispatch natively — and run stylobot in plain
  HTTP behind it (see "Two ways to terminate TLS" above). This is the setup most
  multi-domain deployments should use.

## Upstream (origin) TLS

When your upstream (`stylobot <port> https://your-origin.com`) is itself HTTPS, the
gateway's outbound YARP `HttpClient` uses the **default .NET TLS validation** — the
OS trust store, standard chain + hostname checks. There is no built-in
"skip verification" flag, and none should be added (an unverified upstream hop is
a real MITM risk, not a config convenience) — see the SSL-error table below for what
to do instead if your origin has a self-signed or internal-CA cert.

## Common SSL-error gotchas

| Symptom | Cause | Fix |
|---|---|---|
| `stylobot` won't start: `--key is required when using a .pem certificate` | `--cert` points at a `.pem`/`.crt` file but `--key` wasn't passed | Add `--key <path-to-private-key>`, or switch to a `.pfx` that bundles both. |
| `Certificate file not found: <path>` / `Private key file not found: <path>` at startup | Typo'd path, or the file is inside a directory the process user can't read | Check the path is absolute (or correctly relative to the working directory you launched from) and the running user has read access — `apt`-installed stylobot typically runs as its own service user, not your login user. |
| Client gets a TLS handshake error / "certificate not trusted" hitting stylobot directly | Self-signed or internal-CA cert passed to `--cert`, and the client doesn't trust that CA | Install the CA cert into the client's/OS's trust store for testing, or use a publicly-trusted cert (Let's Encrypt etc.) for anything real users hit. |
| Gateway logs an outbound TLS/handshake exception talking to the **upstream** origin | The origin's cert is self-signed, expired, or issued by an internal CA the gateway's OS doesn't trust | Install the origin's CA cert into the trust store the `stylobot` process runs under (the container/host OS cert store, e.g. `update-ca-certificates` on Debian/Ubuntu) — not a code-level bypass. |
| Browser refuses plain `http://` to a `stylobot` port that used to be HTTPS, even though you removed `--cert` | Browser-cached HSTS (`Strict-Transport-Security`) for that host from a previous HTTPS response — stylobot itself never sends an HSTS header, so this is leftover browser state, not stylobot re-adding it | Clear the browser's HSTS entry for that host (e.g. Chrome: `chrome://net-internals/#hsts` → Delete domain security policies), or keep serving HTTPS if real users depend on it. |
| Requests fail or bot-detection TLS signals (`tls.version`, JA3) are missing when stylobot sits behind Cloudflare/Caddy/nginx | TLS was terminated at the front proxy but the client-TLS forwarding headers weren't configured, so stylobot sees only the proxy's own TLS to itself (or none, if the hop is plain HTTP) | Wire the `X-Client-TLS-*` / `X-JA3-Hash` Transform Rules or module for your specific proxy — see [`REVERSE_PROXY_SIGNALS.md`](REVERSE_PROXY_SIGNALS.md). |
| `stylobot --tunnel` traffic looks fine but you expected TLS between the client and Cloudflare's edge to show up in bot-detection TLS signals | Cloudflare Tunnel terminates the public TLS connection at Cloudflare's edge; the `cloudflared` → `stylobot` hop inside your network is a separate, unencrypted-by-default local connection (h2c) | Expected behavior for a tunnel topology, not a bug — TLS/JA3 fingerprinting for tunnel traffic comes from Cloudflare's own edge signals (Bot Management headers), not a local TLS handshake stylobot can observe directly. |

## Related

- [`REVERSE_PROXY_SIGNALS.md`](REVERSE_PROXY_SIGNALS.md) — forwarding TLS/JA3/HTTP2 fingerprint headers from Cloudflare, Caddy, nginx, HAProxy.
- [`install-linux-apt.md`](install-linux-apt.md) — install + the default (no-flags) behavior before you configure anything, including TLS.
- [`action-policies.md`](../src/Mostlylucid.BotDetection/docs/action-policies.md) — what actually happens to a request once detection runs (block/throttle/logonly), independent of TLS.
