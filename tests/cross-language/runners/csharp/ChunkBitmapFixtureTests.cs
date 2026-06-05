// SPDX-License-Identifier: MIT
// Cross-language ChunkBitmap wire-format fixture verifier — C# runner.
//
// Reads fixtures/content/chunk_bitmap_vectors.json and verifies that this
// implementation produces bit-identical bitsets and JSON payloads for each
// pinned test vector.
//
// The same fixture corpus is exercised by the Go, Python, TypeScript, Rust,
// Kotlin, Swift, and C runners. Any divergence here == a cross-language
// wire-break that must be fixed before shipping.
//
// Wire format:
//   • JSON, snake_case property names.
//   • Bitset: LSB-first within each byte — bit i is set in byte (i/8), at
//     position (i%8).  Length = ceil(chunk_count / 8).
//   • Bitset transmitted as standard Base64 (with padding).
//   • Field order in canonical JSON: root_hash, chunk_count, have_bitset,
//     generation.

using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace AetherMesh.InteropTest;

// ── Model types ───────────────────────────────────────────────────────────────

file record ChunkBitmapVector(
    [property: JsonPropertyName("name")]             string   Name,
    [property: JsonPropertyName("description")]      string   Description,
    [property: JsonPropertyName("root_hash")]        string   RootHash,
    [property: JsonPropertyName("chunk_count")]      int      ChunkCount,
    [property: JsonPropertyName("have_indices")]     int[]    HaveIndices,
    [property: JsonPropertyName("have_bitset_hex")]  string   HaveBitsetHex,
    [property: JsonPropertyName("have_bitset_base64")] string HaveBitsetBase64,
    [property: JsonPropertyName("generation")]       uint     Generation,
    [property: JsonPropertyName("expected_json")]    string   ExpectedJson
);

// ── Bitset codec (canonical cross-language implementation) ────────────────────

file static class BitsetCodec
{
    /// <summary>
    /// Encode a set of present-chunk indices into an LSB-first compact bitset.
    /// Bit i is set in byte (i/8) at bit-position (i%8).
    /// Returns a byte array of length ceil(chunkCount / 8), all trailing bits zero.
    /// </summary>
    public static byte[] Encode(int chunkCount, IEnumerable<int> haveIndices)
    {
        if (chunkCount <= 0) return [];
        var bytes = new byte[(chunkCount + 7) / 8];
        foreach (var i in haveIndices)
        {
            if (i < 0 || i >= chunkCount)
                throw new ArgumentOutOfRangeException(nameof(haveIndices),
                    $"Index {i} is out of range [0, {chunkCount})");
            bytes[i >> 3] |= (byte)(1 << (i & 7));
        }
        return bytes;
    }

    /// <summary>
    /// Decode a compact bitset into the sorted list of set chunk indices.
    /// Bits beyond chunkCount are ignored.
    /// </summary>
    public static List<int> Decode(byte[] bitset, int chunkCount)
    {
        var result = new List<int>();
        var limit  = Math.Min(chunkCount, bitset.Length * 8);
        for (var i = 0; i < limit; i++)
            if ((bitset[i >> 3] & (1 << (i & 7))) != 0)
                result.Add(i);
        return result;
    }
}

// ── Serialisation helpers ──────────────────────────────────────────────────────

file static class ChunkBitmapJson
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented         = false,
    };

    /// <summary>
    /// Produce the canonical wire JSON for a ChunkBitmapPayload.
    /// Field order is fixed: root_hash → chunk_count → have_bitset → generation.
    /// </summary>
    public static string Serialize(string rootHash, int chunkCount, byte[] haveBitset, uint generation)
    {
        // Write fields in the canonical order defined by the wire spec.
        using var ms  = new System.IO.MemoryStream();
        using var w   = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });
        w.WriteStartObject();
        w.WriteString("root_hash",   rootHash);
        w.WriteNumber("chunk_count", chunkCount);
        w.WriteString("have_bitset", Convert.ToBase64String(haveBitset));
        w.WriteNumber("generation",  generation);
        w.WriteEndObject();
        w.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}

// ── Fixture loader ────────────────────────────────────────────────────────────

