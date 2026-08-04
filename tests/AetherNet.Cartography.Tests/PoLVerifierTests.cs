// SPDX-License-Identifier: MIT
using AetherNet.Cartography;
using AetherNet.Cartography.Models;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Cartography.Tests;

public class PoLVerifierTests
{
    private const string Geo = "u4pruy";
    private const string Place = "shop-1";
    private const long Bucket = 5;

    /// <summary>A little world of identities: keys per UHID, and which UHIDs the payer has first-hand
    /// reputation for (weight 1). Everything else weighs 0 — the Sybil defence.</summary>
    private sealed class World
    {
        private readonly Dictionary<string, byte[]> _pub = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _priv = new(StringComparer.Ordinal);
        private readonly HashSet<string> _known = new(StringComparer.Ordinal);

        public void Add(string uhid, bool reputable)
        {
            var (priv, pub) = Ed25519SigningService.GenerateKeyPair();
            _priv[uhid] = priv;
            _pub[uhid] = pub;
            if (reputable) _known.Add(uhid);
        }

        public byte[] Priv(string uhid) => _priv[uhid];
        public byte[]? Resolve(string uhid) => _pub.GetValueOrDefault(uhid);
        public double Weight(string uhid) => _known.Contains(uhid) ? 1.0 : 0.0;
    }

    private static PoLWitnessAttestation Issue(World w, string subject, string witness, string? geo = null)
    {
        var att = new PoLWitnessAttestation
        {
            SubjectUhid = subject,
            WitnessUhid = witness,
            Geohash = geo ?? Geo,
            PlaceId = Place,
            TimeBucket = Bucket,
            Transport = PoLTransport.Ble,
        };
        var body = PoLAttestationCodec.BuildSignableData(att);
        att.WitnessSignature = Ed25519SigningService.Sign(w.Priv(witness), body);
        att.SubjectSignature = Ed25519SigningService.Sign(w.Priv(subject), body);
        return att;
    }

    private static PoLAttestation Aggregate(string subject, params PoLWitnessAttestation[] witnesses)
        => new() { SubjectUhid = subject, Geohash = Geo, PlaceId = Place, TimeBucket = Bucket, Witnesses = [.. witnesses] };

    [Fact]
    public void ThreeReputableWitnesses_MeetQuorum()
    {
        var w = new World();
        w.Add("subject", reputable: false);
        w.Add("w1", true); w.Add("w2", true); w.Add("w3", true);

        var agg = Aggregate("subject", Issue(w, "subject", "w1"), Issue(w, "subject", "w2"), Issue(w, "subject", "w3"));
        var v = PoLVerifier.VerifyQuorum(agg, w.Resolve, w.Weight);

        Assert.True(v.IsValid);
        Assert.Equal(3, v.DistinctWitnesses);
        Assert.Equal(3.0, v.TotalWeight);
    }

    [Fact]
    public void SybilRing_HundredFreshIdentities_FailOnWeight_DespiteValidSignatures()
    {
        var w = new World();
        w.Add("subject", reputable: false);
        var witnesses = new List<PoLWitnessAttestation>();
        for (int i = 0; i < 100; i++)
        {
            w.Add($"bot{i}", reputable: false); // fresh identity, unknown to the payer
            witnesses.Add(Issue(w, "subject", $"bot{i}"));
        }

        var v = PoLVerifier.VerifyQuorum(Aggregate("subject", [.. witnesses]), w.Resolve, w.Weight);

        Assert.False(v.IsValid);                 // rejected...
        Assert.Equal(100, v.DistinctWitnesses);  // ...even though 100 distinct signatures all verify...
        Assert.Equal(0.0, v.TotalWeight);        // ...because fresh identities carry zero weight.
    }

    [Fact]
    public void SelfVouch_IsIgnored()
    {
        var w = new World();
        w.Add("subject", reputable: true); // even a high-rep subject cannot witness itself
        var v = PoLVerifier.VerifyQuorum(Aggregate("subject", Issue(w, "subject", "subject")), w.Resolve, w.Weight);
        Assert.False(v.IsValid);
        Assert.Equal(0, v.DistinctWitnesses);
    }

    [Fact]
    public void WitnessAttestingADifferentCell_IsIgnored()
    {
        var w = new World();
        w.Add("subject", false); w.Add("w1", true);
        // Signed for a different geohash than the aggregate claims → not this encounter.
        var v = PoLVerifier.VerifyQuorum(Aggregate("subject", Issue(w, "subject", "w1", geo: "gcpvj0")), w.Resolve, w.Weight);
        Assert.Equal(0, v.DistinctWitnesses);
    }

    [Fact]
    public void ForgedWitnessSignature_IsIgnored()
    {
        var w = new World();
        w.Add("subject", false); w.Add("w1", true);
        var att = Issue(w, "subject", "w1");
        att.WitnessSignature = new byte[64]; // zeroed / forged
        var v = PoLVerifier.VerifyQuorum(Aggregate("subject", att), w.Resolve, w.Weight);
        Assert.Equal(0, v.DistinctWitnesses);
    }

    [Fact]
    public void DuplicateWitness_CountedOnce()
    {
        var w = new World();
        w.Add("subject", false); w.Add("w1", true); w.Add("w2", true);
        var v = PoLVerifier.VerifyQuorum(
            Aggregate("subject", Issue(w, "subject", "w1"), Issue(w, "subject", "w1"), Issue(w, "subject", "w2")),
            w.Resolve, w.Weight, new PoLPolicy(MinDistinctWitnesses: 2, MinTotalWeight: 2.0));
        Assert.Equal(2, v.DistinctWitnesses); // w1 counted once
        Assert.Equal(2.0, v.TotalWeight);
        Assert.True(v.IsValid);
    }
}
