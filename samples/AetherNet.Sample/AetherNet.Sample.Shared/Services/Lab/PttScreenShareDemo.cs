// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Media;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// Sends push-to-talk audio frames and screen-share video frames between in-process nodes against
/// the real <see cref="VoicePttService"/> and <see cref="ScreenShareService"/>. Both ride the same
/// 29-byte media header the voice and video-call frames use — 16-byte big-endian call id, LE u32
/// sequence, LE i64 timestamp, one flag byte — so the point of the demo is to watch a frame arrive
/// at the addressed peer and show that header decoded straight off the wire.
///
/// Frames are <b>directed</b>: Alice unicasts to Bob, and Charlie — on the same mesh — receives
/// nothing, which is the whole difference between a media frame and a broadcast.
/// </summary>
public sealed class PttScreenShareDemo : IDisposable
{
    private const int MaxLogLines = 200;

    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();
    private readonly List<Node> _nodes = new();
    private readonly string _salt = Guid.NewGuid().ToString("N")[..6];

    private Node? _alice, _bob;
    private HeaderView? _lastHeader;
    private bool _started;
    private bool _disposed;

    public PttScreenShareDemo(ILoggerFactory? loggerFactory = null)
        => _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public event Action? Changed;

    public HeaderView? LastHeader { get { lock (_gate) return _lastHeader; } }

    public IReadOnlyList<LogLine> Snapshot() { lock (_gate) return _log.ToArray(); }

    public IReadOnlyList<NodeView> Nodes()
    {
        lock (_gate)
            return _nodes.Select(n => new NodeView(n.Name, n.Color, n.IsSender, n.PttReceived, n.ScreenReceived)).ToArray();
    }

