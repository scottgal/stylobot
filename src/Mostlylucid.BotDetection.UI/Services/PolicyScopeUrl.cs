using System.Text.Json;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Pure URL + label helpers for <see cref="PolicyScope"/> breadcrumbs.
///     Kept separate from the presenter so Razor partials can call into it
///     without dragging the presenter's dependency graph along. The exact
///     route paths are placeholders B7-B9 will replace when the SbPolicyStack
///     control is wired into the 9 dashboard call sites; until then the
///     anchors render as relative hash anchors that don't navigate, which is
///     the right behaviour for an empty-route control under development.
///
///     <para>
///         Only the Host slot is consulted for URL / label rendering. The
///         orthogonal slots (Method / Geo / Identity) are surfaced separately
///         in the scope picker once it lands; here they degrade to the
///         Host-derived display so breadcrumb chains stay stable.
///     </para>
/// </summary>
public static class PolicyScopeUrl
{
    /// <summary>
    ///     Build the route path that drives the breadcrumb anchor. Today this
    ///     emits a stable hash-only URL keyed off the scope discriminator and
    ///     its parts; B7-B9 will replace this with the actual dashboard route
    ///     once the call sites land.
    /// </summary>
    public static string For(PolicyScope scope) => scope.Host switch
    {
        null => "#policies",
        HostScope.Domain d => $"#policies/{Slug(d.Name)}",
        HostScope.Subdomain s => $"#policies/{Slug(s.DomainName)}/{Slug(s.SubdomainName)}",
        HostScope.Endpoint e => $"#policies/{Slug(e.DomainName)}/{Slug(e.SubdomainName)}/{Slug(e.PathTemplate)}",
        _ => "#policies"
    };

    /// <summary>
    ///     Render the breadcrumb segment label for <paramref name="scope"/>.
    ///     Wildcard renders as "All sites"; Domain as the apex; Subdomain as
    ///     the host; Endpoint as the path template.
    /// </summary>
    public static string Label(PolicyScope scope) => scope.Host switch
    {
        null => "All sites",
        HostScope.Domain d => d.Name,
        HostScope.Subdomain s => s.SubdomainName,
        HostScope.Endpoint e => e.PathTemplate,
        _ => "All sites"
    };

