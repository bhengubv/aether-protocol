// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What a photograph on a page is allowed to be.
///
/// <para>
/// A picture is the difference between a page somebody reads and a page somebody remembers, and it is
/// also the only thing on a card big enough to fail. Everything here is a limit, and every limit is
/// set by the slowest link rather than the fastest — a photograph that arrives instantly over Wi-Fi
/// Direct and never arrives at all over Bluetooth is a page that works on the demo and not on the
/// street.
/// </para>
///
/// <para>
/// The browser has already redrawn and re-encoded whatever was chosen before any of this is reached.
/// These are the checks on what came back, because the numbers on the other side of a JavaScript call
/// are not numbers this code produced.
/// </para>
/// </summary>
public static class PagePhoto
{
    /// <summary>
    /// The most a single picture may weigh.
    /// </summary>
    /// <remarks>
    /// A hundred and twenty kilobytes. Over Wi-Fi Direct that is a blink. Over a Bluetooth link
    /// measured at roughly two kilobytes a second it is about a minute — long, but it finishes, and
    /// once it has arrived it is held content-addressed on that phone forever. A megabyte would not
    /// finish, and a reader who has waited ten minutes for a picture has already closed the page.
    /// </remarks>
    public const int MostBytes = 120 * 1024;

    /// <summary>How many pictures one page may carry.</summary>
    /// <remarks>
    /// Four. A page is something a person reads on a handset, and every extra picture is another
    /// minute of somebody's evening on the slow link.
    /// </remarks>
    public const int MostPerPage = 4;

    /// <summary>The types we are willing to publish.</summary>
    /// <remarks>
    /// Raster only, and only formats every phone decodes. SVG is absent deliberately: it is a document
    /// that can carry script and fetch, so accepting one from a person and serving it to strangers
    /// would put an executable back inside the one thing that must stay inert. Ours are generated in
    /// this codebase and never uploaded.
    /// </remarks>
    public static readonly string[] Kinds = ["image/jpeg", "image/png", "image/webp"];

    /// <summary>Whether this is a picture we will carry.</summary>
    public static bool IsUsable(string? mime) =>
        mime is not null && Kinds.Contains(mime.Trim().ToLowerInvariant());

    /// <summary>Whether these are bytes we will publish.</summary>
    public static bool IsUsable(string? mime, byte[]? bytes) =>
        IsUsable(mime) && bytes is { Length: > 0 } && bytes.Length <= MostBytes && Looks(mime!, bytes);

    /// <summary>
    /// Whether the bytes look like what they claim to be.
    /// </summary>
    /// <remarks>
    /// The type came across a JavaScript call and is a claim, not a fact. Checking the first few bytes
    /// costs nothing and stops a page from serving one thing under the name of another — which is how
    /// a renderer somewhere downstream ends up parsing something it was never meant to.
    /// </remarks>
    private static bool Looks(string mime, byte[] bytes) => mime.Trim().ToLowerInvariant() switch
    {
        "image/jpeg" => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,

        "image/png" => bytes.Length > 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A,

        // RIFF....WEBP
        "image/webp" => bytes.Length > 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50,

        _ => false,
    };

    /// <summary>Whether a picture is a photograph rather than the drawing a page falls back to.</summary>
    /// <remarks>
    /// The distinction the masthead turns on. Vector art is a backdrop and the shader belongs over it;
    /// a photograph is the subject, and painting a shader across somebody's face is not a design
    /// choice. Read from the content type, which the descriptor already carries — no marker, no flag,
    /// nothing a card has to declare about itself.
    /// </remarks>
    public static bool IsPhotograph(string? dataUriOrMime) =>
        dataUriOrMime is { Length: > 0 } what &&
        Kinds.Any(k => what.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>How big a picture is, in a unit a person reads.</summary>
    public static string Size(long bytes) =>
        bytes <= 0 ? "—"
        : bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024} KB"
        : $"{bytes / (1024.0 * 1024.0):0.#} MB";

    /// <summary>Roughly how long this many bytes take on a link this fast, as something to say out loud.</summary>
    /// <remarks>
    /// Shown to the author, not to the reader. Somebody choosing a picture for a page should know what
    /// they are asking of whoever opens it on the slow radio — it is the one cost of publishing that
    /// is paid entirely by other people.
    /// </remarks>
    public static string OverSlowLink(long bytes)
    {
        // Measured: BLE carries about nine frames a second one way, at roughly two hundred bytes each.
        const double bytesPerSecond = 9 * 200;

        var seconds = bytes / bytesPerSecond;

        return seconds < 60
            ? $"about {Math.Max(1, (int)seconds)} seconds on Bluetooth"
            : $"about {Math.Round(seconds / 60)} minutes on Bluetooth";
    }
}
