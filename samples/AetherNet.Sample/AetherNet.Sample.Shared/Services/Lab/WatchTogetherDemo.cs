// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class WatchTogetherDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public WatchTogetherDemo(global::Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewWatchTogether, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetWatchTogether, global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
    public bool HasPool => Now.HasPool;
    public bool IsHosting => Now.IsHosting;
    public decimal PoolCollected => Now.PoolCollected;
    public bool PoolFunded => Now.PoolFunded;
    public decimal PoolTarget => Now.PoolTarget;
    public double Speed => Now.Speed;

    public async global::System.Threading.Tasks.Task ContributeAsync(string followerName, decimal amount)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherContribute, new { followerName, amount }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
    }

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherDispose);
    }

    public async global::System.Threading.Tasks.Task HostAndFollowAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherHostAndFollow, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.WatchTogetherDemo.NodeView> Nodes()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.WatchTogetherDemo.NodeView>>(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherNodes, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
        return answer;
    }

    public async global::System.Threading.Tasks.Task PauseAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherPause, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
    }

    public async global::System.Threading.Tasks.Task PlayAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherPlay, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
    }

    public async global::System.Threading.Tasks.Task ReactAsync(string followerName, string reaction)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherReact, new { followerName, reaction }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
    }

    public async global::System.Threading.Tasks.Task SeekForwardAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherSeekForward, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
    }

    public async global::System.Threading.Tasks.Task SetSpeedAsync(double speed)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherSetSpeed, new { speed }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.WatchTogetherDemo.LogLine> Snapshot()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.WatchTogetherDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherSnapshot, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
        return answer;
    }

    public void Start()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherStart, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
    }

    public async global::System.Threading.Tasks.Task StartChipInAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchTogetherStartChipIn, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventWatchTogether: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public bool HasPool { get; init; }
        public bool IsHosting { get; init; }
        public decimal PoolCollected { get; init; }
        public bool PoolFunded { get; init; }
        public decimal PoolTarget { get; init; }
        public double Speed { get; init; }
    }

    public record LogLine(string Who, string Color, string Text);

    public record NodeView(string Name, string Color, bool IsHost, int LatencyMs, long PositionMs, bool IsPlaying, double Speed, bool Following, long DeltaMs);
}
