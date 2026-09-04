// SPDX-License-Identifier: MIT

using AetherNet.Constants;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Services; // InProcessMeshSender (reused)
using AetherNet.Sos;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// Drives the real <see cref="SosBroadcastService"/> across a live in-process mesh: one originator and
/// four neighbours, fully connected. An SOS floods, every node re-broadcasts, and the same packet id
/// reaching a node twice is suppressed — the demo counts exactly how many wire deliveries the dedup
/// swallowed. The originator learns its true reach as signed acknowledgements flow back. The rolling
/// rate limit (<see cref="ProtocolConstants.MaxSosBroadcastsPerHour"/> per hour) is shown refusing the
/// fourth origination; the pure <see cref="SosEscalationPolicy"/> is shown as its own truth table; and
/// a contacts-only check-in is run for real — when the source doesn't mark safe in time it auto-widens
/// to a full broadcast and starts a locator beacon, exactly as the safety lifecycle prescribes.
/// </summary>
public sealed class SosLabDemo : IDisposable
{
    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();
    private readonly string _run = Guid.NewGuid().ToString("N")[..6];
    private readonly List<Node> _nodes = new();
    private readonly Dictionary<string, string> _petnames = new(StringComparer.Ordinal);

    // Reach + dedup instrumentation for the most recent origination.
    private readonly HashSet<string> _heard = new(StringComparer.Ordinal);      // node uhids that raised SosReceived
    private readonly List<string> _responders = new();
    private int _wireDeliveries;
    private int _distinctReceptions;

    private Guid _liveAlertId;
    private string _liveStatus = "idle";
    private bool _escalationLogged;
    private CancellationTokenSource? _liveCts;

    private bool _started;
    private bool _disposed;

    // A fixed location for the alert (coarse). Durban-ish.
    private const double Lat = -29.85, Lon = 31.02;
    private const string Geo = "ke7yc8p";

    public event Action? Changed;

    public IReadOnlyList<NodeView> Nodes()
    {
        lock (_gate)
            return _nodes.Select(n => new NodeView(n.Name, n.IsOrigin, _heard.Contains(n.Uhid))).ToArray();
    }

    public IReadOnlyList<LogLine> Log()
    {
        lock (_gate) return _log.ToArray();
    }

    public int ReachCount { get { lock (_gate) return _responders.Count; } }
    public IReadOnlyList<string> Responders { get { lock (_gate) return _responders.ToArray(); } }
    public int WireDeliveries => _wireDeliveries;
    public int DistinctReceptions => _distinctReceptions;
    public int Suppressed => Math.Max(0, _wireDeliveries - _distinctReceptions);
    public int RateLimit => ProtocolConstants.MaxSosBroadcastsPerHour;
    public string LiveStatus => _liveStatus;
    public bool LiveActive => _liveAlertId != Guid.Empty;
    public IReadOnlyList<EscalationRow> EscalationTable { get; private set; } = Array.Empty<EscalationRow>();

    // ─── Setup ──────────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        var you = CreateNode("You", isOrigin: true);
        var nomsa = CreateNode("Nomsa", isOrigin: false);
        var thabo = CreateNode("Thabo", isOrigin: false);
        var ayanda = CreateNode("Ayanda", isOrigin: false);
        var sipho = CreateNode("Sipho", isOrigin: false);
        var all = new[] { you, nomsa, thabo, ayanda, sipho };

        foreach (var self in all)
        foreach (var other in all)
            if (!ReferenceEquals(self, other))
                self.Sender.AddPotentialPeer(other.Uhid);

        foreach (var node in all)
        {
            var n = node;
            n.Transport.DataReceived += (_src, bytes) =>
            {
                MeshPacket packet;
                try { packet = PacketSerializer.Deserialize(bytes); }
                catch { return; }

                if (packet.Type == PacketType.SosBroadcast)
                {
                    Interlocked.Increment(ref _wireDeliveries); // every wire delivery, dupes included
                    _ = n.Sos.HandleAsync(packet);
                }
                else if (packet.Type == PacketType.SosAck)
                {
                    _ = n.Sos.HandleAckAsync(packet);
                }
            };

            n.Sos.SosReceived += (_, alert) =>
            {
                Interlocked.Increment(ref _distinctReceptions);
                bool firstForNode;
                lock (_gate) firstForNode = _heard.Add(n.Uhid);
                if (firstForNode) // suppress repeat "heard" lines when a locator beacon re-emits
                    Emit($"{n.Name} heard the SOS ({Short(alert.Id)}) from {Petname(alert.SenderUhid)}: “{alert.Message}”", n.IsOrigin);
            };
        }

