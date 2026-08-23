// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What a friend sees when they tap your phone.
///
/// <para>
/// The invite used to point straight at the package, so tapping a phone dropped a ninety-megabyte
/// <c>.apk</c> into somebody's downloads with nothing but a raw address and a hex string to explain
/// it. That is what a phishing link looks like. It does not matter how sound the bytes are if the
/// moment reads as something to be frightened of.
/// </para>
///
/// <para>
/// So the tap lands on a page instead, and the package is one deliberate press further on. The page
/// says who is offering, what it is, how big it is, and — the part that actually reassures — that it
/// is coming off the handset next to them rather than out of the internet.
/// </para>
///
/// <h3>Self-contained, and it has to be</h3>
/// <para>
/// Not one byte may be fetched from anywhere else. The friend has no internet — that is the entire
/// premise — so a web font, a stylesheet or a logo hosted elsewhere would not render slowly, it would
/// not render at all, and the first thing they ever saw of this network would be a broken page. Every
/// style is inline and the mark is drawn in SVG.
/// </para>
///
/// <h3>Why it is not a card</h3>
/// <para>
/// AetherNet's own cards are signed JSON drawn by a renderer this app owns. The person reading this
/// has none of that — they have a browser and nothing else — so this is plain HTML, which is the only
/// thing a phone with no Aether on it knows how to draw. It borrows the card's manners, not its
/// format.
/// </para>
/// </summary>
public static class ShareCard
{
    /// <summary>
    /// Render the page.
    /// </summary>
    /// <param name="from">The giver's AetherTag, or null when it is not known yet.</param>
    /// <param name="sizeBytes">How big the package is, so nobody is surprised by the download.</param>
    /// <param name="downloadPath">Where the button points — a path on this same phone.</param>
    public static string Render(string? from, long sizeBytes, string downloadPath)
    {
        if (string.IsNullOrWhiteSpace(downloadPath))
            throw new ArgumentException("The page needs somewhere to send them.", nameof(downloadPath));

        var who = Clean(from);
        var size = Size(sizeBytes);
        var link = Clean(downloadPath);

        var page = new StringBuilder(4096);
        page.Append("<!doctype html><html lang=\"en\"><head>");
        page.Append("<meta charset=\"utf-8\">");
        // Fixed scale: this is read at arm's length on a phone somebody else is holding.
        page.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">");
        page.Append("<meta name=\"color-scheme\" content=\"dark\">");
        page.Append("<title>Aether</title>");
        page.Append("<style>").Append(Style).Append("</style>");
        page.Append("</head><body><main class=\"card\">");

        page.Append(Mark);

        page.Append("<p class=\"eyebrow\">Someone next to you is sharing</p>");
        page.Append("<h1>Aether</h1>");

        if (who is { Length: > 0 })
            page.Append(CultureInfo.InvariantCulture, $"<p class=\"from\">from <span class=\"tag\">{who}</span></p>");

        page.Append("<p class=\"what\">Message and call the people around you with no towers, ")
            .Append("no accounts and no company in the middle.</p>");

        page.Append(CultureInfo.InvariantCulture,
            $"<a class=\"get\" href=\"{link}\" download>Get Aether<span class=\"size\">{size}</span></a>");

        page.Append("<p class=\"note\"><span class=\"dot\"></span>Coming off the phone next to you — not the internet.</p>");

        // Said before it happens rather than after. The unknown-sources warning is the single most
        // likely moment for somebody to stop, and it is far less alarming when a friend's page told
        // them it was coming.
        page.Append("<p class=\"fine\">Your phone will ask whether you're sure. ")
            .Append("That's normal — it asks that for anything that didn't come from a shop.</p>");

