using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
///     Browser fingerprint data posted to <c>/bot-detection/fingerprint</c>
///     by the client-side script <c>botdetection.js</c>. The shape mirrors the
///     JS payload object built in the <c>collect()</c> function: top-level
///     scalars (token, version, timestamp, getter-trap result, interaction
///     flag) plus nested blocks for each subsystem (basics, tail, headless,
///     touch, stack, triple, ua, perms, legit, clamp, webgl).
///     <para>
///     A slim variant of this payload (adblocker-only beacon) carries just
///     <see cref="BodyToken"/> + <see cref="Adblocker"/> + <see cref="AdblockerProvider"/>;
///     the analyser recognises it by <see cref="Adblocker"/> = true and
///     <see cref="Timestamp"/> = 0 and short-circuits the integrity scoring.
///     </para>
/// </summary>
public class BrowserFingerprintData
{
    /// <summary>
    ///     Token in the body. The main fingerprint script sends its token via the
    ///     <c>X-ML-BotD-Token</c> header (it owns a normal fetch). The adblocker
    ///     probe uses <c>navigator.sendBeacon</c> which can't set headers, so the
    ///     token rides here instead. Endpoint accepts either source.
    /// </summary>
    [JsonPropertyName("t")] public string? BodyToken { get; set; }

    /// <summary>Script version emitted by <c>botdetection.js</c> (e.g. "2.0.0").</summary>
    [JsonPropertyName("v")] public string? Version { get; set; }

    /// <summary>Client-side <c>Date.now()</c> at payload assembly. Zero on adblocker-only beacons.</summary>
    [JsonPropertyName("ts")] public long Timestamp { get; set; }

    /// <summary>
    ///     Result of the init-time CDP getter trap on <c>console.debug</c>:
    ///     1 = CDP read the property descriptor while the script was loading
    ///     (Puppeteer / Playwright / Selenium / Bright Data are all visible
    ///     this way). 0 = quiet. Distinct from <see cref="BasicsBlock.CdpRuntimeProbe"/>
    ///     which fires call-time. Both being positive is near-certain CDP.
    /// </summary>
    [JsonPropertyName("cdp")] public int CdpGetterHit { get; set; }

    /// <summary>1 = the user interacted (mousemove/mousedown/touchstart/keydown) before send. 0 otherwise.</summary>
    [JsonPropertyName("interacted")] public int Interacted { get; set; }

    /// <summary>Non-null when the JS collector caught an exception before assembling the payload.</summary>
    [JsonPropertyName("error")] public string? Error { get; set; }

    // === Adblocker-only beacon variant ====================================
    // Sent by the inlined <sb:adblock-probe> script via sendBeacon when the
    // configured ad-network resource was blocked. Carries adblocker/provider/
    // token only. See docs/adblocker-detection.md.

    /// <summary>True if the adblocker probe was blocked from reaching its target.</summary>
    [JsonPropertyName("adblocker")] public bool Adblocker { get; set; }

    /// <summary>Ad-network provider alias the probe used (e.g. "adsense", "amazon").</summary>
    [JsonPropertyName("adblockerProvider")] public string? AdblockerProvider { get; set; }

    // === Top-level scalars =================================================

    /// <summary>OfflineAudioContext FFT entropy hash; null on Firefox-RFP / Brave / unsupported.</summary>
    [JsonPropertyName("audio")] public string? AudioHash { get; set; }

    /// <summary>Canvas <c>toDataURL</c> entropy hash; null when canvas disabled or unsupported.</summary>
    [JsonPropertyName("canvas")] public string? CanvasHash { get; set; }

    /// <summary>Median BroadcastChannel round-trip latency in ms; null when unsupported / timed out.</summary>
    [JsonPropertyName("bcast")] public double? BroadcastEchoMs { get; set; }

    // === Nested blocks =====================================================
    // Each may be null when the source helper returned null (UA-CH on
    // non-secure contexts, permissions API unavailable, etc.) or on the
    // adblocker-only beacon variant.

    /// <summary>Hardware + locale + window dims + preferences + network type + CDP call-time probe.</summary>
    [JsonPropertyName("basics")] public BasicsBlock? Basics { get; set; }

    /// <summary>Long-tail automation markers: webdriver / phantom / selenium / iframe.</summary>
    [JsonPropertyName("tail")] public TailBlock? Tail { get; set; }

