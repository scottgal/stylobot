# StyloBot CLI

**Self-hosted bot defense. Free forever.** Single binary, 32 detectors including the new `Threat Intelligence` detector, live detection table, daemon mode, Cloudflare Tunnel support. AOT-compiled for sub-30MB footprint across 6 platforms.

## Install

```bash
# macOS / Linux
brew install scottgal/stylobot/stylobot

# Windows
choco install stylobot

# Or download from GitHub Releases
curl -fsSL https://raw.githubusercontent.com/scottgal/stylobot/main/scripts/install.sh | bash
```

## Quick Start

```bash
# Production mode with live detection table
stylobot 5080 http://localhost:3000

# Demo mode with local test surfaces enabled
stylobot 5080 http://localhost:3000 --mode demo

# With Cloudflare Tunnel (instant public URL)
stylobot 5080 http://localhost:3000 --tunnel

# With LLM enrichment
stylobot 5080 http://localhost:3000 --llm openai --llm-key sk-...
stylobot 5080 http://localhost:3000 --llm ollama
```

The current detector set is 49 detectors total, including the `Threat Intelligence` detector.

## Threat Intelligence

The `Threat Intelligence` detector correlates live traffic against CVE-derived fingerprints generated from security
advisories. The goal is to recognize exploit traffic by shape and behavior, not just by a static payload string.

In practice this means:

- advisories are converted into normalized CVE fingerprints
- those fingerprints live alongside normal traffic fingerprints in the same comparison space
- traffic can be matched against known exploit families and advisory-derived clusters

Plan behavior:

- **Free / FOSS**: consumes a hosted, signed, pre-processed threat-intelligence feed
- **Paid plans**: can do the same, or run local ingestion and processing themselves

Paid plans are designed for **zero phone-home** operation when you want it:

- ingest advisories locally
- generate CVE fingerprints locally
- store and match locally
- keep the threat-intelligence pipeline entirely self-hosted

The important distinction is that paid does **not** mean better intelligence by default. It means **independence and
local control**: the same detector concept, but with the option to keep feed ingestion and fingerprint generation fully
inside your own environment.

The CLI now ships with runtime list/model downloads disabled by default as well, so remote feeds and ONNX model fetches
are explicit opt-ins instead of surprise first-run network behavior.

## CLI Reference

```
stylobot <port> <upstream> [options]
```

### Positional Arguments

| Argument | Description |
|----------|-------------|
| `<port>` | Port to listen on (also: `--port <port>`) |
| `<upstream>` | Upstream server URL to proxy to (also: `--upstream <url>`) |

### Options

| Flag | Description | Default |
|------|-------------|---------|
| `--mode <name>` | Detection mode: `demo`, `production` | `production` |
| `--policy <name>` | Action policy: `logonly`, `block`, `throttle`, `challenge` | `block` in production, `logonly` in demo |
| `--threshold <0.0-1.0>` | Bot probability threshold | `0.7` |
| `--cert <path>` | TLS certificate (.pfx or .pem) | - |
| `--key <path>` | TLS private key (with .pem cert) | - |
| `--cert-password <pass>` | PFX certificate password | - |
| `--tunnel [token]` | Cloudflare Tunnel (requires `cloudflared`) | - |
| `--llm <provider>` | LLM provider preset (see below) | - |
| `--llm-key <key>` | LLM API key (also: env `STYLOBOT_LLM_KEY`) | - |
| `--llm-url <url>` | Custom LLM provider base URL | - |
| `--model <name>` | LLM model name (overrides preset default) | - |
| `--config <path>` | Path to appsettings.json override | - |
| `--log-level <level>` | Minimum log level (`Debug`, `Information`, `Warning`) | `Warning` |
| `--verbose` | Show all log output (disables live table) | - |
| `-h`, `--help` | Show help | - |

### Environment Variables

All options can also be set via environment variables:

| Variable | Maps to |
|----------|---------|
| `PORT` | `--port` |
| `UPSTREAM` / `DEFAULT_UPSTREAM` | `--upstream` |
| `MODE` | `--mode` |
| `STYLOBOT_POLICY` | `--policy` |
| `STYLOBOT_LLM_KEY` | `--llm-key` |
| `KNOWN_NETWORKS` | Trusted proxy networks (CIDR, comma-separated) |
| `KNOWN_PROXIES` | Trusted proxy IPs (comma-separated) |
| `STYLOBOT_ALLOW_PUBLIC_METRICS` | Allow remote scraping of `/metrics` |
| `STYLOBOT_*` | Any config via environment variable prefix |

## Daemon Mode

```bash
stylobot start 5080 http://localhost:3000 --policy block   # Background daemon
stylobot status                                             # Check health
stylobot logs                                               # View recent logs
stylobot stop                                               # Graceful shutdown
```

PID file: `~/.config/stylobot/stylobot.pid`

### As a system service

```bash
# Linux (systemd)
sudo cp scripts/stylobot.service /etc/systemd/system/
sudo systemctl enable --now stylobot

# macOS (launchd via Homebrew)
brew services start stylobot
```

## LLM Providers

Any provider, any tier. Bring your own API key. Detection works fully without LLM.

```bash
stylobot 5080 http://localhost:3000 --llm openai --llm-key sk-...
stylobot 5080 http://localhost:3000 --llm groq --llm-key gsk-...
stylobot 5080 http://localhost:3000 --llm gemini --llm-key AIza...
stylobot 5080 http://localhost:3000 --llm ollama                    # local, free
stylobot 5080 http://localhost:3000 --llm llamasharp                # in-process CPU
```

