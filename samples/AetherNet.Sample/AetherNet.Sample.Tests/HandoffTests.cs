// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Touch phones, and what you were looking at is on theirs.
///
/// <para>
/// The failure that matters here is not a crash — it is landing somebody on the wrong screen, or on a
/// screen they were never meant to see, from a gesture they cannot undo because they do not yet know
/// what happened. So the rules are: describe only what is genuinely a place, refuse anything this
/// build does not fully understand, and never guess.
/// </para>
/// </summary>
public class HandoffTests
{
    private const string Card = "aether://KXJB7-MN2P4/home";
    private const string Tag = "KXJB7-MN2P4";

    // ── What is worth handing over ───────────────────────────────────────────

    [Fact]
    public void A_conversation_is_a_place()
    {
        var note = Handoff.Describe($"/chat/{Tag}");
        Assert.NotNull(note);
        Assert.Equal(Handoff.Kind.Chat, note.Kind);
        Assert.Equal(Tag, note.Target);
    }

    [Fact]
    public void A_page_on_the_mesh_web_is_a_place()
    {
        var note = Handoff.Describe($"/meshweb?a={Uri.EscapeDataString(Card)}");
        Assert.NotNull(note);
        Assert.Equal(Handoff.Kind.Card, note.Kind);
        Assert.Equal(Card, note.Target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]                    // home
    [InlineData("/settings")]            // somewhere you pass through
    [InlineData("/welcome")]             // mid-wizard
    [InlineData("/share")]               // handing over the app, not a place
    [InlineData("/nearby")]
    [InlineData("/meshweb")]             // the mesh-web with nothing open
    [InlineData("/meshweb?b=other")]     // no address in it
    [InlineData("/chat/")]               // a conversation with nobody
    public void Everything_else_is_not(string? route)
        => Assert.Null(Handoff.Describe(route));

    // ── The part that makes it feel alive ────────────────────────────────────

    [Fact]
    public void The_half_written_sentence_goes_with_the_phone()
    {
        // This is the whole difference between a handoff and a bookmark. You are mid-sentence, you
        // touch a phone, and the sentence is there — not "open the same chat", but the actual words
        // you had not finished.
        var note = Handoff.Describe($"/chat/{Tag}", "meet me at the corner in ten", 0.5);

        Assert.Equal("meet me at the corner in ten", note!.Draft);

        var arrived = Handoff.Decode(Handoff.Encode(note));
        Assert.Equal("meet me at the corner in ten", arrived!.Draft);
    }

    [Fact]
    public void Where_you_were_reading_travels_as_a_fraction()
    {
        // A fraction, never a pixel count: the other phone is a different size, and pixels would land
        // somebody somewhere else entirely.
        var note = Handoff.Describe($"/chat/{Tag}", null, 0.42);
        Assert.Equal(0.42, note!.At);

        Assert.Equal(0.42, Handoff.Decode(Handoff.Encode(note))!.At);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    public void A_position_that_is_not_a_position_is_made_safe(double at, double? expected)
    {
        // Fed by a browser measurement, which is empty or mid-layout often enough to matter. A NaN
        // reaching the far side scrolls somebody to nowhere.
        Assert.Equal(expected, Handoff.Describe($"/chat/{Tag}", null, at)!.At);
    }

    [Fact]
    public void Nothing_typed_means_nothing_carried()
    {
        foreach (var empty in new[] { null, "", "   " })
            Assert.Null(Handoff.Describe($"/chat/{Tag}", empty, null)!.Draft);
    }

    [Fact]
    public void A_draft_too_long_to_be_a_gesture_is_left_behind()
    {
        // Past a point this stops being "where you were" and becomes a file transfer wearing a tap.
        var essay = new string('x', Handoff.LongestDraft + 1);
        Assert.Null(Handoff.Describe($"/chat/{Tag}", essay, null)!.Draft);

        var longest = new string('x', Handoff.LongestDraft);
        Assert.Equal(longest, Handoff.Describe($"/chat/{Tag}", longest, null)!.Draft);
    }

    [Fact]
    public void A_card_never_carries_a_draft()
    {
        // There is nothing to type into on a card, so a draft here would be one screen's state
        // leaking onto another.
        var note = Handoff.Describe($"/meshweb?a={Uri.EscapeDataString(Card)}", "typed elsewhere", 0.3);
        Assert.Null(note!.Draft);
        Assert.Equal(0.3, note.At);
    }

    [Fact]
    public void A_handoff_with_a_sentence_in_it_is_still_a_gesture()
    {
        var note = Handoff.Describe($"/chat/{Tag}", new string('x', Handoff.LongestDraft), 0.5);
        Assert.True(Handoff.Encode(note!).Length < 4096,
            $"a handoff with the longest draft is {Handoff.Encode(note!).Length} bytes");
    }

    // ── Where it lands ───────────────────────────────────────────────────────

    [Fact]
    public void What_was_open_here_opens_there()
    {
        foreach (var route in new[] { $"/chat/{Tag}", $"/meshweb?a={Uri.EscapeDataString(Card)}" })
        {
            var note = Handoff.Describe(route);
            Assert.NotNull(note);

            // Round trip through the wire, because that is what actually happens.
            var arrived = Handoff.Decode(Handoff.Encode(note));
            Assert.NotNull(arrived);

            Assert.Equal(route, Handoff.RouteFor(arrived));
        }
    }

    [Fact]
    public void An_address_with_awkward_characters_survives()
    {
        // Addresses carry slashes and colons, and a tag carries a dash. Any of those unescaped turns
        // one route into a different, valid-looking route — which is the exact failure that puts
        // somebody on a screen nobody chose.
        var note = Handoff.Describe("/meshweb?a=" + Uri.EscapeDataString("aether://KXJB7-MN2P4/my page?x=1&y=2"));
        Assert.NotNull(note);
        Assert.Equal("aether://KXJB7-MN2P4/my page?x=1&y=2", note.Target);

        var arrived = Handoff.Decode(Handoff.Encode(note));
        Assert.Equal(note.Target, arrived!.Target);
    }

    [Fact]
    public void A_trailing_slash_is_the_same_place()
        => Assert.Equal(Handoff.Describe($"/chat/{Tag}"), Handoff.Describe($"/chat/{Tag}/"));

    // ── Refusing what it does not understand ─────────────────────────────────

    [Fact]
    public void A_handoff_from_a_newer_build_lands_nowhere()
    {
        // Better to do nothing than to guess at a shape we have never seen. Somebody standing on the
        // wrong screen cannot tell whether the gesture worked or misfired.
        Assert.Null(Handoff.RouteFor(new Handoff.Note(Handoff.Version + 1, Handoff.Kind.Chat, Tag)));
    }

    [Fact]
    public void A_kind_this_build_does_not_know_lands_nowhere()
        => Assert.Null(Handoff.RouteFor(new Handoff.Note(Handoff.Version, (Handoff.Kind)99, Tag)));

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Something_that_is_not_a_handoff_reads_as_nothing(byte[]? body)
        => Assert.Null(Handoff.Decode(body));

    [Fact]
    public void The_two_markers_cannot_be_confused()
    {
        // One asks, the other answers. Reading an ask as an answer would send somebody nowhere and
        // look exactly like a tap that did not land.
        Assert.NotEqual(Handoff.Marker, Handoff.WantMarker);
        Assert.Equal(Handoff.Marker.Length, Handoff.WantMarker.Length);
    }

    [Fact]
    public void A_handoff_with_nothing_in_it_is_refused()
        => Assert.Null(Handoff.Decode(
            System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"kind\":2,\"target\":\"\"}")));

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(20260824);
        for (var i = 0; i < 2000; i++)
        {
            var junk = new byte[random.Next(1, 80)];
            random.NextBytes(junk);
            Handoff.Decode(junk);   // a note, or null. Never an exception.
        }
    }

    // ── Small enough to be a gesture ─────────────────────────────────────────

    [Fact]
    public void What_crosses_is_a_description_not_a_copy()
    {
        // The receiving phone already has the mesh and can fetch whatever this names. Sending the
        // content would be slower, larger, and stale on arrival.
        var note = Handoff.Describe($"/meshweb?a={Uri.EscapeDataString(Card)}");
        Assert.True(Handoff.Encode(note!).Length < 128,
            $"a handoff is {Handoff.Encode(note!).Length} bytes");
    }
}
