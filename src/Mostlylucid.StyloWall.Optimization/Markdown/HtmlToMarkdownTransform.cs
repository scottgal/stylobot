using System.Text;
using Microsoft.Extensions.Options;
using Mostlylucid.StyloWall.Abstractions;

namespace Mostlylucid.StyloWall.Optimization.Markdown;

public sealed class HtmlToMarkdownTransform : IResponseTransform
{
    private readonly IOptionsMonitor<MarkdownEmitterOptions> _options;

    public HtmlToMarkdownTransform(IOptionsMonitor<MarkdownEmitterOptions> options)
    {
        _options = options;
    }

    public string Mode => "markdown";

    public bool CanTransform(ResponseTransformContext context)
    {
        var ct = context.SourceContentType;
        return ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
               || ct.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask<TransformResult> TransformAsync(
        ResponseTransformContext context,
        CancellationToken cancellationToken)
    {
        var encoding = ResolveEncoding(context.SourceEncoding);
        var html = encoding.GetString(context.SourceBody.Span);

        var emitter = new MarkdownEmitter(_options.CurrentValue);
        var markdown = await emitter.EmitAsync(html, cancellationToken);

        var bytes = Encoding.UTF8.GetBytes(markdown);
        return new TransformResult(bytes, "text/markdown; charset=utf-8", "utf-8");
    }

    private static Encoding ResolveEncoding(string name)
    {
        try { return Encoding.GetEncoding(name); }
        catch { return Encoding.UTF8; }
    }
}
