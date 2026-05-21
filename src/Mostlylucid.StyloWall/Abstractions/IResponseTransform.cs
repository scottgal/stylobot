namespace Mostlylucid.StyloWall.Abstractions;

public interface IResponseTransform
{
    string Mode { get; }

    bool CanTransform(ResponseTransformContext context);

    ValueTask<TransformResult> TransformAsync(ResponseTransformContext context, CancellationToken cancellationToken);
}