    /// <summary>Headless markers: plugins / notification.permission / chrome.runtime / languages / connection.</summary>
    [JsonPropertyName("headless")] public HeadlessBlock? Headless { get; set; }

    /// <summary>Touch/pointer surface consistency: maxTouchPoints / ontouchstart / PointerEvent.</summary>
    [JsonPropertyName("touch")] public TouchBlock? Touch { get; set; }

    /// <summary>Error-stack foreign-frame markers (Object.apply / anonymous frames left by wrappers).</summary>
    [JsonPropertyName("stack")] public StackBlock? Stack { get; set; }

    /// <summary>Chromium-feature triple (startViewTransition / Speculation Rules / hasStorageAccess) for UA cross-check.</summary>
    [JsonPropertyName("triple")] public TripleBlock? Triple { get; set; }

    /// <summary>UA-Client Hints high-entropy block; null on non-secure contexts or older browsers.</summary>
    [JsonPropertyName("ua")] public UaBlock? Ua { get; set; }

    /// <summary>Permissions API states keyed by permission name. Real users have at least one "granted".</summary>
    [JsonPropertyName("perms")] public Dictionary<string, string?>? Permissions { get; set; }

    /// <summary>Brave / iOS Lockdown Mode classifiers. Don't penalise.</summary>
    [JsonPropertyName("legit")] public LegitBlock? Legit { get; set; }

    /// <summary>performance.now() clamp-residue probe; reveals real engine even when UA spoofed.</summary>
    [JsonPropertyName("clamp")] public ClampBlock? Clamp { get; set; }

    /// <summary>WebGL UNMASKED_VENDOR / UNMASKED_RENDERER strings; null when WebGL unavailable.</summary>
    [JsonPropertyName("webgl")] public WebglBlock? Webgl { get; set; }

    /// <summary>
    ///     WebRTC ICE-gathering probe result. Drives the mobile-UA × no-srflx
    ///     inconsistency check that catches damru's iptables UDP block, Bright
    ///     Data's restricted-egress hosting, and locked-down VMs.
    /// </summary>
    [JsonPropertyName("ice")] public IceProbeBlock? IceProbe { get; set; }

    /// <summary>
    ///     <c>speechSynthesis.getVoices()</c> count at first paint. Drives the
    ///     Android-UA × empty-voice-list inconsistency check that catches
    ///     damru's fresh-Redroid-container pattern (TTS engine hasn't booted).
    /// </summary>
    [JsonPropertyName("tts")] public TtsProbeBlock? TtsProbe { get; set; }

    /// <summary>
    ///     FingerprintJS BotD verdict (MIT-licensed, dynamic-imported by the
    ///     client when configured). Covers the long tail of automation framework
    ///     markers (Selenium, Puppeteer, Playwright, PhantomJS, CefSharp,
    ///     Awesomium, Nightmare, plus distinctive-property fingerprints) that
    ///     would otherwise be ours to maintain. Null when BotD is not enabled
    ///     or the load failed.
    /// </summary>
    [JsonPropertyName("botd")] public BotdBlock? Botd { get; set; }

    // === Legacy server-side timing probes ==================================
    // The modern botdetection.js does NOT emit these. They are reachable via
    // alternate beacon paths and via test-only construction. The analyser
    // reads them when present.

    /// <summary>DOM layout time in ms; near-zero = no rendering pipeline (headless).</summary>
    [JsonPropertyName("layoutTimeMs")] public double? LayoutTimeMs { get; set; }

    /// <summary>setTimeout drift in ms; near-zero = timer coalescing absent (headless).</summary>
    [JsonPropertyName("setTimeoutDrift")] public double? SetTimeoutDrift { get; set; }

    /// <summary>performance.now() reported resolution in ms.</summary>
    [JsonPropertyName("perfResolution")] public double? PerformanceResolution { get; set; }

    /// <summary>Client-calculated integrity score (legacy path).</summary>
    [JsonPropertyName("score")] public int ClientScore { get; set; }
}

