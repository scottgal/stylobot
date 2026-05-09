# StyloBot - what if client behaviour was a vector?

# Introduction
I started building StyloBot to solve a customer problem: how do you ensure only legitimate clients can access endpoints and use APIs without the brittleness of current methods?

# Quick Start
StyloBot is free to run. Future realtime management and reporting may be commercial, but the core detection engine is intended to stay lightweight and easy to operate.

All the source is here: https://github.com/scottgal/stylobot

To install it:
**macOS (Homebrew)**
```bash
brew install scottgal/stylobot/stylobot
stylobot 5080 http://localhost:3000
```

**Linux (apt - Debian/Ubuntu)**
```bash
curl -1sLf 'https://dl.cloudsmith.io/public/mostlylucid/stylobot/setup.deb.sh' | sudo bash
sudo apt update && sudo apt install stylobot
stylobot 5080 http://localhost:3000
```

**Linux (manual / ARM64)**
```bash
# Download from GitHub Releases: stylobot-linux-x64.tar.gz or stylobot-linux-arm64.tar.gz
tar xzf stylobot-linux-x64.tar.gz && chmod +x stylobot && sudo mv stylobot /usr/local/bin/
stylobot 5080 http://localhost:3000
```

**Docker**
```bash
docker run --rm -p 8080:8080 -e DEFAULT_UPSTREAM=http://host.docker.internal:3000 \
  scottgal/stylobot-gateway:latest
```

**NuGet (embed as ASP.NET Core middleware)**
```bash
dotnet add package mostlylucid.botdetection
dotnet add package mostlylucid.botdetection.ui
```

```csharp
builder.Services.AddStyloBot(dashboard => {
    dashboard.AllowUnauthenticatedAccess = true; // dev only
});

app.UseRouting();
app.UseStyloBot();   // broadcast, detection, dashboard: correct ordering guaranteed
app.MapControllers();
```

Dashboard at `/_stylobot`. Detection at `~150µs` per request from first request.

---

Then run it with `stylobot 5080 http://localhost:3000` and your upstream site is listening (use `--mode block` to actually block traffic too).


[TOC]

## The Current Market
So early in the article? Yup...really the current market shows the issues stylobot attempts to solve

---

### 1. Fail2Ban / log-based banning

* **Mode:** Post (reactive)
* **Latency:** seconds → minutes
* **Cost:** very low (free + ops time)
* **Complexity:** low

> Cheap, simple, but always after the fact

---

### 2. WAF (Cloudflare WAF, AWS WAF, Azure WAF)

* **Mode:** Active (inline)
* **Latency:** ~1–10 ms
* **Cost:** low → medium (rules + request volume)
* **Complexity:** low → medium (rule tuning)

> Fast and cheap-ish, but only for known patterns

---

### 3. Bot Management (Cloudflare Bot Mgmt, DataDome, HUMAN, Akamai, CHEQ)

* **Mode:** Active (inline + challenges)
* **Latency:** ~5–50 ms
* **Cost:** medium → high (often traffic-based or tiered)
* **Complexity:** medium → high (tuning, false positives, UX impact)

> Powerful but expensive, and can affect user experience

---

### 4. Rate Limiting / API Gateway controls

* **Mode:** Active (inline)
* **Latency:** ~1–5 ms
* **Cost:** low → medium (usually bundled but scales with usage)
* **Complexity:** medium (per-endpoint tuning)

> Cheap control, but blunt instrument

---

### 5. DDoS Protection (Cloudflare, Akamai, Fastly, AWS Shield)

* **Mode:** Active (edge/network)
* **Latency:** ~1–5 ms
* **Cost:** medium → very high (especially at scale / enterprise tiers)
* **Complexity:** medium (mostly managed)

> Essential infra layer, but not behavioural

---

### 6. Fraud / Risk Scoring (Sift, Forter, Riskified, Stripe Radar)

