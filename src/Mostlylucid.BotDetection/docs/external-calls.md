# External Network Calls — Compliance Documentation

This document inventories every outbound network call that StyloBot makes, when it happens, what data is transmitted, and the PII implications.

**Last updated:** 2026-04-22 (v6.0.0-beta1)

---

## Outbound Calls

### 1. Verified Bot IP Range Refresh

| Field | Value |
|-------|-------|
| **Target** | `developers.google.com/static/search/apis/ipranges/googlebot.json`, `bing.com/toolbox/bingbot.json`, `openai.com/gptbot-ranges.json` |
| **When** | Application startup, then every 24 hours |
| **Method** | GET |
| **Data sent** | None (fetches public JSON) |
| **Data received** | IP CIDR ranges for verified search engine bots |
| **PII risk** | None |
| **Can be disabled** | Remove bot entries from `VerifiedBotRegistry` config. Detection still works without verified bot IP validation. |

### 2. GeoIP Database Download (DataHub)

| Field | Value |
|-------|-------|
| **Target** | `datahub.io/core/geoip2-ipv4/r/geoip2-ipv4.csv` |
| **When** | Startup, only if no local GeoIP database exists |
| **Method** | GET |
| **Data sent** | None |
| **Data received** | GeoIP CSV database (~20MB) |
| **PII risk** | None |
| **Can be disabled** | Pre-download the database and place in `data/GeoLite2-City.csv`. The download is skipped when the file exists. |

### 3. MaxMind GeoLite2 Update

| Field | Value |
|-------|-------|
| **Target** | `download.maxmind.com` |
| **When** | On schedule, only if `GeoLite2Options.AccountId` and `LicenseKey` are configured |
| **Method** | GET with license key authentication |
| **Data sent** | MaxMind account credentials |
| **Data received** | GeoLite2 database update |
| **PII risk** | None (credentials are the customer's own MaxMind account) |
| **Can be disabled** | Don't configure `AccountId`/`LicenseKey`. Uses DataHub fallback instead. |

### 4. Project Honeypot DNS Lookup

| Field | Value |
|-------|-------|
| **Target** | `dnsbl.httpbl.org` via DNS |
| **When** | Slow-path detection, per unique client IP |
| **Method** | DNS A record lookup (`{access_key}.{reversed_ip}.dnsbl.httpbl.org`) |
| **Data sent** | Client IP address (encoded in DNS query), Project Honeypot access key |
| **Data received** | Threat type, threat score, last seen days |
| **PII risk** | **Client IP address** transmitted to third-party DNS service |
| **Can be disabled** | Set `BotDetection:ProjectHoneypot:Enabled = false` (default: false). The `ProjectHoneypotContributor` is skipped entirely. |

### 5. Bot List Update

| Field | Value |
|-------|-------|
| **Target** | Configured URL (default: none) |
| **When** | Cron schedule (default: daily 2 AM UTC via `UpdateSchedule`) |
| **Method** | GET |
| **Data sent** | None |
| **Data received** | Known bot signature list |
| **PII risk** | None |
| **Can be disabled** | Don't configure `UpdateSchedule`. No outbound call is made. |

### 6. LLM Inference — Local (Ollama / LlamaSharp)

| Field | Value |
|-------|-------|
| **Target** | Configured Ollama endpoint (e.g., `http://localhost:11434`) or in-process LlamaSharp |
| **When** | LLM escalation for edge-case bot classification, holodeck response generation |
| **Method** | POST (Ollama HTTP API) or in-process call (LlamaSharp) |
| **Data sent** | Pseudonymized request metadata: User-Agent, headers, request path, method. **No raw IP address.** IPs are hashed via `SignatureHashKey` before any LLM sees them. |
| **Data received** | Classification result (bot/human/uncertain) or generated holodeck content |
| **PII risk** | **Pseudonymized only.** UA strings and paths are included but these are not PII under most frameworks. IPs are HMAC-hashed. |
| **Can be disabled** | Set `BotDetection:AiDetection:Enabled = false`. All LLM features are opt-in. |

### 7. LLM Inference — Cloud Providers

| Field | Value |
|-------|-------|
| **Target** | Anthropic API (`api.anthropic.com`), OpenAI API (`api.openai.com`), Google Gemini API (`generativelanguage.googleapis.com`) |
| **When** | Same as local LLM, only when a cloud provider is configured |
| **Method** | POST with API key authentication |
| **Data sent** | Same pseudonymized metadata as local LLM, plus API key |
| **Data received** | Classification result |
| **PII risk** | **Same as local LLM** (pseudonymized). Additionally, data transits to third-party cloud. Customer must evaluate their own data processing agreements with the cloud provider. |
| **Can be disabled** | Don't configure cloud LLM provider. Use local Ollama/LlamaSharp instead. |

### 8. Project Honeypot Reporting

| Field | Value |
|-------|-------|
| **Target** | `projecthoneypot.org` |
| **When** | After high-confidence bot detection, if reporting is enabled |
| **Method** | HTTP POST |
| **Data sent** | **Client IP address**, detected threat type |
| **Data received** | Confirmation |
| **PII risk** | **Client IP address** transmitted to third-party service |
| **Can be disabled** | Set `Holodeck:ReportToProjectHoneypot = false` (default: false). Reporting is opt-in. |

---

## What Does NOT Make External Calls

The following components are fully local with zero outbound network activity:

- **Detection pipeline** — all 45+ detectors run locally against request data
- **SQLite persistence** — sessions, signatures, reputation, beacons, weights — all local files
- **Dashboard** — served from embedded resources, no CDN, no external analytics, no tracking scripts
- **Session vector analysis** — Markov chains, HNSW similarity search — all in-process
- **Entity resolution** — merge/split/convergence operations — local
- **Beacon tracking** — canary generation, storage, contributor scanning — local SQLite
- **Holodeck responses** — static templates are local; LLM generation uses local Ollama (see item 6)
- **Compliance packs** — embedded YAML files, no external fetch
- **Action policies** — block, throttle, challenge, holodeck — all local logic

---

## PII Summary

| Data type | Where it goes | Default state |
|-----------|--------------|---------------|
| Client IP address | Project Honeypot DNS (item 4) | **Disabled** by default |
| Client IP address | Project Honeypot reporting (item 8) | **Disabled** by default |
| Pseudonymized request metadata | LLM provider (items 6, 7) | **Disabled** by default (LLM is opt-in) |

**With all defaults:** StyloBot makes zero calls that transmit any client data. The only outbound calls are fetching public IP range lists (items 1, 2) and optionally the MaxMind database (item 3, requires explicit configuration).

---

## Verification

The soak test program (`scripts/soak/`) captures all outbound connections from the SUT every 5 minutes and produces an `external-call-audit.md` artifact verifying that only documented calls were made during the test run.
