// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Security.Backup;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Verifies the BIP-39 implementation against the official Trezor English test
/// vectors (fixtures/bip39/vectors.json): entropy -> mnemonic -> seed, all three
/// columns byte-for-byte. Every AetherNet language SDK runs the same vectors, so
/// a green run here is simultaneously a correctness proof (matches the standard)
/// and a cross-language byte-identity proof (all SDKs match the same bytes).
/// </summary>
public class Bip39FixtureTests
{
    private record Vector(
        [property: JsonPropertyName("entropy")] string Entropy,
        [property: JsonPropertyName("mnemonic")] string Mnemonic,
        [property: JsonPropertyName("seed")] string Seed);

    private record VectorFile(
        [property: JsonPropertyName("passphrase")] string Passphrase,
        [property: JsonPropertyName("vectors")] List<Vector> Vectors);

    private static string Bip39Dir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "bip39", "vectors.json");
            if (File.Exists(candidate)) return Path.Combine(dir, "fixtures", "bip39");
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException("Could not locate fixtures/bip39/vectors.json from " + AppContext.BaseDirectory);
    }

    private static VectorFile Load()
    {
        var path = Path.Combine(Bip39Dir(), "vectors.json");
        return JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(path))!;
    }

    public static IEnumerable<object[]> AllVectors() =>
        Load().Vectors.Select((_, i) => new object[] { i });

    private static byte[] HexToBytes(string hex)
    {
        var n = hex.Length / 2;
        var bytes = new byte[n];
        for (var i = 0; i < n; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string BytesToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Theory]
    [MemberData(nameof(AllVectors))]
    public void EntropyToMnemonic_MatchesVector(int index)
    {
        var f = Load();
        var v = f.Vectors[index];
        Assert.Equal(v.Mnemonic, Bip39Mnemonic.EntropyToMnemonic(HexToBytes(v.Entropy)));
    }

    [Theory]
    [MemberData(nameof(AllVectors))]
    public void MnemonicToEntropy_MatchesVector(int index)
    {
        var f = Load();
        var v = f.Vectors[index];
        Assert.Equal(v.Entropy, BytesToHex(Bip39Mnemonic.MnemonicToEntropy(v.Mnemonic)));
    }

    [Theory]
    [MemberData(nameof(AllVectors))]
    public void MnemonicToSeed_MatchesVector(int index)
    {
        var f = Load();
        var v = f.Vectors[index];
        Assert.Equal(v.Seed, BytesToHex(Bip39Mnemonic.MnemonicToSeed(v.Mnemonic, f.Passphrase)));
    }

    [Fact]
    public void Wordlist_Is2048Words_AbandonToZoo()
    {
        Assert.Equal(2048, Bip39Wordlist.Words.Count);
        Assert.Equal("abandon", Bip39Wordlist.Words[0]);
        Assert.Equal("zoo", Bip39Wordlist.Words[2047]);
    }
}
