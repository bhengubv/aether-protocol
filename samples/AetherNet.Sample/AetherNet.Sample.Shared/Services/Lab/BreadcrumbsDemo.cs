// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class BreadcrumbsDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public BreadcrumbsDemo(double centerLat = -26.2041, double centerLon = 28.0473)
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewBreadcrumbs, new { centerLat, centerLon });
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetBreadcrumbs, global::AetherNetNodeService.Ipc.NodeOp.EventBreadcrumbs);
    public bool Busy => Now.Busy;
    public global::AetherNet.Sample.Shared.Services.Lab.BreadcrumbsDemo.CrumbInfo? Current => Now.Current;

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.BreadcrumbsDemo.DeviceView> Devices()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.BreadcrumbsDemo.DeviceView>>(global::AetherNetNodeService.Ipc.NodeOp.BreadcrumbsDevices, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBreadcrumbs);
        return answer;
    }

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.BreadcrumbsDispose);
    }

    public async global::System.Threading.Tasks.Task DropAsync(string note, global::AetherNet.Space.Models.BreadcrumbType type, int ttlHours)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.BreadcrumbsDrop, new { note, type, ttlHours }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBreadcrumbs);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.BreadcrumbsDemo.LogLine> Log()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.BreadcrumbsDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.BreadcrumbsLog, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBreadcrumbs);
        return answer;
    }

    public void PruneExpired()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.BreadcrumbsPruneExpired, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBreadcrumbs);
    }

    public async global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Space.Models.SpaceBreadcrumb>> ScanFromOriginAsync(int radiusCells)
    {
        var answer = await _menu.CallAsync<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Space.Models.SpaceBreadcrumb>>(global::AetherNetNodeService.Ipc.NodeOp.BreadcrumbsScanFromOrigin, new { radiusCells }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBreadcrumbs);
        return answer;
    }

    public async global::System.Threading.Tasks.Task SeedStaleNoticeAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.BreadcrumbsSeedStaleNotice, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventBreadcrumbs);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventBreadcrumbs: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public bool Busy { get; init; }
        public global::AetherNet.Sample.Shared.Services.Lab.BreadcrumbsDemo.CrumbInfo? Current { get; init; }
    }

    public record CrumbInfo(string Note, global::AetherNet.Space.Models.BreadcrumbType Type, int TtlHours, global::System.DateTime ExpiresAtUtc, string ContentHash, int CarriedBy, int Total);

    public enum CrumbKind
    {
        Idle = 0,
        Origin = 1,
        Carrying = 2,
        Refused = 3,
    }

    public record DeviceView(int Row, int Col, string Cell, global::AetherNet.Sample.Shared.Services.Lab.BreadcrumbsDemo.CrumbKind Kind, int Distance, bool InRadius);

    public record LogLine(string Text, bool Emphasis);
}
