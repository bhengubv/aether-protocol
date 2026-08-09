// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Shared.Tests;

/// <summary>An in-memory vault, so the identity tests don't touch the filesystem or a keystore.</summary>
internal sealed class FakeVault : ISecretVault
{
    private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);
    public bool IsHardwareBacked => false;
    public string ProtectionDescription => "Test vault";
    public byte[]? Get(string name) => _secrets.TryGetValue(name, out var v) ? v : null;
    public void Set(string name, byte[] secret) => _secrets[name] = secret;
}

/// <summary>
/// The identity has to be permanent: someone who added you yesterday must still reach you today, and a
/// card you signed must still verify against the tag you show. Before this work the service minted a
/// fresh keypair in its constructor, so every restart silently became a different person.
/// </summary>
public sealed class IdentityServiceTests
{
    [Fact]
    public void SecondRun_KeepsTheSameIdentity()
    {
        var vault = new FakeVault();
        using var store = AetherStore.InMemory();

        var first = new IdentityService(vault, store);
        var second = new IdentityService(vault, store);   // stands in for the next app launch

        Assert.True(first.IsNewIdentity);
        Assert.False(second.IsNewIdentity);
        Assert.Equal(first.AetherTag, second.AetherTag);
        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal(first.PrivateKey, second.PrivateKey);
    }

    [Fact]
    public void Tag_IsDerivedFromTheKey_AndVerifies()
    {
        var identity = new IdentityService(new FakeVault(), AetherStore.InMemory());

        Assert.True(AetherNetTag.Verify(identity.AetherTag, identity.PublicKey));
        Assert.Equal(AetherNetTag.FromPublicKey(identity.PublicKey).Value, identity.AetherTag);
    }

    [Fact]
    public void Identity_IsMirroredForDisplay()
    {
        using var store = AetherStore.InMemory();
        var identity = new IdentityService(new FakeVault(), store);

        var mirrored = store.GetIdentity();
        Assert.NotNull(mirrored);
        Assert.Equal(identity.AetherTag, mirrored!.Value.Tag);
    }

    [Fact]
    public void TamperedMirror_DoesNotChangeWhoYouAre()
    {
        // The key in the vault is the authority. A rewritten database row must not be able to swap
        // your identity out from under you.
        var vault = new FakeVault();
        using var store = AetherStore.InMemory();
        var original = new IdentityService(vault, store);

        store.SaveIdentity("ZZZZZ-ZZZZZ", new byte[32]);
        var reloaded = new IdentityService(vault, store);

        Assert.Equal(original.AetherTag, reloaded.AetherTag);
        Assert.Equal(original.AetherTag, store.GetIdentity()!.Value.Tag);   // and the mirror is repaired
    }
}

/// <summary>
/// Adding is the BBM handshake: each side adds the other, and the pair only counts once both have.
/// There is no directory to look anyone up in, so these transitions are the whole relationship model.
/// </summary>
public sealed class ContactServiceTests
{
    private static (ContactService Contacts, AetherStore Store, IIdentityService Me) Build()
    {
        var store = AetherStore.InMemory();
        var me = new IdentityService(new FakeVault(), store);
        return (new ContactService(store, me), store, me);
    }

    [Fact]
    public async Task AddingThemThenBeingAdded_BecomesMutual()
    {
        var (contacts, _, _) = Build();
        var (_, theirKey) = AetherNet.Security.Services.Ed25519SigningService.GenerateKeyPair();
        var theirTag = AetherNetTag.FromPublicKey(theirKey).Value;

        Assert.True(await contacts.AddAsync(theirTag, via: "typed"));
        Assert.Single(contacts.Contacts);
        Assert.True(contacts.Contacts[0].IsPending);
        Assert.Empty(contacts.Mutual);

        contacts.TryHandle(AddPacketFrom(theirTag, theirKey));

        Assert.Single(contacts.Mutual);
        Assert.True(contacts.Contacts[0].IsMutual);
    }

    [Fact]
    public async Task BeingAddedFirst_ShowsAsIncomingUntilYouAddBack()
    {
        var (contacts, _, _) = Build();
        var (_, theirKey) = AetherNet.Security.Services.Ed25519SigningService.GenerateKeyPair();
        var theirTag = AetherNetTag.FromPublicKey(theirKey).Value;

        contacts.TryHandle(AddPacketFrom(theirTag, theirKey));
        Assert.Single(contacts.Incoming);
        Assert.Empty(contacts.Mutual);

        await contacts.AddAsync(theirTag, via: "typed");
        Assert.Empty(contacts.Incoming);
        Assert.Single(contacts.Mutual);
    }

