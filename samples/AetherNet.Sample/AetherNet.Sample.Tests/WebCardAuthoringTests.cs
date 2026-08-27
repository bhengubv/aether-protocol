// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Content;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Authoring web cards: the pages a person writes on their phone and their phone then hosts.
///
/// <para>
/// A person's AetherTag is a domain nobody issued, and each page is a path under it. That makes the
/// author and the host the same device, which is the whole proposition — and it puts three failures
/// within reach that all look like nothing going wrong.
/// </para>
///
/// <para>
/// <b>A publish that is not newer is a publish that never happened.</b> The directory refuses a binding
/// that does not raise the version, and it refuses it silently: the author sees their edit, every
/// reader keeps the old page, and nothing anywhere reports a problem.
/// </para>
///
/// <para>
/// <b>A template that pre-fills facts publishes lies.</b> Most people will not edit what they are given,
/// so a starting page carrying invented opening hours is a network of shops with invented opening
/// hours. Structure may be supplied; claims may not.
/// </para>
///
/// <para>
/// <b>A tip jar is where a card names money.</b> It is the one block pointing outside the mesh, so the
/// address is the one string on a card whose exact spelling decides who gets paid.
/// </para>
/// </summary>
public class WebCardAuthoringTests
{
    private static MyPages APhone() => new(AetherStore.InMemory());

    private static (MeshWebService Service, MyPages Pages) ADevice()
    {
        var me = FakeIdentity.Unique();
        var pages = APhone();
        return (new MeshWebService(me, me.Node, new InMemoryContentStore(), null, null, pages), pages);
    }

    // ── Addresses somebody has to be able to say out loud ──────────────────────

    [Theory]
    [InlineData("Urban Plumbers", "urban-plumbers")]
    [InlineData("  My Links  ", "my-links")]
    [InlineData("ME", "me")]
    [InlineData("my_links", "my-links")]
    [InlineData("shop!!!", "shop")]
    [InlineData("--shop--", "shop")]
    [InlineData("a  b", "a-b")]
    public void A_page_name_becomes_something_that_can_be_an_address(string typed, string expected) =>
        Assert.Equal(expected, MyPages.Clean(typed));

    [Fact]
    public void A_page_name_that_is_only_punctuation_is_not_a_name() =>
        Assert.Equal("", MyPages.Clean("!!! ??? ..."));

    [Fact]
    public void A_page_name_is_short_enough_to_be_read_across_a_table() =>
        Assert.True(MyPages.Clean(new string('a', 200)).Length <= MyPages.LongestName);

    /// <summary>
    /// Two pages at one address is one page nobody can reach. The second name has to differ, and it
    /// still has to fit — a suffix that pushes past the limit produces a name the store then trims
    /// back into a collision.
    /// </summary>
    [Fact]
    public void A_second_page_wanting_a_taken_name_gets_a_free_one()
    {
        var mine = APhone();
        mine.Save(new WebCard { Name = "shop" });

        var free = mine.Free("shop");

        Assert.NotEqual("shop", free);
        Assert.False(mine.Has(free));
        Assert.True(free.Length <= MyPages.LongestName);
    }

    [Fact]
    public void A_free_name_for_a_very_long_wish_still_fits()
    {
        var mine = APhone();
        var wanted = new string('a', MyPages.LongestName);
        mine.Save(new WebCard { Name = wanted });

        var free = mine.Free(wanted);

        Assert.True(free.Length <= MyPages.LongestName, free);
        Assert.False(mine.Has(free));
    }

    // ── Keeping what somebody wrote ────────────────────────────────────────────

    [Fact]
    public void A_page_is_still_there_after_the_app_is_closed()
    {
        using var store = AetherStore.InMemory();
        new MyPages(store).Save(new WebCard { Name = "shop", Doc = new CardDocument { Title = "The shop" } });

        var later = new MyPages(store);

        Assert.Equal("The shop", later.Get("shop")!.Doc.Title);
    }

