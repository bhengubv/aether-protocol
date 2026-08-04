// SPDX-License-Identifier: MIT
namespace AetherNet.Map.Crdt;

/// <summary>
/// Grow-only set (G-Set): elements can only be added; merge is set union. Idempotent, commutative,
/// associative, and monotonic — you can never lose a member, so it cannot be tampered "down".
///
/// Used for a map feature's witness confirmations: each witness adds its own Ed25519 public key (hex),
/// so the confidence of an observed field = the distinct member count, and the set is Sybil-auditable
/// (you can see exactly <i>who</i> attested). Idempotent membership means one witness re-confirming does
/// not inflate the count.
/// </summary>
/// <typeparam name="T">Element type (non-null).</typeparam>
public sealed class GrowOnlySet<T> where T : notnull
{
    private readonly HashSet<T> _elements;

    public GrowOnlySet() => _elements = [];

    public GrowOnlySet(IEnumerable<T> elements) => _elements = [.. elements];

    /// <summary>Distinct members.</summary>
    public IReadOnlyCollection<T> Values => _elements;

    /// <summary>Number of distinct members (e.g. the witness/confidence count).</summary>
    public int Count => _elements.Count;

    public bool Contains(T element) => _elements.Contains(element);

    /// <summary>Add a member (idempotent).</summary>
    public void Add(T element) => _elements.Add(element);

    /// <summary>Merge another set — union.</summary>
    public void Merge(GrowOnlySet<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _elements.UnionWith(other._elements);
    }
}
