using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Atoms;
using Mostlylucid.BotDetection.UI.Options;

namespace Mostlylucid.BotDetection.Test.UI.Atoms;

public sealed class PolicyStackHitAtomTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch);

    private PolicyStackHitAtom NewAtom(int maxScopes = 16, TimeSpan? window = null)
        => new(_clock, Options.Create(new PolicyStackHitAtomOptions
        {
            MaxScopes = maxScopes,
            RetentionWindow = window ?? TimeSpan.FromMinutes(2),
            AgeOutTick = TimeSpan.FromMinutes(1),
        }));

    [Fact]
    public void Snapshot_Returns_Counts_Per_Intent()
    {
        var atom = NewAtom();
        atom.Record("global", PolicyIntentKind.Block);
        atom.Record("global", PolicyIntentKind.Block);
        atom.Record("global", PolicyIntentKind.Allow);

        var snapshot = atom.Snapshot("global", TimeSpan.FromMinutes(1));

        Assert.Equal(2, snapshot.Counts[PolicyIntentKind.Block]);
        Assert.Equal(1, snapshot.Counts[PolicyIntentKind.Allow]);
    }

    [Fact]
    public void Bounded_Growth_Evicts_Lru()
    {
        var atom = NewAtom(maxScopes: 2);
        atom.Record("a", PolicyIntentKind.Block);
        _clock.Advance(TimeSpan.FromSeconds(1));
        atom.Record("b", PolicyIntentKind.Block);
        _clock.Advance(TimeSpan.FromSeconds(1));
        atom.Record("c", PolicyIntentKind.Block);

        Assert.Equal(2, atom.ScopeCount);
    }

    [Fact]
    public void Age_Out_Removes_Records_Older_Than_Window()
    {
        var atom = NewAtom(window: TimeSpan.FromMinutes(2));
        atom.Record("global", PolicyIntentKind.Block);

        _clock.Advance(TimeSpan.FromMinutes(5));
        atom.AgeOut();

        var snapshot = atom.Snapshot("global", TimeSpan.FromMinutes(2));
        Assert.Equal(0, snapshot.Counts.GetValueOrDefault(PolicyIntentKind.Block));
    }
}