* **Mode:** Mixed (inline + post)
* **Latency:** ~50–300 ms inline
* **Cost:** high (per transaction / % of revenue / SaaS pricing)
* **Complexity:** high (integration + tuning + ops)

> Deep insight, but slow and expensive used sparingly

---

### 7. Device Fingerprinting (FingerprintJS, ThreatMetrix, iovation)

* **Mode:** Active (client + inline)
* **Latency:** ~10–100 ms
* **Cost:** medium → high (per request/session pricing)
* **Complexity:** high (privacy, evasion, integration)

> Identity-heavy, comes with compliance and cost baggage

---

### 8. SIEM / Observability (Splunk, Datadog, Elastic, Sentinel)

* **Mode:** Post
* **Latency:** seconds → minutes
* **Cost:** very high (data ingestion is the killer)
* **Complexity:** very high (queries, alerts, maintenance)

> Visibility layer expensive but necessary

---

### 9. Custom glue / edge logic / lambdas

* **Mode:** Mixed
* **Latency:** varies
* **Cost:** hidden but real (dev time + infra)
* **Complexity:** high over time

> The “we had to fix gaps” layer

---

So yes, the market covers most of the bases you need. However, it is expensive, can be slow, and can create false positives that affect real users.

# THE BIG PROBLEM
Notice anything about all the market players in the previous example? Many need users or IPs to remain identifiable, or require manual configuration per endpoint to avoid blocking legitimate traffic. They are also slow. If every request goes through this pipeline, it consumes a significant chunk of your time processing and responding.

## The Competition

So these traditional players work up to a point. At some point, no matter how much you spend, you will not block them, and you will be spending more than you save.

Just like our defensive systems on the market above offer different types of protection (at different cost, complexity), 'bots' have their own hierarchy.

---

## Bot sophistication vs detection layers

### 1. Dumb / noisy bots

(curl, scanners, brute force, invalid paths)

* **Fail2Ban:** works well
* **WAF:** works well
* **Bot management:** trivial
* **Rate limiting:** works well

**Failure point:** none everything catches these

These are the scripts that have been around since the start of the web. They are the "go to site, scrape content" type. Easy to identify: single endpoint, same IP generally, same user agent.

---

### 2. Basic scripted bots

(rotating UA, valid endpoints, simple scraping)

* **Fail2Ban:** starts failing
* **WAF:** still effective
* **Bot management:** effective
* **Rate limiting:** depends on tuning

**Failure point:** systems relying on obvious mistakes

It gets harder here. Now you need to identify known patterns and process traffic later.

---

### 3. Headless browser bots

(Puppeteer/Playwright, JS execution, real flows)

* **Fail2Ban:** ineffective
* **WAF:** limited
* **Bot management:** primary layer
* **Rate limiting:** weakening

**Failure point:** anything based on request correctness or signatures

This is hard because they are often used legitimately for scraping, SEO, or testing. Telling legitimate from illegitimate traffic is genuinely difficult.

---

### 4. Stealth bots

(proxy rotation, residential IPs, fingerprint spoofing)

* **Fail2Ban:** ineffective
* **WAF:** largely ineffective
* **Bot management:** starts to struggle
* **Rate limiting:** ineffective if distributed

**Failure point:**

* IP reputation
* static fingerprinting
* threshold-based controls

This is where false positives start rising if you push harder all of your normal identifiers start to fall off *you need to be able to identify the same client from with deceptive identity*. 

---

### 5. Adaptive / LLM-directed bots

(slow, distributed, learn site behaviour, adjust dynamically)

* **Fail2Ban:** irrelevant
* **WAF:** ineffective
* **Bot management:** inconsistent
* **Rate limiting:** ineffective

**Failure point:**

* anything assuming repeatability
* anything assuming known patterns
* anything assuming “bot-like” behaviour

These bots behave correctly and evolve. LLMs can adapt to standard attempts to block them, including CAPTCHA solvers and randomizers.

This is where StyloBot is aimed. Right now these bots are expensive to operate at scale, but that is changing.

---

