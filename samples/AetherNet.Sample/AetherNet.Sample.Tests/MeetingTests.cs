// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Two phones working out where to meet, from their tags and nothing else.
///
/// <para>
/// The thing that was missing. A tag is a hash of a key and cannot be turned back into one, so a
/// phone that had been given only a tag could not work out where its contact would be — and sat
/// saying "waiting for them" with that contact plainly in its list, while no radio started a single
/// operation. The only way in was to scan a QR code: two people, two phones and a camera, which is
/// the thing a mesh exists to avoid needing.
/// </para>
///
/// <para>
/// Both phones hold both tags the moment one has added the other, so both can work this out before
/// they have ever been in the same room, over whichever radio they happen to have.
/// </para>
/// </summary>
public class MeetingTests
{
    private const string Merlin = "7RB9G-97RTG";

    private const string P30 = "Y6TK9-EW9KK";

    // ── Both phones work out the same thing ───────────────────────────────────

    /// <summary>
    /// The same place, computed separately, with nothing sent between them.
    /// </summary>
    /// <remarks>
    /// The whole point. If the two derivations differ by one character each phone waits somewhere the
    /// other has never heard of, and it looks exactly like a radio that does not work.
    /// </remarks>
    [Fact]
    public void Both_phones_land_on_the_same_rendezvous()
    {
        var mine = Meeting.With(P30, Merlin);
        var theirs = Meeting.With(Merlin, P30);

        Assert.Equal(mine!.Value.Rendezvous, theirs!.Value.Rendezvous);
    }

    /// <summary>
    /// And exactly one of them opens it.
    /// </summary>
    /// <remarks>
    /// Both opening is the race that puts Android's "Invitation to connect" dialog in front of
    /// somebody who asked for nothing; neither opening is two phones waiting on each other forever.
    /// </remarks>
    [Fact]
    public void Exactly_one_of_them_starts()
    {
        var mine = Meeting.With(P30, Merlin)!.Value;
        var theirs = Meeting.With(Merlin, P30)!.Value;

        Assert.NotEqual(mine.IStart, theirs.IStart);
        Assert.True(theirs.IStart, "the lower tag did not start");
    }

    [Fact]
    public void It_knows_who_the_meeting_is_with()
    {
        Assert.Equal(Merlin, Meeting.With(P30, Merlin)!.Value.PeerTag);
    }

    // ── Different pairs, different places ─────────────────────────────────────

    /// <summary>Two different people are two different meetings.</summary>
    [Fact]
    public void A_different_person_is_a_different_place()
    {
        var one = Meeting.With(P30, Merlin)!.Value.Rendezvous;
        var other = Meeting.With(P30, "KXJB7-MN2P4")!.Value.Rendezvous;

        Assert.NotEqual(one, other);
    }

    /// <summary>A phone does not meet itself.</summary>
    [Fact]
    public void There_is_no_meeting_with_yourself()
    {
        Assert.Null(Meeting.With(P30, P30));
        Assert.Null(Meeting.With(P30, null));
        Assert.Null(Meeting.With(null, Merlin));
    }

    // ── What a radio takes from it ────────────────────────────────────────────

    /// <summary>
    /// A radio takes as much as it can carry and ignores the rest.
    /// </summary>
    /// <remarks>
    /// Wi-Fi Direct has room for nine characters after its mandatory prefix; a LoRa address has room
    /// for a handful of bits. Neither has to agree with the other about length, and neither has to be
    /// handed a size it then argues with.
    /// </remarks>
    [Fact]
    public void Every_radio_takes_the_same_prefix()
    {
        var meet = Meeting.With(P30, Merlin)!.Value;

        Assert.Equal(9, meet.Where(9).Length);
        Assert.Equal(4, meet.Where(4).Length);
        Assert.StartsWith(meet.Where(4), meet.Where(9), StringComparison.Ordinal);
        Assert.Equal(meet.Rendezvous, meet.Where(500));
        Assert.Equal("", meet.Where(0));
    }

