// SPDX-License-Identifier: MIT
using AetherNet.Map.Models;

namespace AetherNet.Map.Crdt;

/// <summary>
/// The per-feature CRDT: an observed-remove map of typed field CRDTs. A feature's <i>identity</i>
/// (id, type, authority mode, owner key) is fixed at genesis and never merges; everything else —
/// location, scalar attributes, tags, per-field witness sets, sentiment, tombstone — is a field CRDT,
/// so <see cref="Merge"/> is per-field and therefore commutative, associative and idempotent. Two
/// people editing different attributes (or even the same one) of the same feature while partitioned
/// both survive the merge; every node that sees the same ops converges on the identical state,
/// regardless of order or duplication.
/// </summary>
public sealed class MapFeatureCrdt
{
    /// <summary>Author-independent feature id (immutable identity).</summary>
    public string FeatureId { get; }

    /// <summary>What the feature is (immutable identity).</summary>
    public MapFeatureType FeatureType { get; }

    /// <summary>How writes are admitted (immutable identity).</summary>
    public AuthorityMode AuthorityMode { get; }

    /// <summary>Owner Ed25519 public key for owner-authoritative features; null for observed-consensus.</summary>
    public byte[]? OwnerPubKey { get; }

    private LwwRegister<GeoPoint> _location;
    private LwwRegister<bool> _tombstone;
    private readonly Dictionary<string, LwwRegister<MapValue?>> _attributes = new(StringComparer.Ordinal);
    private readonly AddWinsSet<string> _tags = new();
    private readonly Dictionary<string, GrowOnlySet<string>> _fieldWitnesses = new(StringComparer.Ordinal);
    private readonly PnCounter _sentiment = new();

    /// <summary>Create (genesis) a feature at <paramref name="location"/> stamped <paramref name="genesisClock"/>.</summary>
    public MapFeatureCrdt(
        string featureId,
        MapFeatureType featureType,
        AuthorityMode authorityMode,
        byte[]? ownerPubKey,
        GeoPoint location,
        HybridLogicalClock genesisClock)
    {
        FeatureId = featureId ?? throw new ArgumentNullException(nameof(featureId));
        FeatureType = featureType;
        AuthorityMode = authorityMode;
        OwnerPubKey = ownerPubKey;
        _location = new LwwRegister<GeoPoint>(location, genesisClock);
        _tombstone = new LwwRegister<bool>(false, HybridLogicalClock.Zero);
    }

    // ── Materialized views ─────────────────────────────────────────────────
    public GeoPoint Location => _location.Value;
    public bool IsDeleted => _tombstone.Value;

    /// <summary>Attributes that currently have a (non-cleared) value.</summary>
    public IReadOnlyDictionary<string, MapValue> PresentAttributes
    {
        get
        {
            var result = new Dictionary<string, MapValue>(StringComparer.Ordinal);
            foreach (var (key, reg) in _attributes)
                if (reg.Value is { } v) result[key] = v;
            return result;
        }
    }

    /// <summary>Tags currently in the set.</summary>
    public IEnumerable<string> Tags => _tags.Values;

    /// <summary>Net sentiment (up minus down).</summary>
    public long Sentiment => _sentiment.Value;

    /// <summary>
    /// The greatest Hybrid Logical Clock across the feature's clocked fields (location, tombstone,
    /// attributes, tag add/remove) — the feature's sync cursor / "updated at". Used for anti-entropy
    /// ("send me features changed since H"). Note: witness-set and sentiment additions are monotonic but
    /// unclocked, so a witness/vote-only change does not advance this cursor — those propagate on a full
    /// cell pull (<c>MapFeatureRequest</c>) rather than the incremental cursor.
    /// </summary>
    public HybridLogicalClock MaxClock
    {
        get
        {
            var max = _location.Clock;
            if (_tombstone.Clock > max) max = _tombstone.Clock;
            foreach (var reg in _attributes.Values)
                if (reg.Clock > max) max = reg.Clock;
            foreach (var state in _tags.State.Values)
            {
                if (state.Add is { } a && a > max) max = a;
                if (state.Remove is { } r && r > max) max = r;
            }
            return max;
        }
    }

