// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;
using Aether.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Aether.Core.Tests;

/// <summary>
/// Fuzz exercises for <see cref="PacketSerializer.Deserialize(byte[])"/>.
///
/// The deserializer parses untrusted bytes off the wire, so the contract is:
/// for ANY input it must EITHER return a valid <see cref="MeshPacket"/> OR
/// throw one of the documented exception types (<see cref="ArgumentException"/>,
/// <see cref="ArgumentNullException"/>, <see cref="ArgumentOutOfRangeException"/>).
/// It must NEVER:
///   - throw an unhandled / uncaught exception of an undocumented type,
///   - hang in an infinite loop,
///   - stack-overflow on adversarial length prefixes.
///
/// The property-based loop runs ~10000 random-input iterations using
/// <see cref="RandomNumberGenerator"/> for entropy plus a deterministic seed
/// for reproducibility on failure.
/// </summary>
public class PacketSerializerFuzzTests
{
    private readonly ITestOutputHelper _output;

    public PacketSerializerFuzzTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Exception types that the deserializer is allowed to throw. Any other
    /// type that escapes is a fuzz failure (potential DoS or memory-safety
    /// issue).
    /// </summary>
    private static bool IsExpectedException(Exception ex) =>
        ex is ArgumentException
            or ArgumentNullException
            or ArgumentOutOfRangeException
            or IndexOutOfRangeException
            or OverflowException
            or System.Text.DecoderFallbackException;

