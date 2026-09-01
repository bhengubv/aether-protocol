// SPDX-License-Identifier: MIT

using System.Text;

namespace AetherNet.Browser;

/// <summary>
/// A card, as something you write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The card model is a list of typed blocks, which is the right thing to send
/// over a radio and the wrong thing to type into. Asked for a page, the editor handed people a form:
/// a panel per block, a labelled blank per field. Two real pages went a hundred versions through that
/// form and neither of them ever got written, while the one card in the library that looks like
/// anything was authored in C# in a test file. A tool its own author avoids is not a tool.
/// </para>
/// <para>
/// <b>What replaces it.</b> The page is a document. You write it, the way you would write anything,
/// and the blocks fall out of what you wrote. Nothing is lost by doing it this way round — the blocks
/// are still exactly what travels, still typed, still renderable by an implementation that has never
/// heard of this file. They stop being the thing you fill in.
/// </para>
/// <para>
/// <b>Both directions, losslessly.</b> <see cref="From"/> turns a card into its document and
/// <see cref="Read"/> turns it back, and the round trip has to be exact — because the point is not
/// authoring from nothing. It is opening a card somebody handed you, seeing how it was made, changing
/// a line and breaking it and fixing it. A converter that quietly drops the parts it does not
/// understand would make every card you were given a little poorer for having been opened.
/// </para>
/// <para>
/// <b>The vocabulary.</b> Line-led, and deliberately the one people already half-know:
/// </para>
/// <code>
/// # a title              ## a heading           ### a small label
/// a paragraph            - a list line          1. a name = where it goes
/// name = value           &gt; a quotation           ---
/// =&gt; [words](where)      !! something worth saying
/// ![caption](hash)       %theme night           %style ink = #ffffff
/// </code>
/// <para>
/// Emphasis inside a sentence is <see cref="CardMarks"/>'s and is left alone here. A stylesheet is
/// not in the document at all — it is written in its own editor, because CSS is code and prose is
/// not, and putting them in one box makes both worse.
/// </para>
/// </remarks>
public static class CardText
{
    /// <summary>Blocks that are never part of the written document.</summary>
    /// <remarks>
    /// A stylesheet has its own editor beside this one. Keeping it out of the prose means a card can
    /// be re-read as a document without a hundred lines of CSS in the middle of the sentences.
    /// </remarks>
    private static bool Aside(CardBlock block) => block.Kind == CardBlock.Css;

    /// <summary>Whether a block has anything for a reader to see.</summary>
    /// <remarks>
    /// A block with no words is a husk. The block model let them accumulate — a template shipped
    /// "Where =" and "Reach me =" with nothing in them, the renderer dropped them, and the author was
    /// left looking at a form of blanks rather than a page. In a document there is simply nothing to
    /// type for such a block, so writing a card out and reading it back is also how they leave. That
    /// is the editor's existing promise — anything left blank does not appear — made true of what is
    /// stored and not only of what is drawn.
    ///
    /// <para>
    /// A rule is exempt: a line across the page is the whole of its content. A picture is exempt: its
    /// words are a caption and its content is the hash.
    /// </para>
    /// </remarks>
    private static bool Says(CardBlock block) =>
        block.Kind is CardBlock.Rule
        || block.ContentHash is { Length: > 0 }
        || block.Value is { Length: > 0 }
        || block.Items is { Count: > 0 };

    // ── A card, written out ─────────────────────────────────────────────────────

    /// <summary>The document for a card — what its author sees when they open it.</summary>
    public static string From(CardDocument? card)
    {
        if (card?.Blocks is not { Count: > 0 } blocks) return "";

        var said = new StringBuilder();

        foreach (var block in blocks)
        {
            if (Aside(block) || !Says(block)) continue;

            var line = Line(block);
            if (line is null) continue;

            // A blank line between blocks, because this is read as much as it is parsed. Joined
            // with one newline the page came back as a wall - every paragraph, heading and list
            // butted against the next - and the blank lines somebody typed to give their own
            // writing room were quietly lost the moment they reopened it. The parser does not
            // need them; the person does.
            if (said.Length > 0) said.Append("\n\n");
            said.Append(line);
        }

        return said.ToString();
    }

