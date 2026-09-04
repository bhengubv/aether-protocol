// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using AetherNet.Streaming;
using AetherNet.Streaming.Models;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// Drives a synchronised-playback session against the real <see cref="WatchTogetherService"/>.
/// The host (Alice) issues authoritative Play / Pause / Seek / Speed commands; the followers
/// (Bob, Charlie) apply them with the spec's RTT-compensation rule
/// (<c>position + (now − sent_at) × speed</c> while playing, a hard snap on a seek).
///
/// To make that compensation <i>visible</i> — it is otherwise sub-millisecond in-process — each
/// follower is given a simulated one-way link delay, and the demo defers that follower's inbound
/// packets by it. So on Play, Bob (40 ms out) fast-forwards ~40 ms past the host's mark to land
/// where the host is <i>now</i>, Charlie (120 ms) further; on Seek, both snap to the exact frame.
/// Followers also fire reactions, and can chip in to a shared funding pool.
/// </summary>
public sealed class WatchTogetherDemo : IDisposable
{
    private const int MaxLogLines = 200;

    private readonly ILoggerFactory _loggerFactory;
    private readonly DirectRoutingService _routing = new();
    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();
    private readonly List<Node> _nodes = new();
    private readonly string _salt = Guid.NewGuid().ToString("N")[..6];

    private Node? _host;
    private Guid? _sessionId;
    private Guid? _chipInId;
    private long _positionMs;
    private double _speed = 1.0;
    private bool _isPlaying;
    private bool _started;
    private bool _disposed;

    public WatchTogetherDemo(ILoggerFactory? loggerFactory = null)
        => _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public event Action? Changed;

    public bool IsHosting => _sessionId is not null;
    public long HostPositionMs => _positionMs;
    public double Speed => _speed;
    public bool IsPlaying => _isPlaying;

    // ChipIn pool (host ledger — see note in StartChipInAsync).
    public bool HasPool => _chipInId is not null;
    public decimal PoolCollected { get; private set; }
    public decimal PoolTarget { get; private set; }
    public bool PoolFunded => HasPool && PoolCollected >= PoolTarget;

    public IReadOnlyList<LogLine> Snapshot() { lock (_gate) return _log.ToArray(); }

    public IReadOnlyList<NodeView> Nodes()
    {
        lock (_gate)
            return _nodes.Select(n => new NodeView(n.Name, n.Color, n.IsHost, n.LatencyMs,
                n.IsHost ? _positionMs : n.PositionMs,
                n.IsHost ? _isPlaying : n.IsPlaying,
                n.IsHost ? _speed : n.Speed,
                n.Following,
                n.IsHost ? 0 : n.PositionMs - _positionMs)).ToArray();
    }

    // ─── Setup ───────────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        var alice = CreateNode("Alice", "#2196F3", isHost: true, latencyMs: 0);
        var bob = CreateNode("Bob", "#1976D2", isHost: false, latencyMs: 40);
        var charlie = CreateNode("Charlie", "#2c3e50", isHost: false, latencyMs: 120);
        _host = alice;

        var all = new[] { alice, bob, charlie };
        foreach (var self in all)
        foreach (var other in all)
            if (!ReferenceEquals(self, other))
                self.Sender.AddPotentialPeer(other.Uhid);

        foreach (var node in all)
        {
            var n = node;
            n.Transport.DataReceived += (_src, bytes) => _ = DeliverAsync(n, bytes);

            n.Watch.SessionInvited += (_, s) =>
            {
                if (!n.IsHost) Emit(n.Name, n.Color, $"invited to watch “{s.Title}”");
            };
            n.Watch.SyncApplied += (_, s) =>
            {
                lock (_gate) { n.PositionMs = s.PositionMs; n.IsPlaying = s.IsPlaying; n.Speed = s.PlaybackSpeed; n.Following = true; }
                var delta = s.PositionMs - _positionMs;
                var comp = delta > 0 ? $" (+{delta} ms for the {n.LatencyMs} ms link)" : "";
                Emit(n.Name, n.Color, $"{(s.IsPlaying ? "playing" : "paused")} at {Clock(s.PositionMs)}{comp}");
            };
            n.Watch.ReactionReceived += (_, r) =>
                Emit(n.Name, n.Color, $"sees {PetnameOf(r.SenderUhid)} react {r.Reaction} at {Clock(r.PositionMs)}");
            n.Watch.ChipInUpdated += (_, pool) =>
            {
                if (n.IsHost) { PoolCollected = pool.CollectedAmountZar; PoolTarget = pool.TargetAmountZar; }
            };
        }

