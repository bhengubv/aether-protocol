// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class EridLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public EridLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewEridLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetEridLab, global::AetherNetNodeService.Ipc.NodeOp.EventEridLab);
    public string AliceErid => Now.AliceErid;
    public string Clock => Now.Clock;
    public long EpochNow => Now.EpochNow;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.EridLabDemo.Window> History => Now.History;
    public string? PrevErid => Now.PrevErid;
    public string? ResolveNow => Now.ResolveNow;
    public string? ResolvePrev => Now.ResolvePrev;
    public string RotatesIn => Now.RotatesIn;
    public string WindowLabel => Now.WindowLabel;

    public void Back()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.EridLabBack, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventEridLab);
    }

    public void Forward()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.EridLabForward, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventEridLab);
    }

    /// <summary>Stop listening for what the demo says changed.</summary>
    public void Dispose() => _menu.Told -= OnTold;

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventEridLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public string AliceErid { get; init; } = default!;
        public string Clock { get; init; } = default!;
        public long EpochNow { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.EridLabDemo.Window> History { get; init; } = default!;
        public string? PrevErid { get; init; }
        public string? ResolveNow { get; init; }
        public string? ResolvePrev { get; init; }
        public string RotatesIn { get; init; } = default!;
        public string WindowLabel { get; init; } = default!;
    }

    public record Window(long Epoch, string Erid, bool IsNow);
}