    /// <summary>One block as its line — or lines, for the kinds that hold a list.</summary>
    private static string? Line(CardBlock block)
    {
        var said = block.Value ?? "";
        var how = Marker(block);

        return block.Kind switch
        {
            CardBlock.Title => "# " + said + how,
            CardBlock.Heading => "## " + said + how,
            CardBlock.Eyebrow => "### " + said + how,
            CardBlock.Text => said + how,
            CardBlock.Quote => "> " + said + how,
            CardBlock.Rule => said.Length > 0 ? "--- " + said : "---",
            CardBlock.KeyValue => said + how,
            CardBlock.Tip => "!! " + said + how,
            CardBlock.Link => "=> [" + said + "](" + (block.Target ?? "") + ")" + how,
            CardBlock.Image => "![" + said + "](" + (block.ContentHash ?? "") + ")" + how,
            CardBlock.Theme => "%theme " + said,
            CardBlock.List => Lines("- ", block),
            CardBlock.Index => Lines("1. ", block),
            CardBlock.Style => Lines("%style ", block),
            _ => null,
        };
    }

    /// <summary>The kinds that hold several lines, one line each.</summary>
    private static string Lines(string lead, CardBlock block)
    {
        if (block.Items is not { Count: > 0 } items) return lead.TrimEnd();

        var said = new StringBuilder();

        foreach (var item in items)
        {
            if (said.Length > 0) said.Append('\n');
            said.Append(lead).Append(item);
        }

        return said.ToString();
    }

    /// <summary>How a block is set, when it is set at all — written at the end of its line.</summary>
    private static string Marker(CardBlock block)
    {
        var how = new StringBuilder();

        if (block.IsCentred) how.Append(" ::centre");
        if (block.As is { Length: > 0 } dressed) how.Append(" ::").Append(dressed);

        return how.ToString();
    }

    // ── A document, read back ───────────────────────────────────────────────────

    /// <summary>
    /// The card a document describes.
    /// </summary>
    /// <param name="written">What the author typed.</param>
    /// <param name="keeping">
    ///   The card being edited, if there is one, so the parts that are not in the document survive
    ///   the trip. Only the stylesheet is held this way, and it is put back in the place the renderer
    ///   expects rather than wherever it happened to be.
    /// </param>
    public static CardDocument Read(string? written, CardDocument? keeping = null)
    {
        var card = new CardDocument();
        var lines = (written ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        CardBlock? gathering = null;
        string? gatheringLead = null;

        void Done()
        {
            if (gathering is not null) card.Blocks.Add(gathering);
            gathering = null;
            gatheringLead = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.Trim().Length == 0) { Done(); continue; }

            // The two kinds that gather consecutive lines into one block.
            var lead = line.StartsWith("- ", StringComparison.Ordinal) ? "- "
                : Numbered(line) ? "1. "
                : line.StartsWith("%style ", StringComparison.Ordinal) ? "%style "
                : null;

            if (lead is not null)
            {
                var item = lead == "1. " ? line[(line.IndexOf('.') + 1)..].TrimStart() : line[lead.Length..];

                if (gathering is not null && gatheringLead == lead)
                {
                    gathering.Items!.Add(item);
                    continue;
                }

                Done();
                gatheringLead = lead;
                gathering = new CardBlock
                {
                    Kind = lead switch
                    {
                        "- " => CardBlock.List,
                        "1. " => CardBlock.Index,
                        _ => CardBlock.Style,
                    },
                    Items = [item],
                };
                continue;
            }

            Done();

            if (One(line) is { } block) card.Blocks.Add(block);
        }

        Done();

        card.Title = card.Blocks.FirstOrDefault(b => b.Kind == CardBlock.Title)?.Value ?? "";

        if (keeping?.Blocks?.FirstOrDefault(b => b.Kind == CardBlock.Css) is { } css)
            OwnCard.SetCss(card, css.Value ?? "");

        return card;
    }

