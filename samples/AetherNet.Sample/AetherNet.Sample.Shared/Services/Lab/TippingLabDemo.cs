// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class TippingLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public TippingLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewTippingLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetTippingLab, global::AetherNetNodeService.Ipc.NodeOp.EventTippingLab);
    public short Boost => Now.Boost;
    public decimal DailyTotal => Now.DailyTotal;
    public global::AetherNet.Protocol.MeshPacket? LastPacket => Now.LastPacket;
    public bool? LastTipAccepted => Now.LastTipAccepted;
    public int PendingRewards => Now.PendingRewards;
    public int PendingTips => Now.PendingTips;
    public global::AetherNet.Tipping.Models.TipPolicy? Policy => Now.Policy;
    public string RecipientPetname => Now.RecipientPetname;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.SimulatedTipSettlementProvider.Ledger> Settlements => Now.Settlements;
    public global::AetherNet.Tipping.Models.QoSTier Tier => Now.Tier;

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.TippingLabDispose);
    }

    public async global::System.Threading.Tasks.Task SendMeshTipAsync(decimal amount)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.TippingLabSendMeshTip, new { amount }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventTippingLab);
    }

    public async global::System.Threading.Tasks.Task SetConsistencyAsync(short score)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.TippingLabSetConsistency, new { score }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventTippingLab);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.TippingLabDemo.LogLine> Snapshot()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.TippingLabDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.TippingLabSnapshot, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventTippingLab);
        return answer;
    }

    public async global::System.Threading.Tasks.Task StartAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.TippingLabStart, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventTippingLab);
    }

    public async global::System.Threading.Tasks.Task TipOnDeviceAsync(decimal amount)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.TippingLabTipOnDevice, new { amount }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventTippingLab);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventTippingLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public short Boost { get; init; }
        public decimal DailyTotal { get; init; }
        public global::AetherNet.Protocol.MeshPacket? LastPacket { get; init; }
        public bool? LastTipAccepted { get; init; }
        public int PendingRewards { get; init; }
        public int PendingTips { get; init; }
        public global::AetherNet.Tipping.Models.TipPolicy? Policy { get; init; }
        public string RecipientPetname { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.SimulatedTipSettlementProvider.Ledger> Settlements { get; init; } = default!;
        public global::AetherNet.Tipping.Models.QoSTier Tier { get; init; }
    }

    public record LogLine(string Text, bool Emphasis);
}
