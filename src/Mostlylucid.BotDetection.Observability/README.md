# Mostlylucid.BotDetection.Observability

Structured logs, metrics, and OpenTelemetry export for StyloBot.

```csharp
builder.Services.AddBotDetection();
builder.Services.AddStyloBotObservability(builder.Configuration);
```

See `docs/observability.md` in the main repo for the full configuration surface.