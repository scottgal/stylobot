# Adblocker Detection

> **FOSS feature.** The signal, beacon-receive path, contributor logic and
> `<sb:adblock-probe>` TagHelper all ship in the FOSS build -- detection
> capability is never gated. The doc previously framed this as commercial; the
> code ships otherwise.

## Problem

The `ClientSideContributor` penalises document requests that arrive without a
browser fingerprint. Legitimate users with adblockers get caught: their browser
blocks the fingerprint script entirely, so they look identical to a headless bot
that skips JS execution.

The `FingerprintPopulationTracker` eventually learns that certain UA families
(Brave, Firefox with strict ETP) rarely send fingerprints and reduces the penalty
automatically, but that takes at least 20 samples. Chrome users with uBlock
Origin look like normal Chrome users throughout: Chrome's population fingerprint
rate is high, so every uBlock Origin user in that bucket gets penalised.

Pi-hole is even harder: it blocks at DNS level, transparent to the browser, with
no UA or header signal at all.

---

## Solution: Client-ID Probe TagHelper (Commercial)

A TagHelper the developer drops into their layout page. It renders a JS probe
that attempts to fetch a well-known ad-network resource identified by the
developer's own publisher client ID. If the resource is blocked the page reports
a beacon to StyloBot, which sets a blackboard signal that suppresses the
no-fingerprint penalty.

```html
<!-- Layout.cshtml -->
<sb:adblock-probe
    client-id="ca-pub-1234567890"
    provider="adsense"
    timeout-ms="2000" />
```

No other configuration required. The TagHelper generates all client-side code.

---

## Why Client-ID Rather Than a Bait Element

| Method | Browser extensions | Pi-hole | Corporate proxy | False-positive risk |
|---|---|---|---|---|
| Bait element (CSS cosmetic) | Partial | No | No | High (CSS resets) |
| Bait URL `/_sb/ads/probe.js` | Yes (if on list) | No | No | Medium |
| Real ad-network client ID | Yes | Yes | Yes | Low |

Pi-hole and enterprise DNS sinkholes block the actual ad-network hostnames. A
probe using the publisher's real client ID hits the same DNS lookups a real ad
would, so all three blocking mechanisms are caught by a single fetch.

The client ID also makes the probe self-documenting: developers already know why
the request is there, and the probe doubles as a sanity check that their ad
placement still works.

---

## Supported Providers

| Provider | `provider` attribute | Probe URL template |
|---|---|---|
| Google AdSense / AdManager | `adsense` | `https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client={clientId}` |
| Amazon Publisher Services | `amazon` | `https://c.amazon-adsystem.com/aax2/apstag.js` |
| Media.net | `medianet` | `https://contextual.media.net/dmedianet.js?cid={clientId}` |
| Custom | `custom` | Provide `probe-url` attribute directly |

Custom example (no ad network relationship required):

```html
<sb:adblock-probe
    probe-url="https://static.ads-twitter.com/uwt.js"
    timeout-ms="1500" />
```

Any URL that appears on major filter lists (EasyList, EasyPrivacy, uBlock
Origin filter hub) works. The developer provides the URL; StyloBot does not
maintain a list of ad-network hostnames.

---

## Client-Side Behaviour

The TagHelper renders a self-contained `<script>` block. No external JS file is
loaded; the probe logic is inlined so adblockers cannot block the detection code
itself.

```
Page load
    │
    ├─ fetch(probeUrl, { mode: 'no-cors' })  ──► success within timeout?
    │       │                                       │
    │    network error                           yes: no-blocker → no beacon
    │    or DNS failure                         no timeout: verdict pending
    │    or timeout
    │       │
    │       └─ navigator.sendBeacon(sbBeaconUrl, { adblocker: true })
    │
    └─ (if timeout fires first) same beacon path
```

`mode: 'no-cors'` is used so cross-origin responses do not trigger CORS errors
that would be indistinguishable from blocking. A successful `opaque` response
(any HTTP status) counts as "not blocked". Only a network error (DNS failure,
connection refused, extension interception) or a timeout counts as "blocked".

