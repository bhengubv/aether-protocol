// SPDX-License-Identifier: MIT

using System.Text;

namespace AetherNet.Browser;

/// <summary>
/// Emphasis, and links, inside a sentence.
///
/// <para>
/// <b>Why marks and not tags.</b> A card is a document, not a page — the whole reason a stranger's
/// card is safe to open is that nothing in it is markup. So a word set in bold is not <c>&lt;b&gt;</c>
/// stored in the document; it is a pair of characters the author typed, or that the editor's toolbar
/// typed for them, and this is the only place that turns them into anything. Somebody reading the
/// JSON sees the sentence. An older renderer that has never heard of a mark shows the sentence with
/// two extra characters in it, which is a document degrading rather than breaking.
/// </para>
///
/// <para>
/// <b>The vocabulary.</b> Four things, because those are the four things prose actually does:
/// </para>
/// <list type="bullet">
///   <item><description><c>**bold**</c></description></item>
///   <item><description><c>*italic*</c></description></item>
///   <item><description><c>_underlined_</c></description></item>
///   <item><description><c>[the words](where they go)</c></description></item>
/// </list>
///
/// <para>
/// <b>An unmatched mark is a character.</b> Somebody using an asterisk as an asterisk gets an
/// asterisk. Only a closed pair means anything, and the rest of the paragraph is unaffected either
/// way.
/// </para>
///
/// <para>
/// <b>Links are the one that matters.</b> A page whose prose cannot link is a leaflet, and a real web
/// page links from inside its sentences — so a card has to, or "the same page in both places" is not
/// true. It is also the one an attacker would want, so a written address is never taken at its word:
/// a mesh address is handed to the host to decide about, a web address becomes an ordinary anchor
/// that fetches nothing, and anything else — <c>javascript:</c>, <c>data:</c>, a scheme nobody has
/// heard of — is simply the words with no link at all.
/// </para>
/// </summary>
public static class CardMarks
{
    /// <summary>How deeply marks may nest before the rest is taken literally.</summary>
    /// <remarks>
    /// Bold inside a link inside italics is already more than prose needs. The limit exists because
    /// this walks a stranger's document and depth is the only thing here that could be made to grow.
    /// </remarks>
    private const int Deepest = 4;

    /// <summary>
    /// Draw the marks in a piece of text that has <b>already been escaped</b>.
    /// </summary>
    /// <param name="escaped">
    ///   The words, with every angle bracket and ampersand already turned into an entity. Order
    ///   matters and this direction is the safe one: escaping first means a card that contains the
    ///   characters of a tag can only ever produce the characters of a tag.
    /// </param>
    /// <param name="offering">
    ///   Whether this page is being handed to somebody who is not on AetherNet. Their phone cannot
    ///   follow a mesh address, so they are shown the words instead of a link that goes nowhere.
    /// </param>
    public static string Draw(string? escaped, bool offering = false)
    {
        if (string.IsNullOrEmpty(escaped)) return "";

        var made = new StringBuilder(escaped.Length + 24);
        Walk(made, escaped, offering, 0);
        return made.ToString();
    }

    /// <summary>Whether any of this could be a mark, so callers can skip the walk entirely.</summary>
    public static bool Marked(string? escaped) =>
        escaped is { Length: > 1 } && escaped.AsSpan().IndexOfAny('*', '_', '[') >= 0;

    // ── The scanner ───────────────────────────────────────────────────────────

    private static void Walk(StringBuilder made, string s, bool offering, int depth)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (depth < Deepest)
            {
                if (c == '[' && Linked(made, s, ref i, offering, depth)) continue;
                if (c is '*' or '_' && Paired(made, s, ref i, offering, depth)) continue;
            }

            made.Append(c);
        }
    }

    /// <summary>
    /// A pair of the same marker around some words.
    /// </summary>
    /// <remarks>
    /// Two of them is bold and one is italic, so the double is looked for first — otherwise
    /// <c>**both**</c> would open in italics on the first star and the second star would sit in the
    /// text. Returns false when nothing closes it, and the caller writes the character out as itself.
    /// </remarks>
    private static bool Paired(StringBuilder made, string s, ref int i, bool offering, int depth)
    {
        var c = s[i];
        var strong = c == '*' && i + 1 < s.Length && s[i + 1] == '*';
        var mark = strong ? "**" : c.ToString();
        var tag = strong ? "strong" : c == '*' ? "em" : "u";

        var from = i + mark.Length;

        // A mark opens against a word and closes against a word. Without that rule "2 * 3, and
        // *this* counts" sets everything between the two stars in italics, which is not what anybody
        // typing an asterisk as an asterisk meant — and the rest of the sentence pays for it.
        if (from >= s.Length || char.IsWhiteSpace(s[from])) return false;

        var close = s.IndexOf(mark, from, StringComparison.Ordinal);
        while (close > from && char.IsWhiteSpace(s[close - 1]))
            close = s.IndexOf(mark, close + 1, StringComparison.Ordinal);

        // Nothing between the pair is two characters somebody typed, not empty emphasis.
        if (close < 0 || close == from) return false;

        made.Append('<').Append(tag).Append('>');
        Walk(made, s[from..close], offering, depth + 1);
        made.Append("</").Append(tag).Append('>');

        i = close + mark.Length - 1;
        return true;
    }

    /// <summary>
    /// Where the brackets round an address close.
    /// </summary>
    /// <remarks>
    /// Counted rather than searched for, because addresses contain brackets — a Wikipedia article, a
    /// path with a parenthesised note in it — and stopping at the first one truncates the address and
    /// leaves the rest of it sitting in the sentence.
    /// </remarks>
    private static int Closing(string s, int from)
    {
        var depth = 0;

        for (var i = from; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && depth-- == 0) return i;
        }

        return -1;
    }

    /// <summary>
    /// <c>[the words](where they go)</c>, checked before it is believed.
    /// </summary>
    private static bool Linked(StringBuilder made, string s, ref int i, bool offering, int depth)
    {
        var shut = s.IndexOf(']', i + 1);
        if (shut < 0 || shut == i + 1) return false;
        if (shut + 1 >= s.Length || s[shut + 1] != '(') return false;

        var ends = Closing(s, shut + 2);
        if (ends < 0) return false;

        var label = s[(i + 1)..shut];
        var target = s[(shut + 2)..ends];

        // Whatever this turns out to be, the words are the words.
        void Words() => Walk(made, label, offering, depth + 1);

        if (CardBlock.IsMeshAddress(target))
        {
            // The same rule as a link block: the card never gets an address of its own to follow. It
            // asks its host, the host checks it again, and a reader with no Aether is shown words.
            if (offering) Words();
            else
            {
                made.Append("<button type=\"button\" class=\"mk go\" data-aether-to=\"")
                    .Append(target).Append("\">");
                Words();
                made.Append("</button>");
            }
        }
        else if (CardBlock.IsUsableWeb(target))
        {
            // An anchor, for the same reason the tip jar is one: this page is already open in a
            // browser, following it is the reader's decision, and nothing is fetched to draw it — so
            // opening a stranger's card still causes no request of any kind.
            made.Append("<a class=\"mk\" href=\"").Append(target)
                .Append("\" rel=\"noopener noreferrer nofollow\" target=\"_blank\">");
            Words();
            made.Append("</a>");
        }
        else
        {
            Words();
        }

        i = ends;
        return true;
    }
}
