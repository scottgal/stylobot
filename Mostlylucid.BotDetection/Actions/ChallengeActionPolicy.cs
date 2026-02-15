using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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

        // Token format: base64(expiry_unix_seconds:signature)
        // Signature = HMAC-SHA256(expiry_unix_seconds, key)
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var colonIndex = decoded.IndexOf(':');
            if (colonIndex < 0) return false;

            var expiryStr = decoded[..colonIndex];
            var providedSignature = decoded[(colonIndex + 1)..];

            // Check expiry
            if (!long.TryParse(expiryStr, out var expiryUnix)) return false;
            var expiry = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
            if (DateTimeOffset.UtcNow > expiry) return false;

            // Verify HMAC signature
            var key = Encoding.UTF8.GetBytes(_options.TokenSecret ?? _options.TokenCookieName);
            var expectedSignature = Convert.ToHexString(
                HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(expiryStr)));

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedSignature),
                Encoding.UTF8.GetBytes(expectedSignature));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Generates a signed challenge token with expiry.
    /// </summary>
    internal static string GenerateChallengeToken(ChallengeActionOptions options)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(options.TokenValidityMinutes).ToUnixTimeSeconds();
        var expiryStr = expiry.ToString();
        var key = Encoding.UTF8.GetBytes(options.TokenSecret ?? options.TokenCookieName);
        var signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(expiryStr)));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{expiryStr}:{signature}"));
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
        // Generate a proof-of-work challenge
        // Client must find a nonce that when hashed with the challenge produces N leading zeros
        var challenge = Guid.NewGuid().ToString("N");
        var difficulty = CalculateDifficulty(evidence.BotProbability);

        context.Response.StatusCode = _options.ChallengeStatusCode;
        context.Response.ContentType = "text/html";

        var encodedChallengeUrl = WebUtility.HtmlEncode(_options.ChallengeUrl);
        var encodedReturnUrl = WebUtility.HtmlEncode(Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
        var html = $@"<!DOCTYPE html>
<html>
<head>
    <title>Verification Required</title>
</head>
<body>
    <div id=""pow-container"" style=""max-width: 600px; margin: 100px auto; text-align: center;"">
        <h1>Verification Required</h1>
        <p>Please wait while we verify your browser...</p>
        <progress id=""progress"" value=""0"" max=""100"" style=""width: 100%;""></progress>
        <p id=""status"">Computing proof of work...</p>
    </div>
    <script>
        (async function() {{
            const challenge = '{challenge}';
            const difficulty = {difficulty};
            const target = '0'.repeat(difficulty);

            let nonce = 0;
            let found = false;

            while (!found) {{
                const data = challenge + nonce;
                const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(data));
                const hex = Array.from(new Uint8Array(hash)).map(b => b.toString(16).padStart(2, '0')).join('');

                if (hex.startsWith(target)) {{
                    found = true;
                    document.getElementById('status').textContent = 'Verified!';

                    // Submit proof
                    const form = document.createElement('form');
                    form.method = 'POST';
                    form.action = '{encodedChallengeUrl}';
                    form.innerHTML = `
                        <input type=""hidden"" name=""challenge"" value=""${{challenge}}"" />
                        <input type=""hidden"" name=""nonce"" value=""${{nonce}}"" />
                        <input type=""hidden"" name=""returnUrl"" value=""{encodedReturnUrl}"" />
                    `;
                    document.body.appendChild(form);
                    form.submit();
                }}

                nonce++;
                if (nonce % 10000 === 0) {{
                    document.getElementById('progress').value = Math.min(99, nonce / 100000);
                    await new Promise(r => setTimeout(r, 0)); // Yield to UI
                }}
            }}
        }})();
    </script>
</body>
</html>";

        await context.Response.WriteAsync(html, cancellationToken);

        return ActionResult.Blocked(_options.ChallengeStatusCode, $"Proof-of-work challenge presented by {Name}");
    }

    private int CalculateDifficulty(double risk)
    {
        // Higher risk = higher difficulty (more zeros required)
        // Risk 0.5 = 3 zeros, Risk 1.0 = 5 zeros
        return 3 + (int)Math.Round((risk - 0.5) * 4);
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
    ///     If null, falls back to TokenCookieName (insecure - set this in production).
    /// </summary>
    public string? TokenSecret { get; set; }

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

        return new ChallengeActionPolicy(name, challengeOptions, _challengeHandler, _logger);
    }
}