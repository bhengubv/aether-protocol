// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

public class TagPaletteTests
{
    // ── Agreement across devices ──────────────────────────────────────────────

    [Theory]
    [InlineData("DY5CF-84G9T")]
    [InlineData("BH8CZ-B09CA")]
    [InlineData("KXJB7-MN2P4")]
    public void For_returns_the_same_colour_on_every_device(string tag) =>
        // No per-process randomness — string.GetHashCode would fail this.
        Assert.Equal(TagPalette.For(tag), TagPalette.For(tag));

    [Theory]
    [InlineData("DY5CF-84G9T")]
    [InlineData("BH8CZ-B09CA")]
    public void Initial_returns_the_same_letter_on_every_device(string tag) =>
        Assert.Equal(TagPalette.Initial(tag), TagPalette.Initial(tag));

    // ── Telling people apart ──────────────────────────────────────────────────

    [Fact]
    public void For_distinguishes_people_in_one_conversation()
    {
        string[] tags = ["DY5CF-84G9T", "BH8CZ-B09CA", "JPJMX-GR3N7", "KXJB7-MN2P4"];

        var colours = tags.Select(TagPalette.For).Distinct().Count();

        Assert.True(colours > 1, "every tag resolved to one colour — nobody could be told apart");
    }

    // ── Output shape ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DY5CF-84G9T")]
    [InlineData("")]
    [InlineData(null)]
    public void For_always_returns_a_usable_colour(string? tag) =>
        Assert.Matches("^#[0-9a-fA-F]{6}$", TagPalette.For(tag));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Initial_falls_back_when_there_is_no_tag(string? tag) =>
        Assert.Equal("?", TagPalette.Initial(tag));
}
