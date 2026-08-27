// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using System.Reflection;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Every kind of block a card may contain has to survive the journey.
///
/// <para>
/// A card is authored on one phone and drawn on another, by a renderer that skips block kinds it does
/// not recognise. That rule is what makes an old renderer safe against a newer card — but it also means
/// a kind the model defines and the renderer forgot disappears in silence. The author sees their card;
/// the reader sees a card with a hole in it and no error anywhere.
/// </para>
///
/// <para>
/// Found on 2026-08-15: <c>link</c> had been in the model from the start and was never drawn.
/// </para>
/// </summary>
public class CardBlockCoverageTests
{
    /// <summary>Every block kind the model declares, read from the constants themselves.</summary>
    public static TheoryData<string> EveryKind()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(CardBlock).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.IsLiteral && field.FieldType == typeof(string))
                data.Add((string)field.GetRawConstantValue()!);
        return data;
    }

    // ── Every kind round-trips ────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void A_block_of_any_kind_survives_being_written_and_read(string kind)
    {
        var card = new CardDocument { Title = "t", Blocks = [new CardBlock { Kind = kind, Value = "v" }] };

        var read = CardDocument.Parse(card.ToJson());

        Assert.Equal(kind, read!.Blocks.Single().Kind);
    }

    // ── The kinds that carry more than a string ───────────────────────────────

    [Fact]
    public void A_list_keeps_its_items()
    {
        var card = new CardDocument
        {
            Title = "t",
            Blocks = [new CardBlock { Kind = CardBlock.List, Items = ["one", "two"] }],
        };

        var read = CardDocument.Parse(card.ToJson());

        Assert.Equal(["one", "two"], read!.Blocks.Single().Items!);
    }

    /// <summary>
    /// A link needs somewhere to go. Carried as an <c>aether://</c> address like everything else —
    /// never an <c>http</c> URL, because a card that can reach the open web is a card that phones home.
    /// </summary>
    [Fact]
    public void A_link_keeps_where_it_points()
    {
        var card = new CardDocument
        {
            Title = "t",
            Blocks = [new CardBlock { Kind = CardBlock.Link, Value = "The spaza", Target = "aether://KXJB7-MN2P4/home" }],
        };

        var read = CardDocument.Parse(card.ToJson());

        Assert.Equal("aether://KXJB7-MN2P4/home", read!.Blocks.Single().Target);
    }

    [Fact]
    public void An_asset_is_referenced_by_content_hash_not_a_url()
    {
        var card = new CardDocument
        {
            Title = "t",
            Blocks = [new CardBlock { Kind = CardBlock.Text, ContentHash = "abc123" }],
        };

        var read = CardDocument.Parse(card.ToJson());

        Assert.Equal("abc123", read!.Blocks.Single().ContentHash);
    }

    // ── An unknown kind is skipped, never fatal ───────────────────────────────

    [Fact]
    public void A_kind_this_renderer_has_never_heard_of_does_not_break_the_card()
    {
        var json = """
            {"v":1,"title":"t","blocks":[{"k":"heading","t":"Still here"},{"k":"hologram","t":"???"}]}
            """;

        var read = CardDocument.Parse(json);

        Assert.NotNull(read);
        Assert.Equal(2, read!.Blocks.Count);
        Assert.Contains(read.Blocks, b => b.Kind == CardBlock.Heading);
    }
}
