// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class AwareService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public AwareService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetAwareService, global::AetherNetNodeService.Ipc.NodeOp.EventAwareService);
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.AwareThing> MovingWithYou => Now.MovingWithYou;
    public global::AetherNetNodeService.AwareReport Report => Now.Report;

    public void Listen()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.AwareServiceListen, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventAwareService);
    }

    public async global::System.Threading.Tasks.Task RefreshAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.AwareServiceRefresh, null, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventAwareService);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventAwareService: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.AwareThing> MovingWithYou { get; init; } = default!;
        public global::AetherNetNodeService.AwareReport Report { get; init; } = default!;
    }
}
