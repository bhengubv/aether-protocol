// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class AetherDemoService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public AetherDemoService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    public void ClearLog() => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.AetherDemoClearLog, null);

    public async global::System.Threading.Tasks.Task GroupVideoAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.AetherDemoGroupVideo, null);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.AetherDemoService.NodeView> Nodes() => _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.AetherDemoService.NodeView>>(global::AetherNetNodeService.Ipc.NodeOp.AetherDemoNodes, null);

    public async global::System.Threading.Tasks.Task OneToOneVideoAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.AetherDemoOneToOneVideo, null);
    }

    public async global::System.Threading.Tasks.Task PublishGroupTextAsync(string message)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.AetherDemoPublishGroupText, new { message });
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.AetherDemoService.LogLine> Snapshot() => _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.AetherDemoService.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.AetherDemoSnapshot, null);

    public void Start() => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.AetherDemoStart, null);

    public void VerifyAetherTag() => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.AetherDemoVerifyAetherTag, null);

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventAetherDemo: Changed?.Invoke(); break;
        }
    }

    public enum LogKind
    {
        System = 0,
        Text = 1,
        Video = 2,
        Tag = 3,
        Warn = 4,
    }

    public record LogLine(string Who, string Color, string Text, global::AetherNet.Sample.Shared.Services.AetherDemoService.LogKind Kind);

    public record NodeView(string Name, string Uhid, string Tag, string Color, bool IsRelay, global::System.Collections.Generic.IReadOnlyList<string> Peers);
}
