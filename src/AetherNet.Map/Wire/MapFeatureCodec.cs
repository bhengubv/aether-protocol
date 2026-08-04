// SPDX-License-Identifier: MIT
using System.Text;
using AetherNet.Map.Crdt;
using AetherNet.Map.Models;

namespace AetherNet.Map.Wire;

/// <summary>
/// Canonical binary (de)serializer for a whole <see cref="MapFeatureCrdt"/> state. This one format is
/// used for BOTH the <c>MapDelta</c> (packet 44) wire payload and the durable <c>crdt_state</c> blob in
/// <c>AetherNet.Map.Sqlite</c> — merge is state-based (reuses the verified <see cref="MapFeatureCrdt.Merge"/>),
/// so a received state is just deserialized and merged.
///
/// <para>Wire discipline matches the rest of the protocol: version byte first; all multi-byte integers
/// little-endian; doubles as raw IEEE-754 little-endian; strings and byte blobs as <c>u16 length + bytes</c>.
/// Every collection is emitted in a CANONICAL order (keys sorted by ordinal) so two nodes holding the same
/// state produce byte-identical output — the property that makes it fixture-pinnable across all 8 languages
/// and content-addressable.</para>
///
/// (A full-state payload re-sends the whole feature on each update; features are small and the 3-cell
/// flood-guard plus dedup bound the spread. Op-level deltas are a possible later bandwidth optimization.)
/// </summary>
public static class MapFeatureCodec
{
    public const byte Version = 1;

    public static byte[] Serialize(MapFeatureCrdt f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var w = new Writer();
        w.U8(Version);
        w.Str(f.FeatureId);
        w.U8((byte)f.FeatureType);
        w.U8((byte)f.AuthorityMode);
        w.Blob(f.OwnerPubKey);

        // location register
        WriteGeo(w, f.LocationRegister.Value);
        WriteHlc(w, f.LocationRegister.Clock);

        // tombstone register
        w.Bool(f.TombstoneRegister.Value);
        WriteHlc(w, f.TombstoneRegister.Clock);

        // attributes (sorted by key)
        var attrs = f.Attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        w.U16(attrs.Count);
        foreach (var (key, reg) in attrs)
        {
            w.Str(key);
            bool has = reg.Value.HasValue;
            w.Bool(has);
            if (has) WriteValue(w, reg.Value!.Value);
            WriteHlc(w, reg.Clock);
        }

        // tags (add-wins set state, sorted by element)
        var tags = f.TagSet.State.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        w.U16(tags.Count);
        foreach (var (element, state) in tags)
        {
            w.Str(element);
            w.Bool(state.Add.HasValue);
            if (state.Add.HasValue) WriteHlc(w, state.Add.Value);
            w.Bool(state.Remove.HasValue);
            if (state.Remove.HasValue) WriteHlc(w, state.Remove.Value);
        }

        // per-field witness sets (sorted by field, then by witness)
        var fields = f.FieldWitnesses.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        w.U16(fields.Count);
        foreach (var (field, set) in fields)
        {
            w.Str(field);
            var witnesses = set.Values.OrderBy(x => x, StringComparer.Ordinal).ToList();
            w.U16(witnesses.Count);
            foreach (var wk in witnesses) w.Str(wk);
        }

        // sentiment PN-counter (sorted by node)
        WriteCounterMap(w, f.SentimentCounter.Positive);
        WriteCounterMap(w, f.SentimentCounter.Negative);

        return w.ToArray();
    }

    public static MapFeatureCrdt Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var r = new Reader(bytes);
        byte version = r.U8();
        if (version != Version)
            throw new FormatException($"Unsupported MapFeature wire version {version}.");

        string featureId = r.Str();
        var featureType = (MapFeatureType)r.U8();
        var authority = (AuthorityMode)r.U8();
        byte[] ownerBytes = r.Blob();
        byte[]? ownerKey = ownerBytes.Length == 0 ? null : ownerBytes;

        var location = ReadGeo(r);
        var locationClock = ReadHlc(r);
        var feature = new MapFeatureCrdt(featureId, featureType, authority, ownerKey, location, locationClock);

        bool tombstone = r.Bool();
        var tombstoneClock = ReadHlc(r);
        if (tombstoneClock > HybridLogicalClock.Zero)
        {
            if (tombstone) feature.Delete(tombstoneClock);
            else feature.Undelete(tombstoneClock);
        }

        int attrCount = r.U16();
        for (int i = 0; i < attrCount; i++)
        {
            string key = r.Str();
            bool has = r.Bool();
            MapValue? value = has ? ReadValue(r) : null;
            var clock = ReadHlc(r);
            feature.SetAttribute(key, value, clock);
        }

        int tagCount = r.U16();
        for (int i = 0; i < tagCount; i++)
        {
            string element = r.Str();
            if (r.Bool()) feature.AddTag(element, ReadHlc(r));
            if (r.Bool()) feature.RemoveTag(element, ReadHlc(r));
        }

