# Mostlylucid.BotDetection.Llm.Tunnel

**GPU tunnel relay for StyloBot LLM inference.** Registers local GPU LLM nodes and routes cloud LLM inference to them over a Cloudflare tunnel — so `Mostlylucid.BotDetection.Llm.Cloud` providers can hand long-running inference to a local GPU without exposing it directly.

[![NuGet](https://img.shields.io/nuget/v/Mostlylucid.BotDetection.Llm.Tunnel.svg)](https://www.nuget.org/packages/Mostlylucid.BotDetection.Llm.Tunnel)
[![GitHub](https://img.shields.io/badge/GitHub-scottgal%2Fstylobot-blue)](https://github.com/scottgal/stylobot)

---

## Install

```bash
dotnet add package Mostlylucid.BotDetection.Llm.Tunnel
```

This is a **hard dependency of `Mostlylucid.BotDetection.Api`** (its LLM node controller endpoints), so Api consumers get it automatically.

## What it provides

- `ILlmNodeRegistry` / `InMemoryLlmNodeRegistry` — the local GPU node registry
- `LlmNodeImporter`, `LocalLlmProviderProbe` — node discovery + probing
- `LocalLlmTunnelClientProvider`, `LocalLlmTunnelCrypto` — the Cloudflare-tunnel client + crypto
- `LocalLlmAgentEndpoints` / `LlmNodeControllerEndpoints` (via `BotDetection.Api`) — the API surface for registering and routing to local nodes

## License

AGPL-3.0-only. See [LICENSE](https://github.com/scottgal/stylobot/blob/main/LICENSE).
