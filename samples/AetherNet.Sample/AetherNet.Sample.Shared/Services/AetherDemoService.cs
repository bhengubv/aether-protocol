// SPDX-License-Identifier: MIT

using AetherNet.Channels;
using AetherNet.Identity;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Services;
using AetherNet.Transport.Services;
using AetherNet.VideoCallControl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Drives a live three-node AetherNet mesh entirely in-process so the sample UI can
/// show the protocol with zero infrastructure. Everything below is the real code the
/// eight language SDKs port — real Ed25519 identities, the real channel + video
/// call-control services, the real binary wire — only the radio is simulated by an
/// in-process byte transport.
///
/// It demonstrates, in order, exactly the platform goal:
///   1. devices join the mesh and detect one another (every node relays),
///   2. each identity resolves to a shareable AetherTag for 1:1 add-by-tag,
///   3. group text floods a named channel to every subscriber,
///   4. group video call-control rings/accepts/hangs-up across the same mesh.
///
/// One instance owns one mesh, so register it <b>scoped</b> — one mesh per Blazor
/// circuit (Server) or per app session (MAUI).
/// </summary>
public sealed class AetherDemoService : IDisposable
{
    private const int MaxLogLines = 300;
    private const string WatchChannel = "aether:chan:neighbourhood-watch";

    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();
    private readonly List<Node> _nodes = new();
    private bool _started;
    private bool _disposed;

    public AetherDemoService(ILoggerFactory? loggerFactory = null)
        => _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <summary>Raised whenever the log or node state changes; the UI re-renders on it.</summary>
    public event Action? Changed;

    /// <summary>The channel the group-text demo publishes to.</summary>
    public string GroupChannel => WatchChannel;

