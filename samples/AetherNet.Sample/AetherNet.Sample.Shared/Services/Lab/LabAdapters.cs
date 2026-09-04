// SPDX-License-Identifier: MIT

using AetherNet.ApiClients;
using AetherNet.Extensibility;
using AetherNet.Incentive;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Tipping;

namespace AetherNet.Sample.Shared.Services.Lab;

// ─────────────────────────────────────────────────────────────────────────────
// The host seams the /lab economy demos plug into. Every service the pages
// actually showcase is the real src implementation; these are only the small
// adapters a host is expected to supply — the node's own identity, its backend
// bridge, its route table, its wallet — filled here with the honest answers of a
// single offline device so the demo needs no network, no server, and no money.
// None of them stands in for a service under test; each is a collaborator the
// interface was written to have replaced.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The one identity the tipping layer asks about — "who am I sending this from".
/// A device has exactly one, fixed for the session, so the provider just hands it
/// back. (In the real app this is the phone's keystore UHID; here it is a stable
/// demo string.)
/// </summary>
public sealed class DemoLocalNodeProvider : ILocalNodeProvider
{
    private readonly string _uhid;

    public DemoLocalNodeProvider(string uhid) => _uhid = uhid;

    public Task<string?> GetLocalUhidAsync() => Task.FromResult<string?>(_uhid);
}

/// <summary>
/// The backend bridge, with the internet unplugged. Tipping is offline-first: a tip
/// is queued on the device and settles later, when there is a signal — so a demo
/// that never leaves the device correctly reports "nothing synced" from every sync
/// call rather than pretending a server answered. The queue is the real thing; this
/// is only the door it has not walked through yet.
/// </summary>
public sealed class DemoOfflineApiClient : IAetherApiClient
{
    public Task<T?> RecordTipAsync<T>(object request) => Task.FromResult<T?>(default);
    public Task<int> BatchSyncTipsAsync(object request) => Task.FromResult(0);
    public Task<List<T>> GetTipPoliciesAsync<T>() => Task.FromResult(new List<T>());
    public Task<bool> MeshSettleTipAsync(object request) => Task.FromResult(false);
    public Task<T?> GetTipperReputationAsync<T>(string uhid) => Task.FromResult<T?>(default);
    public Task<int> BatchSyncRewardsAsync(object request) => Task.FromResult(0);
    public Task<bool> RegisterOperatorAsync(object request) => Task.FromResult(false);
    public Task<T?> GetNodeReputationAsync<T>(string uhid) => Task.FromResult<T?>(default);
    public Task<T?> CreateWatchSessionAsync<T>(object request) => Task.FromResult<T?>(default);
    public Task<T?> CreateChipInPoolAsync<T>(object request) => Task.FromResult<T?>(default);
    public Task<T?> ContributeChipInAsync<T>(object request) => Task.FromResult<T?>(default);
}

/// <summary>
/// A route table that knows no routes. <see cref="AetherNet.Security.Services.MeshTipService"/>
/// asks routing for a next hop and, finding none, floods the tip to every neighbour instead —
/// which is exactly how a tip reaches a co-present peer that was never formally discovered. An
/// empty table is not a stubbed-out router; it is the first-packet state of a real one, and the
/// broadcast fallback is the path the demo means to exercise. Returning immediately also keeps the
/// UI snappy — a real discovery would block on an RREQ round-trip that has no one to answer it.
/// </summary>
public sealed class BroadcastOnlyRoutingService : IRoutingService
{
    public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default)
        => Task.FromResult<RouteEntry?>(null);

    public RouteEntry? GetCachedRoute(string destinationUhid) => null;

    public IReadOnlyList<RouteEntry> GetAllRoutes() => Array.Empty<RouteEntry>();

    public Task HandleRouteRequestAsync(MeshPacket routeRequest, string? linkLayerSenderUhid = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// The wallet, drawn on a whiteboard. The protocol carries a tip end-to-end and then asks the host,
/// through this seam, what it is worth — and a real host would move ZAR into an SDPKT wallet here.
/// This one only writes a line in a ledger it keeps in memory and announces it, so the demo can show
/// the money side of a mesh tip landing without any money existing. Every entry is stamped
/// <see cref="Ledger.Simulated"/> for exactly that reason.
/// </summary>
public sealed class SimulatedTipSettlementProvider : IAetherNetIncentiveProvider
{
    private readonly List<Ledger> _settled = new();
    private readonly object _gate = new();

    /// <summary>Raised on the receiving node whenever a mesh tip is settled (in play money).</summary>
    public event Action<Ledger>? Settled;

    /// <summary>Every simulated settlement so far, newest last.</summary>
    public IReadOnlyList<Ledger> Entries()
    {
        lock (_gate)
            return _settled.ToArray();
    }

    /// <inheritdoc />
    public Task SettleMeshTipAsync(TipPacketPayload tip, CancellationToken cancellationToken = default)
    {
        var entry = new Ledger(tip.TipperUhid, tip.RecipientUhid, tip.Amount, tip.TrafficType, DateTimeOffset.UtcNow);
        lock (_gate)
            _settled.Add(entry);
        Settled?.Invoke(entry);
        return Task.CompletedTask;
    }

    /// <summary>One simulated credit into a recipient's (imaginary) wallet.</summary>
    /// <param name="Simulated">Always true — a standing reminder that no value moved.</param>
    public sealed record Ledger(string FromUhid, string ToUhid, decimal AmountZar, string TrafficType, DateTimeOffset At)
    {
        public bool Simulated => true;
    }
}