/// <summary>
///     Hardware / locale / window dimensions / preferences / network class /
///     CDP call-time probe. Maps 1:1 to the <c>basics()</c> helper in
///     <c>botdetection.js</c>.
/// </summary>
public class BasicsBlock
{
    [JsonPropertyName("tz")] public string? Timezone { get; set; }
    [JsonPropertyName("lang")] public string? Language { get; set; }
    [JsonPropertyName("langs")] public string? Languages { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("cores")] public int HardwareConcurrency { get; set; }
    [JsonPropertyName("mem")] public double DeviceMemory { get; set; }
    [JsonPropertyName("screen")] public string? ScreenResolution { get; set; }
    [JsonPropertyName("avail")] public string? AvailableResolution { get; set; }
    [JsonPropertyName("dpr")] public double DevicePixelRatio { get; set; }
    [JsonPropertyName("outerW")] public int OuterWidth { get; set; }
    [JsonPropertyName("outerH")] public int OuterHeight { get; set; }
    [JsonPropertyName("innerW")] public int InnerWidth { get; set; }
    [JsonPropertyName("innerH")] public int InnerHeight { get; set; }
    [JsonPropertyName("dark")] public int DarkMode { get; set; }
    [JsonPropertyName("reduced")] public int ReducedMotion { get; set; }
    [JsonPropertyName("net")] public string? NetworkEffectiveType { get; set; }

    /// <summary>
    ///     <c>typeof chrome !== 'undefined'</c>. Distinguishes Chrome family
    ///     (1) from Firefox / Safari / WebKit (0). Combined with
    ///     <see cref="HeadlessBlock.HasChromeRuntime"/> = 0 and
    ///     <see cref="HeadlessBlock.PluginCount"/> = 0 it's the canonical
    ///     headless-Chrome / Puppeteer fingerprint.
    /// </summary>
    [JsonPropertyName("hasChrome")] public int HasChromeObject { get; set; }

    /// <summary>
    ///     <c>navigator.connection.type</c> (wifi/cellular/ethernet/...). Empty
    ///     when API unavailable. Drives the mobile-UA × non-mobile-link check
    ///     in <see cref="Orchestration.ContributingDetectors.InconsistencyContributor"/>.
    /// </summary>
    [JsonPropertyName("connType")] public string? ConnectionType { get; set; }

    /// <summary>
    ///     Count of <c>toString()</c> calls on a probe object passed to
    ///     <c>console.debug</c>. Non-zero = CDP Runtime.enable attached and
    ///     Chromium serialised the args for <c>Console.messageAdded</c>.
    ///     Catches Bright Data Scraping Browser, vanilla Puppeteer/Playwright,
    ///     and damru's patched Playwright during its Runtime.enable window.
    ///     <c>-1</c> sentinel = probe errored.
    /// </summary>
    [JsonPropertyName("cdpRuntime")] public int CdpRuntimeProbe { get; set; }
}

/// <summary>Long-tail automation markers from <c>longTail()</c>. All values are 0/1 ints (or -1 = errored).</summary>
public class TailBlock
{
    /// <summary>navigator.webdriver === true.</summary>
    [JsonPropertyName("webdriver")] public int WebDriver { get; set; }
    /// <summary>window.callPhantom or window._phantom present.</summary>
    [JsonPropertyName("phantom")] public int Phantom { get; set; }
    /// <summary>'webdriver' in window or '__selenium_unwrapped' in window.</summary>
    [JsonPropertyName("selenium")] public int Selenium { get; set; }
    /// <summary>Running inside an iframe.</summary>
    [JsonPropertyName("iframe")] public int Iframe { get; set; }
}

/// <summary>Headless markers from <c>headlessMarkers()</c>.</summary>
public class HeadlessBlock
{
    /// <summary>navigator.plugins.length (Chrome without plugins = highly suspicious).</summary>
    [JsonPropertyName("plugins")] public int PluginCount { get; set; }
    /// <summary>Notification.permission ("default", "granted", "denied", or "unsupported").</summary>
    [JsonPropertyName("notif")] public string? NotificationPermission { get; set; }
    /// <summary>chrome.runtime presence (1 if present, 0 if window.chrome exists without runtime — common Puppeteer tell).</summary>
    [JsonPropertyName("chromeRt")] public int HasChromeRuntime { get; set; }
    /// <summary>navigator.languages count.</summary>
    [JsonPropertyName("languages")] public int LanguageCount { get; set; }
    /// <summary>navigator.connection API present (1) or not (0).</summary>
    [JsonPropertyName("connection")] public int HasConnectionApi { get; set; }
}

/// <summary>Touch and pointer surface from <c>touchProfile()</c>.</summary>
public class TouchBlock
{
    [JsonPropertyName("maxTouch")] public int MaxTouchPoints { get; set; }
    [JsonPropertyName("ontouch")] public int HasOnTouchStart { get; set; }
    [JsonPropertyName("pointerEvt")] public int HasPointerEvent { get; set; }
}

