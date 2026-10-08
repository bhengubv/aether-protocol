// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class SosService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public SosService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetSos, global::AetherNetNodeService.Ipc.NodeOp.EventSos);
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Models.SosAlert> Active => Now.Active;
    public bool CanSend => Now.CanSend;

    public async global::System.Threading.Tasks.Task MarkSafeAsync(global::System.Guid id)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.SosMarkSafe, new { id }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSos);
    }

    public async global::System.Threading.Tasks.Task<bool> SendNearbyAsync(string? message, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.SosSendNearby, new { message }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSos);
        return answer;
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventSos: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Models.SosAlert> Active { get; init; } = default!;
        public bool CanSend { get; init; }
    }
}
