# Brownfield retrofit -- protect an old site without rebuilding it

You should not need to rebuild a site to survive the automation internet.

Drop a behavioural gateway in front of it, route traffic through that gateway, point Cloudflare Tunnel at the gateway. Done. Three steps, no public-facing IP on either host, no certificates to manage, no app code changes. Works for old WordPress / Magento / classic ASP.NET / static IIS / anything that speaks HTTP.

**The strongest version of the story:** the old site has *zero public exposure*. Not a port, not a hostname, not even an inbound firewall rule. Cloudflare Tunnel handles ingress to stylobot AND egress from stylobot to the legacy origin. Stylobot becomes the only public-facing thing in the picture; the old system becomes a strictly-private origin.

## The two tunnels

The brownfield story works because Cloudflare Tunnel can do **both** directions:

| Tunnel | Direction | Whose end is the daemon on | What it carries |
|--------|-----------|---------------------------|-----------------|
| **A -- public ingress** | internet -> stylobot | stylobot's VPS | Real visitor traffic going through CF edge -> tunnel -> stylobot |
| **B -- private origin** | stylobot -> old backend | old-site host | Stylobot's outbound proxy hop. The old backend is the tunnel's local sink. |

Tunnel A makes stylobot reachable from the public internet without opening port 80/443 on its VPS.
Tunnel B lets stylobot reach the legacy backend without that backend having a public hostname or open port.

Both are free on Cloudflare's plan. Both use outbound-only persistent connections from the daemon end -- no inbound firewall rules anywhere.

## The architecture (strongest form)

```
                                              no inbound port open
                                              no cert to install
                                              no DNS surgery to do
                                              ----------------------
        public DNS
       (your-site.com)
            |
            v
       Cloudflare edge ----[Tunnel A]----> stylobot VPS
                                                |
                                                | behavioural detection runs here
                                                | (block / rate-limit / throttle by BotType)
                                                |
                                                v
                                          [Tunnel B]
                                                |
                                                v
                                          old WordPress / Magento / ASP.NET box
                                          (private LAN, no inbound exposure at all)
```

The old box's firewall is closed. Its only network egress is the outbound cloudflared daemon connecting to Cloudflare's edge for Tunnel B. The stylobot VPS's firewall is also closed -- outbound to CF for Tunnel A's daemon, plus DNS / NTP. That's the whole attack surface.

## Three deployment shapes

The same `stylobot` console binary handles all three. Pick the shape that matches how much you can / want to change about the legacy host.

### Shape 1 -- everything on one host (simplest)

Old site lives on `localhost:8080`. Stylobot in front. Single CF Tunnel for ingress (no Tunnel B needed because the upstream is already private).

```bash
stylobot 5080 http://localhost:8080 --tunnel <token>
```

- Stylobot listens on port 5080 for the local tunnel.
- Public traffic: CF edge -> Tunnel A -> stylobot:5080 -> localhost:8080.
- Suitable when you can run stylobot on the same VPS as the legacy site.

### Shape 2 -- separate stylobot host, private LAN to backend

Old site stays on its existing host (private network, e.g. Tailscale / WireGuard / VPC peer). Stylobot runs on a small VPS that can reach the old site via that private network.

```bash
stylobot 5080 http://oldsite.internal:8080 --tunnel <ingress-token>
```

- Stylobot VPS: no inbound public exposure.
- Old-site host: no inbound public exposure.
- The two hosts speak HTTP to each other over your existing private mesh.
- Best when the private network is already there.

### Shape 3 -- stylobot host + Tunnel B to the backend (no private mesh needed)

Same as Shape 2 but the stylobot VPS reaches the legacy host *through Cloudflare* instead of over a private mesh. You don't need a Tailscale / VPN of your own; CF's network is the mesh.

On the **legacy host**:

```bash
# expose the old site privately via Cloudflare Tunnel B
cloudflared tunnel --no-autoupdate run --token <backend-token>
# the tunnel's local sink is http://localhost:8080 (the legacy app)
```

On the **stylobot host** (6.8.2+ -- one command):

```bash
stylobot 5080 --origin-tunnel oldsite.tunnel.your-org --tunnel <ingress-token>
```

`--origin-tunnel <hostname>` makes stylobot pick a free loopback port at startup, launch `cloudflared access tcp` as a sidecar pointed at that port, and rewrite its own upstream to `http://localhost:<auto-port>`. From stylobot's point of view it's a normal proxy hop; from the operator's it's a single flag.

- Legacy host: no inbound port. Outbound 443 to CF only.
- Stylobot host: no inbound port. Outbound 443 to CF only (Tunnel A for ingress, the bundled `--origin-tunnel` cloudflared for the outbound origin hop).
- No VPN, no port-forwarding, no private mesh you have to maintain.
- The "even the old box doesn't talk to the internet directly" story is real here.