The beacon goes to the existing StyloBot fingerprint beacon endpoint, with an
additional `adblocker: true` field appended to the payload.

---

## Server-Side Signal

The beacon receiver sets:

```
SignalKeys.ClientSideAdblockerDetected = "clientside.adblocker_detected"  // bool
SignalKeys.ClientSideAdblockerProvider = "clientside.adblocker_provider"  // "adsense" | "amazon" | ...
```

`ClientSideContributor` checks `ClientSideAdblockerDetected` in the
`isNoFingerprint` path and returns early without adding a contribution:

```csharp
if (isNoFingerprint)
{
    if (state.GetSignal<bool>(SignalKeys.ClientSideAdblockerDetected))
        return contributions; // no penalty: adblocker suppressed the fingerprint script

    // existing population-rate logic ...
}
```

A small negative contribution (human-affinity bias) is also added when the
signal is present, because adblocker users are overwhelmingly human. The
magnitude is configurable via YAML (`adblocker_human_bias`, default `-0.05`).

---

## Distinguishing Pi-hole from Browser Extension

Both produce the same "fetch failed" outcome. Distinction is possible but not
planned for v1:

- **Pi-hole**: DNS failure is fast (<10 ms); the probe rejects immediately.
- **Browser extension**: The extension intercepts the request synchronously
  before the network stack; also fast but slightly different timing signature.
- **Slow network / captive portal**: Request times out at `timeout-ms`.

A future `sensitivity="paranoid"` mode could add a second probe to a different
hostname (resolving one hostname but not the other is a strong Pi-hole signal),
but the operational benefit is minimal: both cases warrant the same
no-fingerprint penalty suppression.

---

## TagHelper API

```
<sb:adblock-probe
    provider="adsense"               Provider alias (see table above).
    client-id="ca-pub-XXXXXXXXXX"    Publisher ID (provider-specific format).
    probe-url=""                     Override: exact URL to probe. Ignores provider/client-id.
    timeout-ms="2000"                How long to wait before treating as blocked.
    beacon-path="/_sb/beacon"        Override beacon endpoint path.
    />
```

All attributes except one of `(provider + client-id)` or `probe-url` are
optional. The TagHelper is a no-op when:
- Client-side detection is disabled in `BotDetectionOptions`
- The tag appears in a non-document response (TagHelper checks `IHttpContextAccessor`)
- The commercial license does not include the adblocker detection feature flag

---

## Rendered Output (illustrative)

```html
<script>(function(){
  var done=false;
  function report(){
    if(done)return; done=true;
    navigator.sendBeacon&&navigator.sendBeacon('/_sb/beacon',
      JSON.stringify({fp:window.__sb_fp_id||null,adblocker:true}));
  }
  var t=setTimeout(report,2000);
  fetch('https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=ca-pub-1234567890',
    {mode:'no-cors',cache:'no-store'})
    .then(function(){clearTimeout(t);done=true;})
    .catch(report);
})();</script>
```

The script is inlined (not a `src=` reference) so it cannot itself be blocked
by a filter list URL pattern match.

---

## Privacy and Compliance

- The probe fires a real network request to the ad network's CDN. This is
  identical to what would happen if a real ad loaded. No additional PII is
  transmitted beyond what a normal ad impression would send.
- If the developer does not have an ad network relationship they should use
  `probe-url` with a resource from a filter list that does not itself perform
  tracking (e.g., a neutral CDN asset that filter lists include).
- The beacon payload contains only the fingerprint session token (already
  present in the fingerprint beacon) plus `adblocker: true`. No IP or UA is
  added.

---

## Licensing

FOSS. Detection capability is never gated. The TagHelper renders the script when
`ClientSide.Enabled = true` AND a `IBrowserTokenService` is registered (it
isn't in dashboard-viewer hosts that don't run detection); otherwise renders
nothing.
