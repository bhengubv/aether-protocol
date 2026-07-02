// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Media;

/// <summary>A push-to-talk audio frame (PacketType.VoicePtt = 15 body).</summary>
public sealed class VoicePttFrame
{
    public Guid CallId { get; set; }
    public uint Sequence { get; set; }
    public long TimestampMs { get; set; }
    public bool IsSilence { get; set; }
    public byte[] EncodedPayload { get; set; } = Array.Empty<byte>();
}

/// <summary>A screen-share video frame (PacketType.ScreenShare = 32 body).</summary>
public sealed class ScreenShareFrame
{
    public Guid CallId { get; set; }
    public uint Sequence { get; set; }
    public long TimestampMs { get; set; }
    public bool IsKeyframe { get; set; }
    public byte[] EncodedPayload { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Binary codec for the VoicePtt(15) and ScreenShare(32) media frames. Both share the exact 29-byte
/// header used by the existing VoiceCall(16)/VideoFrame(31) frames, so a node can treat them uniformly:
///   [0..15]  call_id       — 16 bytes, RFC-4122 BIG-ENDIAN (Guid.TryWriteBytes(bigEndian:true))
///   [16..19] sequence      — u32 LITTLE-ENDIAN
///   [20..27] timestamp_ms  — i64 LITTLE-ENDIAN
///   [28]     flag          — u8 (VoicePtt: is_silence; ScreenShare: is_keyframe)
///   [29..]   payload       — opaque encoded audio/video bytes
/// Byte-identity gate: fixtures/media/vectors.json (expected_hex). The call_id is big-endian (network
/// order), NOT the .NET mixed-endian Guid.ToByteArray() layout — mirror this in every language.
/// </summary>
public static class MediaFrameCodec
{
    private const int HeaderLength = 29;

    public static byte[] SerializeVoicePtt(VoicePttFrame f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return Serialize(f.CallId, f.Sequence, f.TimestampMs, f.IsSilence, f.EncodedPayload);
    }

    public static byte[] SerializeScreenShare(ScreenShareFrame f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return Serialize(f.CallId, f.Sequence, f.TimestampMs, f.IsKeyframe, f.EncodedPayload);
    }

    private static byte[] Serialize(Guid callId, uint sequence, long timestampMs, bool flag, byte[] payload)
    {
        payload ??= Array.Empty<byte>();
        var buf = new byte[HeaderLength + payload.Length];
        callId.TryWriteBytes(buf.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(20, 8), timestampMs);
        buf[28] = flag ? (byte)1 : (byte)0;
        payload.CopyTo(buf.AsSpan(HeaderLength));
        return buf;
    }

    public static VoicePttFrame DeserializeVoicePtt(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderLength) throw new FormatException("VoicePtt frame too short");
        return new VoicePttFrame
        {
            CallId = new Guid(b.Slice(0, 16), bigEndian: true),
            Sequence = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(16, 4)),
            TimestampMs = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(20, 8)),
            IsSilence = b[28] != 0,
            EncodedPayload = b[HeaderLength..].ToArray(),
        };
    }

    public static ScreenShareFrame DeserializeScreenShare(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderLength) throw new FormatException("ScreenShare frame too short");
        return new ScreenShareFrame
        {
            CallId = new Guid(b.Slice(0, 16), bigEndian: true),
            Sequence = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(16, 4)),
            TimestampMs = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(20, 8)),
            IsKeyframe = b[28] != 0,
            EncodedPayload = b[HeaderLength..].ToArray(),
        };
    }
}

/// <summary>Event args: an inbound VoicePtt frame plus the peer that sent it.</summary>
public sealed class VoicePttFrameReceived : EventArgs
{
    public VoicePttFrame Frame { get; init; } = new();
    public string FromUhid { get; init; } = string.Empty;
}

/// <summary>Event args: an inbound ScreenShare frame plus the peer that sent it.</summary>
public sealed class ScreenShareFrameReceived : EventArgs
{
    public ScreenShareFrame Frame { get; init; } = new();
    public string FromUhid { get; init; } = string.Empty;
}

/// <summary>Binds <see cref="PacketType.VoicePtt"/> (15) to the mesh: directed push-to-talk audio frames + inbound event.</summary>
public interface IVoicePttService
{
    event EventHandler<VoicePttFrameReceived>? FrameReceived;
    Task<bool> SendFrameAsync(string peerUhid, VoicePttFrame frame, CancellationToken cancellationToken = default);
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}

/// <summary>Binds <see cref="PacketType.ScreenShare"/> (32) to the mesh: directed screen-share video frames + inbound event.</summary>
public interface IScreenShareService
{
    event EventHandler<ScreenShareFrameReceived>? FrameReceived;
    Task<bool> SendFrameAsync(string peerUhid, ScreenShareFrame frame, CancellationToken cancellationToken = default);
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class VoicePttService : IVoicePttService
{
    private readonly IMeshSender _sender;
    private readonly ILogger<VoicePttService> _logger;
    public event EventHandler<VoicePttFrameReceived>? FrameReceived;

    public VoicePttService(IMeshSender sender, ILogger<VoicePttService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<VoicePttService>.Instance;
    }

    public async Task<bool> SendFrameAsync(string peerUhid, VoicePttFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(frame);
        var packet = new MeshPacket
        {
            Type = PacketType.VoicePtt,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = peerUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = MediaFrameCodec.SerializeVoicePtt(frame),
        };
        return await _sender.SendAsync(packet, peerUhid, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.VoicePtt) return Task.FromResult(false);
        VoicePttFrame frame;
        try { frame = MediaFrameCodec.DeserializeVoicePtt(packet.Payload); }
        catch (FormatException ex) { _logger.LogDebug(ex, "VoicePtt from {Src}: malformed — dropped", packet.SourceUhid); return Task.FromResult(false); }
        FrameReceived?.Invoke(this, new VoicePttFrameReceived { Frame = frame, FromUhid = packet.SourceUhid });
        return Task.FromResult(true);
    }
}

/// <inheritdoc />
public sealed class ScreenShareService : IScreenShareService
{
    private readonly IMeshSender _sender;
    private readonly ILogger<ScreenShareService> _logger;
    public event EventHandler<ScreenShareFrameReceived>? FrameReceived;

    public ScreenShareService(IMeshSender sender, ILogger<ScreenShareService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<ScreenShareService>.Instance;
    }

    public async Task<bool> SendFrameAsync(string peerUhid, ScreenShareFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(frame);
        var packet = new MeshPacket
        {
            Type = PacketType.ScreenShare,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = peerUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = MediaFrameCodec.SerializeScreenShare(frame),
        };
        return await _sender.SendAsync(packet, peerUhid, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.ScreenShare) return Task.FromResult(false);
        ScreenShareFrame frame;
        try { frame = MediaFrameCodec.DeserializeScreenShare(packet.Payload); }
        catch (FormatException ex) { _logger.LogDebug(ex, "ScreenShare from {Src}: malformed — dropped", packet.SourceUhid); return Task.FromResult(false); }
        FrameReceived?.Invoke(this, new ScreenShareFrameReceived { Frame = frame, FromUhid = packet.SourceUhid });
        return Task.FromResult(true);
    }
}
