using Microsoft.AspNetCore.Http;

namespace Mostlylucid.StyloWall.Abstractions;

public sealed class ResponseTransformContext
{
    public required HttpContext Http { get; init; }
    public required StyloWallVerdict Verdict { get; init; }
    public required ReadOnlyMemory<byte> SourceBody { get; init; }
    public required string SourceContentType { get; init; }
    public required string SourceEncoding { get; init; }
}

public readonly record struct TransformResult(
    ReadOnlyMemory<byte> Body,
    string ContentType,
    string Encoding)
{
    public static TransformResult PassThrough(ResponseTransformContext ctx) =>
        new(ctx.SourceBody, ctx.SourceContentType, ctx.SourceEncoding);
}