    private static string Slug(string s)
    {
        // Very conservative slug: anything not alphanumeric / dot / dash becomes
        // a dash. Good enough for routing keys until B7-B9 lands the real route.
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_')
                sb.Append(ch);
            else
                sb.Append('-');
        }
        return sb.ToString();
    }

    // -------- Encode / Decode (round-trippable URL form for query strings) --------

    private const char Separator = '|';

    /// <summary>
    ///     Encode a <see cref="PolicyScope"/> as a single URL-safe query token.
    ///     Used by the explainer HTMX endpoint so the partial can pass the
    ///     scope it's bound to without re-creating the breadcrumb. Only the
    ///     Host slot is encoded today; orthogonal slots will land alongside
    ///     the scope-picker UI work.
    /// </summary>
    public static string Encode(PolicyScope scope) => scope.Host switch
    {
        null => "wildcard",
        HostScope.Domain d => $"domain{Separator}{Uri.EscapeDataString(d.Name)}",
        HostScope.Subdomain s =>
            $"subdomain{Separator}{Uri.EscapeDataString(s.DomainName)}{Separator}{Uri.EscapeDataString(s.SubdomainName)}",
        HostScope.Endpoint e =>
            $"endpoint{Separator}{Uri.EscapeDataString(e.DomainName)}{Separator}{Uri.EscapeDataString(e.SubdomainName)}{Separator}{Uri.EscapeDataString(e.PathTemplate)}",
        _ => "wildcard"
    };

    /// <summary>
    ///     Decode a string previously produced by <see cref="Encode"/>. Unknown
    ///     / malformed tokens degrade gracefully to a wildcard <see cref="PolicyScope"/>
    ///     so a stale URL never throws on the route handler.
    /// </summary>
    public static PolicyScope Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return PolicyScope.Wildcard();
        var parts = encoded.Split(Separator);
        switch (parts[0])
        {
            case "wildcard":
                return PolicyScope.Wildcard();
            case "domain" when parts.Length >= 2:
                return PolicyScope.Domain(Uri.UnescapeDataString(parts[1]));
            case "subdomain" when parts.Length >= 3:
                return PolicyScope.Subdomain(
                    Uri.UnescapeDataString(parts[1]),
                    Uri.UnescapeDataString(parts[2]));
            case "endpoint" when parts.Length >= 4:
                return PolicyScope.Endpoint(
                    Uri.UnescapeDataString(parts[1]),
                    Uri.UnescapeDataString(parts[2]),
                    Uri.UnescapeDataString(parts[3]));
            default:
                return PolicyScope.Wildcard();
        }
    }

    // -------- JSON projection (SbScopePicker payload) ---------------------
    //
    // The SbScopePicker view component (T4 Part A) serialises a composite
    // scope as a four-axis JSON object. The shape mirrors PolicyScope's
    // own slots: `{ host: ..., method: ..., geo: ..., identity: ... }`.
    // Host is itself a discriminated union: `{ kind: domain|subdomain|endpoint, ... }`.
    // Identity follows the same pattern: `{ kind: named_bot|bot_type|human_browser|fingerprint, value: "..." }`.
    // Any missing / null slot is the wildcard for that axis. The parser is
    // tolerant: unknown discriminators, missing required fields, or invalid
    // JSON degrade to a wildcard slot rather than throwing so a half-filled
    // form does not crash the controller.

    /// <summary>
    ///     Parse the JSON payload produced by <c>SbScopePicker</c>'s hidden
    ///     input into a <see cref="PolicyScope"/>. Empty, null, or invalid
    ///     JSON yields a wildcard scope. Unknown axis discriminators are
    ///     treated as the wildcard for that axis so an incomplete form
    ///     never throws.
    /// </summary>
    public static PolicyScope FromPickerJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return PolicyScope.Wildcard();
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return PolicyScope.Wildcard();
        }
        if (root.ValueKind != JsonValueKind.Object) return PolicyScope.Wildcard();

        var host = ParseHost(root);
        var method = ReadString(root, "method")?.Trim().ToUpperInvariant();
        var geo = ReadString(root, "geo")?.Trim().ToUpperInvariant();
        var identity = ParseIdentity(root);

        if (string.IsNullOrEmpty(method)) method = null;
        if (string.IsNullOrEmpty(geo)) geo = null;

        return new PolicyScope(Host: host, Method: method, Geo: geo, Identity: identity);
    }

    private static HostScope? ParseHost(JsonElement root)
    {
        if (!root.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.Object)
            return null;
        var kind = ReadString(host, "kind")?.Trim().ToLowerInvariant();
        var domain = ReadString(host, "domain");
        var sub = ReadString(host, "sub");
        var path = ReadString(host, "path");
        return kind switch
        {
            "domain" when !string.IsNullOrWhiteSpace(domain) =>
                new HostScope.Domain(domain.Trim()),
            "subdomain" when !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(sub) =>
                new HostScope.Subdomain(domain.Trim(), sub.Trim()),
            "endpoint" when !string.IsNullOrWhiteSpace(domain)
                            && !string.IsNullOrWhiteSpace(sub)
                            && !string.IsNullOrWhiteSpace(path) =>
                new HostScope.Endpoint(domain.Trim(), sub.Trim(), path.Trim()),
            _ => null
        };
    }

    private static IdentityScope? ParseIdentity(JsonElement root)
    {
        if (!root.TryGetProperty("identity", out var id) || id.ValueKind != JsonValueKind.Object)
            return null;
        var kind = ReadString(id, "kind")?.Trim().ToLowerInvariant();
        var value = ReadString(id, "value");
        if (string.IsNullOrWhiteSpace(value)) return null;
        return kind switch
        {
            "named_bot" => new IdentityScope.NamedBot(value.Trim()),
            "bot_type" => new IdentityScope.BotType(value.Trim()),
            "human_browser" => new IdentityScope.HumanBrowser(value.Trim()),
            "fingerprint" => new IdentityScope.Fingerprint(value.Trim()),
            _ => null
        };
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Null => null,
            _ => null
        };
    }
}
