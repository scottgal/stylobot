# AI Scraper Detection

## Overview

The `AiScraperContributor` identifies AI bots, LLM training crawlers, and AI-powered scraping services. It detects over 50 known AI bot User-Agents and analyzes modern protocol signals including Cloudflare's "Markdown for Agents" feature, Web Bot Auth (RFC 9421), and AI-specific HTTP headers.

## Detection Layers

### 1. Known AI Bot User-Agents (50+ patterns)

Recognized bots are categorized by purpose:

| Category | Examples | BotType |
|----------|----------|---------|
| **Training** | GPTBot, ClaudeBot, CCBot, Google-Extended, Bytespider | AiBot |
| **Search** | OAI-SearchBot, PerplexityBot, YouBot | GoodBot |
| **Assistant** | ChatGPT-User, Claude-Web, CopilotBot, PerplexityUser | GoodBot |
| **ScrapingService** | Firecrawl, Apify, Diffbot, ScrapingBee | AiBot |

Training bots are classified as `AiBot` (potentially unwanted), while search/assistant bots are classified as `GoodBot` (legitimate real-time retrieval).

**Signal keys:** `aiscraper.detected`, `aiscraper.name`, `aiscraper.operator`, `aiscraper.category`

### 2. Accept: text/markdown (Cloudflare Markdown for Agents)

AI agents increasingly request `text/markdown` in their Accept header, indicating they prefer machine-readable content. This signal was formalized by Cloudflare's "Markdown for Agents" feature (February 2025). When a non-known-bot requests `text/markdown`, it's a strong AI agent indicator.

**Signal key:** `aiscraper.accept_markdown`

### 3. Cloudflare AI Gateway Headers

Requests routed through Cloudflare's AI Gateway include distinctive headers:

- `cf-aig-cache-status` - AI Gateway cache status
- `cf-aig-log-id` - Request log identifier
- `cf-aig-provider` - AI model provider name

**Signal key:** `aiscraper.cloudflare_ai_gateway`

### 4. Web Bot Auth (RFC 9421)

The Web Bot Auth standard uses HTTP Message Signatures for cryptographic bot identity verification. The detector checks for:

- `Signature` header with base64-encoded signature
- `Signature-Input` with `tag="web-bot-auth"`
- `Signature-Agent` identifying the bot operator

Known agents are mapped to friendly names (e.g., `chatgpt.com` -> "ChatGPT", `anthropic.com` -> "Claude").

**Signal keys:** `aiscraper.web_bot_auth`, `aiscraper.web_bot_auth_verified`

### 5. Cloudflare Browser Rendering

Requests from Cloudflare's Browser Rendering API (used by AI agents for JavaScript rendering) include:

- `cf-brapi-request-id`
- `cf-biso-devtools`
- `cf-brapi-devtools`

**Signal key:** `aiscraper.cloudflare_browser_rendering`

### 6. AI Discovery Paths

Requests to AI-specific discovery endpoints indicate an AI agent probing for machine-readable content:

- `/llms.txt` - LLM-optimized site description
- `/llms-full.txt` - Extended LLM content
- `/.well-known/http-message-signatures-directory` - Web Bot Auth key directory

**Signal key:** `aiscraper.ai_discovery_path`

### 7. Jina Reader API

Jina Reader API requests include the `x-respond-with: markdown` header, requesting content conversion for AI consumption.

**Signal key:** `aiscraper.jina_reader`

### 8. Content-Signal Header

The emerging `Content-Signal` header framework allows servers to communicate content metadata to AI agents.

**Signal key:** `aiscraper.content_signal`

## Configuration

YAML manifest: `Orchestration/Manifests/detectors/aiscraper.detector.yaml`

| Parameter | Default | Description |
|-----------|---------|-------------|
| `known_ai_bot_confidence` | 0.95 | Confidence for known AI bot User-Agents |
| `accept_markdown_confidence` | 0.85 | Confidence for Accept: text/markdown |
| `ai_gateway_confidence` | 0.8 | Confidence for CF AI Gateway headers |
| `web_bot_auth_confidence` | 0.95 | Confidence for Web Bot Auth signatures |
| `ai_discovery_path_confidence` | 0.7 | Confidence for AI discovery path access |
| `browser_rendering_confidence` | 0.9 | Confidence for CF Browser Rendering |
| `jina_reader_confidence` | 0.85 | Confidence for Jina Reader API |

## Bot Name Resolution

When a known AI bot is detected (by User-Agent, Web Bot Auth, or other means), the `BotName` field is set on the contribution. This name propagates through the detection pipeline and appears in the dashboard, live feed, and API responses. For example, a request from GPTBot will show "GPTBot" as the visitor identity rather than an anonymous signature hash.

## Signal Deduplication

If a bot is already identified by User-Agent matching, secondary signals (like Accept: text/markdown) do not produce duplicate confidence contributions. The additional signals are still recorded for telemetry but without stacking confidence penalties.
