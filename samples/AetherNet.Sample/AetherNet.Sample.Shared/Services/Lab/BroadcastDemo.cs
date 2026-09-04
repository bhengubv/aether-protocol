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
/// Drives a live broadcast entirely in-process against the real <see cref="StreamingService"/>.
/// One node (Alice) goes live and publishes segments; the others (Bob, Charlie) discover the
/// announce, subscribe, and receive exactly the segments addressed to them. A single bandwidth
/// dial feeds the publisher's real <see cref="AdaptiveBitrateController"/>, so the rung the page
/// highlights — and the point at which the link falls below the floor and the publisher abandons
/// the segment instead of shipping a degraded one — are the protocol's own decisions, not a script.
///
/// The radio is the only simulated part: an <see cref="InProcessTransportService"/> carries the
/// real binary wire between nodes. UHIDs are salted with a per-instance id so a fresh mesh stands
/// up on every visit and never collides with the other Lab demos on the shared node registry.
/// </summary>
public sealed class BroadcastDemo : IDisposable
{
    private const int MaxLogLines = 200;
    private const StreamProfile Profile = StreamProfile.ProfileB; // live broadcast ladder
    private const int SegmentDurationMs = 2_000;

    private readonly ILoggerFactory _loggerFactory;
    private readonly DirectRoutingService _routing = new();
    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();
    private readonly List<Node> _nodes = new();
    private readonly string _salt = Guid.NewGuid().ToString("N")[..6];

    private Guid? _streamId;
    private uint _sequence;
    private long _bandwidthKbps = 10_000;
    private bool _started;
    private bool _disposed;

    public BroadcastDemo(ILoggerFactory? loggerFactory = null)
        => _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <summary>Raised whenever the log or node state changes; the page re-renders on it.</summary>
    public event Action? Changed;

    public IReadOnlyList<BitrateRung> Ladder => BitrateLadder.ForProfile(Profile);
    public BitrateRung? CurrentRung => _streamId is { } id ? _publisher?.Streaming.GetCurrentBitrateRung(id) : null;
    public long BandwidthKbps => _bandwidthKbps;

    /// <summary>The combined audio+video Kbps of the floor rung — below this the publisher abandons.</summary>
    public long FloorKbps { get { var f = Ladder[0]; return f.AudioKbps + f.VideoKbps; } }

    public bool IsLive => _streamId is not null && (_publisher?.Streaming.GetActiveStreams().Any(s => s.Id == _streamId && s.State == StreamState.Live) ?? false);
    public bool WillAbandon => IsLive && _bandwidthKbps < FloorKbps;
    public int SubscriberCount { get { lock (_gate) return _nodes.Count(n => n.Subscribed); } }

    /// <summary>Segments actually shipped (an abandoned segment does not count).</summary>
    public int SegmentsPushed { get; private set; }

    private Node? _publisher;

    public IReadOnlyList<LogLine> Snapshot() { lock (_gate) return _log.ToArray(); }

    public IReadOnlyList<NodeView> Nodes()
    {
        lock (_gate)
            return _nodes.Select(n => new NodeView(n.Name, n.Color, n.IsPublisher, n.Subscribed, n.SegmentsSeen, n.LastSeq)).ToArray();
    }

    // ─── Setup ───────────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        var alice = CreateNode("Alice", "#2196F3", isPublisher: true);
        var bob = CreateNode("Bob", "#1976D2", isPublisher: false);
        var charlie = CreateNode("Charlie", "#2c3e50", isPublisher: false);
        _publisher = alice;

