// SPDX-License-Identifier: MIT

namespace Aether.Streaming.Models;

/// <summary>
/// State of a video call from this node's perspective. Same shape as the audio-only
/// counterpart in Aether.Voice but with video-specific resolution + codec metadata.
/// </summary>
public enum VideoCallState : byte
{
    Idle = 0,
    Outgoing = 1,
    Incoming = 2,
    Connected = 3,
    Ended = 4,
    Failed = 5,
}

public enum VideoHangupReason : byte
{
    Normal = 0,
    Busy = 1,
    Declined = 2,
    Timeout = 3,
    NetworkFailure = 4,
    CodecMismatch = 5,
    BandwidthInsufficient = 6,
    Unknown = 255,
}

/// <summary>
/// Video call session state. Tracks codec / resolution / frame-rate negotiated for
/// this call. Frame I/O happens via the
/// <see cref="Aether.Protocol.PacketType.VideoFrame"/> packet, signaling via
/// <see cref="Aether.Protocol.PacketType.VideoSignaling"/>.
/// </summary>
public sealed class VideoCallSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CallerUhid { get; set; } = string.Empty;
    public string CalleeUhid { get; set; } = string.Empty;
    public VideoCallState State { get; set; } = VideoCallState.Idle;

    /// <summary>Negotiated video codec name ("h264", "h265", "vp8", "av1", …).</summary>
    public string VideoCodec { get; set; } = string.Empty;

    /// <summary>Negotiated audio codec name ("opus", "speex", …). Empty if call is video-only.</summary>
    public string AudioCodec { get; set; } = string.Empty;

    /// <summary>Negotiated resolution.</summary>
    public VideoResolution Resolution { get; set; } = VideoResolution.R480p;

    /// <summary>Target frames per second.</summary>
    public int TargetFps { get; set; } = 30;

    /// <summary>Indicative bitrate in kbps the publisher is targeting.</summary>
    public int TargetBitrateKbps { get; set; } = 500;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConnectedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public VideoHangupReason? HangupReason { get; set; }

    public string RemoteUhid(string localUhid) =>
        string.Equals(localUhid, CallerUhid, StringComparison.Ordinal) ? CalleeUhid : CallerUhid;
}

/// <summary>
/// One encoded video frame in flight. Frame ordering and pacing are subscriber concerns;
/// the protocol just delivers bytes with a sequence + timestamp + keyframe flag.
/// </summary>
public sealed class VideoFrame
{
    public Guid CallId { get; set; }
    public string SenderUhid { get; set; } = string.Empty;
    public uint Sequence { get; set; }
    public long TimestampMs { get; set; }

    /// <summary>True for IDR / random-access frames. Receivers decode-from-start at these.</summary>
    public bool IsKeyframe { get; set; }

    /// <summary>Encoded video bytes — opaque to the protocol.</summary>
    public byte[] EncodedPayload { get; set; } = [];
}

/// <summary>
/// Kind discriminator for video signaling messages.
/// </summary>
public enum VideoSignalingKind : byte
{
    Offer = 0,
    Answer = 1,
    Hangup = 2,
    Cancel = 3,
    /// <summary>Either side requests the remote to send a keyframe (e.g. after observed packet loss).</summary>
    KeyframeRequest = 4,
    /// <summary>Either side reports a bitrate / resolution change; receiver may downshift.</summary>
    QualityChange = 5,
}

/// <summary>
/// Signaling envelope for <see cref="Aether.Protocol.PacketType.VideoSignaling"/> packets.
/// JSON-encoded with snake_case names so non-.NET implementations can produce / consume identical bytes.
/// </summary>
public sealed class VideoSignalingMessage
{
    public VideoSignalingKind Kind { get; set; }
    public Guid CallId { get; set; }
    public string FromUhid { get; set; } = string.Empty;
    public string ToUhid { get; set; } = string.Empty;

    /// <summary>For Offer: video codecs the caller supports, in preference order.</summary>
    public IReadOnlyList<string> ProposedVideoCodecs { get; set; } = Array.Empty<string>();

    /// <summary>For Offer: audio codecs the caller supports.</summary>
    public IReadOnlyList<string> ProposedAudioCodecs { get; set; } = Array.Empty<string>();

    /// <summary>For Answer: chosen video codec.</summary>
    public string SelectedVideoCodec { get; set; } = string.Empty;

    /// <summary>For Answer: chosen audio codec ("" = video-only).</summary>
    public string SelectedAudioCodec { get; set; } = string.Empty;

    /// <summary>For Offer / Answer / QualityChange: target resolution.</summary>
    public VideoResolution Resolution { get; set; } = VideoResolution.R480p;

    /// <summary>For Offer / Answer / QualityChange: target FPS.</summary>
    public int TargetFps { get; set; } = 30;

    /// <summary>For Offer / Answer / QualityChange: target bitrate in kbps.</summary>
    public int TargetBitrateKbps { get; set; } = 500;

    public VideoHangupReason Reason { get; set; } = VideoHangupReason.Unknown;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}
