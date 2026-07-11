# Browser-Characteristic Consistency

**Opt-in. Requires `Identity:Enabled = true` and `Identity:BrowserChar:Enabled = true`.**

Browser-characteristic consistency is **claim verification, not fingerprinting**. Given what a request *claims* to be (its User-Agent and UA Client Hints), it asks whether the *observed* client-side behaviour is consistent with that claim. A spoofer can put any string in the User-Agent, but it is much harder to fake the actual JavaScript engine underneath. When the claim and the engine disagree, that inconsistency is the tell.

It is privacy-preserving by design: it does not build a tracking identifier from the observations. The observations feed a consistency score against a learned per-browser-family shape, and only *inconsistency* is used as a signal.

## What the client collects (botdetection.js v2.1.0)

The client script gathers three classes of observation and sends them in the fingerprint beacon. They differ in how spoofable they are, which is what makes the cross-check work.

**Version-gated feature presence (spoofable).** A small vector of capability probes that a given browser version either has or does not (`1` present, `0` absent, `-1` errored): the Popover API, CSS `:has()`, `Array.prototype.findLast`, `structuredClone`, and WebGPU (`navigator.gpu`). A spoofer *can* fake these, so they are weighted low.

**Engine tells (hard to spoof).** Characteristics that betray the real JavaScript engine (V8 / SpiderMonkey / JavaScriptCore) regardless of the claimed User-Agent: `Intl.v8BreakIterator` and `Error.captureStackTrace` (V8-only), the `Error().stack` format family, `RegExp` lookbehind support (un-polyfillable), `showOpenFilePicker` (Chromium-only), and `navigator.userAgentData` presence. These are weighted high.

**Chromium triple (UA cross-check).** Three Chromium-only features (`document.startViewTransition`, speculation-rules support probed via `HTMLScriptElement.supports()`, `document.hasStorageAccess`) used to catch a UA that claims Chromium but lacks the engine to back it up.

The feature-to-version matrix and the consistency verdict live **server-side only**; the client sends raw observations, never a verdict.

## The signed beacon (provenance, not truthfulness)

The beacon is bound to a single-use, IP-bound browser token so that an off-browser client (a `curl` farm) cannot POST a canned "I'm a human" payload with a captured token.

- The client (bootstrapped via `window.StyloBot`) signs the raw JSON body with an HMAC keyed by the token, `base64(HMAC-SHA256(key = token, message = raw body))`, and sends it in the signature header (`X-SB-Client-Sig` by default) alongside the token header (`X-SB-Client-Token`). Both header names are configurable for white-labelling via `ClientSide:SignatureHeader` and `ClientSide:TokenHeader`.
- The server (`BrowserFingerprintEndpoint`) recomputes the HMAC over the raw body with the token as the key and rejects a mismatch.

