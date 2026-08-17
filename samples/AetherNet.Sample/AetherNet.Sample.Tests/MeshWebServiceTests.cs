// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Content;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

public class MeshWebServiceTests
{
    // Each service registers itself by tag, so every test gets its own device.
    private static (MeshWebService Service, string Tag) ADevice(FakeRadioMesh? radio = null)
    {
        var me = FakeIdentity.Unique();
        return (new MeshWebService(me, me.Node, new InMemoryContentStore(), radio), me.AetherTag);
    }

    private static MeshWebService AService(FakeRadioMesh? radio = null) => ADevice(radio).Service;

    // ── Addressing ────────────────────────────────────────────────────────────

    [Fact]
    public void Address_uses_the_aether_scheme()
    {
        var service = AService();

        Assert.StartsWith("aether://", service.Address("home"));
    }

    [Fact]
    public async Task Address_is_scoped_to_this_device()
    {
        var (service, tag) = ADevice();
        await service.EnsureReadyAsync();

        Assert.Contains(tag, service.Address("home"));
    }

    [Fact]
    public async Task HomeAddress_is_the_home_card()
    {
        var service = AService();
        await service.EnsureReadyAsync();

        Assert.Equal(service.Address("home"), service.HomeAddress);
    }

    // ── Hosting your own card ─────────────────────────────────────────────────

    [Fact]
    public async Task EnsureReadyAsync_publishes_at_least_one_card()
    {
        var service = AService();

        await service.EnsureReadyAsync();

        Assert.NotEmpty(service.Pages);
    }

    [Fact]
    public async Task EnsureReadyAsync_is_safe_to_call_twice()
    {
        var service = AService();

        await service.EnsureReadyAsync();
        var first = service.Pages.Count;
        await service.EnsureReadyAsync();

        Assert.Equal(first, service.Pages.Count);
    }

    [Fact]
    public async Task A_device_serves_its_own_card_with_no_radio_and_no_peer()
    {
        // The point of the mesh-web: your card is on your phone, not on a server.
        var service = AService();
        await service.EnsureReadyAsync();

        var page = await service.OpenAsync(service.HomeAddress);

        Assert.True(page.Ok, page.Error);
    }

    [Fact]
    public async Task An_own_card_is_marked_as_yours()
    {
        var service = AService();
        await service.EnsureReadyAsync();

        var page = await service.OpenAsync(service.HomeAddress);

        Assert.True(page.Own);
        Assert.False(page.Remote);
    }

    [Fact]
    public async Task An_own_card_names_its_author()
    {
        var (service, tag) = ADevice();
        await service.EnsureReadyAsync();

        var page = await service.OpenAsync(service.HomeAddress);

        Assert.Equal(tag, page.AuthorTag);
    }

    [Fact]
    public async Task An_own_card_is_content_addressed()
    {
        // Content-addressing is what lets a third phone verify a card it was handed by someone
        // who is not the author.
        var service = AService();
        await service.EnsureReadyAsync();

        var page = await service.OpenAsync(service.HomeAddress);

        Assert.False(string.IsNullOrWhiteSpace(page.RootHash));
    }

    // ── A card that looks like a place ────────────────────────────────────────

    /// <summary>
    /// Every page a device hosts declares its own accent. Without it a shop's price list comes out in
    /// the app's default blue while its front page is terracotta — the same author, two places.
    /// </summary>
    [Fact]
    public async Task Every_page_a_device_hosts_carries_a_usable_accent()
    {
        var service = AService();
        await service.EnsureReadyAsync();

        foreach (var name in service.Pages)
        {
            var page = await service.OpenAsync(service.Address(name));
            var accent = page.Card!.Blocks.SingleOrDefault(b => b.Kind == CardBlock.Theme);

            Assert.True(accent is not null, $"{name} declares no accent");
            Assert.True(CardBlock.IsUsableAccent(accent!.Value), $"{name} accent '{accent.Value}' is refused");
        }
    }

    [Fact]
    public async Task A_home_page_leads_with_a_picture()
    {
        var service = AService();
        await service.EnsureReadyAsync();

        var page = await service.OpenAsync(service.HomeAddress);

        Assert.Contains(page.Card!.Blocks, b => b.Kind == CardBlock.Image);
    }

