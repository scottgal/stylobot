# Getting Started

## Audience

This guide is for engineers integrating Stylobot into ASP.NET Core apps.

## Prerequisites

- .NET SDK 10
- Basic ASP.NET Core app

## Minimal integration

```csharp
builder.Services.AddBotDetection();

var app = builder.Build();
app.UseBotDetection();
app.MapBotDetectionEndpoints();
```

## Verify

```bash
curl http://localhost:5080/bot-detection/check
curl -A "curl/8.4.0" http://localhost:5080/bot-detection/check
```

## Next

- `ARCHITECTURE.md`
- `OPERATIONS.md`
- `TESTING.md`
