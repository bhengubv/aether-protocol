// SPDX-License-Identifier: MIT

using AetherNet.Map;
using AetherNet.Map.Crdt;
using AetherNet.Map.Models;
using AetherNet.Security.Services;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// The offline neighbourhood map, driven on the real <c>aether-map</c> CRDTs — no server anywhere in it.
///
/// <para>Two people, Thabo and Lerato, each hold a replica of the same storefront. The demo puts them on
/// opposite sides of a partition, lets each edit while the other is unreachable, then merges the two
/// replicas and shows them land on byte-identical state. That is the promise of a field-level CRDT: edits
/// to <i>different</i> attributes both survive (the failure mode of whole-record last-write-wins), and a
/// genuine clash on the <i>same</i> attribute resolves to the higher Hybrid Logical Clock — the same
/// winner on both devices, because the HLC total order is deterministic down to the node-id tiebreak.</para>
///
/// <para>It also shows the two authority modes the model carries. The storefront is
/// <see cref="AuthorityMode.OwnerAuthoritative"/>: its owner key is part of the feature's genesis
/// identity, so a replica claiming a different owner cannot be merged in — <see cref="MapFeatureCrdt.Merge"/>
/// rejects it outright. A kerb ramp is <see cref="AuthorityMode.ObservedConsensus"/>: anyone may attest it,
/// and its confidence is the count of <i>distinct</i> witnesses in a grow-only set, which repeats cannot
/// inflate. And a proximity query over a geohash-indexed store returns exactly the cell and its eight
/// neighbours.</para>
///
/// Every merge, clock tick, witness and query below is the real code the eight SDKs port.
/// </summary>
public sealed class MapDemo
{
    private const string StoreFrontId = "kopano-spaza";
    private const string RampId = "ramp-main-st";
    private const string RampField = "ramp";
    private const int RampThreshold = 3; // witnesses needed before an observed field reads as "confirmed"

    private static readonly string[] Bystanders = { "Thabo", "Lerato", "Naledi", "Sipho", "Amara", "Kagiso" };

    // ── Partition-and-converge storefront ────────────────────────────────────
    private byte[] _ownerKey = Array.Empty<byte>();
    private GeoPoint _genesisLoc;
    private HybridLogicalClock _genesisClock;
    private MapFeatureCrdt _thabo = null!;
    private MapFeatureCrdt _lerato = null!;
    private HybridLogicalClock _clockT;
    private HybridLogicalClock _clockL;
    private HybridLogicalClock? _hoursClockT;
    private HybridLogicalClock? _hoursClockL;
    private bool _thaboEdited, _leratoEdited, _merged;

    // ── Observed-consensus ramp ──────────────────────────────────────────────
    private MapFeatureCrdt _ramp = null!;
    private int _upVoters, _downVoters, _witnessCursor;

    // ── Geohash proximity ────────────────────────────────────────────────────
    private readonly InMemoryMapStore _poiStore = new();
    private readonly Dictionary<string, string> _poiNames = new(StringComparer.Ordinal); // cell → name
    private (int Row, int Col) _queryCenter;
    private IReadOnlyList<string> _matchedCells = Array.Empty<string>();

    private readonly List<string> _log = new();

    public MapDemo()
    {
        ProximityGrid = new GeohashGrid(-26.2041, 28.0473, 5, 5, precision: 6);
        SeedProximity();
        Reset();
    }

    public event Action? Changed;

    public GeohashGrid ProximityGrid { get; }

    // ── Storefront state the page reads ──────────────────────────────────────
    public string FeatureId => StoreFrontId;
    public string OwnerKeyShort => _ownerKey.Length == 0 ? "" : Convert.ToHexString(_ownerKey)[..12].ToLowerInvariant();
    public bool ThaboEdited => _thaboEdited;
    public bool LeratoEdited => _leratoEdited;
    public bool Merged => _merged;
    public IReadOnlyList<string> Log => _log.ToArray();

