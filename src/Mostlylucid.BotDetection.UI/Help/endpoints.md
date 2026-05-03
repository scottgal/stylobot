---
id: endpoints
title: "Endpoints"
section: endpoints
commercial: false
---

# Endpoints

The Endpoints tab gives you path-level detail about where traffic is landing on your site and what proportion of it is automated. This is where you go when you need to understand which specific URLs are being targeted.

## What the table shows

- **Path** -- the URL path (e.g. `/api/search`, `/checkout`, `/login`).
- **Total requests** -- all hits to this path in the time window.
- **Bot %** -- automated share of traffic to this endpoint. The primary metric for identifying targeted abuse.
- **Human requests** -- legitimate traffic count.
- **Avg. response time** -- how long your site took to respond. Useful for spotting endpoints that bots are hammering to the point of degrading performance.
- **Threat hits** -- requests matching known malicious patterns.

## Why path-level data matters

Bots are selective. A scraper hunting for product prices will hit `/api/products` repeatedly while ignoring your blog. A credential stuffer will focus entirely on `/login`. The Endpoints tab makes these concentrations visible in a way that site-wide averages cannot.

## What to look for

- Any endpoint with a bot rate above 50% is likely being deliberately targeted.
- A high response time alongside high bot traffic on the same path is a performance warning -- bots may be causing real latency for human users.
- Endpoints that should be low-traffic (like `/sitemap.xml`) appearing near the top of the list indicate active crawling.

**Tip:** Sort by Bot % descending and then check whether the top results align with your most sensitive or expensive backend operations.
