// SPDX-License-Identifier: MIT

using System.Text;

namespace AetherNet.Browser;

/// <summary>
/// Someone's own card, drawn for a stranger standing next to them.
///
/// <para>
/// The page a person sees before they decide to trust anything is the one place this network makes a
/// first impression, and until now it was a generic block of text with a button on it. It should be
/// <b>theirs</b> — their name, their words, their colours — because a card that looks like a person is
/// a different proposition from a card that looks like an installer.
/// </para>
///
/// <para>
/// <b>Two constraints shape every line of this.</b> The guest has no internet, so the page may not
/// reference a single external thing — no font host, no stylesheet, no image server. And the content
/// is authored by one person and rendered for another, so everything is escaped and nothing is
/// executable. A card is a document, never a program.
/// </para>
///
/// <para>
/// The visual language is ThreeUI's: one monospace face for everything, small radii, a four-step
/// neutral stack from page to content, and borders drawn as a few percent of the foreground rather
/// than as lines. That it uses a single system-available typeface is what makes it survive having no
/// network at all.
/// </para>
/// </summary>
public static class CardPage
{
    /// <summary>
    /// The accent a card asked for, if it is one we are willing to apply.
    /// </summary>
    /// <remarks>
    /// A theme block may carry a palette name or a plain hex colour. Anything else — a gradient, a url,
    /// a second declaration — is refused by <see cref="CardBlock.IsUsableAccent"/>, because this value
    /// is written into a style attribute and a card is not allowed to restyle the page around itself.
    /// </remarks>
    public static string? AccentOf(CardDocument? card) =>
        card?.Blocks?
            .FirstOrDefault(b => b.Kind == CardBlock.Theme && CardBlock.IsUsableAccent(b.Value))?
            .Value;

    /// <summary>
    /// Draw the card.
    /// </summary>
    /// <param name="card">The document. Null or empty still produces a page — a name and nothing else.</param>
    /// <param name="who">Whose card it is, as a person reads it.</param>
    /// <param name="sizeBytes">How big the download is, so nobody is surprised by it.</param>
    /// <param name="downloadPath">
    ///   Where the button points — a path on this same phone. <b>Null when there is nothing to hand
    ///   over</b>, which is the same card drawn as what it is the rest of the time: a page on
    ///   AetherNet. The offer is the exception, not the document.
    /// </param>
    /// <param name="assetPath">
    ///   Turns a content hash into a path this page can fetch it from, or null when the bytes are not
    ///   available. Passed in rather than assumed, so the renderer never invents an address.
    /// </param>
    /// <param name="fonts">
    ///   Turns a typeface family into the bytes to carry with the page, or null when they are not
    ///   available. For a page leaving this device: the reader has no internet, so a linked font never
    ///   arrives — it is embedded or it is absent, and the look falls back to the handset's own faces.
    /// </param>
    /// <param name="sample">
    ///   Whether this is a chooser's tile rather than a page somebody is reading. A tile shows its
    ///   background at strength, because that is the thing being chosen.
    /// </param>
    /// <param name="still">
    ///   Draw the masthead once instead of letting it drift. For a thumbnail: a look-picker shows five
    ///   cards at once, and five animating GL contexts on a handset is a page that competes with real
    ///   websites and loses.
    /// </param>
    /// <param name="fontBase">
    ///   Where the same typefaces can be linked from instead, for a page drawn <b>inside</b> this app —
    ///   the editor's preview, which redraws on every keystroke. Embedding a hundred and thirty
    ///   kilobytes of font six times over per letter typed is what makes a handset feel slow, and the
    ///   bytes are already on the device.
    /// </param>
    public static string Render(
        CardDocument? card,
        string? who,
        long sizeBytes,
        string? downloadPath,
        Func<string, string?>? assetPath = null,
        Func<string, byte[]?>? fonts = null,
        string? fontBase = null,
        bool still = false,
        bool sample = false)
    {
        // "Someone next to you" belongs to the moment somebody is being handed a phone. On a page
        // that was browsed to it is a stranger's name replaced with a description of where they are
        // standing, which is not a fallback — it is the wrong sentence.
        var offering = !string.IsNullOrWhiteSpace(downloadPath);
        var titled = Text(who) is { Length: > 0 } n ? n : "";
        var name = titled.Length > 0 ? titled : offering ? "Someone next to you" : "";
        var look = CardLook.FromCard(card);

        var accent = AccentOf(card);

        var page = new StringBuilder(4096);

        page.Append("<!doctype html><html lang=\"en\">");
        page.Append("<head><meta charset=\"utf-8\">");

        // What this page may do, stated to the browser rather than promised by us.
        //
        // It used to carry no script at all, and that was a property worth something: there was
        // nothing to get wrong. It carries one now — the masthead painter — because the look is the
        // argument this network makes before anybody has read a word of it. So the property is
        // replaced with a stronger one rather than simply given up: default-src 'none' means the page
        // cannot open a connection, load a font, fetch an image or reach any host, whatever ends up
        // written into it. Pictures and typefaces arrive as data, or from the phone that served it.
        //
        // Nothing an author typed is ever handed to the script. The colour is six hexadecimal digits
        // that have already been through IsUsableAccent, and the seed is a number computed here.
        page.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"")
            .Append("default-src 'none'; img-src data: 'self'; font-src data: 'self'; ")
            .Append("style-src 'unsafe-inline'; script-src 'unsafe-inline'; ")
            .Append("base-uri 'none'; form-action 'none'\">");
        page.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        page.Append("<title>").Append(name.Length > 0 ? name : "A page on AetherNet").Append("</title>");
        page.Append("<style>").Append(Faces(look, fonts, fontBase)).Append(Style(accent, look, sample)).Append("</style>");

