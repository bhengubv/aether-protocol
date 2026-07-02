// SPDX-License-Identifier: MIT

namespace AetherNet.Sos;

/// <summary>
/// JSON payload for <see cref="Protocol.PacketType.SosAck"/> packets. Wire format: UTF-8 JSON with
/// snake_case property names. Every field is integer- or string-typed (no floating point), so the
/// encoding is byte-identical across all eight language ports.
///
/// <para>An <c>SosAck</c> is sent by a node that has just received an
/// <see cref="Protocol.PacketType.SosBroadcast"/>, directed back toward the alert's originator, so the
/// person raising the emergency learns their broadcast actually reached at least one device. The
/// acknowledging node's identity is carried by the enclosing packet's <c>SourceUhid</c> — it is not
/// duplicated in the body.</para>
/// </summary>
public sealed class SosAckPayload
{
    /// <summary>Id of the <see cref="Models.SosAlert"/> / SOS broadcast being acknowledged.</summary>
    public Guid BroadcastId { get; set; }

    /// <summary>Unix timestamp in milliseconds at which the acknowledging node received the SOS.</summary>
    public long ReceivedAtMs { get; set; }
}