    /// <summary>
    /// The artwork is published as content in its own right, and the card names it by hash — but the
    /// hash only exists once it has been published, so the persona is declared with a placeholder that
    /// is filled in at publish time. If that substitution ever stops happening the card still renders,
    /// silently, without its picture. This is the test that notices.
    /// </summary>
    [Fact]
    public async Task The_picture_a_card_names_can_actually_be_fetched()
    {
        var service = AService();
        await service.EnsureReadyAsync();
        var page = await service.OpenAsync(service.HomeAddress);
        var image = page.Card!.Blocks.First(b => b.Kind == CardBlock.Image);

        Assert.True(CardBlock.IsUsableAssetHash(image.ContentHash), $"'{image.ContentHash}' is not a hash");

        var uri = await service.AssetAsync(image.ContentHash);

        Assert.StartsWith("data:image/svg+xml;base64,", uri);
    }

    /// <summary>
    /// A card and its artwork are separate content, so nothing forces them to agree. They are drawn
    /// together, though — a green picture under a terracotta heading is a bug the reader sees.
    /// </summary>
    [Fact]
    public async Task The_picture_is_painted_in_the_cards_own_colour()
    {
        var service = AService();
        await service.EnsureReadyAsync();
        var page = await service.OpenAsync(service.HomeAddress);
        var accent = page.Card!.Blocks.Single(b => b.Kind == CardBlock.Theme).Value!;
        var image = page.Card.Blocks.First(b => b.Kind == CardBlock.Image);

        var svg = Encoding.UTF8.GetString(
            Convert.FromBase64String((await service.AssetAsync(image.ContentHash))!.Split(',')[1]));

        Assert.Contains(accent, svg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A picture must never be a place to go and fetch from. A hash names bytes the mesh can supply
    /// years later with the author gone; a URL is a card phoning home the moment a stranger opens it.
    /// </summary>
    [Fact]
    public async Task AssetAsync_refuses_anything_that_is_not_a_content_hash()
    {
        var service = AService();
        await service.EnsureReadyAsync();

        foreach (var notAHash in new[] { "http://example.com/a.png", "//example.com/a.png", "data:image/png;base64,AA", null })
            Assert.Null(await service.AssetAsync(notAHash));
    }

    /// <summary>
    /// A page a device hosts is a place on the mesh, and places link to each other. Every link must
    /// point at an address this same device actually serves — a card that can only point outward is a
    /// leaflet, not a site.
    /// </summary>
    [Fact]
    public async Task A_devices_pages_link_to_each_other_and_nowhere_else()
    {
        var (service, tag) = ADevice();
        await service.EnsureReadyAsync();
        var found = 0;

        foreach (var name in service.Pages)
        {
            var page = await service.OpenAsync(service.Address(name));
            foreach (var link in page.Card!.Blocks.Where(b => b.Kind == CardBlock.Link))
            {
                found++;
                Assert.StartsWith($"aether://{tag}/", link.Target);
                Assert.Contains(service.Pages, p => link.Target == service.Address(p));
            }
        }

        Assert.True(found > 0, "no page links anywhere");
    }

    // ── Addresses that go nowhere ─────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("https://example.com")]
    [InlineData("aether://KXJB7-MN2P4/does-not-exist")]   // a real-shaped tag nobody here owns
    public async Task OpenAsync_fails_cleanly_on_an_address_it_cannot_serve(string address)
    {
        var service = AService();
        await service.EnsureReadyAsync();

        var page = await service.OpenAsync(address);

        Assert.False(page.Ok);
        Assert.False(string.IsNullOrWhiteSpace(page.Error));
    }

    [Fact]
    public async Task OpenAsync_never_throws_on_rubbish()
    {
        var service = AService();

        var page = await service.OpenAsync("aether://!!!!!/@@@");

        Assert.False(page.Ok);
    }

    // ── Radio state is reported honestly ──────────────────────────────────────

    [Fact]
    public void RadioLinked_is_false_without_a_radio() =>
        Assert.False(AService().RadioLinked);

    [Fact]
    public void RadioAvailable_is_false_without_a_radio() =>
        Assert.False(AService().RadioAvailable);

    [Fact]
    public void RadioLinked_follows_the_radio()
    {
        var radio = new FakeRadioMesh("KXJB7-MN2P4");
        var service = AService(radio);
        Assert.False(service.RadioLinked);

        radio.Link();

        Assert.True(service.RadioLinked);
    }
}
