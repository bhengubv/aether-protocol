// SPDX-License-Identifier: MIT

using System.Text;

namespace AetherNet.Sample.Shared.Services;

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
    /// <summary>The palettes a person can choose between.</summary>
    /// <remarks>
    /// Five, named rather than free-form, and this is the whole of the personalisation. Somewhere to
    /// express yourself without every card becoming unreadable in its own particular way — the choice
    /// is real, and the result still belongs to one family.
    /// </remarks>
    public static readonly string[] Palettes = ["mono", "sepia", "azure", "moss", "mauve"];

    /// <summary>What a card gets when it asks for nothing.</summary>
    public const string DefaultPalette = "mono";

    /// <summary>Whether this is a palette we know how to draw.</summary>
    public static bool IsPalette(string? name) =>
        name is not null && Palettes.Contains(name.Trim().ToLowerInvariant());

    /// <summary>
    /// The palette a card asked for, or the default.
    /// </summary>
    /// <remarks>
    /// Read from a theme block. A card asking for something we do not have is drawn in the default
    /// rather than refused — an unknown choice is a newer author, not a broken card.
    /// </remarks>
    public static string PaletteOf(CardDocument? card)
    {
        var asked = card?.Blocks?
            .FirstOrDefault(b => b.Kind == CardBlock.Theme && IsPalette(b.Value))?
            .Value?.Trim().ToLowerInvariant();

        return asked ?? DefaultPalette;
    }

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
    /// <param name="downloadPath">Where the button points — a path on this same phone.</param>
    /// <param name="assetPath">
    ///   Turns a content hash into a path this page can fetch it from, or null when the bytes are not
    ///   available. Passed in rather than assumed, so the renderer never invents an address.
    /// </param>
    public static string Render(
        CardDocument? card,
        string? who,
        long sizeBytes,
        string downloadPath,
        Func<string, string?>? assetPath = null)
    {
        var name = Text(who) is { Length: > 0 } n ? n : "Someone next to you";
        var palette = PaletteOf(card);
        var accent = AccentOf(card);

        var page = new StringBuilder(4096);

        page.Append("<!doctype html><html lang=\"en\" data-palette=\"").Append(palette).Append("\">");
        page.Append("<head><meta charset=\"utf-8\">");
        page.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        page.Append("<title>").Append(name).Append("</title>");
        page.Append("<style>").Append(Style(accent)).Append("</style>");
        page.Append("</head><body><main class=\"card\">");

        page.Append("<p class=\"eyebrow\">Shared with you, phone to phone</p>");
        page.Append("<h1>").Append(name).Append("</h1>");

        Blocks(page, card, assetPath);

        page.Append("<div class=\"give\">");
        page.Append("<a class=\"btn\" href=\"").Append(Attr(downloadPath)).Append("\">Get Aether</a>");
        page.Append("<p class=\"size\">").Append(Size(sizeBytes)).Append(" · comes off the phone beside you, not the internet</p>");
        page.Append("<p class=\"fine\">Your phone will ask whether you're sure. That's normal — it asks that for anything that didn't come from a shop.</p>");
        page.Append("</div>");

        page.Append("</main></body></html>");
        return page.ToString();
    }

    /// <summary>
    /// Draw each block, skipping any kind this renderer does not know.
    /// </summary>
    /// <remarks>
    /// Skipping rather than failing is the compatibility rule the card model was built on: a person on
    /// a newer version can write a block an older reader has never heard of, and the older reader still
    /// shows them a card instead of an error.
    /// </remarks>
    private static void Blocks(StringBuilder page, CardDocument? card, Func<string, string?>? assetPath)
    {
        if (card?.Blocks is not { Count: > 0 } blocks) return;

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case CardBlock.Heading when Text(block.Value) is { Length: > 0 } h:
                    page.Append("<h2>").Append(h).Append("</h2>");
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

                // Rendered as text, never as an anchor. A link inside the mesh is meaningless to a
                // stranger who has no Aether yet, and making it clickable would be an invitation to
                // somewhere their phone cannot reach.
                case CardBlock.Link when Text(block.Value) is { Length: > 0 } label:
                    page.Append("<div class=\"lnk\">").Append(label).Append("</div>");
                    break;

                case CardBlock.Image when assetPath?.Invoke(block.ContentHash ?? "") is { Length: > 0 } src
                                          && CardBlock.IsUsableAssetHash(block.ContentHash):
                    page.Append("<img src=\"").Append(Attr(src))
                        .Append("\" alt=\"").Append(Text(block.Value) ?? "").Append("\">");
                    break;
            }
        }
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
    private static string Style(string? accent)
    {
        var swatch = accent is not null ? $"--accent:{accent};" : "";

        return
            ":root{--r:7px;--rl:14px;" +
            "--font:ui-monospace,SFMono-Regular,Menlo,Consolas,'DejaVu Sans Mono',monospace;" +
            "--page:#ececeb;--wall:#f3f3f2;--surface:#f7f7f6;--content:#fbfbfa;" +
            "--fg:#121211;--fg-2:#5c5c5a;--accent:#1c1c1a;" +
            "--border:rgba(28,28,26,.09);--tint:rgba(28,28,26,.035);" +
            "--fill:#0c0c0b;--onfill:#fbfbfa;color-scheme:light dark}" +

            // Mono is the default, and it still gets its own rule. Relying on the root values would make
            // one palette of five work by accident and break the moment a default moves.
            "html[data-palette=mono]{--page:#ececeb;--wall:#f3f3f2;--surface:#f7f7f6;--content:#fbfbfa;" +
            "--fg:#121211;--fg-2:#5c5c5a;--accent:#1c1c1a;--border:rgba(28,28,26,.09);--fill:#0c0c0b;--onfill:#fbfbfa}" +

            "html[data-palette=sepia]{--page:#efe9df;--wall:#f5f0e8;--surface:#f9f5ef;--content:#fcfaf6;" +
            "--fg:#1c1611;--fg-2:#61564a;--accent:#7a4a1e;--border:rgba(28,22,17,.1);--fill:#3a2a1a;--onfill:#fcfaf6}" +

            "html[data-palette=azure]{--page:#e7ecf2;--wall:#eef2f7;--surface:#f4f7fb;--content:#f9fbfd;" +
            "--fg:#0f1720;--fg-2:#4b5b6b;--accent:#1a4f7a;--border:rgba(15,23,32,.1);--fill:#123a5a;--onfill:#f9fbfd}" +

            "html[data-palette=moss]{--page:#e7ede6;--wall:#eef2ed;--surface:#f4f7f3;--content:#f9fbf9;" +
            "--fg:#111811;--fg-2:#4d5a4c;--accent:#2c5a33;--border:rgba(17,24,17,.1);--fill:#1c3a20;--onfill:#f9fbf9}" +

            "html[data-palette=mauve]{--page:#eee9ef;--wall:#f3eff4;--surface:#f7f4f8;--content:#fbf9fc;" +
            "--fg:#181119;--fg-2:#584c5b;--accent:#5c2f6b;--border:rgba(24,17,25,.1);--fill:#3a1f42;--onfill:#fbf9fc}" +

            "@media(prefers-color-scheme:dark){" +
            ":root,html[data-palette=mono]{--page:#050608;--wall:#0a0b0d;--surface:#101113;--content:#131416;" +
            "--fg:#f7f8f8;--fg-2:#8a8f98;--accent:#f7f8f8;--border:rgba(255,255,255,.075);" +
            "--tint:rgba(255,255,255,.026);--fill:#edeef0;--onfill:#0a0b0d}" +
            "html[data-palette=sepia]{--page:#0a0805;--wall:#100d09;--surface:#16120c;--content:#1a1610;" +
            "--fg:#f7f2e9;--fg-2:#9a8f7e;--accent:#d7a464;--fill:#e8d5b5;--onfill:#100d09}" +
            "html[data-palette=azure]{--page:#04070a;--wall:#090d12;--surface:#0e141b;--content:#12181f;" +
            "--fg:#eef4fa;--fg-2:#7f8f9f;--accent:#6fb2e8;--fill:#d5e6f5;--onfill:#090d12}" +
            "html[data-palette=moss]{--page:#050805;--wall:#0a0e09;--surface:#0f140e;--content:#131812;" +
            "--fg:#eff5ee;--fg-2:#849184;--accent:#7cc088;--fill:#d7ecd9;--onfill:#0a0e09}" +
            "html[data-palette=mauve]{--page:#07050a;--wall:#0c090f;--surface:#120e15;--content:#161219;" +
            "--fg:#f4eef7;--fg-2:#948a99;--accent:#c08ad2;--fill:#e8d5ef;--onfill:#0c090f}}" +

            $":root{{{swatch}}}" +

            "*{box-sizing:border-box}" +
            "body{margin:0;background:var(--page);color:var(--fg);font-family:var(--font);" +
            "font-size:15px;line-height:1.6;-webkit-font-smoothing:antialiased;" +
            "display:flex;justify-content:center;padding:28px 18px 60px}" +

            ".card{width:100%;max-width:420px;background:var(--content);border:1px solid var(--border);" +
            "border-radius:var(--rl);padding:26px 22px;display:flex;flex-direction:column;gap:14px}" +

            ".eyebrow{margin:0;font-size:10.5px;letter-spacing:.14em;text-transform:uppercase;color:var(--fg-2)}" +
            "h1{margin:0;font-size:26px;line-height:1.15;letter-spacing:-.02em;font-weight:700}" +
            "h2{margin:10px 0 -4px;font-size:12px;letter-spacing:.1em;text-transform:uppercase;color:var(--fg-2);font-weight:600}" +
            ".say{margin:0;color:var(--fg-2)}" +
            "ul{margin:0;padding-left:1.1em;display:flex;flex-direction:column;gap:5px;color:var(--fg-2)}" +
            ".kv{display:flex;justify-content:space-between;gap:12px;padding:7px 10px;background:var(--tint);" +
            "border-radius:var(--r);font-size:13px}" +
            ".kv span:first-child{color:var(--fg-2)}" +
            ".lnk{padding:7px 10px;background:var(--tint);border-radius:var(--r);font-size:13px;color:var(--accent)}" +
            "img{width:100%;height:auto;display:block;border-radius:var(--r);border:1px solid var(--border)}" +

            ".give{display:flex;flex-direction:column;gap:9px;margin-top:6px;" +
            "padding-top:16px;border-top:1px solid var(--border)}" +
            ".btn{display:block;text-align:center;background:var(--fill);color:var(--onfill);" +
            "text-decoration:none;font-weight:600;font-size:15px;padding:14px 16px;border-radius:var(--r)}" +
            ".size{margin:0;font-size:12px;color:var(--fg-2);text-align:center}" +
            ".fine{margin:0;font-size:11.5px;line-height:1.5;color:var(--fg-2);text-align:center;opacity:.85}";
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
}
