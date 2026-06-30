using System.IO.Hashing;
using System.Net;
using Mostlylucid.BotDetection.Middleware;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Action policy that presents a challenge (CAPTCHA, proof-of-work, etc.) to verify humanity.
///     Supports multiple challenge types and custom challenge page rendering.
/// </summary>
/// <remarks>
///     <para>
///         Configuration example (appsettings.json):
///         <code>
///         {
///           "BotDetection": {
///             "ActionPolicies": {
///               "captchaChallenge": {
///                 "Type": "Challenge",
///                 "ChallengeType": "Captcha",
///                 "ChallengeUrl": "/challenge",
///                 "TokenCookieName": "bot_challenge_token",
///                 "TokenValidityMinutes": 30,
///                 "ReturnUrlParam": "returnUrl"
///               },
///               "jsChallenge": {
///                 "Type": "Challenge",
///                 "ChallengeType": "JavaScript",
///                 "InlineChallenge": true,
///                 "ChallengeScript": "/scripts/bot-challenge.js"
///               }
///             }
///           }
///         }
///         </code>
///     </para>
///     <para>
///         Code configuration:
///         <code>
///         var challengePolicy = new ChallengeActionPolicy("captcha", new ChallengeActionOptions
///         {
///             ChallengeType = ChallengeType.Captcha,
///             ChallengeUrl = "/challenge",
///             TokenValidityMinutes = 30
///         });
///         actionRegistry.RegisterPolicy(challengePolicy);
///         </code>
///     </para>
///     <para>
///         Implementing custom challenge handlers:
///         <code>
///         services.AddSingleton&lt;IChallengeHandler, MyCaptchaHandler&gt;();
///         </code>
///     </para>
/// </remarks>
public class ChallengeActionPolicy : IActionPolicy
{
    private readonly IChallengeHandler? _challengeHandler;
    private readonly ILogger<ChallengeActionPolicy>? _logger;
    private readonly ChallengeActionOptions _options;

    /// <summary>
    ///     Creates a new challenge action policy with the specified options.
    /// </summary>
    public ChallengeActionPolicy(
        string name,
        ChallengeActionOptions options,
        IChallengeHandler? challengeHandler = null,
        ILogger<ChallengeActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _challengeHandler = challengeHandler;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Challenge;

    /// <inheritdoc />
    public PolicyIntent Intent => PolicyIntent.Challenge;

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        // Check if already solved challenge
        if (HasValidChallengeToken(context))
        {
            _logger?.LogDebug(
                "Request to {Path} has valid challenge token, allowing",
                context.Request.Path);

            return ActionResult.Allowed("Challenge previously completed");
        }

        _logger?.LogInformation(
            "Presenting challenge for {Path}: policy={Policy}, risk={Risk:F2}, type={ChallengeType}",
            context.Request.Path, Name, evidence.BotProbability, _options.ChallengeType);

        // Use custom handler if provided
        if (_challengeHandler != null)
            return await _challengeHandler.HandleChallengeAsync(context, evidence, _options, cancellationToken);

        // Default challenge handling
        return _options.ChallengeType switch
        {
            ChallengeType.Redirect => await HandleRedirectChallenge(context, cancellationToken),
            ChallengeType.Inline => await HandleInlineChallenge(context, evidence, cancellationToken),
            ChallengeType.JavaScript => await HandleJavaScriptChallenge(context, evidence, cancellationToken),
            ChallengeType.Captcha => await HandleCaptchaChallenge(context, cancellationToken),
            ChallengeType.ProofOfWork => await HandleProofOfWorkChallenge(context, evidence, cancellationToken),
            _ => await HandleRedirectChallenge(context, cancellationToken)
        };
    }

    private bool HasValidChallengeToken(HttpContext context)
    {
        if (!_options.UseTokens) return false;

        if (!context.Request.Cookies.TryGetValue(_options.TokenCookieName, out var token)
            || string.IsNullOrEmpty(token))
            return false;

        // Token format: base64(expiry_unix_seconds:request_binding:host_hash:signature)
        // Signature = HMAC-SHA256(expiry_unix_seconds:request_binding:host_hash, key)
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var parts = decoded.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4) return false;

