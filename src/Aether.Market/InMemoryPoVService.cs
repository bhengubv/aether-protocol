// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Security.Cryptography;
using AetherMesh.Market.Models;

namespace AetherMesh.Market;

/// <summary>
/// In-memory <see cref="IPoVService"/> implementation for testing and
/// single-node scenarios.
///
/// Signature generation uses random bytes (not real Ed25519) — sufficient
/// for unit tests. Production implementations must use real Ed25519 key pairs.
/// </summary>
public sealed class InMemoryPoVService : IPoVService
{
    // Tokens indexed by SubjectUhid → list of tokens vouching for that subject.
    private readonly ConcurrentDictionary<string, List<PoVToken>> _tokensBySubject = new();

    // Override scores stored after defection penalties, keyed by WitnessUhid.
    private readonly ConcurrentDictionary<string, double> _scoreOverrides = new();

    private readonly object _lock = new();

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

        var token = new PoVToken
        {
            WitnessUhid      = witnessUhid,
            SubjectUhid      = subjectUhid,
            TimestampUtc     = DateTime.UtcNow,
            TransportUsed    = transport,
            WitnessSignature = RandomNumberGenerator.GetBytes(32),
            SubjectSignature = RandomNumberGenerator.GetBytes(32),
        };

        TokenReceived?.Invoke(this, token);
        return Task.FromResult(token);
    }

    /// <inheritdoc/>
    public Task AcceptTokenAsync(PoVToken token, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_tokensBySubject.TryGetValue(token.SubjectUhid, out var list))
            {
                list = new List<PoVToken>();
                _tokensBySubject[token.SubjectUhid] = list;
            }
            list.Add(token);
        }

        TokenReceived?.Invoke(this, token);
        return Task.CompletedTask;
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
                return Task.FromResult(new PoVScore
                {
                    Uhid            = uhid,
                    UniqueWitnesses = 0,
                    WeightedScore   = 0.0,
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

        // Apply any stored override (defection penalty applies to witness scores,
        // but GetScore is called for subject uhids too — check both paths).
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

        var valid = token.WitnessSignature.Length > 0
                 && token.SubjectSignature.Length > 0
                 && !string.Equals(token.WitnessUhid, token.SubjectUhid, StringComparison.Ordinal);

        return Task.FromResult(valid);
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
}
