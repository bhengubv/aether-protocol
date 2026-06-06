// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using AetherNet.Content.Models;
using BenchmarkDotNet.Attributes;

namespace AetherNet.Benchmarks;

/// <summary>
/// Benchmarks for the <see cref="ChunkBitmapPayload"/> bitset codec —
/// the innermost hot path of the Chunk Shuffle / Self-Assembling Peer
/// Interleaving protocol.
///
/// <para>
/// Every peer broadcasts a ChunkBitmap once per diffusion round for each
/// active content item it is seeding or leeching. Encode runs on the sender
/// side (populate HaveBitset before broadcast); Decode runs on every receiver
/// (parse the incoming bitset to decide which chunks to pull).
/// </para>
///
/// <para>
/// Three chunk-count scales are tested:
/// </para>
/// <list type="table">
///   <item><term>64 chunks</term><description>Small files (≤ 64 KB with 1 KB chunks). 8 bytes of bitset.</description></item>
///   <item><term>1024 chunks</term><description>Typical content (~1 MB with 1 KB chunks). 128 bytes.</description></item>
///   <item><term>65536 chunks</term><description>Large VOD content (64 MB with 1 KB chunks). 8 KB.</description></item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class ChunkBitmapBenchmarks
{
    // ── Setup ──────────────────────────────────────────────────────────────

    private bool[] _flags64   = null!;
    private bool[] _flags1024 = null!;
    private bool[] _flags65k  = null!;

    private byte[] _bitset64   = null!;
    private byte[] _bitset1024 = null!;
    private byte[] _bitset65k  = null!;

    [GlobalSetup]
    public void Setup()
    {
        _flags64   = MakeHalfFullFlags(64);
        _flags1024 = MakeHalfFullFlags(1024);
        _flags65k  = MakeHalfFullFlags(65_536);

        _bitset64   = ChunkBitmapPayload.Encode(_flags64);
        _bitset1024 = ChunkBitmapPayload.Encode(_flags1024);
        _bitset65k  = ChunkBitmapPayload.Encode(_flags65k);
    }

    // ── Encode benchmarks ────────────────────────────────────────────────

    /// <summary>
    /// Encode 64 chunks (half full) — small-file baseline. 8-byte output.
    /// </summary>
    [Benchmark]
    public byte[] Encode_64chunks() => ChunkBitmapPayload.Encode(_flags64);

    /// <summary>
    /// Encode 1024 chunks (half full) — typical content. 128-byte output.
    /// </summary>
    [Benchmark]
    public byte[] Encode_1024chunks() => ChunkBitmapPayload.Encode(_flags1024);

    /// <summary>
    /// Encode 65536 chunks (half full) — large VOD file. 8 KB output.
    /// </summary>
    [Benchmark]
    public byte[] Encode_65536chunks() => ChunkBitmapPayload.Encode(_flags65k);

    // ── Decode benchmarks ────────────────────────────────────────────────

    /// <summary>
    /// Decode a 64-chunk bitset back to bool[64].
    /// </summary>
    [Benchmark]
    public bool[] Decode_64chunks() => ChunkBitmapPayload.Decode(_bitset64, 64);

    /// <summary>
    /// Decode a 1024-chunk bitset back to bool[1024].
    /// </summary>
    [Benchmark]
    public bool[] Decode_1024chunks() => ChunkBitmapPayload.Decode(_bitset1024, 1024);

    /// <summary>
    /// Decode a 65536-chunk bitset back to bool[65536].
    /// </summary>
    [Benchmark]
    public bool[] Decode_65536chunks() => ChunkBitmapPayload.Decode(_bitset65k, 65_536);

    // ── HasAll benchmarks ─────────────────────────────────────────────────

    /// <summary>
    /// HasAll on a fully-seeded 1024-chunk payload — the "I am a complete
    /// seeder" fast-path check run on every received ChunkBitmap to decide
    /// whether to immediately mark the peer as a full seed.
    /// </summary>
    [Benchmark]
    public bool HasAll_1024chunks_FullSeed()
    {
        var fullSeed = new ChunkBitmapPayload
        {
            ChunkCount = 1024,
            HaveBitset = ChunkBitmapPayload.Encode(MakeFullFlags(1024)),
        };
        return fullSeed.HasAll();
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>Create a flag array where every even index is set (50% fill).</summary>
    private static bool[] MakeHalfFullFlags(int count)
    {
        var flags = new bool[count];
        for (var i = 0; i < count; i += 2)
            flags[i] = true;
        return flags;
    }

    private static bool[] MakeFullFlags(int count)
    {
        var flags = new bool[count];
        for (var i = 0; i < count; i++)
            flags[i] = true;
        return flags;
    }
}