            var expiryStr = parts[0];
            var providedBinding = parts[1];
            var providedHostHash = parts[2];
            var providedSignature = parts[3];

            // Check expiry
            if (!long.TryParse(expiryStr, out var expiryUnix)) return false;
            var expiry = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
            if (DateTimeOffset.UtcNow > expiry) return false;

            var currentBinding = ResolveRequestBinding(context, _options.TokenBindingMode);
            var currentHostHash = ComputeHostHash(context);
            if (!FixedTimeEqualsHex(providedBinding, currentBinding) ||
                !FixedTimeEqualsHex(providedHostHash, currentHostHash))
                return false;

            // Verify HMAC signature
            var signedPayload = $"{expiryStr}:{providedBinding}:{providedHostHash}";
            var key = Encoding.UTF8.GetBytes(_options.EffectiveTokenSecret);
            var expectedSignature = Convert.ToHexString(
                HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signedPayload)));

            return FixedTimeEqualsHex(providedSignature, expectedSignature);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Generates a signed challenge token bound to the current request context.
    /// </summary>
    internal static string GenerateChallengeToken(HttpContext context, ChallengeActionOptions options)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(options.TokenValidityMinutes).ToUnixTimeSeconds();
        var expiryStr = expiry.ToString();
        var binding = ResolveRequestBinding(context, options.TokenBindingMode);
        var hostHash = ComputeHostHash(context);
        var signedPayload = $"{expiryStr}:{binding}:{hostHash}";
        var key = Encoding.UTF8.GetBytes(options.EffectiveTokenSecret);
        var signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signedPayload)));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{signedPayload}:{signature}"));
    }

    private static string ResolveRequestBinding(HttpContext context, ChallengeTokenBindingMode mode)
    {
        if (mode == ChallengeTokenBindingMode.Stable)
        {
            // Stable mode: non-cryptographic UA hash. The HMAC over the whole
            // token (signed with EffectiveTokenSecret further down) is what
            // gives the cookie its security guarantee -- the binding hash
            // just needs to be a stable identifier for "this browser, this
            // origin". XxHash64 is ~50-100x faster than SHA256 for short
            // strings and stylobot already uses it for the same shape of
            // problem in WaveformHistoryStore + shape_hash.
            var ua = context.Request.Headers.UserAgent.ToString();
            return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(ua)).ToString("x16");
        }

        // Strict mode: bind to the broadest signature the orchestrator
        // produced (PrimarySignature -> SignatureMultifactor); fall back to
        // IP+UA hash only when detection didn't run at all.
        if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj) &&
            evObj is Orchestration.AggregatedEvidence ev)
        {
            var sig = Orchestration.SignatureLookup.PrimaryOrMultifactor(ev);
            if (sig is not null) return sig;
        }

        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var fallback = $"{remoteIp}\n{userAgent}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fallback)));
    }

    private static string ComputeHostHash(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (string.IsNullOrWhiteSpace(host))
            host = "unknown";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host.ToLowerInvariant())));
    }

    private static bool FixedTimeEqualsHex(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private Task<ActionResult> HandleRedirectChallenge(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var returnUrl = context.Request.Path + context.Request.QueryString;
        var challengeUrl = $"{_options.ChallengeUrl}?{_options.ReturnUrlParam}={Uri.EscapeDataString(returnUrl)}";

        context.Response.Redirect(challengeUrl);

        return Task.FromResult(ActionResult.Redirected(challengeUrl));
    }

    private async Task<ActionResult> HandleInlineChallenge(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = _options.ChallengeStatusCode;
        // Closed-loop feedback gate: stylobot-synthesised response.
        context.MarkResponseFromStyloBot();
        context.Response.ContentType = "text/html";

        var html = GenerateChallengeHtml(context, evidence);
        await context.Response.WriteAsync(html, cancellationToken);

        return ActionResult.Blocked(_options.ChallengeStatusCode, $"Inline challenge presented by {Name}");
    }

    private async Task<ActionResult> HandleJavaScriptChallenge(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = _options.ChallengeStatusCode;
        // Closed-loop feedback gate: stylobot-synthesised response.
        context.MarkResponseFromStyloBot();
        context.Response.ContentType = "text/html";

        var encodedScript = WebUtility.HtmlEncode(_options.ChallengeScript);
        var encodedName = WebUtility.HtmlEncode(Name);
        var returnUrl = Uri.EscapeDataString(context.Request.Path + context.Request.QueryString);
        var html = $@"<!DOCTYPE html>
<html>
<head>
    <title>Verifying your browser...</title>
    <script src=""{encodedScript}""></script>
</head>
<body>
    <div id=""challenge-container"">
        <h1>Please wait while we verify your browser...</h1>
        <noscript>
            <p>JavaScript is required to access this page.</p>
        </noscript>
    </div>
    <script>
        window.__botChallenge = {{
            policy: '{encodedName}',
            risk: {evidence.BotProbability:F3},
            returnUrl: '{returnUrl}'
        }};
    </script>
</body>
</html>";

        await context.Response.WriteAsync(html, cancellationToken);

        return ActionResult.Blocked(_options.ChallengeStatusCode, $"JavaScript challenge presented by {Name}");
    }

    private async Task<ActionResult> HandleCaptchaChallenge(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = _options.ChallengeStatusCode;
        // Closed-loop feedback gate: stylobot-synthesised response.
        context.MarkResponseFromStyloBot();
        context.Response.ContentType = "text/html";

        var returnUrl = context.Request.Path + context.Request.QueryString;
        var encodedChallengeUrl = WebUtility.HtmlEncode(_options.ChallengeUrl);
        var encodedReturnUrl = WebUtility.HtmlEncode(Uri.EscapeDataString(returnUrl));
        var encodedSiteKey = WebUtility.HtmlEncode(_options.CaptchaSiteKey);
        var html = $@"<!DOCTYPE html>
<html>
<head>
    <title>Human Verification Required</title>
    {(!string.IsNullOrEmpty(_options.CaptchaSiteKey) ? @"<script src=""https://www.google.com/recaptcha/api.js"" async defer></script>" : "")}
</head>
<body>
    <div style=""max-width: 400px; margin: 100px auto; text-align: center;"">
        <h1>Human Verification Required</h1>
        <p>Please complete the challenge below to continue.</p>
        <form method=""POST"" action=""{encodedChallengeUrl}"">
            <input type=""hidden"" name=""returnUrl"" value=""{encodedReturnUrl}"" />
            {(!string.IsNullOrEmpty(_options.CaptchaSiteKey) ? $@"<div class=""g-recaptcha"" data-sitekey=""{encodedSiteKey}""></div>" : "<p>[CAPTCHA placeholder - configure CaptchaSiteKey]</p>")}
            <br/>
            <button type=""submit"">Verify</button>
        </form>
    </div>
</body>
</html>";

        await context.Response.WriteAsync(html, cancellationToken);

        return ActionResult.Blocked(_options.ChallengeStatusCode, $"CAPTCHA challenge presented by {Name}");
    }

    private async Task<ActionResult> HandleProofOfWorkChallenge(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken)
    {
        var store = context.RequestServices.GetService<IChallengeStore>();
        if (store is null)
        {
            _logger?.LogWarning("IChallengeStore not registered; falling back to redirect challenge");
            return await HandleRedirectChallenge(context, cancellationToken);
        }

        // Bind the challenge to whatever signature the orchestrator computed (or fall back to
        // the raw IP if detection didn't run, e.g. on a bypass path).
        var signature = Orchestration.SignatureLookup.Primary(evidence)
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        // Transport-aware: API/SignalR/gRPC clients get 429 + JSON, not HTML
        var transportClass = GetSignalString(evidence, "transport.protocol_class");
        if (transportClass is "api" or "signalr" or "grpc")
            return await HandleApiChallenge(context, store, signature, evidence, cancellationToken);

        // Blackboard-driven difficulty scaling
        var (puzzleCount, requiredZeros) = CalculateDifficulty(evidence);

        var expiry = TimeSpan.FromSeconds(_options.ChallengeExpirySeconds);
        var challenge = store.CreateChallenge(signature, puzzleCount, requiredZeros, expiry);

        context.Response.StatusCode = _options.ChallengeStatusCode;
        // Closed-loop feedback gate: stylobot-synthesised response.
        context.MarkResponseFromStyloBot();
        context.Response.ContentType = "text/html";

        var returnUrl = context.Request.Path + context.Request.QueryString;
        var verifyUrl = _options.VerifyEndpoint;
        var seedsJson = System.Text.Json.JsonSerializer.Serialize(
            challenge.Puzzles.Select((p, i) => new { index = i, seed = Convert.ToBase64String(p.Seed) }));

        var html = GenerateProofOfWorkHtml(challenge.Id, seedsJson, requiredZeros, puzzleCount, verifyUrl, returnUrl);
        await context.Response.WriteAsync(html, cancellationToken);

        return ActionResult.Blocked(_options.ChallengeStatusCode, $"Proof-of-work challenge ({puzzleCount} puzzles, {requiredZeros} zeros) presented by {Name}");
    }

    private async Task<ActionResult> HandleApiChallenge(
        HttpContext context,
        IChallengeStore store,
        string signature,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken)
    {
        var (puzzleCount, requiredZeros) = CalculateDifficulty(evidence);
        var expiry = TimeSpan.FromSeconds(_options.ChallengeExpirySeconds);
        var challenge = store.CreateChallenge(signature, puzzleCount, requiredZeros, expiry);

        context.Response.StatusCode = 429;
        // Closed-loop feedback gate: stylobot-synthesised challenge response.
        context.MarkResponseFromStyloBot();
        context.Response.ContentType = "application/json";
        context.Response.Headers["Retry-After"] = _options.ChallengeExpirySeconds.ToString();

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "proof-of-work",
            challengeId = challenge.Id,
            puzzles = challenge.Puzzles.Select((p, i) => new { index = i, seed = Convert.ToBase64String(p.Seed), requiredZeros = p.RequiredZeros }),
            verifyUrl = _options.VerifyEndpoint,
            expiresAt = challenge.ExpiresAt
        });

        await context.Response.WriteAsync(payload, cancellationToken);
        return ActionResult.Blocked(429, $"API PoW challenge ({puzzleCount} puzzles) presented by {Name}");
    }

    /// <summary>
    ///     Calculates PoW difficulty using blackboard signals, not just BotProbability.
    /// </summary>
    private (int puzzleCount, int requiredZeros) CalculateDifficulty(AggregatedEvidence evidence)
    {
        var risk = evidence.BotProbability;

        // Base puzzle count: 4 at 0.5, scaling to max at 1.0
        var basePuzzles = _options.BasePuzzleCount;
        var maxPuzzles = _options.MaxPuzzleCount;
        var riskFactor = Math.Clamp((risk - 0.5) * 2, 0, 1);
        var puzzleCount = basePuzzles + (int)Math.Round(riskFactor * (maxPuzzles - basePuzzles));

        // Base zeros from risk
        var baseZeros = _options.BaseDifficultyZeros;
        var maxZeros = _options.MaxDifficultyZeros;
        var requiredZeros = baseZeros + (int)Math.Round(riskFactor * (maxZeros - baseZeros));

        // Signal-based modifiers: increase puzzle count for high-confidence bot indicators
        var velocityMag = GetSignalDouble(evidence, "session.velocity_magnitude");
        if (velocityMag > 0.5) // High behavioral shift between sessions
            puzzleCount = Math.Min(maxPuzzles, puzzleCount + 4);

        var inCluster = GetSignalString(evidence, "cluster.type");
        if (inCluster is not null) // In a bot cluster
            puzzleCount = Math.Min(maxPuzzles, puzzleCount + 8);

        var reputationBiased = GetSignalBool(evidence, "reputation.bias_applied");
        if (reputationBiased) // Known bad reputation
            puzzleCount = Math.Min(maxPuzzles, puzzleCount + 4);

        var threatScore = GetSignalDouble(evidence, "intent.threat_score");
        if (threatScore > 0.5) // High threat
            requiredZeros = Math.Min(maxZeros, requiredZeros + 1);

        return (puzzleCount, requiredZeros);
    }

    private static string? GetSignalString(AggregatedEvidence evidence, string key)
        => evidence.Signals.TryGetValue(key, out var v) && v is string s ? s : null;

    private static double GetSignalDouble(AggregatedEvidence evidence, string key)
        => evidence.Signals.TryGetValue(key, out var v) && v is double d ? d : 0;

    private static bool GetSignalBool(AggregatedEvidence evidence, string key)
        => evidence.Signals.TryGetValue(key, out var v) && v is bool b && b;

    private static string GenerateProofOfWorkHtml(
        string challengeId, string seedsJson, int requiredZeros, int puzzleCount,
        string verifyUrl, string returnUrl)
    {
        // The PoW solver lives in ClientSide/challenge.js, served at
        // /bot-detection/challenge.js. This page emits a small bootstrap
        // that publishes window.MLChallenge = {id, seeds, requiredZeros,
        // verifyUrl, returnUrl} and then loads the solver. Same
        // architecture as the fingerprint detector (botdetection.js + the
        // BotDetectionTagHelper bootstrap): the JS source on disk IS the
        // JS that runs on the wire, no string-in-C# divergence possible.
        var encodedReturnUrl = Uri.EscapeDataString(returnUrl);
        var enc = System.Text.Encodings.Web.JavaScriptEncoder.Default;
        var jsChallengeId = enc.Encode(challengeId);
        var jsVerifyUrl = enc.Encode(verifyUrl);
        var jsReturnUrl = enc.Encode(encodedReturnUrl);
        return $@"<!DOCTYPE html>
<html>
<head>
    <title>Verification Required</title>
    <style>
        body {{ font-family: -apple-system, system-ui, sans-serif; margin: 0; padding: 20px; background: #f8f9fa; color: #333; }}
        .container {{ max-width: 500px; margin: 80px auto; background: white; padding: 40px; border-radius: 12px; box-shadow: 0 2px 12px rgba(0,0,0,0.08); text-align: center; }}
        h1 {{ font-size: 1.4em; margin-bottom: 8px; }}
        .subtitle {{ color: #666; margin-bottom: 24px; }}
        progress {{ width: 100%; height: 8px; border-radius: 4px; appearance: none; }}
        progress::-webkit-progress-bar {{ background: #e9ecef; border-radius: 4px; }}
        progress::-webkit-progress-value {{ background: #228be6; border-radius: 4px; transition: width 0.3s; }}
        #status {{ color: #666; font-size: 0.9em; margin-top: 12px; }}
        .success {{ color: #2b8a3e; }}
    </style>
</head>
<body>
    <div class=""container"">
        <h1>Verification Required</h1>
        <p class=""subtitle"">Please wait while we verify your browser...</p>
        <progress id=""progress"" value=""0"" max=""{puzzleCount}""></progress>
        <p id=""status"">Solving 0 / {puzzleCount} puzzles...</p>
    </div>
    <script>
    window.MLChallenge = {{
        id: '{jsChallengeId}',
        seeds: {seedsJson},
        requiredZeros: {requiredZeros},
        verifyUrl: '{jsVerifyUrl}',
        returnUrl: '{jsReturnUrl}'
    }};
    </script>
    <script src=""/bot-detection/challenge.js"" defer></script>
</body>
</html>";
    }

    private string GenerateChallengeHtml(HttpContext context, AggregatedEvidence evidence)
    {
        var encodedTitle = WebUtility.HtmlEncode(_options.ChallengeTitle);
        var encodedMessage = WebUtility.HtmlEncode(_options.ChallengeMessage);
        var encodedChallengeUrl = WebUtility.HtmlEncode(_options.ChallengeUrl);
        var encodedReturnUrl = WebUtility.HtmlEncode(Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
        return $@"<!DOCTYPE html>
<html>
<head>
    <title>Verification Required</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 0; padding: 20px; background: #f5f5f5; }}
        .container {{ max-width: 500px; margin: 100px auto; background: white; padding: 40px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }}
        h1 {{ color: #333; }}
        p {{ color: #666; }}
        .button {{ background: #007bff; color: white; padding: 12px 24px; border: none; border-radius: 4px; cursor: pointer; }}
        .button:hover {{ background: #0056b3; }}
    </style>
</head>
<body>
    <div class=""container"">
        <h1>{encodedTitle}</h1>
        <p>{encodedMessage}</p>
        <form method=""POST"" action=""{encodedChallengeUrl}"">
            <input type=""hidden"" name=""returnUrl"" value=""{encodedReturnUrl}"" />
            <button type=""submit"" class=""button"">Continue</button>
        </form>
    </div>
</body>
</html>";
    }
}

/// <summary>
///     Types of challenges that can be presented.
/// </summary>
public enum ChallengeType
{
    /// <summary>Redirect to a challenge page</summary>
    Redirect,

    /// <summary>Render challenge inline (replace response)</summary>
    Inline,

    /// <summary>JavaScript-based challenge (browser automation detection)</summary>
    JavaScript,

    /// <summary>CAPTCHA challenge (reCAPTCHA, hCaptcha, etc.)</summary>
    Captcha,

    /// <summary>Proof-of-work challenge (computational)</summary>
    ProofOfWork
}

/// <summary>
///     How tightly the solved-cookie binds to the visitor identity.
/// </summary>
public enum ChallengeTokenBindingMode
{
    /// <summary>
    ///     Default. Hashes only the User-Agent into the cookie binding.
    ///     Real visitors keep their solved-cookie across normal page loads
    ///     even when foundation signals drift (the typical reverse-proxy /
    ///     SSH-tunnel case). Cross-origin replay is still blocked by the
    ///     cookie's host scope (HttpOnly / Secure / SameSite = Strict).
    /// </summary>
    Stable,

    /// <summary>
    ///     Binds to <see cref="SignalKeys.PrimarySignature"/>; falls back
    ///     to IP+UA hash when it is absent. Re-presents the challenge any
    ///     time the foundation-signal mix shifts. Use only when the policy
    ///     posture explicitly wants per-fingerprint-rotation re-challenge.
    /// </summary>
    Strict,
}

/// <summary>
///     Configuration options for <see cref="ChallengeActionPolicy" />.
/// </summary>
public class ChallengeActionOptions
{
    /// <summary>
    ///     Type of challenge to present.
    ///     Default: Redirect
    /// </summary>
    public ChallengeType ChallengeType { get; set; } = ChallengeType.Redirect;

    /// <summary>
    ///     URL to redirect to for challenge page.
    ///     Used for Redirect and as form action for inline challenges.
    ///     Default: "/challenge"
    /// </summary>
    public string ChallengeUrl { get; set; } = "/challenge";

    /// <summary>
    ///     HTTP status code for inline challenges.
    ///     Default: 403
    /// </summary>
    public int ChallengeStatusCode { get; set; } = 403;

    /// <summary>
    ///     Query parameter name for return URL.
    ///     Default: "returnUrl"
    /// </summary>
    public string ReturnUrlParam { get; set; } = "returnUrl";

    /// <summary>
    ///     Whether to use tokens to track completed challenges.
    ///     Default: true
    /// </summary>
    public bool UseTokens { get; set; } = true;

    /// <summary>
    ///     Cookie name for challenge token.
    ///     Default: "bot_challenge_token"
    /// </summary>
    public string TokenCookieName { get; set; } = "bot_challenge_token";

    /// <summary>
    ///     Token validity in minutes.
    ///     Default: 30
    /// </summary>
    public int TokenValidityMinutes { get; set; } = 30;

    /// <summary>
    ///     HMAC secret for signing challenge tokens.
    ///     If not set, a cryptographically random secret is auto-generated on first use.
    /// </summary>
    public string? TokenSecret { get; set; }

    /// <summary>
    ///     How tightly the solved-cookie binds to the visitor identity.
    ///     <see cref="ChallengeTokenBindingMode.Stable"/> (default) hashes
    ///     only the User-Agent, so a visitor that solved the challenge
    ///     keeps a valid cookie across normal page loads even when
    ///     foundation signals drift (the typical reverse-proxy / SSH-tunnel
    ///     case where TLS and HTTP/2 fingerprints arrive blank).
    ///     <see cref="ChallengeTokenBindingMode.Strict"/> preserves the
    ///     pre-stylobot-7.x behaviour of binding to
    ///     <see cref="SignalKeys.PrimarySignature"/>; opt-in only because
    ///     it re-presents the challenge on every signal-mix change, which
    ///     in practice means every page load behind a non-edge-terminating
    ///     proxy.
    /// </summary>
    public ChallengeTokenBindingMode TokenBindingMode { get; set; } = ChallengeTokenBindingMode.Stable;

    // Auto-generated secret (persists for process lifetime, regenerated on restart)
    private static string? _autoGeneratedSecret;
    private static readonly object _secretLock = new();

    /// <summary>
    ///     Gets the effective token secret: configured value, or auto-generated random secret.
    ///     Never falls back to a guessable value.
    /// </summary>
    public string EffectiveTokenSecret
    {
        get
        {
            if (!string.IsNullOrEmpty(TokenSecret)) return TokenSecret;

            if (_autoGeneratedSecret is null)
            {
                lock (_secretLock)
                {
                    _autoGeneratedSecret ??= Convert.ToBase64String(
                        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                }
            }
            return _autoGeneratedSecret;
        }
    }

    /// <summary>
    ///     JavaScript file URL for JavaScript challenge type.
    ///     Default: "/scripts/bot-challenge.js"
    /// </summary>
    public string ChallengeScript { get; set; } = "/scripts/bot-challenge.js";

    /// <summary>
    ///     reCAPTCHA/hCaptcha site key for Captcha challenge type.
    /// </summary>
    public string? CaptchaSiteKey { get; set; }

    /// <summary>
    ///     reCAPTCHA/hCaptcha secret key for validation.
    /// </summary>
    public string? CaptchaSecretKey { get; set; }

    /// <summary>
    ///     Title for inline challenge page.
    ///     Default: "Verification Required"
    /// </summary>
    public string ChallengeTitle { get; set; } = "Verification Required";

    /// <summary>
    ///     Message for inline challenge page.
    ///     Default: "Please verify that you are human to continue."
    /// </summary>
    public string ChallengeMessage { get; set; } = "Please verify that you are human to continue.";

    // ==========================================
    // Proof-of-Work Options
    // ==========================================

    /// <summary>
    ///     [PoW] Base number of micro-puzzles at minimum risk (0.5).
    ///     Default: 4
    /// </summary>
    public int BasePuzzleCount { get; set; } = 4;

    /// <summary>
    ///     [PoW] Maximum number of micro-puzzles at maximum risk (1.0).
    ///     Default: 32
    /// </summary>
    public int MaxPuzzleCount { get; set; } = 32;

    /// <summary>
    ///     [PoW] Base leading zeros required per puzzle at minimum risk.
    ///     Default: 3
    /// </summary>
    public int BaseDifficultyZeros { get; set; } = 3;

    /// <summary>
    ///     [PoW] Maximum leading zeros per puzzle at maximum risk.
    ///     Default: 5
    /// </summary>
    public int MaxDifficultyZeros { get; set; } = 5;

    /// <summary>
    ///     [PoW] Challenge expiry in seconds. Client must solve and submit within this window.
    ///     Default: 120
    /// </summary>
    public int ChallengeExpirySeconds { get; set; } = 120;

    /// <summary>
    ///     [PoW] Endpoint URL for challenge verification POST.
    ///     Default: "/bot-detection/challenge/verify"
    /// </summary>
    public string VerifyEndpoint { get; set; } = "/bot-detection/challenge/verify";
}

/// <summary>
///     Interface for custom challenge handlers.
///     Implement this to provide custom challenge logic.
/// </summary>
public interface IChallengeHandler
{
    /// <summary>
    ///     Handle the challenge for the given request.
    /// </summary>
    Task<ActionResult> HandleChallengeAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        ChallengeActionOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Factory for creating <see cref="ChallengeActionPolicy" /> from configuration.
/// </summary>
public class ChallengeActionPolicyFactory : IActionPolicyFactory
{
    private readonly IChallengeHandler? _challengeHandler;
    private readonly ILogger<ChallengeActionPolicy>? _logger;

    public ChallengeActionPolicyFactory(
        IChallengeHandler? challengeHandler = null,
        ILogger<ChallengeActionPolicy>? logger = null)
    {
        _challengeHandler = challengeHandler;
        _logger = logger;
    }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Challenge;

    /// <inheritdoc />
    public IActionPolicy Create(string name, IDictionary<string, object> options)
    {
        var challengeOptions = new ChallengeActionOptions();

        if (options.TryGetValue("ChallengeType", out var challengeType))
            if (Enum.TryParse<ChallengeType>(challengeType?.ToString(), true, out var ct))
                challengeOptions.ChallengeType = ct;

        if (options.TryGetValue("ChallengeUrl", out var url))
            challengeOptions.ChallengeUrl = url?.ToString() ?? challengeOptions.ChallengeUrl;

        if (options.TryGetValue("ChallengeStatusCode", out var statusCode))
            challengeOptions.ChallengeStatusCode = Convert.ToInt32(statusCode);

        if (options.TryGetValue("ReturnUrlParam", out var returnParam))
            challengeOptions.ReturnUrlParam = returnParam?.ToString() ?? challengeOptions.ReturnUrlParam;

        if (options.TryGetValue("UseTokens", out var useTokens))
            challengeOptions.UseTokens = Convert.ToBoolean(useTokens);

        if (options.TryGetValue("TokenCookieName", out var cookieName))
            challengeOptions.TokenCookieName = cookieName?.ToString() ?? challengeOptions.TokenCookieName;

        if (options.TryGetValue("TokenValidityMinutes", out var validity))
            challengeOptions.TokenValidityMinutes = Convert.ToInt32(validity);

        if (options.TryGetValue("ChallengeScript", out var script))
            challengeOptions.ChallengeScript = script?.ToString() ?? challengeOptions.ChallengeScript;

        if (options.TryGetValue("CaptchaSiteKey", out var siteKey))
            challengeOptions.CaptchaSiteKey = siteKey?.ToString();

        if (options.TryGetValue("CaptchaSecretKey", out var secretKey))
            challengeOptions.CaptchaSecretKey = secretKey?.ToString();

        if (options.TryGetValue("ChallengeTitle", out var title))
            challengeOptions.ChallengeTitle = title?.ToString() ?? challengeOptions.ChallengeTitle;

        if (options.TryGetValue("ChallengeMessage", out var message))
            challengeOptions.ChallengeMessage = message?.ToString() ?? challengeOptions.ChallengeMessage;

        // PoW options
        if (options.TryGetValue("BasePuzzleCount", out var bpc))
            challengeOptions.BasePuzzleCount = Convert.ToInt32(bpc);
        if (options.TryGetValue("MaxPuzzleCount", out var mpc))
            challengeOptions.MaxPuzzleCount = Convert.ToInt32(mpc);
        if (options.TryGetValue("BaseDifficultyZeros", out var bdz))
            challengeOptions.BaseDifficultyZeros = Convert.ToInt32(bdz);
        if (options.TryGetValue("MaxDifficultyZeros", out var mdz))
            challengeOptions.MaxDifficultyZeros = Convert.ToInt32(mdz);
        if (options.TryGetValue("ChallengeExpirySeconds", out var ces))
            challengeOptions.ChallengeExpirySeconds = Convert.ToInt32(ces);
        if (options.TryGetValue("VerifyEndpoint", out var ve))
            challengeOptions.VerifyEndpoint = ve?.ToString() ?? challengeOptions.VerifyEndpoint;

        return new ChallengeActionPolicy(name, challengeOptions, _challengeHandler, _logger);
    }
}