As we move down the list, the problem shifts from simple identity controls such as blocking an IP address to understanding large quantities of traffic and log data.

To defend against intelligent scrapers at this level, you need intelligent detection and protection.


## Potential Solution
In previous articles I have written about my "Behavioural Inference" systems. In essence, they are a cheat that became a feature.

The problem is that single sensors are easy to bypass now.

In all the examples above, the only constant is how they attempt to deceive: changing identity factors such as headers, IP, and user agent, or changing timings and endpoints. Any one sensor can be bypassed; combining them gives more sensitivity and catches more bots.
However, in static systems false positives start to grow as you increase sensors. If a single one is enough to trigger a false positive, you have a problem.

What behavioural inference does is profile, characterise, and remember. That is all it does.

In StyloBot, those behavioural vectors are what client behaviour becomes. The system is remembering behaviour, not identity.

To the system, you are a projection over a 130-plus-dimensional vector space.

![img.png](img.png)

# StyloBot 

In short, StyloBot is a behavioural inference engine applied to web traffic.
It uses a large vector space to characterise and identify the class and type of web requests in order to distinguish automation from human traffic.

## How it differs from the market.
As we saw in the market leaders list earlier, they all have some commonalities. Either they rely on simple static rules, with constantly updated lists, or they analyse large volumes of real traffic and are heavy systems.

StyloBot aims to have a distribution model like Fail2Ban, with the power of large enterprise models.

It also downloads lists of user agents, CVEs, exploits, and other indicators of compromise to enhance its detection capabilities. However, these are only one factor in a decision.

StyloBot runs roughly 50 contributors, each a small piece of code.

```csharp 
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Execution stage for detectors. Detectors in the same stage run in parallel.
///     Higher stages wait for lower stages to complete.
/// </summary>
public enum DetectorStage
{
    /// <summary>
    ///     Raw signal extraction (UA, headers, IP, client-side).
    ///     No dependencies on other detectors.
    /// </summary>
    RawSignals = 0,

    /// <summary>
    ///     Behavioral analysis that may depend on raw signals.
    ///     Runs after Stage 0 completes.
    /// </summary>
    Behavioral = 1,

    /// <summary>
    ///     Meta-analysis layers (inconsistency detection, risk assessment).
    ///     Reads signals from stages 0 and 1.
    /// </summary>
    MetaAnalysis = 2,

    /// <summary>
    ///     AI/ML-based detection that can use all prior signals.
    ///     Runs last, can learn from all other signals.
    /// </summary>
    Intelligence = 3
}

/// <summary>
///     Interface for bot detection strategies
/// </summary>
public interface IDetector
{
    /// <summary>
    ///     Name of the detector
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Execution stage for this detector.
    ///     Detectors in the same stage run in parallel.
    ///     Higher stages wait for lower stages to complete.
    /// </summary>
    DetectorStage Stage => DetectorStage.RawSignals;

    /// <summary>
    ///     Analyze an HTTP request for bot characteristics.
    ///     Legacy method - prefer DetectAsync with DetectionContext.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detection result with confidence score and reasons</returns>
    Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Analyze an HTTP request for bot characteristics using shared context.
    ///     Detectors should read signals from prior stages and write their own signals.
    /// </summary>
    /// <param name="detectionContext">Shared detection context with signal bus</param>
    /// <returns>Detection result with confidence score and reasons</returns>
    Task<DetectorResult> DetectAsync(DetectionContext detectionContext)
    {
        // Default implementation for backward compatibility
        return DetectAsync(detectionContext.HttpContext, detectionContext.CancellationToken);
    }
}

/// <summary>
///     Result from an individual detector
/// </summary>
public class DetectorResult
{
    /// <summary>
    ///     Confidence score from this detector (0.0 to 1.0)
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    ///     Reasons found by this detector
    /// </summary>
    public List<DetectionReason> Reasons { get; set; } = new();

    /// <summary>
    ///     Bot type if identified
    /// </summary>
    public BotType? BotType { get; set; }

    /// <summary>
    ///     Bot name if known
    /// </summary>
    public string? BotName { get; set; }
}
```

