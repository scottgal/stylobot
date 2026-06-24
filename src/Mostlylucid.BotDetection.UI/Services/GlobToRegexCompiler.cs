using System.Text;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Editor-side glob to anchored-regex compiler. Webmasters author path
///     and host patterns in glob form (<c>/api/users/*</c>, <c>**</c>,
///     <c>?</c>, literal text); the editor stores regex via this compiler.
///     The predicate evaluator's <c>matches</c> operator runs against the
///     compiled regex; webmasters never see regex.
///
///     <para>Glob semantics:</para>
///     <list type="bullet">
///         <item><description><c>*</c> — one path segment (one or more non-slash characters)</description></item>
///         <item><description><c>**</c> — any depth, including slashes (zero or more characters)</description></item>
///         <item><description><c>?</c> — exactly one non-slash character</description></item>
///         <item><description>Other regex metacharacters (<c>.+()[]{}^$\</c>) are escaped literally.</description></item>
///     </list>
///
///     <para>The compiled regex is anchored with <c>^</c> and <c>$</c> — globs match
///     the entire input by convention. Power users editing the raw text DSL
///     bypass this compiler and write regex directly; the editor surface uses
///     this for the curated picker's <c>type: glob</c> facets only.</para>
/// </summary>
public static class GlobToRegexCompiler
{
    /// <summary>Compile a webmaster-authored glob string to an anchored regex.</summary>
    public static string Compile(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    sb.Append(".*");
                    i++; // consume the second *
                    break;
                case '*' when i + 1 < glob.Length && glob[i + 1] == '.':
                    // domain-style wildcard: bind to the next literal separator (a dot here),
                    // so *.example.com captures one label, not anything-up-to-the-next-slash.
                    sb.Append("[^.]+");
                    break;
                case '*':
                    sb.Append("[^/]+");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '.': case '+': case '(': case ')': case '{':
                case '}': case '[': case ']': case '^': case '$': case '\\':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }
}
