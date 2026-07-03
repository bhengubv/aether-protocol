// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Security.Privacy;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Verifies the panic-wipe core against the shared parity fixture
/// (fixtures/panicwipe/vectors.json): the duress-PIN hash (SHA-256) and the
/// identity-key manifest are byte-identical across every AetherNet SDK, and the
/// in-memory secure erase leaves no plaintext secret behind.
/// </summary>
public class PanicWipeFixtureTests
{
    private record PinHash(
        [property: JsonPropertyName("pin")] string Pin,
        [property: JsonPropertyName("sha256")] string Sha256);

    private record NameEx(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("expected")] string Expected);

    private record VectorFile(
        [property: JsonPropertyName("max_prekeys")] int MaxPrekeys,
        [property: JsonPropertyName("identity_key_names")] List<string> IdentityKeyNames,
        [property: JsonPropertyName("prekey_name")] NameEx PreKeyName,
        [property: JsonPropertyName("signed_prekey_name")] NameEx SignedPreKeyName,
        [property: JsonPropertyName("duress_pin_hashes")] List<PinHash> DuressPinHashes);

    private static string Dir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var c = Path.Combine(dir, "fixtures", "panicwipe", "vectors.json");
            if (File.Exists(c)) return Path.Combine(dir, "fixtures", "panicwipe");
            var p = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (p is null || p == dir) break;
            dir = p;
        }
        throw new FileNotFoundException("Could not locate fixtures/panicwipe/vectors.json from " + AppContext.BaseDirectory);
    }

    private static VectorFile Load() =>
        JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(Path.Combine(Dir(), "vectors.json")))!;

    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void DuressPinHash_MatchesVectors_AndVerifies()
    {
        foreach (var v in Load().DuressPinHashes)
        {
            var hash = PanicWipe.DuressPinHash(v.Pin);
            Assert.Equal(v.Sha256, ToHex(hash));
            Assert.True(PanicWipe.VerifyDuressPin(v.Pin, hash));
            Assert.False(PanicWipe.VerifyDuressPin(v.Pin + "x", hash));
        }
    }

    [Fact]
    public void IdentityKeyManifest_MatchesVectors()
    {
        var f = Load();
        Assert.Equal(f.IdentityKeyNames, PanicWipe.IdentityKeyNames);
        Assert.Equal(f.MaxPrekeys, PanicWipe.MaxPreKeys);
        Assert.Equal(f.PreKeyName.Expected, PanicWipe.PreKeyName(f.PreKeyName.Index));
        Assert.Equal(f.SignedPreKeyName.Expected, PanicWipe.SignedPreKeyName(f.SignedPreKeyName.Index));
    }

    [Fact]
    public void SecureErase_ZeroesTheBuffer()
    {
        var secret = new byte[64];
        for (var i = 0; i < secret.Length; i++) secret[i] = (byte)(i + 1);
        PanicWipe.SecureErase(secret);
        Assert.All(secret, b => Assert.Equal(0, b));
    }

    [Fact]
    public void VerifyDuressPin_WrongHashLength_Rejected()
    {
        Assert.False(PanicWipe.VerifyDuressPin("1234", new byte[16]));
        Assert.False(PanicWipe.VerifyDuressPin("1234", System.Array.Empty<byte>()));
    }
}