    public sealed record AttrView(string Key, string Value, string Clock);
    public sealed record FeatureView(string LocationCell, IReadOnlyList<AttrView> Attributes, IReadOnlyList<string> Tags);
    public sealed record HoursConflictView(string ValueT, string ClockT, string ValueL, string ClockL, bool WinnerIsLerato, bool Merged);
    public sealed record ObservedView(bool RampPresent, int Witnesses, int Threshold, bool Confirmed, long Sentiment, IReadOnlyList<string> Attestors);
    public sealed record CellView(int Row, int Col, string Cell, string? Poi, bool InRing, bool Matched, bool IsCenter);

    public FeatureView Replica(bool thabo) => Snapshot(thabo ? _thabo : _lerato);

    /// <summary>Both replicas' "hours", the field they both edited — the LWW clash and its deterministic winner.</summary>
    public HoursConflictView? HoursConflict
    {
        get
        {
            if (_hoursClockT is not { } ct || _hoursClockL is not { } cl) return null;
            var winnerIsLerato = cl > ct;
            return new HoursConflictView(
                ValueOf(_thabo, "hours", "08:00–18:00"), ct.ToString(),
                ValueOf(_lerato, "hours", "09:00–17:00"), cl.ToString(),
                winnerIsLerato, _merged);
        }
    }

    public ObservedView Observed => new(
        _ramp.PresentAttributes.TryGetValue(RampField, out var v) && v.AsBool(),
        _ramp.WitnessCount(RampField), RampThreshold, _ramp.WitnessCount(RampField) >= RampThreshold,
        _ramp.Sentiment,
        _ramp.FieldWitnesses.TryGetValue(RampField, out var set) ? set.Values.ToArray() : Array.Empty<string>());

    // ── Partition & converge ─────────────────────────────────────────────────

    public void Reset()
    {
        var (_, pub) = Ed25519SigningService.GenerateKeyPair();
        _ownerKey = pub;

        var now = NowMs();
        _genesisClock = HybridLogicalClock.Start("genesis", now);
        _genesisLoc = GeoPoint.At(-26.2041, 28.0473, 7);
        _thabo = NewStorefront();
        _lerato = NewStorefront();
        _clockT = HybridLogicalClock.Start("thabo", now);
        _clockL = HybridLogicalClock.Start("lerato", now);
        _hoursClockT = _hoursClockL = null;
        _thaboEdited = _leratoEdited = _merged = false;

        // The observed-consensus ramp, freshly stated, no witnesses yet.
        _ramp = new MapFeatureCrdt(RampId, MapFeatureType.SidewalkFeature, AuthorityMode.ObservedConsensus,
            null, GeoPoint.At(-26.2043, 28.0475, 7), HybridLogicalClock.Start("ramp", now));
        _ramp.SetAttribute(RampField, MapValue.Bool(true), HybridLogicalClock.Start("ramp", now).Tick(now));
        _upVoters = _downVoters = _witnessCursor = 0;

        _log.Clear();
        Note("Genesis: both Thabo and Lerato hold “Kopano Spaza”, an owner-authoritative storefront. Identical replicas.");
        Raise();
    }

    /// <summary>Thabo, offline, sets the hours, re-pins the shop, and tags it wheelchair-accessible.</summary>
    public void EditAsThabo()
    {
        if (_merged) return;
        var now = NowMs();
        _clockT = _clockT.Tick(now); _thabo.SetAttribute("hours", MapValue.String("08:00–18:00"), _clockT); _hoursClockT = _clockT;
        _clockT = _clockT.Tick(now); _thabo.SetLocation(GeoPoint.At(-26.20455, 28.04695, 7), _clockT);
        _clockT = _clockT.Tick(now); _thabo.AddTag("wheelchair", _clockT);
        _thaboEdited = true;
        Note("Thabo (offline) set hours 08:00–18:00, re-pinned the shopfront, tagged wheelchair.");
        Raise();
    }