        // Reach + resolution surface only on the originator.
        you.Sos.SosAcknowledged += (_, ack) =>
        {
            lock (_gate)
            {
                var name = Petname(ack.ResponderUhid);
                if (!_responders.Contains(name)) _responders.Add(name);
            }
            Emit($"reach: {Petname(ack.ResponderUhid)} acknowledged — {ack.TotalAcknowledgements} device(s) confirmed the alert.", strong: true);
        };
        you.Sos.SosResolved += (_, id) =>
        {
            if (id == _liveAlertId) { _liveAlertId = Guid.Empty; _liveStatus = "resolved — source marked safe"; }
            Emit($"SOS {Short(id)} resolved — the source marked safe. Beacon stopped.", strong: true);
        };

        lock (_gate) _nodes.AddRange(all);

        EscalationTable = BuildEscalationTable();
        Emit("Five phones in range, fully connected. Fire an SOS and watch it flood, dedup, and come back with its reach.");
    }

    private Node CreateNode(string name, bool isOrigin)
    {
        var uhid = $"lab:sos:{_run}:{name}";
        var transport = new InProcessTransportService(uhid, NullLogger<InProcessTransportService>.Instance);
        var sender = new InProcessMeshSender(uhid, transport);
        var sos = new SosBroadcastService(sender);
        _petnames[uhid] = name;
        return new Node(name, uhid, isOrigin, transport, sender, sos);
    }

    // ─── Demo 1: flood + dedup + reach ───────────────────────────────────────────

    public async Task BroadcastNearbyAsync(string message)
    {
        var you = Origin();
        if (you is null) return;
        ResetRun();

        Emit($"You broadcast an SOS to everyone nearby (TTL {ProtocolConstants.SosTtl}, priority {ProtocolConstants.SosPriority}).", strong: true);
        // The one-shot flood overload: floods once and dedups on re-broadcast. (The lifecycle/beacon
        // overload is reserved for the check-in demo below.)
        var ok = await you.Sos.BroadcastSosAsync("sos", message, Lat, Lon, Geo).ConfigureAwait(false);
        if (!ok)
        {
            Emit($"Refused — the rolling rate limit ({RateLimit}/hour) is spent. Mark an earlier SOS safe or wait it out.");
            return;
        }

        await Task.Delay(200).ConfigureAwait(false); // let the flood, refloods and acks settle
        Emit($"Wire carried {_wireDeliveries} SOS packet(s); dedup suppressed {Suppressed} reflood(s); {_distinctReceptions} distinct reception(s). Reach: {ReachCount} device(s).", strong: true);
        RaiseChanged();
    }

    // ─── Demo 2: rate limit (isolated sandbox so it stays repeatable) ─────────────

    public async Task RateLimitAsync()
    {
        // A throwaway service with its own budget, so this can be run again and again without spending the
        // real originator's three-per-hour allowance or flooding the mesh.
        var uhid = $"lab:sos:{_run}:rl:{Guid.NewGuid():N}";
        using var transport = new InProcessTransportService(uhid, NullLogger<InProcessTransportService>.Instance);
        var sender = new InProcessMeshSender(uhid, transport); // no peers → floods to nobody
        using var svc = new SosBroadcastService(sender);

        Emit($"Rate-limit test — the protocol caps a node at {RateLimit} SOS originations per rolling hour:");
        for (int i = 1; i <= RateLimit + 1; i++)
        {
            var ok = await svc.BroadcastSosAsync("sos", $"attempt {i}", Lat, Lon, Geo).ConfigureAwait(false);
            Emit(ok
                ? $"  origination {i} → accepted ({i}/{RateLimit})"
                : $"  origination {i} → RATE-LIMITED (returns false) — floods abuse-protection.", strong: !ok);
        }
        RaiseChanged();
    }

    // ─── Demo 3: live contacts-only check-in that escalates ──────────────────────

    public async Task StartCheckInAsync()
    {
        var you = Origin();
        if (you is null || LiveActive) return;
        ResetRun();

        var contacts = _nodes.Where(n => n.Name is "Nomsa" or "Thabo").Select(n => n.Uhid).ToArray();
        var escalateAfter = TimeSpan.FromSeconds(3);
        var beacon = TimeSpan.FromSeconds(3);

        Emit("You start a contacts-only check-in (a dead-man's switch). Only Nomsa and Thabo get it — quietly.", strong: true);
        var ok = await you.Sos.BroadcastSosAsync("check-in", "I'm heading home alone", Lat, Lon,
            SosReach.Contacts, contacts, escalateAfter, beacon, Geo).ConfigureAwait(false);
        if (!ok)
        {
            Emit($"Refused — rate limit ({RateLimit}/hour) spent.");
            return;
        }

        // Find the freshly-created alert to track its lifecycle.
        var alert = you.Sos.GetActiveAlerts().FirstOrDefault(a => a.BroadcastType == "check-in");
        _liveAlertId = alert?.Id ?? Guid.Empty;
        _liveStatus = "contacts-only — counting down to escalation";
        _escalationLogged = false;

        _liveCts?.Cancel();
        _liveCts = new CancellationTokenSource();
        _ = PollLifecycleAsync(you, _liveCts.Token);
        RaiseChanged();
    }

    public async Task MarkSafeAsync()
    {
        var you = Origin();
        if (you is null || _liveAlertId == Guid.Empty) return;
        var id = _liveAlertId;
        _liveCts?.Cancel();
        await you.Sos.ResolveAsync(id).ConfigureAwait(false); // the ONLY thing that stops an active SOS
    }

    // Watch the real lifecycle flip Contacts → Both and start beaconing; refresh the UI as it happens.
    private async Task PollLifecycleAsync(Node you, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow - start < TimeSpan.FromSeconds(12))
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
                var alert = you.Sos.GetActiveAlerts().FirstOrDefault(a => a.Id == _liveAlertId);
                if (alert is null) break; // resolved

                if (alert.Escalated && !_escalationLogged)
                {
                    _escalationLogged = true;
                    _liveStatus = "escalated to broadcast — locator beacon re-emitting";
                    Emit("No “safe” in time → the check-in auto-widened to a full broadcast. Everyone nearby now hears it, and a locator beacon re-emits so rescuers can triangulate.", strong: true);
                }
                RaiseChanged();
            }
        }
        catch (OperationCanceledException) { /* marked safe or torn down */ }
    }

    public void ClearLog()
    {
        lock (_gate) _log.Clear();
        RaiseChanged();
    }

    // ─── Escalation policy truth table (pure, deterministic) ─────────────────────

    private static IReadOnlyList<EscalationRow> BuildEscalationTable()
    {
        var after = TimeSpan.FromSeconds(120);
        return new[]
        {
            Row("contacts-only, 130s elapsed, not yet escalated", SosEscalationPolicy.ShouldEscalate(SosReach.Contacts, false, TimeSpan.FromSeconds(130), after), "escalate"),
            Row("contacts-only, 60s elapsed (still within window)", SosEscalationPolicy.ShouldEscalate(SosReach.Contacts, false, TimeSpan.FromSeconds(60), after), "escalate"),
            Row("contacts-only, already escalated once", SosEscalationPolicy.ShouldEscalate(SosReach.Contacts, true, TimeSpan.FromSeconds(300), after), "escalate"),
            Row("nearby flood (not a check-in)", SosEscalationPolicy.ShouldEscalate(SosReach.Nearby, false, TimeSpan.FromSeconds(999), after), "escalate"),
            Row("contacts-only, before it escalates", SosEscalationPolicy.IsBroadcasting(SosReach.Contacts, false), "beacon"),
            Row("contacts-only, after it escalates", SosEscalationPolicy.IsBroadcasting(SosReach.Contacts, true), "beacon"),
            Row("nearby flood", SosEscalationPolicy.IsBroadcasting(SosReach.Nearby, false), "beacon"),
            Row("both (contacts + nearby)", SosEscalationPolicy.IsBroadcasting(SosReach.Both, false), "beacon"),
        };

        static EscalationRow Row(string scenario, bool result, string decision) => new(scenario, decision, result);
    }

    // ─── Internals ────────────────────────────────────────────────────────────────

    private Node? Origin()
    {
        lock (_gate) return _nodes.FirstOrDefault(n => n.IsOrigin);
    }

    private void ResetRun()
    {
        lock (_gate)
        {
            _heard.Clear();
            _responders.Clear();
        }
        _wireDeliveries = 0;
        _distinctReceptions = 0;
    }

    private string Petname(string uhid) => _petnames.TryGetValue(uhid, out var n) ? n : uhid;

    private static string Short(Guid id) => id.ToString("N")[..8];

    private void Emit(string text, bool strong = false)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(text, strong));
            if (_log.Count > 250) _log.RemoveRange(0, _log.Count - 250);
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _liveCts?.Cancel();
        lock (_gate)
        {
            foreach (var n in _nodes) { n.Sos.Dispose(); n.Transport.Dispose(); }
            _nodes.Clear();
        }
    }

    // ─── View + node types ───────────────────────────────────────────────────────

    public sealed record LogLine(string Text, bool Strong);
    public sealed record NodeView(string Name, bool IsOrigin, bool Heard);
    public sealed record EscalationRow(string Scenario, string Decision, bool Result);

    private sealed class Node
    {
        public Node(string name, string uhid, bool isOrigin, InProcessTransportService transport,
            InProcessMeshSender sender, SosBroadcastService sos)
        {
            Name = name;
            Uhid = uhid;
            IsOrigin = isOrigin;
            Transport = transport;
            Sender = sender;
            Sos = sos;
        }

        public string Name { get; }
        public string Uhid { get; }
        public bool IsOrigin { get; }
        public InProcessTransportService Transport { get; }
        public InProcessMeshSender Sender { get; }
        public SosBroadcastService Sos { get; }
    }
}
