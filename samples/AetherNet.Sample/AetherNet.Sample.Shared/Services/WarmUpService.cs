// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class WarmUpService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public WarmUpService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetWarmUp, global::AetherNetNodeService.Ipc.NodeOp.EventWarmUp);
    public global::System.Collections.Generic.List<global::AetherNet.Mesh.RadioCapability> Found => Now.Found;
    public bool IsWarm => Now.IsWarm;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.WarmStep> Steps => Now.Steps;

    public async global::System.Threading.Tasks.Task WarmAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WarmUpWarm, null, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWarmUp);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventWarmUp: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::System.Collections.Generic.List<global::AetherNet.Mesh.RadioCapability> Found { get; init; } = default!;
        public bool IsWarm { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.WarmStep> Steps { get; init; } = default!;
    }
}

public record WarmStep(string Key, string Title, bool MayContinue = false)
{
    public global::AetherNet.Sample.Shared.Services.WarmState State { get; init; }
    public string? Detail { get; init; }
}

public enum WarmState
{
    Waiting = 0,
    Working = 1,
    Ready = 2,
    Absent = 3,
    Failed = 4,
    Continuing = 5,
}
