// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class SiteIdentityLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public SiteIdentityLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewSiteIdentityLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetSiteIdentityLab, global::AetherNetNodeService.Ipc.NodeOp.EventSiteIdentityLab);
    public string? AddNote => Now.AddNote;
    public bool CanAddTyped => Now.CanAddTyped;
    public string MasterTag => Now.MasterTag;
    public string NewSite
    {
        get => Now.NewSite;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetSiteIdentityLabNewSite, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSiteIdentityLab); }
    }
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.SiteIdentityLabDemo.SiteView> Sites => Now.Sites;

    public async global::System.Threading.Tasks.Task AddRandom()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.SiteIdentityLabAddRandom, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSiteIdentityLab);
    }

    public async global::System.Threading.Tasks.Task AddTyped()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.SiteIdentityLabAddTyped, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSiteIdentityLab);
    }

    public async global::System.Threading.Tasks.Task RevisitAll()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.SiteIdentityLabRevisitAll, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSiteIdentityLab);
    }

    /// <summary>Stop listening for what the demo says changed.</summary>
    public void Dispose() => _menu.Told -= OnTold;

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventSiteIdentityLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public string? AddNote { get; init; }
        public bool CanAddTyped { get; init; }
        public string MasterTag { get; init; } = default!;
        public string NewSite { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.SiteIdentityLabDemo.SiteView> Sites { get; init; } = default!;
    }

    public record SiteView(string Name, string SiteTag, string Pseudonym, bool Recognised);
}
