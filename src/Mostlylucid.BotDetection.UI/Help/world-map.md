---
id: world-map
title: "Global Threat Map"
section: overview
commercial: false
---

# Global Threat Map

The Global Threat Map plots the geographic origin of requests on a world map. Each marker or heat zone represents traffic originating from that country or region during the current time window.

## What the colours mean

- **Blue** -- human traffic. Normal visitor distribution for your site.
- **Amber** -- bot traffic. Automated requests that have been fingerprinted but are not necessarily malicious.
- **Red** -- threat traffic. Requests matching known malicious signatures, scrapers, or attack tools.

Darker or larger markers mean higher volumes from that location.

## How location is determined

StyloBot uses the IP address of each request to look up an approximate country. This is accurate to country level in the vast majority of cases. It does not identify cities or individuals.

## What to look for

- A country you do not expect to have customers showing up in red is a common indicator of a coordinated attack originating from that region.
- A sudden shift in your normal blue distribution -- fewer markers in your usual markets -- can sometimes indicate bot traffic is crowding out real users.
- A single country dominating amber traffic may point to a hosting provider or VPN service that bots favour.

**Tip:** Geographic patterns alone are not a reason to block traffic. Use the Countries tab for a deeper breakdown before making any decisions.
