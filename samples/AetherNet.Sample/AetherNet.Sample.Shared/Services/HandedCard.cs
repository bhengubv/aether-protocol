// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Browser;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// A finished page put on this phone, so there is something worth opening up.
///
/// <para>
/// <b>Why a sample app ships one.</b> A card lands on your phone from somebody else, you like it,
/// and you go and look at how it was made — that is the whole loop, and it is how a generation
/// learned HTML and CSS off MySpace without anybody teaching them. A demo that only ever shows you
/// pages you wrote yourself has removed the one part that does the teaching.
/// </para>
///
/// <para>
/// <b>It seeds only if the files are there.</b> The page and its pictures are a bench fixture — a
/// real page from the open web, rebuilt through the editor's own operations and shrunk to what a
/// radio actually carries — and they are somebody else's words and somebody else's artwork. They are
/// kept out of the repository, so a checkout without them simply starts with no example and
/// everything else works exactly the same.
/// </para>
/// </summary>
public static class HandedCard
{
    /// <summary>
    /// Reading a file that shipped inside the app.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a call, because this project is plain .NET and knows nothing about the
    /// head hosting it — the phone answers with its package reader, and a head with no packaged files
    /// answers with nothing.
    /// </remarks>
    public delegate Task<Stream?> OpenPackaged(string named);

    /// <summary>What the page is called on this device.</summary>
    public const string Name = "sketchbook";

    private const string Document = "sketchbook.json";

    private const string Pictures = "sketchbook-plates.json";

    /// <summary>
    /// Put it on the phone, and keep it current.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The example ships with the app, so it moves with the app: a new version brings a new copy. That
    /// matters because it is the page somebody opens up to see how a page is made, and an example that
    /// is three versions behind teaches the wrong thing.
    /// </para>
    /// <para>
    /// The example belongs to the app, not to the person holding it — so it is replaced whenever the
    /// shipped copy differs, and it is not somewhere to keep your own work. Somewhere to keep your own
    /// work is what "make one like this" is for: that takes a copy under your own name, and the app
    /// never touches it again.
    /// </para>
    /// <para>
    /// "Skip it once it is published" was tried and is wrong, because seeding publishes: the example
    /// would go up once and then never move again, however many versions later.
    /// </para>
    /// </remarks>
    public static async Task SeedAsync(
        MeshWebService mesh, MyPages mine, OpenPackaged open,
        CancellationToken cancellationToken = default)
    {

        if (await Read(open, Document).ConfigureAwait(false) is not { Length: > 0 } written) return;

        var card = JsonSerializer.Deserialize<CardDocument>(written);
        if (card is null) return;


        var plates = await Read(open, Pictures).ConfigureAwait(false) is { Length: > 0 } shown
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(shown)
            : null;

        // The pictures are named in the document by what they were called when it was written. They
        // get hashed here, by this device, from the bytes it actually has — see TakeInAsync.
        var taken = await mesh
            .TakeInAsync(card, Named(plates), cancellationToken)
            .ConfigureAwait(false);

        if (taken is null) return;

        // Compared after the pictures are stored, not before.
        //
        // Storing them is what turns the names in the shipped copy into the hashes this device uses,
        // and it costs nothing to repeat — the same bytes hash to the same thing. Comparing first
        // looked cheaper and was wrong: the pictures were re-cropped without a word of the document
        // changing, the check said "same page", and the phone kept the old ones.
        if (mine.Get(Name) is { } held && Same(held.Doc, taken)) return;

        mine.Save(new WebCard { Name = Name, Doc = taken });

        // And stood up, so it answers at an address. A page that only exists inside the editor is not
        // a page yet — the point of it being here is that somebody can open it, look at how it is
        // made, and hand it on.
        await mesh.PublishAsync(Name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the phone already holds this exact page.
    /// </summary>
    /// <remarks>
    /// Both sides name their pictures by content hash by the time this runs, so a picture that was
    /// re-cropped is a different page — which is the case that got this wrong the first time.
    /// </remarks>
    private static bool Same(CardDocument? held, CardDocument taken) =>
        held is not null && held.ToJson() == taken.ToJson();

    /// <summary>
    /// The picture names as the document spells them.
    /// </summary>
    /// <remarks>
    /// A content hash is letters and digits and nothing else — the model refuses anything that is
    /// not, which is what stops a card naming a path where a hash belongs. The fixture keys are
    /// hyphenated file names, so they are folded the same way the document folded them.
    /// </remarks>
    private static Dictionary<string, string> Named(Dictionary<string, string>? plates)
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, uri) in plates ?? [])
            named[new string([.. key.Where(char.IsAsciiLetterOrDigit)])] = uri;

        return named;
    }

    private static async Task<string?> Read(OpenPackaged open, string named)
    {
        try
        {
            await using var file = await open(named).ConfigureAwait(false);
            if (file is null) return null;

            using var read = new StreamReader(file);
            return await read.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No fixture on this head. The app starts with no example page, which is the normal case
            // for anybody who checked the repository out.
            return null;
        }
    }
}