    /// <summary>Lerato, offline, sets DIFFERENT hours (the clash), adds a phone, tags card-accepted.</summary>
    public void EditAsLerato()
    {
        if (_merged) return;
        var now = NowMs();
        _clockL = _clockL.Tick(now); _lerato.SetAttribute("hours", MapValue.String("09:00–17:00"), _clockL); _hoursClockL = _clockL;
        _clockL = _clockL.Tick(now); _lerato.SetAttribute("phone", MapValue.String("071 555 0142"), _clockL);
        _clockL = _clockL.Tick(now); _lerato.AddTag("card-accepted", _clockL);
        _leratoEdited = true;
        Note("Lerato (offline) set hours 09:00–17:00 — a clash — added a phone, tagged card-accepted.");
        Raise();
    }

    /// <summary>The partition heals: each replica merges the other. Per-field CRDT join → they converge.</summary>
    public void Merge()
    {
        if (_merged) return;
        _thabo.Merge(_lerato);
        _lerato.Merge(_thabo); // now both = Thabo ∪ Lerato (commutative, idempotent)
        var now = NowMs();
        _clockT = _clockT.Receive(_lerato.MaxClock, now);
        _clockL = _clockL.Receive(_thabo.MaxClock, now);
        _merged = true;

        if (HoursConflict is { } hc)
        {
            var winner = hc.WinnerIsLerato ? "Lerato's 09:00–17:00" : "Thabo's 08:00–18:00";
            Note($"Synced. Both replicas are identical now: every distinct edit survived, and the hours clash resolved to {winner} — the higher HLC, the same winner on both phones.");
        }
        else
        {
            Note("Synced. Both replicas are identical now — every edit survived the merge.");
        }
        Raise();
    }

    /// <summary>An impostor forges a replica with a different owner key and tries to merge it in.</summary>
    public bool TryImpostorMerge()
    {
        var (_, impostor) = Ed25519SigningService.GenerateKeyPair();
        var forged = new MapFeatureCrdt(StoreFrontId, MapFeatureType.Storefront, AuthorityMode.OwnerAuthoritative,
            impostor, _genesisLoc, _genesisClock);
        forged.SetAttribute("hours", MapValue.String("closed — under new management"), _clockT.Tick(NowMs()));
        try
        {
            _thabo.Merge(forged); // owner-key check throws before any field is touched
            Note("Impostor merge was ACCEPTED — that should not happen.");
            Raise();
            return false;
        }
        catch (ArgumentException)
        {
            Note("Impostor rejected: the forged replica's owner key isn't the storefront's, so the merge is refused before a single field changes. An owner-authoritative feature can't be rewritten by a stranger.");
            Raise();
            return true;
        }
    }

    // ── Observed-consensus ramp ──────────────────────────────────────────────

    public void ConfirmRamp()
    {
        if (_witnessCursor >= Bystanders.Length)
        {
            Note("Everyone nearby has already attested the ramp.");
            Raise();
            return;
        }
        var who = Bystanders[_witnessCursor++];
        _ramp.AddWitness(RampField, who);
        var n = _ramp.WitnessCount(RampField);
        Note($"{who} attests the ramp is there — {n} witness(es){(n >= RampThreshold ? ", confirmed." : $", needs {RampThreshold}.")}");
        Raise();
    }

    public void ReconfirmRamp()
    {
        if (_witnessCursor == 0) return;
        var who = Bystanders[_witnessCursor - 1];
        var before = _ramp.WitnessCount(RampField);
        _ramp.AddWitness(RampField, who); // same key, grow-only set — no change
        Note($"{who} attests again — still {_ramp.WitnessCount(RampField)} (was {before}). A grow-only set can't be inflated by one device voting twice.");
        Raise();
    }

    public void Upvote() { _ramp.Upvote($"up-{_upVoters++}"); Note($"An anonymous up-vote (PN-counter) — net sentiment {_ramp.Sentiment}."); Raise(); }
    public void Downvote() { _ramp.Downvote($"down-{_downVoters++}"); Note($"An anonymous down-vote — net sentiment {_ramp.Sentiment}."); Raise(); }

    // ── Geohash proximity ────────────────────────────────────────────────────

