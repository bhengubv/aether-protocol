// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherNet.Identity;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Core.Tests.Identity;

/// <summary>
/// The device's node identity — one per device, for the life of the device.
///
/// <para>
/// A node's identity is its address on the mesh, so one device must be one node. An app that mints its
/// own puts a second node on the same handset: a second presence beacon, a second entry in every
/// neighbour's routing table, a second reputation for one human, and two radio stacks contending for a
/// radio that has hard limits. Install fifteen such apps and the phone is fifteen peers.
/// </para>
///
/// <para>
/// So an app never mints and never holds a key. It asks the node for the device's tag — minting it if
/// the device has none yet — and when something must be signed it hands over bytes and gets a signature
/// back. <see cref="INodeIdentity"/> is deliberately shaped so there is no method that could return a
/// private key.
/// </para>
/// </summary>
public class NodeIdentityTests
{
    /// <summary>
    /// Stands in for the one place a device keeps its node identity. Real implementations are
    /// platform-specific and gated by the device's own authentication.
    /// </summary>
    private sealed class FakeStore : INodeIdentityStore
    {
        private byte[]? _privateKey;

        /// <summary>Set to simulate a device that is locked — the identity is there but sealed shut.</summary>
        public bool Locked { get; set; }

        /// <summary>How many times a new identity was written. Must never exceed one per device.</summary>
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

    // ── Mint once, adopt forever ──────────────────────────────────────────────

    [Fact]
    public async Task GetOrMintAsync_mints_when_the_device_has_no_identity()
    {
        var store = new FakeStore();

        var tag = await new NodeIdentity(store).GetOrMintAsync();

        Assert.True(AetherNetTag.TryParse(tag.Value, out _));
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task GetOrMintAsync_adopts_the_identity_the_device_already_has()
    {
        var store = new FakeStore();
        var first = await new NodeIdentity(store).GetOrMintAsync();

        var second = await new NodeIdentity(store).GetOrMintAsync();

        Assert.Equal(first, second);
        Assert.Equal(1, store.Writes);
    }

    /// <summary>
    /// The whole point, stated as the thing that was broken: two apps from two vendors, each with its
    /// own everything, sharing one device. They are one node.
    /// </summary>
    [Fact]
    public async Task Two_unrelated_apps_on_one_device_are_one_node()
    {
        var device = new FakeStore();

        var appA = await new NodeIdentity(device).GetOrMintAsync();
        var appB = await new NodeIdentity(device).GetOrMintAsync();

        Assert.Equal(appA, appB);
    }

    [Fact]
    public async Task Two_devices_are_never_the_same_node()
    {
        var a = await new NodeIdentity(new FakeStore()).GetOrMintAsync();
        var b = await new NodeIdentity(new FakeStore()).GetOrMintAsync();

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// Two apps launching together on a fresh device must not each mint one and race to save. The
    /// loser's tag would already be on the wire.
    /// </summary>
    [Fact]
    public async Task Apps_starting_at_the_same_moment_mint_exactly_one_identity()
    {
        var device = new FakeStore();
        var node = new NodeIdentity(device);

        var tags = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => node.GetOrMintAsync().AsTask()));

        Assert.Single(tags.Distinct());
        Assert.Equal(1, device.Writes);
    }

    // ── The tag is the canonical one ──────────────────────────────────────────

    [Fact]
    public async Task The_tag_is_derived_from_the_node_public_key()
    {
        var node = new NodeIdentity(new FakeStore());

        var tag = await node.GetOrMintAsync();
        var publicKey = await node.GetPublicKeyAsync();

        Assert.Equal(AetherNetTag.FromPublicKey(publicKey), tag);
    }

    [Fact]
    public async Task The_tag_verifies_against_the_node_public_key()
    {
        var node = new NodeIdentity(new FakeStore());

        var tag = await node.GetOrMintAsync();

        Assert.True(AetherNetTag.Verify(tag.Value, await node.GetPublicKeyAsync()));
    }

    // ── An identity is never replaced ─────────────────────────────────────────

