// SPDX-License-Identifier: MIT

using System.Text;

namespace AetherNet.BitTorrent.Bencoding;

/// <summary>Thrown when bencoded data is malformed (BEP-3 violations, overflow, trailing data, …).</summary>
public sealed class BencodeException : Exception
{
    public BencodeException(string message) : base(message) { }
}

/// <summary>
/// Unsigned lexicographic ordering and equality for raw byte-string dictionary keys —
/// the canonical key order required by BEP-3.
/// </summary>
internal sealed class ByteArrayComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());

    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y.AsSpan());

    public int GetHashCode(byte[] obj)
    {
        var hash = new HashCode();
        hash.AddBytes(obj);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A decoded bencode value (BEP-3): an integer, a byte string, a list, or a dictionary.
/// Bencode strings hold raw bytes — they are NOT necessarily text.
/// </summary>
public abstract class BencodeValue
{
    /// <summary>Canonical bencode encoding of this value (dictionary keys sorted by raw byte order).</summary>
    public byte[] Encode() => Bencode.Encode(this);

    public long AsInteger() => this is BencodeInteger i
        ? i.Value
        : throw new InvalidCastException($"bencode value is {GetType().Name}, not an integer");

    public byte[] AsBytes() => this is BencodeString s
        ? s.Value
        : throw new InvalidCastException($"bencode value is {GetType().Name}, not a byte string");

    public string AsText() => Encoding.UTF8.GetString(AsBytes());

    public IReadOnlyList<BencodeValue> AsList() => this is BencodeList l
        ? l.Items
        : throw new InvalidCastException($"bencode value is {GetType().Name}, not a list");

    public BencodeDictionary AsDictionary() => this as BencodeDictionary
        ?? throw new InvalidCastException($"bencode value is {GetType().Name}, not a dictionary");
}

/// <summary>A bencode integer: <c>i&lt;decimal&gt;e</c> (64-bit).</summary>
public sealed class BencodeInteger : BencodeValue
{
    public long Value { get; }
    public BencodeInteger(long value) => Value = value;

    public override bool Equals(object? obj) => obj is BencodeInteger o && o.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => $"i{Value}e";
}

/// <summary>A bencode byte string: <c>&lt;length&gt;:&lt;bytes&gt;</c>. Raw bytes, not necessarily text.</summary>
public sealed class BencodeString : BencodeValue
{
    public byte[] Value { get; }

    public BencodeString(byte[] value) => Value = value ?? throw new ArgumentNullException(nameof(value));
    public BencodeString(string text) => Value = Encoding.UTF8.GetBytes(text);

    public override bool Equals(object? obj) => obj is BencodeString o && o.Value.AsSpan().SequenceEqual(Value);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }
    public override string ToString()
    {
        try { return $"\"{Encoding.UTF8.GetString(Value)}\""; }
        catch { return $"<{Value.Length} bytes>"; }
    }
}

/// <summary>A bencode list: <c>l&lt;values…&gt;e</c>.</summary>
public sealed class BencodeList : BencodeValue
{
    public List<BencodeValue> Items { get; }

    public BencodeList() => Items = new();
    public BencodeList(IEnumerable<BencodeValue> items) => Items = new(items);

    public void Add(BencodeValue value) => Items.Add(value);

    public override bool Equals(object? obj) =>
        obj is BencodeList o && o.Items.Count == Items.Count && o.Items.SequenceEqual(Items);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items) hash.Add(item);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A bencode dictionary: <c>d&lt;key&gt;&lt;value&gt;…e</c> where keys are byte strings.
/// Keys must be unique; canonical encoding emits them sorted by raw byte order.
/// </summary>
public sealed class BencodeDictionary : BencodeValue
{
    private readonly Dictionary<byte[], BencodeValue> _entries = new(ByteArrayComparer.Instance);

    public int Count => _entries.Count;

    public void Add(string key, BencodeValue value) => Add(Encoding.UTF8.GetBytes(key), value);

    public void Add(byte[] key, BencodeValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!_entries.TryAdd(key, value))
            throw new BencodeException($"duplicate dictionary key \"{Encoding.UTF8.GetString(key)}\"");
    }

    public bool TryGet(string key, out BencodeValue value) => _entries.TryGetValue(Encoding.UTF8.GetBytes(key), out value!);
    public bool TryGet(byte[] key, out BencodeValue value) => _entries.TryGetValue(key, out value!);

    /// <summary>Value for a UTF-8 key, or null if absent.</summary>
    public BencodeValue? this[string key] => TryGet(key, out var v) ? v : null;

    /// <summary>Entries in canonical (unsigned-byte-sorted) key order.</summary>
    public IEnumerable<KeyValuePair<byte[], BencodeValue>> SortedEntries() =>
        _entries.OrderBy(e => e.Key, ByteArrayComparer.Instance);

    public override bool Equals(object? obj)
    {
        if (obj is not BencodeDictionary o || o._entries.Count != _entries.Count) return false;
        foreach (var (key, value) in _entries)
            if (!o._entries.TryGetValue(key, out var other) || !Equals(value, other)) return false;
        return true;
    }
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in SortedEntries()) { hash.AddBytes(key); hash.Add(value); }
        return hash.ToHashCode();
    }
}
