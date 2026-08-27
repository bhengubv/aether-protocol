// SPDX-License-Identifier: MIT

using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The masthead every page gets, drawn rather than photographed.
///
/// <para>
/// A page with a picture across the top reads as a place; the same words with no picture read as a
/// form somebody filled in. That difference is most of what separates a page a designer was paid for
/// from one that was not — and it is the one thing a person writing on a handset, with no camera roll
/// worth publishing and no way to move a file onto the mesh yet, cannot supply for themselves.
/// </para>
///
/// <para>
/// So it is generated from what the page already knows: its accent, its title, its address. Every page
/// therefore looks deliberate on the day it is written, and two pages by the same author in different
/// looks do not come out identical. A few hundred bytes of SVG, sharp at any size, which is the only
/// kind of picture a link measured at roughly 5 kbps can carry without the reader waiting.
/// </para>
/// </summary>
public static class PageArt
{
    /// <summary>The masthead for a page.</summary>
    /// <param name="title">The page's own title. Its first letter becomes the mark.</param>
    /// <param name="address">The address, drawn small — a page that shows where it lives.</param>
    /// <param name="accent">The look's colour, so picture and page agree.</param>
    public static string Svg(string? title, string? address, string accent)
    {
        var mark = Mark(title, address);
        var caption = Escape(Shorten(title, 28));
        var where = Escape(Shorten(address, 34));

        var art = new StringBuilder(700);

        art.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 600 260\" role=\"img\">");
        art.Append("<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">");
        art.Append("<stop offset=\"0\" stop-color=\"").Append(accent).Append("\"/>");
        art.Append("<stop offset=\"1\" stop-color=\"").Append(accent).Append("\" stop-opacity=\".55\"/>");
        art.Append("</linearGradient></defs>");
        art.Append("<rect width=\"600\" height=\"260\" fill=\"url(#g)\"/>");
        art.Append("<circle cx=\"505\" cy=\"52\" r=\"120\" fill=\"#fff\" fill-opacity=\".08\"/>");
        art.Append("<circle cx=\"90\" cy=\"232\" r=\"90\" fill=\"#000\" fill-opacity=\".10\"/>");

        art.Append("<text x=\"40\" y=\"150\" font-family=\"system-ui,sans-serif\" font-size=\"104\" ")
           .Append("font-weight=\"800\" fill=\"#fff\" fill-opacity=\".92\">").Append(mark).Append("</text>");

        if (caption.Length > 0)
            art.Append("<text x=\"42\" y=\"196\" font-family=\"system-ui,sans-serif\" font-size=\"26\" ")
               .Append("font-weight=\"600\" fill=\"#fff\" fill-opacity=\".88\">").Append(caption).Append("</text>");

        if (where.Length > 0)
            art.Append("<text x=\"42\" y=\"228\" font-family=\"ui-monospace,monospace\" font-size=\"17\" ")
               .Append("fill=\"#fff\" fill-opacity=\".62\">").Append(where).Append("</text>");

        art.Append("</svg>");
        return art.ToString();
    }

    /// <summary>The single character the masthead is built around.</summary>
    /// <remarks>
    /// The title's first letter where there is one, the address's otherwise. A page written before its
    /// author has typed anything still gets a mark rather than a blank rectangle.
    /// </remarks>
    private static char Mark(string? title, string? address)
    {
        foreach (var source in new[] { title, address })
            foreach (var c in source ?? "")
                if (char.IsAsciiLetterOrDigit(c))
                    return char.ToUpperInvariant(c);

        return 'A';
    }

    private static string Shorten(string? raw, int most)
    {
        var text = raw?.Trim() ?? "";
        return text.Length <= most ? text : text[..(most - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Escape for SVG.
    /// </summary>
    /// <remarks>
    /// The title is typed by a person and this SVG is served to strangers, so it is the same problem
    /// the card renderer has: an unescaped angle bracket in somebody's shop name is a way to write
    /// markup into a document other people open.
    /// </remarks>
    private static string Escape(string raw)
    {
        var clean = new StringBuilder(raw.Length);

        foreach (var c in raw)
        {
            switch (c)
            {
                case '&': clean.Append("&amp;"); break;
                case '<': clean.Append("&lt;"); break;
                case '>': clean.Append("&gt;"); break;
                case '"': clean.Append("&quot;"); break;
                case '\'': clean.Append("&#39;"); break;
                default:
                    if (!char.IsControl(c)) clean.Append(c);
                    break;
            }
        }

        return clean.ToString();
    }
}
