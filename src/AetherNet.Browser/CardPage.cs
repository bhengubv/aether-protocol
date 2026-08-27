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
        bool still = false)
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
        page.Append("<style>").Append(Faces(look, fonts, fontBase)).Append(Style(accent, look)).Append("</style>");
        page.Append("</head><body><main class=\"card\">");

        Plate(page, card, name, accent ?? look.Accent, assetPath, still);

        // The eyebrow explains how the page got here, which is only true when it is being handed
        // over. On AetherNet the reader browsed to it, and telling them it was shared with them is a
        // small lie printed above somebody's name.
        if (offering)
            page.Append("<p class=\"eyebrow\">Shared with you, phone to phone</p>");

        // An eyebrow belongs above the title, wherever its author put it — it qualifies the title
        // rather than sitting in the flow, and a page that printed it halfway down would be a page
        // whose author had to know that.
        if (card?.Blocks?.FirstOrDefault(b => b.Kind == CardBlock.Eyebrow) is { } brow
            && Text(brow.Value) is { Length: > 0 } said)
            page.Append("<p class=\"eyebrow\">").Append(said).Append("</p>");

        // No heading at all rather than an invented one. A page whose author has not named it yet is
        // shorter; it is not a page belonging to somebody called nothing.
        if (name.Length > 0)
            page.Append("<h1>").Append(name).Append("</h1>");

        Blocks(page, card, assetPath, Hero(card), offering);

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

    /// <summary>The picture a card leads with, if it names one we can actually show.</summary>
    private static CardBlock? Hero(CardDocument? card) =>
        card?.Blocks?.FirstOrDefault(b =>
            b.Kind == CardBlock.Image && CardBlock.IsUsableAssetHash(b.ContentHash));

    /// <summary>
    /// The masthead: a lit surface in the page's own colour, painted by the shader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what somebody judges before they have read a word, which is why the aesthetic is not
    /// decoration here — a page that looks made is most of the argument for joining a network nobody
    /// has heard of. It is ThreeUI's language: one hue lit toward white, drawn in raw WebGL, because
    /// the character lives in the shader rather than in any library — and no library could travel to a
    /// reader with no internet anyway.
    /// </para>
    /// <para>
    /// Three layers, each standing in for the one above. The card's own content-addressed picture sits
    /// underneath, so a phone with no GPU still shows a masthead rather than a gap; the canvas covers
    /// it when the shader runs; and behind both is the flat accent, which is what a page with neither
    /// comes out as. The colour is an already-validated hex value and the seed is computed in the
    /// script, so nothing an author typed is ever handed to it.
    /// </para>
    /// </remarks>
    private static void Plate(
        StringBuilder page, CardDocument? card, string name, string accent,
        Func<string, string?>? assetPath, bool still)
    {
        var art = Hero(card) is { } hero ? assetPath?.Invoke(hero.ContentHash!) : null;

        // Somebody's photograph is the subject. Painting a shader across it is not a design choice,
        // and the difference is already in the content type — vector art is a backdrop this app drew,
        // a photograph is a thing that happened to a person.
        var photograph = PagePhoto.IsPhotograph(art);

        // The class rather than :has(). This page is read on whatever browser the reader happens to
        // have, and a selector their engine does not know does not degrade — it simply never matches,
        // and the scrim that makes the mark legible over a photograph silently is not there.
        page.Append("<div class=\"plate").Append(photograph ? " shot" : "").Append("\">");

        if (art is { Length: > 0 })
            page.Append("<img class=\"plate-art\" src=\"")
                .Append(Attr(art))
                .Append("\" alt=\"\">");

        if (photograph)
        {
            page.Append("<span class=\"mark quiet\">").Append(Mark(name)).Append("</span>");
            page.Append("</div>");
            return;
        }

        page.Append("<canvas class=\"plate-gl\" data-aether-shader data-accent=\"")
            .Append(Attr(accent))
            .Append("\" data-seed=\"")
            .Append(Seed(name))
            .Append(still ? "\" data-still" : "\"")
            .Append("></canvas>");

        page.Append("<span class=\"mark\">").Append(Mark(name)).Append("</span>");
        page.Append("</div>");
    }

    /// <summary>
    /// How this page folds, as a number.
    /// </summary>
    /// <remarks>
    /// Computed here so nothing an author typed is ever handed to the shader — the script reads a
    /// colour and an integer, and neither can be anything but a colour and an integer. Stable, so a
    /// page wears the same masthead every time anybody opens it.
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

    /// <summary>The single character the masthead is built around.</summary>
    private static string Mark(string name)
    {
        foreach (var c in name)
            if (char.IsLetterOrDigit(c))
                return Text(char.ToUpperInvariant(c).ToString()) ?? "";

        return "A";
    }

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
        StringBuilder page, CardDocument? card, Func<string, string?>? assetPath, CardBlock? hero,
        bool offering)
    {
        if (card?.Blocks is not { Count: > 0 } blocks) return;

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case CardBlock.Heading when Text(block.Value) is { Length: > 0 } h:
                    page.Append("<h2>").Append(h).Append("</h2>");
                    break;

                // Drawn above the title, not here — see Render. Skipping it means an author can put
                // it wherever they like in the document and it still lands where it belongs.
                case CardBlock.Eyebrow:
                    break;

                case CardBlock.Quote when Text(block.Value) is { Length: > 0 } q:
                    page.Append("<blockquote>").Append(q).Append("</blockquote>");
                    break;

                case CardBlock.Rule:
                    page.Append("<hr>");
                    break;

                // The plate index. Each line is "name = place"; a line with no place is still a line,
                // because a catalogue with one unlabelled entry should not lose the entry.
                case CardBlock.Index when block.Items is { Count: > 0 } plates:
                    page.Append("<div class=\"index\">");

                    var at = 0;
                    foreach (var plate in plates)
                    {
                        if (Text(plate) is not { Length: > 0 } line) continue;

                        var split = line.IndexOf('=');
                        at++;

                        page.Append("<div class=\"plate-row\"><span class=\"plate-n\">")
                            .Append(at.ToString("00"))
                            .Append("</span><span class=\"plate-t\">")
                            .Append((split > 0 ? line[..split] : line).Trim())
                            .Append("</span><span class=\"plate-p\">")
                            .Append(split > 0 ? line[(split + 1)..].Trim() : "")
                            .Append("</span></div>");
                    }

                    page.Append("</div>");
                    break;

                case CardBlock.Text when Text(block.Value) is { Length: > 0 } t:
                    page.Append("<p class=\"say\">").Append(t).Append("</p>");
                    break;

                case CardBlock.List when block.Items is { Count: > 0 } items:
                    page.Append("<ul>");
                    foreach (var item in items)
                        if (Text(item) is { Length: > 0 } li) page.Append("<li>").Append(li).Append("</li>");
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

                case CardBlock.Image when !ReferenceEquals(block, hero)
                                          && assetPath?.Invoke(block.ContentHash ?? "") is { Length: > 0 } src
                                          && CardBlock.IsUsableAssetHash(block.ContentHash):
                    page.Append("<figure><img src=\"").Append(Attr(src))
                        .Append("\" alt=\"").Append(Text(block.Value) ?? "").Append("\">");

                    if (Text(block.Value) is { Length: > 0 } caption)
                        page.Append("<figcaption>").Append(caption).Append("</figcaption>");

                    page.Append("</figure>");
                    break;
            }
        }
    }

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
    private static string Style(string? accent, CardLook look)
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
            "body{margin:0;background:var(--paper);color:var(--ink);" +
            "font-family:var(--body);font-weight:var(--weight);font-size:var(--size);" +
            "line-height:var(--leading);-webkit-font-smoothing:antialiased;" +
            "text-rendering:optimizeLegibility}" +

            ".card{display:flex;flex-direction:column;align-items:stretch;" +
            "gap:0;padding:0 22px 84px;max-width:var(--measure);margin:0 auto}" +

            // Out past the measure to the edges of the window: a full-bleed element inside a centred
            // column. The negative margin is the gutter and the width puts it back.
            ".plate{position:relative;width:100vw;max-width:100vw;margin:0 calc(50% - 50vw) 30px;" +
            "aspect-ratio:600/250;max-height:48vh;overflow:hidden;background:var(--accent)}" +
            ".plate-art,.plate-gl{position:absolute;inset:0;width:100%;height:100%;display:block;border:0}" +
            ".plate-art{object-fit:cover}" +
            ".mark{position:absolute;left:22px;bottom:2px;font-family:var(--display);" +
            "font-size:clamp(62px,13vw,108px);line-height:1;font-weight:400;letter-spacing:-.02em;" +
            "color:#fff;opacity:.95}" +
            ".mark.quiet{font-size:clamp(38px,8vw,58px);text-shadow:0 1px 14px rgba(0,0,0,.55);z-index:1}" +
            ".plate.shot::after{content:'';position:absolute;inset:auto 0 0 0;height:52%;" +
            "background:linear-gradient(to top,rgba(0,0,0,.52),transparent);pointer-events:none}" +

            // Spacing is vertical rhythm, not a uniform gap. What a thing is decides how much air it
            // gets above it, which is most of what makes a page feel set rather than stacked.
            ".eyebrow{margin:0 0 14px;font-size:12px;letter-spacing:.24em;text-transform:uppercase;" +
            "color:var(--ink-2);font-weight:400}" +

            "h1{margin:0;font-family:var(--display);font-size:clamp(38px,9vw,64px);line-height:1.02;" +
            "letter-spacing:-.02em;font-weight:400;text-wrap:balance}" +

            "h2{margin:46px 0 14px;font-size:12px;letter-spacing:.22em;text-transform:uppercase;" +
            "color:var(--ink-2);font-weight:400}" +

            ".say{margin:0 0 22px;max-width:100%}" +
            ".say+.say{margin-top:-4px}" +

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
            ".plate-t{flex:1 1 auto;min-width:0;font-family:var(--display);font-size:20px;" +
            "line-height:1.2;font-weight:400}" +
            ".plate-p{flex:0 0 auto;font-size:12.5px;letter-spacing:.08em;text-transform:uppercase;" +
            "color:var(--ink-3);text-align:right}" +

            "hr{margin:38px 0;border:0;border-top:1px solid var(--rule)}" +

            // A picture with something written under it is a figure. The caption is small, quiet and
            // close to the image — far enough to be a caption, near enough to belong to it.
            "figure{margin:6px 0 28px;width:100%}" +
            "figure img{width:100%;height:auto;display:block;border-radius:2px}" +
            "figcaption{margin-top:9px;font-size:12.5px;letter-spacing:.02em;color:var(--ink-3);" +
            "line-height:1.5}" +

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
    private static bool IsMeshAddress(string? target) =>
        target is { Length: > 0 and < 512 } &&
        target.StartsWith("aether://", StringComparison.OrdinalIgnoreCase);
}
