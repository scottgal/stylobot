using System.Globalization;
using System.Text;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Predicate;

/// <summary>
///     Recursive-descent parser for the policy predicate language.
///     <code>
///     expr     := or
///     or       := and ( "or" and )*
///     and      := sustain_unit ( "and" sustain_unit )*
///     sustain_unit := atom ( "for" duration )?
///     atom     := "(" expr ")"  |  "not" atom  |  term
///     term     := IDENT op value
///     op       := "=" | "!=" | "&gt;=" | "&gt;" | "&lt;=" | "&lt;"
///                | "in" | "not in"
///                | "between" scalar "and" scalar
///                | "matches" | "contains" | "any in" | "all in"
///     value    := list | scalar
///     list     := "(" scalar ("," scalar)* ")"
///     scalar   := NUMBER | STRING | BOOL | IDENT
///     duration := &lt;DurationParser shorthand&gt;  -- 30s, 5m, 1h, 1h30m, 1d, ...
///     </code>
///     <c>between A and B</c> is currently parsed but folded into a single
///     <see cref="PredicateOp.Between"/> term whose value is a string[]
///     <c>{ A, B }</c>; that is the same shape the formatter writes back.
///     <para>
///         T-Expr (<c>X FOR Yt</c>): any unit predicate may be followed by
///         <c>FOR</c> + a duration literal to wrap it in a
///         <see cref="Predicate.Sustain"/> AST node. <c>FOR</c> is
///         case-insensitive and reserved (it joins the keyword set alongside
///         <c>and</c>, <c>or</c>, <c>not</c>, ...). The duration literal is
///         consumed as a raw lexeme and parsed via
///         <see cref="DurationParser.TryParse"/> -- it must end with one of
///         <c>s</c>/<c>m</c>/<c>h</c>/<c>d</c> and may compose
///         (<c>1h30m</c>).
///     </para>
/// </summary>
public static class PredicateParser
{
    public static Predicate Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new PredicateParseException("input is empty", 0);

        var lexer = new Lexer(input);
        var result = ParseOr(lexer);

        var tail = lexer.Peek();
        if (tail.Kind != TokenKind.End)
            throw new PredicateParseException($"unexpected '{tail.Text}' after expression", tail.Position);

