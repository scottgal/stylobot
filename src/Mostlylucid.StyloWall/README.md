# Mostlylucid.StyloWall

StyloWall is a semantic firewall that runs alongside [StyloBot](https://github.com/scottgal/stylobot). Where StyloBot decides *what* a visitor is, StyloWall decides *what content the response should carry*. It provides a gated response-transform pipeline so policies like "serve markdown to AI scrapers" or "rewrite images for low-bandwidth clients" become first-class middleware.

This package is the abstractions + middleware. Optimization packs (HTML→Markdown, image rewriting, minification) ship separately, starting with `Mostlylucid.StyloWall.Optimization`.

## Pipeline

```
UseStyloBot()    -> detection populates HttpContext (bot type, probability, signals)
UseStyloWall()   -> gate decides whether to buffer the response; if yes, runs the
                    IResponseTransform chain after the inner pipeline writes the body
```

## Gating triggers

The default gate considers four signals, all configurable:

- Detection verdict (`BotType.AiBot`, `aiscraper.detected` signal)
- `Accept: text/markdown` request header
- Per-route configuration (`StyloWallOptions.Routes`)
- `?format=md` query string (dev/debug + public alternate URLs)

The gate runs *before* the inner pipeline writes the response. If it returns no transform mode, the request passes through with zero buffering.

## Adding a transform

```csharp
public sealed class MyTransform : IResponseTransform
{
    public string Mode => "markdown";
    public ValueTask<TransformResult> TransformAsync(ResponseTransformContext ctx, CancellationToken ct) { ... }
}

services.AddStyloWall();
services.AddSingleton<IResponseTransform, MyTransform>();
```

## License

Unlicense.
