using Mostlylucid.BotDetection.ClientSide;

namespace Mostlylucid.BotDetection.Identity.BrowserChar;

/// <summary>
///     Encodes the client-attested browser-characteristic observations
///     (<c>versionFeatures()</c> / <c>chromiumTriple()</c> / <c>engineProbes()</c>
///     from botdetection.js) into the shared <see cref="IdentityVectorLayout"/> so
///     seed priors and live observations live in the SAME vector space -- exactly
///     the guarantee <see cref="BrowserModes.ModeVectorEncoder"/> provides for the
///     mode centroids. Only the <c>client.feat.* / client.triple.* / client.eng.*</c>
///     Bool slots (added in layout v4) carry signal; every other slot stays zero, so
///     the cosine is a pure browser-characteristic delta.
///
///     <para>
///     Bool encoding follows the JS sentinels: observed <c>1</c> -&gt; <c>+1</c>
///     (present), <c>0</c> -&gt; <c>-1</c> (absent), anything else (<c>-1</c> errored
///     / block missing) -&gt; <c>0</c> (not observed, presence-gated out of the
///     cosine). <c>stackStyle</c> maps to two collision-proof bools.
///     </para>
/// </summary>
public static class BrowserCharVectorEncoder
{
    // Bool slot names in the v4 layout. Kept here (not magic strings scattered
    // through the encoder) so the seed source and the mask reference the same set.
    public static readonly string[] FeatureSlots =
    {
        "client.feat.popover", "client.feat.css_has", "client.feat.array_findlast",
        "client.feat.structured_clone", "client.feat.webgpu",
        "client.triple.view_tx", "client.triple.speculation", "client.triple.storage_access",
    };

    public static readonly string[] EngineSlots =
    {
        "client.eng.v8_break_iterator", "client.eng.error_capture_stack",
        "client.eng.stack_v8", "client.eng.stack_smjsc", "client.eng.regex_lookbehind",
        "client.eng.show_open_file_picker", "client.eng.user_agent_data",
    };

    /// <summary>1 present -&gt; +1, 0 absent -&gt; -1, else (errored / missing) -&gt; 0 (skipped).</summary>
    private static float Tri(int observed) => observed switch { 1 => 1f, 0 => -1f, _ => 0f };

    public static float[] Encode(
        IdentityVectorLayout layout, FeaturesBlock? feat, TripleBlock? triple, EngineBlock? engine)
    {
        var v = new float[layout.Dimension];
        void W(string name, float val)
        {
            var s = layout.FindSlot(name);
            if (s is not null) v[s.Offset] = val;
        }

        if (feat is not null)
        {
            W("client.feat.popover", Tri(feat.Popover));
            W("client.feat.css_has", Tri(feat.CssHas));
            W("client.feat.array_findlast", Tri(feat.ArrayFindLast));
            W("client.feat.structured_clone", Tri(feat.StructuredClone));
            W("client.feat.webgpu", Tri(feat.WebGpu));
        }

        if (triple is not null)
        {
            W("client.triple.view_tx", Tri(triple.HasViewTransition));
            W("client.triple.speculation", Tri(triple.HasSpeculationRules));
            W("client.triple.storage_access", Tri(triple.HasStorageAccess));
        }

        if (engine is not null)
        {
            W("client.eng.v8_break_iterator", Tri(engine.V8BreakIterator));
            W("client.eng.error_capture_stack", Tri(engine.ErrorCaptureStackTrace));
            W("client.eng.regex_lookbehind", Tri(engine.RegexLookbehind));
            W("client.eng.show_open_file_picker", Tri(engine.ShowOpenFilePicker));
            W("client.eng.user_agent_data", Tri(engine.UserAgentData));
            // stackStyle -> two collision-proof bools. v8 vs spidermonkey-jsc are
            // mutually exclusive; each asserts +1 for its family and -1 for the other,
            // "unknown" leaves both at 0.
            var isV8 = string.Equals(engine.StackStyle, "v8", System.StringComparison.Ordinal);
            var isSmJsc = string.Equals(engine.StackStyle, "spidermonkey-jsc", System.StringComparison.Ordinal);
            W("client.eng.stack_v8", isV8 ? 1f : (isSmJsc ? -1f : 0f));
            W("client.eng.stack_smjsc", isSmJsc ? 1f : (isV8 ? -1f : 0f));
        }

        return v;
    }

    /// <summary>
    ///     Per-dimension weight mask for the browser_char weighted cosine. The
    ///     ENGINE dims are weighted HIGH (un-spoofable substrate a fake cannot move)
    ///     and the FEATURE/triple dims LOW (spoofable, version-dependent), so poisoning
    ///     or version-drift on the feature dims cannot swing the verdict -- overview's
    ///     required anti-poisoning mask. Every non-browser-char dim is zero, so only
    ///     the browser-characteristic subspace contributes to the score.
    /// </summary>
    public static float[] BuildMask(IdentityVectorLayout layout, float engineWeight, float featureWeight)
    {
        var mask = new float[layout.Dimension];
        foreach (var name in EngineSlots)
        {
            var s = layout.FindSlot(name);
            if (s is not null) mask[s.Offset] = engineWeight;
        }
        foreach (var name in FeatureSlots)
        {
            var s = layout.FindSlot(name);
            if (s is not null) mask[s.Offset] = featureWeight;
        }
        return mask;
    }
}
