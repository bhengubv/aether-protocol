// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Cast;

public record CastStatus(string State, long PositionMs, long DurationMs)
{
    public bool IsPlaying { get; init; }
    public bool IsPaused { get; init; }
    public bool IsBuffering { get; init; }
    public bool IsStopped { get; init; }
}

public record CastTarget(string Id, string Name, global::AetherNet.Sample.Shared.Services.Cast.CastKind Kind, string? ControlUrl = null);

public enum CastKind
{
    Aether = 0,
    Dlna = 1,
}
