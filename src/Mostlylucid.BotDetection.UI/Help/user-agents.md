---
id: user-agents
title: "User Agents"
section: useragents
commercial: false
---

# User Agents

The User Agents tab breaks down traffic by the browser or bot identity that each request claimed. A user agent is the label a browser or tool sends to identify itself -- though automated traffic routinely lies about this, which is itself a detection signal.

## What the table shows

- **User agent family** -- the grouped identity (e.g. "Chrome 124", "Googlebot", "Python-requests").
- **Requests** -- how many requests used this identity in the time window.
- **Classification breakdown** -- for each family, how many requests StyloBot classified as Human, Bot, or Threat.
- **Spoofing flag** -- marked when StyloBot determined the claimed identity was false (e.g. something claiming to be Chrome but behaving like a headless scraper).

## How user agents are classified

StyloBot does not simply trust the label. It cross-checks the claimed identity against actual behaviour -- request headers, timing patterns, JavaScript execution results, and TLS fingerprints. A request claiming to be a real iPhone but arriving with no cookie support and machine-speed timing will be flagged as spoofed.

## Common patterns

- **Real browser families** (Chrome, Firefox, Safari) with high bot counts -- spoofed agents mimicking real browsers.
- **Known bot families** (Googlebot, Bingbot) -- legitimate crawlers; their presence is expected and healthy.
- **Library names** (curl, python-requests, axios) -- raw programmatic access; not always malicious but worth monitoring.

## What to look for

A sharp increase in requests claiming to be a specific browser version from a country where that version is uncommon is a classic spoofing pattern.

**Tip:** The spoofing flag is more useful than the raw user agent string -- focus on flagged rows first.