    /// <summary>Whether a line opens with "1." — the numbered form an index takes.</summary>
    private static bool Numbered(string line)
    {
        var dot = line.IndexOf('.');
        if (dot <= 0 || dot + 1 >= line.Length || line[dot + 1] != ' ') return false;

        for (var i = 0; i < dot; i++)
            if (!char.IsAsciiDigit(line[i]))
                return false;

        return true;
    }

    /// <summary>A single line as the block it describes.</summary>
    private static CardBlock? One(string line)
    {
        var (said, align, dressed) = Unmark(line);

        // Absent is null, not "". A picture with no caption has no words at all, and writing it as an
        // empty string is a different document from the one that was handed over.
        CardBlock Of(string kind, string value) => new()
        {
            Kind = kind,
            Value = value.Length > 0 ? value : null,
            Align = align,
            As = dressed,
        };

        if (said.StartsWith("### ", StringComparison.Ordinal)) return Of(CardBlock.Eyebrow, said[4..]);
        if (said.StartsWith("## ", StringComparison.Ordinal)) return Of(CardBlock.Heading, said[3..]);
        if (said.StartsWith("# ", StringComparison.Ordinal)) return Of(CardBlock.Title, said[2..]);
        if (said.StartsWith("> ", StringComparison.Ordinal)) return Of(CardBlock.Quote, said[2..]);
        if (said.StartsWith("!! ", StringComparison.Ordinal)) return Of(CardBlock.Tip, said[3..]);

        if (said.StartsWith("%theme ", StringComparison.Ordinal))
            return Of(CardBlock.Theme, said[7..].Trim());

        if (said == "---") return new CardBlock { Kind = CardBlock.Rule };
        if (said.StartsWith("--- ", StringComparison.Ordinal))
            return Of(CardBlock.Rule, said[4..].Trim());

        if (said.StartsWith("=> ", StringComparison.Ordinal) && Bracketed(said[3..]) is { } link)
        {
            var block = Of(CardBlock.Link, link.Words);
            block.Target = link.Where;
            return block;
        }

        if (said.StartsWith("![", StringComparison.Ordinal) && Bracketed(said[1..]) is { } picture)
        {
            var block = Of(CardBlock.Image, picture.Words);
            block.ContentHash = picture.Where;
            return block;
        }

        // A name, an equals and a value is the one form people write without being taught it.
        if (said.Contains('=', StringComparison.Ordinal) && said.IndexOf('=') > 0)
            return Of(CardBlock.KeyValue, said);

        return said.Length > 0 ? Of(CardBlock.Text, said) : null;
    }

    /// <summary><c>[words](where)</c>, or nothing at all.</summary>
    private static (string Words, string Where)? Bracketed(string said)
    {
        if (!said.StartsWith('[')) return null;

        var close = said.IndexOf(']');
        if (close < 0 || close + 1 >= said.Length || said[close + 1] != '(') return null;

        var end = said.LastIndexOf(')');
        if (end <= close + 1) return null;

        return (said[1..close], said[(close + 2)..end]);
    }

    /// <summary>Take the trailing <c>::how</c> markers off a line and say what they said.</summary>
    private static (string Said, string? Align, string? As) Unmark(string line)
    {
        string? align = null, dressed = null;

        while (true)
        {
            var at = line.LastIndexOf(" ::", StringComparison.Ordinal);
            if (at < 0) break;

            var word = line[(at + 3)..].Trim();
            if (word.Length == 0 || word.Contains(' ')) break;

            if (word.Equals("centre", StringComparison.OrdinalIgnoreCase)) align = "centre";
            else dressed = word;

            line = line[..at].TrimEnd();
        }

        return (line, align, dressed);
    }
}
