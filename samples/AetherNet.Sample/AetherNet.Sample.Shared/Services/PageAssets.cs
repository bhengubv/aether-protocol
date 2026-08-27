// SPDX-License-Identifier: MIT

using System.Reflection;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The bytes a page needs to look like itself, carried inside the assembly.
///
/// <para>
/// Two typefaces and one shader. All three are load-bearing rather than decorative: a page handed to
/// somebody standing next to you has no internet behind it, so a linked font never arrives and a
/// linked script never runs — the look collapses to the handset's own faces and a flat rectangle, at
/// the exact moment the network is making its only first impression.
/// </para>
///
/// <para>
/// Read once and kept. A card is rendered on every keystroke of the editor, and re-reading a hundred
/// and thirty kilobytes of font out of the assembly each time is the kind of cost that shows up as a
/// handset feeling slow rather than as anything anybody can point at.
/// </para>
/// </summary>
public static class PageAssets
{
    /// <summary>Where the app's own copies are served from, for pages rendered inside the app.</summary>
    /// <remarks>
    /// A page drawn in the app can link these instead of carrying them — same bytes, already on the
    /// device, and the editor previews a card six times over without building a megabyte of base64
    /// every time somebody types a letter.
    /// </remarks>
    public const string WebFontBase = "_content/AetherNet.Sample.Shared/fonts/";

    private static readonly Dictionary<string, byte[]?> Held = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>The file each typeface family lives in.</summary>
    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Instrument Serif"] = "instrument-serif.woff2",
        ["Newsreader"] = "newsreader.woff2",
    };

    /// <summary>The typeface for this family, or null if we do not carry it.</summary>
    public static byte[]? Face(string family) =>
        Files.TryGetValue(family, out var file) ? Bytes(file) : null;

    /// <summary>The file a typeface family is served from, relative to <see cref="WebFontBase"/>.</summary>
    public static string? FaceFile(string family) =>
        Files.TryGetValue(family, out var file) ? file : null;

    /// <summary>
    /// The masthead painter, as script to inline into a page that cannot fetch anything.
    /// </summary>
    /// <remarks>
    /// Empty when it cannot be read, which the caller treats as "draw no canvas" rather than as an
    /// error. The page then shows whatever stands behind the masthead — on a card, the picture the
    /// mesh already carries — so a missing shader costs a page its motion and nothing else.
    /// </remarks>
    public static string Shader() =>
        Bytes("aether-shader.js") is { Length: > 0 } bytes
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : "";

    private static byte[]? Bytes(string name)
    {
        lock (Gate)
        {
            if (Held.TryGetValue(name, out var held)) return held;

            byte[]? read = null;
            try
            {
                using var stream = typeof(PageAssets).Assembly.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    read = buffer.ToArray();
                }
            }
            catch (Exception)
            {
                // A resource we cannot read is a look we cannot apply, not a page we cannot draw.
            }

            Held[name] = read;
            return read;
        }
    }

    /// <summary>Every resource name the assembly actually carries — for the test that proves it does.</summary>
    public static IEnumerable<string> Carried() =>
        typeof(PageAssets).Assembly.GetManifestResourceNames();
}
