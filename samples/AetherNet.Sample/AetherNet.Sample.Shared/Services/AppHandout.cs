// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class AppHandout
{
    public const int MaxHandovers = 3;
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public AppHandout(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetHandout, global::AetherNetNodeService.Ipc.NodeOp.EventHandout);
    public global::AetherNet.Browser.CardDocument? Card
    {
        get => Now.Card;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetHandoutCard, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventHandout); }
    }
    public string? Invite => Now.Invite;
    public string? Package => Now.Package;
    public global::System.TimeSpan Remaining => Now.Remaining;
    public int Served => Now.Served;
    public global::System.Collections.Generic.IReadOnlyDictionary<string, string> Pictures { set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetHandoutPictures, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventHandout); } }

    public void CloseSoon()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.HandoutCloseSoon, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventHandout);
    }

    public string? Start(string? host = null, string? from = null)
    {
        var answer = _menu.Call<string?>(global::AetherNetNodeService.Ipc.NodeOp.HandoutStart, new { host, from }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventHandout);
        return answer;
    }

    public void Stop()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.HandoutStop, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventHandout);
    }

    public event global::System.Action? Changed;
    public event global::System.Action? Delivered;
    public event global::System.Action<string>? Say;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventHandout: Changed?.Invoke(); break;
            case global::AetherNetNodeService.Ipc.NodeOp.EventHandoutDelivered: Delivered?.Invoke(); break;
            case global::AetherNetNodeService.Ipc.NodeOp.EventHandoutSay: Say?.Invoke(global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<string>(body)); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::AetherNet.Browser.CardDocument? Card { get; init; }
        public string? Invite { get; init; }
        public string? Package { get; init; }
        public global::System.TimeSpan Remaining { get; init; }
        public int Served { get; init; }
    }
}
