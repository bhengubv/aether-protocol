// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using AetherNet.Market.Models;
using AetherNet.Security.Services;

namespace AetherNet.Market;

/// <summary>
/// In-memory <see cref="IPoVService"/> implementation for testing and single-node scenarios.
///
/// <para>
/// Signatures are REAL Ed25519 (via <see cref="Ed25519SigningService"/>, the self-contained node
/// identity key) over the canonical token body
/// (<see cref="PoVTokenCodec.BuildSignableTokenData(PoVToken)"/> — "SubjectUhid + TimestampUtc.Ticks +
/// Transport"), byte-identical to every other AetherNet language implementation and the CircleAether
/// mirror. <see cref="VerifyTokenAsync"/> cryptographically verifies both the witness and subject
/// signatures — a tampered token fails verification.
/// </para>
///
/// <para>
/// This single-node service holds one identity key and produces both the witness and the subject
/// signature with it (the issue→accept flow happens within one node here). The directed, two-party
/// witness→subject exchange that lives on the mesh — where each side counter-signs with its own key — is
/// handled by <see cref="PoVTokenExchangeService"/> via the <see cref="AetherNet.Protocol.PacketType.PoVTokenExchange"/>
/// (43) packet.
/// </para>
/// </summary>
public sealed class InMemoryPoVService : IPoVService
{
    // Tokens indexed by SubjectUhid → list of tokens vouching for that subject.
    private readonly ConcurrentDictionary<string, List<PoVToken>> _tokensBySubject = new();

    // Override scores stored after defection penalties, keyed by WitnessUhid.
    private readonly ConcurrentDictionary<string, double> _scoreOverrides = new();

    private readonly object _lock = new();

    // Self-contained real Ed25519 identity. The single-node in-memory service both vouches and stands in
    // for the subject, so both signatures on a token it issues are produced with this one key.
    private readonly byte[] _privateKey;
    private readonly byte[] _publicKey;

    /// <summary>Generates a self-contained Ed25519 identity key pair for real signing and verification.</summary>
    public InMemoryPoVService()
    {
        (_privateKey, _publicKey) = Ed25519SigningService.GenerateKeyPair();
    }

    /// <inheritdoc/>
    public event EventHandler<PoVToken>? TokenReceived;

    event EventHandler<PoVToken> IPoVService.TokenReceived
    {
        add    => TokenReceived += value;
        remove => TokenReceived -= value;
    }

    /// <inheritdoc/>
    public Task<PoVToken> IssueTokenAsync(
        string witnessUhid,
        string subjectUhid,
        PoVTransportType transport = PoVTransportType.Ble,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var timestamp = DateTime.UtcNow;
        var signable  = PoVTokenCodec.BuildSignableTokenData(subjectUhid, timestamp.Ticks, transport);

        // REAL Ed25519 over the canonical token body. Both signatures are produced with this node's
        // identity key — in the single-node in-memory model the issuing node both vouches and (stands in
        // for the) subject. The directed two-key exchange lives in PoVTokenExchangeService.
        var signature = Ed25519SigningService.Sign(_privateKey, signable);

        var token = new PoVToken
        {
            WitnessUhid      = witnessUhid,
            SubjectUhid      = subjectUhid,
            TimestampUtc     = timestamp,
            TransportUsed    = transport,
            WitnessSignature = signature,
            SubjectSignature = signature,
        };

        TokenReceived?.Invoke(this, token);
        return Task.FromResult(token);
    }

    /// <inheritdoc/>
    public async Task AcceptTokenAsync(PoVToken token, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Only record a token that cryptographically verifies — both signatures valid + distinct parties.
        if (!await VerifyTokenAsync(token, ct).ConfigureAwait(false))
            return;

        RecordToken(token);

        TokenReceived?.Invoke(this, token);
    }

    /// <inheritdoc/>
    public Task<PoVScore> GetScoreAsync(string uhid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        List<PoVToken> tokens;
        lock (_lock)
        {
            if (!_tokensBySubject.TryGetValue(uhid, out var list))
            {
                // Apply a stored defection override even for a UHID with no inbound tokens (defection
                // penalises witness UHIDs, which may not themselves be subjects).
                var overrideOnly = _scoreOverrides.TryGetValue(uhid, out var ov) ? ov : 0.0;
                return Task.FromResult(new PoVScore
                {
                    Uhid            = uhid,
                    UniqueWitnesses = 0,
                    WeightedScore   = overrideOnly,
                    LastUpdated     = DateTime.UtcNow,
                });
            }
            tokens = new List<PoVToken>(list);
        }

        var uniqueWitnesses = tokens
            .Select(t => t.WitnessUhid)
            .Distinct(StringComparer.Ordinal)
            .Count();

        // Sigmoid-ish: w / (w + 1)
        double baseScore = uniqueWitnesses / (uniqueWitnesses + 1.0);

        // Apply any stored override (defection penalty).
        if (_scoreOverrides.TryGetValue(uhid, out var overrideScore))
            baseScore = overrideScore;

        return Task.FromResult(new PoVScore
        {
            Uhid            = uhid,
            UniqueWitnesses = uniqueWitnesses,
            WeightedScore   = baseScore,
            LastUpdated     = DateTime.UtcNow,
        });
    }

    /// <inheritdoc/>
    public Task<bool> VerifyTokenAsync(PoVToken token, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(token);

        // Structural rule: both parties must have signed, and they must be distinct UHIDs. An unsigned or
        // single-signed token — one party trying to mint a vicinity proof unilaterally — is rejected.
        if (token.WitnessSignature is not { Length: > 0 }
            || token.SubjectSignature is not { Length: > 0 }
            || string.IsNullOrEmpty(token.WitnessUhid)
            || string.IsNullOrEmpty(token.SubjectUhid)
            || string.Equals(token.WitnessUhid, token.SubjectUhid, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        // Cryptographic rule: BOTH signatures must be valid Ed25519 over the canonical token body. Any
        // tampering with the subject/timestamp/transport invalidates them.
        var signable = PoVTokenCodec.BuildSignableTokenData(token);
        var witnessValid = Ed25519SigningService.Verify(_publicKey, signable, token.WitnessSignature);
        var subjectValid = Ed25519SigningService.Verify(_publicKey, signable, token.SubjectSignature);

        return Task.FromResult(witnessValid && subjectValid);
    }

    /// <inheritdoc/>
    public async Task ReportDefectionAsync(
        string witnessUhid,
        string defectorUhid,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Load the witness's current score, then reduce by 20%.
        var score = await GetScoreAsync(witnessUhid, ct).ConfigureAwait(false);
        var penalised = score.WeightedScore * 0.8;
        _scoreOverrides[witnessUhid] = penalised;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private void RecordToken(PoVToken token)
    {
        lock (_lock)
        {
            if (!_tokensBySubject.TryGetValue(token.SubjectUhid, out var list))
            {
                list = new List<PoVToken>();
                _tokensBySubject[token.SubjectUhid] = list;
            }
            list.Add(token);
        }
    }
}
