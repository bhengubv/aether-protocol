// SPDX-License-Identifier: MIT

using AetherNet.Constants;
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
/// Drives a group video call's <b>control plane</b> against the real <see cref="GroupVideoService"/>.
/// The host (Alice) opens a call and invites three peers; each is admitted over the signaling wire,
/// and when the active roster reaches <see cref="ProtocolConstants.SfuThresholdParticipants"/> the
/// service switches topology from full-mesh to SFU on its own and names a relay — the same decision
/// a phone would make on the real radio, made here in-process.
///
/// A compact 1:1 leg exercises the sibling <see cref="VideoCallService"/>: Alice rings Bob with a
/// codec/resolution offer, Bob answers, and the negotiated result is what the pair actually agreed.
/// </summary>
public sealed class GroupVideoDemo : IDisposable
{
    private const int MaxLogLines = 200;

    private readonly ILoggerFactory _loggerFactory;
    private readonly DirectRoutingService _routing = new();
    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();
    private readonly List<Node> _nodes = new();
    private readonly Queue<Node> _pending = new();
    private readonly string _salt = Guid.NewGuid().ToString("N")[..6];

    private Node? _host;
    private Guid? _sessionId;
    private Guid? _callId;
    private bool _started;
    private bool _disposed;

    public GroupVideoDemo(ILoggerFactory? loggerFactory = null)
        => _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public event Action? Changed;

    public int SfuThreshold => ProtocolConstants.SfuThresholdParticipants;
    public bool CallOpen => _sessionId is not null;
    public bool HasPending { get { lock (_gate) return _pending.Count > 0; } }
    public string? NextInvitee { get { lock (_gate) return _pending.Count > 0 ? _pending.Peek().Name : null; } }

    public IReadOnlyList<LogLine> Snapshot() { lock (_gate) return _log.ToArray(); }

    /// <summary>The host's authoritative view of the call: roster, topology and relay.</summary>
    public GroupView Group()
    {
        if (_host is null || _sessionId is not { } id)
            return new GroupView(false, "FullMesh", null, 0, Array.Empty<ParticipantView>());

        var session = _host.Group.GetActiveSessions().FirstOrDefault(s => s.Id == id);
        if (session is null)
            return new GroupView(false, "FullMesh", null, 0, Array.Empty<ParticipantView>());

        var parts = session.Participants
            .Where(p => !p.HasLeft)
            .Select(p => new ParticipantView(
                NameFor(p.Uhid),
                ColorFor(p.Uhid),
                Res(p.Resolution),
                p.VideoCodec,
                string.Equals(p.Uhid, session.HostUhid, StringComparison.Ordinal),
                string.Equals(p.Uhid, session.SfuRelayUhid, StringComparison.Ordinal)))
            .ToArray();

        var active = parts.Length;
        return new GroupView(active >= SfuThreshold, session.Topology.ToString(),
            session.SfuRelayUhid is null ? null : NameFor(session.SfuRelayUhid), active, parts);
    }

    /// <summary>The 1:1 leg's current state, or null if never rung.</summary>
    public CallView? OneToOne()
    {
        if (_host is null || _callId is not { } id) return null;
        var call = _host.Video?.GetActiveCalls().FirstOrDefault(c => c.Id == id)
                   ?? _host.Video?.GetActiveCalls().FirstOrDefault();
        if (call is null) return _lastCall;
        _lastCall = new CallView(call.State.ToString(), call.VideoCodec, call.AudioCodec, Res(call.Resolution), call.TargetFps, call.TargetBitrateKbps);
        return _lastCall;
    }

    private CallView? _lastCall;

    // ─── Setup ───────────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        var alice = CreateNode("Alice", "#2196F3", withVideo: true);
        var bob = CreateNode("Bob", "#1976D2", withVideo: true);
        var charlie = CreateNode("Charlie", "#2c3e50", withVideo: false);
        var dara = CreateNode("Dara", "#1565C0", withVideo: false);
        _host = alice;