    /// <summary>A point-in-time snapshot of the wire log, oldest first.</summary>
    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate)
            return _log.ToArray();
    }

    /// <summary>The three mesh nodes with their identity, AetherTag and detected peers.</summary>
    public IReadOnlyList<NodeView> Nodes()
    {
        lock (_gate)
            return _nodes.Select(n => n.ToView()).ToArray();
    }

    // ─── Setup ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stand up the mesh: generate identities, join the in-process network, wire each
    /// node's inbound dispatcher, and subscribe all three to the neighbourhood channel.
    /// Idempotent — safe to call on every page render.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;
        }

        // Clear any leftover network state from a prior circuit/session.
        InProcessTransportService.ResetNetwork();

        var alice = CreateNode("Alice", "aether:alice:01", "#2196F3", isRelay: false);
        var bob = CreateNode("Bob", "aether:bob:02", "#1976D2", isRelay: false);
        var charlie = CreateNode("Charlie", "aether:charlie:03", "#2c3e50", isRelay: true);

        // Everyone is a potential peer of everyone else — a fully-connected 3-node mesh.
        foreach (var self in new[] { alice, bob, charlie })
        foreach (var other in new[] { alice, bob, charlie })
            if (!ReferenceEquals(self, other))
                self.Sender.AddPotentialPeer(other.Uhid);

        // One inbound-wire dispatcher per node: route each packet to the right service.
        foreach (var node in new[] { alice, bob, charlie })
        {
            var n = node; // capture
            n.Transport.DataReceived += (_src, bytes) =>
            {
                MeshPacket packet;
                try { packet = PacketSerializer.Deserialize(bytes); }
                catch { return; }
                _ = Dispatch(packet, n);
            };
        }

        // Surface what every node receives, straight into the wire log.
        foreach (var node in new[] { alice, bob, charlie })
        {
            var n = node;
            n.Channel.MessageReceived += (_, m) =>
                Emit(n.Name, n.Color, $"received on {Petname(m.ChannelId)}: “{m.Content}”", LogKind.Text);
            n.Video.CallStateChanged += (_, e) =>
                Emit(n.Name, n.Color, $"call {Short(e.CallId)}: '{e.Action}' from {Petname(e.FromUhid)}", LogKind.Video);
        }

        lock (_gate)
            _nodes.AddRange(new[] { alice, bob, charlie });

        foreach (var node in new[] { alice, bob, charlie })
            node.Channel.Subscribe(WatchChannel);

        Emit("mesh", "#2c3e50",
            $"{InProcessTransportService.ActiveNodeCount} devices joined. Every node detects the others and can relay for them.",
            LogKind.System);
        Emit("mesh", "#2c3e50",
            $"All three subscribed to {Petname(WatchChannel)}.",
            LogKind.System);
        RaiseChanged();
    }

    private Node CreateNode(string name, string uhid, string color, bool isRelay)
    {
        var (_, pubKey) = Ed25519SigningService.GenerateKeyPair();
        var tag = AetherNetTag.FromPublicKey(pubKey).Value;
        var transport = new InProcessTransportService(
            uhid, _loggerFactory.CreateLogger<InProcessTransportService>());
        var sender = new InProcessMeshSender(uhid, transport);
        return new Node(name, uhid, color, isRelay, pubKey, tag, transport,
            sender, new ChannelMessageService(sender), new VideoCallControlService(sender));
    }

    private static Task Dispatch(MeshPacket packet, Node node) => packet.Type switch
    {
        PacketType.ChannelMessage => node.Channel.HandleAsync(packet),
        PacketType.VideoCall => node.Video.HandleAsync(packet),
        _ => Task.CompletedTask,
    };

    // ─── Demo 1: AetherTag 1:1 (verify-before-trust) ─────────────────────────────

    /// <summary>
    /// Bob adds Alice by her shared AetherTag and verifies the tag really belongs to
    /// her key — then proves the same tag can't be forged onto an impostor's key.
    /// </summary>
    public void VerifyAetherTag()
    {
        var alice = NodeByName("Alice");
        var bob = NodeByName("Bob");
        if (alice is null || bob is null)
            return;

        Emit(bob.Name, bob.Color, $"adding Alice by tag {alice.Tag} — verifying it against her key…", LogKind.Tag);
        var ok = AetherNetTag.Verify(alice.Tag, alice.PubKey);
        Emit(bob.Name, bob.Color,
            ok ? "Verify(Alice's tag, Alice's key): MATCH — identity confirmed"
               : "Verify(Alice's tag, Alice's key): NO MATCH",
            ok ? LogKind.Tag : LogKind.Warn);

        var (_, impostorPub) = Ed25519SigningService.GenerateKeyPair();
        var forged = AetherNetTag.Verify(alice.Tag, impostorPub);
        Emit(bob.Name, bob.Color,
            forged ? "Verify(Alice's tag, impostor key): MATCH (BAD!)"
                   : "Verify(Alice's tag, impostor key): REJECTED — a tag can't be forged onto another key.",
            forged ? LogKind.Warn : LogKind.Tag);
        RaiseChanged();
    }

    // ─── Demo 2: Group text over a named channel ─────────────────────────────────

    /// <summary>Alice publishes one message; it floods to every subscriber on the channel.</summary>
    public async Task PublishGroupTextAsync(string message)
    {
        var alice = NodeByName("Alice");
        if (alice is null || string.IsNullOrWhiteSpace(message))
            return;

        Emit(alice.Name, alice.Color, $"publishing to {Petname(WatchChannel)}: “{message}”", LogKind.Text);
        var reached = await alice.Channel.PublishAsync(WatchChannel, message).ConfigureAwait(false);
        await Task.Delay(120).ConfigureAwait(false); // let the flood + async dispatch settle
        Emit("mesh", "#2c3e50",
            $"one publish → flooded to {reached} peer(s); every subscriber surfaced it.", LogKind.System);
        RaiseChanged();
    }

    // ─── Demo 3: Video call-control (1:1 and group) ──────────────────────────────

    /// <summary>Alice rings Bob 1:1; Bob accepts; Alice hangs up — full call-control handshake.</summary>
    public async Task OneToOneVideoAsync()
    {
        var alice = NodeByName("Alice");
        var bob = NodeByName("Bob");
        if (alice is null || bob is null)
            return;

        Emit("video", "#2c3e50", "[1:1] Alice rings Bob…", LogKind.System);
        var call = await alice.Video.RingAsync(bob.Uhid).ConfigureAwait(false);
        await Task.Delay(90).ConfigureAwait(false);
        await bob.Video.AcceptAsync(call, alice.Uhid).ConfigureAwait(false);
        await Task.Delay(90).ConfigureAwait(false);
        Emit("video", "#2c3e50", "connected — media (frames / SDP / ICE) rides the streaming layer. Alice hangs up.", LogKind.System);
        await alice.Video.HangupAsync(call, bob.Uhid).ConfigureAwait(false);
        await Task.Delay(90).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>Alice rings Bob AND Charlie; both accept — a 3-way group call over the control plane.</summary>
    public async Task GroupVideoAsync()
    {
        var alice = NodeByName("Alice");
        var bob = NodeByName("Bob");
        var charlie = NodeByName("Charlie");
        if (alice is null || bob is null || charlie is null)
            return;

        Emit("video", "#2c3e50", "[group] Alice rings Bob AND Charlie…", LogKind.System);
        var toBob = await alice.Video.RingAsync(bob.Uhid).ConfigureAwait(false);
        var toCharlie = await alice.Video.RingAsync(charlie.Uhid).ConfigureAwait(false);
        await Task.Delay(90).ConfigureAwait(false);
        await bob.Video.AcceptAsync(toBob, alice.Uhid).ConfigureAwait(false);
        await charlie.Video.AcceptAsync(toCharlie, alice.Uhid).ConfigureAwait(false);
        await Task.Delay(120).ConfigureAwait(false);
        Emit("video", "#2c3e50", "both accepted → a 3-way group video call, established over the control plane.", LogKind.System);
        RaiseChanged();
    }

    /// <summary>Clears the wire log (keeps the mesh up).</summary>
    public void ClearLog()
    {
        lock (_gate)
            _log.Clear();
        RaiseChanged();
    }

    // ─── Internals ────────────────────────────────────────────────────────────────

    private Node? NodeByName(string name)
    {
        lock (_gate)
            return _nodes.FirstOrDefault(n => n.Name == name);
    }

    private void Emit(string who, string color, string text, LogKind kind)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(who, color, text, kind));
            if (_log.Count > MaxLogLines)
                _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private static string Short(Guid id) => id.ToString()[..8];

    // Render "aether:chan:neighbourhood-watch" -> "#neighbourhood-watch",
    // "aether:alice:01" -> "Alice-ish" fallback to the last segment otherwise.
    private static string Petname(string uhid)
    {
        if (uhid.StartsWith("aether:chan:", StringComparison.Ordinal))
            return "#" + uhid["aether:chan:".Length..];
        var parts = uhid.Split(':');
        return parts.Length >= 2 ? char.ToUpperInvariant(parts[1][0]) + parts[1][1..] : uhid;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var node in _nodes)
                node.Transport.Dispose();
            _nodes.Clear();
        }
    }

    // ─── View models the UI binds to ─────────────────────────────────────────────

    public enum LogKind { System, Text, Video, Tag, Warn }

    public sealed record LogLine(string Who, string Color, string Text, LogKind Kind);

    public sealed record NodeView(string Name, string Uhid, string Tag, string Color, bool IsRelay, IReadOnlyList<string> Peers);

    private sealed class Node
    {
        public Node(string name, string uhid, string color, bool isRelay, byte[] pubKey, string tag,
            InProcessTransportService transport, InProcessMeshSender sender,
            ChannelMessageService channel, VideoCallControlService video)
        {
            Name = name;
            Uhid = uhid;
            Color = color;
            IsRelay = isRelay;
            PubKey = pubKey;
            Tag = tag;
            Transport = transport;
            Sender = sender;
            Channel = channel;
            Video = video;
        }

        public string Name { get; }
        public string Uhid { get; }
        public string Color { get; }
        public bool IsRelay { get; }
        public byte[] PubKey { get; }
        public string Tag { get; }
        public InProcessTransportService Transport { get; }
        public InProcessMeshSender Sender { get; }
        public ChannelMessageService Channel { get; }
        public VideoCallControlService Video { get; }

        public NodeView ToView()
        {
            var peers = Sender.GetConnectedPeers()
                .Select(p => PetnameOf(p.Uhid))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            return new NodeView(Name, Uhid, Tag, Color, IsRelay, peers);
        }

        private static string PetnameOf(string uhid)
        {
            var parts = uhid.Split(':');
            return parts.Length >= 2 ? char.ToUpperInvariant(parts[1][0]) + parts[1][1..] : uhid;
        }
    }
}