    /// <summary>
    /// A stored identity that cannot be opened right now is still the device's identity. Minting over
    /// it changes the device's address permanently, and everyone holding the old one is left with an
    /// address that no longer answers.
    /// </summary>
    [Fact]
    public async Task GetOrMintAsync_refuses_rather_than_minting_over_a_sealed_identity()
    {
        var store = new FakeStore();
        await new NodeIdentity(store).GetOrMintAsync();

        store.Locked = true;

        await Assert.ThrowsAsync<NodeIdentityUnavailableException>(
            async () => await new NodeIdentity(store).GetOrMintAsync());
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task An_identity_survives_a_spell_of_being_unavailable()
    {
        var store = new FakeStore();
        var original = await new NodeIdentity(store).GetOrMintAsync();

        store.Locked = true;
        await Assert.ThrowsAsync<NodeIdentityUnavailableException>(
            async () => await new NodeIdentity(store).GetOrMintAsync());
        store.Locked = false;

        Assert.Equal(original, await new NodeIdentity(store).GetOrMintAsync());
    }

    // ── The key never leaves ──────────────────────────────────────────────────

    [Fact]
    public async Task SignAsync_produces_a_signature_the_node_public_key_verifies()
    {
        var node = new NodeIdentity(new FakeStore());
        await node.GetOrMintAsync();
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        var signature = await node.SignAsync(payload);

        Assert.True(Ed25519SigningService.Verify(await node.GetPublicKeyAsync(), payload, signature));
    }

    [Fact]
    public async Task SignAsync_mints_the_identity_if_the_device_has_none()
    {
        var store = new FakeStore();

        await new NodeIdentity(store).SignAsync(new byte[] { 9 });

        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task Two_apps_on_one_device_produce_signatures_under_the_same_identity()
    {
        var device = new FakeStore();
        var appA = new NodeIdentity(device);
        var appB = new NodeIdentity(device);
        var payload = new byte[] { 7, 7, 7 };

        var signature = await appA.SignAsync(payload);

        Assert.True(Ed25519SigningService.Verify(await appB.GetPublicKeyAsync(), payload, signature));
    }

    // ── Derived keys: the root never leaves ───────────────────────────────────

    [Fact]
    public async Task DeriveKeyAsync_gives_the_same_key_for_the_same_purpose()
    {
        var node = new NodeIdentity(new FakeStore());

        var first = await node.DeriveKeyAsync("erid-routing");
        var second = await node.DeriveKeyAsync("erid-routing");

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task DeriveKeyAsync_gives_a_different_key_for_a_different_purpose()
    {
        var node = new NodeIdentity(new FakeStore());

        Assert.NotEqual(
            await node.DeriveKeyAsync("erid-routing"),
            await node.DeriveKeyAsync("content-store"));
    }

    /// <summary>Two apps on one device asking for the same purpose get the same key — one node.</summary>
    [Fact]
    public async Task DeriveKeyAsync_agrees_across_apps_on_one_device()
    {
        var device = new FakeStore();

        Assert.Equal(
            await new NodeIdentity(device).DeriveKeyAsync("erid-routing"),
            await new NodeIdentity(device).DeriveKeyAsync("erid-routing"));
    }

    [Fact]
    public async Task DeriveKeyAsync_gives_different_devices_different_keys()
    {
        Assert.NotEqual(
            await new NodeIdentity(new FakeStore()).DeriveKeyAsync("erid-routing"),
            await new NodeIdentity(new FakeStore()).DeriveKeyAsync("erid-routing"));
    }

    /// <summary>
    /// A derived key that leaked the root, or that equalled it, would make "the root never leaves"
    /// a slogan rather than a property.
    /// </summary>
    [Fact]
    public async Task A_derived_key_is_not_the_public_key_and_is_a_full_length_key()
    {
        var node = new NodeIdentity(new FakeStore());

        var derived = await node.DeriveKeyAsync("erid-routing");

        Assert.Equal(32, derived.Length);
        Assert.NotEqual(await node.GetPublicKeyAsync(), derived);
    }

    [Fact]
    public async Task DeriveKeyAsync_mints_the_identity_if_the_device_has_none()
    {
        var store = new FakeStore();

        await new NodeIdentity(store).DeriveKeyAsync("erid-routing");

        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DeriveKeyAsync_refuses_an_unnamed_purpose(string? purpose)
    {
        var node = new NodeIdentity(new FakeStore());

        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await node.DeriveKeyAsync(purpose!));
    }

    // ── Guarding the inputs ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_refuses_a_node_with_nowhere_to_keep_its_identity() =>
        Assert.Throws<ArgumentNullException>(() => new NodeIdentity(null!));

    [Fact]
    public async Task SignAsync_refuses_nothing_to_sign()
    {
        var node = new NodeIdentity(new FakeStore());

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await node.SignAsync(null!));
    }
}
