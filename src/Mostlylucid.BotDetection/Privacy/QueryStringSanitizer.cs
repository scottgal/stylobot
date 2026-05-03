using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.Privacy;

/// <summary>
///     Detects and sanitizes PII in query strings.
///     Designed for the hot path - uses HashSet lookups and avoids regex where possible.
/// </summary>
public static partial class QueryStringSanitizer
{
    /// <summary>PII-sensitive parameter names (case-insensitive matching).</summary>
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Auth/credentials
        "password", "passwd", "pwd", "pass", "secret", "credential",
        // Tokens
        "token", "access_token", "refresh_token", "api_key", "apikey", "api-key",
        "auth", "authorization", "bearer", "session", "sessionid", "session_id",
        "jwt", "csrf", "csrftoken", "csrf_token", "nonce",
        // Personal info
        "email", "mail", "e-mail", "username", "user_name", "login",
        "phone", "tel", "mobile", "ssn", "social_security",
        "name", "firstname", "first_name", "lastname", "last_name",
        "address", "street", "city", "zip", "zipcode", "postal",
        // Financial
        "credit_card", "creditcard", "card_number", "cvv", "cvc",
        "account", "account_number", "routing", "iban", "swift",
        // Keys/secrets
        "key", "private_key", "public_key", "encryption_key",
        "client_secret", "client_id", "app_secret", "app_key",
        "aws_access_key", "aws_secret", "azure_key"
    };

    /// <summary>Keys that commonly carry values but are not PII themselves.</summary>
    private static readonly HashSet<string> SafeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "limit", "offset", "sort", "order", "dir", "direction",
        "q", "query", "search", "filter", "fields", "include", "exclude",
        "format", "type", "category", "tag", "id", "slug", "lang",
        "callback", "v", "version", "ref", "utm_source", "utm_medium",
        "utm_campaign", "utm_term", "utm_content", "per_page", "page_size",
        "action", "rest_route", "_fields"
    };

    private const int LongTokenMinLength = 32;

    // Pre-compiled regexes via source generators for zero-alloc on hot path
    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^[A-Za-z0-9+/=_\-]{32,}$", RegexOptions.Compiled)]
    private static partial Regex LongTokenRegex();

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_\-]+\.eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+$", RegexOptions.Compiled)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b\d{3}[-.\s]?\d{2}[-.\s]?\d{4}\b")]
    private static partial Regex SsnRegex();

    [GeneratedRegex(@"\b\d{4}[-.\s]?\d{4}[-.\s]?\d{4}[-.\s]?\d{4}\b")]
    private static partial Regex CreditCardRegex();

    [GeneratedRegex(@"\b\+?\d[\d\s\-().]{8,}\d\b")]
    private static partial Regex PhoneRegex();

    /// <summary>
    ///     Sanitize a query string by redacting values of sensitive parameters.
    ///     Safe/known parameters are preserved. Unknown parameters with PII-looking
    ///     values are redacted. Returns the sanitized query string (with leading ?),
    ///     or empty string if the input is null/empty.
    /// </summary>
    public static string Sanitize(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
            return string.Empty;

        // Strip leading ? if present
        var qs = queryString.AsSpan();
        if (qs.Length > 0 && qs[0] == '?')
            qs = qs[1..];

        if (qs.IsEmpty)
            return string.Empty;

        // Parse and rebuild
        var parts = queryString.StartsWith('?')
            ? queryString[1..].Split('&')
            : queryString.Split('&');

        var result = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var eqIdx = parts[i].IndexOf('=');
            if (eqIdx < 0)
            {
                // No value, keep as-is
                result[i] = parts[i];
                continue;
            }

            var paramKey = parts[i][..eqIdx];
            var paramValue = parts[i][(eqIdx + 1)..];

            if (SensitiveKeys.Contains(paramKey))
            {
                result[i] = $"{paramKey}=[REDACTED]";
            }
            else if (SafeKeys.Contains(paramKey))
            {
                result[i] = parts[i]; // keep as-is
            }
            else
            {
                // Unknown key: check value patterns
                result[i] = $"{paramKey}={RedactValueIfNeeded(paramValue)}";
            }
        }

        return "?" + string.Join("&", result);
    }

    /// <summary>
    ///     Detect PII patterns in a query string. Returns a result describing what was found.
    /// </summary>
    public static PiiDetectionResult DetectPii(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
            return PiiDetectionResult.Empty;

        var qs = queryString.StartsWith('?') ? queryString[1..] : queryString;
        if (string.IsNullOrEmpty(qs))
            return PiiDetectionResult.Empty;

        var detectedTypes = new List<string>();
        var parts = qs.Split('&');

        foreach (var part in parts)
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx < 0) continue;

            var paramKey = part[..eqIdx];
            var paramValue = part[(eqIdx + 1)..];

            // Check key name
            if (SensitiveKeys.Contains(paramKey))
            {
                var category = CategorizeSensitiveKey(paramKey);
                if (!detectedTypes.Contains(category))
                    detectedTypes.Add(category);
                continue;
            }

            // Check value patterns
            if (string.IsNullOrEmpty(paramValue)) continue;

            var decoded = Uri.UnescapeDataString(paramValue);

            if (EmailRegex().IsMatch(decoded) && !detectedTypes.Contains("email"))
                detectedTypes.Add("email");
            else if (JwtRegex().IsMatch(decoded) && !detectedTypes.Contains("jwt"))
                detectedTypes.Add("jwt");
            else if (SsnRegex().IsMatch(decoded) && !detectedTypes.Contains("ssn"))
                detectedTypes.Add("ssn");
            else if (CreditCardRegex().IsMatch(decoded) && !detectedTypes.Contains("credit_card"))
                detectedTypes.Add("credit_card");
            else if (PhoneRegex().IsMatch(decoded) && !detectedTypes.Contains("phone"))
                detectedTypes.Add("phone");
            else if (decoded.Length >= LongTokenMinLength && LongTokenRegex().IsMatch(decoded)
                     && !detectedTypes.Contains("token"))
                detectedTypes.Add("token");
        }

        return new PiiDetectionResult
        {
            HasPii = detectedTypes.Count > 0,
            DetectedTypes = detectedTypes
        };
    }

    /// <summary>
    ///     Check if a value looks like it contains PII (email pattern, long token, etc.)
    ///     Conservative: only flags clear PII patterns, not normal query values.
    /// </summary>
    public static bool ValueLooksSensitive(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        // Email pattern (contains @ and .)
        if (value.Contains('@') && EmailRegex().IsMatch(value))
            return true;

        // JWT pattern
        if (value.StartsWith("eyJ", StringComparison.Ordinal) && JwtRegex().IsMatch(value))
            return true;

        // Long token (base64/hex, 32+ chars)
        if (value.Length >= LongTokenMinLength && LongTokenRegex().IsMatch(value))
            return true;

        // SSN pattern
        if (SsnRegex().IsMatch(value))
            return true;

        // Credit card pattern
        if (CreditCardRegex().IsMatch(value))
            return true;

        return false;
    }

    private static string RedactValueIfNeeded(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var decoded = Uri.UnescapeDataString(value);

        if (EmailRegex().IsMatch(decoded))
            return "[REDACTED-EMAIL]";

        if (JwtRegex().IsMatch(decoded))
            return "[REDACTED-JWT]";

        if (decoded.Length >= LongTokenMinLength && LongTokenRegex().IsMatch(decoded))
            return "[REDACTED-TOKEN]";

        return value;
    }

    private static string CategorizeSensitiveKey(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower switch
        {
            "password" or "passwd" or "pwd" or "pass" or "secret" or "credential" => "credential",
            "token" or "access_token" or "refresh_token" or "jwt" or "csrf" or "csrftoken"
                or "csrf_token" or "nonce" or "bearer" => "token",
            "api_key" or "apikey" or "api-key" or "key" or "private_key" or "public_key"
                or "encryption_key" or "client_secret" or "client_id" or "app_secret"
                or "app_key" or "aws_access_key" or "aws_secret" or "azure_key" => "api_key",
            "email" or "mail" or "e-mail" => "email",
            "username" or "user_name" or "login" => "username",
            "phone" or "tel" or "mobile" => "phone",
            "ssn" or "social_security" => "ssn",
            "name" or "firstname" or "first_name" or "lastname" or "last_name" => "name",
            "address" or "street" or "city" or "zip" or "zipcode" or "postal" => "address",
            "credit_card" or "creditcard" or "card_number" or "cvv" or "cvc" => "credit_card",
            "account" or "account_number" or "routing" or "iban" or "swift" => "financial",
            "auth" or "authorization" or "session" or "sessionid" or "session_id" => "session",
            _ => "sensitive"
        };
    }

    /// <summary>
    ///     Detects UTM parameters and click IDs in a query string, returning hashed signals.
    ///     Raw values are never returned - only HMAC-SHA256 (or SHA256 fallback) hashes.
    ///     Also checks for referrer mismatch: click ID present but referer absent or wrong domain.
    /// </summary>
    public static AdTrafficDetectionResult DetectAdTrafficParams(
        string? queryString,
        string? referer,
        byte[]? hmacKey = null)
    {
        if (string.IsNullOrEmpty(queryString))
            return AdTrafficDetectionResult.Empty;

        var qs = queryString.StartsWith('?') ? queryString[1..] : queryString;
        if (string.IsNullOrEmpty(qs))
            return AdTrafficDetectionResult.Empty;

        string? utmSource = null, utmMedium = null, utmCampaign = null;
        string? clickIdValue = null;
        bool hasGclid = false, hasFbclid = false, hasMsclkid = false, hasTtclid = false;

        foreach (var part in qs.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;

            var key = part[..eq];
            string value;
            try
            {
                value = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
            catch (UriFormatException)
            {
                continue;
            }

            if (string.Equals(key, "utm_source", StringComparison.OrdinalIgnoreCase))
                utmSource = value;
            else if (string.Equals(key, "utm_medium", StringComparison.OrdinalIgnoreCase))
                utmMedium = value;
            else if (string.Equals(key, "utm_campaign", StringComparison.OrdinalIgnoreCase))
                utmCampaign = value;
            else if (string.Equals(key, "gclid", StringComparison.OrdinalIgnoreCase))
            { hasGclid = true; clickIdValue = value; }
            else if (string.Equals(key, "fbclid", StringComparison.OrdinalIgnoreCase))
            { hasFbclid = true; clickIdValue ??= value; }
            else if (string.Equals(key, "msclkid", StringComparison.OrdinalIgnoreCase))
            { hasMsclkid = true; clickIdValue ??= value; }
            else if (string.Equals(key, "ttclid", StringComparison.OrdinalIgnoreCase))
            { hasTtclid = true; clickIdValue ??= value; }
        }

        var utmPresent = utmSource != null || utmMedium != null || utmCampaign != null
                         || hasGclid || hasFbclid || hasMsclkid || hasTtclid;

        if (!utmPresent)
            return AdTrafficDetectionResult.Empty;

        var platform = InferSourcePlatform(utmSource, hasGclid, hasFbclid, hasMsclkid, hasTtclid);
        var sourceHash = utmSource != null ? HashAdValue(utmSource, hmacKey) : null;
        var mediumHash = utmMedium != null ? HashAdValue(utmMedium, hmacKey) : null;
        var campaignHash = utmCampaign != null ? HashAdValue(utmCampaign, hmacKey) : null;
        var clickIdHash = clickIdValue != null ? HashAdValue(clickIdValue, hmacKey) : null;

        var referrerPresent = !string.IsNullOrEmpty(referer);
        var referrerMismatch = DetectReferrerMismatch(
            platform, hasGclid || hasFbclid || hasMsclkid || hasTtclid, referrerPresent, referer);

        return new AdTrafficDetectionResult
        {
            UtmPresent = true,
            SourcePlatform = platform,
            SourceHash = sourceHash,
            MediumHash = mediumHash,
            CampaignHash = campaignHash,
            HasGclid = hasGclid,
            HasFbclid = hasFbclid,
            HasMsclkid = hasMsclkid,
            HasTtclid = hasTtclid,
            ClickIdHash = clickIdHash,
            ReferrerPresent = referrerPresent,
            ReferrerMismatch = referrerMismatch
        };
    }

    private static string InferSourcePlatform(
        string? utmSource, bool hasGclid, bool hasFbclid, bool hasMsclkid, bool hasTtclid)
    {
        if (hasGclid) return "google";
        if (hasFbclid) return "meta";
        if (hasMsclkid) return "microsoft";
        if (hasTtclid) return "tiktok";
        if (utmSource == null) return "paid_other";

        return utmSource.ToLowerInvariant() switch
        {
            "google" or "google_ads" => "google",
            "facebook" or "fb" or "instagram" => "meta",
            "bing" or "microsoft" => "microsoft",
            "tiktok" => "tiktok",
            _ => "paid_other"
        };
    }

    private static bool DetectReferrerMismatch(
        string platform, bool hasClickId, bool referrerPresent, string? referer)
    {
        if (!hasClickId) return false;
        if (!referrerPresent) return true;

        var refererLower = referer!.ToLowerInvariant();
        return platform switch
        {
            "google" => !refererLower.Contains("google.") && !refererLower.Contains("googleadservices"),
            "meta" => !refererLower.Contains("facebook.com") && !refererLower.Contains("instagram.com"),
            "microsoft" => !refererLower.Contains("bing.com") && !refererLower.Contains("microsoft.com"),
            "tiktok" => !refererLower.Contains("tiktok.com"),
            _ => false
        };
    }

    private static string HashAdValue(string value, byte[]? hmacKey)
    {
        byte[] hash;
        if (hmacKey != null && hmacKey.Length >= 16)
        {
            using var hmac = new System.Security.Cryptography.HMACSHA256(hmacKey);
            hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        }
        else
        {
            hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value));
        }
        return Convert.ToBase64String(hash[..16])
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