        page.Append("</main></body></html>");
        return page.ToString();
    }

    /// <summary>How big, in words a person reads rather than bytes.</summary>
    /// <remarks>
    /// Invariant, deliberately. This number is rendered by the GIVER's phone and read on the TAKER's,
    /// so following the device locale means the size is formatted for whoever happens to be handing
    /// the app over — and it lands in a page that is otherwise fixed English, where "94,8 MB" reads as
    /// a typo rather than a convention. Left to the ambient culture it was doing exactly that here.
    /// </remarks>
    public static string Size(long bytes) =>
        bytes <= 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB");

    /// <summary>
    /// Everything that reaches the page is escaped.
    /// </summary>
    /// <remarks>
    /// The values here are this device's own tag and its own path, so nothing hostile is expected —
    /// but "nothing hostile is expected" is how a page ends up building markup out of a string it
    /// never checked.
    /// </remarks>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var clean = new StringBuilder(value.Length + 8);
        foreach (var c in value.Trim())
        {
            switch (c)
            {
                case '&': clean.Append("&amp;"); break;
                case '<': clean.Append("&lt;"); break;
                case '>': clean.Append("&gt;"); break;
                case '"': clean.Append("&quot;"); break;
                case '\'': clean.Append("&#39;"); break;
                default: clean.Append(c); break;
            }
        }
        return clean.ToString();
    }

    /// <summary>The Aether mark, drawn rather than fetched.</summary>
    private const string Mark =
        "<svg class=\"mark\" viewBox=\"0 0 48 48\" aria-hidden=\"true\">" +
        "<circle cx=\"24\" cy=\"24\" r=\"20\" fill=\"none\" stroke=\"#2196F3\" stroke-width=\"1.5\" opacity=\".35\"/>" +
        "<circle cx=\"24\" cy=\"24\" r=\"13\" fill=\"none\" stroke=\"#2196F3\" stroke-width=\"1.5\" opacity=\".6\"/>" +
        "<circle cx=\"24\" cy=\"24\" r=\"5.5\" fill=\"#2196F3\"/></svg>";

    /// <summary>
    /// The whole stylesheet, inline.
    /// </summary>
    /// <remarks>
    /// Dark, precise and quiet — hairline borders, tight radii and a layered shadow rather than a
    /// heavy one, which is what makes a small page feel considered instead of thrown together. The one
    /// colour is Aether's own blue; everything else is a step on a near-black ramp. The primary action
    /// is inverted — near-white on near-black — because on a page this dark nothing else reads as
    /// firmly as light does.
    /// </remarks>
    private const string Style = """
        *,*::before,*::after{box-sizing:border-box}
        html,body{margin:0;height:100%}
        body{
          background:#050608;color:#f7f8f8;
          font:400 16px/1.55 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif;
          -webkit-font-smoothing:antialiased;
          display:flex;align-items:center;justify-content:center;
          padding:24px calc(20px + env(safe-area-inset-right)) calc(24px + env(safe-area-inset-bottom)) calc(20px + env(safe-area-inset-left));
        }
        .card{
          width:100%;max-width:380px;text-align:center;
          background:#101113;border:1px solid rgba(255,255,255,.06);border-radius:14px;
          padding:34px 26px 28px;
          box-shadow:0 2.8px 2.2px rgba(0,0,0,.034),0 6.7px 5.3px rgba(0,0,0,.048),
                     0 12.5px 10px rgba(0,0,0,.06),0 22.3px 17.9px rgba(0,0,0,.072),
                     0 41.8px 33.4px rgba(0,0,0,.086),0 100px 80px rgba(0,0,0,.12);
        }
        .mark{width:52px;height:52px;display:block;margin:0 auto 18px}
        .eyebrow{margin:0;font-size:11px;letter-spacing:.14em;text-transform:uppercase;color:#8a8f98}
        h1{margin:6px 0 0;font-size:34px;line-height:1.1;font-weight:700;letter-spacing:-.02em}
        .from{margin:14px 0 0;font-size:14px;color:#8a8f98}
        .tag{
          font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
          font-size:14px;letter-spacing:.06em;color:#2196F3;
          background:rgba(33,150,243,.09);border:1px solid rgba(33,150,243,.22);
          border-radius:7px;padding:3px 8px;margin-left:4px;white-space:nowrap;
        }
        .what{margin:20px 0 0;font-size:14.5px;color:#8a8f98;text-wrap:pretty}
        .get{
          display:flex;align-items:center;justify-content:center;gap:10px;
          margin-top:26px;padding:16px 18px;
          background:#edeef0;color:#0a0b0d;text-decoration:none;
          font-size:16px;font-weight:650;letter-spacing:-.01em;
          border-radius:9px;
          -webkit-tap-highlight-color:transparent;transition:transform .12s ease,background .12s ease;
        }
        .get:active{transform:scale(.985);background:#dcdde0}
        .size{
          font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
          font-size:12.5px;font-weight:500;color:rgba(10,11,13,.55);
        }
        .note{
          margin:18px 0 0;font-size:12.5px;color:#8a8f98;
          display:flex;align-items:center;justify-content:center;gap:7px;
        }
        .dot{
          width:6px;height:6px;border-radius:50%;background:#2196F3;flex:none;
          box-shadow:0 0 0 3px rgba(33,150,243,.14);
        }
        .fine{
          margin:20px 0 0;padding-top:16px;border-top:1px solid rgba(255,255,255,.06);
          font-size:12px;line-height:1.5;color:#6a707a;text-wrap:pretty;
        }
        @media (prefers-reduced-motion:reduce){.get{transition:none}}
        """;
}
