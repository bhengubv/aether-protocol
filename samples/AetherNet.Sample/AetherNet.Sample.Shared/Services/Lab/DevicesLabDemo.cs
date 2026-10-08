// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class DevicesLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public DevicesLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewDevicesLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetDevicesLab, global::AetherNetNodeService.Ipc.NodeOp.EventDevicesLab);
    public global::AetherNet.Sample.Shared.Services.Lab.DevicesLabDemo.DeviceState BState => Now.BState;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.DevicesLabDemo.DeviceView> Devices => Now.Devices;
    public string? ForgeLinkInfo => Now.ForgeLinkInfo;
    public string? ForgeRevInfo => Now.ForgeRevInfo;
    public string? LinkInfo => Now.LinkInfo;
    public string? ReconInfo => Now.ReconInfo;
    public string? RevInfo => Now.RevInfo;
    public string UserTag => Now.UserTag;

    public void ForgeLink()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DevicesLabForgeLink, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDevicesLab);
    }

    public void ForgeRevoke()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DevicesLabForgeRevoke, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDevicesLab);
    }

    public void Link()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DevicesLabLink, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDevicesLab);
    }

    public void Reconcile()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DevicesLabReconcile, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDevicesLab);
    }

    public void Reset()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DevicesLabReset, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDevicesLab);
    }

    public void Revoke()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DevicesLabRevoke, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDevicesLab);
    }

    /// <summary>Stop listening for what the demo says changed.</summary>
    public void Dispose() => _menu.Told -= OnTold;

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventDevicesLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::AetherNet.Sample.Shared.Services.Lab.DevicesLabDemo.DeviceState BState { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.DevicesLabDemo.DeviceView> Devices { get; init; } = default!;
        public string? ForgeLinkInfo { get; init; }
        public string? ForgeRevInfo { get; init; }
        public string? LinkInfo { get; init; }
        public string? ReconInfo { get; init; }
        public string? RevInfo { get; init; }
        public string UserTag { get; init; } = default!;
    }

    public enum DeviceState
    {
        Unlinked = 0,
        Linked = 1,
        Revoked = 2,
    }

    public record DeviceView(string Name, global::AetherNet.Sample.Shared.Services.Lab.DevicesLabDemo.DeviceState State);
}
