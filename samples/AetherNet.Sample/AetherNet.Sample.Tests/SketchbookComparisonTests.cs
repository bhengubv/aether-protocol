// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The sketchbook, built as a card, with its own pictures in it — for looking at side by side.
///
/// <para>
/// The bar is that a page on the open web and the same page on AetherNet are the same page. This
/// builds one of them: the same words, the same nine plates, the same wash, through the editor's own
/// operations, and writes it out as the single self-contained file <see cref="CardExport"/> produces.
/// </para>
///
/// <para>
/// The pictures are supplied from outside, already shrunk to what a card actually carries — a hundred
/// and twenty kilobytes each rather than the eight hundred they start at. That is not a shortcut: it
/// is the comparison. A page that only matches when it is allowed to be eight megabytes has not
/// matched, because eight megabytes never crosses a radio.
/// </para>
///
/// <para>
/// Skipped, not failed, when the pictures are not there. It is a comparison fixture, and a machine
/// that has not fetched them should still run the suite.
/// </para>
/// </summary>
public class SketchbookComparisonTests
{
    private static string? Fixtures => Environment.GetEnvironmentVariable("AETHER_FIXTURES");

    /// <summary>The plates, already shrunk, as data URIs keyed by a usable content hash.</summary>
    /// <remarks>
    /// Keyed without punctuation, because a content hash is letters and digits and nothing else — the
    /// model refuses anything that is not, which is what stops a card naming a path or a URL where a
    /// hash belongs. Naming these "bg-wash" made every picture on the page silently vanish, and the
    /// check was right.
    /// </remarks>
    private static Dictionary<string, string>? Plates()
    {
        if (Fixtures is not { Length: > 0 } where) return null;

        var path = Path.Combine(where, "plates.json");
        if (!File.Exists(path)) return null;

        var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        return read?.ToDictionary(p => Hash(p.Key), p => p.Value);
    }

    /// <summary>A stand-in content hash: letters and digits, exactly as the real ones are.</summary>
    private static string Hash(string name) =>
        new([.. name.Where(char.IsAsciiLetterOrDigit)]);

    /// <summary>The nine plates, in the order the page shows them.</summary>
    private static readonly (string File, string Name, string Place)[] Places =
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

    /// <summary>
    /// The page, built the way a person builds one.
    /// </summary>
    /// <remarks>
    /// Every block through <see cref="OwnCard.Add"/> — the same call the editor's buttons make — so
    /// this cannot quietly rely on something the editor does not offer.
    /// </remarks>
    private static CardDocument Built()
    {
        var card = PageTemplate.Of("blank").Build(null);
        card.Blocks.RemoveAll(b => b.Kind == CardBlock.Text);

        card.Title = "Meng To";
        OwnCard.SetLook(card, "editorial");
        OwnCard.SetShader(card, "tide");

        Put(card, CardBlock.Image, hash: Hash("bg-wash"), how: "wash");

        Put(card, CardBlock.Eyebrow, "Designer / Creator / AI Educator / Founder @ Singapore", centred: true);

        Put(card, CardBlock.Link, "Journal", to: "aether://MENGTO/journal", centred: true);
        Put(card, CardBlock.Link, "About", to: "aether://MENGTO/about", centred: true);
        Put(card, CardBlock.Link, "Contact", to: "aether://MENGTO/contact", centred: true);

        Put(card, CardBlock.Image, "The sketchbook, open at Marina Bay", hash: Hash("bloom"), how: "wide");

        Put(card, CardBlock.Heading, "About");
        Put(card, CardBlock.Text,
            "Meng To is a designer, creator and AI educator based in Singapore, founder of " +
            "_Design+Code_, where for over a decade he has taught designers and developers to build " +
            "real apps — from Sketch and Xcode through SwiftUI, and now the AI tools that let one " +
            "person ship what used to take a team.");
        Put(card, CardBlock.Text,
            "His work lives on the seam between craft and code: teaching designers to build, and " +
            "builders to see. This sketchbook is the other half of that — the city looked at slowly, " +
            "*in ink and a little colour*: shophouse shutters, hawker tents, the bay at dusk.");

        Put(card, CardBlock.Rule, "brush");
        Put(card, CardBlock.Heading, "Plates");

        Lines(card, CardBlock.Index, [.. Places.Select(p => $"{p.Name} = {p.Place}")]);

        foreach (var (file, name, _) in Places)
            Put(card, CardBlock.Image, name, hash: Hash(file));

        Put(card, CardBlock.Rule, "scatter");
        Put(card, CardBlock.Text, "Singapore · Sketchbook · _hello@mengto.com_", centred: true);

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

    // ── Built, weighed, written out ───────────────────────────────────────────

    [Fact]
    public void The_sketchbook_is_written_out_for_comparison()
    {
        if (Plates() is not { Count: > 0 } plates) return;

        var card = Built();
        var page = CardExport.Standalone(
            card,
            hash => plates.GetValueOrDefault(hash),
            at: "aether://Y6TK9-EW9KK/sketchbook");

        File.WriteAllText(Path.Combine(Fixtures!, "sketchbook-card.html"), page);

        // What it weighs, next to what the original weighs, in the file this writes.
        var bytes = Encoding.UTF8.GetByteCount(page);
        File.WriteAllText(
            Path.Combine(Fixtures!, "sketchbook-card.txt"),
            $"card: {bytes / 1024} KB in one file, nothing beside it{Environment.NewLine}" +
            $"blocks: {card.Blocks.Count}{Environment.NewLine}" +
            $"pictures: {card.Blocks.Count(b => b.Kind == CardBlock.Image)}{Environment.NewLine}" +
            $"document alone: {OwnCard.ForPublish(card).ToJson().Length} bytes of JSON{Environment.NewLine}");

        Assert.Contains("Meng To", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every plate is on the page, with its place beside it.
    /// </summary>
    [Fact]
    public void Every_plate_is_there()
    {
        if (Plates() is not { Count: > 0 } plates) return;

        var page = CardExport.Standalone(Built(), hash => plates.GetValueOrDefault(hash));

        foreach (var (_, name, place) in Places)
        {
            Assert.Contains(name, page, StringComparison.Ordinal);
            Assert.Contains(place, page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_plates_are_a_gallery_and_the_wash_is_behind_it()
    {
        if (Plates() is not { Count: > 0 } plates) return;

        var page = CardExport.Standalone(Built(), hash => plates.GetValueOrDefault(hash));

        Assert.Contains("class=\"gallery\"", page, StringComparison.Ordinal);
        Assert.Contains("class=\"wash\"", page, StringComparison.Ordinal);
        Assert.Contains("figure class=\"wide\"", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The document itself — the thing that crosses a radio and gets handed on — is a few kilobytes.
    /// </summary>
    /// <remarks>
    /// The pictures are content, fetched by hash and cached; the card is the words and the shape. That
    /// split is why a page with nine paintings on it is still something you can hand to somebody in a
    /// room with no signal.
    /// </remarks>
    [Fact]
    public void The_card_itself_is_a_few_kilobytes()
    {
        var json = OwnCard.ForPublish(Built()).ToJson();

        Assert.True(json.Length < 4096, $"{json.Length} bytes");
    }
}
