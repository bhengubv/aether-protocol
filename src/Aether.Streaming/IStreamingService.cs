// SPDX-License-Identifier: MIT

using Aether.Protocol;
using Aether.Streaming.Models;

namespace Aether.Streaming;

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
    Task<StreamSession> StartStreamAsync(string title, string contentType, string codec, int segmentDurationMs, CancellationToken cancellationToken = default);

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
