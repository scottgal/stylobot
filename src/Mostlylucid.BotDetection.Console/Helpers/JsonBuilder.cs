using System.Text;

namespace Mostlylucid.BotDetection.Console.Helpers;

/// <summary>
///     AOT-compatible JSON building utilities
/// </summary>
public static class JsonBuilder
{
    /// <summary>
    ///     Escape JSON string values
    /// </summary>
    public static string EscapeJson(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    /// <summary>
    ///     Build JSON object with optional pretty-printing (AOT-compatible)
    /// </summary>
    public static string BuildJsonObject(Dictionary<string, object> obj, int indent = 0)
    {
        var sb = new StringBuilder();
        var prefix = indent > 0 ? new string(' ', indent) : "";
        var innerPrefix = indent > 0 ? new string(' ', indent + 2) : "";
        var nl = indent > 0 ? "\n" : "";

        sb.Append('{');
        if (indent > 0) sb.Append('\n');

        var items = obj.ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var kvp = items[i];
            if (indent > 0) sb.Append(innerPrefix);

            sb.Append('"').Append(EscapeJson(kvp.Key)).Append("\":");
            if (indent > 0) sb.Append(' ');

            if (kvp.Value is string str)
                sb.Append('"').Append(EscapeJson(str)).Append('"');
            else if (kvp.Value is double d)
                sb.Append(d.ToString("0.0###############"));
            else if (kvp.Value is bool b)
                sb.Append(b ? "true" : "false");
            else if (kvp.Value is Dictionary<string, object> nested)
                sb.Append(BuildJsonObject(nested, indent > 0 ? indent + 2 : 0));
            else
                sb.Append(kvp.Value?.ToString() ?? "null");

            if (i < items.Count - 1) sb.Append(',');
            if (indent > 0) sb.Append('\n');
        }

        if (indent > 0) sb.Append(prefix);
        sb.Append('}');
        return sb.ToString();
    }
}