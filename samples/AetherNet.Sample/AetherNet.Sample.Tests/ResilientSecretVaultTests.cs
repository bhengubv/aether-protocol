// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A phone keeps its identity in the best place it has: secure hardware when the Keystore works, an
/// encrypted file when it does not. Which of the two answers can change between one run and the next —
/// a Keystore key gets invalidated, an OS update changes what the silicon will do, a reinstall lands
/// on a different path.
///
/// <para>
/// When that happens the vault must still find the secret it already wrote. Looking in only the store
/// that is usable <i>today</i> reports "this device has never had an identity", and the caller does the
/// one irreversible thing: mints a replacement. Every friend who added that phone is then holding an
/// address that no longer answers — the exact loss <c>ISecretVault.Get</c> warns about, arrived at by a
/// different road.
/// </para>
///
/// <para>Proven on hardware 2026-08-13: a P30 Lite came back from a reinstall with a new AetherTag.</para>
/// </summary>
public class ResilientSecretVaultTests
{
    private const string Name = "aether.identity.ed25519";

    /// <summary>
    /// A hardware vault whose <i>opener</i> can be taken away while the sealed blob stays on disk —
    /// a Keystore key invalidated by a lock-screen change, or simply unavailable this run.
    ///
    /// <para>
    /// <c>Has</c> stays truthful throughout, because it answers "is a sealed blob stored here", not
    /// "can I open it right now". Those are different questions and the phone paid for confusing them.
    /// </para>
    /// </summary>
    private sealed class FlakyVault : ISecretVault
    {
        private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

        public bool Working { get; set; } = true;
        public bool Locked { get; set; }

        public bool IsHardwareBacked => true;
        public string ProtectionDescription => "Sealed by this phone's secure hardware";

        public bool Has(string name) => _secrets.ContainsKey(name);

        public void Set(string name, byte[] secret)
        {
            if (!Working) throw new InvalidOperationException("Keystore unavailable.");
            _secrets[name] = secret;
        }

        public byte[]? Get(string name)
        {
            if (!_secrets.TryGetValue(name, out var secret)) return null;
            if (!Working || Locked) throw new SecretUnavailableException("The sealed key cannot be opened.");
            return secret;
        }
    }

    private static (ResilientSecretVault Vault, FlakyVault Hardware, ISecretVault File) Build()
    {
        var hardware = new FlakyVault();
        var file = new InMemorySecretVault();
        return (new ResilientSecretVault(hardware, file), hardware, file);
    }

    /// <summary>Stands in for the encrypted-file vault without touching a disk.</summary>
    private sealed class InMemorySecretVault : ISecretVault
    {
        private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

        public bool IsHardwareBacked => false;
        public string ProtectionDescription => "Encrypted on this device";
        public bool Has(string name) => _secrets.ContainsKey(name);
        public void Set(string name, byte[] secret) => _secrets[name] = secret;
        public byte[]? Get(string name) => _secrets.TryGetValue(name, out var s) ? s : null;
    }

    // ── The secret survives the store changing under it ───────────────────────

    /// <summary>
    /// The sealed blob is still on disk; only the key that opens it is out of reach. Answering null
    /// here — or from the empty file store — is what replaced a P30 Lite's AetherTag on 2026-08-13.
    /// </summary>
    [Fact]
    public void Get_refuses_rather_than_reporting_nothing_when_the_hardware_store_cannot_open_it()
    {
        var (vault, hardware, _) = Build();
        vault.Set(Name, [1, 2, 3]);

        hardware.Working = false;   // the Keystore will not open the blob it sealed

        Assert.Throws<SecretUnavailableException>(() => vault.Get(Name));
    }

    [Fact]
    public void Has_still_reports_a_secret_the_hardware_store_can_no_longer_open()
    {
        var (vault, hardware, _) = Build();
        vault.Set(Name, [1, 2, 3]);

        hardware.Working = false;

        Assert.True(vault.Has(Name),
            "the vault says this device has no identity — the caller will mint a replacement");
    }

    /// <summary>A Keystore that comes back must hand over the same secret, not a successor to it.</summary>
    [Fact]
    public void Get_returns_the_original_secret_once_the_hardware_store_recovers()
    {
        var (vault, hardware, _) = Build();
        vault.Set(Name, [1, 2, 3]);

        hardware.Working = false;
        Assert.Throws<SecretUnavailableException>(() => vault.Get(Name));
        hardware.Working = true;

        Assert.Equal<byte[]>([1, 2, 3], vault.Get(Name)!);
    }