        // The author's own stylesheet, after ours so their rule wins on their own page — which is the
        // whole point of letting them write one. Confined to the card's subtree and stripped of
        // anything that fetches before it gets here; see CardCss.
        var own = CardCss.Safe(card?.Blocks?.FirstOrDefault(b => b.Kind == CardBlock.Css)?.Value);
        if (own.Length > 0) page.Append("<style>").Append(own).Append("</style>");
        page.Append("</head><body>");

        // The ground, if this page has one.
        //
        // There is no masthead. A band of generated colour above somebody's words announces that the
        // page had nothing of its own to open with, and it made every page look the same and look
        // cheap. What a page can have instead is a ground: a picture its author chose, bleeding off
        // the margins behind everything, or — for a page with no imagery at all — the same surface
        // the catalogue used to paint into the band, drawn faintly under the whole page.
        if (Wash(card) is { } wash && assetPath?.Invoke(wash.ContentHash!) is { Length: > 0 } ground)
        {
            page.Append("<div class=\"wash\" style=\"background-image:url(")
                .Append(Attr(ground)).Append(")\"></div>");
        }
        else if (card?.Blocks?.Any(b => b.Kind == CardBlock.Theme && CardShader.IsShader(b.Value)) == true)
        {
            page.Append("<canvas class=\"wash gl\" data-aether-shader data-accent=\"")
                .Append(Attr(accent ?? look.Accent))
                .Append("\" data-seed=\"").Append(Seed(name))
                .Append(still ? "\" data-still" : "\"")
                .Append("></canvas>");
        }

        page.Append("<main class=\"card card-own\">");

        // The eyebrow explains how the page got here, which is only true when it is being handed
        // over. On AetherNet the reader browsed to it, and telling them it was shared with them is a
        // small lie printed above somebody's name.
        if (offering)
            page.Append("<p class=\"eyebrow\">Shared with you, phone to phone</p>");

        // An eyebrow belongs above the title, wherever its author put it — it qualifies the title
        // rather than sitting in the flow, and a page that printed it halfway down would be a page
        // whose author had to know that.
        // Nothing is hoisted. The label and the name are blocks, drawn where their author put them,
        // and a page can therefore open with a picture, put its navigation above its name, or do any
        // of the things a designed page does. Hoisting them was the renderer deciding the layout.
        //
        // A card written before the name was a block has no title block, so its name is drawn first —
        // which is exactly where it used to be.
        if (card?.Blocks?.Any(b => b.Kind == CardBlock.Title) != true && name.Length > 0)
            page.Append("<h1>").Append(name).Append("</h1>");

        Blocks(page, card, assetPath, offering);

        if (offering)
        {
            page.Append("<div class=\"give\">");
            page.Append("<a class=\"btn\" href=\"").Append(Attr(downloadPath)).Append("\">Get Aether</a>");
            page.Append("<p class=\"size\">").Append(Size(sizeBytes)).Append(" · comes off the phone beside you, not the internet</p>");
            page.Append("<p class=\"fine\">Your phone will ask whether you're sure. That's normal — it asks that for anything that didn't come from a shop.</p>");
            page.Append("</div>");
        }

        page.Append("</main>");

        // Last, so it paints a canvas the page has already laid out. Nothing else depends on it: a
        // reader whose phone refuses WebGL loses the motion and keeps the page.
        if (PageAssets.Shader() is { Length: > 0 } shader)
        {
            // The background this card asked for, from the catalogue, as source we shipped. The key
            // was looked up here — nothing an author typed reaches the GPU, and an unknown key is a
            // newer author rather than a broken card.
            page.Append("<script>window.aetherField=")
                .Append(System.Text.Json.JsonSerializer.Serialize(CardShader.FromCard(card).Field))
                .Append("</script>");

            page.Append("<script>").Append(shader).Append("</script>");
        }

        // And the one that lets a link work and lets the page say how tall it is. Both are things the
        // page asks its host for; neither is something the page does.
        if (PageAssets.Links() is { Length: > 0 } links)
            page.Append("<script>").Append(links).Append("</script>");