/// <summary>Error-stack foreign-frame markers from <c>stackFrames()</c>.</summary>
public class StackBlock
{
    [JsonPropertyName("len")] public int Length { get; set; }
    [JsonPropertyName("hasObjectApply")] public int HasObjectApply { get; set; }
    [JsonPropertyName("hasAnonymous")] public int HasAnonymous { get; set; }
}

/// <summary>Chromium-feature triple from <c>chromiumTriple()</c>.</summary>
public class TripleBlock
{
    [JsonPropertyName("viewTx")] public int HasViewTransition { get; set; }
    [JsonPropertyName("speculation")] public int HasSpeculationRules { get; set; }
    [JsonPropertyName("storageAccess")] public int HasStorageAccess { get; set; }
}

/// <summary>UA-Client Hints high-entropy block from <c>uaCH()</c>.</summary>
public class UaBlock
{
    [JsonPropertyName("brands")] public List<UaBrand>? Brands { get; set; }
    [JsonPropertyName("mobile")] public int Mobile { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("arch")] public string? Architecture { get; set; }
    [JsonPropertyName("bits")] public string? Bitness { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("platformVersion")] public string? PlatformVersion { get; set; }
    [JsonPropertyName("fullVersionList")] public List<UaBrand>? FullVersionList { get; set; }
    [JsonPropertyName("formFactors")] public List<string>? FormFactors { get; set; }
}

