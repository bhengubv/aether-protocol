// SPDX-License-Identifier: MIT

using System.IO;
using System.Linq;
using System.Text.Json;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Drives the C# reference P-256 ECDSA verify path through the cross-language
/// fixture at <c>tests/cross-language/p256-fixtures.json</c>. Every AetherNet SDK
/// drives the SAME vectors — DER SubjectPublicKeyInfo public key + DER ECDSA
/// signature + SHA-256, per PROTOCOL_SPEC.md §7.5 — and MUST accept every
/// <c>valid:true</c> vector and reject every <c>valid:false</c> vector. This is the
/// oracle that proves the legacy P-256 migration fallback is real and byte-identical
/// across all 8 languages.
/// </summary>
public class P256VerifyFixtureTests
{
    private static readonly JsonElement Root = LoadFixture();

    private static JsonElement LoadFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "cross-language", "p256-fixtures.json");
            if (File.Exists(candidate))
                return JsonDocument.Parse(File.ReadAllText(candidate)).RootElement.Clone();
            dir = dir.Parent;
        }
        throw new FileNotFoundException("p256-fixtures.json not found walking up from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void VerifyWithFallback_DrivesEveryP256Vector()
    {
        var vectors = Root.GetProperty("vectors").EnumerateArray().ToList();
        Assert.NotEmpty(vectors);

        foreach (var v in vectors)
        {
            var name = v.GetProperty("name").GetString()!;
            var pub = Convert.FromHexString(v.GetProperty("public_key_der").GetString()!);
            var msg = Convert.FromHexString(v.GetProperty("message").GetString()!);
            var sig = Convert.FromHexString(v.GetProperty("signature_der").GetString()!);
            var expected = v.GetProperty("valid").GetBoolean();

            // A >32-byte key forces the P-256 ECDSA branch of VerifyWithFallback;
            // if that branch regressed to "return false", the valid vector fails here.
            Assert.True(pub.Length > 32, $"{name}: P-256 SPKI key must be > 32 bytes");
            Assert.Equal(expected, Ed25519SigningService.VerifyWithFallback(pub, msg, sig));
        }
    }
}
