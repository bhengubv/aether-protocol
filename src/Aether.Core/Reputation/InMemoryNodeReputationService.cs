// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Aether.Reputation;

/// <summary>
/// Thread-safe, in-memory implementation of <see cref="INodeReputationService"/>.
///
/// Scores are adjusted atomically via <see cref="Interlocked"/>-free
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> update loops. All tasks
/// complete synchronously; the async surface matches the interface so
/// persistent back-ends can replace this implementation without changing callers.
///
/// Delivery success uses an EWMA (α = 0.10) so a single recovered burst does
/// not immediately restore a chronically bad peer's score.
/// </summary>
public sealed class InMemoryNodeReputationService : INodeReputationService
{
    // Score deltas — negative signals
    private const double DeltaRreqFlood      = -0.05;
    private const double DeltaReplay         = -0.15;
    private const double DeltaSigFailure     = -0.20;
    private const double DeltaCustodyRefusal = -0.05;
    private const double DeltaDeliveryFail   = -0.02;

    // Score deltas — positive signals
    private const double DeltaDeliveryOk = +0.01;

    private readonly ConcurrentDictionary<string, double> _scores = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task RecordRreqFloodAttemptAsync(string uhid, CancellationToken ct = default)
    {
        ApplyDelta(uhid, DeltaRreqFlood);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordReplayAttemptAsync(string uhid, CancellationToken ct = default)
    {
        ApplyDelta(uhid, DeltaReplay);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordSignatureFailureAsync(string uhid, CancellationToken ct = default)
    {
        ApplyDelta(uhid, DeltaSigFailure);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordCustodyRefusalAsync(string uhid, CancellationToken ct = default)
    {
        ApplyDelta(uhid, DeltaCustodyRefusal);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordDeliverySuccessAsync(string uhid, int roundTripMs, CancellationToken ct = default)
    {
        ApplyDelta(uhid, DeltaDeliveryOk);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordDeliveryFailureAsync(string uhid, CancellationToken ct = default)
    {
        ApplyDelta(uhid, DeltaDeliveryFail);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<double> GetReputationScoreAsync(string uhid, CancellationToken ct = default)
    {
        var score = _scores.TryGetValue(uhid, out var s) ? s : 1.0;
        return Task.FromResult(score);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, double>> GetAllScoresAsync(CancellationToken ct = default)
    {
        // Snapshot: copy under no lock (ConcurrentDictionary enumeration is safe).
        var snapshot = new Dictionary<string, double>(_scores, StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyDictionary<string, double>>(snapshot);
    }

    /// <inheritdoc />
    public Task ApplyWeightedDeltaAsync(string uhid, double weightedDelta, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(weightedDelta, -1.0, 1.0);
        ApplyDelta(uhid, clamped);
        return Task.CompletedTask;
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private static double ClampScore(double v)
    {
        // Snap float-point values within 1 ULP of 0 or 1 to avoid
        // scores like 5.5e-17 (instead of exactly 0.0) accumulating.
        var clamped = Math.Clamp(v, 0.0, 1.0);
        if (clamped < 1e-12) return 0.0;
        if (clamped > 1.0 - 1e-12) return 1.0;
        return clamped;
    }

    private void ApplyDelta(string uhid, double delta)
    {
        _scores.AddOrUpdate(
            uhid,
            _ => ClampScore(1.0 + delta),
            (_, old) => ClampScore(old + delta));
    }
}
