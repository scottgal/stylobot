# Tutorial: Behaviour-Aware UX with StyloBot

This tutorial walks through the `Mostlylucid.BotDetection.Sample` project - a minimal ASP.NET Core MVC store that demonstrates StyloBot's tag helper and controller attribute API across six realistic commercial scenarios.

The full narrative article is at [docs/articles/behaviour-aware-ux.md](../../docs/articles/behaviour-aware-ux.md). This document covers setup, running the sample, and what to look for on each page.

---

## What this tutorial covers

| Page | Scenario | Techniques demonstrated |
|---|---|---|
| Home (`/`) | Price scrapers harvest the catalogue | `<sb-human>`, `<sb-bot>`, `<sb-gate bot-type>` |
| Product detail | Revenue protection, loyalty offers | `<sb-gate max-risk>`, `<sb-gate min-risk>`, `<sb-signal>`, `<sb-gate human-only>` |
| Checkout | Voucher testers, card testing | `<sb-honeypot>`, `<sb-gate>` tiers, `HoneypotValidator`, `<sb-bot>` fallback |
| Login | Credential stuffing | `<sb-bot>` deterrence, `<sb-honeypot>`, `HttpContext.IsBot()` |
| Newsletter | AI training crawlers, spam bots | `<sb-gate bot-type="AiBot">`, silent accept, `<sb-human>` copy gating |
| My Detection | Developer diagnostics | `<bot-detection-details>`, `<sb-summary>`, `<sb-confidence>`, test mode headers |

---

## Installation

```bash
dotnet add package Mostlylucid.BotDetection
dotnet add package Mostlylucid.BotDetection.UI
```

Add the tag helper import to `_ViewImports.cshtml`:

```cshtml
@addTagHelper *, Mostlylucid.BotDetection.UI
```

Include the component stylesheet in your layout:

```html
<link rel="stylesheet" href="/_content/Mostlylucid.BotDetection.UI/css/sb-components.css" />
```

---

## Program.cs

`AddStyloBot()` registers detection, the dashboard, and all tag helpers in one call. `UseStyloBot()` wires the middleware in the correct order.

```csharp
builder.Services.AddStyloBot(
    configureDashboard: dashboard =>
    {
        dashboard.BasePath = "/_stylobot";
        dashboard.AllowUnauthenticatedAccess = true; // dev only
    },
    configureDetection: detection =>
    {
        detection.ExcludeLocalIpFromBroadcast = false; // see localhost traffic in dashboard
    });

app.UseRouting();
app.UseStyloBot();
app.MapHub<StyloBotDashboardHub>("/_stylobot/hub");
```

That is everything. Every request now carries a risk score available to tag helpers and `HttpContext` extensions.

---

## Running the sample

```bash
cd Mostlylucid.BotDetection.Sample
dotnet run
```

App: `http://localhost:5200`  
Dashboard: `http://localhost:5200/_stylobot`

### Simulating bot traffic

```bash
# Simulate a high-risk scraper
curl -H "ml-bot-test-mode: scraper" http://localhost:5200/

# Simulate a search engine crawler
curl -H "ml-bot-test-mode: googlebot" http://localhost:5200/Product/Detail/1

# Simulate an AI training bot
curl -H "ml-bot-test-mode: aibot" http://localhost:5200/Newsletter/Subscribe

# Real browser (Chrome, Firefox) scores as human automatically
```

`EnableTestMode: true` must be set in `appsettings.Development.json` for test headers to be honoured.

---

## Page walkthroughs

### Home: price scraper defence

The shop floor demonstrates rendering-level information control. A price scraper hits `/` and sees a neutral catalogue message with no welcome copy, no category count, and no promotional context. Googlebot gets structured `<meta>` data for indexing. High-risk sessions see a friction warning before the buy buttons.

Key principle: **these are not access controls, they are rendering hints**. The product list still renders for everyone. What changes is the surrounding commercial context.

