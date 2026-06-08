using System.Text.Json;
using Mostlylucid.BotDetection.Policies.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Predicate;

public class PredicateAstTests
{
    [Fact]
    public void Predicate_serialises_to_json_and_back()
    {
        Mostlylucid.BotDetection.Policies.Predicate.Predicate p = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.And(new Mostlylucid.BotDetection.Policies.Predicate.Predicate[]
        {
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("bot.type", PredicateOp.In, new[] { "scraper", "crawler" }),
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("score.bot_probability", PredicateOp.Gte, 0.7m)
        });

        var json = JsonSerializer.Serialize(p, PredicateJson.Options);
        var back = JsonSerializer.Deserialize<Mostlylucid.BotDetection.Policies.Predicate.Predicate>(json, PredicateJson.Options);

        Assert.Equal(p, back);
    }

    [Fact]
    public void Or_with_nested_and_round_trips()
    {
        Mostlylucid.BotDetection.Policies.Predicate.Predicate p = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Or(new Mostlylucid.BotDetection.Policies.Predicate.Predicate[]
        {
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("geo.country", PredicateOp.In, new[] { "CN", "RU" }),
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.And(new Mostlylucid.BotDetection.Policies.Predicate.Predicate[]
            {
                new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("bot.confirmed", PredicateOp.Eq, false),
                new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("score.confidence", PredicateOp.Gte, 0.8m)
            })
        });

        var json = JsonSerializer.Serialize(p, PredicateJson.Options);
        var back = JsonSerializer.Deserialize<Mostlylucid.BotDetection.Policies.Predicate.Predicate>(json, PredicateJson.Options);

        Assert.Equal(p, back);
    }

    [Fact]
    public void Term_with_scalar_value_round_trips()
    {
        Mostlylucid.BotDetection.Policies.Predicate.Predicate p = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("is_human", PredicateOp.Eq, true);
        var json = JsonSerializer.Serialize(p, PredicateJson.Options);
        var back = JsonSerializer.Deserialize<Mostlylucid.BotDetection.Policies.Predicate.Predicate>(json, PredicateJson.Options);
        Assert.Equal(p, back);
    }

    [Fact]
    public void Unknown_discriminator_throws_helpful_message()
    {
        var bad = """{"$kind":"Xor","children":[]}""";
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Mostlylucid.BotDetection.Policies.Predicate.Predicate>(bad, PredicateJson.Options));
        Assert.Contains("Xor", ex.Message);
    }
}
