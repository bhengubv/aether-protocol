// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class CallService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public CallService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetCall, global::AetherNetNodeService.Ipc.NodeOp.EventCall);
    public bool CanCall => Now.CanCall;
    public bool CanSendVideo => Now.CanSendVideo;
    public bool CanSwitchSpeaker => Now.CanSwitchSpeaker;
    public string? CannotCallReason => Now.CannotCallReason;
    public string? CannotSendVideoReason => Now.CannotSendVideoReason;
    public global::AetherNet.Voice.Models.VoiceCallSession? Current => Now.Current;
    public global::System.TimeSpan? Duration => Now.Duration;
    public bool HasCamera => Now.HasCamera;
    public bool IsMinimised => Now.IsMinimised;
    public bool IsMuted => Now.IsMuted;
    public bool LinkIsStruggling => Now.LinkIsStruggling;
    public string? PeerTag => Now.PeerTag;
    public bool SpeakerphoneOn
    {
        get => Now.SpeakerphoneOn;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetCallSpeakerphoneOn, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventCall); }
    }
    public bool TheirVideoOn => Now.TheirVideoOn;
    public bool VideoOn => Now.VideoOn;

    public async global::System.Threading.Tasks.Task AnswerAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.CallAnswer, null, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventCall);
    }

    public async global::System.Threading.Tasks.Task<bool> CallAsync(string peerTag, bool withVideo = false, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.CallCall, new { peerTag, withVideo }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventCall);
        return answer;
    }

    public async global::System.Threading.Tasks.Task DeclineAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.CallDecline, null, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventCall);
    }

    public async global::System.Threading.Tasks.Task HangUpAsync(global::AetherNet.Voice.Models.HangupReason reason = (global::AetherNet.Voice.Models.HangupReason)0, global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.CallHangUp, new { reason }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventCall);
    }

    public void SetMinimised(bool minimised)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.CallSetMinimised, new { minimised }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventCall);
    }

    public void SetMuted(bool muted)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.CallSetMuted, new { muted }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventCall);
    }

    public async global::System.Threading.Tasks.Task<bool> SetVideoAsync(bool on, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.CallSetVideo, new { on }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventCall);
        return answer;
    }

    public void SwitchCamera()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.CallSwitchCamera, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventCall);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventCall: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public bool CanCall { get; init; }
        public bool CanSendVideo { get; init; }
        public bool CanSwitchSpeaker { get; init; }
        public string? CannotCallReason { get; init; }
        public string? CannotSendVideoReason { get; init; }
        public global::AetherNet.Voice.Models.VoiceCallSession? Current { get; init; }
        public global::System.TimeSpan? Duration { get; init; }
        public bool HasCamera { get; init; }
        public bool IsMinimised { get; init; }
        public bool IsMuted { get; init; }
        public bool LinkIsStruggling { get; init; }
        public string? PeerTag { get; init; }
        public bool SpeakerphoneOn { get; init; }
        public bool TheirVideoOn { get; init; }
        public bool VideoOn { get; init; }
    }
}
