// SPDX-License-Identifier: MIT

using AetherMesh.Protocol;
using AetherMesh.Streaming.Models;

namespace AetherMesh.Streaming;

/// <summary>
/// Live broadcast streaming. Publishers call <see cref="StartStreamAsync"/> + <see cref="PublishSegmentAsync"/> in a loop +
/// <see cref="EndStreamAsync"/>. Subscribers see <see cref="StreamAnnounced"/> events, call <see cref="SubscribeAsync"/>,
/// receive <see cref="SegmentReceived"/> events, and call <see cref="UnsubscribeAsync"/> when done.
/// </summary>
public interface IStreamingService
{
    /// <summary>Raised when a peer announces a new live stream.</summary>
    event EventHandler<StreamSession>? StreamAnnounced;

    /// <summary>Raised when a peer subscribes to a stream this node is publishing.</summary>
    event EventHandler<SubscriberJoinedEventArgs>? SubscriberJoined;

    /// <summary>Raised when a peer unsubscribes from a stream this node is publishing.</summary>
    event EventHandler<SubscriberLeftEventArgs>? SubscriberLeft;

    /// <summary>Raised when a segment arrives for a stream we are subscribed to.</summary>
    event EventHandler<StreamSegment>? SegmentReceived;

    /// <summary>Raised when a publisher signals end-of-stream for a stream we are subscribed to.</summary>
    event EventHandler<StreamSession>? StreamEnded;

    /// <summary>Begin publishing a stream. Broadcasts a <see cref="PacketType.StreamAnnounce"/> packet.</summary>
    /// <param name="profile">ABR latency profile — determines the bitrate ladder used for this stream. Defaults to <see cref="StreamProfile.ProfileB"/> (live broadcast).</param>
    Task<StreamSession> StartStreamAsync(string title, string contentType, string codec, int segmentDurationMs, StreamProfile profile = StreamProfile.ProfileB, CancellationToken cancellationToken = default);

    /// <summary>Publish one segment to every current subscriber. Caller is responsible for pacing (typically <paramref name="segmentDurationMs"/> apart).</summary>
    Task PublishSegmentAsync(Guid streamId, ReadOnlyMemory<byte> encoded, uint sequence, bool isKeyframe, CancellationToken cancellationToken = default);

    /// <summary>End an in-flight stream we are publishing. Sends a final announce with state=Ended.</summary>
    Task EndStreamAsync(Guid streamId, CancellationToken cancellationToken = default);

    /// <summary>Subscribe to a peer's stream. Sends a <see cref="PacketType.StreamSubscribe"/> packet to the publisher.</summary>
    Task SubscribeAsync(Guid streamId, CancellationToken cancellationToken = default);

    /// <summary>Unsubscribe from a stream we are receiving.</summary>
    Task UnsubscribeAsync(Guid streamId, CancellationToken cancellationToken = default);

    /// <summary>Pump an inbound stream-related packet.</summary>
    Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    /// <summary>Streams currently being published or subscribed-to by the local node.</summary>
    IReadOnlyList<StreamSession> GetActiveStreams();

    /// <summary>
    /// Returns the current bitrate rung selected by the ABR controller for this stream.
    /// Returns <see langword="null"/> if the stream is not managed by an ABR controller
    /// (e.g. the stream was started before ABR support was added, or the id is unknown).
    /// </summary>
    BitrateRung? GetCurrentBitrateRung(Guid streamId);

    /// <summary>
    /// Updates the bandwidth estimate for a stream's ABR controller (Kbps).
    /// The controller adjusts the current rung and returns <see langword="true"/> if the rung changed.
    /// Returns <see langword="false"/> if the stream is unknown or the rung did not change.
    /// </summary>
    bool UpdateBandwidthEstimate(Guid streamId, long bandwidthKbps);
}

public sealed class SubscriberJoinedEventArgs : EventArgs
{
    public Guid StreamId { get; init; }
    public string SubscriberUhid { get; init; } = string.Empty;
}

public sealed class SubscriberLeftEventArgs : EventArgs
{
    public Guid StreamId { get; init; }
    public string SubscriberUhid { get; init; } = string.Empty;
}
