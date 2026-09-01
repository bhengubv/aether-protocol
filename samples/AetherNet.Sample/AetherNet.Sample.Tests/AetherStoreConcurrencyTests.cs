// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AetherNet.Browser;
using AetherNet.Sample.Shared.Data;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// One connection, many threads — the phone's actual shape.
/// </summary>
/// <remarks>
/// <para>
/// This store keeps a single long-lived <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> behind a
/// lock, which is right for a device. What was wrong is that four of its methods did not take the
/// lock, and they were exactly the ones a radio calls: a card arriving from a peer is written while
/// the person holding the phone is reading their library.
/// </para>
/// <para>
/// The failure is not a database problem. <c>SqliteConnection</c> keeps a plain in-memory
/// <c>List</c> of its open commands; two threads mutate that list at once and the throw comes out of
/// <c>RemoveCommand</c> as an <b>index-out-of-range</b> when a command is disposed. On Android it
/// reached the top of the Blazor circuit and killed the page — three times in one evening, each
/// looking like a different random bug.
/// </para>
/// <para>
/// <b>Why this version is deterministic where the first was flaky.</b> The first draft asserted on
/// <i>any</i> exception and hammered an on-disk database with sixteen threads for four hundred rounds
/// each. That is right for provoking the race but wrong for a verdict: it ran for tens of seconds,
/// scaled with machine load, and — the real sin — a transient <c>SQLITE_BUSY</c> from ordinary disk
/// contention under a loaded test run was counted as a failure even though the lock had done its job.
/// A false failure that only appears under load is worse than no test.
/// </para>
/// <para>
/// So the verdict is scoped to the <i>one thing the lock prevents</i>: corruption of that in-memory
/// command list, which surfaces as an index / range / null / invalid-operation throw and never as a
/// <c>SqliteException</c>. A busy or locked database is a legitimate, expected outcome of concurrent
/// disk access and is filtered out — filtering it cannot hide the bug, because the bug is not a
/// <c>SqliteException</c>. With the lock present that corruption is impossible, so this test
/// <b>cannot false-fail</b>; with the lock removed a barrier-released burst makes the corruption
/// near-certain, so it still catches a regression. It is bounded and finishes in a second or two
/// regardless of what else is running.
/// </para>
/// </remarks>
public class AetherStoreConcurrencyTests : IDisposable
{
    private readonly List<string> _files = [];

    private AetherStore ADisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-race-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return new AetherStore(path);
    }

    private static HeldCard ACard(int i) => new(
        Address: $"KXJB7-MN2P{i % 10}/card{i}",
        AuthorTag: "KXJB7-MN2P4",
        AuthorKey: new byte[32],
        Name: $"card{i}",
        Title: $"Card {i}",
        Version: i,
        RootHash: new string('a', 64),
        Signature: new byte[64],
        Descriptor: "{}",
        GotMs: 1_700_000_000_000 + i,
        GotFrom: "7RB9G-97RTG");

    /// <summary>
    /// The corruption this test exists to catch, told apart from a database that is merely busy.
    /// </summary>
    /// <remarks>
    /// A <c>SqliteException</c> is disk contention — two threads legitimately queuing for the file —
    /// and the lock is allowed to let one of those surface rather than prevent it. The command-list
    /// corruption is an in-memory throw from <see cref="System.Collections.Generic.List{T}"/>: index,
    /// range, null, or an invalid-operation from a connection used in two places at once. Only the
    /// latter is a failure of the thing under test.
    /// </remarks>
    private static bool IsCorruption(Exception why) => why switch
    {
        IndexOutOfRangeException => true,
        ArgumentOutOfRangeException => true,
        NullReferenceException => true,
        InvalidOperationException => true,
        _ => why.GetType().Name != "SqliteException",
    };

    /// <summary>Release N threads together, run each through <paramref name="round"/>, collect only corruption.</summary>
    private static IReadOnlyList<Exception> Storm(int threads, int rounds, Action<int, int> round)
    {
        var corruption = new ConcurrentQueue<Exception>();
        using var go = new Barrier(threads);
        var running = new List<Thread>(threads);

        for (var t = 0; t < threads; t++)
        {
            var mine = t;
            var thread = new Thread(() =>
            {
                go.SignalAndWait();
                for (var i = 0; i < rounds; i++)
                {
                    try { round(mine, i); }
                    catch (Exception why) when (!IsCorruption(why)) { /* busy disk, not the race */ }
                    catch (Exception why) { corruption.Enqueue(why); }
                }
            })
            { IsBackground = true };
            running.Add(thread);
        }

        foreach (var thread in running) thread.Start();
        foreach (var thread in running) Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a worker hung");

        return [.. corruption];
    }

    [Fact]
    public void Reading_and_writing_held_cards_at_once_never_corrupts_the_connection()
    {
        using var store = ADisk();

        // Seed, so the readers walk real rows — a reader that holds a command open across several
        // Read() calls is the widest half of the race.
        for (var i = 0; i < 20; i++) store.HoldCard(ACard(i));

        var corruption = Storm(threads: 8, rounds: 300, round: (mine, i) =>
        {
            switch (mine % 4)
            {
                case 0: store.HoldCard(ACard(mine * 10_000 + i)); break;      // writer: open + close a command
                case 1: _ = store.GetHeldCards().Count; break;                // reader: command open across the walk
                case 2: _ = store.HoldsCard($"KXJB7-MN2P{i % 10}/card{i % 20}"); break;
                default: _ = store.GetSetting("nothing"); break;
            }
        });

        Assert.True(corruption.Count == 0,
            "the one connection was corrupted under concurrent use: "
            + string.Join(" | ", corruption.Select(f => f.GetType().Name + ": " + f.Message)));
        Assert.NotEmpty(store.GetHeldCards());
    }

    [Fact]
    public void Dropping_a_card_while_the_library_is_being_read_never_corrupts_the_connection()
    {
        using var store = ADisk();
        for (var i = 0; i < 40; i++) store.HoldCard(ACard(i));

        // Half the threads delete, half walk the list — drop and read on the one connection at once.
        var corruption = Storm(threads: 8, rounds: 200, round: (mine, i) =>
        {
            if (mine % 2 == 0) store.DropCard($"KXJB7-MN2P{i % 10}/card{i % 40}");
            else _ = store.GetHeldCards().Count;
        });

        Assert.True(corruption.Count == 0,
            "the one connection was corrupted while dropping and reading at once: "
            + string.Join(" | ", corruption.Select(f => f.GetType().Name + ": " + f.Message)));
    }

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
        }

        GC.SuppressFinalize(this);
    }
}
