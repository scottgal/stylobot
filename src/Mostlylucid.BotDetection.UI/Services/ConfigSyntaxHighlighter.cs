using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Tiny server-side syntax highlighter for the Configuration tab. Replaces the
///     dead Monaco-from-CDN editor with pre-rendered HTML so the dashboard's strict
///     CSP (script-src 'self') can stay strict. Two tokenizers - YAML and JSON -
///     both emit <c>&lt;span class="tok-X"&gt;…&lt;/span&gt;</c> markup against a single
///     token palette (see _ConfigurationEditor.cshtml for the CSS).
///     <para>
///         Read-only by construction. There is no parser - YAML is highlighted
///         line-by-line with regex, JSON via <see cref="Utf8JsonReader"/>. Good
///         enough for human-readable manifest YAML and well-formed
///         <c>BotDetectionOptions</c> JSON; we never round-trip the highlighted
///         output back through a parser.
///     </para>
/// </summary>
public static class ConfigSyntaxHighlighter
{
    // YAML token regexes. Compiled once; YAML grammar is intentionally narrow here -
    // we only highlight the subset that appears in detector manifests + appsettings.
    // `~` (YAML null) isn't in the bool/null regex because \b only matches between
    // \w and \W and `~` is itself \W; if we ever need to highlight tilde-null we
    // would need a separate pattern without word boundaries.
    private static readonly Regex KeyRx = new(@"^(\s*)([\w\.\-/]+)(\s*:)", RegexOptions.Compiled);
    private static readonly Regex ListItemRx = new(@"^(\s*)(-)(\s|$)", RegexOptions.Compiled);
    private static readonly Regex StringRx = new("\"[^\"\\\\]*(\\\\.[^\"\\\\]*)*\"|'[^'\\\\]*(\\\\.[^'\\\\]*)*'", RegexOptions.Compiled);
    private static readonly Regex NumberRx = new(@"(?<![\w.])-?\d+(\.\d+)?([eE][+-]?\d+)?(?![\w.])", RegexOptions.Compiled);
    private static readonly Regex BoolNullRx = new(@"\b(true|false|null|yes|no|on|off)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    ///     Highlight a YAML document. Output is HTML-escaped and wrapped in
    ///     <c>&lt;span class="tok-*"&gt;</c> spans. Caller is responsible for the
    ///     surrounding <c>&lt;pre&gt;&lt;code&gt;</c>.
    /// </summary>
    public static string HighlightYaml(string yaml)
    {
        if (string.IsNullOrEmpty(yaml)) return string.Empty;

        var sb = new StringBuilder(yaml.Length + 256);
        var lines = yaml.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i].TrimEnd('\r');
            HighlightYamlLine(rawLine, sb);
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void HighlightYamlLine(string line, StringBuilder sb)
    {
        if (line.Length == 0) return;

        // Split into "code" portion and trailing "# comment" so the comment regex
        // doesn't get confused by '#' inside a quoted string.
        var commentStart = FindCommentStart(line);
        var code = commentStart < 0 ? line : line[..commentStart];
        var comment = commentStart < 0 ? null : line[commentStart..];

        var remaining = code;

        // Key: prefix
        var keyMatch = KeyRx.Match(remaining);
        if (keyMatch.Success)
        {
            sb.Append(HtmlEncoder.Default.Encode(keyMatch.Groups[1].Value));
            sb.Append("<span class=\"tok-key\">").Append(HtmlEncoder.Default.Encode(keyMatch.Groups[2].Value)).Append("</span>");
            sb.Append("<span class=\"tok-punct\">").Append(HtmlEncoder.Default.Encode(keyMatch.Groups[3].Value)).Append("</span>");
            remaining = remaining[keyMatch.Length..];
        }
        else
        {
            // List-item leader: "- value"
            var listMatch = ListItemRx.Match(remaining);
            if (listMatch.Success)
            {
                sb.Append(HtmlEncoder.Default.Encode(listMatch.Groups[1].Value));
                sb.Append("<span class=\"tok-punct\">-</span>");
                sb.Append(HtmlEncoder.Default.Encode(listMatch.Groups[3].Value));
                remaining = remaining[listMatch.Length..];
            }
        }

        // After the key/dash leader, highlight the value portion.
        HighlightYamlValue(remaining, sb);

        if (comment is not null)
            sb.Append("<span class=\"tok-comment\">").Append(HtmlEncoder.Default.Encode(comment)).Append("</span>");
    }

    private static int FindCommentStart(string line)
    {
        // '#' starts a comment unless inside a quoted string. Single-pass scan.
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble)
            {
                // YAML requires whitespace before '#' for an inline comment.
                if (i == 0 || char.IsWhiteSpace(line[i - 1])) return i;
            }
        }
        return -1;
    }

    private static void HighlightYamlValue(string value, StringBuilder sb)
    {
        if (value.Length == 0) return;

        // Tokenize: strings → numbers → bool/null → plain text.
        // Order matters: strings have the strongest claim (bool words inside a
        // quoted string must not be coloured), so they go in first, then numbers
        // and bool/null skip any range that overlaps a previously-claimed span.
        var spans = new List<(int Start, int Len, string Class)>();
        foreach (Match m in StringRx.Matches(value)) spans.Add((m.Index, m.Length, "tok-str"));
        foreach (Match m in NumberRx.Matches(value))
        {
            if (!Overlaps(spans, m.Index, m.Length)) spans.Add((m.Index, m.Length, "tok-num"));
        }
        foreach (Match m in BoolNullRx.Matches(value))
        {
            if (!Overlaps(spans, m.Index, m.Length)) spans.Add((m.Index, m.Length, "tok-bool"));
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var cursor = 0;
        foreach (var (start, len, cls) in spans)
        {
            if (start > cursor) sb.Append(HtmlEncoder.Default.Encode(value[cursor..start]));
            sb.Append("<span class=\"").Append(cls).Append("\">")
              .Append(HtmlEncoder.Default.Encode(value.Substring(start, len)))
              .Append("</span>");
            cursor = start + len;
        }
        if (cursor < value.Length) sb.Append(HtmlEncoder.Default.Encode(value[cursor..]));
    }

    private static bool Overlaps(List<(int Start, int Len, string Class)> spans, int start, int len)
    {
        var end = start + len;
        foreach (var s in spans)
        {
            var sEnd = s.Start + s.Len;
            if (start < sEnd && end > s.Start) return true;
        }
        return false;
    }

    /// <summary>
    ///     Highlight a JSON document. Whitespace and indentation are taken from the
    ///     input (we re-emit byte-for-byte between tokens). Pass already-indented
    ///     JSON for human-readable output.
    /// </summary>
    public static string HighlightJson(string json)
    {
        if (string.IsNullOrEmpty(json)) return string.Empty;

        var sb = new StringBuilder(json.Length + 256);
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);

        var lastTokenEnd = 0;

        while (reader.Read())
        {
            // Re-emit any whitespace/punctuation between the previous token and this one.
            var tokenStart = (int)reader.TokenStartIndex;
            if (tokenStart > lastTokenEnd)
            {
                var between = Encoding.UTF8.GetString(bytes, lastTokenEnd, tokenStart - lastTokenEnd);
                AppendJsonPunct(sb, between);
            }

            lastTokenEnd = tokenStart + EmitJsonToken(reader, bytes, sb);
        }

        // Trailing whitespace/punctuation after the last token.
        if (lastTokenEnd < bytes.Length)
        {
            var tail = Encoding.UTF8.GetString(bytes, lastTokenEnd, bytes.Length - lastTokenEnd);
            AppendJsonPunct(sb, tail);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Emit the highlight markup for one JSON token and return the number of
    ///     source bytes the token occupied (so the caller can advance the cursor).
    ///     We always read from a complete single-segment byte[] so the reader's
    ///     value span is always contiguous - no need to fall back to HasValueSequence.
    /// </summary>
    private static int EmitJsonToken(Utf8JsonReader reader, byte[] bytes, StringBuilder sb)
    {
        var start = (int)reader.TokenStartIndex;
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            case JsonTokenType.EndObject:
            case JsonTokenType.StartArray:
            case JsonTokenType.EndArray:
                sb.Append("<span class=\"tok-punct\">").Append((char)bytes[start]).Append("</span>");
                return 1;

            case JsonTokenType.PropertyName:
                return EmitJsonQuoted(reader, bytes, start, sb, "tok-key");

            case JsonTokenType.String:
                return EmitJsonQuoted(reader, bytes, start, sb, "tok-str");

            case JsonTokenType.Number:
                {
                    var len = reader.ValueSpan.Length;
                    sb.Append("<span class=\"tok-num\">")
                      .Append(HtmlEncoder.Default.Encode(Encoding.UTF8.GetString(bytes, start, len)))
                      .Append("</span>");
                    return len;
                }

            case JsonTokenType.True:
                sb.Append("<span class=\"tok-bool\">true</span>");
                return 4;
            case JsonTokenType.False:
                sb.Append("<span class=\"tok-bool\">false</span>");
                return 5;
            case JsonTokenType.Null:
                sb.Append("<span class=\"tok-bool\">null</span>");
                return 4;

            default:
                return 0;
        }
    }

    /// <summary>
    ///     Emit a quoted JSON token (PropertyName or String). <c>ValueSpan</c>
    ///     excludes the surrounding quotes, so we add 2 to include both.
    /// </summary>
    private static int EmitJsonQuoted(Utf8JsonReader reader, byte[] bytes, int start, StringBuilder sb, string cls)
    {
        var raw = Encoding.UTF8.GetString(bytes, start, reader.ValueSpan.Length + 2);
        sb.Append("<span class=\"").Append(cls).Append("\">")
          .Append(HtmlEncoder.Default.Encode(raw))
          .Append("</span>");
        return reader.ValueSpan.Length + 2;
    }

    private static void AppendJsonPunct(StringBuilder sb, string text)
    {
        // Whitespace stays plain; punctuation (`:` and `,`) gets a dim class.
        foreach (var c in text)
        {
            if (c == ':' || c == ',')
                sb.Append("<span class=\"tok-punct\">").Append(c).Append("</span>");
            else
                sb.Append(HtmlEncoder.Default.Encode(c.ToString()));
        }
    }
}