    [Fact]
    public void Saving_a_page_of_the_same_name_replaces_it_rather_than_adding_a_second()
    {
        var mine = APhone();
        mine.Save(new WebCard { Name = "shop", Doc = new CardDocument { Title = "First" } });
        mine.Save(new WebCard { Name = "shop", Doc = new CardDocument { Title = "Second" } });

        Assert.Single(mine.All);
        Assert.Equal("Second", mine.Get("shop")!.Doc.Title);
    }

    /// <summary>
    /// Editing is not publishing. A page that reports itself live while the copy on the mesh is the
    /// old one is the single lie a publishing tool must not tell — the author stops editing because
    /// they believe they are done.
    /// </summary>
    [Fact]
    public void Editing_a_published_page_puts_it_back_to_a_draft()
    {
        var mine = APhone();
        mine.Save(new WebCard { Name = "shop" });
        mine.WentLive("shop", 1);

        mine.Save(new WebCard { Name = "shop", Doc = new CardDocument { Title = "Changed" } });

        Assert.False(mine.Get("shop")!.Live);
    }

    [Fact]
    public void A_publish_is_always_newer_than_what_stands()
    {
        var mine = APhone();
        mine.Save(new WebCard { Name = "shop" });
        mine.WentLive("shop", mine.NextVersion("shop"));
        var first = mine.Get("shop")!.Version;

        mine.WentLive("shop", mine.NextVersion("shop"));

        Assert.True(mine.Get("shop")!.Version > first);
    }

    [Fact]
    public void A_device_offers_a_page_count_a_visitor_can_read_rather_than_a_sitemap()
    {
        var mine = APhone();

        for (var i = 0; i < MyPages.MostPages + 5; i++)
            mine.Save(new WebCard { Name = "page-" + i });

        Assert.Equal(MyPages.MostPages, mine.All.Count);
    }

    [Fact]
    public void A_page_can_be_taken_down()
    {
        var mine = APhone();
        mine.Save(new WebCard { Name = "shop" });

        Assert.True(mine.Remove("shop"));
        Assert.Null(mine.Get("shop"));
    }

    [Fact]
    public void Pages_can_be_arranged_so_the_author_decides_what_is_met_first()
    {
        var mine = APhone();
        mine.Save(new WebCard { Name = "one" });
        mine.Save(new WebCard { Name = "two" });

        mine.Move("two", -1);

        Assert.Equal("two", mine.All[0].Name);
    }

    /// <summary>
    /// This app kept a single card before a device hosted pages. Somebody upgrading should find what
    /// they wrote at their front door, not find it gone.
    /// </summary>
    [Fact]
    public void The_one_card_this_app_used_to_keep_becomes_the_front_door()
    {
        using var store = AetherStore.InMemory();
        store.SetSetting(OwnCard.Key, new CardDocument { Title = "Written before" }.ToJson());

        var mine = new MyPages(store);

        Assert.Equal("Written before", mine.Get(MyPages.Home)?.Doc.Title);
    }

    /// <summary>
    /// Unreadable text is not a reason to overwrite somebody's pages with nothing. Whatever is stored
    /// stays stored, and this launch simply starts empty.
    /// </summary>
    [Fact]
    public void Pages_that_cannot_be_read_are_left_where_they_are()
    {
        using var store = AetherStore.InMemory();
        store.SetSetting(MyPages.Key, "{ not json at all");

        var mine = new MyPages(store);

        Assert.Empty(mine.All);
        Assert.Equal("{ not json at all", store.GetSetting(MyPages.Key));
    }

    // ── Templates: a shape, never a claim ──────────────────────────────────────

