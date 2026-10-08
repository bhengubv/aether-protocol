// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class DtnLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public DtnLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewDtnLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetDtnLab, global::AetherNetNodeService.Ipc.NodeOp.EventDtnLab);
    public bool HasMessage => Now.HasMessage;
    public bool RecipientOnline => Now.RecipientOnline;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.DtnLabDemo.NodeView> View => Now.View;

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DtnLabDispose);
    }

    public async global::System.Threading.Tasks.Task ExpireDemoAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.DtnLabExpireDemo, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDtnLab);
    }

    public async global::System.Threading.Tasks.Task LeaveMessageAsync(string message)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.DtnLabLeaveMessage, new { message }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDtnLab);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.DtnLabDemo.LogLine> Log()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.DtnLabDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.DtnLabLog, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDtnLab);
        return answer;
    }

    public async global::System.Threading.Tasks.Task RecipientReturnsAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.DtnLabRecipientReturns, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDtnLab);
    }

    public async global::System.Threading.Tasks.Task ReplicateAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.DtnLabReplicate, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDtnLab);
    }

    public void Start()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DtnLabStart, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDtnLab);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventDtnLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public bool HasMessage { get; init; }
        public bool RecipientOnline { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.DtnLabDemo.NodeView> View { get; init; } = default!;
    }

    public record HeldBundle(string Id8, string Status, string Text);

    public record LogLine(string Text, bool Strong);

    public record NodeView(string Name, bool Online, global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.DtnLabDemo.HeldBundle> Held);
}