    [Fact]
    public void Get_still_finds_a_secret_written_before_the_hardware_store_appeared()
    {
        var (vault, hardware, _) = Build();
        hardware.Working = false;
        vault.Set(Name, [4, 5, 6]);       // written to the file vault

        hardware.Working = true;          // a later run gets its Keystore back

        Assert.Equal<byte[]>([4, 5, 6], vault.Get(Name)!);
    }

    [Fact]
    public void Has_still_reports_a_secret_written_before_the_hardware_store_appeared()
    {
        var (vault, hardware, _) = Build();
        hardware.Working = false;
        vault.Set(Name, [4, 5, 6]);

        hardware.Working = true;

        Assert.True(vault.Has(Name));
    }

    // ── "Not now" is never "not there" ────────────────────────────────────────

    /// <summary>
    /// A locked phone must not be answered from the fallback and must not be answered with null. The
    /// refusal has to travel, because it is the only thing standing between a locked screen and a
    /// replaced identity.
    /// </summary>
    [Fact]
    public void Get_propagates_a_refusal_rather_than_falling_back()
    {
        var (vault, hardware, _) = Build();
        vault.Set(Name, [7, 7, 7]);

        hardware.Locked = true;

        Assert.Throws<SecretUnavailableException>(() => vault.Get(Name));
    }

    [Fact]
    public void Get_returns_null_only_when_neither_store_has_ever_held_it()
    {
        var (vault, _, _) = Build();

        Assert.Null(vault.Get(Name));
        Assert.False(vault.Has(Name));
    }

    // ── Writing goes to the best place available ──────────────────────────────

    [Fact]
    public void Set_prefers_the_hardware_store()
    {
        var (vault, hardware, file) = Build();

        vault.Set(Name, [1]);

        Assert.True(hardware.Has(Name));
        Assert.False(file.Has(Name));
    }

    [Fact]
    public void Set_uses_the_file_store_when_the_hardware_store_refuses()
    {
        var (vault, hardware, file) = Build();
        hardware.Working = false;

        vault.Set(Name, [1]);

        Assert.True(file.Has(Name));
    }

    /// <summary>
    /// Once a secret lives in the file store, a Keystore that comes back must not create a second copy
    /// under the same name — two stores each holding a different key is how a phone ends up with two
    /// identities.
    /// </summary>
    [Fact]
    public void Set_updates_the_store_that_already_holds_the_secret()
    {
        var (vault, hardware, file) = Build();
        hardware.Working = false;
        vault.Set(Name, [1]);

        hardware.Working = true;
        vault.Set(Name, [2]);

        Assert.False(hardware.Has(Name), "the secret now exists in two places under one name");
        Assert.Equal<byte[]>([2], vault.Get(Name)!);
    }

    // ── What the UI is told ───────────────────────────────────────────────────

    [Fact]
    public void IsHardwareBacked_follows_where_the_secret_actually_lives()
    {
        var (vault, hardware, _) = Build();
        hardware.Working = false;
        vault.Set(Name, [1]);

        Assert.False(vault.IsHardwareBacked);
        Assert.Equal("Encrypted on this device", vault.ProtectionDescription);
    }

    // ── The identity itself ───────────────────────────────────────────────────

    /// <summary>The device's node over this vault — how the identity is actually reached.</summary>
    private static AetherNet.Identity.NodeIdentity NodeOver(ISecretVault vault) =>
        new(new VaultNodeIdentityStore(vault));

    /// <summary>
    /// The whole point, stated as the outcome a person would notice: a phone that cannot open its
    /// identity says so. It does not quietly become someone else.
    /// </summary>
    [Fact]
    public void An_aether_tag_is_never_replaced_when_the_hardware_store_stops_opening_it()
    {
        var (vault, hardware, _) = Build();
        var original = NodeOver(vault).GetOrMintAsync().AsTask().GetAwaiter().GetResult();

        hardware.Working = false;
        Assert.Throws<AetherNet.Identity.NodeIdentityUnavailableException>(
            () => NodeOver(vault).GetOrMintAsync().AsTask().GetAwaiter().GetResult());
        hardware.Working = true;

        Assert.Equal(original, NodeOver(vault).GetOrMintAsync().AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void An_aether_tag_survives_the_hardware_store_arriving_later()
    {
        var (vault, hardware, _) = Build();
        hardware.Working = false;
        var original = NodeOver(vault).GetOrMintAsync().AsTask().GetAwaiter().GetResult();

        hardware.Working = true;

        Assert.Equal(original, NodeOver(vault).GetOrMintAsync().AsTask().GetAwaiter().GetResult());
    }
}
