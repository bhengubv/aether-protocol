// SPDX-License-Identifier: MIT

using AetherNet.Market;
using AetherNet.Market.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Sample.Shared.Services; // InProcessMeshSender (defined alongside AetherDemoService)
using AetherNet.Security.Services;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// Drives Proof-of-Vicinity — the anti-Sybil trust graph that lets an offline market
/// tell a real neighbour from a thousand sock-puppets — entirely in-process, over the
/// real src services the eight SDKs port.
///
/// <para>
/// Vicinity is a claim you cannot make alone: two people who are physically together
/// let their phones countersign a token over a short-range radio, and a profile earns
/// trust in proportion to how many <b>distinct</b> humans have stood next to it. This
/// demo shows it two ways, because the mechanism has two halves:
/// </para>
/// <list type="number">
///   <item><b>Two phones vouch over the mesh</b> — the real, directed, two-key exchange
///     (<see cref="PoVTokenExchangeService"/> over <see cref="PacketType.PoVTokenExchange"/>
///     43). Each node holds its own Ed25519 identity; a witness signs and sends, the
///     subject verifies the witness and counter-signs. The subject's score climbs by one
///     distinct witness per exchange.</item>
///   <item><b>What a forged or withdrawn vouch does</b> — the self-contained
///     <see cref="InMemoryPoVService"/> (real Ed25519 too), used to show a tampered token
///     failing verification and a defection docking a voucher's own standing 20%.</item>
/// </list>
///
/// <para>
/// PoV is a purely local identity/routing signal. It carries no value and never touches
/// the money layer — the marketplace merely reads it to rank who to trust.
/// </para>
/// </summary>
public sealed class VicinityLabDemo : IDisposable
{
    // A short per-instance token keeps every in-process transport UHID unique across
    // page visits, so a re-entry never collides with a node the last visit left behind.
    private readonly string _inst = Guid.NewGuid().ToString("N")[..4];

    private readonly List<LogLine> _log = new();
    private readonly object _gate = new();

    // ── Section 1: the on-mesh two-key exchange ─────────────────────────────────
    private MeshNode _subject = null!;
    private readonly List<MeshNode> _witnesses = new();
    private readonly Dictionary<string, byte[]> _pubKeys = new(StringComparer.Ordinal);
    private int _nextWitness;
    private PoVToken? _lastAcceptedOnMesh;

    // ── Section 2: anti-Sybil defences, self-contained ──────────────────────────
    private readonly InMemoryPoVService _pov = new();
    private string _povSubject = "";
    private readonly List<string> _povWitnesses = new();
    private int _nextPovWitness;
    private PoVToken? _sampleToken;

    private bool _started;
    private bool _disposed;

    /// <summary>Raised whenever the log or any score changes; the page re-renders on it.</summary>
    public event Action? Changed;

    // ── Read models the page binds to ───────────────────────────────────────────

    public string SubjectUhid => _subject.Uhid;
    public string SubjectPetname => Petname(_subject.Uhid);
    public int WitnessesRemaining => _witnesses.Count - _nextWitness;
    public PoVToken? LastAcceptedOnMesh => _lastAcceptedOnMesh;

    /// <summary>The on-mesh subject's live PoV score (distinct witnesses + weighted).</summary>
    public PoVScore MeshScore { get; private set; } = new();

    /// <summary>The self-contained subject's score, watched across tamper + defection.</summary>
    public PoVScore DefenceScore { get; private set; } = new();

    public string DefenceSubjectPetname => Petname(_povSubject);
    public bool HasSampleToken => _sampleToken is not null;

