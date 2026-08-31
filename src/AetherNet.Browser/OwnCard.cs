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
    /// <para>
    /// Forty-eight. It was twelve, on the reasoning that a card should stay a card rather than become
    /// a website somebody has to scroll — which sounded like restraint and was actually a decision
    /// about what this network is for. A sketchbook with nine plates, a menu, a portfolio, a body of
    /// work: all of them are more than twelve blocks, and a limit that rules them out rules out
    /// everybody who has something to show.
    /// </para>
    /// <para>
    /// Still a limit, because a card is carried across a radio and held on somebody else's phone. But
    /// the thing that actually costs a reader is pictures, and those have a budget of their own —
    /// forty-eight blocks of text is a few kilobytes.
    /// </para>
    /// </remarks>
    public const int MostBlocks = 48;

    /// <summary>The longest a single line of a card may be.</summary>
    /// <remarks>
    /// A line, not a piece of writing: a label, a heading, a row of a list, the words on a link. These
    /// are the page's furniture and a long one is a mistake rather than a paragraph.
    /// </remarks>
    public const int LongestValue = 280;

    /// <summary>The longest a piece of writing may be.</summary>
    /// <remarks>
    /// <para>
    /// Prose had the same limit as a label, and 280 characters is a tweet. Somebody writing the
    /// paragraph that says what they do had their sentence cut in the middle and nothing told them —
    /// which is not a page that competes with a website, it is a page that looks like it was written
    /// by somebody who ran out of room.
    /// </para>
    /// <para>
    /// Still a limit, because a card is carried across a radio and held on somebody else's phone. But
    /// this is where the cost is worth counting properly: a long paragraph is about a kilobyte, and
    /// the pictures on the same page are a thousand times that. Forty-eight blocks at this length is
    /// under sixty kilobytes — a couple of seconds over Wi-Fi Direct, and less than one photograph.
    /// </para>
    /// </remarks>
    public const int LongestProse = 1200;

    /// <summary>How long this kind of block may be.</summary>
    /// <remarks>
    /// A stylesheet is measured by <see cref="CardCss.Most"/>, not by the line limit. It fell through
    /// to <see cref="LongestValue"/> and was cut at 280 characters — the same mistake this file had
    /// already made once for prose, and with the same result: the author writes a page of CSS, the
    /// braces stop balancing because the closing one was thrown away, and the editor tells them their
    /// stylesheet will not travel without either of them knowing why. Silent truncation of somebody's
    /// work is the failure to design out; a limit that is checked and said out loud is not.
    /// </remarks>
    public static int Longest(string? kind) => kind switch
    {
        CardBlock.Css => CardCss.Most,
        CardBlock.Text or CardBlock.Quote => LongestProse,
        _ => LongestValue,
    };

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
        CardBlock.Title, CardBlock.Eyebrow, CardBlock.Heading, CardBlock.Text, CardBlock.Quote,
        CardBlock.List, CardBlock.Index, CardBlock.KeyValue, CardBlock.Image,
        CardBlock.Rule, CardBlock.Link, CardBlock.Tip,
    ];

    /// <summary>The most lines one list or index block may hold.</summary>
    /// <remarks>
    /// Twenty-four. An index is a catalogue — a menu, a set of works, a price list — and a dozen is
    /// short for all three.
    /// </remarks>
    public const int MostItems = 24;

    /// <summary>What each kind is called on the button that adds it.</summary>
    public static string Label(string kind) => kind switch
    {
        CardBlock.Title => "Name",
        CardBlock.Eyebrow => "Label",
        CardBlock.Heading => "Heading",
        CardBlock.Text => "Words",
        CardBlock.Quote => "Quote",
        CardBlock.List => "List",
        CardBlock.Index => "Index",
        CardBlock.KeyValue => "Detail",
        CardBlock.Rule => "Break",
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
        CardBlock.Title => "Kagiso Plumbing",
        CardBlock.Eyebrow => "Plumber · Kagiso · since 2016",
        CardBlock.Heading => "Hours",
        CardBlock.Text => "A few words about what you do",
        CardBlock.Quote => "The one line you want remembered",
        CardBlock.List => "One thing per line",
        CardBlock.Index => "Geyser replacement = From R2400",
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
        var backed = false;

        foreach (var block in card.Blocks ?? [])
        {
            if (blocks.Count >= MostBlocks) break;

            // One theme block of each kind. A second look is not a second opinion, it is the first
            // being overruled by whichever the renderer happens to find first — and the same is true
            // of a background.
            if (block.Kind == CardBlock.Theme && CardLook.IsLook(block.Value))
            {
                if (themed) continue;
                themed = true;
            }
            else if (block.Kind == CardBlock.Theme && CardShader.IsShader(block.Value))
            {
                if (backed) continue;
                backed = true;
            }

            var most = Longest(block.Kind);
            if (block.Value is { Length: int said } && said > most)
                block.Value = block.Value![..most];

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
        card.Title = Titled(card.Title);
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

    /// <summary>
    /// Turn one dial on the look this card is wearing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as a line in a single style block rather than a block each, so a card that has been
    /// tuned is one block bigger however many dials were turned. Setting a dial back to nothing takes
    /// the line out, and taking the last line out takes the block out — a card that was tuned and then
    /// untuned goes back to being byte-identical to one that never was.
    /// </para>
    /// <para>
    /// Nothing is validated here. <see cref="CardLook.Tuned"/> is what decides whether a value is
    /// allowed, and it has to, because a card arriving over the radio never passed through this
    /// method. Checking in both places means one of them eventually gets it wrong; the reader is the
    /// one that must not.
    /// </para>
    /// </remarks>
    public static void SetDial(CardDocument card, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        card.Blocks ??= [];
        var style = card.Blocks.FirstOrDefault(b => b.Kind == CardBlock.Style);
        if (style is null)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            style = new CardBlock { Kind = CardBlock.Style, Items = [] };

            // After the look it modifies, so reading the card top to bottom reads in the order the
            // decisions were made.
            var after = card.Blocks.FindIndex(b => b.Kind == CardBlock.Theme);
            card.Blocks.Insert(after < 0 ? 0 : after + 1, style);
        }

        var key = name.Trim().ToLowerInvariant();
        style.Items ??= [];
        style.Items.RemoveAll(line =>
            line.Split('=', 2)[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(value))
            style.Items.Add($"{key} = {value.Trim()}");

        if (style.Items.Count == 0) card.Blocks.Remove(style);
    }

    /// <summary>
    /// The author's own stylesheet, kept exactly as they typed it.
    /// </summary>
    /// <remarks>
    /// Stored raw and made safe at render time, never on the way in. Two reasons, and the second is
    /// the one that matters: somebody whose <c>url()</c> vanished from under their cursor as they
    /// typed it has been taught nothing and will assume the editor is broken — they keep their text
    /// and are told what will not travel. And a card arriving over the radio never passed through
    /// this method at all, so the reader's renderer has to be the thing that decides. Sanitising here
    /// as well would mean two places to keep right, and the wrong one would rot.
    /// </remarks>
    public static void SetCss(CardDocument card, string? written)
    {
        card.Blocks ??= [];
        card.Blocks.RemoveAll(b => b.Kind == CardBlock.Css);

        if (string.IsNullOrWhiteSpace(written)) return;

        var at = card.Blocks.FindIndex(b => b.Kind == CardBlock.Style);
        if (at < 0) at = card.Blocks.FindIndex(b => b.Kind == CardBlock.Theme);
        card.Blocks.Insert(at < 0 ? 0 : at + 1,
            new CardBlock { Kind = CardBlock.Css, Value = written });
    }

    /// <summary>What this card has turned, by name — empty where it is wearing the look's own value.</summary>
    public static string Dial(CardDocument? card, string name)
    {
        var line = card?.Blocks?.FirstOrDefault(b => b.Kind == CardBlock.Style)?.Items?
            .FirstOrDefault(l => l.Split('=', 2)[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

        return line?.Split('=', 2) is { Length: 2 } said ? said[1].Trim() : "";
    }

    /// <summary>
    /// Keep the page's name and its title block saying the same thing.
    /// </summary>
    /// <remarks>
    /// The name is metadata — it is what a deck, an address bar and a file name use — and the block is
    /// what a reader sees. They are the same words, so typing in one place sets both, and a card that
    /// has never had a title block gets one the first time somebody names it.
    /// </remarks>
    public static void SetTitle(CardDocument card, string? name)
    {
        card.Title = name ?? "";
        card.Blocks ??= [];

        if (card.Blocks.FirstOrDefault(b => b.Kind == CardBlock.Title) is { } titled)
        {
            titled.Value = name;
            return;
        }

        // A new title goes after the look and the background, and before everything a reader reads.
        var at = card.Blocks.FindLastIndex(b => b.Kind == CardBlock.Theme) + 1;
        card.Blocks.Insert(at, CardBlock.Of(CardBlock.Title, name ?? ""));
    }

    /// <summary>Which look this card is wearing.</summary>
    public static CardLook LookOf(CardDocument card) => CardLook.FromCard(card);

    /// <summary>
    /// Choose the background, replacing whatever was chosen before.
    /// </summary>
    /// <remarks>
    /// Kept beside the look rather than inside it: the same typography carries a dozen different
    /// backgrounds, and a person who has found the words they want should be able to try all of them
    /// without their page changing shape underneath.
    /// </remarks>
    public static void SetShader(CardDocument card, string shader)
    {
        if (!CardShader.IsShader(shader)) return;

        card.Blocks ??= [];
        card.Blocks.RemoveAll(b => b.Kind == CardBlock.Theme && CardShader.IsShader(b.Value));
        card.Blocks.Insert(0, CardBlock.Of(CardBlock.Theme, shader.Trim().ToLowerInvariant()));
    }

    /// <summary>Which background this card is wearing.</summary>
    public static CardShader ShaderOf(CardDocument card) => CardShader.FromCard(card);

    /// <summary>Add a block of the given kind, if there is room.</summary>
    public static bool Add(CardDocument card, string kind)
    {
        card.Blocks ??= [];
        if (card.Blocks.Count >= MostBlocks) return false;
        if (!Writable.Contains(kind)) return false;

        card.Blocks.Add(kind is CardBlock.List or CardBlock.Index
            ? new CardBlock { Kind = kind, Items = ["", ""] }
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

    /// <summary>The longest a page title may be.</summary>
    /// <remarks>
    /// Sixty. Titles used to go through the cleaner meant for a person's <i>name</i>, which is capped
    /// at eighteen characters so it fits inside a Wi-Fi network name — so every page called anything
    /// longer than that was silently cut, and "A card on AetherNet" was published as "A card on
    /// AetherNe". A page is not a person and does not have to fit in an SSID.
    /// </remarks>
    public const int LongestTitle = 60;

    /// <summary>
    /// Tidy a page's title: collapse the whitespace, keep the words.
    /// </summary>
    private static string Titled(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var said = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return said.Length > LongestTitle ? said[..LongestTitle].TrimEnd() : said;
    }

    /// <summary>Whether this block is one a person edits, rather than one the app manages.</summary>
    public static bool IsEditable(CardBlock block) => Writable.Contains(block.Kind);

    /// <summary>
    /// Make one like this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gesture the whole thing is for.</b> A generation learned HTML and CSS off MySpace and
    /// nobody taught them: they saw a profile they liked, looked at how it was made, copied it and
    /// changed it until it was theirs. Cards already travel from phone to phone — so the trading is
    /// not delivery, it is the lesson, and this is the part of it that was missing.
    /// </para>
    /// <para>
    /// <b>Structure and design travel; the person does not.</b> The look, the background, the order of
    /// the blocks, the shape of every list — all of it comes across, because that is the thing worth
    /// learning and the thing somebody wants when they say "one like this". The words come across too,
    /// as something to type over rather than a blank page to face; a template that says nothing is a
    /// page most people abandon.
    /// </para>
    /// <para>
    /// <b>What does not come across is theirs.</b> Photographs stay with their author — the frame
    /// arrives empty, so the page still reads as a page with a picture in it and the picture is yours
    /// to put there. A tip jar arrives empty for a harder reason: a remix that quietly carried
    /// somebody else's payment address would send a stranger's money to them, and it would look
    /// exactly like a page working properly.
    /// </para>
    /// </remarks>
    public static CardDocument Remix(CardDocument? card, string? named = null)
    {
        var made = new CardDocument { Title = named ?? "" };
        var blocks = new List<CardBlock>(MostBlocks);

        foreach (var block in card?.Blocks ?? [])
        {
            if (blocks.Count >= MostBlocks) break;

            blocks.Add(new CardBlock
            {
                Kind = block.Kind,
                Value = block.Kind == CardBlock.Title ? named ?? "" : block.Value,
                Items = block.Items is { Count: > 0 } items ? [.. items] : null,
                Align = block.Align,
                As = block.As,

                // Three things that are theirs and not yours: their photograph, their tip jar, and
                // their pages. A nav row whose words still say Journal / About / Contact is exactly
                // what somebody wants copied — but pointed at aether://THEM, it quietly sends your
                // readers to them, and it looks like a page working properly. The words stay; where
                // they go is yours to say. An address on the open web comes across, because that is a
                // reference rather than a destination they own.
                ContentHash = null,
                Target = block.Kind == CardBlock.Tip || CardBlock.IsMeshAddress(block.Target)
                    ? null
                    : block.Target,
            });
        }

        made.Blocks = blocks;

        if (named is { Length: > 0 }) SetTitle(made, named);

        return Tidy(made);
    }

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

                // An index is its lines. An empty one is a heading with nothing under it.
                case CardBlock.Index:
                    var plates = (block.Items ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
                    if (plates.Count > 0)
                        blocks.Add(new CardBlock { Kind = block.Kind, Value = block.Value, Items = plates });
                    break;

                // A break carries no text, so the empty-value rule below would throw it away.
                case CardBlock.Rule:
                    blocks.Add(block);
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