        var all = new[] { alice, bob, charlie };
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
                _ = n.Streaming.HandleAsync(packet);
            };

            // A viewer discovers a live stream it did not start.
            n.Streaming.StreamAnnounced += (_, session) =>
            {
                if (!n.IsPublisher)
                    Emit(n.Name, n.Color, $"sees a live stream — “{session.Title}” ({session.Codec})");
            };
            // The publisher learns a viewer has subscribed.
            n.Streaming.SubscriberJoined += (_, e) =>
            {
                MarkSubscribed(e.SubscriberUhid);
                Emit(n.Name, n.Color, $"{PetnameOf(e.SubscriberUhid)} subscribed — now serving {SubscriberCount} viewer(s)");
            };
            // A viewer receives a segment addressed to it.
            n.Streaming.SegmentReceived += (_, seg) =>
            {
                RecordSegment(n, seg.Sequence);
                Emit(n.Name, n.Color,
                    $"◀ segment #{seg.Sequence}{(seg.IsKeyframe ? " (keyframe)" : "")} — {seg.EncodedPayload.Length} B");
            };
            n.Streaming.StreamEnded += (_, _) =>
            {
                if (!n.IsPublisher) Emit(n.Name, n.Color, "stream ended — draining");
            };
        }

        lock (_gate) _nodes.AddRange(all);
        Emit("mesh", "#2c3e50", "three nodes up. Alice can publish; Bob and Charlie can subscribe and receive.");
        RaiseChanged();
    }

    private Node CreateNode(string name, string color, bool isPublisher)
    {
        var uhid = $"aether:lab-bcast-{_salt}:{name.ToLowerInvariant()}";
        var (_, pubKey) = Ed25519SigningService.GenerateKeyPair();
        var tag = AetherNetTag.FromPublicKey(pubKey).Value;
        var transport = new InProcessTransportService(uhid, _loggerFactory.CreateLogger<InProcessTransportService>());
        var sender = new InProcessMeshSender(uhid, transport);
        var streaming = new StreamingService(sender, _routing);
        return new Node(name, uhid, color, tag, isPublisher, transport, sender, streaming);
    }

    // ─── Actions ─────────────────────────────────────────────────────────────────

    /// <summary>Alice starts broadcasting; the announce floods to every node.</summary>
    public async Task GoLiveAsync()
    {
        if (_publisher is null || _streamId is not null) return;
        var session = await _publisher.Streaming.StartStreamAsync(
            "Neighbourhood live", "video/h264", "h264", SegmentDurationMs, Profile).ConfigureAwait(false);
        _streamId = session.Id;
        _sequence = 0;
        // Seed the controller at the current dial so the highlighted rung matches the link straight away.
        _publisher.Streaming.UpdateBandwidthEstimate(session.Id, _bandwidthKbps);
        Emit(_publisher.Name, _publisher.Color, $"goes live — “{session.Title}”, announcing to the mesh");
        await Task.Delay(80).ConfigureAwait(false); // let the announce settle on the viewers
        RaiseChanged();
    }

    /// <summary>A named viewer subscribes to Alice's stream.</summary>
    public async Task SubscribeAsync(string viewerName)
    {
        if (_streamId is not { } id) return;
        var viewer = NodeByName(viewerName);
        if (viewer is null || viewer.IsPublisher) return;
        Emit(viewer.Name, viewer.Color, "subscribing to the stream…");
        await viewer.Streaming.SubscribeAsync(id).ConfigureAwait(false);
        await Task.Delay(80).ConfigureAwait(false); // let the publisher register the subscriber
        RaiseChanged();
    }

    /// <summary>Alice ships the next segment to every current subscriber (or abandons it if the link collapsed).</summary>
    public async Task PublishNextSegmentAsync()
    {
        if (_publisher is null || _streamId is not { } id) return;
        var seq = _sequence++;
        var isKeyframe = seq % 4 == 0; // an IDR every four segments
        var payload = new byte[512];
        Random.Shared.NextBytes(payload);
        var abandon = WillAbandon;

        if (abandon)
        {
            Emit(_publisher.Name, _publisher.Color,
                $"link below floor ({_bandwidthKbps} < {FloorKbps} Kbps) — abandons segment #{seq} rather than ship a broken one");
        }
        else
        {
            SegmentsPushed++;
            Emit(_publisher.Name, _publisher.Color,
                $"▶ pushes segment #{seq}{(isKeyframe ? " (keyframe)" : "")} at {CurrentRung?.Label} · {CurrentRung?.VideoQuality}");
        }

        await _publisher.Streaming.PublishSegmentAsync(id, payload, seq, isKeyframe).ConfigureAwait(false);
        await Task.Delay(60).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>Feed the publisher's ABR controller a new measured link speed.</summary>
    public void SetBandwidth(long kbps)
    {
        _bandwidthKbps = Math.Clamp(kbps, 50, 20_000);
        if (_streamId is { } id && _publisher is not null)
        {
            var changed = _publisher.Streaming.UpdateBandwidthEstimate(id, _bandwidthKbps);
            var rung = _publisher.Streaming.GetCurrentBitrateRung(id);
            Emit(_publisher.Name, _publisher.Color,
                changed
                    ? $"link now {_bandwidthKbps} Kbps → shifts to {rung?.Label} · {rung?.VideoQuality}"
                    : $"link now {_bandwidthKbps} Kbps — holds {rung?.Label} · {rung?.VideoQuality}");
        }
        RaiseChanged();
    }

    public async Task EndAsync()
    {
        if (_publisher is null || _streamId is not { } id) return;
        await _publisher.Streaming.EndStreamAsync(id).ConfigureAwait(false);
        Emit(_publisher.Name, _publisher.Color, "ends the broadcast");
        _streamId = null;
        await Task.Delay(60).ConfigureAwait(false);
        RaiseChanged();
    }

    public void ClearLog() { lock (_gate) _log.Clear(); RaiseChanged(); }

    // ─── Internals ───────────────────────────────────────────────────────────────

    private Node? NodeByName(string name) { lock (_gate) return _nodes.FirstOrDefault(n => n.Name == name); }

    private void MarkSubscribed(string uhid)
    {
        lock (_gate)
        {
            var node = _nodes.FirstOrDefault(n => n.Uhid == uhid);
            if (node is not null) node.Subscribed = true;
        }
    }

    private void RecordSegment(Node node, uint seq)
    {
        lock (_gate) { node.LastSeq = seq; node.SegmentsSeen++; }
    }

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

    private static string PetnameOf(string uhid)
    {
        var parts = uhid.Split(':');
        var last = parts[^1];
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

    public sealed record NodeView(string Name, string Color, bool IsPublisher, bool Subscribed, int SegmentsSeen, long LastSeq);

    private sealed class Node
    {
        public Node(string name, string uhid, string color, string tag, bool isPublisher,
            InProcessTransportService transport, InProcessMeshSender sender, StreamingService streaming)
        {
            Name = name; Uhid = uhid; Color = color; Tag = tag; IsPublisher = isPublisher;
            Transport = transport; Sender = sender; Streaming = streaming;
        }

        public string Name { get; }
        public string Uhid { get; }
        public string Color { get; }
        public string Tag { get; }
        public bool IsPublisher { get; }
        public InProcessTransportService Transport { get; }
        public InProcessMeshSender Sender { get; }
        public StreamingService Streaming { get; }

        public bool Subscribed { get; set; }
        public long LastSeq { get; set; } = -1;
        public int SegmentsSeen { get; set; }
    }
}
