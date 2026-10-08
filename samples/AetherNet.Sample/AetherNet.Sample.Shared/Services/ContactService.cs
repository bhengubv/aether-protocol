// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class ContactService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public ContactService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetContact, global::AetherNetNodeService.Ipc.NodeOp.EventContact);
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ContactRecord> Contacts => Now.Contacts;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ContactRecord> Incoming => Now.Incoming;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ContactRecord> Mutual => Now.Mutual;
    public string MyInvite => Now.MyInvite;

    public async global::System.Threading.Tasks.Task<bool> AddAsync(string tagOrInvite, string via, string? displayName = null)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.ContactAdd, new { tagOrInvite, via, displayName }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventContact);
        return answer;
    }

    public string DisplayName(string? tag)
    {
        var answer = _menu.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.ContactDisplayName, new { tag }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventContact);
        return answer;
    }

    public bool HasName(string? tag)
    {
        var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.ContactHasName, new { tag }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventContact);
        return answer;
    }

    public void Remove(string tag)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.ContactRemove, new { tag }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventContact);
    }

    public bool SetName(string? tag, string? name)
    {
        var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.ContactSetName, new { tag, name }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventContact);
        return answer;
    }

    public static bool TryParseInvite(string? text, out string tag, out byte[]? publicKey)
    {
        var answer = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<TryParseInviteAnswer>(global::AetherNetNodeService.Ipc.NodeOp.ContactTryParseInvite, new { text });
        tag = answer.Tag;
        publicKey = answer.PublicKey;
        return answer.Result;
    }

    private sealed record TryParseInviteAnswer(bool Result, string Tag, byte[]? PublicKey);

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventContact: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ContactRecord> Contacts { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ContactRecord> Incoming { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Data.ContactRecord> Mutual { get; init; } = default!;
        public string MyInvite { get; init; } = default!;
    }
}
