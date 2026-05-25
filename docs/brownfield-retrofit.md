# Brownfield retrofit -- protect an old site without rebuilding it

You should not need to rebuild a site to survive the automation internet.

Drop a behavioural gateway in front of it, route traffic through that gateway, point Cloudflare Tunnel at the gateway. Done. Three steps, no public-facing IP, no certificates to manage, no app code changes. Works for old WordPress / Magento / classic ASP.NET / static IIS / anything that speaks HTTP.

This document explains the data flow, the CLI invocations for each variant, and why the architecture has the properties it does.

## The architecture

```
        public DNS                                  no inbound port open
       (your-site.com)                              no certificate to install
            |                                       no DNS surgery to do
            v
       Cloudflare edge ----[encrypted tunnel]----> stylobot   --proxy--> old site
                                                    (anywhere)            (private)
```

There is no inbound HTTP port on the stylobot host or on the old-site host. The Cloudflare tunnel daemon (`cloudflared`) establishes an **outbound** persistent connection to Cloudflare's edge; CF routes inbound public traffic into that connection. The local end of the tunnel points at stylobot; stylobot does detection + proxies to the old site.

Three useful consequences:

1. **No public-facing IP.** The stylobot host doesn't need port 80/443 open to the world (or even a public IP at all). The old-site host doesn't either. Both can sit behind a NAT or on a private VLAN.
2. **TLS is Cloudflare's problem.** Your public hostname terminates TLS at the CF edge with a CF-managed certificate; the tunnel link is encrypted; stylobot speaks plain HTTP internally. No `certbot`, no cert renewal cron, no expired-cert outage.
3. **The token is the upstream.** A Cloudflare tunnel token uniquely identifies a tunnel + its routing config. Once stylobot has the token, it knows where to fetch inbound traffic from -- no other ingress config needed.

## Three deployment shapes

The same `stylobot` console binary handles all three.

### Shape 1 -- everything on one host

Old site lives on `localhost:8080`. Stylobot in front. Cloudflared embedded.

```
stylobot 5080 http://localhost:8080 --tunnel <token>
```

- Stylobot listens on port 5080 for the local tunnel client.
- Public traffic: CF edge -> tunnel -> stylobot:5080 -> localhost:8080.
- Suitable when you have a single VPS running the legacy site and don't want to add hosts.

### Shape 2 -- separate stylobot host

Old site stays where it is (say `oldsite.internal:8080`, private network). Stylobot runs on a small VPS that can reach the old site over the private network (Tailscale, WireGuard, VPC peering, anything).

```
stylobot 5080 http://oldsite.internal:8080 --tunnel <token>
```

- Stylobot host has no inbound exposure to the public internet.
- Old-site host has no inbound exposure to the public internet.
- The two hosts speak HTTP to each other over your private network.
- Best when you want to leave the legacy box alone (no new processes, no firewall changes).

### Shape 3 -- Docker on the legacy host

The legacy site is running in a docker-compose stack. Add two services next to it.

```yaml
# docker-compose.yml fragment (add to existing stack)
services:
  oldsite:
    # ... your existing legacy service ...

  stylobot:
    image: scottgal/stylobot:latest
    command: ["5080", "http://oldsite:8080"]
    # no ports: exposed -- only cloudflared needs to reach it on the docker network

  cloudflared:
    image: cloudflare/cloudflared:latest
    command: ["tunnel", "--no-autoupdate", "run", "--token", "${CF_TUNNEL_TOKEN}"]
    # routes the public hostname to http://stylobot:5080 per your CF dashboard config
```

- Zero ports exposed to the host network. Everything traverses the docker network.
- Public hostname binding lives in the CF dashboard against the tunnel ID.
- Easiest path when the legacy site is already containerised.

## Getting the token

1. Sign in to [dash.cloudflare.com](https://dash.cloudflare.com) -> Zero Trust -> Networks -> Tunnels.
2. Create a tunnel; pick a name. Cloudflare gives you a token (long opaque string starting `ey...`).
3. Add a **public hostname** for the tunnel (e.g. `legacy.your-site.com`). Point it at `http://stylobot:5080` (Shape 3) or `http://localhost:5080` (Shape 1).
4. That's the token you pass to stylobot or to the `cloudflared` container.

Free tier covers unlimited tunnels and unlimited bandwidth for HTTP. Paid Zero Trust adds private network access, policies, etc. -- not required for this scenario.

## Why this matters

Old sites *will* be hit by the same scraper-AI mix that hits new ones. They typically can't be rebuilt -- the original devs are gone, the framework is EOL, the database is hand-maintained. The economic alternative is hosting them behind a CDN that promises bot protection (sometimes worse, sometimes priced per request, often vendor-locked).

The brownfield retrofit gives you the same protection surface as a greenfield deployment:

- **Block / rate-limit / throttle by `BotType`** -- the 6.8 defaults route malicious bots to `block-hard`, AI scrapers to `rate-limit-ai`, search engines to `rate-limit-search`, etc. See [`policy-defaults.md`](../src/Mostlylucid.BotDetection/docs/policy-defaults.md).
- **Adaptive scaling** -- when the legacy origin starts slowing down (5xx rate climbs, P95 latency climbs), bot allowances tighten so humans get priority. Useful for fragile old stacks.
- **Honeypot detection** -- catches WordPress wp-admin probes, .env scrapers, etc. even on stacks that don't actually have those paths. See [`honeypot-catalog.md`](../src/Mostlylucid.BotDetection/docs/honeypot-catalog.md).
- **Operator dashboard** -- enable `--enable-api` on stylobot, run `stylobot-ui` as a viewer; you get the full investigate / policy / honeypot tabs without touching the legacy site's templates.

No code touches the legacy site. The old database, the old PHP, the old `.aspx` -- all untouched. Stylobot is purely the door.

## What's between "this is the story" and "ship it as a one-liner"

This works today; what's missing is packaging polish. Tracked separately:

- A bundled `stylobot` Docker image with `cloudflared` already present (current image is detection-only; today's Shape 3 needs both containers).
- A `stylobot-retrofit` install script that pulls the binary, prompts for a token, writes a systemd unit -- so the one-line install matches the "3 steps" story.
- Per-host site profiles (already designed, see `docs/deferred/site-profiles.md`) so the gateway auto-loads e.g. the WordPress simulation pack when a tunnel routes a WordPress domain.

None of those block the underlying retrofit; they're ergonomics.