| Provider | Default model | Cost |
|----------|---------------|------|
| `openai` | gpt-4o-mini | ~$0.15/1M tokens |
| `anthropic` | claude-haiku-4-5 | ~$0.25/1M tokens |
| `gemini` | gemini-2.0-flash | Free tier |
| `groq` | llama-3.3-70b | Free tier |
| `mistral` | mistral-small | ~$0.10/1M tokens |
| `deepseek` | deepseek-chat | ~$0.07/1M tokens |
| `ollama` | qwen3:0.6b | Free (local) |
| `llamasharp` | qwen2.5:0.5b | Free (in-process) |
| `azure` | (deployment) | Azure pricing |

Advanced orchestration (fallback chains, budgets, per-use-case routing) via `appsettings.json`. See [configuration docs](../Mostlylucid.BotDetection/docs/configuration.md).

## Cloudflare Tunnel

The `--tunnel` flag creates an instant public URL. Requires `cloudflared`:

```bash
# Install cloudflared
brew install cloudflared            # macOS
sudo apt install cloudflared        # Linux
winget install Cloudflare.cloudflared  # Windows

# Quick tunnel (random URL)
stylobot 5080 http://localhost:3000 --tunnel

# Named tunnel (pre-configured)
stylobot 5080 http://localhost:3000 --tunnel eyJhIjoiNjQ2...
```

The tunnel URL is displayed in the live detection table.

## LLM Tunnel (Contribute Local GPU)

The `llmtunnel` command lets you share your local GPU with a remote StyloBot site. An agent process
wraps your local Ollama instance and registers itself with the StyloBot node registry. The remote
site routes LLM escalation requests through a Cloudflare tunnel to your machine.

```bash
# Anonymous quick tunnel (ephemeral URL, good for testing)
stylobot llmtunnel

# Named permanent tunnel (stable hostname, good for production)
stylobot llmtunnel eyJhIjoiNjQ2...

# Restrict which models are exposed
stylobot llmtunnel --models llama3.2:3b,qwen2.5:14b

# Raise concurrency / context limits
stylobot llmtunnel --max-concurrency 4 --max-context 16384
```

### LLM Tunnel Options

| Flag | Description | Default |
|------|-------------|---------|
| `--ollama <url>` | Local Ollama base URL | `http://127.0.0.1:11434` |
| `--models <csv>` | Comma-separated model allowlist (empty = all) | all |
| `--max-concurrency <n>` | Max concurrent inference requests | `2` |
| `--max-context <tokens>` | Max context window in tokens | `8192` |
| `--agent-port <port>` | Loopback port for the agent HTTP listener | random |

### Security Notes

- Anonymous tunnel URLs are **not** credentials. The connection key printed on startup is.
- Connection keys are sensitive — treat them like API keys.
- Named Cloudflare tunnels provide stable hostnames; quick tunnels are ephemeral.
- Re-import the connection key each time a quick tunnel restarts.
- Registered nodes can be revoked via `DELETE /api/v1/llm-nodes/{nodeId}`.
- Every inference request is signed and replay-protected (30-second TTL).
- Raw Ollama endpoints are never exposed through the tunnel.
- Optional AES-GCM payload encryption can be enabled via appsettings for stronger privacy.

## Live Detection Table

The default output is a Spectre.Console live table showing:
- **Status line**: uptime, request count, threat count
- **Config panel**: mode, policy, upstream, tunnel URL
- **Throughput**: req/s, avg/max detection time, bot percentage
- **Top endpoints**: most-hit paths
- **Top bots**: most-seen bot signatures
- **Detection feed**: per-request verdict with risk score, action, detector, bot name

Use `--verbose` for traditional log output (Serilog).

## Configuration

The CLI reads configuration from (in priority order):
1. CLI flags (`--policy block`)
2. Environment variables (`STYLOBOT_POLICY=block`)
3. `--config <path>` override file
4. `appsettings.{mode}.json` (mode-specific)
5. `appsettings.json` (base)

Full configuration reference: [`Mostlylucid.BotDetection/docs/configuration.md`](../Mostlylucid.BotDetection/docs/configuration.md)

Example appsettings: [`Mostlylucid.BotDetection/docs/appsettings.typical.json`](../Mostlylucid.BotDetection/docs/appsettings.typical.json)

## Endpoints

| Path | Description |
|------|-------------|
| `/health` | Health check (minimal JSON: `{"status":"healthy"}`) |
| `/metrics` | Prometheus metrics, loopback-only by default |
| `/test-client-side.html` | Demo/learning mode browser test page |
| `/api/bot-detection/test-status` | Demo/learning mode server verdict + one-time callback token |
| `/api/bot-detection/client-result` | Demo/learning mode signed callback endpoint |
| `/**` | All other paths proxied to upstream with bot detection |

## Platforms

| Platform | Binary | Size |
|----------|--------|------|
| Linux x64 | `stylobot-linux-x64.tar.gz` | ~29MB |
| Linux ARM64 | `stylobot-linux-arm64.tar.gz` | ~27MB |
| Windows x64 | `stylobot-win-x64.zip` | ~26MB |
| Windows ARM64 | `stylobot-win-arm64.zip` | ~25MB |
| macOS Intel | `stylobot-osx-x64.tar.gz` | ~22MB |
| macOS Apple Silicon | `stylobot-osx-arm64.tar.gz` | ~29MB |

## Links

- [Main README](../README.md)
- [Configuration reference](../Mostlylucid.BotDetection/docs/configuration.md)
- [Full config example](../Mostlylucid.BotDetection/docs/appsettings.full.json)
- [Detection strategies](../Mostlylucid.BotDetection/docs/detection-strategies.md)
- [Action policies](../Mostlylucid.BotDetection/docs/action-policies.md)
- [GitHub Releases](https://github.com/scottgal/stylobot/releases)
- [stylobot.net](https://stylobot.net)