    /// <summary>
    /// The test that keeps this network honest. A starting page may carry headings and the labels of
    /// facts — those are true whoever is writing — but never an answer, because most people publish
    /// what they were given.
    /// </summary>
    [Theory]
    [InlineData("me")]
    [InlineData("business")]
    [InlineData("links")]
    [InlineData("notice")]
    [InlineData("blank")]
    public void A_template_states_no_fact_on_the_authors_behalf(string key)
    {
        var card = PageTemplate.Of(key).Build("Thabang");

        foreach (var block in card.Blocks)
        {
            switch (block.Kind)
            {
                case CardBlock.Text:
                    Assert.True(string.IsNullOrWhiteSpace(block.Value), $"{key} puts words in their mouth");
                    break;

                case CardBlock.KeyValue:
                    var said = block.Value!.Split('=', 2);
                    Assert.True(said.Length == 2 && said[1].Trim().Length == 0,
                        $"{key} answers '{block.Value}' for them");
                    break;

                case CardBlock.List:
                    Assert.All(block.Items ?? [], line => Assert.True(string.IsNullOrWhiteSpace(line)));
                    break;

                case CardBlock.Tip:
                    Assert.True(string.IsNullOrWhiteSpace(block.Target), $"{key} names somebody's tip jar");
                    break;
            }
        }
    }

    [Fact]
    public void A_template_supplies_the_shape_so_there_is_something_to_fill_in()
    {
        var card = PageTemplate.Of("business").Build(null);

        Assert.Contains(card.Blocks, b => b.Kind == CardBlock.Heading && !string.IsNullOrWhiteSpace(b.Value));
        Assert.Contains(card.Blocks, b => b.Kind == CardBlock.KeyValue);
    }

    [Fact]
    public void A_template_arrives_wearing_a_look()
    {
        foreach (var template in PageTemplate.All)
            Assert.True(CardLook.IsLook(CardLook.FromCard(template.Build(null)).Key));
    }

    [Fact]
    public void A_template_this_build_does_not_know_falls_back_rather_than_failing() =>
        Assert.NotNull(PageTemplate.Of("something-from-a-newer-app").Build(null));

    // ── What actually goes on the mesh ─────────────────────────────────────────

    [Fact]
    public void The_blanks_a_template_left_are_not_carried_across_the_radio()
    {
        var card = PageTemplate.Of("business").Build(null);

        var sent = OwnCard.ForPublish(card);

        Assert.DoesNotContain(sent.Blocks, b => b.Kind == CardBlock.Text && string.IsNullOrWhiteSpace(b.Value));
        Assert.DoesNotContain(sent.Blocks, b => b.Kind == CardBlock.KeyValue);
        Assert.DoesNotContain(sent.Blocks, b => b.Kind == CardBlock.List);
    }

    [Fact]
    public void A_labelled_fact_with_no_answer_is_not_published()
    {
        var card = new CardDocument { Blocks = [CardBlock.Of(CardBlock.KeyValue, "Open =")] };

        Assert.DoesNotContain(OwnCard.ForPublish(card).Blocks, b => b.Kind == CardBlock.KeyValue);
    }

    [Fact]
    public void A_labelled_fact_that_was_answered_is_published()
    {
        var card = new CardDocument { Blocks = [CardBlock.Of(CardBlock.KeyValue, "Open = Mon to Sat")] };

        Assert.Contains(OwnCard.ForPublish(card).Blocks, b => b.Kind == CardBlock.KeyValue);
    }

    /// <summary>
    /// Stripping blanks is for the reader, not against the author. Somebody who half-filled a page and
    /// came back to it must find their scaffolding intact.
    /// </summary>
    [Fact]
    public void Publishing_does_not_take_the_scaffolding_away_from_the_author()
    {
        var card = PageTemplate.Of("business").Build(null);
        var before = card.Blocks.Count;

        OwnCard.ForPublish(card);

        Assert.Equal(before, card.Blocks.Count);
    }

    [Fact]
    public void An_empty_list_line_is_dropped_but_the_written_ones_survive()
    {
        var card = new CardDocument
        {
            Blocks = [new CardBlock { Kind = CardBlock.List, Items = ["Bread", "", "  ", "Milk"] }],
        };

        var list = Assert.Single(OwnCard.ForPublish(card).Blocks, b => b.Kind == CardBlock.List);

        Assert.Equal(["Bread", "Milk"], list.Items);
    }

