using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Mostlylucid.BotDetection.StyloExtract.Options;

namespace Mostlylucid.BotDetection.StyloExtract.Internals;

/// <summary>
/// Applies <see cref="CacheOverrideOptions"/> to the response Cache-Control and Vary headers
/// according to the selected <see cref="CacheControlMode"/>.
/// </summary>
public sealed class CacheControlWriter
{
    /// <summary>
    /// Applies the cache options to <paramref name="context"/>.
    /// Call this after the downstream response has been captured (before writing the
    /// final body) so the headers are in the final response.
    /// </summary>
    public void Apply(HttpContext context, CacheOverrideOptions options)
    {
        if (options.Mode == CacheControlMode.Respect)
        {
            ApplyVaryHeaders(context, options);
            return;
        }

        var headers = context.Response.Headers;

        if (options.Mode == CacheControlMode.Override)
        {
            headers.Remove(HeaderNames.CacheControl);
            var directives = BuildDirectives(options);
            if (directives.Count > 0)
                headers[HeaderNames.CacheControl] = string.Join(", ", directives);
        }
        else // Add
        {
            var existing = headers[HeaderNames.CacheControl].ToString();
            var directives = BuildDirectives(options);
            var toAdd = new List<string>(directives.Count);

            // Parse existing header into directive names (split by comma, trim, take the
            // part before `=`). Substring matching produced false positives where, for
            // example, an existing `s-maxage=600` would block a new `max-age=86400`.
            var existingNames = ParseDirectiveNames(existing);

            foreach (var directive in directives)
            {
                var name = directive.Contains('=') ? directive[..directive.IndexOf('=')] : directive;
                if (!existingNames.Contains(name))
                    toAdd.Add(directive);
            }

            if (toAdd.Count > 0)
            {
                var combined = string.IsNullOrEmpty(existing)
                    ? string.Join(", ", toAdd)
                    : existing + ", " + string.Join(", ", toAdd);
                headers[HeaderNames.CacheControl] = combined;
            }
        }

        ApplyVaryHeaders(context, options);
    }

    private static HashSet<string> ParseDirectiveNames(string headerValue)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(headerValue)) return names;
        foreach (var directive in headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = directive.Contains('=') ? directive[..directive.IndexOf('=')].Trim() : directive;
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }

    private static List<string> BuildDirectives(CacheOverrideOptions options)
    {
        var directives = new List<string>(4);

        if (options.Public.HasValue && options.Public.Value)
            directives.Add("public");

        if (options.MaxAge.HasValue)
            directives.Add($"max-age={options.MaxAge.Value}");

        if (options.NoStore.HasValue && options.NoStore.Value)
            directives.Add("no-store");

        if (options.MustRevalidate.HasValue && options.MustRevalidate.Value)
            directives.Add("must-revalidate");

        return directives;
    }

    private static void ApplyVaryHeaders(HttpContext context, CacheOverrideOptions options)
    {
        if (!options.VaryByBotType && !options.VaryByAccept)
            return;

        var headers = context.Response.Headers;
        var existing = headers[HeaderNames.Vary].ToString();

        var toAppend = new List<string>(2);

        if (options.VaryByBotType
            && !existing.Contains("X-StyloBot-BotType", StringComparison.OrdinalIgnoreCase))
        {
            toAppend.Add("X-StyloBot-BotType");
        }

        if (options.VaryByAccept
            && !existing.Contains("Accept", StringComparison.OrdinalIgnoreCase))
        {
            toAppend.Add("Accept");
        }

        if (toAppend.Count > 0)
        {
            var combined = string.IsNullOrEmpty(existing)
                ? string.Join(", ", toAppend)
                : existing + ", " + string.Join(", ", toAppend);
            headers[HeaderNames.Vary] = combined;
        }
    }
}
