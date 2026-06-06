// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherNet.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace AetherNet.Core.Tests;

/// <summary>
/// Fuzz exercises for <see cref="EncryptedPayloadCodec.Deserialize"/>.
///
/// The codec parses an untrusted JSON envelope off the wire (Base64-encoded
/// ciphertext + nonce + metadata). Contract: for ANY input the codec must
/// EITHER return a valid <see cref="AetherNet.Security.Models.EncryptedPayload"/>
/// OR throw one of the documented exception types. It must NEVER:
///   - throw an unhandled / uncaught exception of an undocumented type,
///   - hang in an infinite loop,
///   - stack-overflow on adversarial JSON (e.g. deeply nested arrays).
///
/// The property-based loop runs ~10000 iterations of random bytes and
/// random-but-malformed JSON.
/// </summary>
public class EncryptedPayloadCodecFuzzTests
{
    private readonly ITestOutputHelper _output;

    public EncryptedPayloadCodecFuzzTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Exception types the codec is allowed to throw on malformed input.
    /// Anything else escaping is a fuzz failure.
    /// </summary>
    private static bool IsExpectedException(Exception ex) =>
        ex is JsonException
            or ArgumentException                        // FromBase64String of bad chars
            or ArgumentNullException
            or FormatException                          // Convert.FromBase64String length/char errors
            or KeyNotFoundException                     // GetProperty on missing required key
            or InvalidOperationException                // GetInt32/GetString on wrong-kind elem
            or OverflowException                        // GetInt32 out-of-range number
            or System.Text.DecoderFallbackException;    // Encoding for raw byte arrays

    [Theory]
    [InlineData(new byte[] { })]                                    // empty
    [InlineData(new byte[] { 0x00 })]                               // single null byte
    [InlineData(new byte[] { (byte)'{', (byte)'}' })]               // empty json object
    [InlineData(new byte[] { (byte)'[' })]                          // truncated array
    [InlineData(new byte[] { (byte)'n', (byte)'u', (byte)'l', (byte)'l' })] // bare null
    public void HandPickedShortJson_ThrowsExpected(byte[] data)
    {
        var ex = Record.Exception(() => EncryptedPayloadCodec.Deserialize(data));
        Assert.NotNull(ex);
        Assert.True(IsExpectedException(ex!),
            $"Unexpected exception type: {ex!.GetType().FullName}: {ex.Message}");
    }

    [Theory]
    [InlineData("{\"c\":\"!!!not-valid-base64!!!\",\"n\":\"\",\"t\":0,\"s\":\"x\",\"k\":0}")]
    [InlineData("{\"c\":\"\",\"n\":\"\",\"t\":\"not-an-int\",\"s\":\"x\",\"k\":0}")]
    [InlineData("{\"c\":\"\",\"n\":\"\",\"t\":99999999999999999999,\"s\":\"x\",\"k\":0}")] // overflow
    [InlineData("{\"c\":[],\"n\":\"\",\"t\":0,\"s\":\"x\",\"k\":0}")] // wrong type
    [InlineData("{\"c\":\"AAAA\"}")] // missing required keys (n, t, s, k)
    public void MalformedButValidJson_ThrowsExpected(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var ex = Record.Exception(() => EncryptedPayloadCodec.Deserialize(bytes));
        Assert.NotNull(ex);
        Assert.True(IsExpectedException(ex!),
            $"Unexpected exception type: {ex!.GetType().FullName}: {ex.Message}");
    }

    [Fact]
    public void DeeplyNestedJson_TerminatesWithDocumentedException()
    {
        // System.Text.Json caps nesting depth (default 64) and throws
        // JsonException — defends against attacker-controlled stack
        // overflow via deep nesting.
        var depth = 1000;
        var sb = new StringBuilder();
        sb.Append('[', depth);
        sb.Append(']', depth);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        var ex = Record.Exception(() => EncryptedPayloadCodec.Deserialize(bytes));
        Assert.NotNull(ex);
        Assert.True(IsExpectedException(ex!),
            $"Unexpected: {ex!.GetType().FullName}");
    }

    [Fact]
    public void RandomBytes_PropertyBased_NeverUnhandledException()
    {
        const int iterations = 10000;
        var rng = new Random(unchecked((int)0xFEEDFACEu));
        var unexpected = new List<(int len, string type, string msg)>();

        for (var i = 0; i < iterations; i++)
        {
            var len = rng.Next(0, 4096);
            var data = new byte[len];
            rng.NextBytes(data);

            try
            {
                EncryptedPayloadCodec.Deserialize(data);
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
    public void MutatedValidEnvelopes_NeverUnhandledException()
    {
        // Produce a valid envelope, then mutate single random bytes — a
        // common fuzz strategy that exercises edge cases the wholly-random
        // pass tends to skip (semi-valid JSON, mostly-correct base64, etc.).
        const int iterations = 5000;
        var rng = new Random(unchecked((int)0xCAFE2026u));
        var unexpected = new List<(string type, string msg)>();

        var validJson = "{\"c\":\"AQID\",\"n\":\"BAUG\",\"t\":1,\"s\":\"alice-uhid\"," +
                        "\"k\":42,\"ik\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\"," +
                        "\"ek\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\"," +
                        "\"re\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\"," +
                        "\"pn\":3,\"spki\":7,\"opki\":9}";
        var validBytes = Encoding.UTF8.GetBytes(validJson);

        for (var i = 0; i < iterations; i++)
        {
            var data = (byte[])validBytes.Clone();
            // Mutate 1..3 random positions.
            var mutations = rng.Next(1, 4);
            for (var m = 0; m < mutations; m++)
            {
                var pos = rng.Next(data.Length);
                data[pos] = (byte)rng.Next(256);
            }

            try
            {
                EncryptedPayloadCodec.Deserialize(data);
            }
            catch (Exception ex) when (IsExpectedException(ex))
            {
                // Documented.
            }
            catch (Exception ex)
            {
                unexpected.Add((ex.GetType().FullName!, ex.Message));
            }
        }

        if (unexpected.Count > 0)
        {
            foreach (var (type, msg) in unexpected.Take(5))
                _output.WriteLine($"{type}: {msg}");
            Assert.Empty(unexpected);
        }
    }

    [Fact]
    public void TerminatesWithinTimeBudget_OnLargeRandomInputs()
    {
        // Defends against pathological large-input regressions (e.g. an
        // accidental O(n^2) parser change).
        const int iterations = 1000;
        var rng = new Random(unchecked((int)0xD00D2026u));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            // Up to 64KB of random JSON-shaped garbage.
            var len = rng.Next(0, 65536);
            var data = new byte[len];
            rng.NextBytes(data);
            try { EncryptedPayloadCodec.Deserialize(data); } catch { /* expected */ }
        }

        sw.Stop();
        _output.WriteLine($"Fuzz {iterations} large iters in {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed.TotalSeconds < 30,
            $"Codec fuzz sweep took {sw.Elapsed.TotalSeconds:F1}s — possible regression.");
    }
}
