---
id: active-endpoints
title: "Active Endpoints"
section: overview
commercial: false
---

# Active Endpoints

The Active Endpoints panel shows which paths on your site are receiving traffic right now, ranked by request count. It is a fast way to spot which parts of your site are being targeted -- or are simply popular.

## Columns explained

- **Path** -- the URL path being requested (e.g. `/api/products`, `/login`).
- **Requests** -- total hits to that path in the current window, regardless of classification.
- **Bot %** -- the fraction of those requests that StyloBot classified as bots. High numbers here are the clearest signal of targeted abuse.
- **Humans** -- the count of genuine visitor requests to this path.

## Why this matters

Bots rarely distribute their attention evenly. They typically hammer one or two endpoints -- login forms, search APIs, product pages -- while ignoring the rest of your site. The Active Endpoints panel makes that pattern immediately visible.

Common targets:

- **/login** or **/signin** -- credential stuffing and brute force attempts.
- **/api/search** or **/api/products** -- price scraping and inventory monitoring.
- **/sitemap.xml** or **/robots.txt** -- reconnaissance by crawlers mapping your site structure.

## What to look for

Any path with a bot percentage above 30% in the current window warrants a closer look. Click the path to jump to the Endpoints tab for a detailed breakdown.

**Tip:** Login and checkout pages should almost always show low bot percentages. If they don't, review your rate-limit rules.