Each detector indicates what it is, whether it depends on other signals, and what results it gives.

> NOTE: This is a core concept for how I built it. StyloBot is a large system with minimal concepts, so adding detectors remains simple.


Using my [mostlylucid.ephemeral framework ](https://github.com/scottgal/mostlylucid.atoms) it emits what I call "signals": tiny strings like `ua.score=0.75` that act as both request metadata and logging or diagnostic data. This enables fine-grained tuning because the code or the system itself can use these signals to identify efficiencies.

Another trick Ephemeral adds is LFU and sliding-window processing. It is self-limiting, so Least-Frequently-Used eviction lets us drop human requests while retaining a window if a future request later crosses a bot threshold; then we can look back and reprocess the older ones for clues.

It does not always run all 50 detectors. That is the point. Fifty is the capability ceiling, not the usual runtime cost. Typically it only needs 5 to 7 very fast initial detectors and fingerprinting stages.

From that fingerprint it can decide what sort of traffic you are and what the next requests are likely to be. It can then decide what it expects next and escalate if required.

| Layer | Detectors | What it catches |
|-------|-----------|-----------------|
| **Identity** | Signature, HeaderCorrelation, Periodicity | UA rotation, identity factors, temporal patterns |
| **Protocol** | TLS (JA3/JA4), TCP/IP (p0f), HTTP/2, HTTP/3, Transport, StreamAbuse | Spoofed browser fingerprints, protocol inconsistencies |
| **Behavioral** | Waveform, SessionVector, AdvancedBehavioral, CacheBehavior, CookieBehavior, ResourceWaterfall, ContentSequence | Timing patterns, Markov chains, missing assets, page-load sequence divergence |
| **Content** | UserAgent, Header, AiScraper, Haxxor, SecurityTool, VersionAge | Known bots, attack payloads, impossible browser versions |
| **Network** | IP, GeoChange, ResponseBehavior, MultiLayerCorrelation, CveProbe | Datacenter IPs, impossible travel, CVE scanning, cross-layer mismatches |
| **Intelligence** | FastPathReputation, ReputationBias, TimescaleReputation, Cluster, Similarity, Intent | Historical reputation, Leiden clustering, HNSW similarity, threat scoring |
| **Ad Fraud** | ClickFraud, PiiQueryString | IAB SIVT: datacenter/VPN/headless on paid traffic, referrer spoofing, immediate bounce |claud
| **AI** | Heuristic, HeuristicLate, LLM | 50-feature model (<1ms), optional LLM for ambiguous cases |
| **Client** | ClientSide, FingerprintApproval, ChallengeVerification | JS timing probes, headless detection, PoW challenges |

# What if client behaviour was a vector?
So now to get to the POINT. Now we have these 50 detectors (and hundreds of 'signals') we have a LOT of metadata about our clients. 

Remember this from earlier? This is a *projection* (https://en.wikipedia.org/wiki/Projection_(linear_algebra)) of the underlying vector space from the contributors we jsut saw. 
So this is essentially a 'low resolution' image of the fingerprint. 

WHY? Well suddenly your bots aren't just a bunch of numbers. They're SHAPES. This shapes are DIFFERENT to human ones. 

![img.png](img.png)

Then we can combine that with tracking across ALL your sessions (don't worry the system collects ZERO PII). By looking at a single session a client might look totally human (might even be a recording of a human) HOWEVER...sensitivity across TIME (looking fr automated cadences, even human fingerprints which are USED as bots later). 

## Odd Implications
Note what I DIDN'T say...I didn't say 'once set up' or 'when properly configured' because that's StyloBot's secret...It has a good default set but *it learns*. 

As it runs it starts to profile *your traffic* and understand *your users*...not creepily but it works out what endpoints request patterns, timings look like for your human vs your automated traffic. 

You can THEN decide / let the system take care of it (set a bot threshold of say 0.8 for most and 0.6 for secure). 