This proves the beacon came, intact, from the browser that holds the token. It does **not** prove the values are true: the client still controls what it reports. The truth verdict comes from the consistency check, not from trusting the reported values. This is the central rule of the client-attested tier (see [Trust model](#trust-model-client-attested-tier)).

The optional FingerprintJS **BotD** integration (`Botd:Enabled`, off by default, operator brings its own bundle) is a **separate, unrelated** probe. The StyloBot beacon does not depend on it; the `SB` / `StyloBot` naming above is ours and has nothing to do with that library.

## How it scores: `BrowserCharConsistencyAtom`

`BrowserCharConsistencyAtom` (Priority 19, category `Inconsistency`) runs after `ClientSideAtom` and is a **classifier, not a foundation atom**: it depends on a prior beacon round-trip, so it never runs in the Wave 0 band.

1. It reads the parsed `BrowserFingerprintResult` for the request. If there is no engine data (no beacon, or the engine block is empty), it **fails open**: no signal.
2. It maps the claimed UA family (chrome / firefox / safari / edge) to a learned centroid keyed `{family}:normal`.
3. `BrowserCharVectorEncoder` encodes the observations into the identity vector's client-consistency dimensions, with engine dimensions weighted high and feature dimensions weighted low.
4. `BrowserCharConsistencyScorer` scores the encoded observation against the family centroid with a weighted cosine, producing a `Drift` in `[0, 1]` (0 = perfectly consistent, 1 = opposite).
5. If the claimed family has no seeded centroid, it **fails open** (unknown family, no penalty).

**The emission is asymmetric.** Consistency is neutral: if `Drift <= DriftThreshold`, the atom emits nothing. Only when `Drift > DriftThreshold` does it raise suspicion, via the signal `browser.characteristic_drift:{drift}` (the drift value is appended to the key, e.g. `browser.characteristic_drift:0.845`) and a positive confidence contribution that scales with how far past the threshold the drift sits. A client can never *lower* its own bot probability toward "human" on the strength of self-reported values; it can only raise suspicion by being internally inconsistent.

## Identity vector layout v4 and the one-time re-learn

The consistency observations are stored as new `client.*` dimensions in **identity vector layout v4** (feature dims like `client.feat.popover`, chromium-triple dims like `client.triple.view_tx`, and engine dims like `client.eng.v8_break_iterator` / `client.eng.stack_v8`). Engine dims carry the high weight.

**Upgrade note.** When StyloBot starts with a `fingerprints.db` written at an older layout version, the identity store detects the mismatch and **wipes and re-seeds `fingerprints.db` at the new layout**. The per-fingerprint identity centroids then re-learn from live traffic, and the fixed consistency baselines re-seed from their built-in prior. This is deliberate (the identity data is re-learnable, and a partial-layout vector cannot be scored safely), but it means the first deployment of a new layout has a one-time identity warm-up window. It does not affect detection on the fast path, session data, or the main `botdetection.db`.

## Trust model (client-attested tier)

Client-attested signals are a distinct **low-trust tier** on the blackboard, governed by `docs/architecture/signal-contracts.md` Rule 5:

- **One adaptor, one writer.** A single component admits client-attested signals under the `clientattested.*` namespace. A client can never write high-trust keys (`reputation.*`, `signature.*`, `verifiedbot.*`).
- **Whitelist boundary.** Only an explicit allow-list of client-attested keys is admitted. The whitelist is the security boundary, not the signature.
- **Asymmetric weighting.** Client-attested values may only raise suspicion via inconsistency; they can never pull a verdict toward "human". Detection value comes from the consistency check plus the whitelist, never from trusting a raw reported value.

## Configuration

```json
{
  "BotDetection": {
    "Identity": {
      "Enabled": true,
      "BrowserChar": {
        "Enabled": true,
        "DriftThreshold": 0.005
      }
    }
  }
}
```

- `Identity:Enabled` and `Identity:BrowserChar:Enabled` both default off; both must be true for the atom to run.
- `DriftThreshold` (default `0.005`) is the consistency boundary. Below it, the request is treated as consistent and no signal is raised. The atom also exposes weighting knobs so engine dimensions can be emphasised over the more-spoofable feature dimensions.

## Maturity

The scoring path is live behind the opt-in flags, but the per-family baselines are a **fixed seed prior, not live learning yet**. The `{family}:normal` centroids are seeded from a built-in stand-in and do **not** currently adapt to your traffic, so a request is scored against StyloBot's shipped notion of "normal Chrome / Firefox / Safari / Edge", not against your own population. Moving the seed source to editable YAML, and letting the baselines learn, are follow-ups. Treat this as an early-access signal: enable it in observe/calibration first, and confirm the drift distribution on your own traffic before letting it influence enforcement.

## See also

- [`client-integration.md`](client-integration.md) - wiring the client script and the fingerprint beacon endpoint.
- [`identity-fingerprint-match.md`](identity-fingerprint-match.md) - the metastable identity layer these dimensions extend.
- [`docs/architecture/signal-contracts.md`](../../../docs/architecture/signal-contracts.md) - Rule 5, the client-attested tier contract.
