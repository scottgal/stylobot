Signals define receptors.
Receptors activate molecules.
Molecules assemble workflows.
Styloflow evolves architecture from behaviour.

Think of BDF as:

> **“A declarative description of how a client behaves over time.”**

---

## 1. Goals

BDF should let you:

1. **Describe behaviour**: not just single requests, but the *shape* over time (paths, timing, errors, bursts,
   navigation style).
2. **Replay behaviour**: drive a load generator/test harness against Stylobot/YARP/ASP.NET.
3. **Compare & assert**: tell the test what you *expect* Stylobot to classify (bot/human, risk band).
4. **Reverse-map from signatures**: given a `SignatureBehaviorState`, produce an approximate BDF that *would* have
   generated similar metrics.

---

## 2. Top-level structure

Let’s define BDF as JSON (YAML is trivial from that).

```jsonc
{
  "version": "1.0",
  "id": "scenario-human-browse-001",
  "description": "Human-like browsing around product catalogue",
  "metadata": {
    "author": "scott",
    "createdUtc": "2025-12-10T02:30:00Z",
    "tags": ["human-like", "catalogue", "baseline"]
  },

  "client": {
    "signatureId": "test-user-1",
    "ip": "203.0.113.10",
    "userAgent": "Mozilla/5.0 ...",
    "headers": {
      "Accept-Language": "en-GB,en;q=0.9"
    }
  },

  "expectation": {
    "expectedClassification": "Human",
    "maxBotProbability": 0.2,
    "maxRiskBand": "Low"
  },

  "phases": [
    /* array of Phase objects (see below) */
  ]
}
```

### Key concepts

* `client` describes the identity/fingerprint under test.
* `phases` describe behaviour in **chunks** (different modes: browsing, scraping, polling, bursts).
* `expectation` says what Stylobot *should* conclude.

---

## 3. Phase structure

A **Phase** is a time-bounded (or request-bounded) behaviour mode:

```jsonc
{
  "name": "browse-products",
  "duration": "60s",        // OR "requestCount": 30 (mutually exclusive)
  "requestCount": null,
  "concurrency": 1,         // how many parallel request streams
  "baseRateRps": 0.5,       // base requests/sec per stream

  "timing": {
    "mode": "jittered",     // "fixed", "jittered", "burst"
    "jitterStdDev": 0.4,    // seconds, only for jittered mode
    "burst": {
      "burstSize": 10,      // only for "burst" mode
      "burstInterval": "10s"
    }
  },

  "navigation": {
    "mode": "ui_graph",     // "ui_graph", "sequential", "random", "scanner"
    "startPath": "/",
    "uiGraphProfile": "default",   // optional reference into a test-side graph
    "paths": [
      {
        "template": "/products",
        "weight": 2.0
      },
      {
        "template": "/products/{id}",
        "weight": 5.0,
        "idRange": { "min": 1, "max": 100 }
      },
      {
        "template": "/cart",
        "weight": 1.0
      }
    ],
    "offGraphProbability": 0.05   // chance to jump to a non-afforded url
  },

  "errorInteraction": {
    "retryOn4xx": false,
    "retryOn5xx": true,
    "retryDelay": "1s",
    "maxRetries": 3
  },

  "content": {
    "bodyMode": "none",    // "none", "template", "random"
    "templates": []        // for POST/PUT payloads, optional
  }
}
```

### Behaviour bits per phase

* **Timing**: the temporal waveform.
* **Navigation**: how URLs are chosen.
* **ErrorInteraction**: how the client reacts to status codes.
* **Content**: payload behaviour (optional for now).

---

## 4. Navigation modes (behavioural flavour)

To embed your waveform ideas, we define navigation modes:

### `ui_graph`

* Player uses a pre-defined UI graph (from your test harness / captured sitemap).
* Next path is chosen according to graph edges + weights.
* High **affordance-follow-through** → human-ish.

### `sequential`

* For paths with `{id}` ranges:

    * /item/1 → /item/2 → /item/3…
* High **path entropy**, near-zero randomness → scraper-ish.

### `random`

* Uniform random choice among `paths` list.
* High path entropy, but no structure → weird/fuzzing-like.

### `scanner`

* Includes paths that don’t exist in the UI graph (e.g. `/.git`, `/wp-admin`, `/phpmyadmin`).
* Very scanner-like behaviour.

