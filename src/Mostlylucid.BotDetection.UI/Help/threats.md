---
id: threats
title: "Threat Classifications"
section: threats
commercial: false
---

# Threat Classifications

The Threats tab lists requests that matched a known malicious pattern, organised by threat type and severity. Unlike general bot detection (which identifies automation), threat classification identifies *intent* -- what the bot was trying to do.

## Threat types

- **Credential stuffing** -- automated login attempts using lists of stolen username/password pairs.
- **Scraper** -- systematic extraction of content, prices, or data from your site.
- **Scanner** -- probing your site for vulnerabilities, exposed files, or misconfigurations.
- **Inventory monitor** -- bots repeatedly checking stock levels or pricing to gain a competitive advantage.
- **DDoS probe** -- high-volume requests designed to map your site's load limits.
- **AI harvester** -- large language model training crawlers that ignore robots.txt or crawl at abusive rates.
- **Spam bot** -- automated form submissions, comment spam, or fake account creation.

## Severity levels

- **Low** -- detected and logged; behaviour is suspicious but not immediately harmful.
- **Medium** -- active abuse detected; action may already be taken depending on your policy settings.
- **High** -- confirmed malicious behaviour; StyloBot's response depends on your configured action (log, challenge, block).

## Actions taken

The Actions column shows what StyloBot did with each threat: **Logged** (recorded, no other action), **Challenged** (presented a CAPTCHA or proof-of-work), or **Blocked** (request was refused).

## What to look for

Credential stuffing threats on your login endpoint during off-hours is one of the most common and damaging patterns. If you see it, verify your account lockout policies are active.

**Tip:** Threat classifications feed directly into the Clusters tab -- threats from the same campaign will appear grouped there.