See: [Page 1 walkthrough in the article](../../docs/articles/behaviour-aware-ux.md#page-1-the-shop-floor)

### Product detail: revenue protection

Three independent commercial patterns on one page:

- **Loyalty targeting**: discount codes gated on `max-risk="Low"` - price scrapers never see the offer
- **Progressive friction**: medium-risk visitors warned (not blocked) before the cart button
- **Datacenter gating**: `<sb-signal signal="ip.is_datacenter">` removes the buy button for cloud/VPN IPs

See: [Page 2 walkthrough in the article](../../docs/articles/behaviour-aware-ux.md#page-2-the-product-page)

### Checkout: voucher tester and fraud bot defence

Three-layer protection with no CAPTCHA:

1. `<sb-gate human-only>` gates the entire form - bots see a dead-end message
2. `<sb-honeypot prefix="co" fields="2">` traps bots that slip through (misclassified or direct POST)
3. `HoneypotValidator.IsTriggered(HttpContext)` in the controller gives a silent accept

Risk-tiered CTAs mean express checkout requires `max-risk="Low"` - high-velocity sessions from unusual fingerprints route to standard flow without blocking the sale.

See: [Page 3 walkthrough in the article](../../docs/articles/behaviour-aware-ux.md#page-3-checkout)

### Login: credential stuffing defence

Three independent layers:

1. `<sb-bot>` renders a deterrent message (bot sees it but can still access the form)
2. `<sb-honeypot>` inside the form - humans leave it blank, bots fill everything
3. Controller: `HoneypotValidator.IsTriggered() || HttpContext.IsBot()` redirects to `LoginDenied`

Unlike checkout, outright refusal is correct here: the cost of a false positive (a retry) is far lower than the cost of a missed credential-stuffing hit (an account takeover).

See: [Page 4 walkthrough in the article](../../docs/articles/behaviour-aware-ux.md#page-4-login)

### Newsletter: structured AI bot handling

AI training crawlers (GPTBot, ClaudeBot) are handled differently from generic bots. They are polite, they identify themselves, and they are operated by companies with legal teams. A 403 is adversarial; `<sb-gate bot-type="AiBot">` shows a data-licensing message instead. All submissions - bot or human - return the same `Thanks` confirmation page so bots cannot distinguish success from silent discard.

See: [Page 5 walkthrough in the article](../../docs/articles/behaviour-aware-ux.md#page-5-newsletter)

### My Detection: developer diagnostics

`/Me` is primarily a developer tool. During integration it lets you verify that browser sessions score as human and that `curl` scores as bot. The page exposes the full detection breakdown via `<bot-detection-details>` and `HttpContext` extension methods.

See: [Page 6 walkthrough in the article](../../docs/articles/behaviour-aware-ux.md#page-6-my-detection)

---

## The classification banner

Every page in the sample shows a classification banner (set via `ViewBag.PageContext` in each view and rendered in `_Layout.cshtml`). It uses `<sb-badge>` and `<sb-risk-pill>` so you can see in real time how StyloBot has classified your current session as you browse.

---

## Controller attributes used in the sample

```csharp
// Server-side honeypot check (all form controllers)
if (HoneypotValidator.IsTriggered(HttpContext) || HttpContext.IsBot())
    return RedirectToAction("Denied");

// Gate an entire controller to humans only
[RequireHuman]
public class SecureController : Controller { }

// Allow search engines through for SEO pages
[BlockBots(AllowSearchEngines = true)]
public IActionResult ProductDetail() { }
```

For the full attribute reference see [blocking-and-filters.md](blocking-and-filters.md).

---

## Dashboard

After browsing the sample app, visit `/_stylobot` to see:

- **Fingerprints**: each distinct visitor by composite fingerprint (IP + TLS + HTTP/2 + UA + behavioural)
- **Endpoints**: human/bot split per URL - a product page at 80% red is being scraped
- **Top Bots**: ranked by hit count with sparklines
- **Your Detection**: the full breakdown for your current browser session

Use the `ml-bot-test-mode` header to generate different traffic types and watch the dashboard update in real time via SignalR.

---

## Next steps

- [ui-components.md](ui-components.md) - Full tag helper and view component reference
- [blocking-and-filters.md](blocking-and-filters.md) - All controller/Razor Page attributes
- [action-policies.md](action-policies.md) - Named response policies (throttle, challenge, redirect, logonly)
- [signals-and-custom-filters.md](signals-and-custom-filters.md) - Raw signal access and custom filter attributes
- [configuration.md](configuration.md) - Full `BotDetectionOptions` reference
