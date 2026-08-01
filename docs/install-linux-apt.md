# Linux APT Install (Debian/Ubuntu)

StyloBot is published to a Cloudsmith apt repository on every stable release.

## Install

```bash
# 1. Add the repository and GPG key (one time)
curl -1sLf 'https://dl.cloudsmith.io/public/mostlylucid/stylobot/setup.deb.sh' | sudo bash

# 2. Install
sudo apt update
sudo apt install stylobot
```

Supports: Debian 11+, Ubuntu 20.04+, and any apt-compatible distro on x64 or ARM64.

## Upgrade

```bash
sudo apt update && sudo apt upgrade stylobot
```

## Verify the binary

```bash
which stylobot      # /usr/local/bin/stylobot
stylobot --version
```

## Quick start after install

```bash
# Demo mode (verbose, no blocking)
stylobot 5080 https://www.example.com

# Production mode (blocking enabled)
stylobot 5080 https://www.example.com --mode production

# With Cloudflare public tunnel
stylobot 5080 https://www.example.com --tunnel

# Full reference
stylobot man
```

## Default behavior (before you configure anything)

Verified against `Mostlylucid.BotDetection.Console/Program.cs` and its bundled
`appsettings.json` / `appsettings.production.json` — not assumed.

**Run `stylobot <port> <upstream>` with no other flags and you get `--mode demo`:**
detection runs against every request (all detectors, full logging), but
`DefaultActionPolicyName` is `logonly` — **nothing is ever blocked, throttled, or
delayed**. Every request is allowed through untouched; you only see what stylobot
*would have* flagged, in the logs / dashboard. `EnableLearning` is also off in this
mode. This is the safe default for a first run against real traffic — it will not
affect your site's behavior.

**Pass `--mode production` (or `--tunnel` without an explicit `--mode demo`) and the
posture changes completely:** `DefaultActionPolicyName` becomes `block`, with a real
risk-based transition ladder —

| Detected risk | Action |
|---|---|
| > 95% | `block-hard` — immediate 403, no further analysis |
| > 70% | `block` — 403 "Access denied - bot detected" |
| > 50% | `throttle` — rate-limited (10 req / 60s default) |
| < 30% | `logonly` — allowed, just logged |

`EnableLearning` also turns on. **This is the gotcha to watch for:** going straight
from a quiet demo trial to `--tunnel` (which exposes the port publicly and defaults
to production mode) silently flips you from "log only" to "actively blocking 403s"
in one step — there's no separate warning at that point. If you want public exposure
without blocking, pass `--mode demo` explicitly alongside `--tunnel`.

Precedence for the mode/policy choice: `--policy` / `--mode` CLI flags >
`STYLOBOT_POLICY` / `MODE` env vars > the `--tunnel`-implies-production default >
plain demo default. See `stylobot man` or `stylobot --help` for the full flag list.

TLS is off by default too (plain HTTP) until you pass `--cert` — see
[`tls-configuration.md`](tls-configuration.md) for cert/key setup, multi-domain
limits, and common SSL-error gotchas.

## Manual download

If you prefer not to use the apt repo, download the binary directly from the
[GitHub Releases](https://github.com/scottgal/stylobot/releases) page:

| Platform | File |
|----------|------|
| Linux x64 | `stylobot-linux-x64.tar.gz` |
| Linux ARM64 (Raspberry Pi 4/5) | `stylobot-linux-arm64.tar.gz` |

```bash
tar xzf stylobot-linux-x64.tar.gz
chmod +x stylobot
sudo mv stylobot /usr/local/bin/
```
