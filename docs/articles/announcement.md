StyloBot is now RTM.

It is a lightweight, fast, behaviour-based security system for blocking bots, reducing fraud, and protecting APIs in real deployments.

If you’ve been reading this blog for a while, you’ll know I’ve spent a lot of time working on a class of systems I call **Behavioural Inference**.

I’ve built several customer projects using this approach, and it has proven effective at solving awkward real-world problems in ways most standard systems do not.

What stood out to me was the range of problems it applies to.

Everything from improving OCR by building adaptive deterministic templates based on document behaviour, through to advanced Audience Intelligence systems that infer from behaviour rather than identity, to ***lucid*SUPPORT** (a future ***mostly*lucid** product) which learns how users interact with software and can provide predictive, targeted help - even using in-browser LLMs to avoid PII leakage and provide context-specific support.

I’ve also released a number of public projects built on the same ideas. Things like **lucidRAG**, **DocSummarizer**, **DoomSummarizer**, and several other summarisation tools.

The common thread is a signals-based approach: extracting what actually matters from text, documents, video and other inputs, then reducing that into a smaller structured surface that still preserves meaning and detail.

That surface can then be used in two useful ways:

* deterministic systems that give intelligent responses without needing an LLM at all
* feeding a small local model with pre-resolved context so it can perform far beyond its size

But the project that pushed this thinking furthest is the one now solving real customer problems and operating as a product in its own right.

# StyloBot - Behavioral Security Platform

## What does that mean?

Fair question. StyloBot does not fit neatly into the usual boxes.

At its core, StyloBot protects websites, APIs and online systems from fraud, abuse and automated attacks by analysing behaviour rather than relying on simple rules or static signatures.

Instead of only asking who a caller claims to be, StyloBot looks at how they behave.

It examines patterns such as request flow, timing, network traits, protocol signals, navigation behaviour, and changes over time to identify activity that does not look genuine.

It then tracks these signals over time - currently across 119 dimensions - to understand how behaviour changes, drifts or suddenly becomes suspicious.

Unlike most systems in this space, it is not built around blocklists, IP reputation feeds, or user-agent strings.

It doesn’t need constant signature updates. It is resilient to new attack variants. And it is designed to minimise false positives against legitimate users.

Nothing it collects needs to identify a specific person. Even values such as IP addresses and user agents can be stored as HMAC-derived representations rather than raw data.

Unless you want identity-aware policies. For example, authenticated users can have different trust policies applied while their behavioural patterns are still monitored in case credentials are stolen or abused.

That allows StyloBot to detect and respond to things like:

* bots and scrapers
* credential misuse
* stolen API keys
* account abuse
* suspicious automation
* anomalous traffic patterns

StyloBot combines deterministic detection, behavioural inference, and adaptive scoring to make trust decisions in real time.

In short: **security based on behaviour, not just identity.**
