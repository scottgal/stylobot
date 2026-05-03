---
id: recent-activity
title: "Recent Activity"
section: overview
commercial: false
---

# Recent Activity

The Recent Activity feed shows the last several hundred requests processed by StyloBot, updating in real time. It is the closest thing to watching traffic flow through your site as it happens.

## Columns explained

- **Time** -- when the request arrived, shown as a relative age (e.g. "3s ago") or an exact timestamp on hover.
- **IP** -- the source address. Clicking it filters the feed to that address only.
- **Path** -- the URL path that was requested.
- **Classification** -- whether StyloBot judged the visitor to be **Human**, **Bot**, or a specific **Threat** type.
- **Signature** -- the bot family or browser fingerprint that matched. Humans show their browser family here.
- **Score** -- a 0-100 confidence score. Higher means StyloBot is more certain of its classification.

## Filtering the feed

Use the filter bar above the table to narrow results:

- **Bots only** -- hides all human traffic so you can focus on what's being detected.
- **Threats only** -- shows only requests that matched a known malicious pattern.
- **By IP or path** -- type into the search box to filter live.

## What to look for

A sudden wave of bot rows from a single IP or with the same signature usually means a targeted crawl or scrape attempt. If a signature you don't recognise keeps appearing, check the Threats tab for more detail.
