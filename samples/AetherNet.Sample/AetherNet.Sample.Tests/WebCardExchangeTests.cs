// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using System.Text;
using AetherNet.Content;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Cards crossing between phones — the mesh-web, rather than the plumbing underneath it.
///
/// <para>
/// From <c>02_REMAINING_WORK</c> §2: a card is "held, kept offline, and re-served. Any holder can
/// serve a card to a third phone that never met the author, who still verifies it (signature + hash)
/// — <b>hold-and-forward</b>, spread decoupled from origin."
/// </para>
///
/// <para>
/// That is the whole point. A page on a server is reachable while the server is up; a card you are
/// handed is yours, and you can hand it on when the author is long gone and there is no tower.
/// </para>
/// </summary>
public class WebCardExchangeTests
{
    private sealed class Device
    {
        public FakeIdentity Me { get; } = FakeIdentity.Unique();
        public FakeRadioMesh Radio { get; }
        public MeshWebService Web { get; }

        public Device()
        {
            Radio = new FakeRadioMesh(Me.AetherTag);
            Web = new MeshWebService(Me.Node, new InMemoryContentStore(), new RadioMeshLink(Radio));
        }

        public string Tag => Me.AetherTag;
    }

    private static async Task<(Device A, Device B)> TwoLinkedPhonesAsync()
    {
        var a = new Device();
        var b = new Device();

        a.Radio.Peer = b.Radio;
        b.Radio.Peer = a.Radio;
        a.Radio.Link();
        b.Radio.Link();

        await a.Web.EnsureReadyAsync();
        await b.Web.EnsureReadyAsync();
        return (a, b);
    }

    /// <summary>Give the radio a moment — these exchanges are several packets deep.</summary>
    private static async Task<bool> EventuallyAsync(Func<bool> until, int seconds = 10)
    {
        for (var i = 0; i < seconds * 20 && !until(); i++)
            await Task.Delay(50);
        return until();
    }

    // ── Each phone hosts its own ──────────────────────────────────────────────

    [Fact]
    public async Task Each_phone_hosts_a_card_of_its_own()
    {
        var (a, b) = await TwoLinkedPhonesAsync();

        Assert.NotEmpty(a.Web.Pages);
        Assert.NotEmpty(b.Web.Pages);
    }

    [Fact]
    public async Task Two_phones_do_not_share_a_card_address()
    {
        var (a, b) = await TwoLinkedPhonesAsync();

        Assert.NotEqual(a.Web.HomeAddress, b.Web.HomeAddress);
    }

    // ── Fetching the other phone's card ───────────────────────────────────────

    [Fact]
    public async Task A_phone_can_open_the_other_phones_card()
    {
        var (a, b) = await TwoLinkedPhonesAsync();

        var page = await a.Web.OpenAsync(b.Web.HomeAddress);

        Assert.True(page.Ok, page.Error);
    }

    /// <summary>
    /// Not mine — which is the part that matters. Whether the bytes travelled is deliberately not
    /// asserted: cards are content-addressed, so a card whose content this phone already holds is
    /// served locally even the first time it is opened. That is the design working, not a shortcut.
    /// </summary>
    [Fact]
    public async Task A_fetched_card_is_marked_as_someone_elses()
    {
        var (a, b) = await TwoLinkedPhonesAsync();

        var page = await a.Web.OpenAsync(b.Web.HomeAddress);

        Assert.False(page.Own);
    }

    [Fact]
    public async Task A_fetched_card_names_the_phone_that_authored_it()
    {
        var (a, b) = await TwoLinkedPhonesAsync();

        var page = await a.Web.OpenAsync(b.Web.HomeAddress);

        Assert.Equal(b.Tag, page.AuthorTag);
    }

    [Fact]
    public async Task Cards_cross_in_both_directions()
    {
        var (a, b) = await TwoLinkedPhonesAsync();

        var aSeesB = await a.Web.OpenAsync(b.Web.HomeAddress);
        var bSeesA = await b.Web.OpenAsync(a.Web.HomeAddress);

        Assert.True(aSeesB.Ok, aSeesB.Error);
        Assert.True(bSeesA.Ok, bSeesA.Error);
    }

