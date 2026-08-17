// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

public class ContactServiceTests
{
    private const string Me = "KXJB7-MN2P4";
    private const string Them = "DY5CF-84G9T";

    private sealed class Rig : IDisposable
    {
        public AetherStore Store { get; } = AetherStore.InMemory();
        public FakeRadioMesh Radio { get; }
        public ContactService Contacts { get; }

        public Rig(string tag = Me)
        {
            Radio = new FakeRadioMesh(tag);
            Contacts = new ContactService(Store, new FakeIdentity(tag), Radio);
        }

        public ContactRecord? Find(string tag) => Contacts.Contacts.FirstOrDefault(c => c.Tag == tag);

        public void Dispose() => Store.Dispose();
    }

    // ── Invites ───────────────────────────────────────────────────────────────

    [Fact]
    public void MyInvite_uses_the_aether_scheme()
    {
        using var rig = new Rig();

        Assert.StartsWith("aether://", rig.Contacts.MyInvite);
    }

    /// <summary>A key and the tag it actually derives survive the round trip together.</summary>
    [Fact]
    public void TryParseInvite_reads_back_what_BuildInvite_wrote()
    {
        var (key, tagForKey) = AKeyAndItsTag();

        var ok = ContactService.TryParseInvite(ContactService.BuildInvite(tagForKey, key), out var tag, out var parsed);

        Assert.True(ok);
        Assert.Equal(tagForKey, tag);
        Assert.Equal(key, parsed);
    }

    /// <summary>
    /// A tag cannot be forged onto someone else's key. An invite claiming a tag the key does not
    /// derive keeps the tag — you may still have typed it correctly — but the key is thrown away,
    /// so nothing can be encrypted to an impostor.
    /// </summary>
    [Fact]
    public void TryParseInvite_drops_a_key_that_does_not_derive_the_tag()
    {
        var (someoneElsesKey, _) = AKeyAndItsTag();

        var ok = ContactService.TryParseInvite(
            ContactService.BuildInvite(Them, someoneElsesKey), out var tag, out var parsed);

        Assert.True(ok);
        Assert.Equal(Them, tag);
        Assert.Null(parsed);
    }

    private static (byte[] Key, string Tag) AKeyAndItsTag()
    {
        var (_, publicKey) = AetherNet.Security.Services.Ed25519SigningService.GenerateKeyPair();
        return (publicKey, AetherNet.Identity.AetherNetTag.FromPublicKey(publicKey).Value);
    }

    [Fact]
    public void TryParseInvite_accepts_a_bare_tag()
    {
        var ok = ContactService.TryParseInvite(Them, out var tag, out _);

        Assert.True(ok);
        Assert.Equal(Them, tag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-tag")]
    [InlineData("aether://not-a-tag/add")]
    [InlineData("https://example.com/DY5CF-84G9T")]
    public void TryParseInvite_rejects_anything_that_is_not_an_invite(string? text) =>
        Assert.False(ContactService.TryParseInvite(text, out _, out _));

    // ── Adding someone ────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_records_the_person()
    {
        using var rig = new Rig();

        await rig.Contacts.AddAsync(Them, "typed");

        Assert.NotNull(rig.Find(Them));
    }

    [Fact]
    public async Task AddAsync_leaves_the_contact_one_sided_until_they_add_back()
    {
        using var rig = new Rig();

        await rig.Contacts.AddAsync(Them, "typed");

        Assert.True(rig.Find(Them)!.IsPending);
        Assert.False(rig.Find(Them)!.IsMutual);
    }

    [Fact]
    public async Task AddAsync_rejects_a_tag_that_is_not_a_tag()
    {
        using var rig = new Rig();

        var added = await rig.Contacts.AddAsync("not-a-tag", "typed");

        Assert.False(added);
        Assert.Empty(rig.Contacts.Contacts);
    }

    [Fact]
    public async Task AddAsync_is_idempotent()
    {
        using var rig = new Rig();

        await rig.Contacts.AddAsync(Them, "typed");
        await rig.Contacts.AddAsync(Them, "qr");

        Assert.Single(rig.Contacts.Contacts);
    }

    // ── Both sides — the BBM model ────────────────────────────────────────────

    [Fact]
    public async Task Adding_someone_tells_them_so_they_can_add_back()
    {
        using var a = new Rig(Me);
        using var b = new Rig(Them);
        a.Radio.Peer = b.Radio;
        b.Radio.Peer = a.Radio;
        a.Radio.Link();
        b.Radio.Link();

        await a.Contacts.AddAsync(Them, "typed");

        // B now knows A wants to connect, without B having done anything.
        Assert.Contains(b.Contacts.Contacts, c => c.Tag == Me);
    }

    [Fact]
    public async Task A_contact_becomes_mutual_only_when_both_have_added()
    {
        using var a = new Rig(Me);
        using var b = new Rig(Them);
        a.Radio.Peer = b.Radio;
        b.Radio.Peer = a.Radio;
        a.Radio.Link();
        b.Radio.Link();

        await a.Contacts.AddAsync(Them, "typed");
        Assert.False(a.Find(Them)!.IsMutual);

        await b.Contacts.AddAsync(Me, "typed");

        Assert.True(a.Find(Them)!.IsMutual);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Contacts_survive_a_restart()
    {
        using var rig = new Rig();
        await rig.Contacts.AddAsync(Them, "typed");

        var reopened = new ContactService(rig.Store, new FakeIdentity(Me), rig.Radio);

        Assert.Contains(reopened.Contacts, c => c.Tag == Them);
    }

    [Fact]
    public async Task Remove_forgets_the_person()
    {
        using var rig = new Rig();
        await rig.Contacts.AddAsync(Them, "typed");

        rig.Contacts.Remove(Them);

        Assert.Empty(rig.Contacts.Contacts);
    }
}
