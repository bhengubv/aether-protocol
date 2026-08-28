// SPDX-License-Identifier: MIT

using System.Text;

namespace AetherNet.Browser;

/// <summary>
/// A card as a single file you can put on the open web.
///
/// <para>
/// <b>The same page, in both places.</b> Somebody who writes a page on their phone should be able to
/// have it on AetherNet <i>and</i> at their own domain, and have them be the same page — not similar,
/// not a port, the same. That only holds if there is one document and one renderer, which there is:
/// this hands <see cref="CardPage"/> exactly what the mesh hands it, and gets back exactly what a
/// reader on the mesh sees.
/// </para>
///
/// <para>
/// <b>One file, nothing beside it.</b> No folder of assets, no stylesheet, no font directory, no
/// build step and no server. Pictures go in as data, the typefaces go in as bytes, the stylesheet is
/// inline — so it works from a file:// path, from a static host, from a bucket, from a USB stick, and
/// it keeps working when whoever hosted it stops.
/// </para>
///
/// <para>
/// That last part is the point rather than a convenience. A page that needs a server is a page that
/// somebody can take away; the reason a card is worth writing is that nobody can. An exported card
/// carries the same property onto the web.
/// </para>
/// </summary>
public static class CardExport
{
    /// <summary>
    /// Render a card as one standalone HTML file.
    /// </summary>
    /// <param name="card">The document, exactly as it stands on the mesh.</param>
    /// <param name="pictures">
    ///   Turns a content hash into the picture as a <c>data:</c> URI. Everything the page shows must
    ///   come through here — a page that reached out for an image would stop being one file.
    /// </param>
    /// <param name="at">
    ///   Kept for callers that pass it, and deliberately unused: the exported page is the card and
    ///   nothing else. See the remarks — an export that appended even one line would not be the same
    ///   page any more.
    /// </param>
    /// <remarks>
    /// The typefaces are embedded rather than linked, for the same reason as everywhere else here: a
    /// linked font is a dependency on somebody else's uptime, and this file is supposed to outlive
    /// that. It costs about a hundred and fifty kilobytes on the looks that use one.
    /// </remarks>
    public static string Standalone(
        CardDocument card, Func<string, string?>? pictures = null, string? at = null)
    {
        var page = CardPage.Render(
            card, card.Title, 0,
            downloadPath: null,
            assetPath: pictures,
            fonts: PageAssets.Face);

        // Nothing is added. Identical means identical — a line at the foot saying where else the page
        // lives is still a line the author did not write, and a web copy that differs from the mesh
        // copy by one sentence differs.
        return page;
    }

    /// <summary>
    /// How big the file will be, before writing it.
    /// </summary>
    /// <remarks>
    /// Worth knowing rather than discovering: a page of photographs embedded as data is a third larger
    /// than the pictures themselves, because base64 costs a third. Somebody about to put this on a
    /// host they pay for should be told first.
    /// </remarks>
    public static long Weigh(
        CardDocument card, Func<string, string?>? pictures = null, string? at = null) =>
        Encoding.UTF8.GetByteCount(Standalone(card, pictures, at));

    /// <summary>A file name for this card, safe on any filesystem.</summary>
    public static string FileName(string? name) =>
        (MyPages.Clean(name) is { Length: > 0 } clean ? clean : "card") + ".html";
}
