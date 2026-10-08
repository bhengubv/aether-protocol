// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class FmhyDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public FmhyDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewFmhy, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetFmhy, global::AetherNetNodeService.Ipc.NodeOp.EventFmhy);
    public global::System.Collections.Generic.IReadOnlyList<string> Categories => Now.Categories;
    public string CategoryFilter => Now.CategoryFilter;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Fmhy.Models.FmhyEntry> Entries => Now.Entries;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.FmhyDemo.LogLine> Log => Now.Log;
    public string? NewEntryReachedOffline => Now.NewEntryReachedOffline;
    public int OfflineCount => Now.OfflineCount;
    public global::System.DateTime? OfflineSyncedAt => Now.OfflineSyncedAt;
    public int OnlineCount => Now.OnlineCount;
    public global::System.DateTime? OnlineSyncedAt => Now.OnlineSyncedAt;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Fmhy.Models.FmhyEntry> Starred => Now.Starred;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Fmhy.TrackerSource> Trackers => Now.Trackers;

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.FmhyDispose);
    }

    public void Filter(string? category)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.FmhyFilter, new { category }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventFmhy);
    }

    public void LoadSeed()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.FmhyLoadSeed, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventFmhy);
    }

    public async global::System.Threading.Tasks.Task PropagateToOfflineAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.FmhyPropagateToOffline, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventFmhy);
    }

    public async global::System.Threading.Tasks.Task SyncOnlineAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.FmhySyncOnline, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventFmhy);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventFmhy: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::System.Collections.Generic.IReadOnlyList<string> Categories { get; init; } = default!;
        public string CategoryFilter { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Fmhy.Models.FmhyEntry> Entries { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.FmhyDemo.LogLine> Log { get; init; } = default!;
        public string? NewEntryReachedOffline { get; init; }
        public int OfflineCount { get; init; }
        public global::System.DateTime? OfflineSyncedAt { get; init; }
        public int OnlineCount { get; init; }
        public global::System.DateTime? OnlineSyncedAt { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Fmhy.Models.FmhyEntry> Starred { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Fmhy.TrackerSource> Trackers { get; init; } = default!;
    }

    public record LogLine(string Who, string Text);
}