    [Fact]
    public void ForgedKey_IsRejectedButTheTagIsKept()
    {
        // A tag cannot be forged onto someone else's key: the claimed tag stays, the lying key does not.
        var (contacts, store, _) = Build();
        var (_, realKey) = AetherNet.Security.Services.Ed25519SigningService.GenerateKeyPair();
        var (_, otherKey) = AetherNet.Security.Services.Ed25519SigningService.GenerateKeyPair();
        var tag = AetherNetTag.FromPublicKey(realKey).Value;

        contacts.TryHandle(AddPacketFrom(tag, otherKey));

        var stored = store.GetContact(tag);
        Assert.NotNull(stored);
        Assert.Null(stored!.PublicKey);
    }

    [Fact]
    public async Task AddingYourself_IsRefused()
    {
        var (contacts, _, me) = Build();
        Assert.False(await contacts.AddAsync(me.AetherTag, via: "typed"));
        Assert.Empty(contacts.Contacts);
    }

    [Fact]
    public async Task Nonsense_IsRefused()
    {
        var (contacts, _, _) = Build();
        Assert.False(await contacts.AddAsync("not-a-tag", via: "typed"));
        Assert.False(await contacts.AddAsync("", via: "typed"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Invite_RoundTrips_AsUriOrBareTag(bool asUri)
    {
        var (_, key) = AetherNet.Security.Services.Ed25519SigningService.GenerateKeyPair();
        var tag = AetherNetTag.FromPublicKey(key).Value;
        var text = asUri ? ContactService.BuildInvite(tag, key) : tag;

        Assert.True(ContactService.TryParseInvite(text, out var parsedTag, out var parsedKey));
        Assert.Equal(tag, parsedTag);
        if (asUri) Assert.Equal(key, parsedKey);
    }

    [Fact]
    public async Task AddingTwice_DoesNotDuplicate()
    {
        var (contacts, _, _) = Build();
        var (_, key) = AetherNet.Security.Services.Ed25519SigningService.GenerateKeyPair();
        var tag = AetherNetTag.FromPublicKey(key).Value;

        await contacts.AddAsync(tag, via: "typed");
        await contacts.AddAsync(tag, via: "qr");

        Assert.Single(contacts.Contacts);
    }

    /// <summary>An add-request exactly as it arrives off the radio.</summary>
    private static AetherNet.Protocol.MeshPacket AddPacketFrom(string tag, byte[] publicKey)
    {
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new { version = 1, tag, public_key = Convert.ToBase64String(publicKey) });
        var marker = System.Text.Encoding.UTF8.GetBytes("AETHERADD");
        var payload = new byte[marker.Length + body.Length];
        marker.CopyTo(payload, 0);
        body.CopyTo(payload, marker.Length);

        return new AetherNet.Protocol.MeshPacket
        {
            Type = AetherNet.Protocol.PacketType.Data,
            SourceUhid = tag,
            Payload = payload,
        };
    }
}

/// <summary>Setup must not be skippable, so the flag that gates it has to survive a restart.</summary>
public sealed class AetherStoreTests
{
    [Fact]
    public void SetupFlag_DefaultsFalse_AndPersists()
    {
        using var store = AetherStore.InMemory();
        Assert.False(store.GetFlag(SetupKeys.Complete));

        store.SetFlag(SetupKeys.Complete, true);
        Assert.True(store.GetFlag(SetupKeys.Complete));
    }

    [Fact]
    public void Contacts_SurviveReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-app-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new AetherStore(path))
                first.UpsertContact("KXJB7-MN2P4", null, byMe: true, byThem: false, via: "typed");

            using var second = new AetherStore(path);
            var contact = second.GetContact("KXJB7-MN2P4");

            Assert.NotNull(contact);
            Assert.True(contact!.IsPending);
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) try { File.Delete(f); } catch (IOException) { }
        }
    }

    [Fact]
    public void AddedFlags_AreSticky()
    {
        // An inbound request can land before or after your own add; neither may un-do the other.
        using var store = AetherStore.InMemory();
        store.UpsertContact("KXJB7-MN2P4", null, byMe: true, byThem: false, via: "typed");
        store.UpsertContact("KXJB7-MN2P4", null, byMe: false, byThem: true, via: "radio");

        Assert.True(store.GetContact("KXJB7-MN2P4")!.IsMutual);
    }
}