        lock (_gate) _nodes.AddRange(all);
        Emit("mesh", "#2c3e50", "three nodes up. Alice can host; Bob and Charlie will follow and stay in sync.");
        RaiseChanged();
    }

    private Node CreateNode(string name, string color, bool isHost, int latencyMs)
    {
        var uhid = $"aether:lab-watch-{_salt}:{name.ToLowerInvariant()}";
        var (_, pubKey) = Ed25519SigningService.GenerateKeyPair();
        var tag = AetherNetTag.FromPublicKey(pubKey).Value;
        var transport = new InProcessTransportService(uhid, _loggerFactory.CreateLogger<InProcessTransportService>());
        var sender = new InProcessMeshSender(uhid, transport);
        var watch = new WatchTogetherService(sender, _routing);
        return new Node(name, uhid, color, tag, isHost, latencyMs, transport, sender, watch);
    }

    // Defer the inbound packet by this node's simulated link delay, then dispatch — so the
    // follower's RTT compensation has a real elapsed time to correct for.
    private async Task DeliverAsync(Node n, byte[] bytes)
    {
        if (n.LatencyMs > 0) await Task.Delay(n.LatencyMs).ConfigureAwait(false);
        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return; }
        await n.Watch.HandleAsync(packet).ConfigureAwait(false);
    }

    // ─── Actions ─────────────────────────────────────────────────────────────────

    /// <summary>Alice hosts; Bob and Charlie are invited and start following.</summary>
    public async Task HostAndFollowAsync()
    {
        if (_host is null || _sessionId is not null) return;
        var session = await _host.Watch.HostAsync("sha256:9f1c…match", "Match of the Day", WatchMode.SharedFile).ConfigureAwait(false);
        _sessionId = session.Id;
        Emit(_host.Name, _host.Color, "hosts “Match of the Day” — the room is open");
        await Task.Delay(200).ConfigureAwait(false); // let the invite reach both followers over their links

        foreach (var f in Followers())
        {
            await f.Watch.FollowAsync(session.Id).ConfigureAwait(false);
            lock (_gate) f.Following = true;
            Emit(f.Name, f.Color, "follows the host");
        }
        RaiseChanged();
    }

    public Task PlayAsync()
    {
        _isPlaying = true;
        return HostCommandAsync(h => h.Watch.PlayAsync(_sessionId!.Value, _positionMs), "presses play");
    }

    public Task PauseAsync()
    {
        _isPlaying = false;
        return HostCommandAsync(h => h.Watch.PauseAsync(_sessionId!.Value, _positionMs), "pauses");
    }

    public Task SeekForwardAsync()
    {
        _positionMs += 30_000; // jump ahead 30 s
        return HostCommandAsync(h => h.Watch.SeekAsync(_sessionId!.Value, _positionMs), $"seeks to {Clock(_positionMs)}");
    }

    public Task SetSpeedAsync(double speed)
    {
        _speed = speed;
        return HostCommandAsync(h => h.Watch.SetSpeedAsync(_sessionId!.Value, speed, _positionMs), $"sets speed ×{speed:0.##}");
    }

    private async Task HostCommandAsync(Func<Node, Task> cmd, string verb)
    {
        if (_host is null || _sessionId is null) return;
        Emit(_host.Name, _host.Color, verb);
        await cmd(_host).ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false); // let the slowest follower apply
        RaiseChanged();
    }

    /// <summary>A follower fires a reaction; it floods to everyone else in the room.</summary>
    public async Task ReactAsync(string followerName, string reaction)
    {
        if (_sessionId is not { } id) return;
        var f = NodeByName(followerName);
        if (f is null || f.IsHost) return;
        Emit(f.Name, f.Color, $"reacts {reaction}");
        await f.Watch.SendReactionAsync(id, reaction, f.PositionMs).ConfigureAwait(false);
        await Task.Delay(160).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>
    /// Host opens a chip-in pool toward a shared cost. The pool's existence is broadcast to
    /// followers; contributions are recorded on the host's ledger — <c>ContributeAsync</c> updates
    /// the local pool only (the src does not yet emit a contribution packet), so the demo aggregates
    /// pledges on the initiator to show the pool funding.
    /// </summary>
    public async Task StartChipInAsync()
    {
        if (_host is null || _sessionId is not { } id || _chipInId is not null) return;
        var pool = await _host.Watch.StartChipInAsync(id, 150m, "Data for tonight's stream", torrentInfoHash: null, magnetLink: null).ConfigureAwait(false);
        _chipInId = pool.Id;
        PoolTarget = pool.TargetAmountZar;
        PoolCollected = pool.CollectedAmountZar;
        Emit(_host.Name, _host.Color, $"opens a chip-in — target R{pool.TargetAmountZar:0}");
        await Task.Delay(120).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task ContributeAsync(string followerName, decimal amount)
    {
        if (_host is null || _chipInId is not { } poolId) return;
        var f = NodeByName(followerName);
        if (f is null) return;
        await _host.Watch.ContributeAsync(poolId, f.Uhid, amount).ConfigureAwait(false);
        Emit(f.Name, f.Color, $"chips in R{amount:0} — pool at R{PoolCollected:0}/R{PoolTarget:0}");
        await Task.Delay(80).ConfigureAwait(false);
        RaiseChanged();
    }

    public void ClearLog() { lock (_gate) _log.Clear(); RaiseChanged(); }

    // ─── Internals ───────────────────────────────────────────────────────────────

    private IEnumerable<Node> Followers() { lock (_gate) return _nodes.Where(n => !n.IsHost).ToArray(); }
    private Node? NodeByName(string name) { lock (_gate) return _nodes.FirstOrDefault(n => n.Name == name); }

    private void Emit(string who, string color, string text)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(who, color, text));
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private static string Clock(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    private static string PetnameOf(string uhid)
    {
        var parts = uhid.Split(':');
        var last = parts.Length == 0 ? uhid : parts[^1];
        return last.Length == 0 ? uhid : char.ToUpperInvariant(last[0]) + last[1..];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var n in _nodes) n.Transport.Dispose();
            _nodes.Clear();
        }
    }

    // ─── View + node models ────────────────────────────────────────────────────────

    public sealed record LogLine(string Who, string Color, string Text);

    public sealed record NodeView(string Name, string Color, bool IsHost, int LatencyMs,
        long PositionMs, bool IsPlaying, double Speed, bool Following, long DeltaMs);

    private sealed class Node
    {
        public Node(string name, string uhid, string color, string tag, bool isHost, int latencyMs,
            InProcessTransportService transport, InProcessMeshSender sender, WatchTogetherService watch)
        {
            Name = name; Uhid = uhid; Color = color; Tag = tag; IsHost = isHost; LatencyMs = latencyMs;
            Transport = transport; Sender = sender; Watch = watch;
        }

        public string Name { get; }
        public string Uhid { get; }
        public string Color { get; }
        public string Tag { get; }
        public bool IsHost { get; }
        public int LatencyMs { get; }
        public InProcessTransportService Transport { get; }
        public InProcessMeshSender Sender { get; }
        public WatchTogetherService Watch { get; }

        public long PositionMs { get; set; }
        public bool IsPlaying { get; set; }
        public double Speed { get; set; } = 1.0;
        public bool Following { get; set; }
    }
}
