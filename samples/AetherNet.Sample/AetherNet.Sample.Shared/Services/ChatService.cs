// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class ChatService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public ChatService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetChat, global::AetherNetNodeService.Ipc.NodeOp.EventChat);
    public long AppShareSizeBytes => Now.AppShareSizeBytes;
    public bool CanShareApp => Now.CanShareApp;
    public string? CannotShareAppReason => Now.CannotShareAppReason;
    public global::System.Collections.Generic.IReadOnlyList<string> MutualContacts => Now.MutualContacts;

    private global::System.Func<global::System.Threading.Tasks.Task<global::AetherNet.Sample.Shared.Services.Handoff.Note?>>? _holding;

    /// <summary>What this page holds. The service is told what it is each time it is set.</summary>
    public global::System.Func<global::System.Threading.Tasks.Task<global::AetherNet.Sample.Shared.Services.Handoff.Note?>>? Holding
    {
        get => _holding;
        set
        {
            _holding = value;
            _ = TellAsync(value);
        }
    }

    private async global::System.Threading.Tasks.Task TellAsync(global::System.Func<global::System.Threading.Tasks.Task<global::AetherNet.Sample.Shared.Services.Handoff.Note?>>? ask)
        => await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.SetChatHolding, new { value = ask is null ? null : await ask() });
    public string? WhereIAm { set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetChatWhereIAm, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat); } }

    public async global::System.Threading.Tasks.Task AskForHandoffAsync(string peerTag, global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.ChatAskForHandoff, new { peerTag }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
    }

    public async global::System.Threading.Tasks.Task BurnIfSpentAsync(string messageId)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.ChatBurnIfSpent, new { messageId }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ChatMessage> Conversation(string peerTag)
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ChatMessage>>(global::AetherNetNodeService.Ipc.NodeOp.ChatConversation, new { peerTag }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public async global::System.Threading.Tasks.Task<global::AetherNet.Sample.Shared.Data.GroupRecord> CreateGroupAsync(string name, global::System.Collections.Generic.IEnumerable<string> members, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<global::AetherNet.Sample.Shared.Data.GroupRecord>(global::AetherNetNodeService.Ipc.NodeOp.ChatCreateGroup, new { name, members }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public async global::System.Threading.Tasks.Task EnsureSessionAsync(string peerTag, global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.ChatEnsureSession, new { peerTag }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
    }

    public global::AetherNet.Sample.Shared.Data.GroupRecord? Group(string id)
    {
        var answer = _menu.Call<global::AetherNet.Sample.Shared.Data.GroupRecord?>(global::AetherNetNodeService.Ipc.NodeOp.ChatGroup, new { id }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public global::System.Collections.Generic.IReadOnlyList<string> GroupMembers(string id)
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<string>>(global::AetherNetNodeService.Ipc.NodeOp.ChatGroupMembers, new { id }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.GroupRecord> Groups()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.GroupRecord>>(global::AetherNetNodeService.Ipc.NodeOp.ChatGroups, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public bool IsSecure(string peerTag)
    {
        var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.ChatIsSecure, new { peerTag }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ChatMessage> Latest()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ChatMessage>>(global::AetherNetNodeService.Ipc.NodeOp.ChatLatest, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public bool OpenEphemeral(string messageId)
    {
        var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.ChatOpenEphemeral, new { messageId }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public async global::System.Threading.Tasks.Task SendAsync(string peerTag, string text, int ephemeralKind = 0, long ephemeralWindowMs = 0, global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.ChatSend, new { peerTag, text, ephemeralKind, ephemeralWindowMs }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
    }

    public async global::System.Threading.Tasks.Task<bool> SendNoteAsync(string peerTag, byte[] bytes, string contentType, string name, string caption = "", int ephemeralKind = 0, long ephemeralWindowMs = 0, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.ChatSendNote, new { peerTag, bytes, contentType, name, caption, ephemeralKind, ephemeralWindowMs }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public async global::System.Threading.Tasks.Task<bool> SendNoteToGroupAsync(string groupId, byte[] bytes, string contentType, string name, string caption = "", int ephemeralKind = 0, long ephemeralWindowMs = 0, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.ChatSendNoteToGroup, new { groupId, bytes, contentType, name, caption, ephemeralKind, ephemeralWindowMs }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public async global::System.Threading.Tasks.Task SendToGroupAsync(string groupId, string text, int ephemeralKind = 0, long ephemeralWindowMs = 0, global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.ChatSendToGroup, new { groupId, text, ephemeralKind, ephemeralWindowMs }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
    }

    public async global::System.Threading.Tasks.Task<bool> SetRelayingAsync(bool on, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.ChatSetRelaying, new { on }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public async global::System.Threading.Tasks.Task<bool> ShareAppAsync(string peerTag, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.ChatShareApp, new { peerTag }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public async global::System.Threading.Tasks.Task SweepEphemeralAsync(string peerTag)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.ChatSweepEphemeral, new { peerTag }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
    }

    public global::AetherNet.Sample.Shared.Services.Handoff.Note? TakeArriving()
    {
        var answer = _menu.Call<global::AetherNet.Sample.Shared.Services.Handoff.Note?>(global::AetherNetNodeService.Ipc.NodeOp.ChatTakeArriving, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventChat);
        return answer;
    }

    public event global::System.Action? Changed;
    public event global::System.Action<string>? HandoffArrived;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventChat: Changed?.Invoke(); break;
            case global::AetherNetNodeService.Ipc.NodeOp.EventChatHandoffArrived: HandoffArrived?.Invoke(global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<string>(body)); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public long AppShareSizeBytes { get; init; }
        public bool CanShareApp { get; init; }
        public string? CannotShareAppReason { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<string> MutualContacts { get; init; } = default!;
    }
}
