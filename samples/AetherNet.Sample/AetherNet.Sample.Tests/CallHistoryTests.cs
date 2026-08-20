// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Calls that happened, written down.
///
/// <para>
/// A missed call is stored as an incoming call with no connect time — there is no separate "missed"
/// flag, because a flag is one more thing that can drift out of step with what actually occurred.
/// These pin that, and pin that the row is written once however the call ended: hung up, declined,
/// rung out or failed. A history that quietly omits the unusual endings is the history nobody can
/// trust.
/// </para>
/// </summary>
public class CallHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aether-calls-{Guid.NewGuid():N}.db");
    private readonly AetherStore _store;

    public CallHistoryTests() => _store = new AetherStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_path); } catch { }
    }

    private static CallRecord Call(
        string peer = "BQ6NH-V6Q5N",
        bool outgoing = true,
        long started = 1_000,
        long connected = 2_000,
        long ended = 5_000,
        string reason = "Normal",
        string? id = null)
        => new(id ?? Guid.NewGuid().ToString(), peer, outgoing, started, connected, ended, reason);

    // ── what makes a call missed ───────────────────────────────────────────

    /// <summary>Incoming, never connected. That — and only that — is a missed call.</summary>
    [Fact]
    public void An_incoming_call_that_never_connected_is_missed()
    {
        var c = Call(outgoing: false, connected: 0, reason: "Timeout");

        Assert.True(c.Missed);
        Assert.False(c.Connected);
        Assert.Null(c.Duration);
    }

    /// <summary>An unanswered call you placed is not "missed" — nobody missed anything.</summary>
    [Fact]
    public void An_outgoing_call_that_was_never_answered_is_not_missed()
        => Assert.False(Call(outgoing: true, connected: 0, reason: "Timeout").Missed);

    /// <summary>An answered incoming call is not missed either, however short.</summary>
    [Fact]
    public void An_answered_call_is_not_missed()
        => Assert.False(Call(outgoing: false, connected: 2_000).Missed);

    // ── duration ───────────────────────────────────────────────────────────

    /// <summary>Duration is time connected to time ended, not time rung to time ended.</summary>
    [Fact]
    public void Duration_counts_from_the_moment_it_connected()
    {
        var c = Call(started: 1_000, connected: 3_000, ended: 9_000);

        Assert.Equal(TimeSpan.FromSeconds(6), c.Duration);
    }

    /// <summary>Nonsense timestamps produce no duration rather than a negative one.</summary>
    [Fact]
    public void A_call_that_ended_before_it_connected_has_no_duration()
        => Assert.Null(Call(connected: 9_000, ended: 3_000).Duration);

    // ── storing and reading back ───────────────────────────────────────────

    [Fact]
    public void A_saved_call_comes_back()
    {
        var c = Call(peer: "HTJY7-Z7HT0", outgoing: false, connected: 0, reason: "Timeout");
        _store.SaveCall(c);

        var back = Assert.Single(_store.GetCalls());
        Assert.Equal(c.Id, back.Id);
        Assert.Equal("HTJY7-Z7HT0", back.PeerTag);
        Assert.False(back.Outgoing);
        Assert.True(back.Missed);
        Assert.Equal("Timeout", back.Reason);
    }

    /// <summary>Newest first — a call list is read from the top.</summary>
    [Fact]
    public void Calls_come_back_newest_first()
    {
        _store.SaveCall(Call(started: 1_000));
        _store.SaveCall(Call(started: 3_000));
        _store.SaveCall(Call(started: 2_000));

        var got = _store.GetCalls().Select(c => c.StartedMs).ToArray();

        Assert.Equal(new long[] { 3_000, 2_000, 1_000 }, got);
    }

    /// <summary>
    /// The same call saved twice is one row. EndAsync runs on every path a call can finish by, and a
    /// history that double-counts is worse than one that under-counts.
    /// </summary>
    [Fact]
    public void Saving_the_same_call_twice_leaves_one_row()
    {
        var c = Call(id: "same-call", connected: 0, ended: 4_000);
        _store.SaveCall(c);
        _store.SaveCall(c with { ConnectedMs = 2_000, EndedMs = 8_000, Reason = "Normal" });

        var back = Assert.Single(_store.GetCalls());
        Assert.Equal(2_000, back.ConnectedMs);   // the later write wins
        Assert.Equal("Normal", back.Reason);
    }

    [Fact]
    public void Calls_can_be_read_for_one_person()
    {
        _store.SaveCall(Call(peer: "AAAAA-11111"));
        _store.SaveCall(Call(peer: "BBBBB-22222"));
        _store.SaveCall(Call(peer: "AAAAA-11111"));

        Assert.Equal(2, _store.GetCallsWith("AAAAA-11111").Count);
        Assert.Single(_store.GetCallsWith("BBBBB-22222"));
        Assert.Empty(_store.GetCallsWith("CCCCC-33333"));
    }

    // ── the badge ──────────────────────────────────────────────────────────

    [Fact]
    public void The_missed_count_counts_only_unanswered_incoming_calls()
    {
        _store.SaveCall(Call(outgoing: false, connected: 0));    // missed
        _store.SaveCall(Call(outgoing: false, connected: 0));    // missed
        _store.SaveCall(Call(outgoing: false, connected: 2_000)); // answered
        _store.SaveCall(Call(outgoing: true, connected: 0));      // they did not answer me

        Assert.Equal(2, _store.CountMissed());
    }

    [Fact]
    public void No_calls_means_nothing_missed()
        => Assert.Equal(0, _store.CountMissed());

    /// <summary>History survives the app closing — that is the whole point of writing it down.</summary>
    [Fact]
    public void History_survives_a_restart()
    {
        _store.SaveCall(Call(peer: "HTJY7-Z7HT0", outgoing: false, connected: 0));
        _store.Dispose();

        using var reopened = new AetherStore(_path);

        var back = Assert.Single(reopened.GetCalls());
        Assert.True(back.Missed);
        Assert.Equal(1, reopened.CountMissed());
    }
}
