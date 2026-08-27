// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The card a person writes about themselves.
///
/// <para>
/// Every other card in this app describes a place or a thing. This one describes whoever is holding
/// the phone, and it is the page a stranger reads before deciding whether to accept anything from
/// them. That makes it the only card whose author and whose subject are the same person, and the only
/// one that has to be editable on a handset rather than authored somewhere else and published.
/// </para>
///
/// <para>
/// Kept in this device's own settings as the card's own JSON. Not a separate schema, not a table of
/// fields — the same <see cref="CardDocument"/> the mesh already carries, so what somebody edits here
/// is exactly what gets served and exactly what could be published later, with nothing to convert
/// between.
/// </para>
/// </summary>
public static class OwnCard
{
    /// <summary>Where it lives in this device's settings.</summary>
    public const string Key = "my_card";

    /// <summary>How many blocks one card may hold.</summary>
    /// <remarks>
    /// Twelve. Enough for a name, a few lines and a short list; few enough that the page a stranger
    /// reads stays a card rather than becoming a website they have to scroll. The limit exists for the
    /// reader, not for storage.
    /// </remarks>
    public const int MostBlocks = 12;

    /// <summary>The longest a single line of a card may be.</summary>
    public const int LongestValue = 280;

    /// <summary>The kinds somebody can add by hand, in the order the editor offers them.</summary>
    /// <remarks>
    /// Image is absent deliberately: a picture has to come from somewhere, and until the card's assets
    /// travel with it there is nothing honest to offer. Link is absent because a card link points
    /// inside the mesh, which the stranger reading this cannot reach yet.
    /// </remarks>
    public static readonly string[] Writable =
        [CardBlock.Heading, CardBlock.Text, CardBlock.List, CardBlock.KeyValue];

    /// <summary>What each kind is called on the button that adds it.</summary>
    public static string Label(string kind) => kind switch
    {
        CardBlock.Heading => "Heading",
        CardBlock.Text => "Words",
        CardBlock.List => "List",
        CardBlock.KeyValue => "Detail",
        CardBlock.Link => "Link",
        CardBlock.Image => "Picture",
        CardBlock.Theme => "Colour",
        _ => kind,
    };

    /// <summary>
    /// Read this device's own card, or build a first one from the name they already gave.
    /// </summary>
    /// <remarks>
    /// Never returns null. Somebody who has written nothing still has a card — their name and a
    /// palette — because the alternative is a stranger being shown an empty page at the one moment
    /// this network is making its case.
    /// </remarks>
    public static CardDocument Load(string? stored, string? name)
    {
        if (!string.IsNullOrWhiteSpace(stored) && CardDocument.Parse(stored) is { } saved)
            return Tidy(saved);

        return new CardDocument
        {
            Title = MyName.Clean(name),
            Blocks = [CardBlock.Of(CardBlock.Theme, CardLook.DefaultKey)],
        };
    }

    /// <summary>
    /// Bring a card back inside its limits before it is saved or served.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied on the way in as well as the way out. A card can arrive from storage written by a newer
    /// version of this app, by hand, or by an editor that had a bug — and the renderer is the wrong
    /// place to discover that, because by then a stranger is already looking at it.
    /// </para>
    /// <para>
    /// Trimming rather than rejecting: somebody who typed too much should lose the overflow, not the
    /// card.
    /// </para>
    /// </remarks>
    public static CardDocument Tidy(CardDocument card)
    {
        var blocks = new List<CardBlock>(MostBlocks);
        var themed = false;

        foreach (var block in card.Blocks ?? [])
        {
            if (blocks.Count >= MostBlocks) break;

            // One theme block. A second is not a second opinion, it is the first one being overruled
            // by whichever the renderer happens to find first.
            if (block.Kind == CardBlock.Theme && CardLook.IsLook(block.Value))
            {
                if (themed) continue;
                themed = true;
            }

            if (block.Value is { Length: > LongestValue })
                block.Value = block.Value[..LongestValue];

            if (block.Items is { Count: > 0 } items)
                block.Items = [.. items.Where(i => !string.IsNullOrWhiteSpace(i))
                                       .Select(i => i.Length > LongestValue ? i[..LongestValue] : i)
                                       .Take(MostBlocks)];

            blocks.Add(block);
        }

        if (!themed && blocks.Count < MostBlocks)
            blocks.Insert(0, CardBlock.Of(CardBlock.Theme, CardLook.DefaultKey));

        card.Blocks = blocks;
        card.Title = MyName.Clean(card.Title);
        return card;
    }

    /// <summary>
    /// Choose the look, replacing whatever was chosen before.
    /// </summary>
    /// <remarks>
    /// A look is the whole design — type, colour, scale — so there is exactly one, and setting a new
    /// one removes the old rather than layering it. An accent colour, if somebody set one, survives:
    /// it is a smaller decision that sits inside whichever look is current.
    /// </remarks>
    public static void SetLook(CardDocument card, string look)
    {
        if (!CardLook.IsLook(look)) return;

        card.Blocks ??= [];
        card.Blocks.RemoveAll(b => b.Kind == CardBlock.Theme && CardLook.IsLook(b.Value));
        card.Blocks.Insert(0, CardBlock.Of(CardBlock.Theme, look.Trim().ToLowerInvariant()));
    }

    /// <summary>Which look this card is wearing.</summary>
    public static CardLook LookOf(CardDocument card) => CardLook.FromCard(card);

    /// <summary>Add a block of the given kind, if there is room.</summary>
    public static bool Add(CardDocument card, string kind)
    {
        card.Blocks ??= [];
        if (card.Blocks.Count >= MostBlocks) return false;
        if (!Writable.Contains(kind)) return false;

        card.Blocks.Add(kind == CardBlock.List
            ? new CardBlock { Kind = kind, Items = [""] }
            : CardBlock.Of(kind, ""));

        return true;
    }

    /// <summary>Move a block up or down, so a card can be arranged rather than retyped.</summary>
    public static void Move(CardDocument card, int index, int by)
    {
        if (card.Blocks is not { Count: > 1 } blocks) return;
        if (index < 0 || index >= blocks.Count) return;

        var to = index + by;
        if (to < 0 || to >= blocks.Count) return;

        (blocks[index], blocks[to]) = (blocks[to], blocks[index]);
    }

    /// <summary>Remove a block.</summary>
    public static void Remove(CardDocument card, int index)
    {
        if (card.Blocks is not { Count: > 0 } blocks) return;
        if (index < 0 || index >= blocks.Count) return;

        blocks.RemoveAt(index);
    }

    /// <summary>Whether this block is one a person edits, rather than one the app manages.</summary>
    public static bool IsEditable(CardBlock block) => Writable.Contains(block.Kind);
}