Pre-6.8.2 the operator had to run the `cloudflared access tcp` themselves -- see the [git history of this doc](https://github.com/scottgal/stylobot/commits/main/docs/brownfield-retrofit.md) for the manual chain.

### Shape 4 -- docker compose on the legacy host

When the legacy site is already containerised, add two services next to it:

```yaml
services:
  oldsite:
    # ... your existing legacy service ...

  stylobot:
    image: scottgal/stylobot:latest
    command: ["5080", "http://oldsite:8080"]
    # no ports: exposed -- only cloudflared reaches it on the docker network

  cloudflared:
    image: cloudflare/cloudflared:latest
    command: ["tunnel", "--no-autoupdate", "run", "--token", "${CF_TUNNEL_TOKEN}"]
    # CF dashboard routes the public hostname to http://stylobot:5080
```

- Zero ports exposed to the host network. Everything traverses the docker network.
- Easiest path when the legacy site is already containerised.

## What the `--tunnel <token>` and `--origin-tunnel <hostname>` flags do

Two flags, two tunnels, one binary:

| Flag | Direction | What it identifies | Underlying cloudflared command |
|------|-----------|-------------------|---------------------------------|
| `--tunnel` (no value) | Tunnel A (public ingress) | Quick tunnel -- random `*.trycloudflare.com` URL printed at startup, demo-only | `cloudflared tunnel --url http://localhost:<port>` |
| `--tunnel <token>` | Tunnel A (public ingress) | Named tunnel; CF dashboard config supplies the hostname -> local mapping. Token passed via `TUNNEL_TOKEN` env so it never appears in process listings. | `cloudflared tunnel run` |
| `--origin-tunnel <hostname>` (6.8.2+) | Tunnel B (private origin) | Private hostname for your backend tunnel in the CF Zero Trust dashboard. Stylobot picks a free loopback port and rewrites its upstream automatically. | `cloudflared access tcp --hostname <hostname> --url localhost:<auto-port>` |

Combined: `stylobot 5080 --origin-tunnel <backend-host> --tunnel <ingress-token>` -> stylobot binds the gateway, launches both cloudflared sidecars, wires the proxy chain. Two tokens (sort of -- the origin side is a hostname, the ingress side is a token), one command.

## Getting the tokens

For **Tunnel A (public ingress)**, on the stylobot host:

1. [dash.cloudflare.com](https://dash.cloudflare.com) -> Zero Trust -> Networks -> Tunnels -> *Create a tunnel*.
2. Add a **public hostname** for the tunnel (e.g. `legacy.your-site.com`). Point it at `http://localhost:5080` (Shape 1) or `http://stylobot:5080` (Shape 4).
3. Copy the token. Pass to stylobot via `--tunnel` or to a sidecar `cloudflared` container.

For **Tunnel B (private origin)**, on the legacy host (Shape 3):

1. Create a *second* tunnel in the CF dashboard.
2. Don't add a public hostname; instead use a **private network** route to expose the local service.
3. Copy the token. Pass to the `cloudflared` daemon on the legacy host.
4. On the stylobot host, `cloudflared access tcp --hostname <name>` resolves the tunnel and exposes it as a local TCP socket the gateway proxies to.

Free tier covers unlimited tunnels and unlimited bandwidth for HTTP. No Zero Trust subscription needed for this scenario.

## Why this matters

Old sites *will* get hit by the same scraper-AI mix that hits new ones. They typically can't be rebuilt -- the original devs are gone, the framework is EOL, the database is hand-maintained. The economic alternative is hosting them behind a CDN that promises bot protection (sometimes vendor-locked, often priced per request, frequently worse).

The brownfield retrofit gives you the same protection surface as a greenfield deployment:

- **Block / rate-limit / throttle by `BotType`** -- the 6.8 defaults route malicious bots to `block-hard`, AI scrapers to `rate-limit-ai`, search engines to `rate-limit-search`. See [`policy-defaults.md`](../src/Mostlylucid.BotDetection/docs/policy-defaults.md).
- **Adaptive scaling** -- when the legacy origin starts slowing down (5xx rate climbs, P95 latency climbs), bot allowances tighten so humans get priority. Especially valuable for fragile old stacks that can't take a sustained scraper load.
- **Honeypot detection** -- catches WordPress wp-admin probes, .env scrapers, etc. even on stacks that don't actually have those paths. See [`honeypot-catalog.md`](../src/Mostlylucid.BotDetection/docs/honeypot-catalog.md).
- **Operator dashboard** -- enable `--enable-api` on stylobot, run `stylobot-ui` as a viewer; you get the full investigate / policy / honeypot tabs without touching the legacy site's templates.

No code touches the legacy site. The old database, the old PHP, the old `.aspx` -- all untouched. Stylobot is purely the door.

## What's between "this works today" and "ship it as a one-liner"

All four shapes work end-to-end today. Shape 3 (the fully-private-origin story) became a single command in 6.8.2 via `--origin-tunnel`. Remaining packaging polish:

- A bundled `stylobot` Docker image that includes `cloudflared` (current image is detection-only; Shape 4 today needs both containers).
- An install script (`curl ... | bash` style) that prompts for the token(s) and writes a systemd unit -- so the one-liner story matches the "3 steps" narrative.
- Per-host site profiles ([planned](deferred/site-profiles.md)) so the gateway auto-loads e.g. the WordPress simulation pack when a tunnel routes a WordPress domain.

None of those block the underlying retrofit. The architecture is sound; the rest is packaging.

## The pitch

> Your old site does not need to be rebuilt, patched into modern shape, or exposed to the public internet. StyloBot becomes the public behavioural gateway. The old system becomes a strictly-private origin. Three steps, free Cloudflare tier, no code changes.
