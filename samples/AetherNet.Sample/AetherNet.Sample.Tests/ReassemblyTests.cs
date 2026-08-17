// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Putting a message back together from radio fragments, under the conditions a radio actually
/// provides rather than the ones a single-threaded test provides.
///
/// <para>
/// Two things make this harder than it looks. Bluetooth callbacks arrive on whatever thread the
/// platform feels like using, so fragments for different messages land concurrently. And the fragment
/// header identifies a message with a single byte — 256 ids that wrap — with nothing in it to say
/// which <i>attempt</i> a fragment belongs to. A half-finished message therefore leaves pieces lying
/// around that a later message with the same id can be silently completed by.
/// </para>
///
/// <para>
/// The consequence of getting it wrong is not a dropped message, which would be obvious. It is a
/// message assembled from two different messages' pieces — bytes that deserialize into nonsense or
/// fail their authentication tag, looking for all the world like a broken session.
/// </para>
/// </summary>
public class ReassemblyTests
{
    private static byte[] Message(byte fill, int length) => Enumerable.Repeat(fill, length).ToArray();

    /// <summary>Fragments of a message, exactly as the transport would cut them.</summary>
    private static IReadOnlyList<byte[]> Cut(byte[] body, byte id, int mtu = 64) =>
        MeshFraming.Fragment(body, mtu, id);

    // ── Fragments from two messages must never be mixed ───────────────────────

    /// <summary>
    /// A message that never finished leaves pieces behind. A later message reusing that id must be
    /// assembled from its own fragments only — never completed by whatever the abandoned one left.
    /// </summary>
    [Fact]
    public void A_half_finished_message_does_not_complete_a_later_one()
    {
        var reassembler = new MeshFraming.Reassembler();
        var abandoned = Cut(Message(0xAA, 200), id: 7);
        var wanted = Message(0xBB, 200);
        var fresh = Cut(wanted, id: 7);

        // The first attempt loses everything after its opening fragment.
        Assert.Null(reassembler.Accept(abandoned[0]));

        // The same id comes round again. Feeding every fragment of the new message must produce the
        // new message — not a splice of the two.
        byte[]? assembled = null;
        foreach (var frame in fresh) assembled = reassembler.Accept(frame) ?? assembled;

        Assert.Equal(wanted, assembled);
    }

    /// <summary>
    /// The order that actually causes the damage: the later message's opening fragment is the one that
    /// goes missing, so the abandoned message's opening fragment is sitting there ready to fill the gap.
    ///
    /// <para>
    /// Time is what separates the two. A message's fragments arrive within milliseconds of each other,
    /// so anything still waiting much later is not part of the message arriving now — the same rule IP
    /// fragment reassembly has always used.
    /// </para>
    /// </summary>
    [Fact]
    public void An_abandoned_fragment_is_never_used_to_finish_a_later_message()
    {
        var reassembler = new MeshFraming.Reassembler();
        var start = new DateTime(2026, 8, 15, 15, 0, 0, DateTimeKind.Utc);
        var abandoned = Cut(Message(0xAA, 200), id: 7);
        var fresh = Cut(Message(0xBB, 200), id: 7);

        reassembler.Accept(abandoned[0], start);

        // Long enough later that the earlier attempt is plainly dead. Everything except the new
        // message's opening fragment: nothing may be emitted, because the only thing that could
        // complete it belongs to a message that was given up on.
        var later = start.Add(MeshFraming.Reassembler.FragmentLifetime).AddSeconds(1);
        byte[]? assembled = null;
        for (var i = 1; i < fresh.Count; i++) assembled = reassembler.Accept(fresh[i], later) ?? assembled;

        Assert.Null(assembled);
    }

    [Fact]
    public void A_message_whose_fragments_arrive_promptly_is_never_expired()
    {
        var reassembler = new MeshFraming.Reassembler();
        var start = new DateTime(2026, 8, 15, 15, 0, 0, DateTimeKind.Utc);
        var body = Message(0x5A, 500);
        var frames = Cut(body, id: 3);

        // A slow but ordinary send: each fragment a moment after the last.
        byte[]? assembled = null;
        for (var i = 0; i < frames.Count; i++)
            assembled = reassembler.Accept(frames[i], start.AddMilliseconds(200 * i)) ?? assembled;

        Assert.Equal(body, assembled);
    }

    // ── Fragments arrive on whatever thread the radio chose ───────────────────

    /// <summary>
    /// Bluetooth callbacks are not serialised onto one thread. A reassembler that assumes they are
    /// will, under load, corrupt its own bookkeeping or throw inside a callback — and a frame lost
    /// inside a callback is lost silently.
    /// </summary>
    [Fact]
    public async Task Fragments_arriving_on_many_threads_all_come_back_whole()
    {
        var reassembler = new MeshFraming.Reassembler();
        var expected = new Dictionary<byte, byte[]>();
        var frames = new List<(byte Id, byte[] Frame)>();

        for (var i = 0; i < 40; i++)
        {
            var id = (byte)i;
            var body = Message((byte)(i + 1), 300);
            expected[id] = body;
            foreach (var frame in Cut(body, id)) frames.Add((id, frame));
        }

        var assembled = new ConcurrentBag<byte[]>();
        await Parallel.ForEachAsync(frames, async (item, _) =>
        {
            var whole = reassembler.Accept(item.Frame);
            if (whole is not null) assembled.Add(whole);
            await Task.CompletedTask;
        });

        Assert.Equal(expected.Count, assembled.Count);
        foreach (var whole in assembled)
            Assert.Contains(expected.Values, e => e.SequenceEqual(whole));
    }

    // ── The ordinary cases still hold ─────────────────────────────────────────

    [Fact]
    public void A_message_arriving_in_order_comes_back_whole()
    {
        var reassembler = new MeshFraming.Reassembler();
        var body = Message(0x5A, 500);

        byte[]? assembled = null;
        foreach (var frame in Cut(body, id: 1)) assembled = reassembler.Accept(frame) ?? assembled;

        Assert.Equal(body, assembled);
    }

    [Fact]
    public void A_message_arriving_backwards_comes_back_whole()
    {
        var reassembler = new MeshFraming.Reassembler();
        var body = Message(0x5A, 500);

        byte[]? assembled = null;
        foreach (var frame in Cut(body, id: 1).Reverse()) assembled = reassembler.Accept(frame) ?? assembled;

        Assert.Equal(body, assembled);
    }

    [Fact]
    public void The_same_fragment_arriving_twice_does_not_break_the_message()
    {
        var reassembler = new MeshFraming.Reassembler();
        var body = Message(0x5A, 300);
        var frames = Cut(body, id: 1);

        byte[]? assembled = null;
        foreach (var frame in frames)
        {
            assembled = reassembler.Accept(frame) ?? assembled;
            assembled = reassembler.Accept(frame) ?? assembled;   // the radio repeated itself
        }

        Assert.Equal(body, assembled);
    }
}
