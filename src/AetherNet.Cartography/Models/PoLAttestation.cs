// SPDX-License-Identifier: MIT
namespace AetherNet.Cartography.Models;

/// <summary>
/// The aggregate proof that a subject was physically at a coarse place/time: a set of independent
/// <see cref="PoLWitnessAttestation"/>s that all attest the SAME encounter (subject, geohash, place,
/// time bucket). Validity is decided by <c>PoLVerifier.VerifyQuorum</c> — enough <i>distinct</i>
/// witnesses AND enough summed reputation weight, so a lone attacker minting 100 fresh identities
/// (each weight 0 to the payer) can never reach the bar.
/// </summary>
public sealed class PoLAttestation
{
    public string SubjectUhid { get; set; } = string.Empty;
    public string Geohash { get; set; } = string.Empty;
    public string PlaceId { get; set; } = string.Empty;
    public long TimeBucket { get; set; }

    /// <summary>Witness attestations for this encounter (each independently witness- and subject-signed).</summary>
    public List<PoLWitnessAttestation> Witnesses { get; set; } = [];
}
