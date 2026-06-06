// SPDX-License-Identifier: MIT

namespace AetherNet.Streaming.Models;

/// <summary>
/// Cross-language stable wire payload for <see cref="AetherNet.Protocol.PacketType.StreamAnnounce"/>.
/// Broadcast at stream start (and periodically while live) so newly arriving subscribers
/// can discover available streams.
/// </summary>
public sealed class StreamAnnouncePayload
{
    public Guid StreamId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public string Codec { get; set; } = string.Empty;
    public int SegmentDurationMs { get; set; }
    public StreamState State { get; set; } = StreamState.Live;
    public long StartedAtMs { get; set; }
}

/// <summary>
/// Wire payload for <see cref="AetherNet.Protocol.PacketType.StreamSubscribe"/>.
/// </summary>
public sealed class StreamSubscribePayload
{
    public Guid StreamId { get; set; }

    /// <summary>If true, the subscriber wants all keyframes from now on (no historical seek).</summary>
    public bool LiveOnly { get; set; } = true;
}

/// <summary>
/// Wire payload for <see cref="AetherNet.Protocol.PacketType.StreamUnsubscribe"/>.
/// </summary>
public sealed class StreamUnsubscribePayload
{
    public Guid StreamId { get; set; }
}
