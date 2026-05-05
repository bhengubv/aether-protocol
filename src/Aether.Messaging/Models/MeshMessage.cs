// SPDX-License-Identifier: MIT

namespace Aether.Messaging.Models;

/// <summary>
/// Lifecycle state of a 1-to-1 mesh message.
/// </summary>
public enum MessageStatus : byte
{
    /// <summary>Message is in the outbox awaiting an opportunity to send. May be waiting on a Signal session.</summary>
    Pending = 0,
    /// <summary>The host is actively attempting to send.</summary>
    Sending = 1,
    /// <summary>Handed off to transport, DTN, or a backend relay successfully.</summary>
    Sent = 2,
    /// <summary>Delivery confirmed by the recipient (an Ack packet has been received).</summary>
    Delivered = 3,
    /// <summary>Permanent failure after retry exhaustion.</summary>
    Failed = 4,
}

/// <summary>
/// A 1-to-1 encrypted message moving through the mesh. The
/// <see cref="EncryptedContent"/> field is the only payload that goes on the wire;
/// plaintext never leaves the originating node and is never persisted by the
/// messaging layer itself.
/// </summary>
public sealed class MeshMessage
{
    /// <summary>Globally unique message identifier. Used as the correlation key for delivery acks.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>UHID of the sender. Set by <c>MessagingService</c> at send time.</summary>
    public string SenderUhid { get; set; } = string.Empty;

    /// <summary>UHID of the intended recipient.</summary>
    public string RecipientUhid { get; set; } = string.Empty;

    /// <summary>Ciphertext produced by <see cref="IMessageEnvelopeCipher.EncryptAsync"/>. Treated as opaque bytes.</summary>
    public byte[] EncryptedContent { get; set; } = [];

    /// <summary>Caller-defined message kind ("text", "image", "voice-clip", …). Opaque to the protocol.</summary>
    public string MessageType { get; set; } = "text";

    /// <summary>Priority of the underlying <see cref="Aether.Protocol.MeshPacket"/>. Defaults to 0.</summary>
    public byte Priority { get; set; }

    /// <summary>Optional reply-to message id. Null for top-level messages.</summary>
    public Guid? ReplyToId { get; set; }

    /// <summary>Current lifecycle state.</summary>
    public MessageStatus Status { get; set; } = MessageStatus.Pending;

    /// <summary>Number of send attempts so far.</summary>
    public int RetryCount { get; set; }

    /// <summary>UTC timestamp at which the message was enqueued locally.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Receipt sent back from a recipient confirming a message was delivered.
/// Carries observed transport metadata so the sender can reason about route quality.
/// </summary>
public sealed class DeliveryReceipt
{
    /// <summary>The id of the message being acknowledged.</summary>
    public Guid MessageId { get; set; }

    /// <summary>UHID of the original sender (so receivers know who to address the receipt to).</summary>
    public string SenderUhid { get; set; } = string.Empty;

    /// <summary>UHID of the recipient (the node confirming delivery).</summary>
    public string RecipientUhid { get; set; } = string.Empty;

    /// <summary>Hop count observed by the recipient.</summary>
    public int HopCount { get; set; }

    /// <summary>Round-trip latency in milliseconds, when measurable.</summary>
    public int LatencyMs { get; set; }

    /// <summary>Free-form transport identifier ("ble", "wifi-direct", "dtn", "backend-relay", …).</summary>
    public string TransportType { get; set; } = string.Empty;

    /// <summary>UTC timestamp recorded by the recipient at delivery.</summary>
    public DateTime DeliveredAt { get; set; } = DateTime.UtcNow;
}
