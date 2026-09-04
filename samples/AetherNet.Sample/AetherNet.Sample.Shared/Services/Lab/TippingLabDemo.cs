// SPDX-License-Identifier: MIT

using AetherNet.Incentive;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Services; // InProcessMeshSender (defined alongside AetherDemoService)
using AetherNet.Security.Services;
using AetherNet.Tipping;
using AetherNet.Tipping.Incentives;
using AetherNet.Tipping.Models;
using AetherNet.Tipping.Services;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// Drives the whole tipping / creator-rewards path in-process, over the real src services,
/// with the money side drawn in play chips.
///
/// <para>
/// A tip on AetherNet is three separate things, and this demo keeps them apart:
/// </para>
/// <list type="number">
///   <item><b>The wire signal</b> — <see cref="MeshTipService"/> puts a real, Ed25519-signed
///     <see cref="PacketType.TipPacket"/> (24) on a two-node in-process mesh; the recipient
///     verifies and hands it to its settlement seam, which here writes a <i>simulated</i>
///     ledger line. The protocol carries the intent to credit; it moves no money itself.</item>
///   <item><b>The on-device book-keeping</b> — <see cref="TippingService"/> validates a tip
///     against the regulated policy band, queues it so it survives offline, and accrues the
///     tipper's XP via <see cref="AetherRewardService"/>. This is the SDPKT-settled client;
///     settlement batches to the backend later, which here is offline on purpose.</item>
///   <item><b>The earned preference</b> — <see cref="TipperQoSService"/> maps a tipper's
///     consistency standing to a routing-quality boost. A preference, never a gate: a
///     non-tipper always gets service, a steady tipper simply gets a nudge up the queue.</item>
/// </list>
///
/// <para>Nothing here settles real value. Every rand is a demo rand.</para>
/// </summary>
public sealed class TippingLabDemo : IDisposable
{
    private const string TrafficTag = "message-relay";
    private const string EcosystemId = "sdpkt";

    private readonly string _inst = Guid.NewGuid().ToString("N")[..4];
    private readonly List<LogLine> _log = new();
    private readonly object _gate = new();

    private string _tipperUhid = "";
    private string _recipientUhid = "";

    // ── Section 1: the mesh TipPacket ───────────────────────────────────────────
    private MeshNode _tipper = null!;
    private MeshNode _recipient = null!;
    private readonly BroadcastOnlyRoutingService _router = new();
    private readonly SimulatedTipSettlementProvider _settlement = new();
    private MeshPacket? _lastPacket;

    // ── Section 2: the on-device tipping trio ───────────────────────────────────
    private readonly InMemoryAetherTipStore _tipStore = new();
    private readonly InMemoryAetherRewardStore _rewardStore = new();
    private DemoLocalNodeProvider _localNode = null!;
    private readonly DemoOfflineApiClient _api = new();
    private AetherRewardService _rewards = null!;
    private TippingService _tipping = null!;

    // ── Section 3: tipper QoS ───────────────────────────────────────────────────
    private TipperQoSService _qos = null!;

    private bool _started;
    private bool _disposed;

    public event Action? Changed;

    // ── Read models ─────────────────────────────────────────────────────────────

    public string TipperPetname => Petname(_tipperUhid);
    public string RecipientPetname => Petname(_recipientUhid);
    public string RecipientUhid => _recipientUhid;

    // Section 1
    public MeshPacket? LastPacket => _lastPacket;
    public IReadOnlyList<SimulatedTipSettlementProvider.Ledger> Settlements => _settlement.Entries();

    // Section 2
    public int PendingTips { get; private set; }
    public decimal DailyTotal { get; private set; }
    public int PendingRewards { get; private set; }
    public TipPolicy? Policy { get; private set; }
    public bool? LastTipAccepted { get; private set; }

