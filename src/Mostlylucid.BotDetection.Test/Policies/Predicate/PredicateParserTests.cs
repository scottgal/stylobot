using Mostlylucid.BotDetection.Policies.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Predicate;

public class PredicateParserTests
{
    [Theory]
    [InlineData("bot.type in (scraper, crawler)")]
    [InlineData("score.bot_probability >= 0.7")]
    [InlineData("bot.type in (scraper, crawler) and score.bot_probability >= 0.7")]
    [InlineData("geo.country in (CN, RU) or bot.confirmed = false")]
    [InlineData("is_human = true")]
    [InlineData("session.requests_in_window between 10 and 20")]
    [InlineData("ua.family not in (curl, wget)")]
    public void Round_trip_text_ast_text(string expr)
    {
        var ast = PredicateParser.Parse(expr);
        var formatted = PredicateFormatter.Format(ast);
        Assert.Equal(expr, formatted);
    }

    [Fact]
    public void Or_inside_and_gets_parenthesised_on_format()
    {
        var ast = PredicateParser.Parse("bot.type = scraper and (score.bot_probability >= 0.7 or score.confidence >= 0.9)");
        var formatted = PredicateFormatter.Format(ast);
        Assert.Equal("bot.type = scraper and (score.bot_probability >= 0.7 or score.confidence >= 0.9)", formatted);
    }

    [Fact]
    public void And_inside_or_does_not_need_parens()
    {
        var ast = PredicateParser.Parse("bot.type = scraper and score.bot_probability >= 0.7 or geo.country = US");
        var formatted = PredicateFormatter.Format(ast);
        Assert.Equal("bot.type = scraper and score.bot_probability >= 0.7 or geo.country = US", formatted);
    }

    [Theory]
    [InlineData("bot.type ?? (scraper)",                  "unexpected character")]
    [InlineData("score.bot_probability >=",               "expected value after operator")]
    [InlineData("",                                        "input is empty")]
    [InlineData("bot.type in (",                           "expected list item")]
    [InlineData("bot.type in scraper",                     "expected '(' to begin list")]
    public void Parse_errors_have_position(string expr, string substring)
    {
        var ex = Assert.Throws<PredicateParseException>(() => PredicateParser.Parse(expr));
        Assert.Contains(substring, ex.Message);
        Assert.True(ex.Position >= 0, $"position was {ex.Position}");
    }

    [Fact]
    public void Unknown_op_token_reports_position()
    {
        var ex = Assert.Throws<PredicateParseException>(() => PredicateParser.Parse("bot.type ?? scraper"));
        Assert.Equal(9, ex.Position);
    }

    [Fact]
    public void Quoted_string_value_is_unquoted_in_ast()
    {
        var ast = PredicateParser.Parse("ua.raw = \"Mozilla/5.0\"") as Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term;
        Assert.NotNull(ast);
        Assert.Equal("Mozilla/5.0", ast!.Value);
    }
}
