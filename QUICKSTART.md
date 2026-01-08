# Quick Start Guide - Bot Detection Demo with Real-Time Signatures

**Get up and running in 5 minutes!**

## Prerequisites

- .NET 8.0 or higher
- Web browser (Chrome/Edge/Firefox)

## Step 1: Run the Demo

```bash
cd D:\Source\mostlylucid.nugetpackages\Mostlylucid.BotDetection.Demo
dotnet run
```

You should see:
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://localhost:5001
      Now listening on: http://localhost:5000
```

## Step 2: Open the Signature Demo

Open your browser to:
```
https://localhost:5001/SignatureDemo
```

## Step 3: Subscribe to Live Signatures

1. Click the **"Subscribe to Live Stream"** button
2. You'll see "Connected" status turn green
3. Recent signatures will load automatically

## Step 4: Generate Test Traffic

Open a new terminal and make test requests:

### Test as a Human Browser
```bash
curl https://localhost:5001/api/test -H "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" -k
```

### Test as a Bot
```bash
curl https://localhost:5001/api/test -H "User-Agent: curl/8.4.0" -k
```

### Test as a Scanner
```bash
curl https://localhost:5001/api/test -H "User-Agent: Nikto/2.1.6" -k
```

### Test as Googlebot
```bash
curl https://localhost:5001/api/test -H "User-Agent: Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)" -k
```

## Step 5: Watch Signatures Appear!

In your browser, you'll see **real-time signatures** streaming in:

- **Green badges** = Low bot probability (human)
- **Yellow badges** = Medium bot probability
- **Red badges** = High bot probability (bot)

Each signature shows:
- Bot probability percentage
- Request path and IP
- User-Agent
- Risk band (VeryLow, Low, Elevated, Medium, High, VeryHigh)
- Number of detector contributions

## Step 6: Explore Detector Contributions

Click on any signature to see the full analysis:

- **21 detectors** analyzed the request
- Each contribution shows:
  - Detector name
  - Confidence delta (positive = bot, negative = human)
  - Weight (importance)
  - Priority
  - Reason/signals

## Step 7: View Statistics

The statistics panel updates automatically:
- Total signatures captured
- Bot count
- Human count
- Average bot probability

## Advanced: REST API Usage

### Get a Specific Signature

```bash
# Copy a signature ID from the UI, then:
curl https://localhost:5001/api/signature/YOUR-SIGNATURE-ID -k | jq
```

### Get Recent Signatures

```bash
curl https://localhost:5001/api/signature/recent?count=20 -k | jq
```

### Get Statistics

```bash
curl https://localhost:5001/api/signature/stats -k | jq
```

### Get Current Request Signature

```bash
curl https://localhost:5001/api/signature/current -k | jq
```

## JavaScript Integration

```html
<script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@latest/dist/browser/signalr.min.js"></script>
<script>
const connection = new signalR.HubConnectionBuilder()
    .withUrl("https://localhost:5001/hubs/signatures")
    .withAutomaticReconnect()
    .build();

connection.on("ReceiveNewSignature", (sig) => {
    console.log(`${sig.signatureId}: ${(sig.evidence.botProbability * 100).toFixed(1)}% bot`);
    console.log(`Risk: ${sig.evidence.riskBand}`);
    console.log(`Path: ${sig.requestMetadata.path}`);
    console.log(`Detectors: ${sig.evidence.contributions.length}`);
});

