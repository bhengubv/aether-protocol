// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The panic wipe: a duress PIN destroys the identity key and every trace of local data. These hold the
/// wiring to that promise — the right PIN wipes, the wrong PIN is a silent no-op with no tell, and the
/// identity the app actually files (not the protocol's canonical name) is the one that gets removed.
/// </summary>
public sealed class PanicWipeServiceTests
{
    /// <summary>An in-memory vault that honours the new <c>Remove</c>, standing in for the keystore.</summary>
    private sealed class FakeVault : ISecretVault
    {
        private readonly Dictionary<string, byte[]> _d = new();
        public bool IsHardwareBacked => false;
        public string ProtectionDescription => "test";
        public byte[]? Get(string name) => _d.TryGetValue(name, out var v) ? v : null;
        public bool Has(string name) => _d.ContainsKey(name);
        public void Set(string name, byte[] secret) => _d[name] = secret;
        public void Remove(string name) => _d.Remove(name);
    }

    private static (AetherStore store, FakeVault vault, PanicWipeService svc) Fresh()
    {
        var store = AetherStore.InMemory();
        store.SaveIdentity("KXJB7-MN2P4", new byte[] { 1, 2, 3 });
        store.UpsertContact("AAAAA-11111", null, byMe: true, byThem: true, "typed");
        var vault = new FakeVault();
        vault.Set("aether.node.identity", new byte[] { 9, 9, 9 });   // the real name the app files under
        return (store, vault, new PanicWipeService(store, vault));
    }

    [Fact]
    public void Unarmed_submit_never_wipes()
    {
        var (store, vault, svc) = Fresh();
        Assert.False(svc.IsArmed);

        Assert.False(svc.TrySubmit("0000"));

        Assert.NotNull(store.GetIdentity());
        Assert.True(vault.Has("aether.node.identity"));
    }

    [Fact]
    public void Wrong_pin_is_a_silent_no_op()
    {
        var (store, vault, svc) = Fresh();
        svc.SetDuressPin("911911");
        Assert.True(svc.IsArmed);

        Assert.False(svc.TrySubmit("123456"));

        Assert.NotNull(store.GetIdentity());
        Assert.Single(store.GetContacts());
        Assert.True(vault.Has("aether.node.identity"));
    }

    [Fact]
    public void Duress_pin_wipes_identity_and_all_local_data()
    {
        var (store, vault, svc) = Fresh();
        svc.SetDuressPin("911911");

        Assert.True(svc.TrySubmit("911911"));

        // Identity gone (the app's name and any legacy), database empty — a fresh install.
        Assert.False(vault.Has("aether.node.identity"));
        Assert.False(vault.Has("aether.identity.ed25519"));
        Assert.Null(store.GetIdentity());
        Assert.Empty(store.GetContacts());
    }

    [Fact]
    public void Wipe_button_destroys_without_a_pin()
    {
        var (store, vault, svc) = Fresh();

        svc.Wipe();

        Assert.False(vault.Has("aether.node.identity"));
        Assert.Null(store.GetIdentity());
    }

    [Fact]
    public void Disarm_forgets_the_pin()
    {
        var (store, _, svc) = Fresh();
        svc.SetDuressPin("911911");
        Assert.True(svc.IsArmed);

        svc.Disarm();

        Assert.False(svc.IsArmed);
        Assert.False(svc.TrySubmit("911911"));   // no longer triggers
        Assert.NotNull(store.GetIdentity());
    }
}
