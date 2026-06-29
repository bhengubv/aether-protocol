// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AetherNet.Identity;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Drives the C# reference <see cref="PeerId"/> derivation through the cross-language corpus at
/// <c>fixtures/peerid/</c>. Every AetherNet SDK derives the SAME PeerID for the same Ed25519
/// public key, and the expected values are real <c>js-libp2p</c> output — so passing here proves the
/// derivation is both cross-language byte-identical AND interoperable with the real libp2p network.
/// </summary>
public class PeerIdFixtureTests
{
    private static string FixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures", "peerid");
            if (File.Exists(Path.Combine(candidate, "inputs.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("fixtures/peerid/inputs.json not found walking up from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void FromEd25519PublicKey_MatchesRealLibp2pPeerIds()
    {
        var dir = FixturesDir();
        var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "inputs.json")))
            .RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(inputs);

        foreach (var input in inputs)
        {
            var name = input.GetProperty("name").GetString()!;
            var pub = Convert.FromHexString(input.GetProperty("pubkey_hex").GetString()!);
            var expected = File.ReadAllText(Path.Combine(dir, "expected", name + ".txt")).Trim();

            var actual = PeerId.FromEd25519PublicKey(pub);

            Assert.Equal(expected, actual);
            Assert.StartsWith("12D3Koo", actual); // Ed25519 PeerIDs always render this way
        }
    }

    [Fact]
    public void FromEd25519PublicKey_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => PeerId.FromEd25519PublicKey(new byte[31]));
        Assert.Throws<ArgumentException>(() => PeerId.FromEd25519PublicKey(new byte[33]));
        Assert.Throws<ArgumentNullException>(() => PeerId.FromEd25519PublicKey(null!));
    }
}