You can mix these across phases:

* Phase 1: `ui_graph` (normal browsing)
* Phase 2: `scanner` (attacker/scraper)

---

## 5. Timing modes (to feed spectral/behavioural analysis)

### `fixed`

* `baseRateRps` strictly followed (ideal timer).
* Perfect candidate for FFT → strong spectral peak.

### `jittered`

* Requests scheduled at `1/baseRateRps ± N(0, jitterStdDev)`.
* Good for “bot faking jitter”.

### `burst`

* `burstSize` requests back-to-back every `burstInterval`.
* Very high-frequency spikes.

These map directly to your:

* timing entropy,
* CV,
* burst detection,
* spectral detector.

---

## 6. Error interaction model

This lets you model:

* bots that retry aggressively on 403/429,
* humans who don’t.

```jsonc
"errorInteraction": {
  "retryOn4xx": true,
  "retryOn5xx": false,
  "retryDelay": "500ms",
  "maxRetries": 5,
  "respectRetryAfter": true
}
```

Your behavioural detectors will see:

* loops,
* immediate retries,
* or lack thereof.

---

## 7. How this ties into your *signatures*

You asked:

> “I guess it can be reversed from a full signature?”

Not perfectly 1:1 (many behaviours map to similar signatures), but we can **project**:

### Define a compact `SignatureBehaviorState` (you basically have this already):

```csharp
public sealed record SignatureBehaviorState(
    double PathEntropy,
    double TimingEntropy,
    double CoefficientOfVariation,
    double BurstScore,
    double NavAnomalyScore,
    double SpectralPeakToNoise,
    double SpectralEntropy,
    double AffordanceFollowThroughRatio,
    double FourOhFourRatio,
    double FiveOhOhRatio
);
```

Then define a **mapping to BDF defaults**:

* `SpectralPeakToNoise ↑` and `SpectralEntropy ↓` → `timing.mode = "fixed"` or `"jittered"` with low jitter.
* `BurstScore ↑` → add a `burst` phase.
* `PathEntropy ↓` & low nav anomaly → `navigation.mode = "ui_graph"`.
* `PathEntropy ↑` & nav anomaly ↑ → `navigation.mode = "scanner"` or `"sequential"`.
* `AffordanceFollowThroughRatio ↓` → raise `offGraphProbability`.
* `FourOhFourRatio ↑` → include scanner paths.

So a **reverse generator** roughly does:

```text
SignatureBehaviorState → set of Phase templates + parameter ranges
                        → BDF scenario describing a client that *resembles* this signature
```

You’ll never reconstruct the exact request sequence, but you’ll generate a new “bot that looks like this kind of bot”
for replay/regression.

---

## 8. Example: Bot-ish scenario

```jsonc
{
  "version": "1.0",
  "id": "scenario-bot-scanner-001",
  "description": "Simple timed scanner hitting known CMS paths",
  "client": {
    "signatureId": "scanner-bot-1",
    "ip": "198.51.100.23",
    "userAgent": "curl/8.0"
  },
  "expectation": {
    "expectedClassification": "Bot",
    "minBotProbability": 0.9,
    "minRiskBand": "High"
  },
  "phases": [
    {
      "name": "cms-scanner",
      "requestCount": 200,
      "duration": null,
      "concurrency": 1,
      "baseRateRps": 2.0,
      "timing": {
        "mode": "fixed",
        "jitterStdDev": 0.0,
        "burst": null
      },
      "navigation": {
        "mode": "scanner",
        "startPath": "/",
        "paths": [
          { "template": "/wp-login.php" },
          { "template": "/wp-admin/" },
          { "template": "/.git/HEAD" },
          { "template": "/phpmyadmin/" },
          { "template": "/admin" }
        ],
        "offGraphProbability": 0.8
      },
      "errorInteraction": {
        "retryOn4xx": false,
        "retryOn5xx": false,
        "retryDelay": "0s",
        "maxRetries": 0
      },
      "content": {
        "bodyMode": "none",
        "templates": []
      }
    }
  ]
}
```

Your detectors will see:

* fixed timing → strong spectral peak,
* scanner paths → nav anomaly, high 404,
* low affordance follow-through → bot-ish,
* no CSS/js loads → HTTP behaviour odd.

Exactly the kind of thing the system should classify as bot.

---

## 9. How to “run it against the system”

