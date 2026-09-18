// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AetherNet.Identity;
using AetherNet.Security.Backup;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Core.Tests.Identity;

/// <summary>
/// Moving a device's identity — adopt a known seed, export it to a recovery phrase, restore from one.
///
/// <para>
/// The portability half of the node identity. Everyday callers never touch a key; these operations do,
/// which is why they are a separate, deliberately-reached surface. Every route reproduces the SAME tag,
/// because the tag is a function of the key and the key moves unchanged — that is what makes one person
/// one identity across devices and apps, instead of a different node per install.
/// </para>
/// </summary>
public class NodeIdentityRecoveryTests
{
    /// <summary>The one place a device keeps its identity; mirrors the real, platform-gated store.</summary>
    private sealed class FakeStore : INodeIdentityStore
    {
        private byte[]? _privateKey;
        public bool Locked { get; set; }
        public int Writes { get; private set; }
        public bool Exists => _privateKey is not null;

        public byte[]? Load()
        {
            if (_privateKey is null) return null;
            if (Locked) throw new NodeIdentityUnavailableException("The device is locked.");
            return _privateKey;
        }

        public void Save(byte[] privateKey)
        {
            _privateKey = privateKey;
            Writes++;
        }
    }

    private static byte[] Seed(byte fill)
    {
        var s = new byte[32];
        Array.Fill(s, fill);
        return s;
    }

    // ── Adopt a seed ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AdoptSeedAsync_installs_the_seed_and_returns_the_tag_it_derives()
    {
        var store = new FakeStore();
        var seed = Seed(0x42);

        var tag = await new NodeIdentityRecovery(store).AdoptSeedAsync(seed);

        Assert.Equal(AetherNetTag.FromPublicKey(Ed25519SigningService.DerivePublicKey(seed)), tag);
        Assert.Equal(1, store.Writes);
    }

    /// <summary>Adopting a seed IS the device's identity — the everyday node then reports the same tag.</summary>
    [Fact]
    public async Task An_adopted_seed_becomes_the_identity_the_node_serves()
    {
        var store = new FakeStore();
        var seed = Seed(0x42);

        var adopted = await new NodeIdentityRecovery(store).AdoptSeedAsync(seed);
        var served = await new NodeIdentity(store).GetOrMintAsync();

        Assert.Equal(adopted, served);
    }

    /// <summary>Cross-device portability: the same seed on two devices is the same node.</summary>
    [Fact]
    public async Task Adopting_the_same_seed_on_two_devices_makes_them_one_node()
    {
        var seed = Seed(0x7e);

        var a = await new NodeIdentityRecovery(new FakeStore()).AdoptSeedAsync(seed);
        var b = await new NodeIdentityRecovery(new FakeStore()).AdoptSeedAsync(seed);

        Assert.Equal(a, b);
    }

    [Fact]
    public async Task AdoptSeedAsync_refuses_to_overwrite_a_live_identity()
    {
        var store = new FakeStore();
        await new NodeIdentity(store).GetOrMintAsync();          // device already has one
        var writesBefore = store.Writes;

        await Assert.ThrowsAsync<IdentityAlreadyExistsException>(
            async () => await new NodeIdentityRecovery(store).AdoptSeedAsync(Seed(0x01)));
        Assert.Equal(writesBefore, store.Writes);
    }

