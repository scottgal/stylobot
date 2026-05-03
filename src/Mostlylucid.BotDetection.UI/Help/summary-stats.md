---
id: summary-stats
title: "Summary Statistics"
section: always
commercial: false
---

# Summary Statistics

The summary statistics cards appear at the top of every dashboard section. They give you a constant reference point regardless of which tab you are currently viewing.

## The four cards

- **Total Requests** -- every request StyloBot processed in the selected time window, bots and humans combined. A useful baseline for understanding scale.

- **Bot %** -- the percentage of total requests that StyloBot classified as automated. Industry averages vary by sector; anything above 40% across your whole site is worth investigating.

- **Human %** -- the complement of Bot %. These are requests StyloBot is confident came from real people. This is the traffic that matters for your analytics, conversions, and capacity planning.

- **Unique Signatures** -- how many distinct bot signatures were observed in the time window. A high number here alongside a moderate bot percentage suggests many small, varied automated actors rather than one large campaign. A low number alongside a high bot percentage points to a single organised campaign.

## How the window works

All four numbers reflect the currently selected time range. Switching from "last hour" to "last 7 days" will change every card. The cards do not auto-select the most alarming window -- they always reflect exactly what you have chosen.

**Tip:** If Bot % looks unexpectedly high, check whether the time window includes a known crawl event (such as a fresh sitemap submission) before treating it as an incident.
