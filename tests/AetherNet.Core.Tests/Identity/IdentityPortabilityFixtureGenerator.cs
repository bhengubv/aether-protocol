// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherNet.Identity;
using AetherNet.Security.Backup;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Core.Tests.Identity;

/// <summary>
/// One-shot generator for the cross-language identity-portability parity fixture. Run explicitly:
///   dotnet test --filter "FullyQualifiedName~IdentityPortabilityFixtureGenerator"
/// Writes <c>fixtures/identity/portability.json</c> from the C# reference — the single source of truth
/// every language port (Go/Python/Rust/Swift/Kotlin/TS/C) must reproduce byte-for-byte: a 32-byte Ed25519
/// seed → its public key, its <see cref="AetherNetTag"/>, and its 24-word BIP-39 recovery phrase.
///
/// <para>
/// Deterministic: re-running writes the identical file, so it is safe to leave in the suite. The seeds are
/// fixed and recognisable so a reviewer can eyeball the vectors.
/// </para>
/// </summary>
public class IdentityPortabilityFixtureGenerator
{
    [Fact]
    public void Generate()
    {
        static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
        static byte[] Repeat(byte value) { var a = new byte[32]; Array.Fill(a, value); return a; }
        static byte[] Sequential() { var a = new byte[32]; for (var i = 0; i < 32; i++) a[i] = (byte)i; return a; }

        byte[][] seeds =
        {
            Sequential(),
            Repeat(0x00),
            Repeat(0xff),
            Repeat(0x11),
            SHA256.HashData(Encoding.ASCII.GetBytes("aethernet-identity-parity-v1")),
        };

        var vectors = new List<object>();
        foreach (var seed in seeds)
        {
            var publicKey = Ed25519SigningService.DerivePublicKey(seed);
            vectors.Add(new
            {
                seed = Hex(seed),
                public_key = Hex(publicKey),
                tag = AetherNetTag.FromPublicKey(publicKey).Value,
                recovery_phrase = Bip39Mnemonic.EntropyToMnemonic(seed),
            });
        }

        var doc = new
        {
            note = "Canonical AetherNet identity-portability vectors from the C# reference. Every language "
                 + "port MUST reproduce these byte-for-byte: seed (32-byte Ed25519) -> public_key, tag, and "
                 + "24-word BIP-39 recovery_phrase.",
            vectors,
        };

        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.Combine(FixturesRoot(), "identity");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "portability.json"), json);
    }

    private static string FixturesRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            var candidate = Path.Combine(dir, "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        throw new DirectoryNotFoundException("Could not locate the fixtures/ directory from " + AppContext.BaseDirectory);
    }
}
