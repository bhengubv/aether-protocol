// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Data;

public sealed class AetherStore
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public AetherStore(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
    }

    public int CountMissed() => _menu.Call<int>(global::AetherNetNodeService.Ipc.NodeOp.StoreCountMissed, null);

    public global::AetherNet.Sample.Shared.Data.Account GetAccount() => _menu.Call<global::AetherNet.Sample.Shared.Data.Account>(global::AetherNetNodeService.Ipc.NodeOp.StoreGetAccount, null);

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.CallRecord> GetCalls(int limit = 200) => _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.CallRecord>>(global::AetherNetNodeService.Ipc.NodeOp.StoreGetCalls, new { limit });

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ContactRecord> GetContacts() => _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ContactRecord>>(global::AetherNetNodeService.Ipc.NodeOp.StoreGetContacts, null);

    public bool GetFlag(string key) => _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.StoreGetFlag, new { key });

    public global::AetherNet.Sample.Shared.Data.GroupRecord? GetGroup(string groupId) => _menu.Call<global::AetherNet.Sample.Shared.Data.GroupRecord?>(global::AetherNetNodeService.Ipc.NodeOp.StoreGetGroup, new { groupId });

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ChatMessage> GetMessages(string peerTag, int limit = 500) => _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ChatMessage>>(global::AetherNetNodeService.Ipc.NodeOp.StoreGetMessages, new { peerTag, limit });

    public string? GetSetting(string key) => _menu.Call<string?>(global::AetherNetNodeService.Ipc.NodeOp.StoreGetSetting, new { key });

    public void SaveAccount(string displayName, string avatar) => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.StoreSaveAccount, new { displayName, avatar });

    public void SetFlag(string key, bool @value) => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.StoreSetFlag, new { key, @value });

    public void SetRecoveryBackedUp(bool backedUp) => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.StoreSetRecoveryBackedUp, new { backedUp });

    public void SetSetting(string key, string @value) => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.StoreSetSetting, new { key, @value });

    public void WipeAll() => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.StoreWipeAll, null);
}

public record Account(string DisplayName, string Avatar, long RecoveryBackedUpMs)
{
    public bool RecoveryBackedUp { get; init; }
}

public record CallRecord(string Id, string PeerTag, bool Outgoing, long StartedMs, long ConnectedMs, long EndedMs, string Reason)
{
    public bool Missed { get; init; }
    public bool Connected { get; init; }
    public global::System.TimeSpan? Duration { get; init; }
    public global::System.DateTimeOffset StartedAt { get; init; }
}

public record ChatMessage(string Id, string PeerTag, string Body, bool Mine, string State, long SentMs, string? SenderTag = null, string? AttachmentHash = null, string? AttachmentType = null, long AttachmentBytes = 0, int EphemeralKind = 0, long EphemeralWindowMs = 0, int EphemeralViews = 0, long EphemeralStartedMs = 0, bool EphemeralSpent = false)
{
    public const int EphNone = 0;
    public const int EphOnce = 1;
    public const int EphTwice = 2;
    public const int EphTimeout = 3;
    public const long EphemeralMaxWindowMs = 300000;
    public const string VoiceNote = "audio/ogg";
    public const string VoiceNoteAac = "audio/mp4";
    public const string VideoNote = "video/mp4";
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string Received = "received";
    public bool HasAttachment { get; init; }
    public bool IsVoiceNote { get; init; }
    public bool IsVideoNote { get; init; }
    public bool IsPhoto { get; init; }
    public bool IsFile { get; init; }
    public bool IsEphemeral { get; init; }
    public int EphemeralViewLimit { get; init; }
    public int EphemeralViewsLeft { get; init; }
    public bool EphemeralTimerRunning { get; init; }
    public long EphemeralExpiresAtMs { get; init; }
    public string EphemeralLabel { get; init; } = default!;
}

public record ContactRecord(string Tag, string DisplayName, byte[]? PublicKey, bool AddedByMe, bool AddedByThem, string AddedVia, long FirstSeenMs, long LastSeenMs)
{
    public bool IsMutual { get; init; }
    public bool IsIncoming { get; init; }
    public bool IsPending { get; init; }
}

public record GroupRecord(string Id, string Name, string AdminTag, long CreatedMs);
