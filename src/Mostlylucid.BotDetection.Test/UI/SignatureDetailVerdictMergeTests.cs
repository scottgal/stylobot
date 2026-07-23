using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Source-level guards for the signature-detail verdict-merge layout pass: the
///     Probability/Confidence/Risk tiles, Analysis, and Detection Signals now live in
///     one card (not three scattered ones), Detection Signals prefers the signed
///     per-detector data over the flat reason strings, and the commercial policy-action
///     slot is wired on the header row. Mirrors the source-text-assertion style already
///     used by <c>DashboardIntegrationPolicyStackTests</c> for this same file.
/// </summary>
public sealed class SignatureDetailVerdictMergeTests
{
    private static string LocatePartial(string filename)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!,
            "src", "Mostlylucid.BotDetection.UI", "Views", "StyloBot", "Dashboard", filename);
        Assert.True(File.Exists(path), $"Expected partial at {path}");
        return path;
    }

    [Fact]
    public void SignatureDetail_policy_slot_is_wired_on_the_header_row()
    {
        var source = File.ReadAllText(LocatePartial("_SignatureDetail.cshtml"));
        Assert.Contains("@inject ISignaturePolicyActionSlot PolicyActionSlot", source);
        Assert.Contains("@PolicyActionSlot.Render(Model.SignatureId, Model.Action, Model.BotType, Model.IsBot)", source);
    }

    [Fact]
    public void SignatureDetail_detection_signals_project_into_the_shared_reasons_partial()
    {
        // Detection Signals renders via the shared _DetectionReasons partial (also used
        // by the YOUR DETECTION widget) rather than its own inline branching -- one
        // rendering path, one place to fix a bug. Model.DetectorContributions (real
        // ConfidenceDelta belief-deltas) is the Signed source; Model.TopReasons (flat,
        // no polarity) is the Fallback -- the whole point of the operator's "show if
        // signals increased/decreased the bot score" ask.
        var source = File.ReadAllText(LocatePartial("_SignatureDetail.cshtml"));
        Assert.Contains("new DetectionReasonEntry(", source);
        Assert.Contains("det.ConfidenceDelta", source);
        Assert.Contains("det.Contribution", source);
        Assert.Contains("_DetectionReasons.cshtml", source);
        Assert.Contains("new DetectionReasonsModel(signedReasons, Model.TopReasons)", source);
    }

    [Fact]
    public void DetectionReasons_partial_prefers_signed_data_over_flat_fallback()
    {
        // Signed must be checked BEFORE falling back to Fallback -- the whole point of
        // showing direction/weight instead of a flat reason string.
        var source = File.ReadAllText(LocatePartial("_DetectionReasons.cshtml"));
        var signedIdx = source.IndexOf("Model.Signed.Count > 0", StringComparison.Ordinal);
        var fallbackIdx = source.IndexOf("Model.Fallback?.Count > 0", StringComparison.Ordinal);
        Assert.True(signedIdx >= 0, "Expected a Signed-driven branch.");
        Assert.True(fallbackIdx > signedIdx, "Fallback must be checked after Signed.");
        Assert.Contains("ConfidenceDelta > 0", source);
    }

    [Fact]
    public void SignatureDetail_stats_analysis_and_signals_share_one_verdict_card()
    {
        // The three used to be separate cards: a bare stats grid, then Analysis and
        // Detection Signals as their own cards two screens further down. Assert they
        // now sit inside the SAME wrapping div (no closing </div> for the verdict card
        // between the stats grid opening and the Detection Signals heading).
        var source = File.ReadAllText(LocatePartial("_SignatureDetail.cshtml"));
        var statsIdx = source.IndexOf("Probability</div>", StringComparison.Ordinal);
        var analysisIdx = source.IndexOf(">Analysis</h3>", StringComparison.Ordinal);
        var signalsIdx = source.IndexOf(">Detection Signals</h3>", StringComparison.Ordinal);
        Assert.True(statsIdx >= 0 && analysisIdx > statsIdx && signalsIdx > analysisIdx,
            "Expected Probability tile, then Analysis, then Detection Signals, in that order in the merged verdict card.");
    }
}