You’d have a **BDF runner** that:

1. Loads BDF JSON.
2. For each `phase`:

    * spawns `concurrency` tasks,
    * respects `timing.mode` and `baseRateRps`,
    * builds URLs from `navigation` rules,
    * optionally builds bodies from `content`.
3. Sends real HTTP requests to the system under test.
4. Optionally:

    * polls your detection API / logs / echo endpoint,
    * asserts that classification matches `expectation`.

This gives you:

* behavioural regression tests,
* synthetic bots and humans,
* replay of “typical waveforms”.

---

If you like, I can next:

* define a *tiny* C# DTO model for BDF, or
* sketch how a `SignatureBehaviorState → BDF` reverse mapper would look (mapping thresholds to phase choices).

Yes, this is the fun bit: the mapper is what closes the loop and lets you *stress-test* your own signatures.

Here’s a clean spec for the **Signature → BDF Mapper**, so you can:

* take a `SignatureBehaviorState`,
* generate a BDF scenario that *looks like* that behaviour,
* replay it,
* and see if your system classifies it the same way.

---

## 1. Data model: SignatureBehaviorState

First, formalise the “waveform snapshot” you’re mapping from:

```csharp
public sealed record SignatureBehaviorState(
    double PathEntropy,
    double TimingEntropy,
    double CoefficientOfVariation,
    double BurstScore,
    double NavAnomalyScore,
    double SpectralPeakToNoise,
    double SpectralEntropy,
    double AffordanceFollowThroughRatio,
    double FourOhFourRatio,
    double FiveOhOhRatio,
    double AverageRps,
    double AverageSessionDurationSeconds,
    int AverageRequestsPerSession
);
```

You basically already compute most of these; a few (like `AverageRps`) you can derive from your existing behavioural
analyzer.

---

## 2. Mapper overview

**Type:** `SignatureToBdfMapper`
**Input:** `SignatureBehaviorState state`, `SignatureBehaviorProfile profile`
**Output:** `BdfScenario scenario`

Where `SignatureBehaviorProfile` is just a tag for how you *expect* this signature to behave:

```csharp
public enum SignatureBehaviorProfile
{
    Unknown,
    ExpectedHuman,
    ExpectedBot,
    ExpectedMixed
}
```

This lets you bake expectations into the scenario.

---

## 3. High-level algorithm

Given `SignatureBehaviorState`:

1. Decide **timing mode** (fixed / jittered / burst)
2. Decide **navigation mode** (ui_graph / sequential / random / scanner)
3. Decide **off-graph probability**
4. Decide **error interaction flavour** (reties or not, 4xx vs 5xx)
5. Derive **baseRateRps**, **concurrency**, **phase count/durations**
6. Assemble one or more BDF `Phase` objects.
7. Wrap into a `BdfScenario` with expectations derived from `SignatureBehaviorProfile`.

---

## 4. Threshold config (so you can tune this)

Put thresholds into something like:

```csharp
public sealed class SignatureToBdfMapperOptions
{
    // Path entropy thresholds
    public double PathEntropyLow  { get; init; } = 0.5;
    public double PathEntropyHigh { get; init; } = 3.0;

    // Navigation anomaly thresholds
    public double NavAnomalyHigh  { get; init; } = 0.6;

    // Timing / spectral thresholds
    public double SpectralPnBot   { get; init; } = 4.0;
    public double SpectralEntropyBot { get; init; } = 0.4;

    public double BurstScoreHigh  { get; init; } = 0.7;

    // Affordance thresholds
    public double AffordanceLow   { get; init; } = 0.4;
    public double AffordanceHigh  { get; init; } = 0.8;

    // Error thresholds
    public double FourOhFourRatioHigh { get; init; } = 0.3;
    public double FiveOhOhRatioHigh   { get; init; } = 0.2;
}
```

These are *not* detection thresholds, just mapping heuristics.

---

## 5. Mapping rules (core logic)

### 5.1 Timing → timing.mode / jitter / bursts

