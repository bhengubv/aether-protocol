// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class PttScreenShareDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public PttScreenShareDemo(global::Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewPttScreenShare, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetPttScreenShare, global::AetherNetNodeService.Ipc.NodeOp.EventPttScreenShare);
    public global::AetherNet.Sample.Shared.Services.Lab.PttScreenShareDemo.HeaderView? LastHeader => Now.LastHeader;

    public void ClearLog()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PttScreenShareClearLog, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPttScreenShare);
    }

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PttScreenShareDispose);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PttScreenShareDemo.NodeView> Nodes()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PttScreenShareDemo.NodeView>>(global::AetherNetNodeService.Ipc.NodeOp.PttScreenShareNodes, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPttScreenShare);
        return answer;
    }

    public async global::System.Threading.Tasks.Task PushToTalkAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.PttScreenSharePushToTalk, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPttScreenShare);
    }

    public async global::System.Threading.Tasks.Task ShareScreenAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.PttScreenShareShareScreen, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPttScreenShare);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PttScreenShareDemo.LogLine> Snapshot()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PttScreenShareDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.PttScreenShareSnapshot, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPttScreenShare);
        return answer;
    }

    public void Start()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PttScreenShareStart, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPttScreenShare);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventPttScreenShare: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::AetherNet.Sample.Shared.Services.Lab.PttScreenShareDemo.HeaderView? LastHeader { get; init; }
    }

    public record HeaderView(string Kind, string CallIdShort, string CallIdHex, uint Sequence, string SeqHex, long TimestampMs, string TsHex, string FlagLabel, string FlagHex, int PayloadLen);

    public record LogLine(string Who, string Color, string Text);

    public record NodeView(string Name, string Color, bool IsSender, int PttReceived, int ScreenReceived);
}
