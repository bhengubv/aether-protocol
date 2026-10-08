// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class PetnamesLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public PetnamesLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewPetnamesLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetPetnamesLab, global::AetherNetNodeService.Ipc.NodeOp.EventPetnamesLab);
    public string FormName
    {
        get => Now.FormName;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetPetnamesLabFormName, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPetnamesLab); }
    }
    public string FormTag
    {
        get => Now.FormTag;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetPetnamesLabFormTag, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPetnamesLab); }
    }
    public string? GossipOut => Now.GossipOut;
    public global::AetherNet.Sample.Shared.Services.Lab.PetnamesLabDemo.Registry Me => Now.Me;
    public string MyTag => Now.MyTag;
    public string? Note => Now.Note;
    public global::AetherNet.Sample.Shared.Services.Lab.PetnamesLabDemo.Registry Peer => Now.Peer;
    public string PeerTag => Now.PeerTag;
    public global::System.Collections.Generic.IReadOnlyList<string> People => Now.People;
    public string ResolveName
    {
        get => Now.ResolveName;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetPetnamesLabResolveName, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPetnamesLab); }
    }
    public string ResolveOut => Now.ResolveOut;

    public void Gossip()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PetnamesLabGossip, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPetnamesLab);
    }

    public void Pin()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PetnamesLabPin, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPetnamesLab);
    }

    public void ProposeAsPeer()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PetnamesLabProposeAsPeer, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPetnamesLab);
    }

    public void Reject(string tag)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PetnamesLabReject, new { tag }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPetnamesLab);
    }

    public void Resolve()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PetnamesLabResolve, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPetnamesLab);
    }

    /// <summary>Stop listening for what the demo says changed.</summary>
    public void Dispose() => _menu.Told -= OnTold;

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventPetnamesLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public string FormName { get; init; } = default!;
        public string FormTag { get; init; } = default!;
        public string? GossipOut { get; init; }
        public global::AetherNet.Sample.Shared.Services.Lab.PetnamesLabDemo.Registry Me { get; init; } = default!;
        public string MyTag { get; init; } = default!;
        public string? Note { get; init; }
        public global::AetherNet.Sample.Shared.Services.Lab.PetnamesLabDemo.Registry Peer { get; init; } = default!;
        public string PeerTag { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<string> People { get; init; } = default!;
        public string ResolveName { get; init; } = default!;
        public string ResolveOut { get; init; } = default!;
    }

    public record Binding(string Tag, string Name, bool Pinned, string Badge);

    public record Registry(global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PetnamesLabDemo.Binding> All);
}
