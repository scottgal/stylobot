using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.SignalShingle.AspNetCore;

/// <summary>
/// Wraps an HTML shingle in an Alpine/SignalR-aware island. The tag body is used while the
/// materializer has not produced a value; warmed cache content replaces only the island body.
/// </summary>
[HtmlTargetElement("signal-shingle")]
public sealed class SignalShingleTagHelper(
    ISignalShingleCache<string, string> cache,
    SignalShingleUiOptions options) : TagHelper
{
    [HtmlAttributeName("key")] public required string Key { get; set; }
    [HtmlAttributeName("consumer")] public string? Consumer { get; set; }
    [HtmlAttributeName("refresh-seconds")] public int RefreshSeconds { get; set; } = 60;
    [HtmlAttributeName("lease-seconds")] public int LeaseSeconds { get; set; } = 120;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var demand = SignalShingleDemand.Create(Consumer ?? Key, TimeSpan.FromSeconds(RefreshSeconds),
            TimeSpan.FromSeconds(LeaseSeconds));
        var read = cache.Read(Key, demand);
        output.TagName = "div";
        output.Attributes.SetAttribute("data-signal-shingle-key", Key);
        output.Attributes.SetAttribute("data-signal-shingle-endpoint", options.EndpointPrefix);
        output.Attributes.SetAttribute("data-signal-shingle-hub", options.HubPath);
        output.Attributes.SetAttribute("x-data", "signalShingle()") ;
        output.Attributes.SetAttribute("x-init", "connect($el)");
        if (read.IsWarm) output.Content.SetHtmlContent(new HtmlString(read.Value!));
        else output.Content.SetHtmlContent(await output.GetChildContentAsync());
    }
}