        return result;
    }

    private static Predicate ParseOr(Lexer lexer)
    {
        var left = ParseAnd(lexer);
        var children = new List<Predicate> { left };

        while (lexer.Peek() is { Kind: TokenKind.Keyword, Text: "or" })
        {
            lexer.Next();
            children.Add(ParseAnd(lexer));
        }

        return children.Count == 1 ? children[0] : new Predicate.Or(children.ToArray());
    }

    private static Predicate ParseAnd(Lexer lexer)
    {
        var left = ParseSustainUnit(lexer);
        var children = new List<Predicate> { left };

        while (lexer.Peek() is { Kind: TokenKind.Keyword, Text: "and" })
        {
            lexer.Next();
            children.Add(ParseSustainUnit(lexer));
        }

        return children.Count == 1 ? children[0] : new Predicate.And(children.ToArray());
    }

    /// <summary>
    ///     <c>sustain_unit := atom ( "for" duration )?</c>. Consumes an
    ///     <c>atom</c> first; if the next token is the reserved
    ///     <c>for</c> keyword, switches the lexer to "duration-literal" mode,
    ///     reads the next contiguous lexeme (digits/letters with no whitespace),
    ///     hands it to <see cref="DurationParser.TryParse" />, and wraps the
    ///     atom in a <see cref="Predicate.Sustain" /> node.
    /// </summary>
    private static Predicate ParseSustainUnit(Lexer lexer)
    {
        var inner = ParseAtom(lexer);

        var peek = lexer.Peek();
        if (peek is not { Kind: TokenKind.Keyword, Text: "for" }) return inner;
        lexer.Next(); // consume 'for'

        var (text, position) = lexer.ReadDurationLiteral();
        // Constrain to the SUFFIX grammar -- 30s / 5m / 1h / 1h30m -- so that
        // an HH:MM:SS literal or a bare integer doesn't sneak through via
        // DurationParser's TimeSpan.TryParse fallback. The last character must
        // be a unit ([smhd], case-insensitive) and DurationParser must succeed
        // with a strictly-positive value.
        var lastChar = text.Length > 0 ? text[text.Length - 1] : '\0';
        var unitOk = lastChar is 's' or 'S' or 'm' or 'M' or 'h' or 'H' or 'd' or 'D';
        if (!unitOk || !DurationParser.TryParse(text, out var duration) || duration <= TimeSpan.Zero)
            throw new PredicateParseException(
                $"invalid duration literal after 'for': '{text}' (expected e.g. 30s, 5m, 1h, 1h30m)",
                position);

        return new Predicate.Sustain(inner, duration);
    }

    private static Predicate ParseAtom(Lexer lexer)
    {
        var peek = lexer.Peek();

        if (peek.Kind == TokenKind.LParen)
        {
            lexer.Next();
            var inner = ParseOr(lexer);
            var close = lexer.Next();
            if (close.Kind != TokenKind.RParen)
                throw new PredicateParseException("expected ')' to close group", close.Position);
            return inner;
        }

        return ParseTerm(lexer);
    }

    private static Predicate.Term ParseTerm(Lexer lexer)
    {
        var ident = lexer.Next();
        if (ident.Kind != TokenKind.Ident)
            throw new PredicateParseException($"expected signal name, got '{ident.Text}'", ident.Position);

        var (op, isList, isBetween) = ReadOperator(lexer);

        object value;
        if (isList)
        {
            value = ReadList(lexer);
        }
        else if (isBetween)
        {
            var lo = ReadScalar(lexer);
            var andTok = lexer.Next();
            if (!(andTok.Kind == TokenKind.Keyword && andTok.Text == "and"))
                throw new PredicateParseException("expected 'and' between bounds of 'between'", andTok.Position);
            var hi = ReadScalar(lexer);
            value = new[] { ScalarToString(lo), ScalarToString(hi) };
        }
        else
        {
            value = ReadScalar(lexer);
        }

        return new Predicate.Term(ident.Text, op, value);
    }

    private static (PredicateOp op, bool isList, bool isBetween) ReadOperator(Lexer lexer)
    {
        var tok = lexer.Next();
        switch (tok.Kind)
        {
            case TokenKind.Eq: return (PredicateOp.Eq, false, false);
            case TokenKind.Neq: return (PredicateOp.Neq, false, false);
            case TokenKind.Gte: return (PredicateOp.Gte, false, false);
            case TokenKind.Gt: return (PredicateOp.Gt, false, false);
            case TokenKind.Lte: return (PredicateOp.Lte, false, false);
            case TokenKind.Lt: return (PredicateOp.Lt, false, false);

            case TokenKind.Keyword:
                return tok.Text switch
                {
                    "in" => (PredicateOp.In, true, false),
                    "matches" => (PredicateOp.Matches, false, false),
                    "contains" => (PredicateOp.Contains, false, false),
                    "between" => (PredicateOp.Between, false, true),
                    "not" => ReadNotPrefixed(lexer, tok.Position),
                    "any" => ReadAnyAllPrefixed(lexer, tok.Position, allMode: false),
                    "all" => ReadAnyAllPrefixed(lexer, tok.Position, allMode: true),
                    _ => throw new PredicateParseException($"expected operator, got '{tok.Text}'", tok.Position)
                };

            default:
                throw new PredicateParseException(
                    tok.Kind == TokenKind.End
                        ? "expected operator, but expression ended"
                        : $"expected operator, got '{tok.Text}'",
                    tok.Position);
        }
    }

    private static (PredicateOp op, bool isList, bool isBetween) ReadNotPrefixed(Lexer lexer, int notPos)
    {
        var follow = lexer.Next();
        if (follow.Kind == TokenKind.Keyword && follow.Text == "in")
            return (PredicateOp.NotIn, true, false);
        throw new PredicateParseException($"expected 'in' after 'not', got '{follow.Text}'", follow.Position);
    }

    private static (PredicateOp op, bool isList, bool isBetween) ReadAnyAllPrefixed(Lexer lexer, int prefixPos, bool allMode)
    {
        var follow = lexer.Next();
        if (follow.Kind == TokenKind.Keyword && follow.Text == "in")
            return (allMode ? PredicateOp.AllIn : PredicateOp.AnyIn, true, false);
        throw new PredicateParseException(
            $"expected 'in' after '{(allMode ? "all" : "any")}', got '{follow.Text}'",
            follow.Position);
    }

    private static object ReadScalar(Lexer lexer)
    {
        var tok = lexer.Next();
        return tok.Kind switch
        {
            TokenKind.Number => tok.Decimal ?? 0m,
            TokenKind.String => tok.Text,
            TokenKind.Ident => tok.Text,
            TokenKind.Keyword when tok.Text is "true" => true,
            TokenKind.Keyword when tok.Text is "false" => false,
            TokenKind.End => throw new PredicateParseException("expected value after operator", tok.Position),
            _ => throw new PredicateParseException($"expected value, got '{tok.Text}'", tok.Position)
        };
    }

    private static string ScalarToString(object value) =>
        value switch
        {
            bool b => b ? "true" : "false",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            string s => s,
            _ => value.ToString() ?? string.Empty
        };

    private static string[] ReadList(Lexer lexer)
    {
        var open = lexer.Next();
        if (open.Kind != TokenKind.LParen)
            throw new PredicateParseException("expected '(' to begin list", open.Position);

        var items = new List<string>();
        while (true)
        {
            var item = lexer.Next();
            if (item.Kind == TokenKind.RParen && items.Count == 0) break;          // empty list
            if (item.Kind == TokenKind.RParen)
                throw new PredicateParseException("expected value before ')'", item.Position);
            if (item.Kind != TokenKind.Ident && item.Kind != TokenKind.String && item.Kind != TokenKind.Number
                && !(item.Kind == TokenKind.Keyword && (item.Text == "true" || item.Text == "false")))
                throw new PredicateParseException($"expected list item, got '{item.Text}'", item.Position);

            items.Add(item.Kind == TokenKind.Number
                ? (item.Decimal?.ToString(CultureInfo.InvariantCulture) ?? "0")
                : item.Text);

            var sep = lexer.Next();
            if (sep.Kind == TokenKind.RParen) break;
            if (sep.Kind != TokenKind.Comma)
                throw new PredicateParseException($"expected ',' or ')' in list, got '{sep.Text}'", sep.Position);
        }

        return items.ToArray();
    }

    // ── lexer ──────────────────────────────────────────────────────────────

    private enum TokenKind { End, Ident, Number, String, Keyword, Eq, Neq, Gte, Gt, Lte, Lt, LParen, RParen, Comma }

    private sealed record Token(TokenKind Kind, string Text, int Position, decimal? Decimal = null);

    private sealed class Lexer
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "and", "or", "not", "in", "between", "matches", "contains", "true", "false", "any", "all", "for"
        };

        private readonly string _src;
        private int _pos;
        private Token? _peek;

        public Lexer(string src) { _src = src; }

        public Token Peek() => _peek ??= ReadNext();

        public Token Next()
        {
            var t = _peek ?? ReadNext();
            _peek = null;
            return t;
        }

        /// <summary>
        ///     Pull the next contiguous run of digits/dot/letters as a single
        ///     duration literal -- bypasses the normal Number/Ident split so
        ///     <c>1h30m</c> arrives at <see cref="DurationParser"/> intact.
        ///     Discards any token already buffered in <c>_peek</c> (the parser
        ///     only invokes this after consuming the <c>for</c> keyword, so
        ///     the buffer is guaranteed empty).
        /// </summary>
        public (string Text, int Position) ReadDurationLiteral()
        {
            _peek = null;
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
            var start = _pos;
            while (_pos < _src.Length
                   && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '.'))
                _pos++;
            return (_src.Substring(start, _pos - start), start);
        }

        private Token ReadNext()
        {
            // Skip whitespace
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;

            if (_pos >= _src.Length) return new Token(TokenKind.End, string.Empty, _pos);

            var start = _pos;
            var ch = _src[_pos];

            switch (ch)
            {
                case '(': _pos++; return new Token(TokenKind.LParen, "(", start);
                case ')': _pos++; return new Token(TokenKind.RParen, ")", start);
                case ',': _pos++; return new Token(TokenKind.Comma, ",", start);
                case '=': _pos++; return new Token(TokenKind.Eq, "=", start);
                case '!':
                    if (_pos + 1 < _src.Length && _src[_pos + 1] == '=') { _pos += 2; return new Token(TokenKind.Neq, "!=", start); }
                    throw new PredicateParseException("expected '!=', got '!'", start);
                case '>':
                    if (_pos + 1 < _src.Length && _src[_pos + 1] == '=') { _pos += 2; return new Token(TokenKind.Gte, ">=", start); }
                    _pos++; return new Token(TokenKind.Gt, ">", start);
                case '<':
                    if (_pos + 1 < _src.Length && _src[_pos + 1] == '=') { _pos += 2; return new Token(TokenKind.Lte, "<=", start); }
                    _pos++; return new Token(TokenKind.Lt, "<", start);
                case '"':
                    return ReadQuotedString(start);
            }

            if (char.IsDigit(ch) || (ch == '-' && _pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1])))
                return ReadNumber(start);

            if (IsIdentStart(ch))
                return ReadIdent(start);

            throw new PredicateParseException($"unexpected character '{ch}'", _pos);
        }

        private Token ReadQuotedString(int start)
        {
            _pos++; // skip opening quote
            var sb = new StringBuilder();
            while (_pos < _src.Length && _src[_pos] != '"')
            {
                if (_src[_pos] == '\\' && _pos + 1 < _src.Length)
                {
                    sb.Append(_src[_pos + 1]);
                    _pos += 2;
                    continue;
                }
                sb.Append(_src[_pos++]);
            }
            if (_pos >= _src.Length) throw new PredicateParseException("unterminated string", start);
            _pos++; // skip closing quote
            return new Token(TokenKind.String, sb.ToString(), start);
        }

        private Token ReadNumber(int start)
        {
            while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.' || _src[_pos] == '-'))
                _pos++;
            var text = _src.Substring(start, _pos - start);
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                throw new PredicateParseException($"invalid number '{text}'", start);
            return new Token(TokenKind.Number, text, start, d);
        }

        private Token ReadIdent(int start)
        {
            while (_pos < _src.Length && IsIdentPart(_src[_pos])) _pos++;
            var text = _src.Substring(start, _pos - start);
            return Keywords.Contains(text)
                ? new Token(TokenKind.Keyword, text.ToLowerInvariant(), start)
                : new Token(TokenKind.Ident, text, start);
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
    }
}
