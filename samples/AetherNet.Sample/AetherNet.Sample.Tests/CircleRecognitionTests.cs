// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Rendezvous;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Who gets dialled, and who never does.
///
/// <para>
/// A radio only ever sees a rotating address, so "is this beacon one of ours" cannot be answered by
/// looking at it. These cover the answer: a contact who has shared a routing key inside a session is
/// recognised behind every address they rotate through, and everybody else resolves to nobody — which
/// is what stops the radio dialling a stranger because their beacon happened to answer.
/// </para>
/// </summary>
public class CircleRecognitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aether-circle-" + Guid.NewGuid().ToString("N"));
    private readonly List<AetherStore> _stores = [];

    private AetherStore NewStore()
    {
        Directory.CreateDirectory(_dir);
        var store = new AetherStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
        _stores.Add(store);
        return store;
    }

    private (CircleDirectory Circle, FakeIdentity Me) APhone()
    {
        var me = new FakeIdentity();
        return (new CircleDirectory(NewStore(), me), me);
    }

    public void Dispose()
    {
        foreach (var s in _stores) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── the stranger ───────────────────────────────────────────────────────

    /// <summary>
    /// The case the whole thing exists for. An address nobody has shared a key for belongs to nobody,
    /// so there is nothing for the radio to dial — a cold connect is not refused by policy, it simply
    /// has no target.
    /// </summary>
    [Fact]
    public void An_address_from_nobody_we_know_resolves_to_nobody()
    {
        var (circle, _) = APhone();
        var (stranger, strangerMe) = APhone();

        Assert.Null(circle.Recognise(stranger.MyAddress()));
        Assert.Null(circle.Recognise(strangerMe.AetherTag));   // nor does a raw tag pass for an address
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOTANADDRESS")]
    public void Nonsense_resolves_to_nobody(string? address)
    {
        var (circle, _) = APhone();

        Assert.Null(circle.Recognise(address));
    }

    // ── the contact ────────────────────────────────────────────────────────

    /// <summary>
    /// Once a contact has handed over their routing key, their beacon has a name on it. This is the
    /// exchange that happens inside the established session, and the reason it is worth doing.
    /// </summary>
    [Fact]
    public void A_contact_who_shared_their_key_is_recognised_behind_their_address()
    {
        var (circle, _) = APhone();
        var (them, theirMe) = APhone();

        circle.Learn(theirMe.AetherTag, theirMe.RoutingKey);

        Assert.Equal(theirMe.AetherTag, circle.Recognise(them.MyAddress()));
    }

    /// <summary>
    /// The point of a rotating address is that it rotates. Recognition has to survive that, or it
    /// would work for fifteen minutes and then quietly stop.
    /// </summary>
    [Fact]
    public void Recognition_follows_them_across_an_epoch()
    {
        var (circle, _) = APhone();
        var (them, theirMe) = APhone();
        circle.Learn(theirMe.AetherTag, theirMe.RoutingKey);

        var now = DateTimeOffset.UtcNow;
        var later = now.AddHours(3);

        var early = them.MyAddress(now);
        var late = them.MyAddress(later);

        Assert.NotEqual(early, late);                                  // it really did rotate
        Assert.Equal(theirMe.AetherTag, circle.Recognise(early, now));
        Assert.Equal(theirMe.AetherTag, circle.Recognise(late, later));
    }

    /// <summary>
    /// A beacon composed just before an epoch turned over is read just after it. Without accepting the
    /// window just gone, every contact would go unrecognised for a moment every fifteen minutes.
    /// </summary>
    [Fact]
    public void An_address_from_the_epoch_just_gone_is_still_recognised()
    {
        var (circle, _) = APhone();
        var (them, theirMe) = APhone();
        circle.Learn(theirMe.AetherTag, theirMe.RoutingKey);

        var justBefore = DateTimeOffset.UtcNow;
        var justAfter = justBefore.AddSeconds(AetherNet.Identity.EphemeralRoutingId.DefaultEpochSeconds);

        Assert.Equal(theirMe.AetherTag, circle.Recognise(them.MyAddress(justBefore), justAfter));
    }

    // ── revoking ───────────────────────────────────────────────────────────

    /// <summary>
    /// The relationship is what granted the capability, so removing the contact has to take it away.
    /// A forgotten contact is a stranger again, and strangers are not dialled.
    /// </summary>
    [Fact]
    public void Forgetting_a_contact_stops_recognising_them()
    {
        var (circle, _) = APhone();
        var (them, theirMe) = APhone();
        circle.Learn(theirMe.AetherTag, theirMe.RoutingKey);

        circle.Forget(theirMe.AetherTag);

        Assert.Null(circle.Recognise(them.MyAddress()));
        Assert.False(circle.Knows(theirMe.AetherTag));
    }

    // ── surviving a restart ────────────────────────────────────────────────

    /// <summary>
    /// A restart must not turn every contact back into a stranger. The key is stored, so the next run
    /// starts already able to recognise everyone it could recognise before.
    /// </summary>
    [Fact]
    public void Recognition_survives_a_restart()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "restart.db");
        var me = new FakeIdentity();
        var (them, theirMe) = APhone();

        using (var first = new AetherStore(path))
            new CircleDirectory(first, me).Learn(theirMe.AetherTag, theirMe.RoutingKey);

        using var second = new AetherStore(path);
        var reopened = new CircleDirectory(second, me);

        Assert.Equal(theirMe.AetherTag, reopened.Recognise(them.MyAddress()));
        Assert.Equal(1, reopened.KnownCount);
    }

    // ── who hosts ──────────────────────────────────────────────────────────

    /// <summary>
    /// Exactly one of the two hosts. Both ends run this comparison with no way to ask each other, so
    /// if they ever agreed on the answer both would create a group, or neither would.
    /// </summary>
    [Fact]
    public void Exactly_one_of_two_phones_hosts()
    {
        const string A = "AAAAA-11111";
        const string B = "ZZZZZ-99999";

        Assert.NotEqual(GroupRole.HostsTheGroup(A, B), GroupRole.HostsTheGroup(B, A));
    }

    [Fact]
    public void The_lower_tag_hosts_and_says_so_every_time()
    {
        const string Lower = "AAAAA-11111";
        const string Higher = "ZZZZZ-99999";

        Assert.True(GroupRole.HostsTheGroup(Lower, Higher));
        Assert.False(GroupRole.HostsTheGroup(Higher, Lower));
        Assert.Equal(GroupRole.HostsTheGroup(Lower, Higher), GroupRole.HostsTheGroup(Lower, Higher));
    }

    /// <summary>A phone does not form a group with itself, and must not sit hosting an empty one.</summary>
    [Theory]
    [InlineData("AAAAA-11111", "AAAAA-11111")]
    [InlineData("AAAAA-11111", null)]
    [InlineData(null, "AAAAA-11111")]
    [InlineData(null, null)]
    public void Nobody_hosts_a_group_with_nobody(string? mine, string? theirs)
    {
        Assert.False(GroupRole.HostsTheGroup(mine, theirs));
    }
}
