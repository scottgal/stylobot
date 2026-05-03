---
id: visitors
title: "Visitors"
section: visitors
commercial: false
---

# Visitors

The Visitors tab presents a card-per-IP view of everyone who has interacted with your site in the selected time window. Each card summarises what StyloBot observed about that visitor across all of their requests.

## What each card shows

- **IP address** -- the source of the requests. Clicking it opens a detailed view of every request from that address.
- **Classification badge** -- Human, Bot, or a named threat type.
- **Risk band** -- a colour-coded label: Green (low), Amber (moderate), Red (high). This is a friendlier summary of the underlying confidence score.
- **Request count** -- how many requests this IP made in the window.
- **First seen / Last seen** -- the time range of activity, useful for identifying brief burst attacks versus persistent crawlers.
- **Signature** -- the fingerprint associated with this visitor.

## Risk bands explained

- **Green** -- StyloBot is confident this is a legitimate visitor or a harmless known crawler.
- **Amber** -- some automated signals were detected but nothing conclusive. Worth monitoring.
- **Red** -- high confidence of malicious or abusive automated activity.

## Filtering and sorting

Use the filter bar to show only Bots, only Threats, or to search by IP. Sort by request count to find the most active actors, or sort by risk to surface the most dangerous ones.

**Tip:** An IP that has been classified as Human on most visits but occasionally shows Bot signals may be a shared NAT address -- multiple people and automated tools sharing the same IP.
