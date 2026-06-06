// SPDX-License-Identifier: MIT

namespace AetherNet.Voice.Models;

/// <summary>
/// A single encoded audio frame in flight. The payload is whatever the negotiated
/// codec produces; this layer treats it as opaque bytes. The metadata fields are
/// what the jitter buffer uses to order, drop, and pace frames into playback.
/// </summary>
public sealed class VoiceFrame
{
    /// <summary>The call this frame belongs to.</summary>
    public Guid CallId { get; set; }

    /// <summary>UHID of the node that produced this frame.</summary>
    public string SenderUhid { get; set; } = string.Empty;

    /// <summary>Monotonically increasing per-call sequence number. Wraps at uint32 max.</summary>
    public uint Sequence { get; set; }

    /// <summary>Sender's monotonic clock (ms). Used by the jitter buffer to compute delivery skew.</summary>
    public long TimestampMs { get; set; }

    /// <summary>Encoded audio bytes — opaque to this layer.</summary>
    public byte[] EncodedPayload { get; set; } = [];

    /// <summary>True if this frame contains only silence (the encoder said so). Lets the jitter buffer drop without artifacts.</summary>
    public bool IsSilence { get; set; }
}
