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
        return read?.ToDictionary(p => Sketchbook.Hash(p.Key), p => p.Value);
    }

    /// <summary>A stand-in content hash: letters and digits, exactly as the real ones are.</summary>
    // ── Built, weighed, written out ───────────────────────────────────────────

    [Fact]
    public void The_sketchbook_is_written_out_for_comparison()
    {
        if (Plates() is not { Count: > 0 } plates) return;

        var card = Sketchbook.Built();
        var page = CardExport.Standalone(
            card,
            hash => plates.GetValueOrDefault(hash),
            at: "aether://Y6TK9-EW9KK/sketchbook");

        File.WriteAllText(Path.Combine(Fixtures!, "sketchbook-card.html"), page);

        // Exactly what the background picker puts in one of its tiles, so a background that does not
        // paint can be looked at on its own rather than guessed at through a phone screenshot.
        File.WriteAllText(
            Path.Combine(Fixtures!, "backdrop.html"),
            CardPage.Render(
                new CardDocument
                {
                    Title = "Meng To",
                    Blocks = [CardBlock.Of(CardBlock.Theme, "editorial"), CardBlock.Of(CardBlock.Theme, "tide")],
                },
                "Meng To", 0, downloadPath: null, fonts: PageAssets.Face, still: true, sample: true));

        // The document on its own, so it can be put on a handset and opened by the app rather than
        // looked at in a desktop browser. A page that only renders on a laptop has not been replicated.
        File.WriteAllText(
            Path.Combine(Fixtures!, "sketchbook.json"),
            OwnCard.ForPublish(Sketchbook.Built()).ToJson());

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

        var page = CardExport.Standalone(Sketchbook.Built(), hash => plates.GetValueOrDefault(hash));

        foreach (var (_, name, place) in Sketchbook.Places)
        {
            Assert.Contains(name, page, StringComparison.Ordinal);
            Assert.Contains(place, page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_plates_are_a_gallery_and_the_wash_is_behind_it()
    {
        if (Plates() is not { Count: > 0 } plates) return;

        var page = CardExport.Standalone(Sketchbook.Built(), hash => plates.GetValueOrDefault(hash));

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
        var json = OwnCard.ForPublish(Sketchbook.Built()).ToJson();

        Assert.True(json.Length < 4096, $"{json.Length} bytes");
    }
}
