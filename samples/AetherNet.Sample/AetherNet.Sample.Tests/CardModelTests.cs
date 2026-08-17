// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The card model from <c>02_REMAINING_WORK</c> §2, decided 2026-08-09, which the sample breaks.
///
/// <para>
/// A hosted page is a <b>card, not a page</b>: "a signed JSON blob, not HTML". Two properties make
/// that non-negotiable — it is authored by a stranger, so it must be <b>safe</b>; and it must render
/// on any device, offline, years later, with the author long gone, so it must be <b>portable</b>.
/// HTML surrenders both.
/// </para>
///
/// <para>
/// Expected to FAIL. The same document already names this defect in this repo: "the aether-protocol
/// sample renderer currently injects HTML (MarkupString) — an XSS / network-egress footgun. Migrate
/// it to the JSON block-renderer."
/// </para>
/// </summary>
public class CardModelTests
{
    private static (MeshWebService Service, string Tag) ADevice()
    {
        var me = FakeIdentity.Unique();
        return (new MeshWebService(me, me.Node, new InMemoryContentStore()), me.AetherTag);
    }

    /// <summary>§2: a card is "a versioned document = metadata + an ordered list of typed blocks".</summary>
    [Fact]
    public async Task A_card_is_delivered_as_blocks_not_as_markup()
    {
        var (service, _) = ADevice();
        await service.EnsureReadyAsync();

        var page = await service.OpenAsync(service.HomeAddress);

        Assert.Null(typeof(MeshWebService.MeshPage).GetProperty("Html"));
        Assert.NotNull(page);
    }

    /// <summary>
    /// §2: "drawn by one renderer we own → uniform, theme-aware, accessible, and inert (no code
    /// execution, no fetch)". A card carrying raw markup cannot be drawn inertly, because the markup
    /// itself is the instruction.
    /// </summary>
    [Fact]
    public async Task A_card_carries_no_markup()
    {
        var (service, _) = ADevice();
        await service.EnsureReadyAsync();

        var page = await service.OpenAsync(service.HomeAddress);
        var carried = typeof(MeshWebService.MeshPage).GetProperty("Html")?.GetValue(page) as string;

        Assert.True(carried is null, "a card is delivered as markup, so no renderer can draw it inertly");
    }

    /// <summary>
    /// §2: "every asset referenced by content-hash, never a URL — nothing phones home; bytes come
    /// from the mesh". A card that can name an off-mesh address can be made to fetch one.
    /// </summary>
    [Fact]
    public async Task A_card_names_no_address_outside_the_mesh()
    {
        var (service, _) = ADevice();
        await service.EnsureReadyAsync();

        var page = await service.OpenAsync(service.HomeAddress);
        var carried = typeof(MeshWebService.MeshPage).GetProperty("Html")?.GetValue(page) as string ?? "";

        Assert.DoesNotContain("http://", carried, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", carried, StringComparison.OrdinalIgnoreCase);
    }
}
