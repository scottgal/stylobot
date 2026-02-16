# UI Components (Tag Helpers & View Components)

The `Mostlylucid.BotDetection.UI` package provides Razor Tag Helpers and View Components for rendering bot detection results directly in your pages. All components read detection data from `HttpContext.Items` — no manual wiring needed.

## Setup

```csharp
// Program.cs
builder.Services.AddStyloBotDashboard();
app.UseStyloBotDashboard();
```

In your `_ViewImports.cshtml`:

```cshtml
@addTagHelper *, Mostlylucid.BotDetection.UI
```

Include the standalone CSS for component styling:

```html
<link rel="stylesheet" href="/_stylobot/css/sb-components.css" />
```

## Content Gating Tag Helpers

These tag helpers conditionally show or hide content based on the detection result for the current request.

### `<sb-gate>`

Universal content gate. All conditions AND together. If no conditions are set, content always shows.

| Attribute | Type | Description |
|-----------|------|-------------|
| `human-only` | bool | Show only to humans |
| `bot-only` | bool | Show only to bots |
| `min-risk` | string | Show when risk >= this band (VeryLow, Low, Elevated, Medium, High, VeryHigh) |
| `max-risk` | string | Show when risk <= this band |
| `bot-type` | string | Comma-separated bot types to match (SearchEngine, AiBot, Scraper, etc.) |
| `verified-only` | bool | Show only to verified bots |
| `fallback` | string | `"show"` (default, fail-open) or `"hide"` (fail-closed) when detection hasn't run |
| `negate` | bool | Invert all conditions |

```cshtml
<!-- Premium content for humans only -->
<sb-gate human-only>
    <h2>Welcome!</h2>
    <p>Premium content here.</p>
</sb-gate>

<!-- Structured data for verified search engines -->
<sb-gate bot-type="SearchEngine" verified-only>
    <script type="application/ld+json">{ ... }</script>
</sb-gate>

<!-- Challenge for medium+ risk -->
<sb-gate min-risk="Medium">
    <div class="captcha-wrapper">Please verify you're human.</div>
</sb-gate>

<!-- Show to everyone EXCEPT bots (negate + bot-only = human-only) -->
<sb-gate bot-only negate>
    <p>You're not a bot.</p>
</sb-gate>
```

### `<sb-human>`

Shorthand for `<sb-gate human-only>`. Shows content only to humans. Default fallback is `"show"` (fail-open).

| Attribute | Type | Description |
|-----------|------|-------------|
| `fallback` | string | `"show"` (default) or `"hide"` when detection hasn't run |

```cshtml
<sb-human>
    <p>This content is only visible to human visitors.</p>
</sb-human>

<!-- Fail-closed: hide if detection hasn't run -->
<sb-human fallback="hide">
    <p>Sensitive content, hidden until verified human.</p>
</sb-human>
```

### `<sb-bot>` (in `SbHumanTagHelper.cs`)

Shorthand for `<sb-gate bot-only>`. Shows content only to bots. Default fallback is `"hide"` (fail-closed).

```cshtml
<sb-bot>
    <p>Hello, bot! Here's some structured data for you.</p>
</sb-bot>
```

### `<sb-risk>`

Content gate based on risk band level.

| Attribute | Type | Description |
|-----------|------|-------------|
| `band` | string | Exact risk band match |
| `min` | string | Show when risk >= this band |
| `max` | string | Show when risk <= this band |
| `fallback` | string | `"show"` (default) or `"hide"` when detection hasn't run |

```cshtml
<!-- Show for low-risk visitors only -->
<sb-risk max="Low">
    <p>Normal access granted.</p>
</sb-risk>

<!-- Show warning for high+ risk -->
<sb-risk min="High" fallback="hide">
    <div class="alert alert-danger">Suspicious activity detected.</div>
</sb-risk>

<!-- Exact match -->
<sb-risk band="Medium">
    <p>Elevated monitoring active.</p>
</sb-risk>
```

### `<sb-signal>`

Content gate based on individual blackboard signals from the detection pipeline. Supports rich conditions.

| Attribute | Type | Description |
|-----------|------|-------------|
| `signal` | string | Comma-separated signal key(s) (e.g. `"geo.country_code"`, `"ip.is_datacenter"`) |
| `condition` | string | `exists` (default), `not-exists`, `equals`, `not-equals`, `true`, `false`, `any-true`, `all-true`, `gt`, `lt`, `gte`, `lte`, `contains` |
| `value` | string | Comparison value for equals/numeric conditions |
| `fallback` | string | `"show"` (default) or `"hide"` |
| `negate` | bool | Invert the condition |

```cshtml
<!-- Show VPN notice -->
<sb-signal signal="geo.is_vpn" condition="true">
    <p>You appear to be using a VPN.</p>
</sb-signal>

<!-- US visitors only -->
<sb-signal signal="geo.country_code" condition="equals" value="US">
    <p>US-specific content.</p>
</sb-signal>

<!-- Any anonymisation detected -->
<sb-signal signal="geo.is_vpn,geo.is_tor" condition="any-true">
    <div class="alert">Anonymised traffic detected.</div>
</sb-signal>

<!-- High bot probability threshold -->
<sb-signal signal="detection.bot_probability" condition="gte" value="0.8">
    <p>Very likely a bot.</p>
</sb-signal>

<!-- Datacenter IP check ran (signal exists, regardless of value) -->
<sb-signal signal="ip.is_datacenter" condition="exists">
    <p>IP analysis completed.</p>
</sb-signal>
```

