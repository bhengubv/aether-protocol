// SPDX-License-Identifier: MIT

using AetherNet.Messaging.Models;
using AetherNet.Protocol;

namespace AetherNet.Messaging;

/// <summary>
/// 1-to-1 encrypted messaging service. Hosts call <see cref="SendAsync"/> to enqueue
/// outbound messages, pump received <see cref="PacketType.Data"/> and <see cref="PacketType.Ack"/>
/// packets through <see cref="HandleAsync"/>, and run <see cref="ProcessOutboxAsync"/>
/// periodically to retry pending messages.
/// </summary>
public interface IMessagingService
{
    /// <summary>Raised when a new message arrives for the local node and is successfully decrypted.</summary>
    event EventHandler<MeshMessage>? MessageReceived;

    /// <summary>
    /// Raised when an Ack packet matching one of our outbound messages comes back.
    /// Receivers can use this to mark threads as "seen by recipient".
    /// </summary>
    event EventHandler<DeliveryReceipt>? DeliveryConfirmed;

    /// <summary>
    /// Raised when an outbound send is queued because no Signal session exists with the recipient.
    /// Hosts typically respond by initiating a pre-key fetch + session bootstrap.
    /// </summary>
    event EventHandler<string>? SessionRequired;

    /// <summary>
    /// Encrypts (if a session exists), persists, and attempts to deliver a message.
    /// Returns true if the message was handed off to a transport, DTN, or backend relay.
    /// Returns false if the message was queued (no session yet, or no delivery path) — it
    /// stays in the outbox for the next <see cref="ProcessOutboxAsync"/> pass.
    ///
    /// Callers populate <paramref name="message"/>'s <see cref="MeshMessage.RecipientUhid"/>
    /// and any plaintext bytes in <see cref="MeshMessage.EncryptedContent"/>; the service
    /// fills in the sender, encrypts, and assigns the final ciphertext to the same field
    /// before persistence.
    /// </summary>
    Task<bool> SendAsync(MeshMessage message, byte[] plaintext, CancellationToken cancellationToken = default);

    /// <summary>Pump an inbound DATA or ACK packet into the messaging layer.</summary>
    Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-attempt pending outbox messages. Returns the number successfully sent
    /// during this pass. Messages that exhaust <see cref="MessagingOptions.MaxRetries"/>
    /// transition to <see cref="MessageStatus.Failed"/>.
    /// </summary>
    Task<int> ProcessOutboxAsync(CancellationToken cancellationToken = default);

    /// <summary>Most-recent inbox messages addressed to the local node, newest first.</summary>
    Task<IReadOnlyList<MeshMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Most-recent outbox messages from the local node, newest first.</summary>
    Task<IReadOnlyList<MeshMessage>> GetOutboxAsync(int limit = 50, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tunables for <see cref="MessagingService"/>. Defaults match the private CircleAether
/// reference implementation.
/// </summary>
public sealed class MessagingOptions
{
    /// <summary>Maximum send attempts before a message transitions to <see cref="MessageStatus.Failed"/>.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>If true, fall back to DTN store-and-forward when no live route exists. Default true.</summary>
    public bool EnableDtnFallback { get; set; } = true;

    /// <summary>
    /// If true, fall back to the registered <see cref="AetherNet.Extensibility.IAetherNetBackendClient"/>'s
    /// relay path when no mesh route exists. Default true; only effective if a backend client is wired.
    /// </summary>
    public bool EnableBackendRelay { get; set; } = true;

    /// <summary>If true, send a delivery <see cref="PacketType.Ack"/> back to the sender on every received message. Default true.</summary>
    public bool SendDeliveryAcks { get; set; } = true;

    /// <summary>
    /// Optional Brotli payload compression. See <see cref="CompressionOptions"/>
    /// for the migration concern around the unconditional flag byte; defaults
    /// to enabled but adopters can set <c>Compression.Enabled = false</c> during
    /// rollout until all peers understand the flag.
    /// </summary>
    public CompressionOptions Compression { get; set; } = new();
}
