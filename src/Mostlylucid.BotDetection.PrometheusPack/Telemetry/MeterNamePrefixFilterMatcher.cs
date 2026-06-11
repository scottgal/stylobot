namespace Mostlylucid.BotDetection.PrometheusPack.Telemetry;

internal static class MeterNamePrefixFilterMatcher
{
    public static bool Matches(string meterName, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;

        var span = filter.AsSpan();
        while (!span.IsEmpty)
        {
            var sep = span.IndexOf('|');
            var token = sep < 0 ? span : span[..sep];
            if (!token.IsEmpty
                && meterName.AsSpan().StartsWith(token, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (sep < 0) break;
            span = span[(sep + 1)..];
        }
        return false;
    }
}
