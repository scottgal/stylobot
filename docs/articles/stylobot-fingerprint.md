# StyloBot - what if client behaviour was a vector?

# Introduction
I started building StyloBot following solving a problem for a customer; how do you ensure only legitimate clients can access endpoints and use APIs WITHOUT the britlness of current methods.

# Quick Start
StyloBot is ENTIRELY FREE TO RUN...in future I'll sell realtime management and reporting (to try and you know...eat). 

All the source is here https://github.com/scottgal/stylobot

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

Then just run it `stylobot 5080 http://localhost:3000` and voila your upstream site is *listening* (use --mode block to actuall block too).


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

> Deep insight, but slow and expensive — used sparingly

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

> Visibility layer — expensive but necessary

---

### 9. Custom glue / edge logic / lambdas

* **Mode:** Mixed
* **Latency:** varies
* **Cost:** hidden but real (dev time + infra)
* **Complexity:** high over time

> The “we had to fix gaps” layer

---

SO, simple, right. This is a market which does cover MOST of the bases you need. However, it's expensive, can be slow, prone to false positives  (read actual users having bad experiences). 

# THE BIG PROBLEM
Notice anything about all the market players in the previous example; lots of needing US / IP to remain identifiable, or need manua; config per endpoint to avoid blocking 'legitimate' traffic. They're also SLOW...if every requerst goes through this pipeline that's likely a significant chunk of your time spent processing requests & responding. 

## The Competition

So these traditional plyers work...up to a point. At some point no matter how much you really spend; you won't block them AND you'll be spending more than you save. 

Just like our defensive systems on the market above offer different types of protection (at different cost, complexity), 'bots' have their own hierarchy.

---

## Bot sophistication vs detection layers

### 1. Dumb / noisy bots

(curl, scanners, brute force, invalid paths)

* **Fail2Ban:** works well
* **WAF:** works well
* **Bot management:** trivial
* **Rate limiting:** works well

**Failure point:** none — everything catches these

Really these are the scripts which have been around since the start of the web (perl FTW!) they're the 'go to site, scrape content' tyes. EASY to identify (single endpoint, same ip generally, same UA...). 

---

### 2. Basic scripted bots

(rotating UA, valid endpoints, simple scraping)

* **Fail2Ban:** starts failing
* **WAF:** still effective
* **Bot management:** effective
* **Rate limiting:** depends on tuning

**Failure point:** systems relying on obvious mistakes

Starting to get harder. Now you need to identify known patterns, process traffic later etc...

---

### 3. Headless browser bots

(Puppeteer/Playwright, JS execution, real flows)

* **Fail2Ban:** ineffective
* **WAF:** limited
* **Bot management:** primary layer
* **Rate limiting:** weakening

**Failure point:** anything based on request correctness or signatures

Easy ONLY because they're being used legitimately most of the time (e.g. scraping, SEO, etc.) however this is false positive city...telling legit from illegitimate traffic is HARD.

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

These bots behave “correctly” and evolve - LLMs can adapt to standard attempts to block them (CATCHA solvers, randomizers). 

THIS is where StyloBot is aimed...right NOW these bots are expensive to operate at scale. THAT IS CHANGING. 

---

As we move down the list  we start to move through time...we moved from simple identity (block IP) through starting to need to understand huge quantities of traffic and log files. 

To defend from INTELLIGENT scrapers like we see at level 5 you need INTELLIGENT detection AND protection.


## Potential Solution
In previous articles I've written about my 'Behavioural Inference' systems. In essence these are a CHEAT that became a feature. 

The problem - single 'sensors' are easy to bypass now. 

In all the examples above the only constant is how they attempt to decieve. Changing factors about their identity (headers ,IP, UA etc), changing timings / endpoints etc. So any ONE sensor can be bypassed, combining them gives more sensitivity (read catches more bots). 
HOWEVER in these static systems false positives start to grow as you increase sensors; if a single one is enough to trigger a false positive then you have a problem. 

What behavioural iunference does is *profile* -> *characterize*-> *remember* whether it's in lucirRAG / StyloBot etc..that's all it does really. 

In StyloBot it's remembering these behavioural vectors, THAT is what a client *behaviour* becomes (note behaviour NOT identity...). 

To IT you are a projection over a 130+vector dimensional space.

![img.png](img.png)

# StyloBot 

In short, StyloBot is a behavioural inference engine applied to web traffic. 
It uses a large vector space to characterise and identify the class and type of web requests in order to identify automations vs humans. 

## How it differs from the market.
As we saw in the market leaders list earlier they all have some commonalities, either they rely on simple static rules (potentially with updated lists constantly like OWASP) or they analyze TONS of real traffic and are heavy beasts. 

StyloBot aims to have a distribution model like Fail2Ban (run an exe) along with the power of the large enterprise models. 

It ALSO downloads lists of user agents, cves, exploits, and other indicators of compromise to enhance its detection capabilities. HOWEVER these are just a factor in a decision.

In all StyloBot runs 50 (ish) 'contributors' they are little bits of code 

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

Each detector indicates what it is, if it depends on others (signals) and what results it gives. 

> NOTE: This is a core concept for how I built it. StyloBot is a LARGE system with MINIMAL concepts. So adding detectors is SIMPLE. 


Using my [mostlylucid.ephemeral framework ](https://github.com/scottgal/mostlylucid.atoms) it emits what I call 'signals' these are tiny strings like 'ua.score=0.75' which act BOTH as metadata for the request AND logging / diagnostic data. This enables me to tune the system very finely as the Code LLM / the system itself can use these signals to identify efficiencies (auto-tuning). 

Another trick Ephemeral adds is LFU / sliding window processing. It's self-limiting so using Least-Frequently-Used it lets us drop human requests while retaining a window if a *future* request passes a bot threshold; then we can look back and reprocess the older ones for clues. 

*It doesn't always run all 50 detectors* - this is the point. The 50 is the CAPABILITY it only needs what it needs to do its job. That's typically 5-7 SUPER fast (sub millisecond) set of inital detectors and fingerprinters. 

From that fingerprint it can decide what sort of thing you are...AND what the like next requests will be (content->resource pathing). Then use THAT to decide what the next request it expects and excalate if required.

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