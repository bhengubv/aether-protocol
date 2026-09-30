// SPDX-License-Identifier: MIT

using AetherNet.Rendezvous;
using Xunit;

namespace AetherNetNodeService.Host.Tests;

/// <summary>Whom a phone points its pair-by-pair radios at.</summary>
public class MeetingHostTests
{
    private const string P30 = "9DMPE-YEWAE";
    private const string Pixel = "GK5GC-AZWAQ";
    private const string Away = "71P7B-TPERH";   // sorts below both, and is not in the room

    private static Func<string, bool> Here(params string[] here) => tag => here.Contains(tag);

    [Fact]
    public void With_nobody_here_it_is_the_lowest_sorting_contact()
    {
        Assert.Equal(Away, MeetingHost.Choose([Pixel, Away], Here(), current: null));
    }

    /// <summary>
    /// The bug on the bench: an absent contact sorted lowest, so the P30's radios chased them every half-minute
    /// while the Pixel sat in the same room.
    /// </summary>
    [Fact]
    public void Somebody_here_beats_somebody_absent_who_sorts_lower()
    {
        Assert.Equal(Pixel, MeetingHost.Choose([Pixel, Away], Here(Pixel), current: Away));
    }

    [Fact]
    public void Two_phones_in_a_room_choose_each_other()
    {
        var p30Chooses = MeetingHost.Choose([Pixel, Away], Here(Pixel), current: null);
        var pixelChooses = MeetingHost.Choose([P30], Here(P30), current: null);

        Assert.Equal(Pixel, p30Chooses);
        Assert.Equal(P30, pixelChooses);
    }

    [Fact]
    public void Among_several_here_it_is_the_lowest_sorting_of_them()
    {
        Assert.Equal(P30, MeetingHost.Choose([Pixel, P30, Away], Here(Pixel, P30), current: Pixel));
    }

    /// <summary>A link dropping does not swing the radios to somebody absent.</summary>
    [Fact]
    public void When_everybody_leaves_the_radios_stay_on_whoever_they_were_on()
    {
        Assert.Equal(Pixel, MeetingHost.Choose([Pixel, Away], Here(), current: Pixel));
    }

    [Fact]
    public void Somebody_no_longer_a_contact_is_not_kept()
    {
        Assert.Equal(Away, MeetingHost.Choose([Pixel, Away], Here(), current: P30));
    }

    [Fact]
    public void No_contacts_means_nobody()
    {
        Assert.Null(MeetingHost.Choose([], Here(), current: Pixel));
        Assert.Null(MeetingHost.Choose(["", ""], Here(), current: null));
    }
}