/// <summary>
/// Bridges the packet-level <see cref="IMeshSender"/> (consumed by the channel and
/// video call-control services) to the byte-level in-process transport. Reports a
/// peer as connected only while its transport is live in the simulated network — so
/// the UI's "detected peers" reflects real reachability, not a static list.
/// </summary>
internal sealed class InProcessMeshSender : IMeshSender
{
    private readonly HashSet<string> _potentialPeers = new(StringComparer.Ordinal);
    private readonly InProcessTransportService _transport;

    public InProcessMeshSender(string localUhid, InProcessTransportService transport)
    {
        LocalUhid = localUhid;
        _transport = transport;
    }

    public string LocalUhid { get; }
    public string? LocalGeohash => null;

    public void AddPotentialPeer(string uhid) => _potentialPeers.Add(uhid);

    public IReadOnlyList<PeerInfo> GetConnectedPeers()
    {
        var alive = new List<PeerInfo>();
        foreach (var uhid in _potentialPeers)
            if (_transport.IsConnected(uhid))
                alive.Add(new PeerInfo { Uhid = uhid, TransportType = "InProcess" });
        return alive;
    }

    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
        => _transport.SendAsync(nextHopUhid, PacketSerializer.Serialize(packet), cancellationToken);

    public async Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        var bytes = PacketSerializer.Serialize(packet);
        var delivered = 0;
        foreach (var uhid in _potentialPeers)
            if (await _transport.SendAsync(uhid, bytes, cancellationToken).ConfigureAwait(false))
                delivered++;
        return delivered;
    }
}
