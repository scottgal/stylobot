using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace Mostlylucid.StyloWall.Optimization.Markdown;

public sealed class MarkdownEmitter
{
    private readonly MarkdownEmitterOptions _options;
    private readonly IConfiguration _angleSharpConfig;

    public MarkdownEmitter(MarkdownEmitterOptions options)
    {
        _options = options;
        _angleSharpConfig = Configuration.Default;
    }

    public async ValueTask<string> EmitAsync(string html, CancellationToken cancellationToken)
    {
        var ctx = BrowsingContext.New(_angleSharpConfig);
        using var doc = await ctx.OpenAsync(req => req.Content(html), cancellationToken);

        var root = ChooseRoot(doc);
        if (root is null) return string.Empty;

        var sb = new StringBuilder(Math.Max(256, html.Length / 4));
        var state = new EmitState(sb, _options);

        if (_options.EmitFrontMatter)
        {
            var title = doc.QuerySelector("title")?.TextContent?.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                sb.Append("---\n");
                sb.Append("title: ").Append(EscapeYaml(title!)).Append('\n');
                sb.Append("---\n\n");
            }
        }

        WalkBlock(root, state);

        return state.Result();
    }

    private INode? ChooseRoot(IDocument doc)
    {
        if (_options.PreferMainContent)
        {
            var main = doc.QuerySelector("main")
                       ?? doc.QuerySelector("article")
                       ?? doc.QuerySelector("[role=main]");
            if (main is not null) return main;
        }
        return doc.Body;
    }

    private static void WalkBlock(INode node, EmitState state)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case NodeType.Element:
                    EmitElement((IElement)child, state);
                    break;
                case NodeType.Text:
                    state.WriteInlineText(child.TextContent);
                    break;
            }
        }
    }

    private static void EmitElement(IElement el, EmitState state)
    {
        var tag = el.TagName;

        switch (tag)
        {
            case "SCRIPT":
            case "STYLE":
            case "NOSCRIPT":
            case "IFRAME":
            case "SVG":
            case "CANVAS":
            case "TEMPLATE":
            case "HEAD":
            case "NAV":
            case "FOOTER":
            case "ASIDE":
                return;

            case "H1": case "H2": case "H3": case "H4": case "H5": case "H6":
                EmitHeading(el, tag[1] - '0', state);
                return;

            case "P":
                state.StartBlock();
                state.BeginInline();
                WalkInline(el, state);
                state.EndInline();
                state.EndBlock();
                return;

            case "BR":
                state.WriteInlineRaw("  \n");
                return;

            case "HR":
                state.StartBlock();
                state.AppendLine("---");
                state.EndBlock();
                return;

            case "UL":
                EmitList(el, ordered: false, state);
                return;
            case "OL":
                EmitList(el, ordered: true, state);
                return;

            case "BLOCKQUOTE":
                EmitBlockquote(el, state);
                return;

            case "PRE":
                EmitPreformatted(el, state);
                return;

            case "TABLE":
                if (state.Options.EmitTables)
                {
                    EmitTable(el, state);
                    return;
                }
                break;

            case "DIV":
            case "SECTION":
            case "ARTICLE":
            case "MAIN":
            case "HEADER":
            case "FIGURE":
            case "FIGCAPTION":
                WalkBlock(el, state);
                return;
        }

        WalkBlock(el, state);
    }

    private static void EmitHeading(IElement el, int level, EmitState state)
    {
        state.StartBlock();
        state.AppendRaw(new string('#', level));
        state.AppendRaw(" ");
        state.BeginInline();
        WalkInline(el, state);
        state.EndInline();
        state.AppendRaw("\n");
        state.EndBlock();
    }

    private static void EmitList(IElement el, bool ordered, EmitState state)
    {
        if (state.ListDepth >= state.Options.MaxNestedListDepth) return;
        state.StartBlock();
        state.PushList(ordered);

        int index = 1;
        foreach (var child in el.Children)
        {
            if (!string.Equals(child.TagName, "LI", StringComparison.Ordinal)) continue;
            state.WriteListItemPrefix(index++);
            state.BeginInline();

            foreach (var inner in child.ChildNodes)
            {
                if (inner is IElement nested && (nested.TagName is "UL" or "OL"))
                {
                    state.EndInline();
                    state.AppendRaw("\n");
                    EmitElement(nested, state);
                    state.BeginInline();
                    continue;
                }
                if (inner.NodeType == NodeType.Element)
                    WalkInline((IElement)inner, state);
                else if (inner.NodeType == NodeType.Text)
                    state.WriteInlineText(inner.TextContent);
            }

            state.EndInline();
            state.AppendRaw("\n");
        }

        state.PopList();
        state.EndBlock();
    }

    private static void EmitBlockquote(IElement el, EmitState state)
    {
        state.StartBlock();
        state.PushQuote();
        WalkBlock(el, state);
        state.PopQuote();
        state.EndBlock();
    }

    private static void EmitPreformatted(IElement el, EmitState state)
    {
        var codeEl = el.QuerySelector("code");
        var language = ExtractLanguage(codeEl) ?? ExtractLanguage(el) ?? string.Empty;
        var text = (codeEl ?? el).TextContent ?? string.Empty;

        state.StartBlock();
        state.AppendRaw("```");
        if (!string.IsNullOrEmpty(language)) state.AppendRaw(language);
        state.AppendRaw("\n");
        state.AppendRaw(text.TrimEnd('\n'));
        state.AppendRaw("\n```\n");
        state.EndBlock();
    }

    private static string? ExtractLanguage(IElement? el)
    {
        if (el is null) return null;
        var cls = el.GetAttribute("class");
        if (string.IsNullOrEmpty(cls)) return null;
        foreach (var token in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("language-", StringComparison.Ordinal))
                return token.AsSpan("language-".Length).ToString();
            if (token.StartsWith("lang-", StringComparison.Ordinal))
                return token.AsSpan("lang-".Length).ToString();
        }
        return null;
    }

    private static void EmitTable(IElement el, EmitState state)
    {
        var rows = el.QuerySelectorAll("tr");
        if (rows.Length == 0) return;

        state.StartBlock();

        var headerCells = rows[0].Children.Where(c => c.TagName is "TH" or "TD").ToArray();
        if (headerCells.Length == 0) { state.EndBlock(); return; }

        WriteRow(headerCells, state);
        state.AppendRaw("|");
        for (int i = 0; i < headerCells.Length; i++) state.AppendRaw(" --- |");
        state.AppendRaw("\n");

        for (int r = 1; r < rows.Length; r++)
        {
            var cells = rows[r].Children.Where(c => c.TagName is "TH" or "TD").ToArray();
            if (cells.Length == 0) continue;
            WriteRow(cells, state);
        }

        state.EndBlock();
    }

    private static void WriteRow(IElement[] cells, EmitState state)
    {
        state.AppendRaw("|");
        foreach (var cell in cells)
        {
            state.AppendRaw(" ");
            state.BeginInline();
            WalkInline(cell, state);
            state.EndInlineTableCell();
            state.AppendRaw(" |");
        }
        state.AppendRaw("\n");
    }

    private static void WalkInline(INode node, EmitState state)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case NodeType.Text:
                    state.WriteInlineText(child.TextContent);
                    break;
                case NodeType.Element:
                    EmitInlineElement((IElement)child, state);
                    break;
            }
        }
    }

    private static void EmitInlineElement(IElement el, EmitState state)
    {
        switch (el.TagName)
        {
            case "STRONG":
            case "B":
                state.WriteInlineRaw("**");
                WalkInline(el, state);
                state.WriteInlineRaw("**");
                return;
            case "EM":
            case "I":
                state.WriteInlineRaw("*");
                WalkInline(el, state);
                state.WriteInlineRaw("*");
                return;
            case "CODE":
                state.WriteInlineRaw("`");
                state.WriteInlineRawNoEscape(el.TextContent);
                state.WriteInlineRaw("`");
                return;
            case "A":
                EmitLink(el, state);
                return;
            case "IMG":
                EmitImage(el, state);
                return;
            case "BR":
                state.WriteInlineRaw("  \n");
                return;
            case "DEL":
            case "S":
            case "STRIKE":
                state.WriteInlineRaw("~~");
                WalkInline(el, state);
                state.WriteInlineRaw("~~");
                return;
            case "SCRIPT":
            case "STYLE":
            case "NOSCRIPT":
            case "IFRAME":
            case "SVG":
                return;
            default:
                WalkInline(el, state);
                return;
        }
    }

    private static void EmitLink(IElement el, EmitState state)
    {
        var href = el.GetAttribute("href");
        if (string.IsNullOrEmpty(href))
        {
            WalkInline(el, state);
            return;
        }
        state.WriteInlineRaw("[");
        WalkInline(el, state);
        state.WriteInlineRaw("](");
        state.WriteInlineRawNoEscape(href!);
        var title = el.GetAttribute("title");
        if (!string.IsNullOrEmpty(title))
        {
            state.WriteInlineRaw(" \"");
            state.WriteInlineRawNoEscape(title!.Replace("\"", "\\\""));
            state.WriteInlineRaw("\"");
        }
        state.WriteInlineRaw(")");
    }

    private static void EmitImage(IElement el, EmitState state)
    {
        var src = el.GetAttribute("src");
        if (string.IsNullOrEmpty(src)) return;
        var alt = el.GetAttribute("alt") ?? string.Empty;
        state.WriteInlineRaw("![");
        state.WriteInlineRawNoEscape(alt);
        state.WriteInlineRaw("](");
        state.WriteInlineRawNoEscape(src!);
        state.WriteInlineRaw(")");
    }

    private static string EscapeYaml(string value) =>
        value.Contains(':') || value.Contains('#') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private sealed class EmitState
    {
        private readonly StringBuilder _sb;
        private readonly Stack<ListFrame> _lists = new();
        private int _quoteDepth;
        private bool _atLineStart = true;
        private bool _inInline;
        private bool _pendingBlockSeparator;

        public EmitState(StringBuilder sb, MarkdownEmitterOptions options)
        {
            _sb = sb;
            Options = options;
        }

        public MarkdownEmitterOptions Options { get; }

        public int ListDepth => _lists.Count;

        public string Result()
        {
            var s = _sb.ToString();
            return s.TrimEnd('\n', ' ') + "\n";
        }

        public void StartBlock()
        {
            if (_sb.Length > 0) _pendingBlockSeparator = true;
        }

        public void EndBlock()
        {
            _pendingBlockSeparator = true;
        }

        public void BeginInline()
        {
            FlushPendingSeparator();
            _inInline = true;
        }

        public void EndInline()
        {
            _inInline = false;
        }

        public void EndInlineTableCell() => _inInline = false;

        public void AppendLine(string text)
        {
            FlushPendingSeparator();
            WritePrefixIfAtLineStart();
            _sb.Append(text);
            _sb.Append('\n');
            _atLineStart = true;
        }

        public void AppendRaw(string text)
        {
            FlushPendingSeparator();
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (_atLineStart && c != '\n') WritePrefixIfAtLineStart();
                _sb.Append(c);
                _atLineStart = c == '\n';
            }
        }

        public void WriteInlineText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            FlushPendingSeparator();
            var normalized = CollapseWhitespace(text);
            if (normalized.Length == 0) return;
            WritePrefixIfAtLineStart();
            EscapeAndAppend(normalized);
        }

        public void WriteInlineRaw(string text)
        {
            FlushPendingSeparator();
            WritePrefixIfAtLineStart();
            _sb.Append(text);
            if (text.Length > 0) _atLineStart = text[^1] == '\n';
        }

        public void WriteInlineRawNoEscape(string text) => WriteInlineRaw(text);

        public void PushList(bool ordered) => _lists.Push(new ListFrame(ordered));

        public void PopList()
        {
            if (_lists.Count > 0) _lists.Pop();
        }

        public void WriteListItemPrefix(int oneBasedIndex)
        {
            FlushPendingSeparator();
            WritePrefixIfAtLineStart();
            for (int i = 0; i < _lists.Count - 1; i++) _sb.Append("  ");
            var top = _lists.Peek();
            if (top.Ordered)
            {
                _sb.Append(oneBasedIndex);
                _sb.Append(". ");
            }
            else
            {
                _sb.Append("- ");
            }
            _atLineStart = false;
        }

        public void PushQuote() => _quoteDepth++;

        public void PopQuote()
        {
            if (_quoteDepth > 0) _quoteDepth--;
        }

        private void FlushPendingSeparator()
        {
            if (!_pendingBlockSeparator) return;
            _pendingBlockSeparator = false;
            if (_sb.Length == 0) return;
            if (_sb[^1] != '\n') _sb.Append('\n');
            _sb.Append('\n');
            _atLineStart = true;
        }

        private void WritePrefixIfAtLineStart()
        {
            if (!_atLineStart) return;
            for (int i = 0; i < _quoteDepth; i++) _sb.Append("> ");
            _atLineStart = false;
        }

        private void EscapeAndAppend(string text)
        {
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': _sb.Append("\\\\"); break;
                    case '`': _sb.Append("\\`"); break;
                    case '*': _sb.Append("\\*"); break;
                    case '_': _sb.Append("\\_"); break;
                    case '[': _sb.Append("\\["); break;
                    case ']': _sb.Append("\\]"); break;
                    case '<': _sb.Append("&lt;"); break;
                    case '>': _sb.Append("&gt;"); break;
                    case '|': _sb.Append("\\|"); break;
                    default: _sb.Append(c); break;
                }
            }
        }

        private static string CollapseWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool lastWasSpace = false;
            foreach (var c in text)
            {
                if (c is '\n' or '\r' or '\t' or ' ')
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString();
        }

        private readonly record struct ListFrame(bool Ordered);
    }
}
