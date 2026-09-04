// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The app's view of who this device is.
///
/// <para>
/// This service does not create an identity and does not hold a key — it asks the node. Minting,
/// adopting and refusing all belong to <c>NodeIdentity</c> and are tested there; what matters here is
/// that the app is a <i>client</i>: it reports what the node says, it never mints its own, and it can
/// get things signed without ever being handed the secret that signs them.
/// </para>
/// </summary>
public class IdentityServiceTests
{
    /// <summary>A vault the app can describe but no longer mints into.</summary>
    private sealed class FakeVault : ISecretVault
    {
        private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

        public bool IsHardwareBacked => true;
        public string ProtectionDescription => "Test vault";
        public bool Has(string name) => _secrets.ContainsKey(name);
        public void Set(string name, byte[] secret) => _secrets[name] = secret;
        public byte[]? Get(string name) => _secrets.TryGetValue(name, out var s) ? s : null;
        public void Remove(string name) => _secrets.Remove(name);
    }

    /// <summary>
    /// Build the service and resolve the identity, which is what starting the app does.
    /// </summary>
    /// <remarks>
    /// The unsealing used to happen in the constructor, which meant it ran on whichever thread
    /// resolved the service — the UI thread, in a Blazor Hybrid app, where three keystore round trips
    /// froze the interface. It now happens behind a Lazy that the warm-up drives off that thread.
    /// <para>
    /// The guarantees these tests exist for are unchanged: a locked device still refuses rather than
    /// minting a second identity, and starting twice still writes once. Only the moment they surface
    /// moved, so the tests ask at the new moment.
    /// </para>
    /// </remarks>
    private static IdentityService Build(FakeNodeIdentityStore device, AetherStore store, ISecretVault? vault = null)
    {
        var identity = new IdentityService(device.Node(), vault ?? new FakeVault(), store);
        _ = identity.AetherTag;   // resolve now, exactly as the warm-up does
        return identity;
    }

    // ── The app asks; it does not mint ────────────────────────────────────────

    [Fact]
    public void AetherTag_is_whatever_the_node_says_it_is()
    {
        using var store = AetherStore.InMemory();
        var device = new FakeNodeIdentityStore();
        var expected = device.Node().GetOrMintAsync().AsTask().GetAwaiter().GetResult().Value;

        Assert.Equal(expected, Build(device, store).AetherTag);
    }

    /// <summary>
    /// Two apps, each with its own everything, on one handset. One node — the point of all of it.
    /// </summary>
    [Fact]
    public void Two_apps_on_one_device_report_the_same_tag()
    {
        using var storeA = AetherStore.InMemory();
        using var storeB = AetherStore.InMemory();
        var device = new FakeNodeIdentityStore();

        Assert.Equal(Build(device, storeA).AetherTag, Build(device, storeB).AetherTag);
        Assert.Equal(1, device.Writes);
    }

    [Fact]
    public void Two_devices_report_different_tags()
    {
        using var storeA = AetherStore.InMemory();
        using var storeB = AetherStore.InMemory();

        Assert.NotEqual(
            Build(new FakeNodeIdentityStore(), storeA).AetherTag,
            Build(new FakeNodeIdentityStore(), storeB).AetherTag);
    }

    [Fact]
    public void Starting_the_app_again_does_not_mint_a_second_identity()
    {
        using var store = AetherStore.InMemory();
        var device = new FakeNodeIdentityStore();

        Build(device, store);
        Build(device, store);

        Assert.Equal(1, device.Writes);
    }

    // ── An identity is never replaced ─────────────────────────────────────────

    [Fact]
    public void A_locked_device_refuses_rather_than_becoming_someone_new()
    {
        using var store = AetherStore.InMemory();
        var device = new FakeNodeIdentityStore();
        Build(device, store);

        device.Locked = true;

        Assert.Throws<NodeIdentityUnavailableException>(() => Build(device, store));
        Assert.Equal(1, device.Writes);
    }

    [Fact]
    public void A_tampered_local_record_does_not_change_who_this_device_is()
    {
        using var store = AetherStore.InMemory();
        var device = new FakeNodeIdentityStore();
        var original = Build(device, store).AetherTag;

        store.SaveIdentity("AAAAA-AAAAA", new byte[32]);   // someone else's tag in the local mirror

        Assert.Equal(original, Build(device, store).AetherTag);
    }

    [Fact]
    public void The_local_mirror_is_brought_back_in_line()
    {
        using var store = AetherStore.InMemory();
        var device = new FakeNodeIdentityStore();

        var tag = Build(device, store).AetherTag;

        Assert.Equal(tag, store.GetIdentity()!.Value.Tag);
    }

    // ── The tag, per IDENTITY_AND_DATA_SOVEREIGNTY §1 ─────────────────────────

    [Fact]
    public void AetherTag_has_the_documented_shape()
    {
        using var store = AetherStore.InMemory();

        Assert.Matches("^[0-9A-Z]{5}-[0-9A-Z]{5}$", Build(new FakeNodeIdentityStore(), store).AetherTag);
    }

    [Fact]
    public void AetherTag_is_derived_from_the_public_key()
    {
        using var store = AetherStore.InMemory();

        var me = Build(new FakeNodeIdentityStore(), store);

        Assert.Equal(AetherNetTag.FromPublicKey(me.PublicKey).Value, me.AetherTag);
    }

    // ── Signing without holding the key ───────────────────────────────────────

    [Fact]
    public void Sign_produces_a_signature_this_device_verifies()
    {
        using var store = AetherStore.InMemory();
        var me = Build(new FakeNodeIdentityStore(), store);
        var payload = new byte[] { 1, 2, 3 };

        Assert.True(AetherNet.Security.Services.Ed25519SigningService.Verify(
            me.PublicKey, payload, me.Sign(payload)));
    }

    // ── The wire address is derived, not the identity ─────────────────────────

    [Fact]
    public void RoutingKey_is_not_the_public_key()
    {
        using var store = AetherStore.InMemory();

        var me = Build(new FakeNodeIdentityStore(), store);

        Assert.NotEqual(me.PublicKey, me.RoutingKey);
    }

    [Fact]
    public void RoutingKey_is_the_same_for_two_apps_on_one_device()
    {
        using var storeA = AetherStore.InMemory();
        using var storeB = AetherStore.InMemory();
        var device = new FakeNodeIdentityStore();

        Assert.Equal(Build(device, storeA).RoutingKey, Build(device, storeB).RoutingKey);
    }

    [Fact]
    public void RoutingKey_differs_between_devices()
    {
        using var storeA = AetherStore.InMemory();
        using var storeB = AetherStore.InMemory();

        Assert.NotEqual(
            Build(new FakeNodeIdentityStore(), storeA).RoutingKey,
            Build(new FakeNodeIdentityStore(), storeB).RoutingKey);
    }

    // ── First run ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsNewIdentity_is_true_only_on_a_device_that_never_had_one()
    {
        using var store = AetherStore.InMemory();
        var vault = new FakeVault();
        var device = new FakeNodeIdentityStore();

        Assert.True(Build(device, store, vault).IsNewIdentity);
    }
}