        var all = new[] { alice, bob, charlie, dara };
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
                switch (packet.Type)
                {
                    case PacketType.GroupVideoSignaling:
                        _ = n.Group.HandleAsync(packet);
                        break;
                    case PacketType.VideoSignaling:
                        if (n.Video is not null) _ = n.Video.HandleAsync(packet);
                        break;
                    case PacketType.VideoFrame:
                        // A video frame could belong to the group session or the 1:1 call; each
                        // service ignores ids it does not own, so offer it to both.
                        _ = n.Group.HandleAsync(packet);
                        if (n.Video is not null) _ = n.Video.HandleAsync(packet);
                        break;
                }
            };

            n.Group.SessionCreated += (_, _) =>
            {
                if (!ReferenceEquals(n, _host)) Emit(n.Name, n.Color, "sees the group call open — invited");
            };
            n.Group.TopologyChanged += (_, s) =>
                Emit(n.Name, n.Color, $"topology → {s.Topology}" + (s.SfuRelayUhid is null ? "" : $", relay {NameFor(s.SfuRelayUhid)}"));

            if (n.Video is not null)
            {
                n.Video.IncomingCall += (_, c) => Emit(n.Name, n.Color, $"incoming 1:1 video from {NameFor(c.CallerUhid)}");
                n.Video.CallConnected += (_, c) => Emit(n.Name, n.Color, $"1:1 connected — {c.VideoCodec}/{c.AudioCodec} @ {Res(c.Resolution)}");
                n.Video.CallEnded += (_, _) => Emit(n.Name, n.Color, "1:1 call ended");
            }
        }

        lock (_gate) _nodes.AddRange(all);
        Emit("mesh", "#2c3e50", $"four nodes up. Full-mesh until {SfuThreshold} are on the call, then the service switches to SFU.");
        RaiseChanged();
    }

    private Node CreateNode(string name, string color, bool withVideo)
    {
        var uhid = $"aether:lab-gvid-{_salt}:{name.ToLowerInvariant()}";
        var (_, pubKey) = Ed25519SigningService.GenerateKeyPair();
        var tag = AetherNetTag.FromPublicKey(pubKey).Value;
        var transport = new InProcessTransportService(uhid, _loggerFactory.CreateLogger<InProcessTransportService>());
        var sender = new InProcessMeshSender(uhid, transport);
        var group = new GroupVideoService(sender, _routing);
        var video = withVideo ? new VideoCallService(sender, _routing) : null;
        return new Node(name, uhid, color, tag, transport, sender, group, video);
    }

    // ─── Actions: group control plane ─────────────────────────────────────────────

    /// <summary>Alice opens the call and invites the other three; they queue for admission.</summary>
    public async Task OpenCallAsync()
    {
        if (_host is null || _sessionId is not null) return;
        var invitees = _nodes.Where(n => !ReferenceEquals(n, _host)).ToArray();
        var session = await _host.Group.CreateAsync(
            invitees.Select(n => n.Uhid).ToArray(), VideoResolution.R720p, "H264", 1500).ConfigureAwait(false);
        _sessionId = session.Id;
        lock (_gate) { _pending.Clear(); foreach (var n in invitees) _pending.Enqueue(n); }
        Emit(_host.Name, _host.Color, $"opens a group call — invites {string.Join(", ", invitees.Select(n => n.Name))}");
        await Task.Delay(90).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>Admit the next queued invitee over the control plane.</summary>
    public async Task AdmitNextAsync()
    {
        if (_sessionId is not { } id) return;
        Node next;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            next = _pending.Dequeue();
        }
        Emit(next.Name, next.Color, "joins the call");
        await next.Group.JoinAsync(id, VideoResolution.R720p, "H264", 1500).ConfigureAwait(false);
        await Task.Delay(110).ConfigureAwait(false); // let the host update the roster + maybe flip to SFU
        var g = Group();
        if (g.IsSfu)
            Emit("call", "#2c3e50", $"{g.ActiveCount} on the call → SFU, {g.RelayName} relays for everyone");
        RaiseChanged();
    }

    // ─── Actions: 1:1 leg (VideoCallService) ──────────────────────────────────────

    /// <summary>Alice rings Bob 1:1 with a codec/resolution offer; Bob answers; they negotiate.</summary>
    public async Task RingBobAsync()
    {
        var alice = _host;
        var bob = NodeByName("Bob");
        if (alice?.Video is null || bob?.Video is null || _callId is not null) return;

        Emit(alice.Name, alice.Color, "rings Bob 1:1 — offers h264/vp8, opus, 480p");
        var call = await alice.Video.PlaceAsync(bob.Uhid, new[] { "h264", "vp8" }, new[] { "opus" }, VideoResolution.R480p, 30, 500).ConfigureAwait(false);
        _callId = call.Id;
        await Task.Delay(90).ConfigureAwait(false);
        await bob.Video.AnswerAsync(call.Id, "h264", "opus", VideoResolution.R480p, 30, 500).ConfigureAwait(false);
        await Task.Delay(90).ConfigureAwait(false);
        // One keyframe so FrameReceived has something real to carry.
        var frame = new byte[256];
        Random.Shared.NextBytes(frame);
        await alice.Video.SendFrameAsync(call.Id, frame, 0, isKeyframe: true).ConfigureAwait(false);
        await Task.Delay(60).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task HangupAsync()
    {
        var alice = _host;
        if (alice?.Video is null || _callId is not { } id) return;
        await alice.Video.HangupAsync(id).ConfigureAwait(false);
        _callId = null;
        await Task.Delay(60).ConfigureAwait(false);
        RaiseChanged();
    }

    public void ClearLog() { lock (_gate) _log.Clear(); RaiseChanged(); }

    // ─── Internals ───────────────────────────────────────────────────────────────

    private Node? NodeByName(string name) { lock (_gate) return _nodes.FirstOrDefault(n => n.Name == name); }

    private string NameFor(string uhid)
    {
        lock (_gate)
        {
            var n = _nodes.FirstOrDefault(x => x.Uhid == uhid);
            return n?.Name ?? PetnameOf(uhid);
        }
    }

    private string ColorFor(string uhid)
    {
        lock (_gate)
            return _nodes.FirstOrDefault(x => x.Uhid == uhid)?.Color ?? "#2c3e50";
    }

    private static string Res(VideoResolution r) => r switch
    {
        VideoResolution.AudioOnly => "audio",
        VideoResolution.R360p => "360p",
        VideoResolution.R480p => "480p",
        VideoResolution.R720p => "720p",
        VideoResolution.R1080p => "1080p",
        _ => r.ToString(),
    };

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

    public sealed record ParticipantView(string Name, string Color, string Resolution, string Codec, bool IsHost, bool IsRelay);

    public sealed record GroupView(bool IsSfu, string Topology, string? RelayName, int ActiveCount, IReadOnlyList<ParticipantView> Participants);

    public sealed record CallView(string State, string VideoCodec, string AudioCodec, string Resolution, int Fps, int BitrateKbps);

    private sealed class Node
    {
        public Node(string name, string uhid, string color, string tag,
            InProcessTransportService transport, InProcessMeshSender sender, GroupVideoService group, VideoCallService? video)
        {
            Name = name; Uhid = uhid; Color = color; Tag = tag;
            Transport = transport; Sender = sender; Group = group; Video = video;
        }

        public string Name { get; }
        public string Uhid { get; }
        public string Color { get; }
        public string Tag { get; }
        public InProcessTransportService Transport { get; }
        public InProcessMeshSender Sender { get; }
        public GroupVideoService Group { get; }
        public VideoCallService? Video { get; }
    }
}
