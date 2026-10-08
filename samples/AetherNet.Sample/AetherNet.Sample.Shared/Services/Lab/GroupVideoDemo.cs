// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class GroupVideoDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public GroupVideoDemo(global::Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewGroupVideo, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetGroupVideo, global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo);
    public bool CallOpen => Now.CallOpen;
    public bool HasPending => Now.HasPending;
    public string? NextInvitee => Now.NextInvitee;
    public int SfuThreshold => Now.SfuThreshold;

    public async global::System.Threading.Tasks.Task AdmitNextAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.GroupVideoAdmitNext, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo);
    }

    public void ClearLog()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.GroupVideoClearLog, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo);
    }

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.GroupVideoDispose);
    }

    public global::AetherNet.Sample.Shared.Services.Lab.GroupVideoDemo.GroupView Group()
    {
        var answer = _menu.Call<global::AetherNet.Sample.Shared.Services.Lab.GroupVideoDemo.GroupView>(global::AetherNetNodeService.Ipc.NodeOp.GroupVideoGroup, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo);
        return answer;
    }

    public async global::System.Threading.Tasks.Task HangupAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.GroupVideoHangup, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo);
    }

    public global::AetherNet.Sample.Shared.Services.Lab.GroupVideoDemo.CallView? OneToOne()
    {
        var answer = _menu.Call<global::AetherNet.Sample.Shared.Services.Lab.GroupVideoDemo.CallView?>(global::AetherNetNodeService.Ipc.NodeOp.GroupVideoOneToOne, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo);
        return answer;
    }

    public async global::System.Threading.Tasks.Task OpenCallAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.GroupVideoOpenCall, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo);
    }

    public async global::System.Threading.Tasks.Task RingBobAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.GroupVideoRingBob, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.GroupVideoDemo.LogLine> Snapshot()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.GroupVideoDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.GroupVideoSnapshot, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo);
        return answer;
    }

    public void Start()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.GroupVideoStart, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventGroupVideo: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public bool CallOpen { get; init; }
        public bool HasPending { get; init; }
        public string? NextInvitee { get; init; }
        public int SfuThreshold { get; init; }
    }

    public record CallView(string State, string VideoCodec, string AudioCodec, string Resolution, int Fps, int BitrateKbps);

    public record GroupView(bool IsSfu, string Topology, string? RelayName, int ActiveCount, global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.GroupVideoDemo.ParticipantView> Participants);

    public record LogLine(string Who, string Color, string Text);

    public record ParticipantView(string Name, string Color, string Resolution, string Codec, bool IsHost, bool IsRelay);
}
