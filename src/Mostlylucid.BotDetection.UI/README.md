# Mostlylucid.BotDetection.UI

UI components (Tag Helpers and View Components) for displaying bot detection results in ASP.NET Core applications.

## Features

- **Tag Helper**: Easy-to-use `<bot-detection-details />` tag
- **View Component**: Programmatic rendering with `@await Component.InvokeAsync("BotDetectionDetails")`
- **Dual Mode**: Works both inline with middleware and behind YARP proxy
- **Styled Display**: Professional, responsive UI with gradient headers and contribution bars
- **Zero Configuration**: Automatically extracts detection data from `HttpContext.Items` or headers

## Installation

```bash
dotnet add package Mostlylucid.BotDetection.UI
```

## Usage

### 1. Add Services

In `Program.cs`:

```csharp
builder.Services.AddHttpContextAccessor();
```

### 2. Add Tag Helper

In `_ViewImports.cshtml`:

```cshtml
@addTagHelper *, Mostlylucid.BotDetection.UI
```

### 3. Include CSS

In your layout (`_Layout.cshtml` or equivalent):

```html
<link rel="stylesheet" href="~/_content/Mostlylucid.BotDetection.UI/bot-detection-details.css" />
```

### 4. Use in Views

#### Option A: Tag Helper (Recommended)

```cshtml
<bot-detection-details />
```

With options:

```cshtml
<bot-detection-details class="my-custom-class" collapsed="false" />
```

#### Option B: View Component

```cshtml
@await Component.InvokeAsync("BotDetectionDetails")
```

## How It Works

The component automatically detects and extracts bot detection data from two sources:

### 1. Inline Mode (HttpContext.Items)

When bot detection middleware runs inline, it stores `AggregatedEvidence` in
`HttpContext.Items["BotDetection.Evidence"]`.

The component reads this directly and displays:

- Bot vs Human classification
- Bot probability and confidence scores
- Risk band (VeryLow, Low, Medium, High, VeryHigh)
- Top detection reasons
- Per-detector contributions with visual bars
- Processing time and metadata

### 2. YARP Proxy Mode (Headers)

When behind a YARP proxy, bot detection results are forwarded via headers:

- `X-Bot-Detection-Result`: true/false
- `X-Bot-Detection-Probability`: 0.0-1.0
- `X-Bot-Detection-Confidence`: 0.0-1.0
- `X-Bot-Detection-RiskBand`: VeryLow, Low, etc.
- `X-Bot-Detection-Reasons`: JSON array of reasons
- `X-Bot-Detection-Contributions`: JSON array of detector contributions
- `X-Bot-Detection-Cluster`: YARP cluster name
- `X-Bot-Detection-Destination`: YARP destination

The component automatically reads these headers and displays the same rich UI.

## Display Features

### Header Section

- 🤖/👤 Icon (Bot/Human)
- Bot Probability percentage
- Confidence percentage
- Risk Band badge (color-coded)

### Metadata Section

- Bot Type (if detected)
- Bot Name (if identified)
- Policy applied
- Action taken (Allow, Block, Throttle, Challenge)
- Processing time in milliseconds

### YARP Section (if behind proxy)

- Cluster name
- Destination server

### Detection Reasons

- Top 5 reasons for the detection decision
- Ordered by contribution strength

### Detector Contributions

- Visual contribution bars (positive = red, negative = green)
- Detector name and category
- Confidence delta, weight, and execution time
- Priority/wave information

### Footer

- Request ID for correlation
- Detection timestamp (UTC)

## Customization

### CSS Classes

The component uses these CSS classes:

- `.bot-detection-details` - Container
- `.bot-detection-header` - Header with gradient
- `.bot-detection-status.is-bot` - Bot indicator
- `.bot-detection-status.is-human` - Human indicator
- `.risk-verylow`, `.risk-low`, `.risk-medium`, `.risk-high`, `.risk-veryhigh` - Risk badges
- `.contribution-bar.positive` - Positive contributions (toward bot)
- `.contribution-bar.negative` - Negative contributions (toward human)

Override these in your own CSS to customize appearance.

### Collapsed State

Show initially collapsed:

```cshtml
<bot-detection-details collapsed="true" />
```

Add custom CSS class:

```cshtml
<bot-detection-details class="my-detection-panel" />
```

## Example Output

```
┌─────────────────────────────────────────────────────────┐
│ 🤖 Bot    Bot Probability: 87.5%   Confidence: 92.3%   │
│                                Risk Band: High          │
├─────────────────────────────────────────────────────────┤
│ Bot Type: Scraper   Policy: Aggressive   Action: Block │
│ Processing Time: 45.23ms                                │
├─────────────────────────────────────────────────────────┤
│ Detection Reasons                                        │
│  • Datacenter IP detected                               │
│  • Missing common browser headers                       │
│  • Suspicious timing pattern                            │
├─────────────────────────────────────────────────────────┤
│ Detector Contributions                                   │
│                                                          │
│ DatacenterIpDetector (Network)              +45.0%     │
│ ████████████████████████████                            │
│ Reason: IP from AWS datacenter               │
│ Delta: 0.450  Weight: 1.00  Time: 12.5ms  Priority: 0  │
│                                                          │
│ HeaderAnalysisDetector (Headers)            +28.5%     │
│ ███████████████                                         │
│ Reason: Missing sec-ch-ua headers                       │
│ Delta: 0.285  Weight: 1.00  Time: 8.3ms  Priority: 0   │
└─────────────────────────────────────────────────────────┘
```

## Requirements

- ASP.NET Core 8.0 or 9.0
- Mostlylucid.BotDetection (automatically referenced)

## License

MIT
