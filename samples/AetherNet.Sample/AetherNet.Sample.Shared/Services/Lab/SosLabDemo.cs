// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class SosLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public SosLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewSosLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetSosLab, global::AetherNetNodeService.Ipc.NodeOp.EventSosLab);
    public int DistinctReceptions => Now.DistinctReceptions;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.SosLabDemo.EscalationRow> EscalationTable => Now.EscalationTable;
    public bool LiveActive => Now.LiveActive;
    public string LiveStatus => Now.LiveStatus;
    public int ReachCount => Now.ReachCount;
    public global::System.Collections.Generic.IReadOnlyList<string> Responders => Now.Responders;
    public int Suppressed => Now.Suppressed;
    public int WireDeliveries => Now.WireDeliveries;

    public async global::System.Threading.Tasks.Task BroadcastNearbyAsync(string message)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.SosLabBroadcastNearby, new { message }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSosLab);
    }

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SosLabDispose);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.SosLabDemo.LogLine> Log()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.SosLabDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.SosLabLog, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSosLab);
        return answer;
    }

    public async global::System.Threading.Tasks.Task MarkSafeAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.SosLabMarkSafe, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSosLab);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.SosLabDemo.NodeView> Nodes()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.SosLabDemo.NodeView>>(global::AetherNetNodeService.Ipc.NodeOp.SosLabNodes, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSosLab);
        return answer;
    }

    public async global::System.Threading.Tasks.Task RateLimitAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.SosLabRateLimit, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSosLab);
    }

    public void Start()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SosLabStart, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSosLab);
    }

    public async global::System.Threading.Tasks.Task StartCheckInAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.SosLabStartCheckIn, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventSosLab);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventSosLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public int DistinctReceptions { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.SosLabDemo.EscalationRow> EscalationTable { get; init; } = default!;
        public bool LiveActive { get; init; }
        public string LiveStatus { get; init; } = default!;
        public int ReachCount { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<string> Responders { get; init; } = default!;
        public int Suppressed { get; init; }
        public int WireDeliveries { get; init; }
    }

    public record EscalationRow(string Scenario, string Decision, bool Result);

    public record LogLine(string Text, bool Strong);

    public record NodeView(string Name, bool IsOrigin, bool Heard);
}