/// <summary>
///     Result of PII detection in a query string.
/// </summary>
public sealed record PiiDetectionResult
{
    public static readonly PiiDetectionResult Empty = new()
    {
        HasPii = false,
        DetectedTypes = Array.Empty<string>()
    };

    /// <summary>Whether any PII was detected.</summary>
    public bool HasPii { get; init; }

    /// <summary>List of detected PII types (e.g., "email", "token", "credential").</summary>
    public IReadOnlyList<string> DetectedTypes { get; init; } = [];
}

/// <summary>
///     Result of ad traffic parameter detection in a query string.
///     All hashed values use HMAC-SHA256 (or SHA256 fallback) - no raw values stored.
/// </summary>
public sealed record AdTrafficDetectionResult
{
    public static readonly AdTrafficDetectionResult Empty = new();

    public bool UtmPresent { get; init; }
    public string SourcePlatform { get; init; } = "organic";
    public string? SourceHash { get; init; }
    public string? MediumHash { get; init; }
    public string? CampaignHash { get; init; }
    public bool HasGclid { get; init; }
    public bool HasFbclid { get; init; }
    public bool HasMsclkid { get; init; }
    public bool HasTtclid { get; init; }
    public string? ClickIdHash { get; init; }
    public bool ReferrerPresent { get; init; }
    public bool ReferrerMismatch { get; init; }
}
