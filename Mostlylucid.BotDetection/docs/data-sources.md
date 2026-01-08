# Data Sources

The package automatically fetches and updates bot detection data from authoritative sources.

## Default Sources

| Source                 | Description                                | Default   |
|------------------------|--------------------------------------------|-----------|
| **IsBot**              | Comprehensive bot regex patterns (primary) | Enabled   |
| Matomo Device Detector | Categorized bot patterns with metadata     | Disabled* |
| Crawler User Agents    | Community-maintained crawler list          | Disabled* |
| AWS IP Ranges          | Official Amazon IP ranges                  | Enabled   |
| Google Cloud IP Ranges | Official GCP IP ranges                     | Enabled   |
| Azure IP Ranges        | Changes weekly, requires manual update     | Disabled  |
| Cloudflare IP Ranges   | IPv4 and IPv6 ranges                       | Enabled   |

\* *IsBot already incorporates patterns from Matomo, crawler-user-agents, myip.ms, and more. Enable these only if you
need their specific metadata.*

## Configuration

```json
{
  "BotDetection": {
    "DataSources": {
      "IsBot": {
        "Enabled": true,
        "Url": "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json"
      },
      "Matomo": {
        "Enabled": false,
        "Url": "https://raw.githubusercontent.com/matomo-org/device-detector/master/regexes/bots.yml"
      },
      "CrawlerUserAgents": {
        "Enabled": false,
        "Url": "https://raw.githubusercontent.com/monperrus/crawler-user-agents/master/crawler-user-agents.json"
      },
      "AwsIpRanges": {
        "Enabled": true,
        "Url": "https://ip-ranges.amazonaws.com/ip-ranges.json"
      },
      "GcpIpRanges": {
        "Enabled": true,
        "Url": "https://www.gstatic.com/ipranges/cloud.json"
      },
      "AzureIpRanges": {
        "Enabled": false,
        "Url": ""
      },
      "CloudflareIpv4": {
        "Enabled": true,
        "Url": "https://www.cloudflare.com/ips-v4"
      },
      "CloudflareIpv6": {
        "Enabled": true,
        "Url": "https://www.cloudflare.com/ips-v6"
      }
    }
  }
}
```

## Background Updates

```json
{
  "BotDetection": {
    "EnableBackgroundUpdates": true,
    "UpdateIntervalHours": 24,
    "UpdateCheckIntervalMinutes": 60,
    "StartupDelaySeconds": 5,
    "ListDownloadTimeoutSeconds": 30,
    "MaxDownloadRetries": 3
  }
}
```

## Fail-Safe Behavior

The update service never crashes your application:

- **Startup failures**: Detection continues with embedded lists
- **Download failures**: Uses exponential backoff and retries
- **Timeout handling**: Long downloads are cancelled gracefully
- **Fallback**: If all sources fail, embedded static lists are used

## Custom Sources

You can point to your own URLs for any data source. Format requirements:

- **IsBot-style**: JSON array of regex patterns
- **Matomo-style**: YAML with bot definitions
- **IP Ranges**: JSON with CIDR prefixes

## External Resources

- [isbot](https://github.com/omrilotan/isbot) - Primary bot pattern source
- [Matomo Device Detector](https://github.com/matomo-org/device-detector) - Bot patterns with metadata
- [crawler-user-agents](https://github.com/monperrus/crawler-user-agents) - Community crawler list
- [AWS IP Ranges](https://docs.aws.amazon.com/general/latest/gr/aws-ip-ranges.html) - Official Amazon ranges
- [GCP IP Ranges](https://cloud.google.com/compute/docs/faq#find_ip_range) - Official Google ranges
- [Cloudflare IP Ranges](https://www.cloudflare.com/ips/) - Official Cloudflare ranges