    // ── Saying hello ──────────────────────────────────────────────────────────

    /// <summary>
    /// Two phones whose radios were already linked before either opened the mesh-web.
    ///
    /// <para>
    /// This is the ordinary case, not an edge case: the link comes up while you are reading your
    /// chats, and you go to the AetherNet tab afterwards. If the greeting is only sent when the link
    /// <b>changes</b>, neither phone ever speaks — each is waiting for a transition that already
    /// happened — and no phone ever learns that there is a site next to it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_phone_learns_the_others_address_even_though_the_link_was_already_up()
    {
        var (a, b) = await TwoLinkedPhonesAsync();

        var learned = await EventuallyAsync(() => a.Web.PeerSiteAddress == b.Web.HomeAddress);

        Assert.True(learned, $"A never learned B's address (saw '{a.Web.PeerSiteAddress}')");
    }

    [Fact]
    public async Task Both_phones_learn_each_others_address()
    {
        var (a, b) = await TwoLinkedPhonesAsync();

        var learned = await EventuallyAsync(() =>
            a.Web.PeerSiteAddress == b.Web.HomeAddress && b.Web.PeerSiteAddress == a.Web.HomeAddress);

        Assert.True(learned,
            $"A saw '{a.Web.PeerSiteAddress}', B saw '{b.Web.PeerSiteAddress}'");
    }

    // ── The picture crosses too ───────────────────────────────────────────────

    /// <summary>
    /// A card carries its own descriptor, so it arrives whole. Its picture does not — a picture is
    /// separate content, named by hash, and a phone cannot verify an arriving chunk without the
    /// descriptor for it. So the author announces its pictures when the link comes up, and this is
    /// what proves the announcement actually reaches the other side: without it the card renders as
    /// text with a caption where the picture should be, and nothing anywhere reports a failure.
    /// </summary>
    [Fact]
    public async Task A_card_from_another_phone_arrives_with_its_picture()
    {
        var (a, b) = await TwoLinkedPhonesAsync();
        await PublishWithPictureAsync(b);

        var page = await a.Web.OpenAsync(b.Web.Address("shop"));
        var image = Assert.Single(page.Card!.Blocks, x => x.Kind == CardBlock.Image);

        string? uri = null;
        await EventuallyAsync(() => (uri = a.Web.AssetAsync(image.ContentHash).GetAwaiter().GetResult()) is not null);

        Assert.NotNull(uri);
        Assert.StartsWith("data:image/jpeg;base64,", uri);
    }

    /// <summary>
    /// And it is <b>their</b> picture.
    /// </summary>
    /// <remarks>
    /// Both phones hold pictures of the same kind and shape, so a bug that resolved the hash against
    /// this phone's own store instead of pulling it over the radio would still hand back a perfectly
    /// plausible image. The author's tag is written into the bytes, so a lookalike fails.
    /// </remarks>
    [Fact]
    public async Task The_picture_that_arrives_is_the_authors_own()
    {
        var (a, b) = await TwoLinkedPhonesAsync();

        // Both sides hold one, so "found a picture" is not the same as "found theirs".
        await PublishWithPictureAsync(a);
        await PublishWithPictureAsync(b);

        var page = await a.Web.OpenAsync(b.Web.Address("shop"));
        var image = Assert.Single(page.Card!.Blocks, x => x.Kind == CardBlock.Image);

        string? uri = null;
        await EventuallyAsync(() => (uri = a.Web.AssetAsync(image.ContentHash).GetAwaiter().GetResult()) is not null);

        var bytes = Convert.FromBase64String(uri!.Split(',')[1]);
        var inside = Encoding.UTF8.GetString(bytes);

        Assert.Contains(b.Tag, inside, StringComparison.Ordinal);
        Assert.DoesNotContain(a.Tag, inside, StringComparison.Ordinal);
    }

    /// <summary>Give this phone a page with a picture only it could have made.</summary>
    private static async Task PublishWithPictureAsync(Device phone)
    {
        var hash = await phone.Web.KeepPictureAsync(Marked(phone.Tag), "image/jpeg");

        phone.Web.Mine.Save(new WebCard
        {
            Name = "shop",
            Doc = new CardDocument
            {
                Title = "The shop",
                Blocks = [new CardBlock { Kind = CardBlock.Image, ContentHash = hash, Value = "The van" }],
            },
        });

        await phone.Web.PublishAsync("shop");
    }

