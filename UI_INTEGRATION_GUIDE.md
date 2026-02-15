# Bot Name Display in UI - Integration Guide

## ✅ Good News: Infrastructure Already Ready

The dashboard models already have `BotName` fields:
- `DashboardDetectionEvent.BotName` (line 16)
- `DashboardSignatureEvent.BotName` (line 85)

**You just need to populate them!**

---

## 🔌 How to Wire It Up

### Step 1: Connect SignatureDescriptionService to Dashboard

Edit: `Mostlylucid.BotDetection.UI/Services/DetectionBroadcastMiddleware.cs`

```csharp
public class DetectionBroadcastMiddleware
{
    private readonly SignatureDescriptionService _signatureDescService;
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;

    public DetectionBroadcastMiddleware(
        SignatureDescriptionService signatureDescService,  // Add this
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        ...)
    {
        _signatureDescService = signatureDescService;
        _hubContext = hubContext;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // ... existing code ...

        // Wire up signature description events
        _signatureDescService.DescriptionGenerated += async (sig, name, desc) =>
        {
            await _hubContext.Clients.All.BotSignatureNamed(sig, name, desc);
        };
    }
}
```

### Step 2: Add Hub Method

Edit: `Mostlylucid.BotDetection.UI/Hubs/IStyloBotDashboardHub.cs`

```csharp
public interface IStyloBotDashboardHub
{
    // Existing methods...

    /// <summary>New bot name synthesized for a signature</summary>
    Task BotSignatureNamed(string signatureId, string botName, string? description);
}
```

### Step 3: Populate BotName in Detection Events

Edit: `Mostlylucid.BotDetection.UI/Services/DetectionDataExtractor.cs` or wherever `DashboardDetectionEvent` is created:

```csharp
// When building DashboardDetectionEvent, look up bot name from signature
var botName = await GetBotNameForSignature(detection.Signature);

var dashboardEvent = new DashboardDetectionEvent
{
    // ... existing fields ...
    PrimarySignature = detection.Signature,
    BotName = botName,  // ← Add this
    // ... rest of fields ...
};
```

### Step 4: Update Frontend Display

Where signatures are currently displayed (likely in JavaScript/Razor views):

**Old:**
```html
<td>{{ event.primarySignature }}</td>
```

**New:**
```html
<td>
    {{ event.botName || event.primarySignature }}
    <small class="text-muted" v-if="event.botName">
        ({{ event.primarySignature.substring(0, 8) }}...)
    </small>
</td>
```

Or with Razor:
```razor
<td>
    @(event.BotName ?? event.PrimarySignature)
    @if (!string.IsNullOrEmpty(event.BotName))
    {
        <small class="text-muted">(@event.PrimarySignature.Substring(0, 8))...</small>
    }
</td>
```

### Step 5: Register Service in DI

Edit: `Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs`

```csharp
public static IServiceCollection AddStyloBotDashboard(
    this IServiceCollection services,
    Action<StyloBotDashboardOptions>? configure = null)
{
    // ... existing code ...

    // Add signature description service for bot name synthesis
    services.AddHostedService<SignatureDescriptionService>();

    return services;
}
```

---

## 📊 What You'll See

### Current (Signature IDs only):
```
IP: 192.168.1.100
Signature: d4a2f8c9e3b1...
Status: Bot
```

### After Integration (With Bot Names):
```
IP: 192.168.1.100
Signature: GoogleBot Variant (d4a2f8c9e3b1...)
Status: Bot
Description: Search engine crawler from Google datacenter, benign
```

---

## 🔄 Real-Time Updates

When `SignatureDescriptionService` generates a new name:

```javascript
// SignalR connection on frontend
connection.on('BotSignatureNamed', (signatureId, botName, description) => {
    // Update all instances of this signature in the UI
    document.querySelectorAll(`[data-signature="${signatureId}"]`)
        .forEach(el => {
            el.textContent = botName;
            el.title = description;
        });
});
```

---

## 💾 Persistence (Optional)

For persistent bot name storage, add to database:

```sql
CREATE TABLE BotNameCache (
    SignatureId VARCHAR(255) PRIMARY KEY,
    BotName VARCHAR(255) NOT NULL,
    Description TEXT,
    SynthesizedAt DATETIME NOT NULL,
    RequestCount INT DEFAULT 0
);
```

Then in `SignatureDescriptionService`:

```csharp
DescriptionGenerated += async (sig, name, desc) =>
{
    await _database.UpsertBotName(sig, name, desc);
};
```

---

## 📝 Testing Checklist

- [ ] Build solution compiles
- [ ] Dashboard loads without errors
- [ ] Signature reaches threshold (50 requests default)
- [ ] Bot name appears in feed
- [ ] Hovering shows full description
- [ ] Real-time updates work (SignalR)
- [ ] Mobile view displays correctly

---

## 🎯 Summary

**No code changes needed in `Mostlylucid.BotDetection.Core`** - everything is ready!

Just wire up the `SignatureDescriptionService` event → Dashboard Hub → Frontend and start displaying bot names.

**Estimated time to integrate**: 30-45 minutes