    /// <summary>Nothing in it can upset a radio that has to put it in a name.</summary>
    [Fact]
    public void It_is_only_letters_and_digits()
    {
        var meet = Meeting.With(P30, Merlin)!.Value;

        Assert.Equal(Meeting.Length, meet.Rendezvous.Length);
        Assert.All(meet.Rendezvous, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"'{c}'"));
    }

    // ── The shapes other radios need ──────────────────────────────────────────

    /// <summary>
    /// Bluetooth finds people by advertising a service id, so the meeting has to be one.
    /// </summary>
    /// <remarks>
    /// One fixed id for the whole app means every phone running it answers every other — which is
    /// discovery, and discovery is the thing that must not happen. Advertising the meeting means only
    /// the person whose tag you were handed can see you.
    /// </remarks>
    [Fact]
    public void Both_phones_work_out_the_same_bluetooth_id()
    {
        Assert.Equal(Meeting.With(Merlin, P30)!.Value.Uuid(), Meeting.With(P30, Merlin)!.Value.Uuid());
    }

    [Fact]
    public void A_different_pair_advertises_a_different_id()
    {
        Assert.NotEqual(
            Meeting.With(Merlin, P30)!.Value.Uuid(),
            Meeting.With(Merlin, "KXJB7-MN2P4")!.Value.Uuid());
    }

    /// <summary>
    /// And it is a UUID a Bluetooth stack will accept, not sixteen random bytes.
    /// </summary>
    /// <remarks>
    /// Asserted on the written form rather than on byte indices, because that is what a stack parses
    /// and because the indices are exactly what got this wrong: .NET stores the first three fields
    /// little-endian, so RFC 4122's version octet is not where counting from the front puts it.
    /// </remarks>
    [Fact]
    public void The_bluetooth_id_is_well_formed()
    {
        var written = Meeting.With(Merlin, P30)!.Value.Uuid().ToString();

        Assert.Equal('4', written[14]);                        // version 4
        Assert.Contains(written[19], "89abAB");                // variant 1
    }

    /// <summary>
    /// A radio with almost no address space still gets something both sides compute.
    /// </summary>
    /// <remarks>
    /// LoRa has a handful of frequencies and a short address inside them. Two pairs can collide in a
    /// space that small; that costs a dropped frame and never a wrong link, because what arrives is
    /// still checked against a key.
    /// </remarks>
    [Fact]
    public void A_small_radio_gets_a_small_address()
    {
        var mine = Meeting.With(Merlin, P30)!.Value;
        var theirs = Meeting.With(P30, Merlin)!.Value;

        Assert.Equal(mine.Address(8), theirs.Address(8));
        Assert.InRange(mine.Address(8), 0u, 255u);
        Assert.InRange(mine.Address(16), 0u, 65535u);
    }

    [Fact]
    public void An_address_of_no_bits_is_refused()
    {
        var meet = Meeting.With(Merlin, P30)!.Value;

        Assert.Throws<ArgumentOutOfRangeException>(() => meet.Address(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => meet.Address(33));
    }

    /// <summary>Each shape is its own — a radio cannot be tracked from one to the next.</summary>
    [Fact]
    public void The_shapes_do_not_give_each_other_away()
    {
        var meet = Meeting.With(Merlin, P30)!.Value;

        Assert.DoesNotContain(
            meet.Uuid().ToString("N"), meet.Rendezvous, StringComparison.OrdinalIgnoreCase);
    }

    // ── The Wi-Fi Direct door it opens ────────────────────────────────────────

    /// <summary>
    /// Two phones holding only tags now compute the same Wi-Fi Direct group.
    /// </summary>
    /// <remarks>
    /// Measured on the bench before this existed: merlin hosted <c>DIRECT-AC8G9BTF8</c>, derived from
    /// its own key, and the P30 had no way to know that was the name — so it never joined, and the
    /// pair sat there with both radios ready.
    /// </remarks>
    [Fact]
    public void Two_phones_with_only_tags_work_out_the_same_group()
    {
        var hosting = GroupCredentials.ForMeeting(Meeting.With(Merlin, P30));
        var joining = GroupCredentials.ForMeeting(Meeting.With(P30, Merlin));

        Assert.True(WifiDirectCredentials.IsUsable(hosting));
        Assert.Equal(hosting!.NetworkName, joining!.NetworkName);
        Assert.Equal(hosting.Passphrase, joining.Passphrase);
    }

    /// <summary>And it is a group Android will actually accept.</summary>
    [Fact]
    public void The_group_is_one_android_will_take()
    {
        var credentials = GroupCredentials.ForMeeting(Meeting.With(Merlin, P30))!;

        Assert.StartsWith("DIRECT-", credentials.NetworkName, StringComparison.Ordinal);
        Assert.InRange(credentials.NetworkName.Length, 8, 32);
        Assert.InRange(credentials.Passphrase.Length, 8, 63);
    }

    /// <summary>
    /// The first-meeting group is not the one they use once they know each other.
    /// </summary>
    /// <remarks>
    /// It is derived from tags, which are handed out; the other is derived from a key, which is not.
    /// A pair that never moved off the first one would be meeting somewhere computable by anybody who
    /// had been given both tags, forever.
    /// </remarks>
    [Fact]
    public void It_is_not_the_group_they_settle_on()
    {
        var first = GroupCredentials.ForMeeting(Meeting.With(Merlin, P30))!;
        var settled = GroupCredentials.ForHost(System.Text.Encoding.UTF8.GetBytes("merlin's key"))!;

        Assert.NotEqual(first.NetworkName, settled.NetworkName);
    }

    [Fact]
    public void No_meeting_is_no_group()
    {
        Assert.Null(GroupCredentials.ForMeeting(null));
    }
}