    /// <summary>Result of the most recent verify: null (not run), true (valid), false (rejected).</summary>
    public bool? PristineVerify { get; private set; }
    public bool? TamperedVerify { get; private set; }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate)
            return _log.ToArray();
    }

    // ── Setup ───────────────────────────────────────────────────────────────────

    /// <summary>Stand up the two-node mesh and the self-contained ledger. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        // Section 1 — three real identities on one in-process mesh: a subject and two
        // witnesses, each with its own Ed25519 key and packet-signing service.
        _subject = NewMeshNode("thabo");
        _witnesses.Add(NewMeshNode("lerato"));
        _witnesses.Add(NewMeshNode("naledi"));

        foreach (var n in All())
            _pubKeys[n.Uhid] = n.Identity.GetPublicKey();

        // Witnesses can reach the subject; the subject accepts what arrives.
        foreach (var w in _witnesses)
        {
            w.Sender.AddPotentialPeer(_subject.Uhid);
            _subject.Sender.AddPotentialPeer(w.Uhid);
        }

        // The subject's inbound wire: a PoV-exchange packet is verified against the
        // witness's published key and counter-signed. (Only the subject receives here —
        // the exchange is directed witness → subject.)
        _subject.Transport.DataReceived += (src, bytes) =>
        {
            MeshPacket packet;
            try { packet = PacketSerializer.Deserialize(bytes); }
            catch { return; }
            if (packet.Type == PacketType.PoVTokenExchange && _pubKeys.TryGetValue(src, out var key))
                _ = _subject.Exchange.HandleTokenExchangeAsync(packet, key);
        };

        ((IPoVTokenExchangeService)_subject.Exchange).TokenReceived += (_, token) =>
        {
            _lastAcceptedOnMesh = token;
            Emit($"{Petname(_subject.Uhid)} accepted a vicinity token from {Petname(token.WitnessUhid)} — " +
                 $"witness sig {token.WitnessSignature.Length}B, subject countersig {token.SubjectSignature.Length}B, both real Ed25519.",
                 emphasis: true);
        };

        // Section 2 — a self-contained ledger with one subject and a couple of vouchers.
        _povSubject = Uhid("kagiso");
        _povWitnesses.Add(Uhid("lerato"));
        _povWitnesses.Add(Uhid("naledi"));
        _povWitnesses.Add(Uhid("sipho"));

        Emit($"Two phones on one mesh: {Petname(_subject.Uhid)} is the profile; " +
             $"{string.Join(" and ", _witnesses.Select(w => Petname(w.Uhid)))} can vouch for it in person.");
        RaiseChanged();
    }

    // ── Section 1: two phones vouch over the mesh ───────────────────────────────

    /// <summary>
    /// The next witness issues a signed vicinity token to the subject over the real
    /// PoVTokenExchange packet; the subject verifies + counter-signs; the score climbs
    /// by one distinct witness.
    /// </summary>
    public async Task VouchOverMeshAsync()
    {
        if (_nextWitness >= _witnesses.Count)
            return;

        var witness = _witnesses[_nextWitness++];
        Emit($"{Petname(witness.Uhid)} is next to {Petname(_subject.Uhid)} → issuing a BLE vicinity token…");

        var issued = await witness.Exchange.IssueTokenAsync(_subject.Uhid, PoVTransportType.Ble).ConfigureAwait(false);
        if (issued is null)
        {
            Emit($"{Petname(witness.Uhid)} refused to issue (self-vouch or non-short-range).");
            RaiseChanged();
            return;
        }

        // Delivery is synchronous over the in-process transport; give the subject's async
        // verify + counter-sign a beat to settle before reading the score back.
        await Task.Delay(80).ConfigureAwait(false);

        MeshScore = await _subject.Exchange.GetScoreAsync(_subject.Uhid).ConfigureAwait(false);
        Emit($"{Petname(_subject.Uhid)}'s vicinity score → {MeshScore.UniqueWitnesses} distinct witness(es), " +
             $"weighted {MeshScore.WeightedScore:0.00}.");
        RaiseChanged();
    }

    // ── Section 2: forged and withdrawn vouches ─────────────────────────────────

    /// <summary>A fresh witness vouches for the self-contained subject; the score climbs.</summary>
    public async Task AddVouchAsync()
    {
        if (_nextPovWitness >= _povWitnesses.Count)
            return;

        var witness = _povWitnesses[_nextPovWitness++];
        var token = await _pov.IssueTokenAsync(witness, _povSubject, PoVTransportType.Ble).ConfigureAwait(false);
        await _pov.AcceptTokenAsync(token).ConfigureAwait(false);
        _sampleToken = token;

        DefenceScore = await _pov.GetScoreAsync(_povSubject).ConfigureAwait(false);
        PristineVerify = await _pov.VerifyTokenAsync(token).ConfigureAwait(false);
        TamperedVerify = null;
        Emit($"{Petname(witness)} vouched for {Petname(_povSubject)} — token verifies {(PristineVerify == true ? "VALID" : "INVALID")}. " +
             $"Score → {DefenceScore.UniqueWitnesses} witness(es), weighted {DefenceScore.WeightedScore:0.00}.");
        RaiseChanged();
    }

    /// <summary>
    /// Take the last real token, change one field, and re-verify: the Ed25519 signatures
    /// no longer cover the body, so a tampered vicinity proof is rejected. The stored
    /// token is left untouched.
    /// </summary>
    public async Task TamperAsync()
    {
        if (_sampleToken is null)
            return;

        var forged = new PoVToken
        {
            WitnessUhid      = _sampleToken.WitnessUhid,
            SubjectUhid      = _sampleToken.SubjectUhid,
            TimestampUtc     = _sampleToken.TimestampUtc.AddSeconds(1), // a single second is enough
            TransportUsed    = _sampleToken.TransportUsed,
            WitnessSignature = _sampleToken.WitnessSignature,
            SubjectSignature = _sampleToken.SubjectSignature,
        };

        TamperedVerify = await _pov.VerifyTokenAsync(forged).ConfigureAwait(false);
        Emit($"Timestamp nudged by one second → verify now {(TamperedVerify == true ? "VALID (BAD!)" : "INVALID — the signatures no longer cover the token.")}",
             emphasis: true);
        RaiseChanged();
    }

    /// <summary>
    /// Report that someone the subject once vouched for has defected. A vicinity vouch is
    /// a stake in a stranger's honesty, so the report docks the voucher's own weighted
    /// score 20%.
    /// </summary>
    public async Task ReportDefectionAsync()
    {
        var before = DefenceScore.WeightedScore;
        var defector = Uhid("mallory");
        await _pov.ReportDefectionAsync(_povSubject, defector).ConfigureAwait(false);
        DefenceScore = await _pov.GetScoreAsync(_povSubject).ConfigureAwait(false);
        Emit($"{Petname(_povSubject)} had vouched for {Petname(defector)}, who defected → weighted score {before:0.00} → {DefenceScore.WeightedScore:0.00} (−20%).",
             emphasis: true);
        RaiseChanged();
    }

    // ── Internals ───────────────────────────────────────────────────────────────

    private IEnumerable<MeshNode> All()
    {
        yield return _subject;
        foreach (var w in _witnesses)
            yield return w;
    }

    private MeshNode NewMeshNode(string name)
    {
        var uhid = Uhid(name);
        var identity = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        identity.SetLocalUhid(uhid);
        var signing = new PacketSigningService(identity, NullLogger<PacketSigningService>.Instance);
        var transport = new InProcessTransportService(uhid, NullLogger<InProcessTransportService>.Instance);
        var sender = new InProcessMeshSender(uhid, transport);
        var exchange = new PoVTokenExchangeService(sender, signing, identity,
            NullLogger<PoVTokenExchangeService>.Instance);
        return new MeshNode(uhid, identity, signing, transport, sender, exchange);
    }

    private string Uhid(string name) => $"aether:{name}:{_inst}";

    private void Emit(string text, bool emphasis = false)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(text, emphasis));
            if (_log.Count > 200)
                _log.RemoveRange(0, _log.Count - 200);
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    // Render "aether:thabo:ab12" → "Thabo".
    private static string Petname(string uhid)
    {
        var parts = uhid.Split(':');
        return parts.Length >= 2 && parts[1].Length > 0
            ? char.ToUpperInvariant(parts[1][0]) + parts[1][1..]
            : uhid;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_started) return; // nothing was stood up
        foreach (var n in All())
        {
            n.Transport.Dispose();
            n.Signing.Dispose();
        }
    }

    public sealed record LogLine(string Text, bool Emphasis);

    private sealed record MeshNode(
        string Uhid,
        SignalProtocolService Identity,
        PacketSigningService Signing,
        InProcessTransportService Transport,
        InProcessMeshSender Sender,
        PoVTokenExchangeService Exchange);
}
