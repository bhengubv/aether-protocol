// SPDX-License-Identifier: MIT

namespace AetherNet.Streaming.Models;

/// <summary>
/// Lifecycle state of a live stream from this node's perspective.
/// </summary>
public enum StreamState : byte
{
    /// <summary>Initial state; <see cref="AetherNet.Protocol.PacketType.StreamAnnounce"/> not yet broadcast.</summary>
    Idle = 0,
    /// <summary>Publisher actively producing segments.</summary>
    Live = 1,
    /// <summary>Publisher has signalled end of stream; receivers may still drain buffered segments.</summary>
    Ending = 2,
    /// <summary>Stream has fully ended.</summary>
    Ended = 3,
}

/// <summary>
/// Direction relative to this node — are we publishing this stream or receiving it.
/// </summary>
public enum StreamRole : byte
{
    Publisher = 0,
    Subscriber = 1,
    Relay = 2,
}

/// <summary>
/// State for a single live stream — same identity from every node's perspective
/// (the stream id is global), with role-specific fields populated as appropriate.
/// </summary>
public sealed class StreamSession
{
    /// <summary>Globally unique stream id. Used as the correlation key for every announce/segment/subscribe packet.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>UHID of the publisher.</summary>
    public string PublisherUhid { get; set; } = string.Empty;

    /// <summary>This node's role for this stream.</summary>
    public StreamRole Role { get; set; } = StreamRole.Subscriber;

    /// <summary>Current lifecycle state.</summary>
    public StreamState State { get; set; } = StreamState.Idle;

    /// <summary>Publisher-supplied human-readable stream title. Hint only.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>MIME type of the segment payloads ("audio/opus", "video/h264", "video/mp4", …). Opaque to the protocol.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Codec name negotiated for this stream ("opus", "h264", "av1", …).</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Segment duration in milliseconds. Each <see cref="StreamSegment"/> covers exactly this much wall-clock audio/video.</summary>
    public int SegmentDurationMs { get; set; } = AetherNet.Constants.ProtocolConstants.StreamSegmentDurationMs;

    /// <summary>UTC timestamp the stream went live (or the announcement was received).</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp the stream ended, or null if still live.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Highest segment sequence number observed locally so far.</summary>
    public uint HighestSegmentSequence { get; set; }
}

/// <summary>
/// One segment of a live stream. Lifetime is short — receivers play and discard.
/// </summary>
public sealed class StreamSegment
{
    /// <summary>The stream this segment belongs to.</summary>
    public Guid StreamId { get; set; }

    /// <summary>Monotonically increasing per-stream sequence number. Wraps at uint32 max.</summary>
    public uint Sequence { get; set; }

    /// <summary>Publisher's monotonic clock at segment-start (ms).</summary>
    public long TimestampMs { get; set; }

    /// <summary>Encoded segment bytes — opaque to the protocol.</summary>
    public byte[] EncodedPayload { get; set; } = [];

    /// <summary>True if this segment is a keyframe / IDR / random-access point (subscribers can start playback here).</summary>
    public bool IsKeyframe { get; set; }
}
