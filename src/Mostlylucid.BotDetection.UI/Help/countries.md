---
id: countries
title: "Countries"
section: countries
commercial: false
---

# Countries

The Countries tab breaks down your traffic by the geographic origin of each request. It helps you understand whether bot activity is concentrated in specific regions and whether your legitimate audience is where you expect it to be.

## What the table shows

- **Country** -- derived from the IP address of each request.
- **Total requests** -- all requests from that country in the time window.
- **Human requests** -- the subset StyloBot classified as real visitors.
- **Bot requests** -- automated traffic from that country.
- **Bot rate** -- bots as a percentage of all traffic from that country. This is the most useful column for spotting problematic origins.
- **Threat requests** -- requests matching known malicious signatures from that country.

## Reading bot rate by country

A high bot rate from a country that is also a major source of legitimate traffic (a key market) is different from the same rate in a country where you have few real customers. Context matters:

- High bot rate, low overall volume -- probably just a small number of bots, not an emergency.
- High bot rate, high overall volume -- worth investigating; this may be a coordinated campaign.
- A country you have never marketed to suddenly appearing in the top 10 -- often a hosting region used by bot operators.

## What to look for

Watch for countries you would not expect that show up with high threat counts. Hosting-heavy regions (data centre hubs) often appear here regardless of your actual customer base.

**Tip:** Geographic blocks are a blunt tool. Use country data to understand patterns, not to make blocking decisions in isolation.
