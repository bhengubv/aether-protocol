// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class GroupCallService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public GroupCallService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetGroupCall, global::AetherNetNodeService.Ipc.NodeOp.EventGroupCall);
    public bool CameraOn => Now.CameraOn;
    public bool CanSendVideo => Now.CanSendVideo;
    public string? CannotSendVideoReason => Now.CannotSendVideoReason;
    public string? GroupId => Now.GroupId;
    public bool IsRinging => Now.IsRinging;
    public bool Joined => Now.Joined;
    public global::System.Collections.Generic.IReadOnlyList<string> OnCamera => Now.OnCamera;
    public global::System.Collections.Generic.IReadOnlyList<string> Participants => Now.Participants;

    public async global::System.Threading.Tasks.Task DeclineAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.GroupCallDecline, null, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupCall);
    }

    public async global::System.Threading.Tasks.Task<bool> JoinAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.GroupCallJoin, null, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupCall);
        return answer;
    }

    public async global::System.Threading.Tasks.Task LeaveAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.GroupCallLeave, null, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupCall);
    }

    public async global::System.Threading.Tasks.Task<bool> SetCameraAsync(bool on, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.GroupCallSetCamera, new { on }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupCall);
        return answer;
    }

    public async global::System.Threading.Tasks.Task<bool> StartAsync(string groupId, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.GroupCallStart, new { groupId }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventGroupCall);
        return answer;
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventGroupCall: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public bool CameraOn { get; init; }
        public bool CanSendVideo { get; init; }
        public string? CannotSendVideoReason { get; init; }
        public string? GroupId { get; init; }
        public bool IsRinging { get; init; }
        public bool Joined { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<string> OnCamera { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<string> Participants { get; init; } = default!;
    }
}
