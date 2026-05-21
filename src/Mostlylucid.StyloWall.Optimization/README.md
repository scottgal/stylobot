# Mostlylucid.StyloWall.Optimization

The first StyloWall pack: response optimizations modeled on Cloudflare-style edge transforms, gated by StyloBot detection.

## What's in v0.1

- `HtmlToMarkdownTransform` — AngleSharp-driven walker that emits clean Markdown from arbitrary HTML. Skips `<script>`, `<style>`, `<noscript>`, `<iframe>`, `<svg>`. Prefers `<main>`/`<article>` as content root when present. Emits GFM tables, fenced code blocks (language hint from `class="language-*"`), reference-free links and images.

## Wiring

```csharp
builder.Services.AddStyloBot();
builder.Services.AddStyloWall();
builder.Services.AddStyloWallOptimization();

app.UseStyloBot();
app.UseStyloWall();
```

Then any of:

- `curl -H "Accept: text/markdown" https://your.site/post/foo`
- `https://your.site/post/foo?format=md`
- An AI scraper (BotType=AiBot, prob ≥ MinBotProbability) hitting an HTML page

…receives `text/markdown` instead of HTML, with `X-StyloWall-Mode: markdown` and `X-StyloWall-Trigger: <reason>` for observability.

## Roadmap

- ImageRewriteTransform (ImageSharp.Web sizing/format)
- HtmlMinifyTransform
- LlmsTxtGenerator (site-wide `/llms.txt` builder from sitemap + markdown extraction)
