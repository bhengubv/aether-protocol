// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public class SimulatedTipSettlementProvider
{
    public record Ledger(string FromUhid, string ToUhid, decimal AmountZar, string TrafficType, global::System.DateTimeOffset At)
    {
        public bool Simulated { get; init; }
    }
}