    // Section 3
    public short Consistency { get; private set; }
    public QoSTier Tier { get; private set; } = QoSTier.Standard;
    public short Boost { get; private set; }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate)
            return _log.ToArray();
    }

    // ── Setup ───────────────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        _tipperUhid = Uhid("you");
        _recipientUhid = Uhid("nomsa"); // a relay operator who accepts tips

        // Section 1 — two real identities on one in-process mesh. Only the recipient
        // carries a settlement seam; the tipper just signs and sends.
        _tipper = NewMeshNode(_tipperUhid, incentives: null);
        _recipient = NewMeshNode(_recipientUhid, incentives: _settlement);
        _tipper.Sender.AddPotentialPeer(_recipientUhid);

        _recipient.Transport.DataReceived += (_src, bytes) =>
        {
            MeshPacket packet;
            try { packet = PacketSerializer.Deserialize(bytes); }
            catch { return; }
            if (packet.Type == PacketType.TipPacket)
                _ = _recipient.MeshTip.HandleTipPacketAsync(packet);
        };

        _settlement.Settled += entry =>
            Emit($"{Petname(entry.ToUhid)} settled R{entry.AmountZar:0.00} (simulated) from {Petname(entry.FromUhid)} — its wallet, its rules.", emphasis: true);

        // Section 2 — the on-device trio over in-memory stores + an offline backend.
        _localNode = new DemoLocalNodeProvider(_tipperUhid);
        _rewards = new AetherRewardService(_rewardStore, _api, NullLogger<AetherRewardService>.Instance);
        _tipping = new TippingService(_tipStore, _localNode, _api, _rewards, NullLogger<TippingService>.Instance);

        // The recipient has to be a registered, tip-accepting operator, or the client
        // (correctly) refuses to queue a tip to it.
        await _tipStore.SaveNodeOperatorAsync(new NodeOperatorProfile
        {
            Uhid = _recipientUhid,
            SdpktWalletAddress = "sdpkt:demo-wallet",
            IsRegistered = true,
            AcceptsTips = true,
            OperatorSince = DateTimeOffset.UtcNow.AddMonths(-3),
        }).ConfigureAwait(false);

        Policy = await _tipping.GetPolicyAsync(TipTrafficType.MessageRelay).ConfigureAwait(false);

        // Section 3 — QoS reads the tipper's own consistency standing.
        _qos = new TipperQoSService(_tipStore, _localNode, NullLogger<TipperQoSService>.Instance);
        await RefreshOnDeviceAsync().ConfigureAwait(false);

        Emit($"{Petname(_tipperUhid)} can tip {Petname(_recipientUhid)}, a registered relay operator. " +
             $"Policy: R{Policy?.MinAmount:0.00}–R{Policy?.MaxAmount:0.00}, daily cap R{Policy?.DailyCapPerTipper:0.00}.");

        // Seed a starting QoS standing so the tier ladder is live from the first render.
        await SetConsistencyAsync(60).ConfigureAwait(false);
        RaiseChanged();
    }

    // ── Section 1: send a tip over the mesh (TipPacket) ─────────────────────────

    public async Task SendMeshTipAsync(decimal amount)
    {
        Emit($"{Petname(_tipperUhid)} → building a signed TipPacket for R{amount:0.00} and flooding it to {Petname(_recipientUhid)}…");

        _lastPacket = await _tipper.MeshTip
            .SendTipAsync(_recipientUhid, amount, TrafficTag, EcosystemId)
            .ConfigureAwait(false);

        // Delivery is synchronous over the in-process transport; let the recipient's
        // async verify + settle land before we read the ledger back.
        await Task.Delay(80).ConfigureAwait(false);

        Emit($"TipPacket on the wire: type {(int)_lastPacket.Type} (TipPacket), envelope signature {_lastPacket.Signature.Length}B, ecosystem \"{EcosystemId}\".");
        RaiseChanged();
    }

    // ── Section 2: on-device queue + reward accrual ─────────────────────────────

    public async Task TipOnDeviceAsync(decimal amount)
    {
        var ok = await _tipping.TipNodeAsync(_recipientUhid, amount, TipTrafficType.MessageRelay).ConfigureAwait(false);
        LastTipAccepted = ok;

        await RefreshOnDeviceAsync().ConfigureAwait(false);

        Emit(ok
            ? $"Queued R{amount:0.00} to {Petname(_recipientUhid)} — +{TipPolicyConstants.XpMeshTip} XP accrued. " +
              $"Pending tips {PendingTips}, pending XP {PendingRewards}, today R{DailyTotal:0.00}."
            : $"Tip of R{amount:0.00} refused — outside policy or over the daily cap. Service is unaffected; a tip is never a gate.",
            emphasis: true);
        RaiseChanged();
    }

    private async Task RefreshOnDeviceAsync()
    {
        PendingTips = await _tipping.GetPendingTipCountAsync().ConfigureAwait(false);
        DailyTotal = await _tipping.GetDailyTotalAsync().ConfigureAwait(false);
        PendingRewards = await _rewards.GetPendingCountAsync().ConfigureAwait(false);
    }

    // ── Section 3: tipper QoS effect ────────────────────────────────────────────

    public async Task SetConsistencyAsync(short score)
    {
        Consistency = score;

        // The QoS service recomputes the tier from the stored consistency score, so the
        // demo writes that standing and asks the service to refresh.
        await _tipStore.SaveTipperReputationAsync(new TipperReputation
        {
            TipperUhid = _tipperUhid,
            ConsistencyScore = score,
            TipCount = PendingTips,
            LastTippedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(false);

        await _qos.RefreshScoresAsync().ConfigureAwait(false);
        Tier = _qos.GetTier(_tipperUhid);
        Boost = _qos.GetQoSBoost(_tipperUhid);

        Emit($"Consistency {score}/100 → {Tier} tier, routing-quality boost +{Boost}.", emphasis: true);
        RaiseChanged();
    }

    // ── Internals ───────────────────────────────────────────────────────────────

    private MeshNode NewMeshNode(string uhid, AetherNet.Extensibility.IAetherNetIncentiveProvider? incentives)
    {
        var identity = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        identity.SetLocalUhid(uhid);
        var signing = new PacketSigningService(identity, NullLogger<PacketSigningService>.Instance);
        var transport = new InProcessTransportService(uhid, NullLogger<InProcessTransportService>.Instance);
        var sender = new InProcessMeshSender(uhid, transport);
        var meshTip = new MeshTipService(sender, _router, signing, identity, incentives,
            NullLogger<MeshTipService>.Instance);
        return new MeshNode(uhid, identity, signing, transport, sender, meshTip);
    }

    private string Uhid(string name) => $"aether:{name}:{_inst}";

    private void Emit(string text, bool emphasis = false)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(text, emphasis));
            if (_log.Count > 200)
                _log.RemoveRange(0, _log.Count - 200);
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private static string Petname(string uhid)
    {
        var parts = uhid.Split(':');
        return parts.Length >= 2 && parts[1].Length > 0
            ? char.ToUpperInvariant(parts[1][0]) + parts[1][1..]
            : uhid;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var n in new[] { _tipper, _recipient })
        {
            n?.Transport.Dispose();
            n?.Signing.Dispose();
        }
    }

    public sealed record LogLine(string Text, bool Emphasis);

    private sealed record MeshNode(
        string Uhid,
        SignalProtocolService Identity,
        PacketSigningService Signing,
        InProcessTransportService Transport,
        InProcessMeshSender Sender,
        MeshTipService MeshTip);
}
