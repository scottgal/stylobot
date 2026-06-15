using System.Reflection;
using System.Xml.Linq;
using System.Xml.XPath;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Policies.Signals;

/// <summary>
///     Regressions for the operator-reported gaps in the
///     <c>&lt;vc:sb-all-signals show-descriptions="true" /&gt;</c> dashboard
///     view: missing XML doc summaries on <c>SignalKeys</c> constants surfaced
///     as "no catalog entry" in the Description column;
///     <see cref="MultiFactorSignatures"/> leaked the type name in the Value
///     column; and the Source column showed the SignalKeys class for every
///     row, giving no useful provenance.
/// </summary>
public class SbAllSignalsCatalogCoverageTests
{
    /// <summary>
    ///     Every <c>public const string</c> on <see cref="SignalKeys"/> must
    ///     have an XML <c>&lt;summary&gt;</c> so the dashboard Description
    ///     column has something to render. Catches the "operator sees an
    ///     undocumented signal" regression directly off the XML doc file the
    ///     SignalCatalog reads.
    /// </summary>
    [Fact]
    public void All_SignalKeys_constants_have_XML_doc_summary()
    {
        var keysType = typeof(SignalKeys);
        var fields = keysType
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToList();
        Assert.NotEmpty(fields);

        var docFile = Path.Combine(
            Path.GetDirectoryName(keysType.Assembly.Location)!,
            Path.GetFileNameWithoutExtension(keysType.Assembly.Location) + ".xml");
        Assert.True(File.Exists(docFile),
            $"XML doc file must exist for SignalCatalog to populate Short/Long; expected {docFile}");

        var doc = XDocument.Load(docFile);
        var missing = new List<string>();
        foreach (var field in fields)
        {
            var member = doc.XPathSelectElement(
                $"//member[@name='F:{keysType.FullName}.{field.Name}']/summary");
            if (member is null || string.IsNullOrWhiteSpace(member.Value))
                missing.Add(field.Name);
        }

        Assert.True(missing.Count == 0,
            $"Missing XML <summary> on SignalKeys constants: {string.Join(", ", missing)}");
    }

    /// <summary>
    ///     The Value formatter must not leak the <c>MultiFactorSignatures</c>
    ///     type name and must produce a shape-aware summary instead. Drives
    ///     the dashboard "signature.multifactor" row directly.
    /// </summary>
    [Fact]
    public void MultiFactorSignatures_renders_without_revealing_type_name()
    {
        var sigs = new MultiFactorSignatures
        {
            PrimarySignature = "abcdef0123456789",
            IpSignature = "11112222",
            UaSignature = "33334444",
            FactorCount = 3
        };

        var rendered = SignalValueFormatter.Format(sigs);

        Assert.DoesNotContain("Mostlylucid.BotDetection.Dashboard.MultiFactorSignatures",
            rendered);
        Assert.Contains("3 factors", rendered);
        Assert.Contains("abcdef012345", rendered);
    }

    /// <summary>
    ///     The Value formatter must produce stable, type-name-free output for
    ///     all the primitive / collection shapes the all-signals view actually
    ///     encounters (null, string, bool, number, dictionary, list).
    /// </summary>
    [Fact]
    public void Format_handles_primitives_and_collections_without_leaking_type_names()
    {
        Assert.Equal("null", SignalValueFormatter.Format(null));
        Assert.Equal("true", SignalValueFormatter.Format(true));
        Assert.Equal("false", SignalValueFormatter.Format(false));
        Assert.Equal("hello", SignalValueFormatter.Format("hello"));
        Assert.Equal("42", SignalValueFormatter.Format(42));
        Assert.Equal("3.14", SignalValueFormatter.Format(3.14));
        Assert.Equal("[a, b, c]", SignalValueFormatter.Format(new[] { "a", "b", "c" }));

        var dict = SignalValueFormatter.Format(new Dictionary<string, object> { ["a"] = 1, ["b"] = 2 });
        Assert.Equal("Dictionary(2 entries)", dict);
    }

    /// <summary>
    ///     The Source column must show the emitting detector (from the
    ///     manifest inverted index) rather than the useless SignalKeys class
    ///     name. Verifies the inverted-index path end-to-end against the
    ///     embedded manifests.
    /// </summary>
    [Fact]
    public async Task SignalCatalog_resolves_emitting_contributor_from_manifests()
    {
        var loader = new DetectorManifestLoader();
        loader.LoadEmbeddedManifests();

        var emittedBy = SignalEmissionIndex.Build(loader);
        Assert.NotEmpty(emittedBy);

        var asm = typeof(SignalKeys).Assembly;
#pragma warning disable IL2026
        var catalog = await SignalCatalog.LoadAsync(asm, sources: null, emittedBy: emittedBy);
#pragma warning restore IL2026

        // tls.is_https is declared in tls.detector.yaml on_complete -- emit
        // index must resolve to TlsFingerprintContributor.
        var tls = catalog.TryGet(SignalKeys.TlsIsHttps);
        Assert.NotNull(tls);
        Assert.Contains("TlsFingerprintContributor", tls!.EmittedBy);

        // h2.is_http2 is declared in http2.detector.yaml on_complete.
        var h2 = catalog.TryGet(SignalKeys.H2IsHttp2);
        Assert.NotNull(h2);
        Assert.Contains("Http2FingerprintContributor", h2!.EmittedBy);

        // header.count is declared in header.detector.yaml on_complete
        // (added by the SbAllSignals cohesion pass).
        var headerCount = catalog.TryGet(SignalKeys.HeaderCount);
        Assert.NotNull(headerCount);
        Assert.Contains("HeaderContributor", headerCount!.EmittedBy);
    }
}