        int fieldCount = r.U16();
        for (int i = 0; i < fieldCount; i++)
        {
            string field = r.Str();
            int witnessCount = r.U16();
            for (int j = 0; j < witnessCount; j++)
                feature.AddWitness(field, r.Str());
        }

        ReadCounterMap(r, (node, value) => feature.SentimentCounter.Increment(node, value));
        ReadCounterMap(r, (node, value) => feature.SentimentCounter.Decrement(node, value));

        return feature;
    }

    // ── field encoders ──────────────────────────────────────────────────────
    private static void WriteGeo(Writer w, GeoPoint g)
    {
        w.F64(g.Latitude);
        w.F64(g.Longitude);
        w.Str(g.Geohash);
    }

    private static GeoPoint ReadGeo(Reader r)
    {
        double lat = r.F64();
        double lon = r.F64();
        string geo = r.Str();
        return new GeoPoint(lat, lon, geo);
    }

    private static void WriteValue(Writer w, MapValue v)
    {
        w.U8((byte)v.Kind);
        switch (v.Kind)
        {
            case MapValueKind.String: w.Str(v.Text); break;
            case MapValueKind.Int: w.I64(v.AsInt()); break;
            case MapValueKind.Float: w.F64(v.AsFloat()); break;
            case MapValueKind.Bool: w.Bool(v.AsBool()); break;
            default: throw new FormatException($"Unknown MapValueKind {v.Kind}.");
        }
    }

    private static MapValue ReadValue(Reader r) => (MapValueKind)r.U8() switch
    {
        MapValueKind.String => MapValue.String(r.Str()),
        MapValueKind.Int => MapValue.Int(r.I64()),
        MapValueKind.Float => MapValue.Float(r.F64()),
        MapValueKind.Bool => MapValue.Bool(r.Bool()),
        var k => throw new FormatException($"Unknown MapValueKind {k}."),
    };

    private static void WriteHlc(Writer w, HybridLogicalClock c)
    {
        w.I64(c.PhysicalMs);
        w.U16(c.Counter);
        w.Str(c.NodeId);
    }

    private static HybridLogicalClock ReadHlc(Reader r)
    {
        long ms = r.I64();
        ushort counter = (ushort)r.U16();
        string node = r.Str();
        return new HybridLogicalClock(ms, counter, node);
    }

    private static void WriteCounterMap(Writer w, IReadOnlyDictionary<string, long> map)
    {
        var sorted = map.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        w.U16(sorted.Count);
        foreach (var (node, value) in sorted)
        {
            w.Str(node);
            w.I64(value);
        }
    }

    private static void ReadCounterMap(Reader r, Action<string, long> apply)
    {
        int count = r.U16();
        for (int i = 0; i < count; i++)
        {
            string node = r.Str();
            long value = r.I64();
            apply(node, value);
        }
    }

    // ── little-endian, u16-length wire primitives (matches SyncRecordSerializer discipline) ──
    private sealed class Writer
    {
        private readonly List<byte> _b = new(256);
        public void U8(int v) => _b.Add((byte)v);
        public void Bool(bool v) => _b.Add(v ? (byte)1 : (byte)0);
        public void U16(int v) { _b.Add((byte)(v & 0xFF)); _b.Add((byte)((v >> 8) & 0xFF)); }
        public void I64(long v) { for (int i = 0; i < 8; i++) { _b.Add((byte)(v & 0xFF)); v >>= 8; } }
        public void F64(double v) => I64(BitConverter.DoubleToInt64Bits(v));
        public void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            if (bytes.Length > ushort.MaxValue) throw new FormatException("String exceeds u16 length.");
            U16(bytes.Length);
            _b.AddRange(bytes);
        }
        public void Blob(byte[]? b)
        {
            b ??= [];
            if (b.Length > ushort.MaxValue) throw new FormatException("Blob exceeds u16 length.");
            U16(b.Length);
            _b.AddRange(b);
        }
        public byte[] ToArray() => [.. _b];
    }

    private sealed class Reader(byte[] buffer)
    {
        private readonly byte[] _b = buffer;
        private int _o;

        private void Need(int n)
        {
            if (_o + n > _b.Length) throw new FormatException("Truncated MapFeature payload.");
        }
        public byte U8() { Need(1); return _b[_o++]; }
        public bool Bool() => U8() != 0;
        public int U16() { Need(2); int v = _b[_o] | (_b[_o + 1] << 8); _o += 2; return v; }
        public long I64() { Need(8); long v = 0; for (int i = 0; i < 8; i++) v |= (long)_b[_o + i] << (8 * i); _o += 8; return v; }
        public double F64() => BitConverter.Int64BitsToDouble(I64());
        public string Str() { int n = U16(); Need(n); var s = Encoding.UTF8.GetString(_b, _o, n); _o += n; return s; }
        public byte[] Blob() { int n = U16(); Need(n); var r = new byte[n]; Array.Copy(_b, _o, r, 0, n); _o += n; return r; }
    }
}