    /// <summary>A sealed (locked) identity is still an identity — adoption must not clobber it either.</summary>
    [Fact]
    public async Task AdoptSeedAsync_refuses_over_a_sealed_identity()
    {
        var store = new FakeStore();
        await new NodeIdentity(store).GetOrMintAsync();
        store.Locked = true;

        await Assert.ThrowsAsync<IdentityAlreadyExistsException>(
            async () => await new NodeIdentityRecovery(store).AdoptSeedAsync(Seed(0x01)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public async Task AdoptSeedAsync_refuses_a_seed_that_is_not_32_bytes(int length)
    {
        var recovery = new NodeIdentityRecovery(new FakeStore());

        await Assert.ThrowsAsync<ArgumentException>(async () => await recovery.AdoptSeedAsync(new byte[length]));
    }

    [Fact]
    public async Task AdoptSeedAsync_refuses_a_null_seed()
    {
        var recovery = new NodeIdentityRecovery(new FakeStore());

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await recovery.AdoptSeedAsync(null!));
    }

    /// <summary>The caller keeps ownership of the array it passed; clearing it must not disturb the store.</summary>
    [Fact]
    public async Task AdoptSeedAsync_keeps_its_own_copy_of_the_seed()
    {
        var store = new FakeStore();
        var seed = Seed(0x5a);
        var expected = await new NodeIdentityRecovery(store).AdoptSeedAsync(seed);

        Array.Clear(seed);   // caller wipes its copy

        Assert.Equal(expected, await new NodeIdentity(store).GetOrMintAsync());
    }

    // ── Export → restore round-trip ───────────────────────────────────────────

    [Fact]
    public async Task Export_then_restore_on_a_fresh_device_reproduces_the_same_tag()
    {
        var deviceA = new FakeStore();
        var original = await new NodeIdentity(deviceA).GetOrMintAsync();

        var phrase = await new NodeIdentityRecovery(deviceA).ExportRecoveryPhraseAsync();
        var restored = await new NodeIdentityRecovery(new FakeStore()).RestoreFromPhraseAsync(phrase);

        Assert.Equal(original, restored);
    }

    [Fact]
    public async Task An_exported_phrase_is_twenty_four_words()
    {
        var store = new FakeStore();
        await new NodeIdentity(store).GetOrMintAsync();

        var phrase = await new NodeIdentityRecovery(store).ExportRecoveryPhraseAsync();

        Assert.Equal(24, phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.True(Bip39Mnemonic.IsValid(phrase));
    }

    [Fact]
    public async Task ExportRecoveryPhraseAsync_refuses_when_there_is_no_identity()
    {
        var recovery = new NodeIdentityRecovery(new FakeStore());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await recovery.ExportRecoveryPhraseAsync());
    }

    [Fact]
    public async Task ExportRecoveryPhraseAsync_reports_a_locked_device_as_unavailable()
    {
        var store = new FakeStore();
        await new NodeIdentity(store).GetOrMintAsync();
        store.Locked = true;

        await Assert.ThrowsAsync<NodeIdentityUnavailableException>(
            async () => await new NodeIdentityRecovery(store).ExportRecoveryPhraseAsync());
    }

    [Fact]
    public async Task RestoreFromPhraseAsync_refuses_a_mistyped_phrase()
    {
        // A real 24-word phrase with one word changed so the checksum fails.
        var store = new FakeStore();
        await new NodeIdentity(store).GetOrMintAsync();
        var good = await new NodeIdentityRecovery(store).ExportRecoveryPhraseAsync();
        var words = good.Split(' ');
        words[^1] = words[^1] == "zoo" ? "abandon" : "zoo";
        var mistyped = string.Join(' ', words);

        await Assert.ThrowsAsync<FormatException>(
            async () => await new NodeIdentityRecovery(new FakeStore()).RestoreFromPhraseAsync(mistyped));
    }

    [Fact]
    public async Task RestoreFromPhraseAsync_refuses_a_phrase_that_is_not_a_32_byte_seed()
    {
        // A valid 12-word BIP-39 phrase decodes to 16 bytes — not an Ed25519 seed.
        var twelveWords = Bip39Mnemonic.EntropyToMnemonic(new byte[16]);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await new NodeIdentityRecovery(new FakeStore()).RestoreFromPhraseAsync(twelveWords));
    }

    [Fact]
    public async Task RestoreFromPhraseAsync_refuses_over_a_live_identity()
    {
        var source = new FakeStore();
        await new NodeIdentity(source).GetOrMintAsync();
        var phrase = await new NodeIdentityRecovery(source).ExportRecoveryPhraseAsync();

        var occupied = new FakeStore();
        await new NodeIdentity(occupied).GetOrMintAsync();

        await Assert.ThrowsAsync<IdentityAlreadyExistsException>(
            async () => await new NodeIdentityRecovery(occupied).RestoreFromPhraseAsync(phrase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RestoreFromPhraseAsync_refuses_an_empty_phrase(string? phrase)
    {
        var recovery = new NodeIdentityRecovery(new FakeStore());

        // null throws ArgumentNullException, ""/whitespace throws ArgumentException — both derive from
        // ArgumentException, so accept either.
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await recovery.RestoreFromPhraseAsync(phrase!));
    }

    [Fact]
    public void Constructor_refuses_a_recovery_with_nowhere_to_keep_the_identity() =>
        Assert.Throws<ArgumentNullException>(() => new NodeIdentityRecovery(null!));

    // ── Cross-language parity fixture ─────────────────────────────────────────
    //
    // fixtures/identity/portability.json is written from this same C# reference by
    // IdentityPortabilityFixtureGenerator, and is the contract every language port must reproduce
    // byte-for-byte. Asserting it here keeps the C# side honest against its own published vectors.

    private record PVector(
        [property: JsonPropertyName("seed")] string Seed,
        [property: JsonPropertyName("public_key")] string PublicKey,
        [property: JsonPropertyName("tag")] string Tag,
        [property: JsonPropertyName("recovery_phrase")] string RecoveryPhrase);

    private record PFile([property: JsonPropertyName("vectors")] List<PVector> Vectors);

    private static PFile LoadFixture()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "identity", "portability.json");
            if (File.Exists(candidate))
                return JsonSerializer.Deserialize<PFile>(File.ReadAllText(candidate))!;
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException(
            "fixtures/identity/portability.json not found — run IdentityPortabilityFixtureGenerator first.");
    }

    public static IEnumerable<object[]> FixtureVectors() =>
        LoadFixture().Vectors.Select((_, i) => new object[] { i });

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Theory]
    [MemberData(nameof(FixtureVectors))]
    public async Task Reference_reproduces_every_portability_vector(int index)
    {
        var v = LoadFixture().Vectors[index];
        var seed = Hex(v.Seed);

        // seed -> public key -> tag, all deterministic and matching the published bytes.
        var publicKey = Ed25519SigningService.DerivePublicKey(seed);
        Assert.Equal(v.PublicKey, Hex(publicKey));
        Assert.Equal(v.Tag, AetherNetTag.FromPublicKey(publicKey).Value);

        // seed <-> recovery phrase, both directions.
        Assert.Equal(v.RecoveryPhrase, Bip39Mnemonic.EntropyToMnemonic(seed));
        Assert.Equal(v.Seed, Hex(Bip39Mnemonic.MnemonicToEntropy(v.RecoveryPhrase)));

        // And the public surface lands the same tag when it adopts that seed.
        var adopted = await new NodeIdentityRecovery(new FakeStore()).AdoptSeedAsync(seed);
        Assert.Equal(v.Tag, adopted.Value);
    }
}
