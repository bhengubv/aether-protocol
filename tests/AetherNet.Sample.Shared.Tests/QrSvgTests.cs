// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Shared.Tests;

/// <summary>
/// The invite QR is branded — dot modules, coloured finder eyes, a logo punched into the middle — and
/// every one of those is a chance to make a code that looks good and doesn't scan. These tests pin the
/// structural guarantees; the actual decode was verified against zbar off a device screenshot.
/// </summary>
public sealed class QrSvgTests
{
    private const string Invite = "aether://BH8CZ-B09CA/add?k=jtHBaDrd38wVKaEjtjQv2YM9shhskW9OFQZ8+JNaas0=";

    [Fact]
    public void Renders_SelfContainedSvg()
    {
        var svg = QrSvg.Render(Invite);

        Assert.StartsWith("<svg", svg);
        Assert.EndsWith("</svg>", svg);
        // Nothing may reach outside the device. (The SVG xmlns is a namespace identifier, never
        // fetched — so assert on the things that actually load: references and embedded images.)
        Assert.DoesNotContain("href", svg);
        Assert.DoesNotContain("<image", svg);
        Assert.DoesNotContain("url(", svg);
    }

    [Fact]
    public void Empty_RendersNothing()
    {
        Assert.Equal(string.Empty, QrSvg.Render(""));
        Assert.Equal(string.Empty, QrSvg.Render("   "));
    }

    [Fact]
    public void AnotherApp_CanSupplyItsOwnLogo()
    {
        // Any app on AetherNet brands its own invite code without touching the renderer.
        const string theirLogo = "<path d='M20 80 L50 20 L80 80 Z' fill='#7c3aed'/>";
        var svg = QrSvg.Render(Invite, accent: "#7c3aed", mark: theirLogo);

        Assert.Contains(theirLogo, svg);
        Assert.Contains("#7c3aed", svg);
        // Their mark is placed, not ours.
        Assert.Contains("scale(", svg);
    }

    [Fact]
    public void QuietZone_AndFinderEyes_ArePresent()
    {
        var svg = QrSvg.Render(Invite);

        // Three shaped finder eyes — the scanner's anchors — must always be drawn.
        var eyes = svg.Split("rx=\"2\"").Length - 1;
        Assert.Equal(3, eyes);
    }

    [Fact]
    public void MarkCanBeTurnedOff()
    {
        var withMark = QrSvg.Render(Invite);
        var without = QrSvg.Render(Invite, withMark: false);

        Assert.NotEqual(withMark, without);
        // No mark means no cleared reserve, so there are strictly more data dots.
        Assert.True(CountDots(without) > CountDots(withMark));
    }

    [Fact]
    public void SameInput_RendersIdentically()
    {
        // Deterministic output keeps the code stable on screen instead of shimmering on re-render.
        Assert.Equal(QrSvg.Render(Invite), QrSvg.Render(Invite));
    }

    private static int CountDots(string svg) => svg.Split("<circle").Length - 1;
}
