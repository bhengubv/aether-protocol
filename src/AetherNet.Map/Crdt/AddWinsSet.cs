// SPDX-License-Identifier: MIT
namespace AetherNet.Map.Crdt;

/// <summary>
/// Add-wins last-write-wins element set. Each element carries the latest add-clock and the latest
/// remove-clock; the element is present when it has an add that is not strictly older than its remove
/// (so on a concurrent add vs. remove, the add wins — e.g. two people tagging a shop "wheelchair" while
/// a third removes it: the tag stays). Chosen over a full OR-Set to keep per-element metadata bounded to
/// two clocks on a low-RAM phone, and over a 2P-set because an element can be re-added after removal
/// (a ramp removed then rebuilt).
///
/// Merge takes the per-element max of both clocks, so it is commutative, associative and idempotent.
/// Used for a map feature's amenity/attribute tag set.
/// </summary>
/// <typeparam name="T">Element type (non-null; typically a tag string).</typeparam>
public sealed class AddWinsSet<T> where T : notnull
{
    /// <summary>Per-element add/remove clocks. Exposed for serialization of the full CRDT state.</summary>
    public readonly record struct ElementState(HybridLogicalClock? Add, HybridLogicalClock? Remove)
    {
        public bool IsPresent => Add.HasValue && (!Remove.HasValue || Add.Value >= Remove.Value);
    }

    private readonly Dictionary<T, ElementState> _elements;

    public AddWinsSet() => _elements = new Dictionary<T, ElementState>();

    public AddWinsSet(IEnumerable<KeyValuePair<T, ElementState>> state)
        => _elements = new Dictionary<T, ElementState>(state);

    /// <summary>Elements currently present in the set.</summary>
    public IEnumerable<T> Values => _elements.Where(kv => kv.Value.IsPresent).Select(kv => kv.Key);

    /// <summary>Full per-element state (present and tombstoned) for serialization.</summary>
    public IReadOnlyDictionary<T, ElementState> State => _elements;

    public bool Contains(T element)
        => _elements.TryGetValue(element, out var s) && s.IsPresent;

    /// <summary>Record an add of <paramref name="element"/> at <paramref name="clock"/> (keeps the max add-clock).</summary>
    public void Add(T element, HybridLogicalClock clock)
    {
        _elements.TryGetValue(element, out var s);
        var add = s.Add is { } a && a >= clock ? a : clock;
        _elements[element] = s with { Add = add };
    }

    /// <summary>Record a remove of <paramref name="element"/> at <paramref name="clock"/> (keeps the max remove-clock).</summary>
    public void Remove(T element, HybridLogicalClock clock)
    {
        _elements.TryGetValue(element, out var s);
        var rem = s.Remove is { } r && r >= clock ? r : clock;
        _elements[element] = s with { Remove = rem };
    }

    /// <summary>Merge another set into this one — per-element max of add and remove clocks.</summary>
    public void Merge(AddWinsSet<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (key, os) in other._elements)
        {
            _elements.TryGetValue(key, out var s);
            _elements[key] = new ElementState(
                Add: MaxClock(s.Add, os.Add),
                Remove: MaxClock(s.Remove, os.Remove));
        }
    }

    private static HybridLogicalClock? MaxClock(HybridLogicalClock? a, HybridLogicalClock? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Value >= b.Value ? a : b;
    }
}
