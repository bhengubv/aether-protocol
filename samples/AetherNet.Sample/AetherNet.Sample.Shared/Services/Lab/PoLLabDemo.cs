// SPDX-License-Identifier: MIT

using AetherNet.Cartography;
using AetherNet.Cartography.Models;
using AetherNet.Security.Services;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// Drives the real Proof-of-Location verifier. Every witness attestation below is a genuine
/// dual-signed statement: the witness signs the canonical body with its Ed25519 key and the subject
/// countersigns the identical bytes (<see cref="PoLAttestationCodec"/>), so neither forges alone. The
/// aggregate is judged by the actual <see cref="PoLVerifier.VerifyQuorum"/> — enough DISTINCT verified
/// witnesses AND enough summed reputation weight — which is why a hundred fresh phones (each weight 0)
/// never reach the bar, an unknown key can't be verified at all, and a subject vouching for itself is
/// thrown out. Nothing here is mocked; the signatures really verify, and tampering really fails.
/// </summary>
public sealed class PoLLabDemo
{
    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();
    private readonly PoLPolicy _policy = new(); // MinDistinctWitnesses=3, MinTotalWeight=2.0

    // The encounter every co-present witness signs. Coarse geohash (precision 7, ~150 m) — never raw GPS.
    private const string Geohash = "ke7yc8p";
    private const string PlaceId = "";                 // "" = free-roam
    private readonly long _timeBucket = PoLAttestationCodec.TimeBucketFor(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private string _subjectUhid = "";
    private byte[] _subjectPriv = Array.Empty<byte>();
    private byte[] _subjectPub = Array.Empty<byte>();

    private readonly List<Witness> _roster = new();
    private readonly HashSet<string> _collected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tampered = new(StringComparer.Ordinal);
    private bool _selfVouch;
    private bool _started;

    private PoLVerdict _verdict = new(false, 0, 0, "no witnesses yet");
    private IReadOnlyList<WitnessRow> _rows = Array.Empty<WitnessRow>();
    private string _bodyHex = "";

    public event Action? Changed;

    public PoLVerdict Verdict => _verdict;
    public IReadOnlyList<WitnessRow> Rows => _rows;
    public bool SelfVouch => _selfVouch;
    public string SubjectTag => _subjectUhid;
    public string EncounterGeohash => Geohash;
    public string EncounterPlace => string.IsNullOrEmpty(PlaceId) ? "free-roam (no place bound)" : PlaceId;
    public long TimeBucket => _timeBucket;
    public string SignableBodyHex => _bodyHex;
    public int MinWitnesses => _policy.MinDistinctWitnesses;
    public double MinWeight => _policy.MinTotalWeight;

    public IReadOnlyList<LogLine> Log()
    {
        lock (_gate) return _log.ToArray();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        (_subjectPriv, _subjectPub) = Ed25519SigningService.GenerateKeyPair();
        _subjectUhid = "lab:pol:subject";

        // Reputation weight is the payer's own view of a witness. Unknown/fresh identities MUST weigh 0 —
        // that is the Sybil defence. "Passer-by" is not in the web of trust at all, so its key won't even
        // resolve (a stronger rejection than weight 0).
        AddWitness("Nomsa", 0.9, known: true);
        AddWitness("Thabo", 0.8, known: true);
        AddWitness("Ayanda", 0.7, known: true);
        AddWitness("Lerato", 0.5, known: true);
        AddWitness("Sipho (fresh phone)", 0.0, known: true);
        AddWitness("Passer-by (unknown)", 0.0, known: false);

        // Start part-way: two good witnesses collected — not yet a quorum, so the demo builds up to VALID.
        _collected.Add("Nomsa");
        _collected.Add("Thabo");

        Emit("Subject collected two co-signed attestations. A quorum needs three distinct verified witnesses AND summed weight ≥ 2.00.");
        Recompute();
    }

    private void AddWitness(string name, double weight, bool known)
    {
        var (priv, pub) = Ed25519SigningService.GenerateKeyPair();
        _roster.Add(new Witness(name, $"lab:pol:{Slug(name)}", priv, pub, weight, known));
    }

    // ─── Interaction ──────────────────────────────────────────────────────────

    public void Toggle(string name)
    {
        if (_collected.Contains(name)) _collected.Remove(name);
        else _collected.Add(name);
        Recompute();
    }

    public void ToggleTamper(string name)
    {
        if (_tampered.Contains(name)) _tampered.Remove(name);
        else _tampered.Add(name);
        Recompute();
    }

    public void SetSelfVouch(bool on)
    {
        _selfVouch = on;
        if (on) Emit("Injected a self-vouch: the subject signs itself as its own witness. The verifier drops it — a node cannot witness itself.");
        Recompute();
    }

    public void ClearLog()
    {
        lock (_gate) _log.Clear();
        RaiseChanged();
    }

    // ─── Core: build the aggregate and run the real verifier ────────────────────

    private void Recompute()
    {
        var attestation = new PoLAttestation
        {
            SubjectUhid = _subjectUhid,
            Geohash = Geohash,
            PlaceId = PlaceId,
            TimeBucket = _timeBucket,
        };

        foreach (var w in _roster.Where(w => _collected.Contains(w.Name)))
            attestation.Witnesses.Add(BuildWitnessAttestation(w));

        if (_selfVouch)
            attestation.Witnesses.Add(BuildSelfVouch());

        // The real quorum decision. resolveKey maps a UHID to its public key (null = unknown);
        // witnessWeight returns the payer's reputation weight (0 for unknown/fresh).
        _verdict = PoLVerifier.VerifyQuorum(attestation, ResolveKey, WitnessWeight, _policy);

        // Per-row status, each computed with the REAL per-attestation verifier.
        var rows = new List<WitnessRow>();
        foreach (var w in _roster)
        {
            var collected = _collected.Contains(w.Name);
            bool? verified = null;
            if (collected)
            {
                var att = BuildWitnessAttestation(w);
                var witPub = ResolveKey(w.Uhid);
                verified = witPub is not null
                    && PoLVerifier.VerifyWitnessAttestation(att, witPub, _subjectPub);
            }
            var counted = collected && verified == true && w.Known;
            var badge = counted ? "counts"
                : collected && verified == false ? "rejected"
                : "";
            rows.Add(new WitnessRow(w.Name, w.Weight, w.Known, collected,
                _tampered.Contains(w.Name), verified, counted, badge, RowNote(w)));
        }
        _rows = rows;

        // Show the canonical bytes one witness actually signs, to make "both sign the identical body" real.
        var sample = _roster.FirstOrDefault(w => _collected.Contains(w.Name));
        if (sample is not null)
        {
            var body = PoLAttestationCodec.BuildSignableData(_subjectUhid, Geohash, PlaceId, _timeBucket, PoLTransport.Ble);
            _bodyHex = Convert.ToHexString(body);
        }

        RaiseChanged();
    }

    private PoLWitnessAttestation BuildWitnessAttestation(Witness w)
    {
        var att = new PoLWitnessAttestation
        {
            SubjectUhid = _subjectUhid,
            WitnessUhid = w.Uhid,
            Geohash = Geohash,
            PlaceId = PlaceId,
            TimeBucket = _timeBucket,
            Transport = PoLTransport.Ble,
            ProximityRssiDbm = -55,
        };
        var body = PoLAttestationCodec.BuildSignableData(att);
        att.WitnessSignature = Ed25519SigningService.Sign(w.Priv, body);
        att.SubjectSignature = Ed25519SigningService.Sign(_subjectPriv, body);

        // A tampered signature is a real, corrupted 64 bytes — the verifier rejects it on the maths, not a flag.
        if (_tampered.Contains(w.Name))
            att.WitnessSignature[0] ^= 0xFF;

        return att;
    }

    private PoLWitnessAttestation BuildSelfVouch()
    {
        var att = new PoLWitnessAttestation
        {
            SubjectUhid = _subjectUhid,
            WitnessUhid = _subjectUhid, // the subject as its own witness
            Geohash = Geohash,
            PlaceId = PlaceId,
            TimeBucket = _timeBucket,
            Transport = PoLTransport.Ble,
        };
        var body = PoLAttestationCodec.BuildSignableData(att);
        att.WitnessSignature = Ed25519SigningService.Sign(_subjectPriv, body);
        att.SubjectSignature = Ed25519SigningService.Sign(_subjectPriv, body);
        return att;
    }

    private byte[]? ResolveKey(string uhid)
    {
        if (string.Equals(uhid, _subjectUhid, StringComparison.Ordinal)) return _subjectPub;
        var w = _roster.FirstOrDefault(x => x.Uhid == uhid);
        return w is { Known: true } ? w.Pub : null; // unknown identity → cannot be verified
    }

    private double WitnessWeight(string uhid)
        => _roster.FirstOrDefault(x => x.Uhid == uhid)?.Weight ?? 0.0;

    private string RowNote(Witness w)
    {
        if (!w.Known) return "not in your web of trust — key won't resolve";
        if (_tampered.Contains(w.Name)) return "signature tampered — fails verification";
        if (w.Weight <= 0.0) return "fresh identity — weight 0 (Sybil-proof)";
        return "known, weighted";
    }

    // ─── plumbing ───────────────────────────────────────────────────────────────

    private void Emit(string text)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(text));
            if (_log.Count > 120) _log.RemoveRange(0, _log.Count - 120);
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private static string Slug(string name)
    {
        var cut = name.IndexOf(' ');
        return (cut > 0 ? name[..cut] : name).ToLowerInvariant();
    }

    public sealed record LogLine(string Text);

    public sealed record WitnessRow(
        string Name, double Weight, bool Known, bool Collected, bool Tampered,
        bool? Verified, bool Counted, string Badge, string Note);

    private sealed record Witness(string Name, string Uhid, byte[] Priv, byte[] Pub, double Weight, bool Known);
}
