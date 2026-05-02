# StyloBot UI: Behaviour-Aware ASP.NET Applications

Most bot protection advice boils down to: detect the bot, block the bot, move on. That works for a firewall. It is a poor strategy for a storefront.

Real bots are a spectrum. At one end: credential-stuffing scripts that hammer your login endpoint. At the other: Googlebot, which you actively want to crawl your catalogue. Between them: price scrapers, AI training crawlers, headless browsers running competitor analytics, automated voucher-code testers, and a hundred other automated clients - each with a different intent, each deserving a different response.

Blocking everything that isn't a verified browser fingerprint loses Googlebot, penalises low-data mobile customers, and flags legitimate API integrations. Letting everything through hands scrapers your product data, lets price bots undercut you in real time, and silently fills your mailing list with honeypot addresses.

> StyloBot takes a different approach. Instead of a binary gate, it gives each request a continuous risk score, classifies the bot type if it is one, and exposes that classification to your application layer. What you do with it is up to you.

Consider the login page. A credential-stuffing script hits it and encounters three independent layers with no CAPTCHA, no rate-limit config, no custom middleware:

```html
<!-- Layer 1: bots see a deterrent message before the form -->
<sb-bot>
    <div class="alert alert-warning">Automated login attempts are detected and blocked.</div>
</sb-bot>

<form method="post" action="/Account/Login">
    <!-- Layer 2: hidden trap fields - humans leave them blank; bots fill everything -->
    <sb-honeypot prefix="hp" fields="2"></sb-honeypot>

    <input type="email" name="email" />
    <input type="password" name="password" />
    <button type="submit">Sign In</button>
</form>
```

```csharp
// Layer 3: server-side final check before any processing
if (HoneypotValidator.IsTriggered(HttpContext) || HttpContext.IsBot())
    return RedirectToAction("LoginDenied");
```

