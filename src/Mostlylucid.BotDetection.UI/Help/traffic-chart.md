---
id: traffic-chart
title: "Traffic Over Time"
section: overview
commercial: false
---

# Traffic Over Time

The traffic chart shows how request volume has changed over the selected time window. Bars are stacked so you can see the composition of traffic -- not just how busy your site was, but what kind of visitors were responsible.

## Reading the bars

Each bar represents one time bucket (minute, hour, or day depending on your selected range). The bar is split into three layers:

- **Green** -- human visitors. Legitimate browsers, logged-in users, real people.
- **Amber** -- detected bots. Crawlers, scrapers, automated agents that StyloBot has fingerprinted.
- **Red** -- threats. Requests that matched a known malicious signature or behaviour pattern.

## Changing the time range

Use the range selector above the chart to switch between the last hour, 24 hours, 7 days, or 30 days. The bar width adjusts automatically to keep the chart readable.

## Interpreting patterns

- A spike in amber with no change in green suggests a crawl or scrape ran during that window.
- Red appearing during a green spike could indicate a credential-stuffing attempt piggybacking on real traffic.
- A flat line across all layers during business hours may mean a misconfigured detector is not passing traffic through.

**Tip:** Hover over any bar to see the exact counts for that time bucket.
