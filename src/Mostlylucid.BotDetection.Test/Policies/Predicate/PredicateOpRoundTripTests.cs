using System.Text.Json;
using Mostlylucid.BotDetection.Policies.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Predicate;

/// <summary>
///     Round-trip coverage for every <see cref="PredicateOp"/> value through
///     the JSON wire form. The wire-form mapping itself lives in private
///     <c>OpToString</c> / <c>StringToOp</c> helpers inside
///     <see cref="PredicatePolymorphicConverter"/>; the public-surface way
///     to assert it round-trips is to serialise a <c>Term</c> carrying each
///     op and confirm the deserialised value matches.
///
///     The "all ops" canary guards against future ops being added to the
///     enum without a matching wire-form entry (the converter falls back to
///     <c>op.ToString().ToLowerInvariant()</c> on serialise but
///     <c>StringToOp</c> falls back to <see cref="PredicateOp.Eq"/> on
///     deserialise -- silent data corruption unless this test catches it).
/// </summary>
public class PredicateOpRoundTripTests
{
    [Fact]
    public void InCidr_round_trips_through_wire_form()
    {
        Mostlylucid.BotDetection.Policies.Predicate.Predicate p =
            new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
                "ip.address", PredicateOp.InCidr, "192.168.0.0/16");

        var json = JsonSerializer.Serialize(p, PredicateJson.Options);
        Assert.Contains("\"op\":\"in_cidr\"", json);

        var back = JsonSerializer.Deserialize<Mostlylucid.BotDetection.Policies.Predicate.Predicate>(
            json, PredicateJson.Options);
        Assert.Equal(p, back);
    }

    [Fact]
    public void All_predicate_ops_round_trip_through_wire_form()
    {
        foreach (var op in Enum.GetValues<PredicateOp>())
        {
            // Use a string facet value -- works for every op including set ops,
            // because the round-trip we're asserting here is on the OP wire form,
            // not on the value shape (other tests cover string[] value round-trip).
            Mostlylucid.BotDetection.Policies.Predicate.Predicate p =
                new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term(
                    "test.facet", op, "test-value");

            var json = JsonSerializer.Serialize(p, PredicateJson.Options);
            var back = JsonSerializer.Deserialize<Mostlylucid.BotDetection.Policies.Predicate.Predicate>(
                json, PredicateJson.Options);

            var backTerm = Assert.IsType<Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term>(back);
            Assert.Equal(op, backTerm.Op);
        }
    }
}