```csharp
TimingConfig MapTiming(SignatureBehaviorState s, SignatureToBdfMapperOptions o)
{
    // Bots with timer loops: strong spectral peak, low spectral entropy
    if (s.SpectralPeakToNoise >= o.SpectralPnBot &&
        s.SpectralEntropy <= o.SpectralEntropyBot)
    {
        return new TimingConfig
        {
            Mode = "fixed",
            BaseRateRps = Clamp(s.AverageRps, 0.1, 10.0),
            JitterStdDevSeconds = 0.0,
            Burst = null
        };
    }

    // Bursty signatures
    if (s.BurstScore >= o.BurstScoreHigh)
    {
        return new TimingConfig
        {
            Mode = "burst",
            BaseRateRps = Clamp(s.AverageRps, 1.0, 20.0),
            Burst = new BurstConfig
            {
                BurstSize = 10,             // heuristic
                BurstIntervalSeconds = 10.0 // heuristic
            },
            JitterStdDevSeconds = 0.1
        };
    }

    // Human-ish or jittered bots: jittered
    return new TimingConfig
    {
        Mode = "jittered",
        BaseRateRps = Clamp(s.AverageRps, 0.1, 5.0),
        JitterStdDevSeconds = MapJitterFromEntropy(s.TimingEntropy, s.CoefficientOfVariation)
    };
}
```

### 5.2 Navigation → navigation.mode / offGraphProbability

```csharp
NavigationConfig MapNavigation(SignatureBehaviorState s, SignatureToBdfMapperOptions o)
{
    // Scanner / off-graph attacker
    if (s.PathEntropy >= o.PathEntropyHigh && s.NavAnomalyScore >= o.NavAnomalyHigh)
    {
        return new NavigationConfig
        {
            Mode = "scanner",
            OffGraphProbability = 0.8
        };
    }

    // Sequential scraper (low path entropy, high nav anomaly)
    if (s.PathEntropy <= o.PathEntropyLow && s.NavAnomalyScore >= o.NavAnomalyHigh)
    {
        return new NavigationConfig
        {
            Mode = "sequential",
            OffGraphProbability = 0.6
        };
    }

    // Normal-ish UI navigation
    if (s.AffordanceFollowThroughRatio >= o.AffordanceHigh &&
        s.NavAnomalyScore < o.NavAnomalyHigh)
    {
        return new NavigationConfig
        {
            Mode = "ui_graph",
            OffGraphProbability = 0.05
        };
    }

    // Weird but not full-on scanner
    return new NavigationConfig
    {
        Mode = "random",
        OffGraphProbability = s.AffordanceFollowThroughRatio <= o.AffordanceLow ? 0.5 : 0.2
    };
}
```

### 5.3 Error behaviour → errorInteraction

```csharp
ErrorInteractionConfig MapError(SignatureBehaviorState s, SignatureToBdfMapperOptions o)
{
    return new ErrorInteractionConfig
    {
        RetryOn4xx = s.FourOhFourRatio >= o.FourOhFourRatioHigh,
        RetryOn5xx = s.FiveOhOhRatio >= o.FiveOhOhRatioHigh,
        RespectRetryAfter = true,
        MaxRetries = s.FourOhFourRatio >= o.FourOhFourRatioHigh ? 5 : 2,
        RetryDelay = TimeSpan.FromSeconds(1)
    };
}
```

### 5.4 Phase count

Start simple: **one main phase** that reflects the dominant behaviour. Later you can split signatures with:

* early “human-like” then “scanner-like”,
* into multiple phases if you track time-varying behaviour in the signature.

For now:

```csharp
int MapPhaseRequestCount(SignatureBehaviorState s)
{
    // Use average session size as a rough request count
    return Math.Max(10, Math.Min(500, s.AverageRequestsPerSession));
}
```

---

## 6. Scenario expectations

Map `SignatureBehaviorProfile` → BDF expectation:

```csharp
ExpectationConfig MapExpectation(SignatureBehaviorProfile profile)
{
    return profile switch
    {
        SignatureBehaviorProfile.ExpectedHuman => new ExpectationConfig
        {
            ExpectedClassification = "Human",
            MaxBotProbability = 0.3,
            MaxRiskBand = "Low"
        },
        SignatureBehaviorProfile.ExpectedBot => new ExpectationConfig
        {
            ExpectedClassification = "Bot",
            MinBotProbability = 0.8,
            MinRiskBand = "High"
        },
        SignatureBehaviorProfile.ExpectedMixed => new ExpectationConfig
        {
            ExpectedClassification = "Mixed",
            MaxBotProbability = 0.8,
            MinBotProbability = 0.2
        },
        _ => new ExpectationConfig
        {
            ExpectedClassification = "Unknown"
        }
    };
}
```