await connection.start();
await connection.invoke("SubscribeToSignatures");
</script>
```

## All 21 Detectors Explained

When you look at a signature, you'll see contributions from:

### Fast Reputation & Analysis (< 1ms each)
1. **FastPathReputation** - Cached known-good/bad
2. **HoneypotLink** - Honeypot trap detection
3. **UserAgent** - Bot UA patterns
4. **Header** - Missing/suspicious headers
5. **Ip** - Datacenter/cloud detection
6. **SecurityTool** - Scanner signatures
7. **CacheBehavior** - Cache header analysis
8. **Behavioral** - Rate limiting patterns
9. **AdvancedBehavioral** - Advanced patterns
10. **ClientSide** - Browser fingerprints
11. **Inconsistency** - Cross-layer contradictions
12. **VersionAge** - Browser/OS freshness
13. **ReputationBias** - Historical reputation
14. **Heuristic** - ML-based scoring

### Network Reputation (~100ms)
15. **ProjectHoneypot** - IP reputation lookup

### Advanced Fingerprinting (New!)
16. **TlsFingerprint** - JA3/JA4 TLS fingerprinting
17. **TcpIpFingerprint** - p0f passive OS detection
18. **Http2Fingerprint** - HTTP/2 SETTINGS analysis
19. **MultiLayerCorrelation** - Cross-layer consistency
20. **BehavioralWaveform** - Temporal pattern detection
21. **ResponseBehavior** - Historical behavior feedback

## Common Scenarios

### Scenario 1: Legitimate Search Engine Bot

```bash
curl https://localhost:5001/api/test -H "User-Agent: Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)" -k
```

**Expected Result:**
- Bot Probability: ~70-85%
- Risk Band: Medium to High
- **Reason**: Googlebot is a legitimate bot, but still a bot!
- **UserAgent detector**: Recognizes Googlebot pattern
- **Ip detector**: May flag if not from Google IP ranges

### Scenario 2: Security Scanner

```bash
curl https://localhost:5001/api/test -H "User-Agent: Nikto/2.1.6" -k
```

**Expected Result:**
- Bot Probability: ~95-100%
- Risk Band: VeryHigh
- **SecurityTool detector**: Strong positive contribution
- **UserAgent detector**: Recognizes scanner pattern
- **Recommendation**: Block

### Scenario 3: Headless Browser

```bash
curl https://localhost:5001/api/test -H "User-Agent: Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36" -k
```

**Expected Result:**
- Bot Probability: ~80-90%
- Risk Band: High
- **UserAgent detector**: Recognizes "Headless" keyword
- **ClientSide detector**: Missing browser fingerprint

### Scenario 4: Real Human

Open your actual browser and visit:
```
https://localhost:5001/api/test
```

**Expected Result:**
- Bot Probability: ~5-20%
- Risk Band: VeryLow to Low
- **Multiple detectors**: Negative contributions (human indicators)
- **ClientSide detector**: Valid browser fingerprint

## Troubleshooting

### SignalR Not Connecting

**Problem**: Connection status shows "Disconnected"

**Solution**:
1. Check browser console for errors
2. Verify HTTPS certificate is trusted (click through warning)
3. Check firewall/antivirus isn't blocking WebSockets

### No Signatures Appearing

**Problem**: No signatures show up after making requests

**Solution**:
1. Verify demo is running (`dotnet run`)
2. Check logs for errors
3. Try clicking "Unsubscribe" then "Subscribe" again
4. Make a test request: `curl https://localhost:5001/api/test -k`

### Certificate Errors

**Problem**: Browser shows certificate warning

**Solution**:
1. Click "Advanced" then "Proceed to localhost"
2. For production, use proper TLS certificates
3. For dev, this is normal for self-signed certs

## Next Steps

1. **Explore the Original Demo**: Visit `/bot-test` for the full interactive simulator
2. **Read the Documentation**: See `README.md` for complete feature list
3. **Check the Tests**: Look at `Mostlylucid.BotDetection.Demo.Tests` for examples
4. **Customize Policies**: Edit `appsettings.json` to adjust detector configurations
5. **Add Authentication**: Secure the API endpoints before production deployment

## Performance Tips

- **Default capacity**: 10,000 signatures in memory
- **Adjust if needed**: Change in `Program.cs`:
  ```csharp
  services.AddSingleton<SignatureStore>(sp =>
      new SignatureStore(logger, maxSignatures: 50000));
  ```
- **For production**: Replace with Redis/SQL for persistence
- **SignalR scale-out**: Use backplane (Redis/Azure SignalR Service)

## Support

- **Issues**: https://github.com/anthropics/claude-code/issues
- **Documentation**: See `/docs` folder
- **Examples**: See `/Pages` folder

---

**You're all set!** 🎉

Enjoy exploring the comprehensive bot detection system with real-time signature analysis.

**Built with Claude Code** 🤖
