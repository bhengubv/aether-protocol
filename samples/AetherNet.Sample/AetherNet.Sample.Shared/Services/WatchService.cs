// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class WatchService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public WatchService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetWatch, global::AetherNetNodeService.Ipc.NodeOp.EventWatch);
    public global::AetherNet.Streaming.Models.WatchSession? Current => Now.Current;
    public bool IsHost => Now.IsHost;

    public async global::System.Threading.Tasks.Task FollowAsync(global::System.Guid sessionId, global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchFollow, new { sessionId }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatch);
    }

    public async global::System.Threading.Tasks.Task<global::AetherNet.Streaming.Models.WatchSession?> HostAsync(string contentRootHash, string title, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<global::AetherNet.Streaming.Models.WatchSession?>(global::AetherNetNodeService.Ipc.NodeOp.WatchHost, new { contentRootHash, title }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatch);
        return answer;
    }

    public async global::System.Threading.Tasks.Task LeaveAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchLeave, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatch);
    }

    public async global::System.Threading.Tasks.Task PauseAsync(long positionMs)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchPause, new { positionMs }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatch);
    }

    public async global::System.Threading.Tasks.Task PlayAsync(long positionMs)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchPlay, new { positionMs }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatch);
    }

    public async global::System.Threading.Tasks.Task ReactAsync(string reaction, long positionMs)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchReact, new { reaction, positionMs }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatch);
    }

    public async global::System.Threading.Tasks.Task SeekAsync(long positionMs)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WatchSeek, new { positionMs }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWatch);
    }

    public event global::System.Action? Changed;
    public event global::System.Action<global::AetherNet.Streaming.Models.WatchSession>? Invited;
    public event global::System.Action<global::AetherNet.Streaming.Models.WatchReactionPayload>? Reacted;
    public event global::System.Action<global::AetherNet.Streaming.Models.WatchSession>? Synced;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventWatch: Changed?.Invoke(); break;
            case global::AetherNetNodeService.Ipc.NodeOp.EventWatchInvited: Invited?.Invoke(global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<global::AetherNet.Streaming.Models.WatchSession>(body)); break;
            case global::AetherNetNodeService.Ipc.NodeOp.EventWatchReacted: Reacted?.Invoke(global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<global::AetherNet.Streaming.Models.WatchReactionPayload>(body)); break;
            case global::AetherNetNodeService.Ipc.NodeOp.EventWatchSynced: Synced?.Invoke(global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<global::AetherNet.Streaming.Models.WatchSession>(body)); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::AetherNet.Streaming.Models.WatchSession? Current { get; init; }
        public bool IsHost { get; init; }
    }
}
