// SPDX-License-Identifier: MIT
namespace Aether.Market.Models;

/// <summary>
/// Proof-of-Vicinity token issued by one node (<see cref="WitnessUhid"/>) to
/// another (<see cref="SubjectUhid"/>) during a physical co-presence event.
///
/// Both parties must countersign — this prevents unilateral forgery.
/// Token is transmitted over a short-range transport (BLE/NFC/NearLink only)
/// to prevent remote minting.
/// </summary>
public sealed class PoVToken
{
    /// <summary>UHID of the node issuing the voucher.</summary>
    public string WitnessUhid { get; set; } = string.Empty;

    /// <summary>UHID of the node being vouched for.</summary>
    public string SubjectUhid { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the co-presence event.</summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Transport channel used (must be short-range).</summary>
    public PoVTransportType TransportUsed { get; set; } = PoVTransportType.Ble;

    /// <summary>Ed25519 signature by the witness over (SubjectUhid + TimestampUtc.Ticks + Transport).</summary>
    public byte[] WitnessSignature { get; set; } = [];

    /// <summary>Ed25519 countersignature by the subject — required for token validity.</summary>
    public byte[] SubjectSignature { get; set; } = [];
}
