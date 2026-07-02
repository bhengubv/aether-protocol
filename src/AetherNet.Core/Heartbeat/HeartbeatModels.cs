// SPDX-License-Identifier: MIT

namespace AetherNet.Heartbeat;

/// <summary>
/// JSON payload for <see cref="Protocol.PacketType.Heartbeat"/> packets. Wire format: UTF-8 JSON with
/// snake_case property names. Both fields are integers, so the encoding is byte-identical across all
/// eight language ports (locked by fixtures/heartbeat/vectors.json).
///
/// <para>A node periodically broadcasts a heartbeat (TTL 1 — direct neighbours only) so peers can track
/// liveness. <see cref="Sequence"/> lets a receiver detect loss/ordering; <see cref="SentAtMs"/> lets it
/// gauge freshness. The heartbeat's originator is the enclosing packet's <c>SourceUhid</c>.</para>
/// </summary>
public sealed class HeartbeatPayload
{
    /// <summary>Monotonic heartbeat sequence number from the sender (starts at 1, increments per beat).</summary>
    public int Sequence { get; set; }

    /// <summary>Unix timestamp in milliseconds when the sender emitted this heartbeat.</summary>
    public long SentAtMs { get; set; }
}

/// <summary>
/// A peer's last observed liveness, maintained by <see cref="IHeartbeatService"/> on the receiving node.
/// </summary>
public sealed class PeerLiveness
{
    /// <summary>UHID of the peer this liveness record describes.</summary>
    public string Uhid { get; set; } = string.Empty;

    /// <summary>The <see cref="HeartbeatPayload.Sequence"/> of the most recent heartbeat seen from the peer.</summary>
    public int LastSequence { get; set; }

    /// <summary>The peer-stamped <see cref="HeartbeatPayload.SentAtMs"/> of the most recent heartbeat.</summary>
    public long LastSentAtMs { get; set; }

    /// <summary>Local Unix-ms timestamp when the most recent heartbeat was received.</summary>
    public long ReceivedAtMs { get; set; }
}
