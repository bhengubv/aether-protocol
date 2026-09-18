// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNet.Node.Host;

/// <summary>
/// The messaging seam the node host adapts to <see cref="IAetherNodeClient"/>. A platform supplies this
/// over the real <c>IMessagingService</c> (and its Signal decryption), so the host itself deals only in
/// tags and already-decrypted application payloads — never the wire format, the ratchet, or a UHID.
/// </summary>
public interface INodeMessaging
{
    /// <summary>Send a payload to a peer addressed by tag; the implementation resolves the wire address.</summary>
    Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>The most recent inbound messages, already projected to <see cref="InboundMessage"/>.</summary>
    Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Raised when a message arrives.</summary>
    event Action<InboundMessage>? Inbound;
}
