// SPDX-License-Identifier: MIT
using AetherNet.Market.Models;
using AetherNet.Protocol;

namespace AetherNet.Market;

/// <summary>
/// On-mesh Proof-of-Vicinity token exchange over <see cref="PacketType.PoVTokenExchange"/> (value 43).
///
/// <para>
/// PoV is a DIRECTED, two-party co-presence proof: a witness vouches for a subject it is physically
/// near (BLE / NFC / NearLink only). The witness signs the canonical token body with its real Ed25519
/// identity key and sends it point-to-point to the subject; the subject counter-signs with ITS key on
/// receipt. A token is only valid once it carries both signatures, which is what makes a vicinity proof
/// un-forgeable by either party alone.
/// </para>
///
/// <para>
/// The wire payload (snake_case JSON <see cref="PoVToken"/>) and the canonical signable body
/// (<see cref="PoVTokenCodec"/>) are byte-identical to every other AetherNet language implementation and
/// the CircleAether mirror, so a token issued by any node interoperates on one mesh.
/// </para>
/// </summary>
public interface IPoVTokenExchangeService
{
    /// <summary>
    /// Issue a PoV token for <paramref name="subjectUhid"/> and send it over the mesh as a signed
    /// <see cref="PacketType.PoVTokenExchange"/> packet. The local node is the witness; it signs the
    /// canonical body with its real Ed25519 identity key and leaves the subject signature empty for the
    /// subject to fill on receipt. Returns the issued token, or <c>null</c> if issuance was refused
    /// (empty subject, non-short-range transport, missing local identity, or self-vouch).
    /// </summary>
    Task<PoVToken?> IssueTokenAsync(
        string subjectUhid,
        PoVTransportType transport = PoVTransportType.Ble,
        CancellationToken ct = default);

    /// <summary>
    /// Handle an inbound <see cref="PacketType.PoVTokenExchange"/> packet. Verifies the enclosing
    /// MeshPacket signature (which also enforces freshness + nonce replay-dedup) against
    /// <paramref name="senderPublicKey"/>, verifies the witness's Ed25519 signature over the token body,
    /// counter-signs as the subject with the local identity key, and records the verified token (which
    /// increments the witness's contribution to the local node's score). Returns <c>true</c> if a valid
    /// token was accepted, <c>false</c> if the packet was dropped.
    /// </summary>
    Task<bool> HandleTokenExchangeAsync(
        MeshPacket packet,
        byte[] senderPublicKey,
        CancellationToken ct = default);

    /// <summary>Return the current PoV score for a UHID (from tokens accepted via the exchange).</summary>
    Task<PoVScore> GetScoreAsync(string uhid, CancellationToken ct = default);

    /// <summary>Fired when a valid PoV token is accepted from the mesh.</summary>
    event EventHandler<PoVToken> TokenReceived;
}
