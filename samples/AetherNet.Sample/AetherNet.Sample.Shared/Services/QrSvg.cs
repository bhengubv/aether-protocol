// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using QRCoder;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Renders an <c>aether://</c> invite as an inline, brand-styled SVG QR code.
///
/// Deliberately generate-only. Aether registers the <c>aether://</c> scheme, so the other phone can
/// scan this with the camera app it already has and land straight in Aether with the tag filled in —
/// no camera permission for us, no scanning library, and nothing from Google Play Services.
///
/// QRCoder (MIT) supplies the module matrix; the SVG is emitted here, which is what lets the code
/// carry the brand — round modules, brand-blue rounded finder eyes, and the Aether mark punched into
/// the middle — instead of looking like a generated barcode.
///
/// Scannability is a hard constraint, not a preference:
/// <list type="bullet">
/// <item>Error correction is <b>H</b> (~30% recoverable) to pay for the centre mark.</item>
/// <item>The mark covers well under the ~30% budget, and the modules beneath it are cleared so no
/// half-drawn dots confuse a decoder.</item>
/// <item>Dark-on-light always, even in dark mode — inverted codes defeat many scanners — which is why
/// the caller sits this on a light tile.</item>
/// </list>
/// </summary>
public static class QrSvg
{
    private const string Brand = "#2196F3";
    private const string Ink = "#16232f";

    /// <summary>
    /// Render <paramref name="payload"/> as a self-contained SVG.
    /// </summary>
    /// <param name="payload">What the code encodes.</param>
    /// <param name="dark">Module colour.</param>
    /// <param name="light">Background colour. Keep this light — see the class remarks.</param>
    /// <param name="accent">Finder-eye and default centre-mark colour.</param>
    /// <param name="withMark">Punch a mark into the middle.</param>
    /// <param name="mark">
    /// The app's own logo, as SVG markup drawn into a <b>0 0 100 100</b> box — the renderer scales and
    /// centres it into the cleared reserve, so every app on AetherNet gets its own branded invite code
    /// without touching this code. Null uses the Aether mark.
    /// <example>
    /// <code>QrSvg.Render(invite, accent: "#7c3aed", mark: "&lt;path d='M20 80 L50 20 L80 80 Z' fill='#7c3aed'/&gt;");</code>
    /// </example>
    /// Keep it bold and simple — it is rendered small, and fine detail will not read.
    /// </param>
    public static string Render(
        string payload,
        string dark = Ink,
        string light = "#ffffff",
        string accent = Brand,
        bool withMark = true,
        string? mark = null)
    {
        if (string.IsNullOrWhiteSpace(payload)) return string.Empty;

        using var generator = new QRCodeGenerator();
        // H, so the centre mark costs us nothing we can't recover.
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.H);

        var matrix = data.ModuleMatrix;
        var size = matrix.Count;

        // QRCoder includes a 4-module quiet zone in the matrix; keep it, scanners need it.
        var svg = new StringBuilder(size * size);
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" role=\"img\" aria-label=\"Your AetherTag as a QR code\">");
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"{size}\" height=\"{size}\" fill=\"{light}\"/>");

        // ── Data modules: dots, skipping the three finder patterns and the centre reserve ──
        var reserve = withMark ? MarkReserve(size) : (-1, -1);
        svg.Append(CultureInfo.InvariantCulture, $"<g fill=\"{dark}\">");
        for (var y = 0; y < size; y++)
        {
            var row = matrix[y];
            for (var x = 0; x < size; x++)
            {
                if (!row[x]) continue;
                if (IsFinder(x, y, size)) continue;                     // drawn as shaped eyes below
                if (withMark && InReserve(x, y, reserve)) continue;      // cleared for the mark
                // r=0.42 leaves a hairline gap between dots: reads as designed, still decodes.
                svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x + 0.5:0.##}\" cy=\"{y + 0.5:0.##}\" r=\"0.42\"/>");
            }
        }
        svg.Append("</g>");

        // ── Finder eyes: rounded squares in brand blue. The strongest "this is ours" signal. ──
        foreach (var (fx, fy) in FinderOrigins(size))
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{fx + 0.5:0.##}\" y=\"{fy + 0.5:0.##}\" width=\"6\" height=\"6\" rx=\"2\" ry=\"2\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"1\"/>");
            svg.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{fx + 2:0.##}\" y=\"{fy + 2:0.##}\" width=\"3\" height=\"3\" rx=\"1\" ry=\"1\" fill=\"{accent}\"/>");
        }

        // ── The centre mark: the app's own logo, or Aether's by default ──
        if (withMark)
        {
            var c = size / 2.0;

            // Clear a disc so the logo never sits on half-drawn dots.
            svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{c:0.##}\" cy=\"{c:0.##}\" r=\"{size * 0.132:0.##}\" fill=\"{light}\"/>");

            if (string.IsNullOrWhiteSpace(mark))
            {
                var r = size * 0.085;
                svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{c:0.##}\" cy=\"{c:0.##}\" r=\"{r * 1.15:0.##}\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"{size * 0.012:0.###}\"/>");
                svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{c:0.##}\" cy=\"{c:0.##}\" r=\"{r * 0.62:0.##}\" fill=\"{accent}\"/>");
            }
            else
            {
                // The caller draws in a 0..100 box; scale and centre it into the cleared disc.
                var side = size * 0.20;
                var scale = side / 100.0;
                var origin = c - side / 2.0;
                svg.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate({origin:0.###} {origin:0.###}) scale({scale:0.####})\">");
                svg.Append(mark);
                svg.Append("</g>");
            }
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>Top-left origins of the three finder patterns, inside the 4-module quiet zone.</summary>
    private static (int X, int Y)[] FinderOrigins(int size) =>
        new[] { (4, 4), (size - 11, 4), (4, size - 11) };

    private static bool IsFinder(int x, int y, int size)
    {
        foreach (var (fx, fy) in FinderOrigins(size))
            if (x >= fx && x < fx + 7 && y >= fy && y < fy + 7) return true;
        return false;
    }

    /// <summary>The square of modules cleared for the centre mark, as (start, length).</summary>
    private static (int Start, int Length) MarkReserve(int size)
    {
        // ~17% of the width — comfortably inside what ECC level H can lose.
        var length = Math.Max(5, (int)Math.Round(size * 0.17));
        if (length % 2 != size % 2) length++;               // keep it centred on the module grid
        return ((size - length) / 2, length);
    }

    private static bool InReserve(int x, int y, (int Start, int Length) reserve) =>
        x >= reserve.Start && x < reserve.Start + reserve.Length &&
        y >= reserve.Start && y < reserve.Start + reserve.Length;
}
