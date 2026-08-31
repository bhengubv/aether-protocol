// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
/// The failure does not look like a database problem. <c>SqliteConnection</c> keeps a plain
/// <c>List</c> of its open commands; two threads inside it corrupt the list, and the throw comes out
/// of <c>RemoveCommand</c> as <c>IndexOutOfRangeException</c> when a command is disposed. On Android
/// that reached the top of the Blazor circuit and the whole page died with "Something went wrong" —
/// three times in one evening, each time looking like a different, random bug.
/// </para>
/// <para>
/// So this test does the one thing the two phones on the desk could not be made to do on demand:
/// hold the reader and the writer in the store at the same moment, many times over.
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

    [Fact]
    public async Task Reading_and_writing_held_cards_at_once_does_not_break_the_connection()
    {
        using var store = ADisk();

        // Seed, so the readers have rows to walk while the writers are inside the connection.
        for (var i = 0; i < 20; i++) store.HoldCard(ACard(i));

        var faults = new List<Exception>();
        var work = new List<Task>();

        // Pressure, not politeness. The window is between CreateCommand putting a command into the
        // connection's list and Dispose taking it out again, so what has to overlap is that pair —
        // many times, on many threads, with as little else in between as possible.
        using var go = new System.Threading.Barrier(16);

        for (var t = 0; t < 16; t++)
        {
            var mine = t;
            work.Add(Task.Factory.StartNew(() =>
            {
                go.SignalAndWait();
                try
                {
                    for (var i = 0; i < 400; i++)
                    {
                        if (mine % 3 == 0) store.HoldCard(ACard(mine * 1000 + i));
                        else if (mine % 3 == 1) store.HoldsCard($"KXJB7-MN2P{i % 10}/card{i % 20}");
                        else _ = store.GetHeldCards().Count;
                    }
                }
                catch (Exception why)
                {
                    lock (faults) faults.Add(why);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        }

        await Task.WhenAll(work);

        Assert.True(faults.Count == 0,
            "the store threw under concurrent use: " + string.Join(" | ", faults.Select(f => f.Message)));
        Assert.NotEmpty(store.GetHeldCards());
    }

    [Fact]
    public async Task Dropping_a_card_while_the_library_is_being_read_is_safe()
    {
        using var store = ADisk();
        for (var i = 0; i < 40; i++) store.HoldCard(ACard(i));

        var faults = new List<Exception>();

        var dropping = Task.Run(() =>
        {
            try { for (var i = 0; i < 40; i++) store.DropCard($"KXJB7-MN2P{i % 10}/card{i}"); }
            catch (Exception why) { lock (faults) faults.Add(why); }
        });

        var reading = Task.Run(() =>
        {
            try { for (var i = 0; i < 120; i++) _ = store.GetHeldCards().Count; }
            catch (Exception why) { lock (faults) faults.Add(why); }
        });

        await Task.WhenAll(dropping, reading);

        Assert.True(faults.Count == 0,
            "the store threw while dropping and reading at once: "
            + string.Join(" | ", faults.Select(f => f.Message)));
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
