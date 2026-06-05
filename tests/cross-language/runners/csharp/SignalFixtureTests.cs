// SPDX-License-Identifier: MIT
// Cross-language Signal Protocol fixture verifier — C# runner.
//
// Reads fixtures/signal/inputs.json and fixtures/signal/expected/*.json,
// then exercises the raw crypto primitives (X25519, HKDF-SHA256, HMAC-SHA256)
// to verify this implementation produces bit-identical outputs for each case.
//
// The same fixture corpus is exercised by the Go, Python, TypeScript, Rust,
// Kotlin, Swift, and C runners. Any divergence here == a cross-language
// wire-break that must be fixed before shipping.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;

namespace AetherMesh.InteropTest;

// ─── Model types ──────────────────────────────────────────────────────────────

file record FixtureCase(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,

    // x3dh_basic
    [property: JsonPropertyName("alice_identity_priv_hex")] string? AliceIdentityPrivHex,
    [property: JsonPropertyName("alice_ephemeral_priv_hex")] string? AliceEphemeralPrivHex,
    [property: JsonPropertyName("bob_identity_priv_hex")] string? BobIdentityPrivHex,
    [property: JsonPropertyName("bob_signed_pre_key_priv_hex")] string? BobSignedPreKeyPrivHex,
    [property: JsonPropertyName("bob_one_time_pre_key_priv_hex")] string? BobOneTimePreKeyPrivHex,
    [property: JsonPropertyName("hkdf_root_info_utf8")] string? HkdfRootInfoUtf8,
    [property: JsonPropertyName("hkdf_chain_initiator_send_info_utf8")] string? HkdfChainSendInfoUtf8,
    [property: JsonPropertyName("hkdf_chain_initiator_recv_info_utf8")] string? HkdfChainRecvInfoUtf8,

    // ratchet_step_basic
    [property: JsonPropertyName("chain_key_hex")] string? ChainKeyHex,

    // ratchet_step_three_iterations
    [property: JsonPropertyName("initial_chain_key_hex")] string? InitialChainKeyHex,

    // kdf_rk_basic
    [property: JsonPropertyName("root_key_hex")] string? RootKeyHex,
    [property: JsonPropertyName("dh_output_hex")] string? DhOutputHex,
    [property: JsonPropertyName("hkdf_info_utf8")] string? KdfRkInfoUtf8
);

file record FixtureInputs(
    [property: JsonPropertyName("cases")] List<FixtureCase> Cases);

// ─── Helpers ──────────────────────────────────────────────────────────────────

file static class Hex
{
    public static byte[] Decode(string hex)
    {
        var n = hex.Length / 2;
        var bytes = new byte[n];
        for (var i = 0; i < n; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    public static string Encode(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}

// ─── Fixture loader ───────────────────────────────────────────────────────────

file static class FixtureLoader
{
    private static string FixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "signal", "inputs.json");
            if (File.Exists(candidate)) return Path.Combine(dir, "fixtures", "signal");
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException(
            $"Could not locate fixtures/signal/inputs.json walking up from {AppContext.BaseDirectory}");
    }

    private static readonly Lazy<(string Dir, FixtureInputs Inputs)> _cache = new(() =>
    {
        var dir = FixturesDir();
        var json = File.ReadAllText(Path.Combine(dir, "inputs.json"));
        var inputs = JsonSerializer.Deserialize<FixtureInputs>(json)!;
        return (dir, inputs);
    });

    public static string Dir => _cache.Value.Dir;
    public static List<FixtureCase> Cases => _cache.Value.Inputs.Cases;

    public static JsonDocument LoadExpected(string caseName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "expected", caseName + ".json")));

    public static IEnumerable<object[]> CasesOfType(params string[] names) =>
        Cases.Where(c => names.Contains(c.Name)).Select(c => new object[] { c.Name });
}

// ─── Tests ────────────────────────────────────────────────────────────────────

public sealed class SignalFixtureTests
{
    // ── X3DH ──────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> X3dhCases() =>
        FixtureLoader.CasesOfType("x3dh_basic");

