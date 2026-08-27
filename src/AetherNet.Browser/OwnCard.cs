// SPDX-License-Identifier: MIT

namespace AetherNet.Browser;

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
    /// <para>
    /// Picture is here now that a page's assets travel with it — chosen on the phone, shrunk to
    /// something the slow radio can actually deliver, and named by content hash rather than by a place
    /// to go and fetch it from.
    /// </para>
    /// <para>
    /// Link is present now that a person hosts more than one page — it points at another page under
    /// their own tag, chosen from a list rather than typed, so a link can only ever go somewhere that
    /// exists and belongs to its author.
    /// </para>
    /// </remarks>
    public static readonly string[] Writable =
    [
        CardBlock.Heading, CardBlock.Text, CardBlock.List,
        CardBlock.KeyValue, CardBlock.Image, CardBlock.Link, CardBlock.Tip,
    ];

    /// <summary>The most lines one list block may hold.</summary>
    public const int MostItems = 12;

    /// <summary>What each kind is called on the button that adds it.</summary>
    public static string Label(string kind) => kind switch
    {
        CardBlock.Heading => "Heading",
        CardBlock.Text => "Words",
        CardBlock.List => "List",
        CardBlock.KeyValue => "Detail",
        CardBlock.Link => "Link to another page",
        CardBlock.Tip => "Tip jar",
        CardBlock.Image => "Picture",
        CardBlock.Theme => "Colour",
        _ => kind,
    };

    /// <summary>
    /// What the empty field says before anybody types in it.
    /// </summary>
    /// <remarks>
    /// Written as an example of the answer rather than a name for the question. "A few words about
    /// what you do" gets a sentence; "Words" gets a blank stare.
    /// </remarks>
    public static string Placeholder(string kind) => kind switch
    {
        CardBlock.Heading => "Hours",
        CardBlock.Text => "A few words about what you do",
        CardBlock.List => "One thing per line",
        CardBlock.KeyValue => "Open = Mon to Sat, 8 to 5",
        CardBlock.Link => "What the link says",
        CardBlock.Image => "What it is a picture of",
        CardBlock.Tip => "Buy me a coffee",
        _ => "",
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

            // Empty lines are kept here, not swept up. This runs on every keystroke of the editor, and
            // a list that loses its blank rows the moment somebody pauses is a list they cannot type
            // into. They are dropped at publish time instead — see ForPublish.
            if (block.Items is { Count: > 0 } items)
                block.Items = [.. items.Select(i => i.Length > LongestValue ? i[..LongestValue] : i)
                                       .Take(MostItems)];

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

    /// <summary>
    /// The document as it should go on the mesh: everything unfilled left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A page arrives from a template as a shape — headings written, values blank — so that somebody
    /// fills it in rather than starts from nothing. Those blanks are scaffolding for the author and
    /// noise for everybody else, and they would be signed, chunked and carried across a radio link to
    /// be skipped at the far end.
    /// </para>
    /// <para>
    /// A copy, not an edit in place. The author keeps their scaffolding; only the reader is spared it.
    /// </para>
    /// </remarks>
    public static CardDocument ForPublish(CardDocument card)
    {
        var blocks = new List<CardBlock>(card.Blocks?.Count ?? 0);

        foreach (var block in card.Blocks ?? [])
        {
            switch (block.Kind)
            {
                case CardBlock.List:
                    var lines = (block.Items ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
                    if (lines.Count > 0)
                        blocks.Add(new CardBlock { Kind = block.Kind, Value = block.Value, Items = lines });
                    break;

                case CardBlock.Tip when CardBlock.IsUsableTip(block.Target):
                    blocks.Add(block);
                    break;

                case CardBlock.Tip:
                    break;

                // A picture is its hash. A block with no hash is somebody who opened the picker and
                // changed their mind, and publishing it would put an empty frame on their page.
                case CardBlock.Image when CardBlock.IsUsableAssetHash(block.ContentHash):
                    blocks.Add(block);
                    break;

                case CardBlock.Image:
                    break;

                // A key with nothing after the equals sign is a label nobody answered.
                case CardBlock.KeyValue when Answered(block.Value):
                    blocks.Add(block);
                    break;

                case CardBlock.KeyValue:
                    break;

                case CardBlock.Theme:
                    blocks.Add(block);
                    break;

                default:
                    if (!string.IsNullOrWhiteSpace(block.Value)) blocks.Add(block);
                    break;
            }
        }

        return new CardDocument { Version = card.Version, Title = card.Title, Blocks = blocks };
    }

    /// <summary>Whether a labelled fact actually carries a fact.</summary>
    private static bool Answered(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var cut = value.IndexOf('=');
        return cut < 0 ? true : value[(cut + 1)..].Trim().Length > 0;
    }
}
