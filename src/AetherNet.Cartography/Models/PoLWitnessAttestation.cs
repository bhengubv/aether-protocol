// SPDX-License-Identifier: MIT
namespace AetherNet.Cartography.Models;

/// <summary>
/// One witness's co-signed statement that a subject was physically present at a coarse place/time.
/// Both parties sign the identical canonical body (<c>PoLAttestationCodec</c>): the witness signature
/// proves an independent identity vouched; the subject countersignature proves the walker consented in
/// that short-range (BLE/NFC/NearLink) exchange. Neither forges alone. N distinct such attestations over
/// the same body aggregate into a <c>PoLAttestation</c>.
///
/// The signed body binds a COARSE geohash (~150 m, precision 7) — never the subject's raw GPS — so a
/// remote GPS spoof has no co-present witnesses to concur, and the time bucket is quantized so every
/// witness in one encounter signs the same value.
/// </summary>
public sealed class PoLWitnessAttestation
{
    /// <summary>The walker being attested.</summary>
    public string SubjectUhid { get; set; } = string.Empty;

    /// <summary>The independent nearby node vouching.</summary>
    public string WitnessUhid { get; set; } = string.Empty;

    /// <summary>Coarse geohash of the encounter (precision 7, ~150 m). Signed.</summary>
    public string Geohash { get; set; } = string.Empty;

    /// <summary>Stable place id (e.g. hash of a storefront/task record); empty for free-roam. Signed.</summary>
    public string PlaceId { get; set; } = string.Empty;

    /// <summary>Quantized 5-minute time bucket (see <c>PoLAttestationCodec.TimeBucketFor</c>). Signed.</summary>
    public long TimeBucket { get; set; }

    /// <summary>Short-range transport used. Signed.</summary>
    public PoLTransport Transport { get; set; } = PoLTransport.Ble;

    /// <summary>Ed25519 signature by the witness over the canonical body.</summary>
    public byte[] WitnessSignature { get; set; } = [];

    /// <summary>Ed25519 countersignature by the subject over the same body.</summary>
    public byte[] SubjectSignature { get; set; } = [];

    /// <summary>Advisory RSSI proximity sample (dBm). NOT signed — used only for plausibility screening.</summary>
    public int ProximityRssiDbm { get; set; }
}
