// SPDX-License-Identifier: MIT

using AetherNet.Protocol;

namespace AetherNet.Channels;

/// <summary>
/// Application-layer named-channel pub/sub over <see cref="PacketType.ChannelMessage"/>. A node
/// subscribes to channel ids it cares about; publishing floods the mesh; subscribed receivers surface
/// the message via <see cref="MessageReceived"/>. Messages are de-duplicated by
/// <see cref="ChannelMessagePayload.MessageId"/> and re-flooded (TTL-bounded) so they reach subscribers
/// several hops away.
/// </summary>
public interface IChannelMessageService
{
    /// <summary>Raised when a message arrives on a subscribed channel (not raised for this node's own messages).</summary>
    event EventHandler<ChannelMessageReceived>? MessageReceived;

    /// <summary>Subscribe to a channel — messages on it will raise <see cref="MessageReceived"/>.</summary>
    void Subscribe(string channelId);

    /// <summary>Stop surfacing messages for a channel.</summary>
    void Unsubscribe(string channelId);

    /// <summary>The channels this node is currently subscribed to.</summary>
    IReadOnlyList<string> GetSubscriptions();

    /// <summary>
    /// Publish <paramref name="content"/> to <paramref name="channelId"/>: floods a signed-by-nobody
    /// <see cref="PacketType.ChannelMessage"/> to all peers. Returns the number of peers reached directly.
    /// </summary>
    Task<int> PublishAsync(string channelId, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Process an incoming <see cref="PacketType.ChannelMessage"/> packet: de-dup by message id, surface
    /// it if we are subscribed to its channel (and it is not our own), and re-flood while TTL allows.
    /// Returns false for the wrong packet type, a malformed payload, or a duplicate.
    /// </summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}
