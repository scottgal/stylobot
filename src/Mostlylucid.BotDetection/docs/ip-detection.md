# IP Detection

IP detection identifies requests originating from datacenter infrastructure, cloud providers, and anonymization
networks.

## How It Works

The detector checks client IP addresses against:

1. **Downloaded cloud provider ranges** - Official IP ranges from AWS, Azure, GCP, etc.
2. **Static datacenter prefixes** - Pre-configured CIDR ranges
3. **First octet heuristics** - Quick provider identification
4. **Tor exit nodes** - Anonymization network detection

## Detection Flow

```
Client IP → X-Forwarded-For extraction → Real IP
         ↓
         → Downloaded CIDR check → +0.5 confidence (cloud provider)
         ↓
         → Static CIDR check → +0.4 confidence (datacenter)
         ↓
         → First octet heuristic → +0.3 confidence (likely cloud)
         ↓
         → Tor exit node check → +0.5 confidence (anonymization)
```

## Configuration

```json
{
  "BotDetection": {
    "EnableIpDetection": true,
    "DatacenterIpPrefixes": [
      "3.0.0.0/8",
      "13.0.0.0/8",
      "18.0.0.0/8",
      "34.0.0.0/8",
      "35.0.0.0/8"
    ]
  }
}
```

## Cloud Provider Detection

### Auto-Downloaded Ranges

The system can download official IP ranges from cloud providers:

```json
{
  "BotDetection": {
    "IpRanges": {
      "DownloadEnabled": true,
      "UpdateIntervalHours": 24,
      "Sources": [
        "https://ip-ranges.amazonaws.com/ip-ranges.json",
        "https://www.gstatic.com/ipranges/cloud.json"
      ]
    }
  }
}
```

Downloaded ranges are:

- Pre-parsed into optimized CIDR structures
- Cached with background refresh
- Fallback to static ranges if download fails

### First Octet Heuristics

Quick identification without full CIDR parsing:

| First Octet   | Provider     |
|---------------|--------------|
| 3, 13, 18, 52 | AWS          |
| 20, 40, 104   | Azure        |
| 34, 35        | Google Cloud |
| 138, 139, 140 | Oracle Cloud |

Impact: +0.3 confidence (lower than CIDR match)

## Proxy and Load Balancer Support

The detector respects proxy headers:

```
X-Forwarded-For: client-ip, proxy1, proxy2
```

The first IP in the chain is used as the client IP.

Configure trusted proxies:

```json
{
  "BotDetection": {
    "TrustedProxies": [
      "10.0.0.0/8",
      "172.16.0.0/12",
      "192.168.0.0/16"
    ]
  }
}
```

## Anonymization Detection

### Tor Exit Nodes

Requests from Tor are flagged:

- Impact: +0.5 confidence
- BotType: MaliciousBot (by default)

Tor detection can be configured:

```json
{
  "BotDetection": {
    "Tor": {
      "BlockTor": false,
      "TorExitListUrl": "https://check.torproject.org/exit-addresses",
      "UpdateIntervalHours": 1
    }
  }
}
```

### VPN/Proxy Detection

For commercial VPN detection, integrate with services like:

- MaxMind GeoIP2 Proxy Detection
- IPQualityScore
- ip-api.com

## Performance

IP detection is optimized with pre-parsed CIDR structures:

| Operation                    | Typical Time |
|------------------------------|--------------|
| X-Forwarded-For parsing      | < 0.01ms     |
| Downloaded CIDR check (trie) | < 0.1ms      |
| Static CIDR check            | < 0.1ms      |
| First octet heuristic        | < 0.01ms     |
| **Total**                    | **< 0.3ms**  |

## Integration with Behavioral Analysis

IP detection feeds into behavioral tracking:

- Per-IP rate limiting
- IP reputation tracking
- Geographic anomaly detection

```csharp
// Get IP-specific information
var isDatacenter = context.IsFromDatacenter();
var cloudProvider = context.GetCloudProvider();
```

## Datacenter vs Bot

Not all datacenter traffic is bad:

| Source                  | Typical Action      |
|-------------------------|---------------------|
| Cloud-hosted monitoring | Whitelist           |
| CI/CD systems           | Whitelist           |
| Corporate VPN egress    | Allow with tracking |
| Unknown datacenter      | Increased scrutiny  |

Whitelist known good IPs:

```json
{
  "BotDetection": {
    "WhitelistedIpRanges": [
      "203.0.113.0/24",
      "198.51.100.50/32"
    ]
  }
}
```

## Extending IP Detection

Add custom IP checks:

```csharp
public class CustomIpDetector : IDetector
{
    public string Name => "Custom IP Detector";
    public DetectorStage Stage => DetectorStage.RawSignals;

    public async Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken ct)
    {
        var result = new DetectorResult();
        var ip = context.Connection.RemoteIpAddress;

        // Check against custom reputation service
        var reputation = await _reputationService.CheckAsync(ip, ct);

        if (reputation.IsMalicious)
        {
            result.Confidence = 0.9;
            result.BotType = BotType.MaliciousBot;
            result.Reasons.Add(new DetectionReason
            {
                Category = "IP",
                Detail = $"IP flagged by reputation service: {reputation.Reason}"
            });
        }

        return result;
    }
}
```

## Accessing Results

```csharp
// Get IP detection reasons
var reasons = context.GetDetectionReasons();
var ipReasons = reasons.Where(r => r.Category == "IP");

// Example reasons:
// "IP 3.236.x.x matched cloud provider range: AWS"
// "IP is a Tor exit node"
// "IP from cloud provider (heuristic): Azure"
```

## Best Practices

1. **Keep IP ranges updated** - Cloud providers add ranges frequently
2. **Don't block all datacenter IPs** - Many legitimate services run in cloud
3. **Combine with other signals** - IP alone shouldn't determine bot status
4. **Log for forensics** - IP data is valuable for incident response
5. **Consider IPv6** - Modern networks increasingly use IPv6