    public IReadOnlyList<CellView> ProximityCells()
    {
        var ring = new HashSet<string>(Geohash.CellAndNeighbours(ProximityGrid.Cell(_queryCenter.Row, _queryCenter.Col)), StringComparer.Ordinal);
        var matched = new HashSet<string>(_matchedCells, StringComparer.Ordinal);
        var cells = new List<CellView>(ProximityGrid.Rows * ProximityGrid.Cols);
        for (var r = 0; r < ProximityGrid.Rows; r++)
        for (var c = 0; c < ProximityGrid.Cols; c++)
        {
            var cell = ProximityGrid.Cell(r, c);
            cells.Add(new CellView(r, c, cell,
                _poiNames.GetValueOrDefault(cell),
                ring.Contains(cell),
                matched.Contains(cell),
                (r, c) == _queryCenter));
        }
        return cells;
    }

    public async Task MoveQueryAsync(int row, int col)
    {
        _queryCenter = (row, col);
        var center = ProximityGrid.Cell(row, col);
        var results = await _poiStore.QueryProximityAsync(center, radiusCells: 1);
        _matchedCells = results.Select(f => f.Location.Geohash).ToArray();
        var hits = string.Join(", ", results.Select(f => _poiNames.GetValueOrDefault(f.Location.Geohash) ?? f.Location.Geohash));
        Note($"Proximity query at {center}: cell + 8 neighbours → {results.Count} feature(s){(hits.Length > 0 ? $" — {hits}" : "")}.");
        Raise();
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private MapFeatureCrdt NewStorefront()
        => new(StoreFrontId, MapFeatureType.Storefront, AuthorityMode.OwnerAuthoritative, _ownerKey, _genesisLoc, _genesisClock);

    private void SeedProximity()
    {
        // A handful of places on the block: the shop at the centre, a few one cell out, two two cells out.
        (int R, int C, string Name)[] places =
        {
            (2, 2, "Kopano Spaza"),
            (1, 2, "Clinic"),
            (2, 3, "Taxi rank"),
            (3, 1, "Primary school"),
            (0, 0, "Reservoir"),
            (4, 4, "Water point"),
        };
        var now = NowMs();
        foreach (var (r, c, name) in places)
        {
            var cell = ProximityGrid.Cell(r, c);
            var (lat, lon) = Geohash.Decode(cell);
            var f = new MapFeatureCrdt($"poi-{r}-{c}", MapFeatureType.Landmark, AuthorityMode.ObservedConsensus,
                null, new GeoPoint(lat, lon, cell), HybridLogicalClock.Start("poi", now));
            f.SetAttribute("name", MapValue.String(name), HybridLogicalClock.Start("poi", now).Tick(now));
            _poiStore.ApplyAsync(f).GetAwaiter().GetResult(); // in-memory, completes synchronously
            _poiNames[cell] = name;
        }
        _queryCenter = ProximityGrid.Center;
        _matchedCells = _poiStore
            .QueryProximityAsync(ProximityGrid.Cell(_queryCenter.Row, _queryCenter.Col), radiusCells: 1)
            .GetAwaiter().GetResult()
            .Select(f => f.Location.Geohash).ToArray();
    }

    private static FeatureView Snapshot(MapFeatureCrdt f)
    {
        var attrs = new List<AttrView>();
        foreach (var (key, reg) in f.Attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (reg.Value is { } v)
                attrs.Add(new AttrView(key, v.ToString(), reg.Clock.ToString()));
        return new FeatureView(f.Location.Geohash, attrs, f.Tags.OrderBy(t => t, StringComparer.Ordinal).ToArray());
    }

    private static string ValueOf(MapFeatureCrdt f, string key, string fallback)
        => f.PresentAttributes.TryGetValue(key, out var v) ? v.ToString() : fallback;

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void Note(string text)
    {
        _log.Add(text);
        if (_log.Count > 120) _log.RemoveRange(0, _log.Count - 120);
    }

    private void Raise() => Changed?.Invoke();
}
