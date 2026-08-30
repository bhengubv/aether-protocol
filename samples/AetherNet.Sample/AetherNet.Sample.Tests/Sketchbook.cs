// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Meng To's sketchbook, rebuilt as a card — the one page everything here is measured against.
///
/// <para>
/// <b>Built the way a person builds one.</b> Blank template, then blocks added one at a time through
/// <see cref="OwnCard.Add"/> — the same call the editor's buttons make — and filled in. Nothing is
/// hand-assembled, so the page cannot quietly depend on something the editor does not offer. If a
/// kind ever leaves <see cref="OwnCard.Writable"/> this stops compiling into a page and the tests
/// either side say so.
/// </para>
///
/// <para>
/// <b>One copy of it.</b> There were two, and they drifted: the page the standard made its
/// assertions about and the page written out to be looked at were no longer the same page, so a
/// capability could be proved in one and missing from the other. Both now build this.
/// </para>
///
/// <para>
/// <b>What is deliberately out of reach.</b> The sketchbook you drag to turn, and the glass you move
/// across it. A card is opened on a stranger's phone, so it does not run its author's code. Everything
/// a reader <i>looks at</i> is in scope; everything they play with is not.
/// </para>
/// </summary>
internal static class Sketchbook
{
    /// <summary>The nine plates, in the order the page shows them.</summary>
    internal static readonly (string File, string Name, string Place)[] Places =
    [
        ("marina-bay-sands", "Marina Bay Sands", "Bayfront"),
        ("gardens-by-the-bay", "Gardens by the Bay", "Supertree Grove"),
        ("merlion", "The Merlion", "Merlion Park"),
        ("buddha-tooth", "Buddha Tooth Relic Temple", "Chinatown"),
        ("joo-chiat", "Joo Chiat Shophouses", "Katong"),
        ("lau-pa-sat", "Lau Pa Sat", "Raffles Quay"),
        ("marina-bay-skyline", "Marina Bay Skyline", "The Bay"),
        ("singapore-river", "Singapore River", "Boat Quay"),
        ("botanic-gardens", "Botanic Gardens", "Tanglin"),
    ];

    /// <summary>A stand-in content hash: letters and digits, exactly as the real ones are.</summary>
    /// <remarks>
    /// A content hash is letters and digits and nothing else — the model refuses anything that is not,
    /// which is what stops a card naming a path or a URL where a hash belongs. Naming these "bg-wash"
    /// made every picture on the page silently vanish, and the check was right.
    /// </remarks>
    internal static string Hash(string name) => new([.. name.Where(char.IsAsciiLetterOrDigit)]);

    /// <summary>The page itself.</summary>
    internal static CardDocument Built()
    {
        var card = PageTemplate.Of("blank").Build(null);
        card.Blocks.RemoveAll(b => b.Kind == CardBlock.Text);

        OwnCard.SetLook(card, "editorial");
        OwnCard.SetShader(card, "tide");

        Put(card, CardBlock.Image, hash: Hash("bg-wash"), how: "wash");

        // The order at the top of that page: the name quietly first as a wordmark, the navigation
        // under it, and only then the line saying who this is. Every one of those is a decision the
        // author made and none of them are available while a renderer lifts the title to the top and
        // the label above it — which is what this one used to do, and why the page came out reordered.
        Put(card, CardBlock.Title, "Meng To", how: "small", centred: true);
        card.Title = "Meng To";

        Put(card, CardBlock.Link, "Journal", to: "aether://MENGTO/journal", centred: true);
        Put(card, CardBlock.Link, "About", to: "aether://MENGTO/about", centred: true);
        Put(card, CardBlock.Link, "Contact", to: "aether://MENGTO/contact", centred: true);

        // The row of small marks under the navigation. Every page that points at the rest of
        // somebody's life has one, and spelled out as words they take a paragraph and read as a menu.
        // They say what the link is for rather than whose logo it is — see CardIcon.
        Put(card, CardBlock.Link, "Writing", to: "https://designcode.io/journal", how: "doc", centred: true);
        Put(card, CardBlock.Link, "Profile", to: "https://designcode.io/about", how: "person", centred: true);
        Put(card, CardBlock.Link, "Photographs", to: "https://designcode.io/gallery", how: "photo", centred: true);
        Put(card, CardBlock.Link, "Email", to: "https://designcode.io/contact", how: "mail", centred: true);

        Put(card, CardBlock.Eyebrow, "Designer / Creator / AI Educator / Founder @ Singapore",
            centred: true);

        Put(card, CardBlock.Image, "Marina Bay Skyline",
            hash: Hash("marina-bay-skyline"), how: "wide", centred: true);

        Put(card, CardBlock.Heading, "About");

        // All three of the emphasised phrases on that page are links — the studio, the medium, the
        // address. A card had no way to say that until prose could link, so they were written as
        // emphasis and the page was nearly right for a reason nobody would find by looking at it.
        Put(card, CardBlock.Text,
            "Meng To is a designer, creator and AI educator based in Singapore, founder of " +
            "[Design+Code](https://designcode.io), where for over a decade he has taught designers " +
            "and developers to build real apps — from Sketch and Xcode through SwiftUI, and now the " +
            "AI tools that let one person ship what used to take a team.");
        Put(card, CardBlock.Text,
            "His work lives on the seam between craft and code: teaching designers to build, and " +
            "builders to see. This sketchbook is the other half of that — the city looked at slowly, " +
            "[*in ink and a little colour*](https://designcode.io/sketchbook): shophouse shutters, " +
            "hawker tents, the bay at dusk.");

        Put(card, CardBlock.Rule, "brush");
        Put(card, CardBlock.Heading, "Plates");

        Lines(card, CardBlock.Index, [.. Places.Select(p => $"{p.Name} = {p.Place}")]);

        // Nine plates, added the way somebody adds pictures — one after another. Consecutive pictures
        // become a gallery; nobody has to know that.
        foreach (var (file, name, _) in Places)
            Put(card, CardBlock.Image, name, hash: Hash(file));

        Put(card, CardBlock.Rule, "scatter");
        Put(card, CardBlock.Text,
            "Singapore · Sketchbook · [hello@mengto.com](https://designcode.io/contact)", centred: true);

        return OwnCard.Tidy(card);
    }

    private static void Put(
        CardDocument card, string kind, string? value = null,
        string? to = null, string? hash = null, string? how = null, bool centred = false)
    {
        Assert.True(OwnCard.Add(card, kind), $"the editor does not offer {kind}");

        var block = card.Blocks[^1];
        block.Value = value;
        block.Target = to;
        block.ContentHash = hash;
        block.As = how;
        if (centred) block.Align = "centre";
    }

    private static void Lines(CardDocument card, string kind, string[] lines)
    {
        Assert.True(OwnCard.Add(card, kind), $"the editor does not offer {kind}");
        card.Blocks[^1].Items = [.. lines];
    }
}