/// <summary>UA-CH brand entry (brand + version).</summary>
public class UaBrand
{
    [JsonPropertyName("brand")] public string? Brand { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

/// <summary>Legitimate-user classifiers from <c>legitimacyMarkers()</c>. Don't penalise.</summary>
public class LegitBlock
{
    [JsonPropertyName("brave")] public int Brave { get; set; }
    [JsonPropertyName("lockdown")] public int Lockdown { get; set; }
}

/// <summary>performance.now() clamp probe from <c>clampProbe()</c>.</summary>
public class ClampBlock
{
    [JsonPropertyName("modalDeltaMs")] public double ModalDeltaMs { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

/// <summary>WebGL renderer info from <c>webglRenderer()</c>.</summary>
public class WebglBlock
{
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    [JsonPropertyName("renderer")] public string? Renderer { get; set; }
}

/// <summary>
///     WebRTC ICE-gathering probe from <c>iceProbe()</c>. Client creates an
///     <c>RTCPeerConnection</c> with a STUN server, calls <c>createOffer()</c>,
///     and waits ~2.5s while ICE candidates accumulate. <see cref="HasSrflx"/>
///     reports whether at least one server-reflexive candidate materialised
///     (the canonical "you can reach the internet via UDP" signal).
/// </summary>
public class IceProbeBlock
{
    /// <summary>Total candidate count observed during the gathering window.</summary>
    [JsonPropertyName("count")] public int CandidateCount { get; set; }

    /// <summary>1 = at least one srflx (server-reflexive) candidate observed; 0 = none.</summary>
    [JsonPropertyName("srflx")] public int HasSrflx { get; set; }

    /// <summary>1 = at least one host (local-interface) candidate observed; 0 = none.</summary>
    [JsonPropertyName("host")] public int HasHost { get; set; }

    /// <summary>1 = probe errored (RTCPeerConnection unavailable, createOffer threw). Treat as no-signal.</summary>
    [JsonPropertyName("errored")] public int Errored { get; set; }
}

/// <summary>
///     <c>speechSynthesis.getVoices()</c> probe from <c>ttsProbe()</c>. The
///     client reads the voice list at first paint. Real Android Chrome
///     populates it before the script runs; emulated Android (damru / fresh
///     Redroid) reports zero until first user gesture.
/// </summary>
public class TtsProbeBlock
{
    /// <summary>Voice count at first paint. -1 sentinel = speechSynthesis unsupported.</summary>
    [JsonPropertyName("count")] public int VoiceCount { get; set; }
}

/// <summary>
///     FingerprintJS BotD verdict from the dynamic-imported BotD bundle.
///     <see cref="Bot"/> = 1 when BotD identified the visitor as automated.
///     <see cref="Kind"/> names the framework (selenium, puppeteer, phantomjs,
///     headless_chrome, etc.) when known. <see cref="Errored"/> = 1 when the
///     load or detect call threw.
/// </summary>
public class BotdBlock
{
    [JsonPropertyName("bot")] public int Bot { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("errored")] public int Errored { get; set; }
}

/// <summary>
///     Processed browser fingerprint result with server-side analysis.
/// </summary>
public class BrowserFingerprintResult
{
    /// <summary>
    ///     Unique identifier for this fingerprint submission.
    /// </summary>
    public string RequestId { get; set; } = "";

    /// <summary>
    ///     Whether the browser appears to be automated/headless.
    /// </summary>
    public bool IsHeadless { get; set; }

    /// <summary>
    ///     Confidence score that this is a bot (0.0 to 1.0).
    /// </summary>
    public double HeadlessLikelihood { get; set; }

    /// <summary>
    ///     Browser integrity score (0-100, higher = more likely human).
    /// </summary>
    public int BrowserIntegrityScore { get; set; }

    /// <summary>
    ///     Fingerprint consistency score (0-100).
    /// </summary>
    public int FingerprintConsistencyScore { get; set; }

    /// <summary>
    ///     Detected automation framework, if any.
    /// </summary>
    public string? DetectedAutomation { get; set; }

    /// <summary>
    ///     Hash of the fingerprint for session correlation.
    /// </summary>
    public string FingerprintHash { get; set; } = "";

    /// <summary>
    ///     Detailed reasons for the scores.
    /// </summary>
    public List<string> Reasons { get; set; } = [];

    /// <summary>
    ///     Whether any JS execution timing anomaly was detected.
    /// </summary>
    public bool TimingAnomaly { get; set; }

    /// <summary>
    ///     Raw DOM layout time in ms (from client-side timing probe).
    /// </summary>
    public double? LayoutTimeMs { get; set; }

    /// <summary>
    ///     Raw setTimeout drift in ms (from client-side timing probe).
    /// </summary>
    public double? SetTimeoutDrift { get; set; }

    /// <summary>
    ///     Raw performance.now() resolution in ms (from client-side timing probe).
    /// </summary>
    public double? PerformanceResolution { get; set; }

    /// <summary>
    ///     When this fingerprint was processed.
    /// </summary>
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     True when the adblocker probe (inlined by <c>AdblockerProbeTagHelper</c>)
    ///     reported that the configured ad-network resource was blocked. Drives the
    ///     <see cref="Models.SignalKeys.ClientSideAdblockerDetected"/> blackboard
    ///     signal so <see cref="ContributingDetectors.ClientSideContributor"/> can
    ///     suppress the no-fingerprint penalty for legitimate users whose adblocker
    ///     blocked the fingerprint script along with everything else.
    /// </summary>
    public bool Adblocker { get; set; }

    /// <summary>Ad-network provider alias the probe used (e.g. "adsense", "amazon").</summary>
    public string? AdblockerProvider { get; set; }

    /// <summary>
    ///     Connection class reported by <c>navigator.connection.type</c> on the client.
    ///     Persisted on the result so <see cref="Orchestration.ContributingDetectors.ClientSideContributor"/>
    ///     can publish it as a blackboard signal on the next request from the same IP.
    /// </summary>
    public string? ConnectionType { get; set; }

    /// <summary>
    ///     True when the client-side WebRTC ICE probe completed (no error) and
    ///     observed no srflx candidate. Drives the mobile-UA × ICE inconsistency
    ///     check. Null when the probe was absent or errored.
    /// </summary>
    public bool? IceNoSrflx { get; set; }

    /// <summary>
    ///     <c>speechSynthesis.getVoices().length</c> at first paint. Null when
    ///     the probe was absent or unsupported.
    /// </summary>
    public int? TtsVoiceCount { get; set; }

    /// <summary>
    ///     BotD verdict: null when not configured or load errored, otherwise
    ///     a short kind string ("selenium", "puppeteer", "headless_chrome",
    ///     "phantomjs", "cefsharp", "awesomium", "nightmare", "fminer",
    ///     "geb", "phantomas", "rhino", "webdriverio", ...) when BotD
    ///     classified the visitor as automated.
    /// </summary>
    public string? BotdKind { get; set; }
}

/// <summary>
///     Stores fingerprint results in memory for correlation with requests.
/// </summary>
public interface IBrowserFingerprintStore
{
    /// <summary>
    ///     Stores a fingerprint result keyed by IP hash.
    /// </summary>
    void Store(string ipHash, BrowserFingerprintResult result);

    /// <summary>
    ///     Retrieves the most recent fingerprint for an IP hash.
    /// </summary>
    BrowserFingerprintResult? Get(string ipHash);
}