    /// <summary>
    /// Verifies initiator-side X3DH:
    ///   DH1 = DH(IK_A, SPK_B)
    ///   DH2 = DH(EK_A, IK_B)
    ///   DH3 = DH(EK_A, SPK_B)
    ///   DH4 = DH(EK_A, OPK_B)
    ///   shared_secret = DH1 || DH2 || DH3 || DH4
    ///   root_key = HKDF-SHA256(salt=zeros32, ikm=shared_secret, info=hkdf_root_info_utf8, L=32)
    ///   send_ck  = HKDF-SHA256(salt=zeros32, ikm=root_key,      info=hkdf_chain_send_info, L=32)
    ///   recv_ck  = HKDF-SHA256(salt=zeros32, ikm=root_key,      info=hkdf_chain_recv_info, L=32)
    /// </summary>
    [Theory]
    [MemberData(nameof(X3dhCases))]
    public void X3dh_MatchesExpected(string caseName)
    {
        var tc = FixtureLoader.Cases.Single(c => c.Name == caseName);

        var aliceIkPriv = Hex.Decode(tc.AliceIdentityPrivHex!);
        var aliceEkPriv = Hex.Decode(tc.AliceEphemeralPrivHex!);
        var bobIkPriv   = Hex.Decode(tc.BobIdentityPrivHex!);
        var bobSpkPriv  = Hex.Decode(tc.BobSignedPreKeyPrivHex!);
        var bobOpkPriv  = Hex.Decode(tc.BobOneTimePreKeyPrivHex!);

        // Derive public keys from private keys (BouncyCastle handles RFC 7748 clamping).
        byte[] PubFrom(byte[] priv) =>
            new X25519PrivateKeyParameters(priv, 0).GeneratePublicKey().GetEncoded();

        var aliceIkPub = PubFrom(aliceIkPriv);
        var aliceEkPub = PubFrom(aliceEkPriv);
        var bobIkPub   = PubFrom(bobIkPriv);
        var bobSpkPub  = PubFrom(bobSpkPriv);
        var bobOpkPub  = PubFrom(bobOpkPriv);

        // X25519 ECDH helper.
        byte[] DH(byte[] localPriv, byte[] remotePub)
        {
            var priv = new X25519PrivateKeyParameters(localPriv, 0);
            var pub  = new X25519PublicKeyParameters(remotePub, 0);
            var ag   = new X25519Agreement();
            ag.Init(priv);
            var shared = new byte[ag.AgreementSize];
            ag.CalculateAgreement(pub, shared, 0);
            return shared;
        }

        var dh1 = DH(aliceIkPriv,  bobSpkPub);
        var dh2 = DH(aliceEkPriv,  bobIkPub);
        var dh3 = DH(aliceEkPriv,  bobSpkPub);
        var dh4 = DH(aliceEkPriv,  bobOpkPub);

        var sharedSecret = Concat(dh1, dh2, dh3, dh4);
        var rootInfo     = System.Text.Encoding.UTF8.GetBytes(tc.HkdfRootInfoUtf8!);
        var sendInfo     = System.Text.Encoding.UTF8.GetBytes(tc.HkdfChainSendInfoUtf8!);
        var recvInfo     = System.Text.Encoding.UTF8.GetBytes(tc.HkdfChainRecvInfoUtf8!);
        var zeros32      = new byte[32];

        var rootKey  = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt: zeros32, info: rootInfo);
        var sendCk   = HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey,      32, salt: zeros32, info: sendInfo);
        var recvCk   = HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey,      32, salt: zeros32, info: recvInfo);

        using var exp = FixtureLoader.LoadExpected(caseName);
        var root = exp.RootElement;

        Assert.Equal(root.GetProperty("alice_identity_pub_hex").GetString(),  Hex.Encode(aliceIkPub));
        Assert.Equal(root.GetProperty("alice_ephemeral_pub_hex").GetString(), Hex.Encode(aliceEkPub));
        Assert.Equal(root.GetProperty("bob_identity_pub_hex").GetString(),    Hex.Encode(bobIkPub));
        Assert.Equal(root.GetProperty("bob_signed_pre_key_pub_hex").GetString(),    Hex.Encode(bobSpkPub));
        Assert.Equal(root.GetProperty("bob_one_time_pre_key_pub_hex").GetString(),  Hex.Encode(bobOpkPub));
        Assert.Equal(root.GetProperty("dh1_hex").GetString(), Hex.Encode(dh1));
        Assert.Equal(root.GetProperty("dh2_hex").GetString(), Hex.Encode(dh2));
        Assert.Equal(root.GetProperty("dh3_hex").GetString(), Hex.Encode(dh3));
        Assert.Equal(root.GetProperty("dh4_hex").GetString(), Hex.Encode(dh4));
        Assert.Equal(root.GetProperty("shared_secret_hex").GetString(), Hex.Encode(sharedSecret));
        Assert.Equal(root.GetProperty("root_key_hex").GetString(),  Hex.Encode(rootKey));
        Assert.Equal(root.GetProperty("initiator_send_chain_key_hex").GetString(), Hex.Encode(sendCk));
        Assert.Equal(root.GetProperty("initiator_recv_chain_key_hex").GetString(), Hex.Encode(recvCk));
    }

    // ── Ratchet step (basic) ──────────────────────────────────────────────────

    public static IEnumerable<object[]> RatchetBasicCases() =>
        FixtureLoader.CasesOfType("ratchet_step_basic");

    /// <summary>
    /// Validates: messageKey     = HMAC-SHA256(chainKey, 0x01)
    ///            nextChainKey   = HMAC-SHA256(chainKey, 0x02)
    /// </summary>
    [Theory]
    [MemberData(nameof(RatchetBasicCases))]
    public void RatchetStep_MatchesExpected(string caseName)
    {
        var tc = FixtureLoader.Cases.Single(c => c.Name == caseName);
        var chainKey = Hex.Decode(tc.ChainKeyHex!);

        var msgKey       = HMACSHA256.HashData(chainKey, (byte[])[0x01]);
        var nextChainKey = HMACSHA256.HashData(chainKey, (byte[])[0x02]);

        using var exp  = FixtureLoader.LoadExpected(caseName);
        var root = exp.RootElement;
        Assert.Equal(root.GetProperty("message_key_hex").GetString(),    Hex.Encode(msgKey));
        Assert.Equal(root.GetProperty("next_chain_key_hex").GetString(), Hex.Encode(nextChainKey));
    }

    // ── Ratchet step (three iterations) ──────────────────────────────────────

    public static IEnumerable<object[]> RatchetThreeIterCases() =>
        FixtureLoader.CasesOfType("ratchet_step_three_iterations");

    [Theory]
    [MemberData(nameof(RatchetThreeIterCases))]
    public void RatchetThreeIterations_MatchesExpected(string caseName)
    {
        var tc = FixtureLoader.Cases.Single(c => c.Name == caseName);
        var ck = Hex.Decode(tc.InitialChainKeyHex!);

        using var exp = FixtureLoader.LoadExpected(caseName);
        var root = exp.RootElement;

        for (var step = 0; step < 3; step++)
        {
            var msgKey  = HMACSHA256.HashData(ck, (byte[])[0x01]);
            var nextCk  = HMACSHA256.HashData(ck, (byte[])[0x02]);

            Assert.Equal(
                root.GetProperty($"step_{step}_message_key_hex").GetString(),
                Hex.Encode(msgKey));
            Assert.Equal(
                root.GetProperty($"step_{step}_chain_key_after_hex").GetString(),
                Hex.Encode(nextCk));

            ck = nextCk;
        }
    }

    // ── KDF_RK ────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> KdfRkCases() =>
        FixtureLoader.CasesOfType("kdf_rk_basic");

    /// <summary>
    /// Validates KDF_RK (Signal §5.2):
    ///   HKDF-SHA256(salt=root_key, ikm=dh_output, info=UTF8('aether-ratchet-rk-v1'), L=64)
    ///   → new_root_key (first 32 bytes) + new_chain_key (second 32 bytes)
    /// </summary>
    [Theory]
    [MemberData(nameof(KdfRkCases))]
    public void KdfRk_MatchesExpected(string caseName)
    {
        var tc       = FixtureLoader.Cases.Single(c => c.Name == caseName);
        var rootKey  = Hex.Decode(tc.RootKeyHex!);
        var dhOutput = Hex.Decode(tc.DhOutputHex!);
        var info     = System.Text.Encoding.UTF8.GetBytes(tc.KdfRkInfoUtf8!);

        var output = new byte[64];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm: dhOutput, output: output, salt: rootKey, info: info);

        var newRootKey  = output[..32];
        var newChainKey = output[32..];

        using var exp = FixtureLoader.LoadExpected(caseName);
        var root = exp.RootElement;
        Assert.Equal(root.GetProperty("new_root_key_hex").GetString(),  Hex.Encode(newRootKey));
        Assert.Equal(root.GetProperty("new_chain_key_hex").GetString(), Hex.Encode(newChainKey));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = arrays.Sum(a => a.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var a in arrays) { Buffer.BlockCopy(a, 0, result, offset, a.Length); offset += a.Length; }
        return result;
    }
}