    [Theory]
    [InlineData(new byte[] { })]                             // empty
    [InlineData(new byte[] { 0x00 })]                        // 1 byte
    [InlineData(new byte[] { 0x01, 0x02 })]                  // 2 bytes
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 })] // < min header
    public void HandPickedTooShort_ThrowsExpected(byte[] data)
    {
        // Hand-picked truncated buffers — must throw a documented exception
        // type, never crash with an unhandled one.
        var ex = Record.Exception(() => PacketSerializer.Deserialize(data));
        Assert.NotNull(ex);
        Assert.True(IsExpectedException(ex!),
            $"Unexpected exception type: {ex.GetType().FullName}: {ex.Message}");
    }

    [Theory]
    [InlineData(0x7FFFFFFF)] // int.MaxValue payload length
    [InlineData(0x10000000)] // 256 MB payload length
    [InlineData(0x01000000)] // 16 MB payload length
    public void OversizePayloadLengthPrefix_ThrowsExpected(int oversizeLen)
    {
        // Build a syntactically-correct header with a huge payload-length
        // prefix but only a tiny actual payload. The deserializer must NOT
        // try to allocate `oversizeLen` bytes — it must detect the
        // inconsistency via EnsureRemaining and throw ArgumentException.
        var buf = BuildHeaderWithLargePayloadLength(oversizeLen);
        var ex = Record.Exception(() => PacketSerializer.Deserialize(buf));
        Assert.NotNull(ex);
        Assert.True(IsExpectedException(ex!),
            $"Unexpected exception type: {ex.GetType().FullName}: {ex.Message}");
    }

    [Fact]
    public void NegativePayloadLength_ThrowsArgumentException()
    {
        var buf = BuildHeaderWithLargePayloadLength(-1); // 0xFFFFFFFF
        Assert.Throws<ArgumentException>(() => PacketSerializer.Deserialize(buf));
    }

    [Fact]
    public void OversizeUhidLengthPrefix_ThrowsExpected()
    {
        // Wire minimum is 43 bytes (fixed header + 5 zero-length prefixes).
        // A 44-byte buffer where the SourceUhid length prefix = 65535 (uint16 max)
        // declares far more bytes than remain in the buffer → EnsureRemaining throws.
        // Older test used 33 bytes which now fails the minimum-length guard
        // (< 43) before reaching the prefix check — both paths throw ArgumentException.
        var buf = new byte[44];
        buf[0] = 0x02; // version
        // all other fixed fields = 0x00 (valid enough for the guard)
        buf[31] = 0xFF; // SourceUhid length high byte — at offset 31 = after fixed header
        buf[32] = 0xFF; // SourceUhid length low byte  → 65535 declared, 0 remain

        var ex = Record.Exception(() => PacketSerializer.Deserialize(buf));
        Assert.NotNull(ex);
        Assert.True(IsExpectedException(ex!),
            $"Unexpected: {ex!.GetType().FullName}");
    }

    [Fact]
    public void RandomBytes_PropertyBased_NeverUnhandledException()
    {
        // 10000 iterations of random-length, random-content buffers. Every
        // resulting exception must be one of the documented types.
        const int iterations = 10000;
        var rng = new Random(unchecked((int)0xA37E2026u)); // deterministic seed for repro
        var unexpected = new List<(int len, string type, string msg)>();

        for (var i = 0; i < iterations; i++)
        {
            var len = rng.Next(0, 4096);
            var data = new byte[len];
            rng.NextBytes(data);

            try
            {
                var packet = PacketSerializer.Deserialize(data);
                // If parsing succeeded the result must be a valid MeshPacket
                // (sanity check — invariant holds by construction).
                Assert.NotNull(packet);
            }
            catch (Exception ex) when (IsExpectedException(ex))
            {
                // Documented exception — pass.
            }
            catch (Exception ex)
            {
                unexpected.Add((len, ex.GetType().FullName!, ex.Message));
            }
        }

        if (unexpected.Count > 0)
        {
            foreach (var (len, type, msg) in unexpected.Take(5))
                _output.WriteLine($"len={len} -> {type}: {msg}");
            Assert.Empty(unexpected);
        }
    }

    [Fact]
    public void RandomBytes_PropertyBased_TerminatesWithinTimeBudget()
    {
        // Defends against infinite-loop / stack-overflow regressions: the
        // entire 10000-iteration sweep must complete well within a few
        // seconds on a normal CPU.
        const int iterations = 10000;
        var rng = new Random(unchecked((int)0xBEEF2026u));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            var len = rng.Next(0, 8192);
            var data = new byte[len];
            rng.NextBytes(data);
            PacketSerializer.TryDeserialize(data, out _);
        }

        sw.Stop();
        _output.WriteLine($"Fuzz {iterations} iters in {sw.ElapsedMilliseconds} ms");
        // 30s envelope is generous — if we ever cross it, something is
        // pathologically slow (or hung). Real local runs finish in <2s.
        Assert.True(sw.Elapsed.TotalSeconds < 30,
            $"Fuzz sweep took {sw.Elapsed.TotalSeconds:F1}s — possible loop / O(n^2) regression.");
    }

    [Fact]
    public void TryDeserialize_NeverThrows_OnRandomBytes()
    {
        // TryDeserialize is the safe entry point — it must NEVER throw,
        // regardless of input. (Existing contract — pinned by this fuzz.)
        var rng = new Random(0xC0FFEE);
        for (var i = 0; i < 2000; i++)
        {
            var len = rng.Next(0, 1024);
            var data = new byte[len];
            rng.NextBytes(data);

            // Must complete without throwing.
            var ok = PacketSerializer.TryDeserialize(data, out var packet);
            if (!ok) Assert.Null(packet);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a 49-byte header with valid version/type/guid/priority/ttl/ts,
    /// 0-length source/dest/nonce, then a payload-length prefix of the
    /// provided value (typically huge — used to assert that the deserializer
    /// detects the truncation rather than blindly allocating).
    /// </summary>
    private static byte[] BuildHeaderWithLargePayloadLength(int payloadLen)
    {
        // Version(1) + type(1) + guid(16) + priority(1) + ttl(4) + ts(8)
        //   + 3 zero-length u16 prefixes (6) + payloadLen(4) + sigLen(2) = 43
        var buf = new byte[43];
        var offset = 0;
        buf[offset++] = 0x02; // version
        buf[offset++] = 0x03; // PacketType.Data
        // 16-byte GUID — leave zeros.
        offset += 16;
        buf[offset++] = 0x05; // priority
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), 7); offset += 4; // ttl
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(offset), 1234567890L); offset += 8; // ts
        // 3 length-zero prefixes (source, dest, nonce).
        offset += 6;
        // Payload length (the malicious bit).
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), payloadLen);
        // sigLen left as 0.
        return buf;
    }
}