        page.Append("</body></html>");
        return page.ToString();
    }

    /// <summary>The picture a card uses as its ground, if it names one.</summary>
    private static CardBlock? Wash(CardDocument? card) =>
        card?.Blocks?.FirstOrDefault(b =>
            b.Kind == CardBlock.Image && b.IsWash && CardBlock.IsUsableAssetHash(b.ContentHash));

    /// <summary>
    /// How this page's ground folds, as a number.
    /// </summary>
    /// <remarks>
    /// Computed here so nothing an author typed is ever handed to the shader — it reads a colour and
    /// an integer, and neither can be anything but a colour and an integer. Stable, so a page keeps
    /// the same ground every time anybody opens it.
    /// </remarks>
    private static int Seed(string name)
    {
        var hash = 2166136261u;

        foreach (var c in name)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return (int)(hash % 100000);
    }

    /// <summary>Whether this card brings any picture of its own that a reader will see.</summary>
    private static bool Pictured(CardDocument? card, Func<string, string?>? assetPath) =>
        card?.Blocks?.Any(b =>
            b.Kind == CardBlock.Image
            && CardBlock.IsUsableAssetHash(b.ContentHash)
            && assetPath?.Invoke(b.ContentHash!) is { Length: > 0 }) == true;

    /// <summary>
    /// Draw each block, skipping any kind this renderer does not know.
    /// </summary>
    /// <remarks>
    /// Skipping rather than failing is the compatibility rule the card model was built on: a person on
    /// a newer version can write a block an older reader has never heard of, and the older reader still
    /// shows them a card instead of an error.
    /// </remarks>
    /// <param name="offering">
    ///   Whether this page is being handed to somebody who has no Aether yet. It changes one thing: a
    ///   link inside the mesh is somewhere they cannot go, so it is drawn as text rather than as an
    ///   invitation. A reader who is already on the mesh gets a link that works.
    /// </param>
    private static void Blocks(
        StringBuilder page, CardDocument? card, Func<string, string?>? assetPath, bool offering)
    {
        if (card?.Blocks is not { Count: > 0 } blocks) return;

        for (var at = 0; at < blocks.Count; at++)
        {
            var block = blocks[at];

            // A run of pictures is a gallery, and a run of links is a row. Neither needs a block kind
            // of its own: somebody adding three pictures in a row means a gallery, and asking them to
            // say so as well is asking them to know how a renderer is built.
            if (Run(blocks, at, CardBlock.Image) is > 1 and var pictures
                && Drawable(blocks, at, pictures, assetPath) is { Count: > 1 } shown)
            {
                Gallery(page, shown, assetPath);
                at += pictures - 1;
                continue;
            }

            if (Links(blocks, at) is > 1 and var links && !offering)
            {
                Row(page, blocks, at, links);
                at += links - 1;
                continue;
            }

            switch (block.Kind)
            {
                case CardBlock.Title when Text(block.Value) is { Length: > 0 } t1:
                    page.Append("<h1").Append(Dress(block)).Append(">").Append(t1).Append("</h1>");
                    break;

                case CardBlock.Eyebrow when Text(block.Value) is { Length: > 0 } brow:
                    page.Append("<p class=\"eyebrow").Append(block.IsCentred ? " mid" : "").Append("\">")
                        .Append(brow).Append("</p>");
                    break;

                case CardBlock.Heading when Text(block.Value) is { Length: > 0 } h:
                    page.Append("<h2").Append(Set(block)).Append(">").Append(h).Append("</h2>");
                    break;

                case CardBlock.Quote when Text(block.Value) is { Length: > 0 } q:
                    page.Append("<blockquote").Append(Set(block)).Append(">")
                        .Append(Marks(q, offering)).Append("</blockquote>");
                    break;

                // A break with a mark in it. The value names one from the catalogue; a break that
                // names nothing is a hairline, which is what it always was.
                case CardBlock.Rule:
                    page.Append("<div class=\"brk\">")
                        .Append(CardOrnament.Of(block.Value).Svg())
                        .Append("</div>");
                    break;

                // The plate index. Each line is "name = place"; a line with no place is still a line,
                // because a catalogue with one unlabelled entry should not lose the entry.
                case CardBlock.Index when block.Items is { Count: > 0 } plates:
                    page.Append("<div class=\"index\">");

                    var ordinal = 0;
                    foreach (var plate in plates)
                    {
                        if (Text(plate) is not { Length: > 0 } line) continue;

                        var split = line.IndexOf('=');
                        ordinal++;

                        page.Append("<div class=\"plate-row\"><span class=\"plate-n\">")
                            .Append(ordinal.ToString("00"))
                            .Append("</span><span class=\"plate-t\">")
                            .Append((split > 0 ? line[..split] : line).Trim())
                            .Append("</span><span class=\"plate-p\">")
                            .Append(split > 0 ? line[(split + 1)..].Trim() : "")
                            .Append("</span></div>");
                    }

                    page.Append("</div>");
                    break;

                // Somebody writing prose presses Return, and what they meant by it is the only
                // question worth answering: once is a new line, twice is a new paragraph. Both are
                // drawn here rather than asking the author to make a second block every time they
                // finish a thought — the block is the piece of writing, not the sentence.
                case CardBlock.Text when Text(block.Value) is { Length: > 0 } t:
                    foreach (var said in Paragraphs(t))
                        page.Append("<p class=\"say\"").Append(Set(block)).Append(">")
                            .Append(Marks(said, offering).Replace("\n", "<br>")).Append("</p>");
                    break;

                case CardBlock.List when block.Items is { Count: > 0 } items:
                    page.Append("<ul>");
                    foreach (var item in items)
                        if (Text(item) is { Length: > 0 } li)
                            page.Append("<li>").Append(Marks(li, offering)).Append("</li>");
                    page.Append("</ul>");
                    break;

                case CardBlock.KeyValue when Text(block.Value) is { Length: > 0 } kv:
                    var cut = kv.IndexOf('=');
                    page.Append("<div class=\"kv\"><span>")
                        .Append(cut > 0 ? kv[..cut] : kv)
                        .Append("</span><span>")
                        .Append(cut > 0 ? kv[(cut + 1)..] : "")
                        .Append("</span></div>");
                    break;

                // A link, followable only by somebody who can actually follow it.
                //
                // On the page a stranger is handed it is plain text: they have no Aether yet, so a
                // mesh address is an invitation somewhere their phone cannot go, and the address
                // itself never appears. On the mesh it works — but never as an address the page can
                // act on. The target goes into a data attribute, the page asks its host to navigate,
                // and the host checks it again. A card that could hand a browser an address of its
                // own choosing is what publishing cards as JSON rather than HTML exists to prevent.
                case CardBlock.Link when Text(block.Value) is { Length: > 0 } label:
                    if (!offering && IsMeshAddress(block.Target))
                        page.Append("<button type=\"button\" class=\"lnk go\" data-aether-to=\"")
                            .Append(Attr(block.Target)).Append("\">").Append(label).Append("</button>");
                    else
                        page.Append("<div class=\"lnk\">").Append(label).Append("</div>");
                    break;

                // The tip jar. An anchor, because this page is already open in a browser and following
                // it is the reader's decision to make — but nothing is fetched to draw it, so opening
                // a stranger's card still causes no request of any kind. The host is shown beside the
                // label: the decision belongs on where the money goes, not on what it was called.
                case CardBlock.Tip when CardBlock.IsUsableTip(block.Target):
                    page.Append("<a class=\"tip\" href=\"").Append(Attr(block.Target))
                        .Append("\" rel=\"noopener noreferrer nofollow\" target=\"_blank\">")
                        .Append("<span>").Append(Text(block.Value) is { Length: > 0 } jar ? jar : "Tip").Append("</span>")
                        .Append("<span class=\"tip-h\">").Append(Text(CardBlock.TipHost(block.Target))).Append("</span>")
                        .Append("</a>");
                    break;

                case CardBlock.Image when !block.IsWash
                                          && assetPath?.Invoke(block.ContentHash ?? "") is { Length: > 0 } src
                                          && CardBlock.IsUsableAssetHash(block.ContentHash):
                    page.Append(block.IsWide ? "<figure class=\"wide\">" : "<figure>")
                        .Append("<img src=\"").Append(Attr(src))
                        .Append("\" alt=\"").Append(Text(block.Value) ?? "").Append("\">");

                    if (Text(block.Value) is { Length: > 0 } caption)
                        page.Append("<figcaption").Append(Set(block)).Append('>')
                            .Append(caption).Append("</figcaption>");

                    page.Append("</figure>");
                    break;
            }
        }
    }

    /// <summary>How many blocks of this kind sit together starting here.</summary>
    private static int Run(IReadOnlyList<CardBlock> blocks, int at, string kind)
    {
        var n = 0;
        while (at + n < blocks.Count && blocks[at + n].Kind == kind) n++;
        return n;
    }

    /// <summary>
    /// A run of links that belong in one row together.
    /// </summary>
    /// <remarks>
    /// A row is words or it is marks, never a mixture — one link wearing a mark among four that do
    /// not is a page with something wrong with it. So the run ends where that changes, and a page
    /// that has a worded navigation followed by a row of small marks gets both, which is what every
    /// page with a row of small marks under its navigation actually is. Grouping them all together
    /// and giving up made the whole thing words.
    /// </remarks>
    private static int Links(IReadOnlyList<CardBlock> blocks, int at)
    {
        if (at >= blocks.Count || blocks[at].Kind != CardBlock.Link) return 0;

        var marked = CardIcon.IsIcon(blocks[at].As);
        var n = 0;

        while (at + n < blocks.Count
               && blocks[at + n].Kind == CardBlock.Link
               && CardIcon.IsIcon(blocks[at + n].As) == marked) n++;

        return n;
    }

    /// <summary>The pictures in a run that can actually be shown, in order.</summary>
    private static List<CardBlock> Drawable(
        IReadOnlyList<CardBlock> blocks, int at, int count, Func<string, string?>? assetPath)
    {
        var shown = new List<CardBlock>(count);

        for (var i = at; i < at + count; i++)
        {
            var block = blocks[i];
            if (block.IsWash) continue;
            if (!CardBlock.IsUsableAssetHash(block.ContentHash)) continue;
            if (assetPath?.Invoke(block.ContentHash!) is not { Length: > 0 }) continue;

            shown.Add(block);
        }

        return shown;
    }

    /// <summary>
    /// Several pictures together, as a grid.
    /// </summary>
    /// <remarks>
    /// Two abreast, and a caption under each that has one. A body of work — plates, a portfolio, a
    /// menu with photographs — is the shape a page of stacked full-width images cannot make, and it is
    /// most of what separates a page somebody is showing from a page somebody is filling in.
    /// </remarks>
    private static void Gallery(StringBuilder page, List<CardBlock> shown, Func<string, string?>? assetPath)
    {
        page.Append("<div class=\"gallery\">");

        foreach (var picture in shown)
        {
            page.Append("<figure><img src=\"")
                .Append(Attr(assetPath!.Invoke(picture.ContentHash!)))
                .Append("\" alt=\"").Append(Text(picture.Value) ?? "").Append("\">");

            if (Text(picture.Value) is { Length: > 0 } caption)
                page.Append("<figcaption>").Append(caption).Append("</figcaption>");

            page.Append("</figure>");
        }

        page.Append("</div>");
    }

    /// <summary>
    /// Several links together, as a row rather than a stack.
    /// </summary>
    /// <remarks>
    /// What a page's own navigation looks like. Stacked full-width buttons read as a form; a row of
    /// words reads as a place with parts to it.
    /// </remarks>
    private static void Row(StringBuilder page, IReadOnlyList<CardBlock> blocks, int at, int count)
    {
        // A row of marks or a row of words, never a mixture. One link wearing a mark among four that
        // do not is a page with something wrong with it, so the row falls back to words — which is
        // never wrong, only quieter.
        var marks = true;
        for (var i = at; i < at + count; i++)
            if (!CardIcon.IsIcon(blocks[i].As)) { marks = false; break; }

        var kind = marks ? "icons" : "row";
        page.Append(blocks[at].IsCentred ? $"<nav class=\"{kind} mid\">" : $"<nav class=\"{kind}\">");

        for (var i = at; i < at + count; i++)
        {
            var link = blocks[i];
            if (Text(link.Value) is not { Length: > 0 } label) continue;

            // The words are what a mark is called: the title a pointer shows, and the only thing a
            // screen reader has to go on. A row of marks with nothing said about them is a row of
            // shapes, and the reader is left guessing which one is the shop.
            var drawn = marks && CardIcon.Of(link.As) is { } icon ? icon.Svg() : label;
            var named = marks ? $" title=\"{label}\" aria-label=\"{label}\"" : "";
            var css = marks ? "mark" : "rowlnk";

            if (IsMeshAddress(link.Target))
                page.Append("<button type=\"button\" class=\"").Append(css)
                    .Append("\" data-aether-to=\"").Append(Attr(link.Target)).Append('"')
                    .Append(named).Append('>').Append(drawn).Append("</button>");
            else if (marks && CardBlock.IsUsableWeb(link.Target))
                page.Append("<a class=\"").Append(css).Append("\" href=\"").Append(Attr(link.Target))
                    .Append("\" rel=\"noopener noreferrer nofollow\" target=\"_blank\"")
                    .Append(named).Append('>').Append(drawn).Append("</a>");
            else
                page.Append("<span class=\"").Append(css).Append('"').Append(named).Append('>')
                    .Append(drawn).Append("</span>");
        }

        page.Append("</nav>");
    }

    /// <summary>The paragraphs in a piece of writing — a blank line between each.</summary>
    private static IEnumerable<string> Paragraphs(string said)
    {
        foreach (var part in said.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            if (part.Trim('\n', ' ', '\t') is { Length: > 0 } clean)
                yield return clean;
    }

    /// <summary>The class attribute a block's alignment asks for, or nothing.</summary>
    private static string Set(CardBlock block) => block.IsCentred ? " class=\"mid\"" : "";

    /// <summary>The classes a title asks for — where it sits, and how loud it is.</summary>
    private static string Dress(CardBlock block)
    {
        var classes = string.Concat(
            block.IsCentred ? " mid" : "",
            block.IsSmall ? " wordmark" : "");

        return classes.Length > 0 ? $" class=\"{classes.Trim()}\"" : "";
    }

    /// <summary>
    /// Emphasis inside a sentence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A page where nothing inside a paragraph can be stressed is a page of even grey text, and the
    /// pages this is measured against lean on an underlined phrase in the middle of a line more than
    /// they lean on anything else.
    /// </para>
    /// <para>
    /// Run <b>after</b> escaping, never before. The text arriving here has already had every angle
    /// bracket turned into an entity, so the only tags that can exist afterwards are the two this
    /// writes — an author cannot smuggle markup through by writing it in the middle of a sentence,
    /// and the card stays as inert as it was.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The words of a block, with its emphasis and its links drawn.
    /// </summary>
    /// <remarks>
    /// Escaped first and marked second, always. A card is a document rather than a page, so the only
    /// markup that can ever come out of one is markup this renderer wrote — see <see cref="CardMarks"/>
    /// for the vocabulary, and for why an address somebody wrote is never taken at its word.
    /// </remarks>
    private static string Marks(string escaped, bool offering = false) =>
        CardMarks.Marked(escaped) ? CardMarks.Draw(escaped, offering) : escaped;

    /// <summary>
    /// Carry the look's typefaces inside the page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the difference between a card that looks made and one that looks typed, and it costs
    /// about fifteen kilobytes a face — less than a small photograph. Linking them instead would be
    /// free and useless: the reader is on a phone-to-phone network with no way out, so a linked font
    /// never arrives and every look collapses to the same system face.
    /// </para>
    /// <para>
    /// A face the caller cannot supply is simply left out. The look then falls back through its own
    /// stack to something the handset already has, which is why every look declares a real fallback
    /// rather than a lone family name.
    /// </para>
    /// </remarks>
    private static string Faces(CardLook look, Func<string, byte[]?>? fonts, string? fontBase)
    {
        var css = new StringBuilder();

        // Only what this look asks for. Carrying all five looks' faces meant every card paid for four
        // typefaces it would never draw a letter in — and this page is competing with real websites,
        // where a hundred and thirty kilobytes nobody reads is the difference somebody feels.
        foreach (var family in look.Faces().Distinct(StringComparer.Ordinal))
        {
            if (fonts?.Invoke(family) is { Length: > 0 } bytes)
            {
                css.Append("@font-face{font-family:'").Append(family)
                   .Append("';font-display:swap;src:url(data:font/woff2;base64,")
                   .Append(Convert.ToBase64String(bytes))
                   .Append(") format('woff2')}");
            }
            else if (fontBase is not null && PageAssets.FaceFile(family) is { } file)
            {
                css.Append("@font-face{font-family:'").Append(family)
                   .Append("';font-display:swap;src:url(").Append(Attr(fontBase + file))
                   .Append(") format('woff2')}");
            }
        }

        return css.ToString();
    }

    /// <summary>
    /// The stylesheet, inline and self-contained.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not one external reference anywhere in it. The person reading this has joined a phone-to-phone
    /// network with no way out to the internet, so a linked font or stylesheet does not degrade
    /// gracefully — it simply never arrives, and the page they are deciding whether to trust arrives
    /// half-drawn.
    /// </para>
    /// <para>
    /// ThreeUI's own type stack is what makes that painless: it asks for one monospace face and every
    /// phone already has one.
    /// </para>
    /// </remarks>
    private static string Style(string? accent, CardLook look, bool sample)
    {
        // A card may name its own accent. It has already been through IsUsableAccent, so what lands
        // here is six hexadecimal digits — the one thing an author gets to change about the look they
        // chose, and the only place a card gets near CSS at all.
        var chosen = accent is not null ? $":root{{--accent:{accent}}}" : "";

        return
            look.Tokens() + chosen +

            "*{box-sizing:border-box}" +

            // The card IS the page. It was a bordered, rounded box floating in the middle of whatever
            // was showing it — which reads as a widget containing content rather than as a page, and
            // a widget does not compete with a website. The ground runs edge to edge, the masthead is
            // full-bleed, and the words sit in a measure inside it.
            "html{background:var(--paper);-webkit-text-size-adjust:100%}" +

            // The wash. Behind everything, fixed, and clipped to the page rather than tiled — it is a
            // painted margin, not a wallpaper.
            // The size is stated, not inferred from the insets.
            //
            // A <canvas> with inset:0 stretches to fill. The <img> that replaces it once the frame is
            // frozen does not: a replaced element with auto width resolves to its *intrinsic* size, so
            // the wash became a 75x38 thumbnail in the top-left corner and every card with a painted
            // background had been rendering without one — on the phone and on the desktop, silently,
            // because a wash that is not there looks exactly like a wash that is very quiet.
            ".wash{position:fixed;inset:0;width:100%;height:100%;object-fit:cover;" +
            "z-index:-1;pointer-events:none;border:0;" +
            "background-repeat:no-repeat;background-position:center top;background-size:cover}" +

            // A painted surface under a whole page has to be far quieter than one inside a band —
            // it is the paper, and paper does not compete with what is printed on it.
            // A wash is a wash: barely there, so what you read is the writing.
            //
            // Except in a chooser, where barely-there means all fourteen backgrounds are the same
            // cream rectangle and somebody is being asked to pick between things that look identical.
            // A tile is a sample of the background, so it shows the background.
            (sample ? ".wash.gl{opacity:.62}" : ".wash.gl{opacity:.14}") +
            "body{margin:0;background:var(--paper);color:var(--ink);" +
            "font-family:var(--body);font-weight:var(--weight);font-size:var(--size);" +
            "line-height:var(--leading);-webkit-font-smoothing:antialiased;" +
            "text-rendering:optimizeLegibility}" +

            ".card{display:flex;flex-direction:column;align-items:stretch;" +
            "gap:0;padding:56px 22px 84px;max-width:var(--measure);margin:0 auto;" +
            "overflow-x:hidden}" +

            // Spacing is vertical rhythm, not a uniform gap. What a thing is decides how much air it
            // gets above it, which is most of what makes a page feel set rather than stacked.
            ".eyebrow{margin:0 0 14px;font-size:12px;letter-spacing:.24em;text-transform:uppercase;" +
            "color:var(--ink-2);font-weight:400}" +

            // Wraps rather than overflows. A long name set large runs past the measure on a narrow
            // phone and the last letter is simply gone — which reads as a bug in the page rather than
            // as a title that needed two lines.
            "h1{margin:0 0 18px;font-family:var(--display);font-size:clamp(32px,8.4vw,64px);" +
            "line-height:1.04;letter-spacing:-.02em;font-weight:400;text-wrap:balance;" +
            "overflow-wrap:break-word}" +

            // A wordmark rather than a headline. The difference between a masthead and a signature,
            // and the reason a page can put its name quietly at the top and lead with the work.
            "h1.wordmark{font-size:clamp(21px,4vw,26px);letter-spacing:.01em;margin-bottom:10px}" +

            "h2{margin:46px 0 14px;font-size:12px;letter-spacing:.22em;text-transform:uppercase;" +
            "color:var(--ink-2);font-weight:400}" +

            ".say{margin:0 0 22px;max-width:100%}" +
            ".say+.say{margin-top:-4px}" +

            // A link inside a sentence.
            //
            // On the open web it is an anchor and on the mesh it is a button that asks the host, and
            // those are two different elements — so everything a button brings with it is taken back
            // off, down to the font. A reader must not be able to tell which one they are looking at,
            // because the whole claim is that these are the same page.
            ".mk{font:inherit;color:inherit;background:none;border:0;padding:0;margin:0;" +
            "text-align:inherit;letter-spacing:inherit;line-height:inherit;cursor:pointer;" +
            "text-decoration:underline;text-decoration-thickness:1px;text-underline-offset:2.5px;" +
            "text-decoration-color:var(--ink-2)}" +
            ".mk:hover{text-decoration-color:var(--accent)}" +
            ".mk:active{color:var(--accent)}" +

            // A pulled quote earns the space around it by being the only thing there.
            "blockquote{margin:34px 0;padding:0;font-family:var(--display);" +
            "font-size:clamp(23px,4.6vw,30px);line-height:1.28;font-weight:400;color:var(--ink)}" +

            "ul{margin:0 0 22px;padding-left:1.15em;display:flex;flex-direction:column;gap:9px}" +
            "li{padding-left:2px}" +

            // A labelled fact reads as an index row — label left, answer right, a hairline between —
            // rather than as a chip. It is the shape a printed page uses for exactly this job.
            ".kv{display:flex;justify-content:space-between;align-items:baseline;gap:18px;" +
            "padding:14px 2px;border-bottom:1px solid var(--rule);font-size:15px}" +
            ".kv:first-of-type{border-top:1px solid var(--rule)}" +
            ".kv span:first-child{color:var(--ink-2)}" +
            ".kv span:last-child{text-align:right;font-variant-numeric:tabular-nums}" +

            // The plate index. An ordinal, a name in the display face, and where it belongs — set
            // right, quiet and wide. A catalogue, a menu, a set of works and a schedule are all this.
            ".index{margin:8px 0 30px;display:flex;flex-direction:column}" +
            ".plate-row{display:flex;align-items:baseline;gap:18px;padding:15px 4px;" +
            "border-bottom:1px solid var(--rule)}" +
            ".plate-row:first-child{border-top:1px solid var(--rule)}" +
            ".plate-n{flex:0 0 auto;width:2.2em;font-size:12px;letter-spacing:.06em;color:var(--ink-3);" +
            "font-variant-numeric:tabular-nums}" +
            // Clipped rather than overrunning. A long name used to slide straight under the place
            // beside it and the two printed on top of each other — which reads as a broken page, and
            // is the kind of thing that only shows up with real words in it.
            ".plate-t{flex:1 1 auto;min-width:0;font-family:var(--display);font-size:20px;" +
            "line-height:1.2;font-weight:400;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}" +
            ".plate-p{flex:0 0 auto;max-width:46%;font-size:12.5px;letter-spacing:.08em;" +
            "text-transform:uppercase;color:var(--ink-3);text-align:right;overflow:hidden;" +
            "text-overflow:ellipsis;white-space:nowrap}" +

            // On a handset the row stacks instead of clipping twice.
            //
            // A name and a place side by side is right on a page and wrong on a phone: at 393px
            // "Gardens by the Bay — Supertree Grove" became "Gardens b… SUPERTREE GRO…", so the row
            // was two ellipses and told the reader nothing at all. Both halves get a whole line, the
            // place stays under its name, and nothing is cut. The rule ends where a row genuinely
            // fits — this is the shape of the content, not a phone-sized copy of the design.
            "@media(max-width:26rem){" +
            ".plate-row{flex-wrap:wrap;align-items:baseline;gap:0 12px;padding:13px 4px}" +
            ".plate-t{flex:1 1 auto;white-space:normal;text-overflow:clip;text-wrap:balance}" +
            // The basis takes the indent off, or the row is its own width plus the indent and the last
            // characters go under the edge of the page. Caught by measuring, not by looking.
            ".plate-p{flex:1 0 calc(100% - 2.2em - 12px);max-width:100%;" +
            "margin-left:calc(2.2em + 12px);padding-top:3px;" +
            "text-align:left;white-space:normal}}" +



            // A picture with something written under it is a figure. The caption is small, quiet and
            // close to the image — far enough to be a caption, near enough to belong to it.
            ".mid{text-align:center;margin-left:auto;margin-right:auto}" +
            "nav.row.mid{justify-content:center}" +
            "h1.mid,.say.mid{max-width:none}" +

            // A run of pictures. Two abreast on a handset is the most that leaves either of them
            // worth looking at.
            ".gallery{display:grid;grid-template-columns:repeat(2,1fr);gap:10px;margin:10px 0 30px}" +
            ".gallery figure{margin:0}" +
            ".gallery img{aspect-ratio:1;object-fit:cover;border-radius:2px}" +
            ".gallery figcaption{margin-top:6px;font-size:11.5px}" +

            // A page's own navigation: a row of words, not a stack of buttons.
            ".row{display:flex;flex-wrap:wrap;gap:18px;margin:0 0 26px;padding:0}" +
            ".rowlnk{appearance:none;background:none;border:0;padding:0;font:inherit;font-size:15px;" +
            "color:var(--ink-2);cursor:pointer;text-decoration:none;border-bottom:1px solid var(--rule)}" +
            ".rowlnk:active{color:var(--accent)}" +

            // The break, with whatever mark it named.
            ".brk{margin:38px 0;color:var(--accent);line-height:0}" +
            ".orn{display:block;width:100%;height:24px}" +

            "em{font-style:italic}" +
            "u{text-decoration:underline;text-underline-offset:3px;" +
            "text-decoration-color:color-mix(in srgb,currentColor 45%,transparent)}" +

            // A picture that is the subject rather than the backdrop: out to the edges, at its own
            // shape, uncropped.
            // A picture shown whole runs to the edges of the page — its own edges, not the window's.
            //
            // This was a viewport-width bleed, and the card clips its own overflow, so the picture was
            // quietly cut off on the left and the caption under it lost its first thirteen characters:
            // "Marina Bay Skyline" was drawn as "yline". Nothing reported anything, because as far as
            // the browser was concerned the page asked for exactly that.
            //
            // The negative margin is the card's own padding, so the figure lands on the padding edge
            // — which is where clipping stops — and it does not matter how wide the window is. A card
            // is drawn inside a frame of somebody else's choosing; a card that measures itself against
            // the window is measuring the wrong thing.
            "figure.wide{width:auto;margin:22px -22px 26px}" +
            ".card > figure.wide:first-child{margin-top:-56px}" +
            "figure.wide img{border-radius:0}" +
            "figure.wide figcaption{padding:0 22px}" +

            "figure{margin:6px 0 28px;width:100%}" +
            "figure img{width:100%;height:auto;display:block;border-radius:2px}" +
            "figcaption{margin-top:9px;font-size:12.5px;letter-spacing:.02em;color:var(--ink-3);" +
            "line-height:1.5}" +

            // A plate under a picture is a label, not a sentence.
            //
            // Centred, spaced and set in small capitals — the way a caption is set under a plate in a
            // printed book, and the way the page this is measured against sets it. It is the author's
            // call, taken by centring the picture: a centred picture wants a centred label under it,
            // and a picture set left wants a caption that reads as an aside.
            "figcaption.mid{margin-top:15px;font-size:12px;letter-spacing:.22em;" +
            "text-transform:uppercase;color:var(--ink-2)}" +

            // A row of marks. Small, evenly spaced, and the same weight as the words above them —
            // a signature at the foot of a page rather than a toolbar bolted to it.
            ".icons{display:flex;flex-wrap:wrap;gap:20px;margin:0 0 26px;align-items:center}" +
            ".icons.mid{justify-content:center}" +
            ".mark{display:inline-flex;width:21px;height:21px;padding:0;margin:0;border:0;" +
            "background:none;color:var(--ink-2);cursor:pointer;text-decoration:none}" +
            ".mark svg{width:100%;height:100%;display:block}" +
            ".mark:hover,.mark:active{color:var(--accent)}" +

            ".lnk{display:block;width:100%;text-align:left;margin:0 0 10px;padding:15px 17px;" +
            "background:var(--tint);border:0;border-radius:3px;font:inherit;font-size:15px;" +
            "color:var(--accent)}" +
            ".lnk.go{cursor:pointer}" +
            ".lnk.go:active{background:var(--rule)}" +

            ".tip{display:flex;align-items:baseline;justify-content:space-between;gap:14px;" +
            "margin:14px 0 10px;padding:15px 17px;border:1px solid var(--accent);border-radius:3px;" +
            "text-decoration:none;color:var(--ink);font-size:15px}" +
            ".tip-h{font-size:12px;letter-spacing:.06em;color:var(--accent);white-space:nowrap}" +

            ".give{display:flex;flex-direction:column;gap:10px;margin-top:46px;" +
            "padding-top:26px;border-top:1px solid var(--rule)}" +
            ".btn{display:block;text-align:center;background:var(--ink);color:var(--paper);" +
            "text-decoration:none;font-weight:500;font-size:16px;padding:16px 18px;border-radius:3px}" +
            ".size{margin:0;font-size:12.5px;color:var(--ink-2);text-align:center}" +
            ".fine{margin:0;font-size:12px;line-height:1.5;color:var(--ink-3);text-align:center}";
    }

    /// <summary>How big the download is, in a unit a person reads.</summary>
    private static string Size(long bytes) =>
        bytes <= 0 ? "—"
        : bytes < 1024L * 1024 ? $"{bytes / 1024} KB"
        : $"{bytes / (1024.0 * 1024.0):0.#} MB";

    /// <summary>
    /// Escape text for the page body.
    /// </summary>
    /// <remarks>
    /// Every string on a card was typed by one person and is being shown to another. Without this, a
    /// card is a way to run whatever its author likes inside a stranger's browser the moment they are
    /// deciding whether to trust them — which is the exact moment they are least equipped to notice.
    /// </remarks>
    private static string? Text(string? raw)
    {
        if (raw is null) return null;

        var clean = new StringBuilder(raw.Length);

        foreach (var c in raw.Trim())
        {
            switch (c)
            {
                case '&': clean.Append("&amp;"); break;
                case '<': clean.Append("&lt;"); break;
                case '>': clean.Append("&gt;"); break;
                case '"': clean.Append("&quot;"); break;
                case '\'': clean.Append("&#39;"); break;
                default:
                    // Control characters are dropped rather than escaped: nothing legitimate on a card
                    // contains them, and they are how a payload hides from a reader's eye.
                    if (!char.IsControl(c) || c is '\n' or '\t') clean.Append(c);
                    break;
            }
        }

        return clean.ToString();
    }

    /// <summary>Escape a value going into an attribute, where quotes end the value.</summary>
    private static string Attr(string? raw) => Text(raw) ?? "";

    /// <summary>
    /// May this card send the reader somewhere? Only to another card on the mesh.
    /// </summary>
    /// <remarks>
    /// A card is a blob written by a stranger, so its link targets are untrusted input. An
    /// <c>http</c> or <c>javascript</c> target would turn a card into a way to reach out of the mesh
    /// the moment somebody opened it. Anything that is not an <c>aether://</c> address is drawn as
    /// plain text and goes nowhere — the card still renders, the link simply does not work, which is
    /// the safe way round.
    /// </remarks>
    private static bool IsMeshAddress(string? target) => CardBlock.IsMeshAddress(target);
}