    /// <summary>A JPEG with the author's tag written inside it.</summary>
    /// <remarks>
    /// Real enough to pass the checks a picture goes through, and distinctive enough that a picture
    /// fetched from the wrong phone is obviously the wrong picture.
    /// </remarks>
    private static byte[] Marked(string tag)
    {
        var mark = Encoding.UTF8.GetBytes(tag);
        var picture = new byte[2048];

        picture[0] = 0xFF;
        picture[1] = 0xD8;
        picture[2] = 0xFF;
        mark.CopyTo(picture, 8);

        for (var i = 8 + mark.Length; i < picture.Length; i++) picture[i] = (byte)(i * 31 % 251);

        return picture;
    }

    // ── Held, then served offline ─────────────────────────────────────────────

    [Fact]
    public async Task A_fetched_card_is_kept()
    {
        var (a, b) = await TwoLinkedPhonesAsync();

        await a.Web.OpenAsync(b.Web.HomeAddress);

        Assert.Contains(a.Web.Deck.All, c => c.AuthorTag == b.Tag);
    }

    /// <summary>
    /// The property that makes it a card and not a page: once you hold it, losing the radio — and
    /// the author — does not take it away from you.
    /// </summary>
    [Fact]
    public async Task A_kept_card_still_opens_after_the_radio_goes_away()
    {
        var (a, b) = await TwoLinkedPhonesAsync();
        var address = b.Web.HomeAddress;
        await a.Web.OpenAsync(address);

        a.Radio.Unlink();
        a.Radio.Peer = null;

        var page = await a.Web.OpenAsync(address);

        Assert.True(page.Ok, page.Error);
    }

    [Fact]
    public async Task A_kept_card_still_names_its_original_author()
    {
        var (a, b) = await TwoLinkedPhonesAsync();
        var address = b.Web.HomeAddress;
        await a.Web.OpenAsync(address);

        a.Radio.Unlink();
        a.Radio.Peer = null;

        var page = await a.Web.OpenAsync(address);

        Assert.Equal(b.Tag, page.AuthorTag);
    }

    // ── Hold-and-forward: spread decoupled from origin ────────────────────────

    /// <summary>
    /// §2's baseball-card property. C never meets B, and B is gone by the time C asks — but A holds
    /// B's card and can serve it, and C can still verify it because the card is signed and
    /// content-addressed rather than trusted because of who handed it over.
    /// </summary>
    [Fact]
    public async Task A_third_phone_can_be_served_a_card_by_someone_who_did_not_write_it()
    {
        var (a, b) = await TwoLinkedPhonesAsync();
        var bsCard = b.Web.HomeAddress;
        await a.Web.OpenAsync(bsCard);          // A now holds B's card

        b.Radio.Unlink();                        // B leaves entirely
        b.Radio.Peer = null;
        a.Radio.Peer = null;

        var c = new Device();                    // C has never met B
        c.Radio.Peer = a.Radio;
        a.Radio.Peer = c.Radio;
        c.Radio.Link();
        a.Radio.Link();
        await c.Web.EnsureReadyAsync();

        var page = await c.Web.OpenAsync(bsCard);

        Assert.True(page.Ok,
            $"a held card could not be re-served to a phone that never met its author: {page.Error}");
    }

    [Fact]
    public async Task A_re_served_card_still_names_its_original_author()
    {
        var (a, b) = await TwoLinkedPhonesAsync();
        var bsCard = b.Web.HomeAddress;
        await a.Web.OpenAsync(bsCard);

        b.Radio.Unlink();
        b.Radio.Peer = null;
        a.Radio.Peer = null;

        var c = new Device();
        c.Radio.Peer = a.Radio;
        a.Radio.Peer = c.Radio;
        c.Radio.Link();
        a.Radio.Link();
        await c.Web.EnsureReadyAsync();

        var page = await c.Web.OpenAsync(bsCard);

        Assert.Equal(b.Tag, page.AuthorTag);
    }
}
