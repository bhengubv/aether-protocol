// SPDX-License-Identifier: MIT
//
// Verifies that the C# Signal-Protocol crypto produces the same byte-level
// outputs that fixtures/signal/expected/*.json record. These same fixtures
// are consumed by every other language implementation, so any drift between
// languages surfaces as a failing test in whichever language diverges.
//
// To regenerate the fixtures (i.e. you intentionally changed the protocol),
// set the env var AETHER_SIGNAL_FIXTURES_REGEN=1 — the test prints expected
// values to stdout instead of asserting. Copy the printed values into the
// committed fixtures/signal/expected/*.json files, then re-verify every
// language. Any change is a wire-break event.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherMesh.Security.Services;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;
using Xunit.Abstractions;

namespace AetherMesh.Core.Tests;

public class SignalFixtureTests
{
    private readonly ITestOutputHelper _output;
    private static bool RegenerateMode =>
        Environment.GetEnvironmentVariable("AETHER_SIGNAL_FIXTURES_REGEN") == "1";

    public SignalFixtureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void X3DH_Basic_MatchesFixture()
    {
        var (inputs, expected) = LoadFixture("x3dh_basic");

        var aliceIkPriv = HexToBytes(inputs.GetProperty("alice_identity_priv_hex").GetString()!);
        var aliceEkPriv = HexToBytes(inputs.GetProperty("alice_ephemeral_priv_hex").GetString()!);
        var bobIkPriv = HexToBytes(inputs.GetProperty("bob_identity_priv_hex").GetString()!);
        var bobSpkPriv = HexToBytes(inputs.GetProperty("bob_signed_pre_key_priv_hex").GetString()!);
        var bobOpkPriv = HexToBytes(inputs.GetProperty("bob_one_time_pre_key_priv_hex").GetString()!);

        var aliceIkPub = X25519DerivePublic(aliceIkPriv);
        var aliceEkPub = X25519DerivePublic(aliceEkPriv);
        var bobIkPub = X25519DerivePublic(bobIkPriv);
        var bobSpkPub = X25519DerivePublic(bobSpkPriv);
        var bobOpkPub = X25519DerivePublic(bobOpkPriv);

        var dh1 = X25519AgreeViaService(aliceIkPriv, bobSpkPub);
        var dh2 = X25519AgreeViaService(aliceEkPriv, bobIkPub);
        var dh3 = X25519AgreeViaService(aliceEkPriv, bobSpkPub);
        var dh4 = X25519AgreeViaService(aliceEkPriv, bobOpkPub);

        var sharedSecret = Concat(dh1, dh2, dh3, dh4);
        var rootInfo = Encoding.UTF8.GetBytes(inputs.GetProperty("hkdf_root_info_utf8").GetString()!);
        var sendInfo = Encoding.UTF8.GetBytes(inputs.GetProperty("hkdf_chain_initiator_send_info_utf8").GetString()!);
        var recvInfo = Encoding.UTF8.GetBytes(inputs.GetProperty("hkdf_chain_initiator_recv_info_utf8").GetString()!);

        var rootKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, info: rootInfo);
        var sendChainKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey, 32, info: sendInfo);
        var recvChainKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey, 32, info: recvInfo);

        var actuals = new Dictionary<string, byte[]>
        {
            ["alice_identity_pub_hex"] = aliceIkPub,
            ["alice_ephemeral_pub_hex"] = aliceEkPub,
            ["bob_identity_pub_hex"] = bobIkPub,
            ["bob_signed_pre_key_pub_hex"] = bobSpkPub,
            ["bob_one_time_pre_key_pub_hex"] = bobOpkPub,
            ["dh1_hex"] = dh1,
            ["dh2_hex"] = dh2,
            ["dh3_hex"] = dh3,
            ["dh4_hex"] = dh4,
            ["shared_secret_hex"] = sharedSecret,
            ["root_key_hex"] = rootKey,
            ["initiator_send_chain_key_hex"] = sendChainKey,
            ["initiator_recv_chain_key_hex"] = recvChainKey,
        };

        VerifyOrPrint("x3dh_basic", expected, actuals);
    }

    [Fact]
    public void RatchetStep_Basic_MatchesFixture()
    {
        var (inputs, expected) = LoadFixture("ratchet_step_basic");
        var chainKey = HexToBytes(inputs.GetProperty("chain_key_hex").GetString()!);

        var msgKey = HmacSha256(chainKey, 0x01);
        var nextChainKey = HmacSha256(chainKey, 0x02);

        VerifyOrPrint("ratchet_step_basic", expected, new()
        {
            ["message_key_hex"] = msgKey,
            ["next_chain_key_hex"] = nextChainKey,
        });
    }

    [Fact]
    public void RatchetStep_ThreeIterations_MatchesFixture()
    {
        var (inputs, expected) = LoadFixture("ratchet_step_three_iterations");
        var chainKey = HexToBytes(inputs.GetProperty("initial_chain_key_hex").GetString()!);

        var actuals = new Dictionary<string, byte[]>();
        for (var i = 0; i < 3; i++)
        {
            var msgKey = HmacSha256(chainKey, 0x01);
            var next = HmacSha256(chainKey, 0x02);
            actuals[$"step_{i}_message_key_hex"] = msgKey;
            actuals[$"step_{i}_chain_key_after_hex"] = next;
            chainKey = next;
        }

        VerifyOrPrint("ratchet_step_three_iterations", expected, actuals);
    }

    private void VerifyOrPrint(string caseName, JsonElement expected, Dictionary<string, byte[]> actuals)
    {
        if (RegenerateMode)
        {
            _output.WriteLine($"=== {caseName} (regen mode) ===");
            foreach (var kv in actuals)
                _output.WriteLine($"  \"{kv.Key}\": \"{Hex(kv.Value)}\",");
            return;
        }

        foreach (var kv in actuals)
        {
            if (!expected.TryGetProperty(kv.Key, out var elem))
                Assert.Fail($"[{caseName}] expected fixture missing field '{kv.Key}'.");
            var expectedHex = elem.GetString();
            var actualHex = Hex(kv.Value);
            Assert.True(
                string.Equals(expectedHex, actualHex, StringComparison.Ordinal),
                $"[{caseName}] {kv.Key} mismatch:\n  expected: {expectedHex}\n  actual:   {actualHex}");
        }
    }

    private static (JsonElement Inputs, JsonElement Expected) LoadFixture(string caseName)
    {
        var inputsPath = ResolveFixturePath("inputs.json");
        var expectedPath = ResolveFixturePath(Path.Combine("expected", $"{caseName}.json"));

        var inputsDoc = JsonDocument.Parse(File.ReadAllBytes(inputsPath));
        JsonElement caseElem = default;
        var found = false;
        foreach (var c in inputsDoc.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (c.GetProperty("name").GetString() == caseName)
            {
                caseElem = c.Clone();
                found = true;
                break;
            }
        }
        if (!found)
            throw new InvalidOperationException($"Case '{caseName}' not in inputs.json.");

        var expectedDoc = JsonDocument.Parse(File.ReadAllBytes(expectedPath));
        return (caseElem, expectedDoc.RootElement.Clone());
    }

    private static byte[] HmacSha256(byte[] key, byte input) =>
        HMACSHA256.HashData(key, new byte[] { input });

    private static byte[] X25519AgreeViaService(byte[] priv, byte[] pub)
    {
        var t = typeof(SignalProtocolService).Assembly
            .GetType("AetherMesh.Security.Services.X25519Service")
            ?? throw new InvalidOperationException("X25519Service type not found.");
        var method = t.GetMethod("Agree", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("X25519Service.Agree not found.");
        return (byte[])method.Invoke(null, new object[] { priv, pub })!;
    }

    private static byte[] X25519DerivePublic(byte[] priv)
    {
        var p = new X25519PrivateKeyParameters(priv, 0);
        return p.GeneratePublicKey().GetEncoded();
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = 0;
        foreach (var a in arrays) total += a.Length;
        var result = new byte[total];
        var offset = 0;
        foreach (var a in arrays)
        {
            Buffer.BlockCopy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }
        return result;
    }

    private static string ResolveFixturePath(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AetherProtocol.slnx")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("Repo root not found from test assembly.");
        return Path.Combine(dir.FullName, "fixtures", "signal", relativePath);
    }
}
