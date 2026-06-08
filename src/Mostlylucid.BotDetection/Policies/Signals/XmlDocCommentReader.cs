using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     Loads the assembly's emitted XML doc file and indexes <c>&lt;summary&gt;</c>
///     text by field name. The C# compiler writes these keys as
///     <c>F:&lt;FullyQualifiedType&gt;.&lt;FieldName&gt;</c>; this reader strips
///     down to the bare field name so the catalog can match on
///     a reflected field name directly (no string concatenation gymnastics).
///
///     <para>
///     A missing XML file is NOT an error. The catalogue still loads -- descriptors
///     just carry empty <c>Short</c> / <c>Long</c> strings. This keeps the loader
///     robust in two real scenarios: AOT publishes that do not ship the XML file,
///     and test/debug builds that turn off <c>GenerateDocumentationFile</c>.
///     </para>
/// </summary>
public sealed class XmlDocCommentReader
{
    private readonly Dictionary<string, string> _summariesByFieldName;

    private XmlDocCommentReader(Dictionary<string, string> summariesByFieldName)
    {
        _summariesByFieldName = summariesByFieldName;
    }

    /// <summary>
    ///     Locate the XML doc file next to the assembly's DLL and parse every
    ///     <c>F:</c> field entry into the in-memory index. Returns an empty
    ///     reader when the file is missing.
    /// </summary>
    public static XmlDocCommentReader Load(Assembly assembly)
    {
        var dllPath = assembly.Location;
        if (string.IsNullOrEmpty(dllPath))
            return new XmlDocCommentReader(new Dictionary<string, string>(StringComparer.Ordinal));

        var xmlPath = Path.ChangeExtension(dllPath, ".xml");
        if (!File.Exists(xmlPath))
            return new XmlDocCommentReader(new Dictionary<string, string>(StringComparer.Ordinal));

        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var doc = XDocument.Load(xmlPath);
            var members = doc.Root?.Element("members");
            if (members is null) return new XmlDocCommentReader(index);

            foreach (var member in members.Elements("member"))
            {
                var name = member.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                // Only field entries -- methods, properties, types use M: / P: / T:
                if (!name.StartsWith("F:", StringComparison.Ordinal)) continue;

                // F:Namespace.Type.FieldName -> FieldName
                var lastDot = name.LastIndexOf('.');
                if (lastDot < 0 || lastDot == name.Length - 1) continue;
                var fieldName = name[(lastDot + 1)..];

                var summary = member.Element("summary");
                if (summary is null) continue;

                var text = ExtractText(summary);
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Multiple SignalKeys partials in different files could yield the
                // same field name; last write wins. The vocabulary disallows
                // collisions anyway, so this is a non-issue in practice.
                index[fieldName] = text;
            }
        }
        catch
        {
            // Malformed XML should not nuke the catalogue; treat it as no docs available.
            return new XmlDocCommentReader(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        return new XmlDocCommentReader(index);
    }

    /// <summary>Returns the full summary text for <paramref name="fieldName"/> or null when not indexed.</summary>
    public string? TryGetSummary(string fieldName)
        => _summariesByFieldName.TryGetValue(fieldName, out var v) ? v : null;

    /// <summary>
    ///     Walk the summary element's children and concatenate all text content,
    ///     flattening nested tags like <c>&lt;see cref="..."/&gt;</c> into their
    ///     <c>cref</c>'s tail. Collapses runs of whitespace introduced by the
    ///     compiler's pretty-printer so the resulting string is one human-readable
    ///     paragraph.
    /// </summary>
    private static string ExtractText(XElement summary)
    {
        var sb = new StringBuilder();
        AppendNodeText(summary, sb);

        // Compiler indents every summary line by the file's natural indent; collapse
        // every run of whitespace (including embedded newlines) to a single space so
        // tooltips don't carry a wall of leading whitespace.
        var raw = sb.ToString();
        var collapsed = new StringBuilder(raw.Length);
        var inWs = false;
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inWs)
                {
                    collapsed.Append(' ');
                    inWs = true;
                }
            }
            else
            {
                collapsed.Append(c);
                inWs = false;
            }
        }
        return collapsed.ToString().Trim();
    }

    private static void AppendNodeText(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText t:
                    sb.Append(t.Value);
                    break;
                case XElement e:
                    // For <see cref="X.Y.Z"/> render the tail segment of the cref so the
                    // resulting prose reads as English ("see RiskJustification") rather
                    // than as XML noise.
                    if (string.Equals(e.Name.LocalName, "see", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.Name.LocalName, "seealso", StringComparison.OrdinalIgnoreCase))
                    {
                        var cref = e.Attribute("cref")?.Value;
                        if (!string.IsNullOrEmpty(cref))
                        {
                            var tail = cref.AsSpan();
                            var colon = tail.IndexOf(':');
                            if (colon >= 0) tail = tail[(colon + 1)..];
                            var lastDot = tail.LastIndexOf('.');
                            if (lastDot >= 0) tail = tail[(lastDot + 1)..];
                            sb.Append(tail);
                        }
                        else
                        {
                            AppendNodeText(e, sb);
                        }
                    }
                    else
                    {
                        AppendNodeText(e, sb);
                    }
                    break;
            }
        }
    }
}
