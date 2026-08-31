// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.RegularExpressions;

namespace AetherNet.Browser;

/// <summary>
/// The author's own stylesheet, made safe to open on a stranger's phone.
///
/// <para>
/// <b>Why author CSS at all.</b> Sliders are not coding. MySpace taught a generation to write CSS
/// because the page was theirs to break — you saw a profile you liked, looked at how it was made,
/// copied it, changed one thing, broke it, fixed it. A settings panel cannot do that, and a card
/// nobody can take apart teaches nothing.
/// </para>
///
/// <para>
/// <b>Why it cannot travel as written.</b> A card is opened by somebody who has never met its author.
/// Raw CSS carries three real weapons: <c>url(...)</c> is a request, so a stylesheet is a tracking
/// pixel and a deanonymiser on a network whose entire point is that nobody watches you; <c>@import</c>
/// is the same thing with a longer fuse; and an unscoped selector reaches out of the card and repaints
/// the app around it, which is how a page forges a button that is not its own.
/// </para>
///
/// <para>
/// <b>So: everything is allowed except the things that reach.</b> Nothing here is a taste judgement.
/// You may set anything you like, in any way you like, and make it as ugly as you want. You may not
/// fetch, and you may not leave your own page.
/// </para>
/// </summary>
public static class CardCss
{
    /// <summary>How much stylesheet a card may carry.</summary>
    /// <remarks>
    /// Generous — a real MySpace layout was a few kilobytes and this is four times that. It exists so
    /// a card stays a thing a phone can hand another phone in a second, not because anybody should be
    /// writing less.
    /// </remarks>
    public const int Most = 16 * 1024;

    /// <summary>The root every selector is confined to.</summary>
    public const string Root = ".card-own";

    private static readonly Regex Comment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Anything that makes a request, or executes.
    /// </summary>
    /// <remarks>
    /// <c>url()</c> goes wholesale rather than by scheme. A card's pictures arrive by content hash
    /// through the renderer, so a stylesheet has no honest reason to name a location — and the moment
    /// one is allowed, "only https" becomes an argument about redirects, then about data:, then about
    /// nothing at all. <c>expression()</c> and <c>-moz-binding</c> are old script vectors and cost
    /// nothing to refuse.
    /// </remarks>
    private static readonly (Regex What, string With)[] Reaches =
    [
        // The whole construct, not the opening of it. Removing "url(" and leaving the address behind
        // left the tracker's hostname sitting in the stylesheet as a bare token — refused by the test
        // that reads the OUTPUT rather than the intent.
        (new Regex(@"url\s*\([^)]*\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled), "none"),

        // An at-rule statement runs to its semicolon, and @import takes a bare string as readily as
        // a url() — so the statement goes, not the keyword.
        (new Regex(@"@(?:import|charset|namespace)[^;{}]*;?", RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),

        (new Regex(@"expression\s*\([^)]*\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled), "none"),
        (new Regex(@"(?:-moz-binding|behaviou?r)\s*:[^;}]*;?", RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
        (new Regex(@"javascript\s*:[^;}]*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "none"),
    ];

    /// <summary>
    /// The author's stylesheet, confined to their own card.
    /// </summary>
    /// <remarks>
    /// Returns empty rather than throwing on anything it will not carry: a card arriving over the
    /// radio is not a form submission, and a reader whose page went blank because a stranger's
    /// stylesheet was malformed has been failed by us, not by them.
    /// </remarks>
    public static string Safe(string? written)
    {
        if (string.IsNullOrWhiteSpace(written)) return "";

        var css = Comment.Replace(written, " ");
        if (css.Length > Most) css = css[..Most];
        foreach (var (what, with) in Reaches) css = what.Replace(css, with);

        // Braces have to balance before anything is scoped, or a stray one lets a later rule out.
        if (css.Count(c => c == '{') != css.Count(c => c == '}')) return "";

        return Scope(css);
    }

    /// <summary>
    /// Put every selector inside the card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the load-bearing half. Without it <c>body { display: none }</c> in a card handed to you
    /// blanks the app, and <c>.tabbar { }</c> repaints the navigation — a stranger's page dressing
    /// itself up as yours.
    /// </para>
    /// <para>
    /// At-rules that hold other rules — media and supports queries — are walked into rather than
    /// prefixed, because a card that cannot say "on a narrow screen" cannot be responsive, and
    /// responsive is not a luxury on a phone-to-phone network.
    /// </para>
    /// </remarks>
    private static string Scope(string css)
    {
        var outp = new StringBuilder();
        var i = 0;

        while (i < css.Length)
        {
            var brace = css.IndexOf('{', i);
            if (brace < 0) break;

            var head = css[i..brace].Trim();
            var close = Match(css, brace);
            if (close < 0) break;

            var body = css[(brace + 1)..close];

            if (head.StartsWith('@'))
            {
                // A block at-rule holds rules; a keyframes block holds percentages, which must not be
                // prefixed or the animation stops existing.
                var nests = head.StartsWith("@media", StringComparison.OrdinalIgnoreCase)
                         || head.StartsWith("@supports", StringComparison.OrdinalIgnoreCase);

                outp.Append(head).Append(" {\n")
                    .Append(nests ? Scope(body) : body)
                    .Append("\n}\n");
            }
            else
            {
                outp.Append(Confine(head)).Append(" {").Append(body).Append("}\n");
            }

            i = close + 1;
        }

        return outp.ToString();
    }

    /// <summary>Each selector in a comma-separated head, rooted in the card.</summary>
    /// <remarks>
    /// A bare <c>body</c> or <c>html</c> becomes the card's own root rather than being thrown away —
    /// somebody writing <c>body { background: black }</c> means "my page", and meaning it is not an
    /// attack. Percentages are left alone so keyframes survive.
    /// </remarks>
    private static string Confine(string head) =>
        string.Join(", ", head.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s =>
                s.EndsWith('%') || s.All(char.IsAsciiDigit) ? s
                : s is "html" or "body" or ":root" ? Root
                : s.StartsWith(Root, StringComparison.Ordinal) ? s
                : $"{Root} {s}"));

    /// <summary>Where a block opened at <paramref name="open"/> closes.</summary>
    private static int Match(string css, int open)
    {
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }
}
