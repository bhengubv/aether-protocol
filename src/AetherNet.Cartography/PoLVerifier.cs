// SPDX-License-Identifier: MIT
using AetherNet.Cartography.Models;
using AetherNet.Security.Services;

namespace AetherNet.Cartography;

/// <summary>Quorum policy: how many distinct witnesses and how much summed reputation weight a
/// <see cref="PoLAttestation"/> needs to be accepted.</summary>
public sealed record PoLPolicy(int MinDistinctWitnesses = 3, double MinTotalWeight = 2.0);

/// <summary>Outcome of quorum verification.</summary>
public sealed record PoLVerdict(bool IsValid, int DistinctWitnesses, double TotalWeight, string? Reason = null);

/// <summary>
/// Verifies coordinate-bound Proof-of-Location. A single witness attestation is valid when BOTH the
/// witness and the subject signatures verify over the identical canonical body (and it is not a
/// self-vouch). A <see cref="PoLAttestation"/> passes quorum when enough DISTINCT witnesses — each
/// verified and each weighted by its reputation (unknown identities weigh 0) — attest the same encounter.
///
/// The reputation weight and key resolution are injected (host-provided, e.g. over
/// <c>INodeReputationService.GetGossipWeightAsync</c> and the node's key directory), so this stays a
/// pure, deterministic, testable core.
/// </summary>
public static class PoLVerifier
{
    /// <summary>True if both signatures on <paramref name="a"/> verify and it is not a self-vouch.</summary>
    public static bool VerifyWitnessAttestation(PoLWitnessAttestation a, byte[] witnessPublicKey, byte[] subjectPublicKey)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (string.Equals(a.WitnessUhid, a.SubjectUhid, StringComparison.Ordinal))
            return false; // a node cannot witness itself
        var body = PoLAttestationCodec.BuildSignableData(a);
        return Ed25519SigningService.Verify(witnessPublicKey, body, a.WitnessSignature)
            && Ed25519SigningService.Verify(subjectPublicKey, body, a.SubjectSignature);
    }

    /// <summary>
    /// Decide whether <paramref name="attestation"/> meets quorum. <paramref name="resolveKey"/> maps a
    /// UHID to its 32-byte Ed25519 public key (null = unknown → that party can't be verified);
    /// <paramref name="witnessWeight"/> returns a witness's reputation weight in [0,1] from the payer's
    /// vantage (unknown/fresh identities MUST return 0 — that is the Sybil defence).
    /// </summary>
    public static PoLVerdict VerifyQuorum(
        PoLAttestation attestation,
        Func<string, byte[]?> resolveKey,
        Func<string, double> witnessWeight,
        PoLPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(resolveKey);
        ArgumentNullException.ThrowIfNull(witnessWeight);
        policy ??= new PoLPolicy();

        var subjectPub = resolveKey(attestation.SubjectUhid);
        if (subjectPub is null)
            return new PoLVerdict(false, 0, 0, "subject key unresolved");

        var counted = new HashSet<string>(StringComparer.Ordinal);
        double totalWeight = 0;

        foreach (var w in attestation.Witnesses)
        {
            if (!SameEncounter(attestation, w)) continue;                             // not this encounter
            if (string.Equals(w.WitnessUhid, attestation.SubjectUhid, StringComparison.Ordinal)) continue; // self-vouch
            if (counted.Contains(w.WitnessUhid)) continue;                            // distinct witnesses only

            var witPub = resolveKey(w.WitnessUhid);
            if (witPub is null) continue;
            if (!VerifyWitnessAttestation(w, witPub, subjectPub)) continue;

            counted.Add(w.WitnessUhid);
            totalWeight += Math.Clamp(witnessWeight(w.WitnessUhid), 0.0, 1.0);
        }

        int distinct = counted.Count;
        bool ok = distinct >= policy.MinDistinctWitnesses && totalWeight >= policy.MinTotalWeight;
        return new PoLVerdict(ok, distinct, totalWeight,
            ok ? null : $"quorum not met: {distinct}/{policy.MinDistinctWitnesses} witnesses, weight {totalWeight:0.00}/{policy.MinTotalWeight:0.00}");
    }

    private static bool SameEncounter(PoLAttestation agg, PoLWitnessAttestation w)
        => string.Equals(agg.SubjectUhid, w.SubjectUhid, StringComparison.Ordinal)
        && string.Equals(agg.Geohash, w.Geohash, StringComparison.Ordinal)
        && string.Equals(agg.PlaceId, w.PlaceId, StringComparison.Ordinal)
        && agg.TimeBucket == w.TimeBucket;
}
