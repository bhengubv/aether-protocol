// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class BroadcastDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public BroadcastDemo(global::Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewBroadcast, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetBroadcast, global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast);
    public long BandwidthKbps => Now.BandwidthKbps;
    public global::AetherNet.Streaming.BitrateRung? CurrentRung => Now.CurrentRung;
    public long FloorKbps => Now.FloorKbps;
    public bool IsLive => Now.IsLive;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Streaming.BitrateRung> Ladder => Now.Ladder;
    public int SegmentsPushed => Now.SegmentsPushed;
    public int SubscriberCount => Now.SubscriberCount;
    public bool WillAbandon => Now.WillAbandon;

    public void ClearLog()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.BroadcastClearLog, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast);
    }

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.BroadcastDispose);
    }

    public async global::System.Threading.Tasks.Task EndAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.BroadcastEnd, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast);
    }

    public async global::System.Threading.Tasks.Task GoLiveAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.BroadcastGoLive, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.BroadcastDemo.NodeView> Nodes()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.BroadcastDemo.NodeView>>(global::AetherNetNodeService.Ipc.NodeOp.BroadcastNodes, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast);
        return answer;
    }

    public async global::System.Threading.Tasks.Task PublishNextSegmentAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.BroadcastPublishNextSegment, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast);
    }

    public void SetBandwidth(long kbps)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.BroadcastSetBandwidth, new { kbps }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.BroadcastDemo.LogLine> Snapshot()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.BroadcastDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.BroadcastSnapshot, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast);
        return answer;
    }

    public void Start()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.BroadcastStart, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast);
    }

    public async global::System.Threading.Tasks.Task SubscribeAsync(string viewerName)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.BroadcastSubscribe, new { viewerName }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventBroadcast: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public long BandwidthKbps { get; init; }
        public global::AetherNet.Streaming.BitrateRung? CurrentRung { get; init; }
        public long FloorKbps { get; init; }
        public bool IsLive { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Streaming.BitrateRung> Ladder { get; init; } = default!;
        public int SegmentsPushed { get; init; }
        public int SubscriberCount { get; init; }
        public bool WillAbandon { get; init; }
    }

    public record LogLine(string Who, string Color, string Text);

    public record NodeView(string Name, string Color, bool IsPublisher, bool Subscribed, int SegmentsSeen, long LastSeq);
}