file static class ChunkBitmapFixtureLoader
{
    private static string FixturePath()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "content", "chunk_bitmap_vectors.json");
            if (File.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(
                dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException(
            $"Could not locate fixtures/content/chunk_bitmap_vectors.json " +
            $"walking up from {AppContext.BaseDirectory}");
    }

    private static readonly Lazy<List<ChunkBitmapVector>> _cache = new(() =>
    {
        var json = File.ReadAllText(FixturePath());
        return JsonSerializer.Deserialize<List<ChunkBitmapVector>>(json)!;
    });

    public static List<ChunkBitmapVector> Vectors => _cache.Value;
    public static IEnumerable<object[]> AllVectors =>
        Vectors.Select(v => new object[] { v.Name });
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class ChunkBitmapFixtureTests
{
    // ── Encoding: have_indices → bitset ───────────────────────────────────────

    public static IEnumerable<object[]> AllVectors => ChunkBitmapFixtureLoader.AllVectors;

    /// <summary>
    /// Encoding test: given the canonical have_indices, produce a bitset whose
    /// hex matches <c>have_bitset_hex</c> and whose Base64 matches
    /// <c>have_bitset_base64</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllVectors))]
    public void Encode_ProducesCorrectBitset(string vectorName)
    {
        var v      = ChunkBitmapFixtureLoader.Vectors.Single(x => x.Name == vectorName);
        var bitset = BitsetCodec.Encode(v.ChunkCount, v.HaveIndices);

        var actualHex    = Convert.ToHexString(bitset).ToLowerInvariant();
        var actualBase64 = Convert.ToBase64String(bitset);

        Assert.Equal(v.HaveBitsetHex,    actualHex,    StringComparer.OrdinalIgnoreCase);
        Assert.Equal(v.HaveBitsetBase64, actualBase64, StringComparer.Ordinal);
    }

    // ── Decoding: bitset → have_indices ───────────────────────────────────────

    /// <summary>
    /// Decoding test: given <c>have_bitset_base64</c>, decode to indices and
    /// verify they match the canonical <c>have_indices</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllVectors))]
    public void Decode_RecoversCorrectIndices(string vectorName)
    {
        var v         = ChunkBitmapFixtureLoader.Vectors.Single(x => x.Name == vectorName);
        var bitset    = Convert.FromBase64String(v.HaveBitsetBase64);
        var recovered = BitsetCodec.Decode(bitset, v.ChunkCount);

        Assert.Equal(v.HaveIndices.OrderBy(x => x).ToArray(),
                     recovered.OrderBy(x => x).ToArray());
    }

    // ── JSON round-trip ───────────────────────────────────────────────────────

    /// <summary>
    /// JSON round-trip test: given the fixture inputs, produce the wire JSON and
    /// assert it matches <c>expected_json</c> character-for-character.
    ///
    /// This is the canonical cross-language wire compatibility check: all eight
    /// language implementations must emit identical JSON for the same inputs.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllVectors))]
    public void JsonSerialize_MatchesExpected(string vectorName)
    {
        var v      = ChunkBitmapFixtureLoader.Vectors.Single(x => x.Name == vectorName);
        var bitset = BitsetCodec.Encode(v.ChunkCount, v.HaveIndices);
        var actual = ChunkBitmapJson.Serialize(v.RootHash, v.ChunkCount, bitset, v.Generation);

        Assert.Equal(v.ExpectedJson, actual);
    }

    // ── Bitset length invariant ───────────────────────────────────────────────

    /// <summary>
    /// Bitset length must equal ceil(chunk_count / 8) — no more, no less.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllVectors))]
    public void Encode_BitsetLengthIsCeilDiv8(string vectorName)
    {
        var v              = ChunkBitmapFixtureLoader.Vectors.Single(x => x.Name == vectorName);
        var bitset         = BitsetCodec.Encode(v.ChunkCount, v.HaveIndices);
        var expectedLength = (v.ChunkCount + 7) / 8;

        Assert.Equal(expectedLength, bitset.Length);
    }

    // ── Trailing bits must be zero ────────────────────────────────────────────

    /// <summary>
    /// Bits beyond chunk_count in the last byte must be zero after encoding.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllVectors))]
    public void Encode_TrailingBitsAreZero(string vectorName)
    {
        var v      = ChunkBitmapFixtureLoader.Vectors.Single(x => x.Name == vectorName);
        var bitset = BitsetCodec.Encode(v.ChunkCount, v.HaveIndices);

        if (bitset.Length == 0) return;   // zero-chunk content is trivially compliant

        var trailingBits = v.ChunkCount % 8; // bits used in the last byte (0 = full byte)
        if (trailingBits == 0) return;       // last byte is fully used — no trailing bits

        var lastByte    = bitset[^1];
        var validMask   = (byte)((1 << trailingBits) - 1);
        var trailingSet = lastByte & ~validMask;

        Assert.Equal(0, trailingSet);
    }
}
