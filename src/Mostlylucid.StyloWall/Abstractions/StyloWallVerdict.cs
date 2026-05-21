namespace Mostlylucid.StyloWall.Abstractions;

public enum StyloWallTrigger
{
    None = 0,
    AcceptHeader,
    QueryString,
    RouteRule,
    DetectionVerdict
}

public readonly record struct StyloWallVerdict(string Mode, StyloWallTrigger Trigger)
{
    public static readonly StyloWallVerdict PassThrough = new(string.Empty, StyloWallTrigger.None);

    public bool ShouldTransform => Trigger != StyloWallTrigger.None && !string.IsNullOrEmpty(Mode);
}