This is what lets you **evaluate signatures against expected behaviour**: you generate a scenario that matches the
measured waveform and assert your detector still arrives at the same high-level judgement.

---

## 7. Putting it all together (pseudo-code)

```csharp
public sealed class SignatureToBdfMapper
{
    private readonly SignatureToBdfMapperOptions _options;

    public SignatureToBdfMapper(IOptions<SignatureToBdfMapperOptions> options)
        => _options = options.Value;

    public BdfScenario Map(SignatureBehaviorState state, SignatureBehaviorProfile profile)
    {
        var timing = MapTiming(state, _options);
        var nav    = MapNavigation(state, _options);
        var error  = MapError(state, _options);
        var expect = MapExpectation(profile);

        var phase = new BdfPhase
        {
            Name = "auto-derived-main",
            Duration = null,
            RequestCount = MapPhaseRequestCount(state),
            Concurrency = 1, // start simple
            BaseRateRps = timing.BaseRateRps,
            Timing = timing,
            Navigation = nav,
            ErrorInteraction = error,
            Content = new ContentConfig
            {
                BodyMode = "none"
            }
        };

        return new BdfScenario
        {
            Version = "1.0",
            Id = $"scenario-{profile.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
            Description = "Auto-derived scenario from SignatureBehaviorState",
            Client = new ClientConfig
            {
                SignatureId = "derived-from-signature",
                UserAgent = "BDF-Replay/1.0"
            },
            Expectation = expect,
            Phases = new[] { phase }
        };
    }

    // mapping helpers from sections above...
}
```

---

## 8. How you use it to “evaluate signatures against expected behaviour”

1. **Collect real behaviour**

    * Run Stylobot in front of something.
    * Let signatures accumulate normal waveform state.

2. **Snapshot a signature**

    * Grab `SignatureBehaviorState` for `SignatureId = X`.
    * Decide `SignatureBehaviorProfile` (e.g. we *expect* this to be Human or Bot).

3. **Generate BDF scenario**

   ```csharp
   var scenario = mapper.Map(signatureState, SignatureBehaviorProfile.ExpectedHuman);
   ```

4. **Replay scenario** through your BDF runner against Stylobot.

5. **Assert** that:

    * resulting detection JSON matches `scenario.Expectation`,
    * spectral / behavioural scores are in the same ballpark as the original state,
    * changes to your detectors (thresholds, new contributors) don’t break this invariant.

That gives you:

* A **closed testing loop** between:

    * measured real behaviour (signatures),
    * synthetic replay (BDF),
    * and your classifier’s expectations.

---

If you want, I can next:

* tighten `SignatureBehaviorState` to exactly match the fields you already emit in your debug JSON, or
* sketch a tiny test showing “take existing signature JSON → map → BDF → run → compare risk bands”.

Yep, you’ve basically got a **two-for-one** here:

* Same mapper that generates BDF
* Can also generate **“English” explanations** for the dashboard from the same `SignatureBehaviorState`.

Let’s spec that bit so you can wire it in nicely.

---

## 1. Data model: what the dashboard wants

Add an “explanation” object you can stick straight into your API response:

```csharp
public sealed record SignatureExplanation(
    string Summary,                      // one-liner
    IReadOnlyList<string> Highlights,    // bullet points
    IReadOnlyDictionary<string, object> RawMetrics // optional, for UI
);
```

You’ll generate this from:

```csharp
public sealed record SignatureBehaviorState { /* as before */ }

public sealed record DetectionOutcome(
    bool IsBot,
    double BotProbability,
    string RiskBand
);
```

---

## 2. Mapper: Behaviour → English

Create a small service:

```csharp
public interface ISignatureExplanationFormatter
{
    SignatureExplanation Explain(
        SignatureBehaviorState state,
        DetectionOutcome outcome);
}
```

Internally you’ll do thresholded rules, same style as the BDF mapper.

### 2.1 Summary line

Examples:

* For obvious bot:

> “This signature behaves like a **high-confidence automated scanner**.”

* For normal human:

> “This signature shows **natural, human-like browsing behaviour**.”

* For ambiguous/mixed:

> “This signature mixes **human-like navigation with periodic, scripted activity**.”

You pick based on:

* `BotProbability`
* `RiskBand`
* key features (spectral peak, path entropy, nav anomaly, etc.).