    // ─── Setup ───────────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        var alice = CreateNode("Alice", "#2196F3", isSender: true);
        var bob = CreateNode("Bob", "#1976D2", isSender: false);
        var charlie = CreateNode("Charlie", "#2c3e50", isSender: false);
        _alice = alice;
        _bob = bob;

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
                switch (packet.Type)
                {
                    case PacketType.VoicePtt: _ = n.Ptt.HandleAsync(packet); break;
                    case PacketType.ScreenShare: _ = n.Screen.HandleAsync(packet); break;
                }
            };

            n.Ptt.FrameReceived += (_, e) =>
            {
                lock (_gate) n.PttReceived++;
                CaptureHeader("audio", MediaFrameCodec.SerializeVoicePtt(e.Frame), e.Frame.CallId, e.Frame.Sequence,
                    e.Frame.TimestampMs, e.Frame.IsSilence ? "silence" : "voiced", e.Frame.EncodedPayload.Length);
                Emit(n.Name, n.Color,
                    $"◀ PTT from {PetnameOf(e.FromUhid)} — seq {e.Frame.Sequence}, {(e.Frame.IsSilence ? "silence" : "voiced")}, {e.Frame.EncodedPayload.Length} B");
            };
            n.Screen.FrameReceived += (_, e) =>
            {
                lock (_gate) n.ScreenReceived++;
                CaptureHeader("video", MediaFrameCodec.SerializeScreenShare(e.Frame), e.Frame.CallId, e.Frame.Sequence,
                    e.Frame.TimestampMs, e.Frame.IsKeyframe ? "keyframe" : "delta", e.Frame.EncodedPayload.Length);
                Emit(n.Name, n.Color,
                    $"◀ screen from {PetnameOf(e.FromUhid)} — seq {e.Frame.Sequence}, {(e.Frame.IsKeyframe ? "keyframe" : "delta")}, {e.Frame.EncodedPayload.Length} B");
            };
        }

        lock (_gate) _nodes.AddRange(all);
        Emit("mesh", "#2c3e50", "three nodes up. Alice can talk to Bob and share her screen to Bob; Charlie is on the mesh but not addressed.");
        RaiseChanged();
    }

    private Node CreateNode(string name, string color, bool isSender)
    {
        var uhid = $"aether:lab-media-{_salt}:{name.ToLowerInvariant()}";
        var (_, pubKey) = Ed25519SigningService.GenerateKeyPair();
        var tag = AetherNetTag.FromPublicKey(pubKey).Value;
        var transport = new InProcessTransportService(uhid, _loggerFactory.CreateLogger<InProcessTransportService>());
        var sender = new InProcessMeshSender(uhid, transport);
        return new Node(name, uhid, color, tag, isSender, transport, sender,
            new VoicePttService(sender), new ScreenShareService(sender));
    }

    // ─── Actions ─────────────────────────────────────────────────────────────────

    /// <summary>Alice holds the button and sends a short PTT burst (voiced frames, then a silence tail) to Bob.</summary>
    public async Task PushToTalkAsync()
    {
        if (_alice is null || _bob is null) return;
        var callId = Guid.NewGuid();
        Emit(_alice.Name, _alice.Color, "holds talk → Bob");
        for (uint seq = 0; seq < 3; seq++)
        {
            var payload = new byte[40];
            Random.Shared.NextBytes(payload); // stand-in for encoded Opus
            var frame = new VoicePttFrame
            {
                CallId = callId,
                Sequence = seq,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsSilence = false,
                EncodedPayload = payload,
            };
            await _alice.Ptt.SendFrameAsync(_bob.Uhid, frame).ConfigureAwait(false);
            await Task.Delay(45).ConfigureAwait(false);
        }
        // Release: one silence frame closes the burst.
        await _alice.Ptt.SendFrameAsync(_bob.Uhid, new VoicePttFrame
        {
            CallId = callId,
            Sequence = 3,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsSilence = true,
            EncodedPayload = Array.Empty<byte>(),
        }).ConfigureAwait(false);
        await Task.Delay(45).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>Alice shares her screen to Bob — a keyframe then two delta frames.</summary>
    public async Task ShareScreenAsync()
    {
        if (_alice is null || _bob is null) return;
        var callId = Guid.NewGuid();
        Emit(_alice.Name, _alice.Color, "shares screen → Bob");
        for (uint seq = 0; seq < 3; seq++)
        {
            var isKeyframe = seq == 0;
            var payload = new byte[isKeyframe ? 120 : 60];
            Random.Shared.NextBytes(payload); // stand-in for encoded H.264
            var frame = new ScreenShareFrame
            {
                CallId = callId,
                Sequence = seq,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsKeyframe = isKeyframe,
                EncodedPayload = payload,
            };
            await _alice.Screen.SendFrameAsync(_bob.Uhid, frame).ConfigureAwait(false);
            await Task.Delay(50).ConfigureAwait(false);
        }
        RaiseChanged();
    }

    public void ClearLog()
    {
        lock (_gate) { _log.Clear(); _lastHeader = null; }
        RaiseChanged();
    }

    // ─── Internals ───────────────────────────────────────────────────────────────

    // Rebuild the exact 29-byte wire header from the decoded frame (the header is a pure function of
    // the frame's fields), split into the four fields the codec lays down, for the decode panel.
    private void CaptureHeader(string kind, byte[] wire, Guid callId, uint seq, long ts, string flag, int payloadLen)
    {
        var h = wire.AsSpan(0, 29);
        var view = new HeaderView(
            kind,
            callId.ToString()[..8],
            Convert.ToHexString(h.Slice(0, 16)),
            seq, Convert.ToHexString(h.Slice(16, 4)),
            ts, Convert.ToHexString(h.Slice(20, 8)),
            flag, Convert.ToHexString(h.Slice(28, 1)),
            payloadLen);
        lock (_gate) _lastHeader = view;
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

    public sealed record NodeView(string Name, string Color, bool IsSender, int PttReceived, int ScreenReceived);

    /// <summary>The 29-byte media header of the most recent frame, decoded field by field.</summary>
    public sealed record HeaderView(
        string Kind,
        string CallIdShort, string CallIdHex,
        uint Sequence, string SeqHex,
        long TimestampMs, string TsHex,
        string FlagLabel, string FlagHex,
        int PayloadLen);

    private sealed class Node
    {
        public Node(string name, string uhid, string color, string tag, bool isSender,
            InProcessTransportService transport, InProcessMeshSender sender, VoicePttService ptt, ScreenShareService screen)
        {
            Name = name; Uhid = uhid; Color = color; Tag = tag; IsSender = isSender;
            Transport = transport; Sender = sender; Ptt = ptt; Screen = screen;
        }

        public string Name { get; }
        public string Uhid { get; }
        public string Color { get; }
        public string Tag { get; }
        public bool IsSender { get; }
        public InProcessTransportService Transport { get; }
        public InProcessMeshSender Sender { get; }
        public VoicePttService Ptt { get; }
        public ScreenShareService Screen { get; }

        public int PttReceived { get; set; }
        public int ScreenReceived { get; set; }
    }
}