The bot never sees the form. If it submits anyway, the honeypot catches it. If it somehow bypasses both, the controller rejects it. Each layer is one line. That is what protection in depth looks like when the detection result is available at render time. [See the full walkthrough below.](#page-4-login)

This article walks through a sample storefront that puts every part of that API to work. The full source is in `Mostlylucid.BotDetection.Sample` in the repository. A follow-up article will cover StyloBot's SSR, client-side, and real-time UI layer.

---

## The cost of not knowing who is in your shop

**Price scrapers.** Retail bots harvest product prices across hundreds of stores every few hours. They are well-written, polite (they respect `robots.txt`), and use real browser fingerprints. They cost you nothing directly - until a competitor uses that data to undercut you by 2% on every high-margin product, in real time.

**Voucher testers.** Scripts that iterate through discount-code spaces hit your checkout endpoint thousands of times a minute. They find valid codes. Those codes get posted to coupon forums. Your margin evaporates.

**Credential stuffers.** Purchased credential lists get replayed against your login form. Even a 0.5% hit rate on a million-entry list means 5,000 compromised accounts. Customer trust is expensive to rebuild.

**AI training crawlers.** GPTBot, ClaudeBot, and similar crawlers harvest your product descriptions, reviews, and editorial content for training datasets. Your copywriting team's work feeds a model that will compete with your ads.

**Newsletter harvesters.** Bots submit to email signup forms to validate addresses - a confirmed bounce-free address is worth money on spam lists - or to probe your signup flow for injection vulnerabilities.

None of these are the same problem. A single block-everything strategy solves the easy ones (curl scripts, known bad IPs) and makes the hard ones - the ones that cost real money - slightly harder to write while still succeeding. A behaviour-aware strategy handles all of them differently, and more accurately.

---

## The sample store

The sample app is a minimal ASP.NET Core MVC store: forty generated products, five categories, a checkout flow, a newsletter signup, and a login page. It demonstrates StyloBot's tag helper API across six pages, each built around a realistic commercial scenario.

Install the NuGet packages:

```bash
dotnet add package Mostlylucid.BotDetection
dotnet add package Mostlylucid.BotDetection.UI
```

Then wire it up in `Program.cs`:

```csharp
builder.Services.AddStyloBot(
    configureDashboard: dashboard =>
    {
        dashboard.BasePath = "/_stylobot";
        dashboard.AllowUnauthenticatedAccess = true; // dev only
    },
    configureDetection: detection =>
    {
        detection.ExcludeLocalIpFromBroadcast = false;
    });

// ...

app.UseStyloBot();
app.MapHub<StyloBotDashboardHub>("/_stylobot/hub");
```

That is everything. Detection is running, the dashboard is live at `/_stylobot`, and every request now carries a risk score.

---

## Page 1: The shop floor

The home page (`/`) is where most scrapers start. Their goal is the product catalogue: names, prices, descriptions, availability. Your goal is to make that data available to customers and expensive to harvest systematically.

*Scenario: a price scraper hits `/`; it sees a neutral catalogue message, no welcome copy, and no category count.*

```html
<!-- Human visitors see the welcome message and category count -->
<sb-human>
    <p class="muted">Welcome! Browse @Model.Count products.</p>
</sb-human>

<!-- Bots see a neutral, non-committal message -->
<sb-bot fallback="hide">
    <p class="muted">Product catalogue.</p>
</sb-bot>

<!-- Search engine crawlers get structured metadata, not price data -->
<sb-gate bot-type="SearchEngine">
    <meta name="description" content="@Model.Count products across @categories categories." />
</sb-gate>

<!-- Verified bots (Googlebot etc.) see a specific indicator -->
<sb-gate bot-type="VerifiedBot">
    <div class="alert alert-info">Verified crawler detected. Serving crawl-optimised view.</div>
</sb-gate>

<!-- High-risk sessions see friction before the buy buttons -->
<sb-gate min-risk="High">
    <div class="alert alert-warning">Additional verification may be required at checkout.</div>
</sb-gate>
```

**These are not access controls. They are rendering hints.** The page is still accessible. The product list still renders. What changes is the surrounding context - making the page less useful for automated harvesting while keeping it fully functional for customers.

The `<sb-gate bot-type="SearchEngine">` block deserves attention. Googlebot visits matter; you want it indexing your products. But rendering full pricing data in the search-engine version of your page means that data ends up in AI-generated snippets, bypassing your storefront entirely. This lets you serve structured, indexable metadata to verified crawlers without exposing your full commercial data layer.

---

## Page 2: The product page

The product detail page is where revenue protection becomes concrete. Price scrapers spend the most time here. So do customers deciding whether to buy.

*Scenario: a price scraper lands on `/Product/Detail/12`; it sees no discount code, no add-to-cart button, and no purchase signal to act on.*

```html
<!-- Exclusive discount - only shown to low-risk, verified human visitors -->
<sb-gate max-risk="Low">
    <div class="alert alert-success">
        Member discount: use code LOYAL10 for 10% off today.
    </div>
</sb-gate>

<!-- Medium-risk visitors get a friction signal before the cart button -->
<sb-gate min-risk="Medium">
    <div class="alert alert-warning">
        We noticed some unusual activity from your network.
        You can still purchase - you may be asked to verify at checkout.
    </div>
</sb-gate>

<!-- Datacenter/VPN visitors lose the buy button -->
<sb-signal signal="ip.is_datacenter" condition="true">
    <p class="muted">Purchase unavailable from datacenter or VPN networks.</p>
</sb-signal>

<!-- Human-only: the add-to-cart button -->
<sb-gate human-only>
    <form method="post" action="/Cart/Add">
        <button type="submit" class="btn btn-success">Add to Cart</button>
    </form>
</sb-gate>

<!-- Detection mini-card for transparency -->
<sb-summary variant="card"></sb-summary>
<sb-confidence display="bar" width="180px"></sb-confidence>
```

Three distinct commercial patterns:

**Loyalty targeting.** Discount codes shown only to low-risk visitors are not wasted on price scrapers or voucher testers. `max-risk="Low"` means only visitors with a clean session history, normal browser fingerprint, and no datacenter IP see the offer.

**Progressive friction.** Medium-risk visitors are not blocked. They are warned. Many medium-risk signals are false positives - shared corporate NAT, privacy-focused browsers, VPNs used for legitimate reasons. Blocking loses real customers. Warning surfaces the issue without costing the sale.

**Datacenter gating.** `ip.is_datacenter` is a raw signal, not a risk score. Most production scraping infrastructure runs in datacenters. This gate is about access policy, not risk level. You can gate on individual signals without needing them to contribute to the overall risk score.

---

## Page 3: Checkout

The checkout is the highest-value target on the site. Voucher testers, account takeover bots, and automated card-testing scripts all want this endpoint.

*Scenario: a voucher-testing bot hits `/Cart/Checkout`; it sees a dead-end message, submits a honeypot-filled form, and gets a silent accept with no retry incentive.*

```html
<!-- Gate the entire checkout form on human-only detection -->
<sb-gate human-only>
    <form method="post" action="/Cart/Order">
        @Html.AntiForgeryToken()

        <!-- Honeypot trap fields - invisible to humans, irresistible to bots -->
        <sb-honeypot prefix="co" fields="2"></sb-honeypot>

        <!-- Express checkout only for trusted visitors -->
        <sb-gate max-risk="Low">
            <button type="submit" name="express" value="true" class="btn btn-success">
                Express Checkout
            </button>
        </sb-gate>

        <!-- Standard checkout available up to elevated risk -->
        <sb-gate max-risk="Elevated">
            <button type="submit" class="btn btn-primary">Proceed to Payment</button>
        </sb-gate>

        <!-- High-risk visitors get an alternative path -->
        <sb-gate min-risk="High">
            <p>Please call us to complete your order: 0800 123 456</p>
        </sb-gate>
    </form>
</sb-gate>

<!-- Bots see a dead end, not an error -->
<sb-bot>
    <p class="muted">Checkout is only available to human visitors.</p>
</sb-bot>
```

And in the controller:

```csharp
[HttpPost]
public IActionResult Order(OrderModel model)
{
    if (HoneypotValidator.IsTriggered(HttpContext))
    {
        // Silent accept - bot thinks the order succeeded
        return RedirectToAction("Confirmed");
    }

    return ProcessOrder(model);
}
```

Three patterns:

**Honeypot fields.** `<sb-honeypot>` renders hidden form fields invisible to human users (positioned off-screen, zero opacity). Automated form-fillers fill everything. When `HoneypotValidator.IsTriggered()` returns true, you know the submission came from a bot. The response is a silent accept - the bot thinks it succeeded, gets no error to work around, and does not retry with a different approach.

**Risk-tiered CTAs.** Express checkout requires `max-risk="Low"`. This is not security theatre - it is a business decision. High-velocity checkout from an unusual session is a fraud signal regardless of whether a human or a script initiated it. Routing those sessions to the standard flow adds friction without blocking the sale.

**Graceful bot degradation.** The `<sb-bot>` fallback is not a 403. It is a human-readable message. Bots ignore messages. Humans operating them do not. A 403 triggers an alert in a monitoring dashboard; a polite message does not. You want to be visible to security engineers while staying quiet to scraping infrastructure.

---

## Page 4: Login

Credential stuffing targets the login form. Buy a leaked credential list, replay it against every login form on the internet, see what sticks. Defence needs to operate at both the rendering and controller layers.

*Scenario: a credential-stuffing script hits `/Account/Login`; it never sees the form, fills the honeypot, and gets bounced to `LoginDenied`.*

```html
<!-- High-risk sessions see friction before the form -->
<sb-gate min-risk="High">
    <div class="alert alert-danger">
        High-risk signals detected. Login attempts are logged and may be blocked.
    </div>
</sb-gate>

<!-- Bots see a deterrent message - but the form is still rendered below -->
<sb-bot>
    <div class="alert alert-warning">
        Automated login attempts are detected and blocked.
    </div>
</sb-bot>

<!-- Form is visible to everyone; the honeypot is the second layer -->
<form method="post" action="/Account/Login">
    @Html.AntiForgeryToken()
    <sb-honeypot prefix="hp" fields="2"></sb-honeypot>
    <div class="form-group">
        <label for="email">Email address</label>
        <input type="email" id="email" name="email" autocomplete="email" />
    </div>
    <div class="form-group">
        <label for="password">Password</label>
        <input type="password" id="password" name="password" autocomplete="current-password" />
    </div>
    <button type="submit" class="btn btn-primary">Sign In</button>
</form>
```

The controller applies a second layer:

```csharp
[HttpPost]
public IActionResult Login(LoginModel model)
{
    if (HoneypotValidator.IsTriggered(HttpContext))
        return RedirectToAction("LoginDenied");

    if (HttpContext.IsBot())
        return RedirectToAction("LoginDenied");

    return Authenticate(model);
}
```

The form is rendered for everyone - the three layers are deterrence (`<sb-bot>` warning), detection (honeypot catches bots that submit), and enforcement (server-side check). Unlike checkout, where a false positive costs a sale, a false positive here just means a retry. The cost is low; the cost of a missed credential-stuffing hit is an account takeover.

---

## Page 5: Newsletter

This page shows one of StyloBot's most commercially interesting patterns: structured AI bot handling.

*Scenario: GPTBot hits `/Newsletter/Subscribe`; it sees a data-licensing message instead of a subscription pitch, and its form submission is silently discarded.*

AI training crawlers are different from traditional scrapers. They are not trying to steal your prices or break your login. They are harvesting text for training datasets. They are usually polite, they identify themselves honestly, and they are operated by companies with legal and compliance teams. Responding with a 403 is adversarial. Responding with a structured message is a business conversation.

```html
<!-- Human pitch - only visible to real visitors -->
<sb-human>
    <p class="muted">
        Get exclusive deals and discount codes delivered to your inbox.
        Subscribe below - unsubscribe any time.
    </p>
</sb-human>

<!-- AI crawlers get a licensing message, not a block -->
<sb-gate bot-type="AiBot">
    <div class="alert alert-info">
        This email subscription endpoint is for human readers.
        For data licensing enquiries please contact us directly.
    </div>
</sb-gate>

<!-- Other automated clients get a simpler message -->
<sb-bot>
    <sb-gate bot-type="AiBot" negate="true">
        <div class="alert alert-warning">
            Automated subscription attempts are discarded.
        </div>
    </sb-gate>
</sb-bot>

<!-- The form - visible to everyone, processed differently per visitor type -->
<div class="card">
    <form method="post" action="/Newsletter/Subscribe">
        @Html.AntiForgeryToken()
        <sb-honeypot prefix="nl" fields="3"></sb-honeypot>
        <div class="form-group">
            <label for="email">Your email address</label>
            <input type="email" id="email" name="email" autocomplete="email" />
        </div>
        <button type="submit" class="btn btn-success">Subscribe</button>
    </form>
</div>
```

And in the controller:

```csharp
[HttpPost]
public IActionResult Subscribe(string email)
{
    if (HoneypotValidator.IsTriggered(HttpContext) || HttpContext.IsBot())
    {
        // Silent accept: bot thinks it succeeded, no retry incentive
        return RedirectToAction("Thanks", new { real = false });
    }

    _mailingList.Subscribe(email);
    return RedirectToAction("Thanks", new { real = true });
}
```

The `Thanks` view shows the same confirmation page regardless of whether the subscription was real. Bots cannot distinguish a successful submission from a silently-discarded one. Any visible error creates a retry incentive.

`bot-type="AiBot"` matches GPTBot, ClaudeBot, and similar crawlers. The message is neutral and constructive: "this is not the right interface for you, here is the right one." That is not a technical decision. It is a business decision. A 403 is adversarial; a licensing pointer is the start of a conversation.

---

## Page 6: My Detection

The `/Me` page answers the question every developer asks during integration: "what does StyloBot think about this request, right now?" Use it when debugging unusual classifications or building custom UI that reads detection signals.

*Scenario: a developer has just deployed StyloBot and wants to verify that browser sessions score as human and that `curl` scores as bot.*

```html
<!-- Full detection panel: confidence, risk, reasons, contributing detectors -->
<bot-detection-details collapsed="false"></bot-detection-details>

<!-- Individual components for custom layouts -->
<sb-badge variant="full"></sb-badge>
<sb-confidence display="both" width="100%"></sb-confidence>
<sb-risk-pill></sb-risk-pill>
<sb-summary variant="card"></sb-summary>
```

And via the HttpContext extension API:

```csharp
ViewBag.IsBot      = HttpContext.IsBot();
ViewBag.IsHuman    = HttpContext.IsHuman();
ViewBag.Probability = HttpContext.GetBotProbability();
ViewBag.RiskBand   = HttpContext.GetRiskBand();
ViewBag.BotType    = HttpContext.GetBotType();
ViewBag.BotName    = HttpContext.GetBotName();
ViewBag.Reasons    = HttpContext.GetDetectionReasons().ToList();
```

To simulate different detection states during development:

```bash
# Simulate a search engine crawler
curl -H "ml-bot-test-mode: googlebot" http://localhost:5200/Me

# Simulate a high-risk scraper
curl -H "ml-bot-test-mode: scraper" http://localhost:5200/Me

# Real browser (Playwright, Chrome, etc.) scores as human
```

`EnableTestMode: true` must be set in `appsettings.Development.json` for test mode headers to be honoured.

---

## The tag helper model

Ten tag helpers, what they do, and what they accept:

| Tag helper | Role | Key attributes |
|---|---|---|
| `<sb-human>` | Render only for humans | `fallback` ("show"/"hide" when unclassified; default: show) |
| `<sb-bot>` | Render only for bots | `fallback` (default: hide) |
| `<sb-gate>` | Multi-condition gate | see below |
| `<sb-signal>` | Single blackboard signal gate | `signal`, `condition`, `value`, `fallback`, `negate` |
| `<sb-honeypot>` | Invisible trap fields | `prefix`, `fields` (1-3; default: 2) |
| `<sb-badge>` | Detection status chip | `variant` ("full"/"compact"/"icon") |
| `<sb-confidence>` | Bot probability bar | `display` ("bar"/"text"/"both"), `width` |
| `<sb-risk-pill>` | Risk band label | none |
| `<sb-summary>` | Compact detection card | `variant` ("inline"/"card") |
| `<bot-detection-details>` | Full detection breakdown | `collapsed` (bool), `view` ("default"/"compact") |

`<sb-gate>` is the most general-purpose. Its attributes compose:

```html
<sb-gate human-only>...</sb-gate>
<sb-gate bot-only>...</sb-gate>
<sb-gate verified-only>...</sb-gate>
<sb-gate max-risk="Low">...</sb-gate>
<sb-gate min-risk="Medium">...</sb-gate>
<sb-gate bot-type="SearchEngine,VerifiedBot">...</sb-gate>
<sb-gate bot-type="AiBot" negate="true">...</sb-gate>
<sb-gate max-risk="Low" fallback="hide">...</sb-gate>
```

Risk bands in order: `VeryLow`, `Low`, `Elevated`, `Medium`, `High`, `VeryHigh`, `Critical`.

`<sb-signal>` gates on raw blackboard signals. The `condition` attribute accepts: `exists`, `not-exists`, `true`, `false`, `equals`, `not-equals`, `gt`, `lt`, `gte`, `lte`, `contains`, `any-true`, `all-true`.

```html
<sb-signal signal="ip.is_datacenter" condition="true">...</sb-signal>
<sb-signal signal="detection.probability" condition="gte" value="0.8">...</sb-signal>
```

### Controller and Razor Page attributes

Tag helpers gate rendering. For server-side enforcement - blocking, throttling, or challenging - StyloBot provides action filter attributes for controller actions and Razor Page handlers.

**`[BlockBots]`** - returns 403 for any bot-classified request. Allow flags let specific bot types through.

```csharp
[BlockBots]                                                         // block everything
[BlockBots(AllowSearchEngines = true)]                              // let Googlebot through
[BlockBots(AllowSearchEngines = true, AllowSocialMediaBots = true)] // SEO + social previews
[BlockBots(BlockCountries = "CN,RU", BlockVpn = true)]              // geo + network enforcement
```

Allow flags: `AllowVerifiedBots`, `AllowSearchEngines`, `AllowSocialMediaBots`, `AllowMonitoringBots`, `AllowAiBots`, `AllowGoodBots`, `AllowScrapers`, `AllowMaliciousBots`, `AllowTools`.

Network flags: `BlockCountries`, `AllowCountries`, `BlockVpn`, `BlockProxy`, `BlockDatacenter`, `BlockTor`.

**`[RequireHuman]`** - stricter than `[BlockBots]`; only passes requests classified as human.

**`[AllowBots]`** - exempts an action from a controller-level `[BlockBots]`.

```csharp
[BlockBots]
public class AccountController : Controller
{
    public IActionResult Login() { }          // blocked

    [AllowBots]
    public IActionResult HealthCheck() { }    // passes through
}
```

**`[BotPolicy("name")]`** - applies a named detection and action policy, with per-endpoint overrides.

```csharp
[BotPolicy("strict")]
[BotPolicy("strict", BlockThreshold = 0.75, MinConfidence = 0.85, ActionPolicy = "throttle-stealth")]
```

**`[BotDetector("names")]`** - run specific detectors inline without defining a full policy.

```csharp
[BotDetector("UserAgent,Header,Ip", BlockThreshold = 0.8)]
```

**`[BotAction("name")]`** - override the response action without changing the detection policy.

```csharp
[BotPolicy("default")]
[BotAction("challenge-captcha", FallbackAction = "block")]
public IActionResult Checkout() { }
```

**`[BlockIfSignal]`** and **`[RequireSignal]`** - gate on individual blackboard signals.

```csharp
[BlockIfSignal("ip.is_datacenter")]
[RequireSignal("geo.country_code", Value = "GB")]
```

**`[SkipBotDetection]`** - bypass detection entirely for health checks and metrics endpoints.

### HttpContext extensions

All classification data is available directly in controllers, Razor Pages, and Minimal API handlers:

```csharp
// Classification
bool isBot      = HttpContext.IsBot();
bool isHuman    = HttpContext.IsHuman();
bool isVerified = HttpContext.IsVerifiedBot();
bool isSearch   = HttpContext.IsSearchEngineBot();

// Scores
double prob       = HttpContext.GetBotProbability();   // 0.0 - 1.0
double conf       = HttpContext.GetBotConfidence();
RiskBand risk     = HttpContext.GetRiskBand();         // VeryLow ... Critical
ThreatBand threat = HttpContext.GetThreatBand();       // None ... Critical

// Bot identity
BotType? type = HttpContext.GetBotType();
string?  name = HttpContext.GetBotName();

// Network
bool isDc  = HttpContext.IsDatacenter();
bool isVpn = HttpContext.IsVpn();
string? cc = HttpContext.GetCountryCode();

// Raw signals (blackboard passthrough)
T? val = HttpContext.GetSignal<T>("ip.is_datacenter");

// Recommended action
bool allow   = HttpContext.ShouldAllowRequest();
bool block   = HttpContext.ShouldBlockRequest();
bool captcha = HttpContext.ShouldChallengeRequest();
```

In a Minimal API endpoint:

```csharp
app.MapPost("/api/order", (HttpContext ctx, OrderModel order) =>
{
    if (ctx.IsBot() || ctx.GetRiskBand() >= RiskBand.High)
        return Results.Forbid();

    return Results.Ok(ProcessOrder(order));
});
```

Tag helpers and extension methods read from the same detection result written once per request by the middleware - no double-detection cost.

---

## What the dashboard shows

The dashboard at `/_stylobot` shows the system's live view of your traffic:

- **Fingerprints**: distinct visitors by composite fingerprint (IP + TLS + HTTP/2 + UA + behavioural patterns)
- **Bot %**: proportion of requests from bot-classified sessions, per endpoint
- **Split bar**: visual human/bot ratio per endpoint - useful for spotting endpoints being targeted
- **Your Detection panel**: how StyloBot classified the current browser session, including contributing detector scores and reasons

The per-endpoint split bars are the most immediately useful signal for detecting scraping campaigns. A product detail page with a bar that is 80% red is being systematically scraped. A checkout endpoint showing 100% red is being tested by a voucher bot or credential stuffer.

---

## The one-line case for behaviour-aware UX

Block/allow is a switch. Behaviour-aware gating is a dial.

A switch means every false positive is a lost customer and every false negative is a successful attack. A dial means you can route suspicious-but-not-certain sessions to higher friction without losing them, show your best offers only to your best customers, and give AI crawlers a path to a commercial relationship instead of a 403.

StyloBot gives you the dial. What you do with it is application logic - yours to control, test, and tune without touching your detection infrastructure.

The sample app shows six patterns across six pages. Each one maps to a commercial scenario where the right response to a bot is not "block" but "do something different." That distinction *is* the product.