## Display Tag Helpers

These render visual components showing detection data.

### `<sb-badge />`

Inline detection badge showing bot/human status with risk colouring. Renders via the `SbBadgeViewComponent`.

| Attribute | Type | Default | Description |
|-----------|------|---------|-------------|
| `variant` | string | `"full"` | `"full"` (icon + label + risk), `"compact"` (icon + risk), `"icon"` (icon only) |
| `class` | string | — | Additional CSS classes |

```cshtml
<!-- Full badge with icon, label, and risk -->
<sb-badge />

<!-- Compact badge (icon + risk level) -->
<sb-badge variant="compact" />

<!-- Icon only -->
<sb-badge variant="icon" />

<!-- With custom styling -->
<sb-badge class="my-custom-badge" />
```

Output example:
```html
<span class="sb-badge sb-badge--human sb-badge--risk-low" data-sb-is-bot="false" data-sb-risk="Low">
    <span class="sb-badge__icon">👤</span>
    <span class="sb-badge__label">Human</span>
    <span class="sb-badge__risk">Low risk</span>
</span>
```

### `<sb-confidence />`

Bot probability confidence meter. Renders via the `SbConfidenceViewComponent`.

| Attribute | Type | Default | Description |
|-----------|------|---------|-------------|
| `display` | string | `"bar"` | `"bar"`, `"text"`, or `"both"` |
| `width` | string | `"120px"` | CSS width for the bar |
| `class` | string | — | Additional CSS classes |

```cshtml
<!-- Default bar display -->
<sb-confidence />

<!-- Text-only display -->
<sb-confidence display="text" />

<!-- Both bar and text, wider -->
<sb-confidence display="both" width="200px" />
```

### `<sb-risk-pill />`

Compact coloured risk band pill. Renders a single `<span>` — no ViewComponent needed.

| Attribute | Type | Default | Description |
|-----------|------|---------|-------------|
| `class` | string | — | Additional CSS classes |

```cshtml
<sb-risk-pill />
```

Output:
```html
<span class="sb-risk-pill sb-risk-pill--low" data-sb-risk="Low">Low</span>
```

### `<sb-summary />`

Full detection summary. Renders via the `SbSummaryViewComponent` with two variants.

| Attribute | Type | Default | Description |
|-----------|------|---------|-------------|
| `variant` | string | `"inline"` | `"inline"` (compact one-liner) or `"card"` (full card with stats) |
| `class` | string | — | Additional CSS classes |

```cshtml
<!-- Inline summary -->
<sb-summary />

<!-- Card variant with full stats -->
<sb-summary variant="card" />
```

The card variant displays: bot probability %, confidence %, processing time, and policy name.

### `<sb-honeypot />`

Injects hidden honeypot trap fields into a form. Bots that fill these hidden fields are detected. Must be inside a `<form>` element.

| Attribute | Type | Default | Description |
|-----------|------|---------|-------------|
| `prefix` | string | `"sb"` | Field name prefix |
| `fields` | int | `2` | Number of trap fields (1-3) |

```cshtml
<form method="post" asp-action="Submit">
    <sb-honeypot />
    <input type="text" name="name" />
    <button type="submit">Submit</button>
</form>
```

Server-side validation:

```csharp
[HttpPost]
public IActionResult Submit()
{
    if (HoneypotValidator.IsTriggered(Request))
    {
        // Bot detected — fake success or block
        return Ok("Thank you!"); // Stealth response
    }
    // Process real submission...
}
```

## CSS Classes Reference

All components use the `sb-` prefix. Include `sb-components.css` for default styling.

| Class | Description |
|-------|-------------|
| `sb-badge` | Badge container |
| `sb-badge--bot` / `sb-badge--human` | Bot/human state |
| `sb-badge--risk-{band}` | Risk-coloured variant (e.g. `sb-badge--risk-high`) |
| `sb-risk-pill` | Compact risk pill |
| `sb-risk-pill--{band}` | Risk-coloured pill |
| `sb-summary-card` | Card summary container |
| `sb-summary-card--bot` / `--human` | Card state variant |

## Fallback Behaviour

All gating tag helpers support a `fallback` attribute that controls what happens when bot detection hasn't run for the current request (e.g. the path is excluded):

- `"show"` (default for most) — fail-open, content is visible
- `"hide"` — fail-closed, content is suppressed

Choose based on your security requirements. For sensitive content, use `fallback="hide"`.

## Data Flow

1. `BotDetectionMiddleware` runs the detection pipeline and stores results in `HttpContext.Items`
2. Tag helpers read results via `DetectionDataExtractor`, which pulls from `HttpContext.Items`
3. No extra HTTP calls or database lookups — all data is in-memory per request