### 2.2 Highlight bullets

Take the same thresholds from your behaviour state and turn them into short bullets:

```csharp
var highlights = new List<string>();

if (state.SpectralPeakToNoise >= options.SpectralPnBot &&
    state.SpectralEntropy <= options.SpectralEntropyBot)
{
    highlights.Add(
        "Requests are sent at highly regular intervals, typical of timer-driven scripts.");
}

if (state.BurstScore >= options.BurstScoreHigh)
{
    highlights.Add(
        "Traffic arrives in short, intense bursts rather than steady browsing sessions.");
}

if (state.PathEntropy >= options.PathEntropyHigh &&
    state.NavAnomalyScore >= options.NavAnomalyHigh)
{
    highlights.Add(
        "Navigation frequently jumps to unusual or non-UI paths, consistent with scanners or crawlers.");
}

if (state.AffordanceFollowThroughRatio >= options.AffordanceHigh &&
    state.NavAnomalyScore < options.NavAnomalyHigh)
{
    highlights.Add(
        "Navigation mostly follows links exposed in the UI, consistent with real users clicking around.");
}

if (state.FourOhFourRatio >= options.FourOhFourRatioHigh)
{
    highlights.Add(
        "A large fraction of requests result in 404 errors, suggesting path probing or discovery.");
}
```

If you want a “this looks good” human card:

```csharp
if (!highlights.Any() && outcome.BotProbability < 0.3)
{
    highlights.Add(
        "Request timing, navigation and error patterns all fall within normal human ranges.");
}
```

---

## 3. Re-use thresholds from the BDF mapper

You can literally share the same `SignatureToBdfMapperOptions` thresholds so:

* the **behaviour → BDF** and
* the **behaviour → explanation**

speak the same language.

Example:

```csharp
public sealed class SignatureExplanationFormatter : ISignatureExplanationFormatter
{
    private readonly SignatureToBdfMapperOptions _o;

    public SignatureExplanationFormatter(IOptions<SignatureToBdfMapperOptions> options)
        => _o = options.Value;

    public SignatureExplanation Explain(SignatureBehaviorState s, DetectionOutcome outcome)
    {
        var highlights = new List<string>();

        // ... rules from above ...

        var summary = BuildSummary(s, outcome, highlights);

        var metrics = new Dictionary<string, object>
        {
            ["BotProbability"] = outcome.BotProbability,
            ["RiskBand"] = outcome.RiskBand,
            ["PathEntropy"] = s.PathEntropy,
            ["TimingEntropy"] = s.TimingEntropy,
            ["SpectralPeakToNoise"] = s.SpectralPeakToNoise,
            ["SpectralEntropy"] = s.SpectralEntropy,
            ["NavAnomalyScore"] = s.NavAnomalyScore,
            ["AffordanceFollowThrough"] = s.AffordanceFollowThroughRatio
        };

        return new SignatureExplanation(summary, highlights, metrics);
    }
}
```

Where `BuildSummary` is something like:

```csharp
private static string BuildSummary(
    SignatureBehaviorState s,
    DetectionOutcome outcome,
    IReadOnlyList<string> highlights)
{
    if (outcome.BotProbability >= 0.9)
        return "This signature behaves like a high-confidence automated client.";

    if (outcome.BotProbability <= 0.2 && s.AffordanceFollowThroughRatio >= 0.7)
        return "This signature behaves like a normal human user.";

    if (s.BurstScore >= 0.7 && s.PathEntropy >= 3.0)
        return "This signature shows bursty, exploratory behaviour typical of scrapers.";

    return "This signature shows mixed behavioural characteristics; further review may be useful.";
}
```

---

## 4. How it looks on the dashboard

For a given `SignatureId`:

* **Title:** “Signature: scanner-bot-123 (High Risk)”
* **Summary:** `SignatureExplanation.Summary`
* **Highlights:**

    * bullet list from `SignatureExplanation.Highlights`
* **Raw metrics panel:** values from `RawMetrics` (tiny table / graph).

And if you want a “Replay this behaviour” button:

* Same click can call the **BDF mapper** on the same state and store the scenario ID:

    * “Re-run as synthetic test” in one click.

So yeah: the explanation formatter is just a **human-readable front-end** to the same mapping logic you’re already
designing for BDF — which is exactly what you noticed.
