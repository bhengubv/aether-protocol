// SPDX-License-Identifier: MIT
namespace AetherNet.Map.Crdt;

/// <summary>
/// Positive-negative counter (PN-Counter): a pair of grow-only per-node tallies. Each node only ever
/// increments its <i>own</i> positive and negative entries, so merge is the element-wise max of both
/// maps — commutative, associative, idempotent. The value is sum(positives) − sum(negatives).
///
/// Used for a map feature's anonymous up/down sentiment where naming the voter (the witness G-Set) is
/// not wanted. Prefer the G-Set when attribution/Sybil-auditing matters.
/// </summary>
public sealed class PnCounter
{
    private readonly Dictionary<string, long> _positive;
    private readonly Dictionary<string, long> _negative;

    public PnCounter()
    {
        _positive = new Dictionary<string, long>(StringComparer.Ordinal);
        _negative = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    public PnCounter(IEnumerable<KeyValuePair<string, long>> positive, IEnumerable<KeyValuePair<string, long>> negative)
    {
        _positive = new Dictionary<string, long>(positive, StringComparer.Ordinal);
        _negative = new Dictionary<string, long>(negative, StringComparer.Ordinal);
    }

    /// <summary>Net value: total positive minus total negative.</summary>
    public long Value
    {
        get
        {
            long p = 0, n = 0;
            foreach (var v in _positive.Values) p += v;
            foreach (var v in _negative.Values) n += v;
            return p - n;
        }
    }

    /// <summary>Per-node positive tallies (for serialization).</summary>
    public IReadOnlyDictionary<string, long> Positive => _positive;

    /// <summary>Per-node negative tallies (for serialization).</summary>
    public IReadOnlyDictionary<string, long> Negative => _negative;

    /// <summary>Increment this node's positive tally (amount must be ≥ 0).</summary>
    public void Increment(string nodeId, long amount = 1)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Use Decrement for negative deltas.");
        _positive[nodeId] = _positive.GetValueOrDefault(nodeId) + amount;
    }

    /// <summary>Increment this node's negative tally (amount must be ≥ 0).</summary>
    public void Decrement(string nodeId, long amount = 1)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be non-negative.");
        _negative[nodeId] = _negative.GetValueOrDefault(nodeId) + amount;
    }

    /// <summary>Merge another counter — element-wise max of both per-node maps.</summary>
    public void Merge(PnCounter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        MergeInto(_positive, other._positive);
        MergeInto(_negative, other._negative);
    }

    private static void MergeInto(Dictionary<string, long> target, Dictionary<string, long> source)
    {
        foreach (var (key, value) in source)
        {
            if (!target.TryGetValue(key, out var existing) || value > existing)
                target[key] = value;
        }
    }
}