    /// <summary>
    /// A list being typed into must keep its blank rows, or the row somebody is about to write in
    /// vanishes underneath them on the keystroke before.
    /// </summary>
    [Fact]
    public void A_list_being_edited_keeps_the_line_somebody_is_about_to_type_into()
    {
        var card = new CardDocument
        {
            Blocks = [new CardBlock { Kind = CardBlock.List, Items = ["Bread", ""] }],
        };

        OwnCard.Tidy(card);

        Assert.Equal(2, card.Blocks.Single(b => b.Kind == CardBlock.List).Items!.Count);
    }

    // ── The tip jar ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://buymeacoffee.com/thabang")]
    [InlineData("https://ko-fi.com/someone")]
    [InlineData("https://pay.example.co.za/tip?to=me")]
    public void A_tip_jar_may_be_any_https_address(string address) =>
        Assert.True(CardBlock.IsUsableTip(address));

    /// <summary>
    /// The plain-text one matters most: this is where a card names money, and an unencrypted jar is
    /// anybody on the same network rewriting where the money goes.
    /// </summary>
    [Theory]
    [InlineData("http://buymeacoffee.com/thabang")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("buymeacoffee.com/thabang")]
    [InlineData("https://")]
    [InlineData("https://nodot")]
    [InlineData("https://.example.com")]
    [InlineData("https://example.com./x")]
    [InlineData("https://exa mple.com")]
    [InlineData("")]
    [InlineData(null)]
    public void A_tip_jar_that_is_not_an_https_address_is_refused(string? address) =>
        Assert.False(CardBlock.IsUsableTip(address));

    /// <summary>
    /// The oldest way to make a familiar name point at a stranger's wallet: everything before the @ is
    /// a credential, and the host is whatever comes after it.
    /// </summary>
    [Fact]
    public void A_tip_jar_wearing_a_familiar_name_as_a_credential_is_refused() =>
        Assert.False(CardBlock.IsUsableTip("https://buymeacoffee.com@example.invalid/thabang"));

    [Fact]
    public void A_reader_is_told_where_the_money_actually_goes() =>
        Assert.Equal("buymeacoffee.com", CardBlock.TipHost("https://buymeacoffee.com/thabang?x=1"));

    [Fact]
    public void A_tip_jar_nobody_filled_in_is_not_published()
    {
        var card = new CardDocument
        {
            Blocks = [new CardBlock { Kind = CardBlock.Tip, Value = "Buy me a coffee", Target = "" }],
        };

        Assert.DoesNotContain(OwnCard.ForPublish(card).Blocks, b => b.Kind == CardBlock.Tip);
    }

    [Fact]
    public void A_tip_jar_we_would_refuse_to_show_is_not_published()
    {
        var card = new CardDocument
        {
            Blocks =
            [
                new CardBlock { Kind = CardBlock.Tip, Value = "Tip", Target = "http://example.com/x" },
            ],
        };

        Assert.DoesNotContain(OwnCard.ForPublish(card).Blocks, b => b.Kind == CardBlock.Tip);
    }

    // ── The same document, drawn as a page or as an offer ──────────────────────

    /// <summary>
    /// One renderer, two occasions. Handed to somebody with nothing installed it is an offer, and the
    /// eyebrow and the button belong. Browsed to on AetherNet it is a page — telling a reader who went
    /// looking for it that it was "shared with you" is a small untruth printed above somebody's name,
    /// and offering an app to a phone that is already running it is worse.
    /// </summary>
    [Fact]
    public void A_page_nobody_is_being_handed_carries_no_offer()
    {
        var card = new CardDocument { Title = "Mama Dlamini" };

        var page = CardPage.Render(card, card.Title, 0, downloadPath: null);

        Assert.DoesNotContain("Get Aether", page);
        Assert.DoesNotContain("Shared with you", page);
        Assert.Contains("Mama Dlamini", page);
    }

    [Fact]
    public void The_same_card_handed_over_does_carry_the_offer()
    {
        var card = new CardDocument { Title = "Mama Dlamini" };

        var page = CardPage.Render(card, card.Title, 51 * 1024 * 1024, "/app.apk");

        Assert.Contains("Get Aether", page);
        Assert.Contains("Shared with you", page);
    }

