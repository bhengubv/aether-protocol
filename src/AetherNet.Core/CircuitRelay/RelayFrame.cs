// SPDX-License-Identifier: MIT

namespace AetherNet.Models;

/// <summary>
/// Message type of a <see cref="RelayFrame"/> — the circuit-relay-v2 control/data verbs.
/// Any AetherNet node can act as a relay: a client that cannot reach a target directly
/// reserves capacity on a relay it <em>can</em> reach, asks the relay to bridge to the
/// target, and then tunnels data through the bridge. This is the native, no-libp2p
/// equivalent of libp2p circuit-relay-v2's HOP/STOP protocol.
/// </summary>
public enum RelayMessageType : byte
{
    /// <summary>Client → relay: request a reservation (permission to be relayed to).</summary>
    Reserve = 1,
    /// <summary>Relay → client: reservation grant/refusal + limits (see <see cref="RelayStatus"/>).</summary>
    ReserveResponse = 2,
    /// <summary>Client → relay: bridge me to <see cref="RelayFrame.DestinationUhid"/>.</summary>
    Connect = 3,
    /// <summary>Relay → target: client <see cref="RelayFrame.SourceUhid"/> wants to reach you.</summary>
    Stop = 4,
    /// <summary>Target → relay: accept/reject the inbound bridge.</summary>
    StopResponse = 5,
    /// <summary>Relay → client: bridge established/refused.</summary>
    ConnectResponse = 6,
    /// <summary>Either endpoint → relay → other endpoint: opaque tunnelled payload.</summary>
    Data = 7,
}

/// <summary>
/// Status carried by a relay response frame. Mirrors the libp2p circuit-relay-v2 status
/// codes closely enough to be intuitive, but is an independent native enum.
/// </summary>
public enum RelayStatus : byte
{
    /// <summary>Success (reservation granted / bridge established / no error).</summary>
    Ok = 0,
    /// <summary>Relay declined to reserve capacity for the client.</summary>
    ReservationRefused = 1,
    /// <summary>Connect attempted without a valid reservation.</summary>
    NoReservation = 2,
    /// <summary>The bridge's data or duration budget was exhausted.</summary>
    ResourceLimitExceeded = 3,
    /// <summary>Policy denied the reservation or connection.</summary>
    PermissionDenied = 4,
    /// <summary>Relay could not reach / was refused by the target.</summary>
    ConnectionFailed = 5,
    /// <summary>A received frame was malformed.</summary>
    MalformedMessage = 6,
}

/// <summary>
/// A single circuit-relay-v2 wire frame. One fixed-layout struct carries every verb
/// (type-discriminated) so the format is trivial to keep byte-identical across all eight
/// language SDKs. It rides in <c>MeshPacket.Payload</c> the same way the DTN envelope does.
///
/// <para>Serialized by <see cref="AetherNet.CircuitRelay.RelayFrameSerializer"/>. All
/// multi-byte integers are little-endian; the 16-byte <see cref="ConnectionId"/> is the
/// <see cref="Guid"/> in RFC-4122 big-endian order; strings are uint16-LE length-prefixed
/// UTF-8; the payload is int32-LE length-prefixed raw bytes and always last.</para>
/// </summary>
public sealed class RelayFrame
{
    /// <summary>Which verb this frame carries.</summary>
    public RelayMessageType Type { get; set; }

    /// <summary>Result code (meaningful on the *Response frames; <see cref="RelayStatus.Ok"/> otherwise).</summary>
    public RelayStatus Status { get; set; } = RelayStatus.Ok;

    /// <summary>UHID of the originating client (A).</summary>
    public string SourceUhid { get; set; } = string.Empty;

    /// <summary>UHID of the final target (B).</summary>
    public string DestinationUhid { get; set; } = string.Empty;

    /// <summary>UHID of the relay node (R). May be empty on client→relay requests.</summary>
    public string RelayUhid { get; set; } = string.Empty;

    /// <summary>Correlation id for a bridge session, shared by all frames of that session.</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>Reservation expiry as Unix ms. 0 when not applicable.</summary>
    public long ReservationExpiresAtMs { get; set; }

    /// <summary>Bridge duration budget in seconds. 0 = unlimited.</summary>
    public int LimitDurationSeconds { get; set; }

    /// <summary>Bridge data budget in bytes. 0 = unlimited.</summary>
    public long LimitDataBytes { get; set; }

    /// <summary>Tunnelled payload (<see cref="RelayMessageType.Data"/> only; empty otherwise).</summary>
    public byte[] Payload { get; set; } = [];
}
