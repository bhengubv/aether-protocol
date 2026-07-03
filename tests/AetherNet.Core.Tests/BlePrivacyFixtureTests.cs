// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Security.Privacy;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Verifies the BLE tracking-protection primitives against the shared parity
/// fixture (fixtures/bleprivacy/vectors.json): the rotating Service UUID and the
/// IRK-based Resolvable Private Address are deterministic for a given key and
/// time window, so every AetherNet SDK reproduces the same bytes — a passive
/// scanner cannot link a node across windows, but a peer holding the IRK can.
/// </summary>
public class BlePrivacyFixtureTests
{
    private record UuidVector(
        [property: JsonPropertyName("window")] long Window,
        [property: JsonPropertyName("uuid")] string Uuid);

    private record RpaVector(
        [property: JsonPropertyName("window")] long Window,
        [property: JsonPropertyName("rpa")] string Rpa);

    private record VectorFile(
        [property: JsonPropertyName("rotation_seconds")] int RotationSeconds,
        [property: JsonPropertyName("rotation_key")] string RotationKey,
        [property: JsonPropertyName("irk")] string Irk,
        [property: JsonPropertyName("wrong_irk")] string WrongIrk,
        [property: JsonPropertyName("uuid_vectors")] List<UuidVector> UuidVectors,
        [property: JsonPropertyName("rpa_vectors")] List<RpaVector> RpaVectors);

    private static string Dir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var c = Path.Combine(dir, "fixtures", "bleprivacy", "vectors.json");
            if (File.Exists(c)) return Path.Combine(dir, "fixtures", "bleprivacy");
            var p = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (p is null || p == dir) break;
            dir = p;
        }
        throw new FileNotFoundException("Could not locate fixtures/bleprivacy/vectors.json from " + AppContext.BaseDirectory);
    }

    private static VectorFile Load() =>
        JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(Path.Combine(Dir(), "vectors.json")))!;

    private static byte[] FromHex(string h) => Convert.FromHexString(h);
    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void ServiceUuid_MatchesVectors()
    {
        var f = Load();
        var key = FromHex(f.RotationKey);
        foreach (var v in f.UuidVectors)
            Assert.Equal(v.Uuid, BlePrivacy.ServiceUuid(key, v.Window));
    }

    [Fact]
    public void ResolvableAddress_MatchesVectors_AndResolves()
    {
        var f = Load();
        var irk = FromHex(f.Irk);
        var wrongIrk = FromHex(f.WrongIrk);
        foreach (var v in f.RpaVectors)
        {
            var rpa = BlePrivacy.ResolvableAddress(irk, v.Window);
            Assert.Equal(v.Rpa, ToHex(rpa));
            Assert.True(BlePrivacy.ResolveAddress(irk, rpa));
            Assert.False(BlePrivacy.ResolveAddress(wrongIrk, rpa));
        }
    }

    [Fact]
    public void RotationConstant_And_Window()
    {
        var f = Load();
        Assert.Equal(f.RotationSeconds, BlePrivacy.RotationSeconds);
        Assert.Equal(0, BlePrivacy.WindowFor(899));
        Assert.Equal(1, BlePrivacy.WindowFor(900));
        Assert.Equal(1, BlePrivacy.WindowFor(1799));
    }

    [Fact]
    public void Irk_WrongLength_Rejected()
    {
        Assert.Throws<ArgumentException>(() => BlePrivacy.ResolvableAddress(new byte[15], 0));
        Assert.False(BlePrivacy.ResolveAddress(new byte[15], new byte[6]));
        Assert.False(BlePrivacy.ResolveAddress(new byte[16], new byte[5]));
    }
}
