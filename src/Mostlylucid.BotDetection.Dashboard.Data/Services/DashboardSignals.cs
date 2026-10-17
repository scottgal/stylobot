using System.Collections.Frozen;
using System.Text.Json;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     The single chokepoint for the per-detection <c>important_signals</c> payload: the
///     non-PII admission filter, and the JSON encode/decode used at every persistence and
///     transport boundary (SQLite column, upstream edge header).
///     <para>
///         Both the broadcast builder (<c>DetectionBroadcastMiddleware.BuildImportantSignals</c>)
///         and the store write (<c>SqliteDashboardEventStore.AddDetectionAsync</c>) go through
///         <see cref="IsAllowed"/>, so the two can never drift: a key the builder admits cannot
///         be silently dropped on write, and a PII key the builder rejects cannot sneak into the
///         dashboard DB through a hand-built event. Likewise <see cref="Decode"/> is the one
///         decode implementation, so a value that was a <see cref="bool"/> on the hot path reads
///         back as a <see cref="bool"/> (not a <see cref="JsonElement"/>) after a store
///         round-trip — readers such as the verified-bot trust triple do <c>is bool b &amp;&amp; b</c>
///         and would otherwise silently stop matching.
///     </para>
/// </summary>
public static class DashboardSignals
{
    /// <summary>
    ///     Signal key prefixes that must never reach the dashboard (PII / secrets).
    ///     <para>
    ///         Note this is a BLOCK list, not an allow-list: an allow-list is exactly why
    ///         arcjet, ja3/ja4, headless, ai-scraper, datacenter and others were captured by
    ///         detection yet invisible on the dashboard, and why "Real browser (confirmed)"
    ///         detail panels showed "No detection signals recorded"
    ///         ([[feedback_no_word_lists]]). Detection signals are non-PII by construction
    ///         (hashes, scores, bands, booleans, category labels); the handful of
    ///         PII-carrying keys are blocked explicitly below.
    ///     </para>
    /// </summary>
    private static readonly FrozenSet<string> BlockedPrefixes =
        FrozenSet.ToFrozenSet(
        [
            "pii.", "raw.", "secret.", "cookie.", "auth.", "credential."
        ], StringComparer.OrdinalIgnoreCase);

    /// <summary>Individual signal keys that must never reach the dashboard (PII/secret).</summary>
    private static readonly FrozenSet<string> BlockedKeys = FrozenSet.ToFrozenSet(
    [
        "ua.raw", "ip.address", "client_ip", "ip_address",
        "email", "phone", "session_id", "cookie", "authorization"
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum number of signals forwarded to the dashboard per detection.</summary>
    public const int MaxSignalsPerDetection = 80;

    /// <summary>
    ///     True when <paramref name="key"/> may be persisted / broadcast. Prefix-blocked keys
    ///     are rejected by the leading <c>"segment."</c> (a key with no dot is never
    ///     prefix-blocked).
    /// </summary>
    public static bool IsAllowed(string key)
    {
        if (BlockedKeys.Contains(key)) return false;
        var dot = key.IndexOf('.');
        return dot < 0 || !BlockedPrefixes.Contains(key[..(dot + 1)]);
    }

    /// <summary>
    ///     Project an arbitrary signal dictionary through <see cref="IsAllowed"/>, preserving
    ///     insertion order and capping at <see cref="MaxSignalsPerDetection"/>. Returns null
    ///     when nothing survives so the caller stores SQL NULL rather than an empty blob.
    /// </summary>
    public static Dictionary<string, object>? Filter(IReadOnlyDictionary<string, object>? signals)
    {
        if (signals is not { Count: > 0 }) return null;

        Dictionary<string, object>? filtered = null;
        foreach (var (key, value) in signals)
        {
            if (!IsAllowed(key)) continue;
            filtered ??= new Dictionary<string, object>(Math.Min(signals.Count, MaxSignalsPerDetection));
            if (filtered.Count >= MaxSignalsPerDetection) break;
            filtered[key] = value;
        }
        return filtered;
    }

    /// <summary>
    ///     Encode a signal dict for storage / transport. Returns null for an absent or empty
    ///     dict so the column stays NULL instead of <c>{}</c>.
    /// </summary>
    public static string? Encode(IReadOnlyDictionary<string, object>? signals)
    {
        var filtered = Filter(signals);
        return filtered is null ? null : JsonSerializer.Serialize(filtered);
    }

    /// <summary>
    ///     Decode a stored / transported signal payload back to CLR primitives.
    ///     Strings stay strings, JSON numbers become <see cref="long"/>/<see cref="double"/>,
    ///     and booleans become <see cref="bool"/> — never <see cref="JsonElement"/> boxes.
    ///     Malformed payloads decode to null rather than throwing on a read path.
    /// </summary>
    public static Dictionary<string, object>? Decode(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var decoded = new Dictionary<string, object>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!IsAllowed(property.Name)) continue;
                // A JSON null carries no signal; skip it rather than planting a null
                // value that readers doing `is not null` / `is bool` would trip over.
                if (property.Value.ValueKind == JsonValueKind.Null) continue;
                decoded[property.Name] = DecodeValue(property.Value);
            }
            return decoded.Count == 0 ? null : decoded;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Decode a single JSON value to the CLR primitive the hot path would have held.</summary>
    public static object DecodeValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => element.GetRawText(),
    };
}