    /// <summary>
    /// An unnamed page is shorter, not somebody called "Someone next to you".
    /// </summary>
    /// <remarks>
    /// That phrase describes where a person is standing, which is true at the moment they are handing
    /// over a phone and false everywhere else. Printed above a browsed page it replaces an author's
    /// name with a stranger's location.
    /// </remarks>
    [Fact]
    public void An_untitled_page_invents_nobody()
    {
        var page = CardPage.Render(new CardDocument(), who: "", 0, downloadPath: null);

        Assert.DoesNotContain("Someone next to you", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void An_untitled_offer_still_says_who_is_offering()
    {
        var page = CardPage.Render(new CardDocument(), who: "", 0, "/app.apk");

        Assert.Contains("Someone next to you", page, StringComparison.Ordinal);
    }

    // ── Pictures that travel ───────────────────────────────────────────────────

    /// <summary>A JPEG, as far as anything that inspects bytes is concerned.</summary>
    private static byte[] AJpeg(int bytes = 2048)
    {
        var picture = new byte[bytes];
        picture[0] = 0xFF;
        picture[1] = 0xD8;
        picture[2] = 0xFF;

        for (var i = 3; i < bytes; i++) picture[i] = (byte)(i * 31 % 251);

        return picture;
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public void The_kinds_of_picture_a_page_carries_are_ones_every_phone_draws(string mime) =>
        Assert.True(PagePhoto.IsUsable(mime));

    /// <summary>
    /// SVG is refused, and not for want of support.
    /// </summary>
    /// <remarks>
    /// An SVG is a document that can carry script and can fetch. Accepting one from a person and
    /// serving it to strangers would put an executable back inside the one thing on this network that
    /// has to stay inert — after the card model went to the trouble of being JSON precisely so that it
    /// could not.
    /// </remarks>
    [Fact]
    public void A_picture_that_is_really_a_document_is_refused() =>
        Assert.False(PagePhoto.IsUsable("image/svg+xml"));

    /// <summary>
    /// The type is a claim made across a JavaScript call, so the bytes are asked instead.
    /// </summary>
    [Fact]
    public void Bytes_that_are_not_what_they_say_they_are_are_refused()
    {
        var notAJpeg = new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x20 };

        Assert.False(PagePhoto.IsUsable("image/jpeg", notAJpeg));
        Assert.True(PagePhoto.IsUsable("image/jpeg", AJpeg()));
    }

    /// <summary>
    /// The budget is set by the slowest radio, not the fastest.
    /// </summary>
    /// <remarks>
    /// A picture that arrives instantly over Wi-Fi Direct and never arrives at all over Bluetooth is a
    /// page that works in a demo and fails on a street.
    /// </remarks>
    [Fact]
    public void A_picture_bigger_than_the_budget_is_refused() =>
        Assert.False(PagePhoto.IsUsable("image/jpeg", AJpeg(PagePhoto.MostBytes + 1)));

    [Fact]
    public void A_picture_inside_the_budget_is_kept() =>
        Assert.True(PagePhoto.IsUsable("image/jpeg", AJpeg(PagePhoto.MostBytes)));

    /// <summary>
    /// A photograph is the subject; a drawing is a backdrop. The masthead turns on the difference, and
    /// it is read from the content type rather than from anything a card declares about itself.
    /// </summary>
    [Fact]
    public void A_photograph_and_a_drawing_are_told_apart_by_what_they_are()
    {
        Assert.True(PagePhoto.IsPhotograph("data:image/jpeg;base64,AAAA"));
        Assert.False(PagePhoto.IsPhotograph("data:image/svg+xml;base64,AAAA"));
        Assert.False(PagePhoto.IsPhotograph(null));
    }

    [Fact]
    public async Task A_picture_somebody_chose_is_kept_and_can_be_fetched_back()
    {
        var (service, _) = ADevice();
        await service.EnsureReadyAsync();

        var hash = await service.KeepPictureAsync(AJpeg(), "image/jpeg");

        Assert.NotNull(hash);
        Assert.True(CardBlock.IsUsableAssetHash(hash));
        Assert.StartsWith("data:image/jpeg;base64,", await service.AssetAsync(hash));
    }

    /// <summary>
    /// The bug this catches: every asset came back declared as SVG, because card art used to be the
    /// only kind there was. A photograph then arrived correctly, verified correctly, and drew as
    /// nothing at all.
    /// </summary>
    [Fact]
    public async Task A_picture_comes_back_as_what_it_actually_is()
    {
        var (service, _) = ADevice();
        await service.EnsureReadyAsync();

        var photograph = await service.KeepPictureAsync(AJpeg(), "image/jpeg");
        var page = await service.OpenAsync(service.HomeAddress);
        var drawing = page.Card!.Blocks.First(b => b.Kind == CardBlock.Image).ContentHash;

        Assert.StartsWith("data:image/jpeg;", await service.AssetAsync(photograph));
        Assert.StartsWith("data:image/svg+xml;", await service.AssetAsync(drawing));
    }

    [Fact]
    public async Task Bytes_we_would_not_carry_are_not_kept()
    {
        var (service, _) = ADevice();
        await service.EnsureReadyAsync();

        Assert.Null(await service.KeepPictureAsync(AJpeg(PagePhoto.MostBytes + 1), "image/jpeg"));
        Assert.Null(await service.KeepPictureAsync(AJpeg(), "image/svg+xml"));
        Assert.Null(await service.KeepPictureAsync([], "image/jpeg"));
    }

    /// <summary>
    /// A page whose author chose a picture does not also get ours. Two mastheads is a page arguing
    /// with itself, and the generated one exists only so that a page with no picture still has a face.
    /// </summary>
    [Fact]
    public async Task A_page_with_a_photograph_is_not_given_a_drawing_as_well()
    {
        var (service, mine) = ADevice();
        await service.EnsureReadyAsync();

        var hash = await service.KeepPictureAsync(AJpeg(), "image/jpeg");
        mine.Save(new WebCard
        {
            Name = "shop",
            Doc = new CardDocument
            {
                Title = "The shop",
                Blocks = [new CardBlock { Kind = CardBlock.Image, ContentHash = hash, Value = "The shop front" }],
            },
        });

        var page = await service.OpenAsync((await service.PublishAsync("shop"))!);
        var picture = Assert.Single(page.Card!.Blocks, b => b.Kind == CardBlock.Image);

        Assert.Equal(hash, picture.ContentHash);
    }

    /// <summary>
    /// The whole point: a picture crosses to a phone that has never seen it, verified against the
    /// descriptor rather than trusted from the sender.
    /// </summary>
    [Fact]
    public async Task A_picture_reaches_a_phone_that_never_had_it()
    {
        var mine = APhone();
        var me = FakeIdentity.Unique();
        var you = FakeIdentity.Unique();

        var hereRadio = new FakeRadioMesh(me.AetherTag);
        var thereRadio = new FakeRadioMesh(you.AetherTag);
        hereRadio.Peer = thereRadio;
        thereRadio.Peer = hereRadio;
        hereRadio.Link();
        thereRadio.Link();

        var here = new MeshWebService(me, me.Node, new InMemoryContentStore(), hereRadio, null, mine);
        await here.EnsureReadyAsync();

        var hash = await here.KeepPictureAsync(AJpeg(), "image/jpeg");
        mine.Save(new WebCard
        {
            Name = "shop",
            Doc = new CardDocument
            {
                Title = "The shop",
                Blocks = [new CardBlock { Kind = CardBlock.Image, ContentHash = hash, Value = "The shop front" }],
            },
        });
        await here.PublishAsync("shop");

        var there = new MeshWebService(you, you.Node, new InMemoryContentStore(), thereRadio, null, APhone());
        await there.EnsureReadyAsync();

        var page = await there.OpenAsync(here.Address("shop"));

        Assert.True(page.Ok, page.Error);
        Assert.True(page.Remote);

        var named = Assert.Single(page.Card!.Blocks, b => b.Kind == CardBlock.Image);
        Assert.Equal(hash, named.ContentHash);
        Assert.StartsWith("data:image/jpeg;base64,", await there.AssetAsync(named.ContentHash));
    }

    /// <summary>
    /// A picture nobody chose is not published as an empty frame.
    /// </summary>
    [Fact]
    public void A_picture_block_with_no_picture_in_it_is_not_published()
    {
        var card = new CardDocument
        {
            Blocks = [new CardBlock { Kind = CardBlock.Image, Value = "The shop front" }],
        };

        Assert.DoesNotContain(OwnCard.ForPublish(card).Blocks, b => b.Kind == CardBlock.Image);
    }

    [Fact]
    public void A_picture_is_something_somebody_can_add_to_a_page() =>
        Assert.Contains(CardBlock.Image, OwnCard.Writable);

    /// <summary>
    /// An author is told what they are asking of whoever opens the page. It is the one cost of
    /// publishing that is paid entirely by other people.
    /// </summary>
    [Fact]
    public void The_cost_of_a_picture_is_stated_in_time_somebody_else_spends()
    {
        var said = PagePhoto.OverSlowLink(PagePhoto.MostBytes);

        Assert.Contains("Bluetooth", said, StringComparison.Ordinal);
        Assert.Contains("minute", said, StringComparison.Ordinal);
    }

    // ── The masthead ───────────────────────────────────────────────────────────

    [Fact]
    public void A_pages_picture_is_painted_in_its_own_colour() =>
        Assert.Contains("#7a4a1e", PageArt.Svg("The shop", "TAG/shop", "#7a4a1e"));

    /// <summary>
    /// The title is typed by a person and this drawing is served to strangers — the same problem the
    /// card renderer has, in a file nobody thinks of as a renderer.
    /// </summary>
    [Fact]
    public void A_pages_picture_cannot_carry_markup_out_of_somebodys_title()
    {
        var art = PageArt.Svg("</text><script>alert(1)</script>", "TAG/x", "#1c1c1a");

        Assert.DoesNotContain("<script>", art);
        Assert.Contains("&lt;script&gt;", art);
    }

    [Fact]
    public void A_page_with_no_title_yet_still_gets_a_picture_rather_than_a_blank()
    {
        var art = PageArt.Svg("", "Y6TK9-EW9KK/me", "#1c1c1a");

        Assert.Contains("<svg", art);
        Assert.Contains("Y6TK9", art);
    }

    // ── Publishing, end to end ─────────────────────────────────────────────────

    [Fact]
    public async Task A_page_somebody_wrote_is_what_the_device_hosts()
    {
        var (service, mine) = ADevice();
        await service.EnsureReadyAsync();

        mine.Save(new WebCard { Name = "shop", Doc = new CardDocument { Title = "Mama Dlamini" } });
        var address = await service.PublishAsync("shop");

        var page = await service.OpenAsync(address!);

        Assert.True(page.Ok, page.Error);
        Assert.Equal("Mama Dlamini", page.Card!.Title);
    }

    [Fact]
    public async Task Publishing_marks_the_page_live_at_a_newer_version()
    {
        var (service, mine) = ADevice();
        await service.EnsureReadyAsync();
        mine.Save(new WebCard { Name = "shop" });

        await service.PublishAsync("shop");
        var first = mine.Get("shop")!.Version;
        await service.PublishAsync("shop");

        Assert.True(mine.Get("shop")!.Live);
        Assert.True(mine.Get("shop")!.Version > first, "a re-publish that is not newer reaches nobody");
    }

    /// <summary>
    /// A look is a key this build understands; an accent is a colour any build understands. A page
    /// published with only the first opens in somebody else's app wearing the app's own colour, and
    /// the author never sees it happen.
    /// </summary>
    [Fact]
    public async Task A_published_page_carries_the_plain_colour_of_the_look_it_wears()
    {
        var (service, mine) = ADevice();
        await service.EnsureReadyAsync();

        var doc = new CardDocument { Title = "Reading" };
        OwnCard.SetLook(doc, "editorial");
        mine.Save(new WebCard { Name = "reading", Doc = doc });

        var page = await service.OpenAsync((await service.PublishAsync("reading"))!);

        Assert.Contains(page.Card!.Blocks, b => b.Kind == CardBlock.Theme && CardBlock.IsUsableAccent(b.Value));
        Assert.Contains(page.Card.Blocks, b => b.Kind == CardBlock.Theme && b.Value == "editorial");
    }

    [Fact]
    public async Task A_published_page_leads_with_a_picture_nobody_had_to_supply()
    {
        var (service, mine) = ADevice();
        await service.EnsureReadyAsync();
        mine.Save(new WebCard { Name = "shop", Doc = new CardDocument { Title = "The shop" } });

        var page = await service.OpenAsync((await service.PublishAsync("shop"))!);
        var picture = Assert.Single(page.Card!.Blocks, b => b.Kind == CardBlock.Image);

        Assert.True(CardBlock.IsUsableAssetHash(picture.ContentHash));
        Assert.StartsWith("data:image/svg+xml;base64,", await service.AssetAsync(picture.ContentHash));
    }

    /// <summary>
    /// A draft stays a draft across a restart.
    /// </summary>
    /// <remarks>
    /// Every page standing on the mesh is republished on launch, because a directory binding lives in
    /// the memory of whichever phones heard it. Sweeping drafts up with them means somebody who opened
    /// the editor, got halfway and walked off finds a half-written page under their own tag — the app
    /// publishing on their behalf, which is the one thing a publishing tool must never do.
    /// </remarks>
    [Fact]
    public async Task A_draft_is_not_published_by_a_restart()
    {
        var (service, mine) = ADevice();
        mine.Save(new WebCard { Name = "half", Doc = new CardDocument { Title = "Half written" } });

        await service.EnsureReadyAsync();

        Assert.DoesNotContain("half", service.Pages);
        Assert.False(mine.Get("half")!.Live);
        Assert.False((await service.OpenAsync(service.Address("half"))).Ok);
    }

    /// <summary>
    /// But a page they did publish goes back up, and the front door always does.
    /// </summary>
    [Fact]
    public async Task What_was_published_is_published_again_on_the_next_launch()
    {
        var (service, mine) = ADevice();
        await service.EnsureReadyAsync();

        mine.Save(new WebCard { Name = "shop", Doc = new CardDocument { Title = "The shop" } });
        await service.PublishAsync("shop");

        var second = new MeshWebService(
            FakeIdentity.Unique(), FakeIdentity.Unique().Node, new InMemoryContentStore(), null, null, mine);
        await second.EnsureReadyAsync();

        Assert.Contains("shop", second.Pages);
        Assert.Contains(MyPages.Home, second.Pages);
    }

    [Fact]
    public async Task Asking_to_publish_a_page_that_does_not_exist_answers_nothing()
    {
        var (service, _) = ADevice();
        await service.EnsureReadyAsync();

        Assert.Null(await service.PublishAsync("never-written"));
    }

    /// <summary>
    /// The whole point of authoring on the device: what a person typed is what a stranger's phone
    /// draws, with nothing in between that could have rewritten it.
    /// </summary>
    [Fact]
    public async Task What_was_typed_is_what_the_reader_gets()
    {
        var (service, mine) = ADevice();
        await service.EnsureReadyAsync();

        mine.Save(new WebCard
        {
            Name = "shop",
            Doc = new CardDocument
            {
                Title = "Mama Dlamini",
                Blocks =
                [
                    CardBlock.Of(CardBlock.Heading, "Hours"),
                    CardBlock.Of(CardBlock.KeyValue, "Open = 06:00 to 20:00"),
                    new CardBlock { Kind = CardBlock.List, Items = ["Bread", "Milk", ""] },
                ],
            },
        });

        var page = await service.OpenAsync((await service.PublishAsync("shop"))!);
        var drawn = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(page.Card!.ToJson()));

        Assert.Contains("Mama Dlamini", drawn);
        Assert.Contains("06:00 to 20:00", drawn);
        Assert.Contains("Bread", drawn);
    }
}
