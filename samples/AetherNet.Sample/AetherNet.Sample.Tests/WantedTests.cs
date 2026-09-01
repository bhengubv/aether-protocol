// SPDX-License-Identifier: MIT

using System.Linq;
using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The addresses a phone was handed but could not reach yet — reach, across distance.
/// </summary>
/// <remarks>
/// A device that hosts a card is a server, and the piece that was missing is being visited from more
/// than a room away. Half is free: an address is text, it travels over any channel there is. The
/// other half is the moment after — the fetch fails because the author is not near and there is no
/// relay yet — and the address had nowhere to live. This is where it lives, so the reach is not lost
/// between "someone told me their address" and "I have their card."
/// </remarks>
public class WantedTests
{
    private static Wanted APhone() => new(new InMemoryCardStore());

    private const string A = "aether://KXJB7-MN2P4/me";
    private const string B = "aether://7RB9G-97RTG/shop";

    [Fact]
    public void An_address_that_could_not_be_reached_is_kept()
    {
        var wanted = APhone();

        Assert.True(wanted.Add(A));
        Assert.True(wanted.Holds(A));
        Assert.Equal([A], wanted.All);
    }

    [Fact]
    public void Only_a_mesh_address_is_worth_keeping()
    {
        var wanted = APhone();

        Assert.False(wanted.Add("https://example.com"));
        Assert.False(wanted.Add("just some words"));
        Assert.False(wanted.Add("   "));
        Assert.False(wanted.Add(null));
        Assert.Empty(wanted.All);
    }

    [Fact]
    public void Wanting_the_same_address_twice_keeps_one_copy_at_the_top()
    {
        var wanted = APhone();
        wanted.Add(A);
        wanted.Add(B);

        wanted.Add(A);   // again — still matters

        Assert.Equal([A, B], wanted.All);   // one A, and it is first
    }

    [Fact]
    public void Reaching_an_address_removes_it_from_wanted()
    {
        var wanted = APhone();
        wanted.Add(A);
        wanted.Add(B);

        // What the browser does the moment an address finally resolves.
        Assert.True(wanted.Remove(A));

        Assert.False(wanted.Holds(A));
        Assert.Equal([B], wanted.All);
    }

    [Fact]
    public void Forgetting_an_address_that_is_not_there_changes_nothing()
    {
        var wanted = APhone();
        wanted.Add(A);

        Assert.False(wanted.Remove(B));
        Assert.Equal([A], wanted.All);
    }

    [Fact]
    public void The_list_survives_a_new_view_over_the_same_store()
    {
        var store = new InMemoryCardStore();
        new Wanted(store).Add(A);

        // A fresh Wanted over the same device storage — what a reopened app gets.
        Assert.True(new Wanted(store).Holds(A));
    }

    [Fact]
    public void The_list_is_bounded_so_a_run_of_dead_addresses_cannot_grow_without_limit()
    {
        var wanted = APhone();

        for (var i = 0; i < Wanted.Most + 50; i++)
            wanted.Add($"aether://KXJB7-MN2P4/p{i}");

        Assert.Equal(Wanted.Most, wanted.All.Count);
        // Newest kept, oldest fell off the end.
        Assert.Equal($"aether://KXJB7-MN2P4/p{Wanted.Most + 49}", wanted.All[0]);
        Assert.DoesNotContain("aether://KXJB7-MN2P4/p0", wanted.All);
    }

    [Fact]
    public void A_corrupt_store_starts_empty_rather_than_throwing()
    {
        var store = new InMemoryCardStore();
        store.SetWanted("{ this is not json");

        var wanted = new Wanted(store);

        Assert.Empty(wanted.All);
        Assert.True(wanted.Add(A));   // and still works
    }
}