    /// <summary>Distinct witnesses attesting a given field's current value (observed-consensus confidence).</summary>
    public int WitnessCount(string fieldKey)
        => _fieldWitnesses.TryGetValue(fieldKey, out var set) ? set.Count : 0;

    // ── State accessors (for serialization of crdt_state) ───────────────────
    public LwwRegister<GeoPoint> LocationRegister => _location;
    public LwwRegister<bool> TombstoneRegister => _tombstone;
    public IReadOnlyDictionary<string, LwwRegister<MapValue?>> Attributes => _attributes;
    public AddWinsSet<string> TagSet => _tags;
    public IReadOnlyDictionary<string, GrowOnlySet<string>> FieldWitnesses => _fieldWitnesses;
    public PnCounter SentimentCounter => _sentiment;

    // ── Mutations (apply an op; caller supplies the HLC) ────────────────────
    public void SetLocation(GeoPoint location, HybridLogicalClock clock)
        => _location = _location.Set(location, clock);

    /// <summary>Set (or, with a null value, clear) a scalar attribute.</summary>
    public void SetAttribute(string key, MapValue? value, HybridLogicalClock clock)
    {
        var current = _attributes.TryGetValue(key, out var reg)
            ? reg
            : new LwwRegister<MapValue?>(null, HybridLogicalClock.Zero);
        _attributes[key] = current.Set(value, clock);
    }

    public void AddTag(string tag, HybridLogicalClock clock) => _tags.Add(tag, clock);
    public void RemoveTag(string tag, HybridLogicalClock clock) => _tags.Remove(tag, clock);

    /// <summary>Record a witness (Ed25519 pubkey hex) attesting the current value of a field.</summary>
    public void AddWitness(string fieldKey, string witnessPublicKeyHex)
    {
        if (!_fieldWitnesses.TryGetValue(fieldKey, out var set))
            _fieldWitnesses[fieldKey] = set = new GrowOnlySet<string>();
        set.Add(witnessPublicKeyHex);
    }

    public void Upvote(string nodeId) => _sentiment.Increment(nodeId);
    public void Downvote(string nodeId) => _sentiment.Decrement(nodeId);

    public void Delete(HybridLogicalClock clock) => _tombstone = _tombstone.Set(true, clock);
    public void Undelete(HybridLogicalClock clock) => _tombstone = _tombstone.Set(false, clock);

    // ── Merge ───────────────────────────────────────────────────────────────
    /// <summary>Merge another replica of the SAME feature into this one (per-field CRDT join).</summary>
    public void Merge(MapFeatureCrdt other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(FeatureId, other.FeatureId, StringComparison.Ordinal))
            throw new ArgumentException("Cannot merge different features.", nameof(other));
        if (FeatureType != other.FeatureType || AuthorityMode != other.AuthorityMode)
            throw new ArgumentException("Feature identity (type/authority) diverged — genesis inconsistency.", nameof(other));
        if (!OwnerKeysEqual(OwnerPubKey, other.OwnerPubKey))
            throw new ArgumentException("Feature owner key diverged — genesis inconsistency.", nameof(other));

        _location = _location.Merge(other._location);
        _tombstone = _tombstone.Merge(other._tombstone);

        foreach (var (key, reg) in other._attributes)
        {
            var current = _attributes.TryGetValue(key, out var mine)
                ? mine
                : new LwwRegister<MapValue?>(null, HybridLogicalClock.Zero);
            _attributes[key] = current.Merge(reg);
        }

        _tags.Merge(other._tags);

        foreach (var (fieldKey, otherSet) in other._fieldWitnesses)
        {
            if (!_fieldWitnesses.TryGetValue(fieldKey, out var set))
                _fieldWitnesses[fieldKey] = set = new GrowOnlySet<string>();
            set.Merge(otherSet);
        }

        _sentiment.Merge(other._sentiment);
    }

    private static bool OwnerKeysEqual(byte[]? a, byte[]? b)
    {
        if (a is null) return b is null;
        if (b is null) return false;
        return a.AsSpan().SequenceEqual(b);
    }
}
