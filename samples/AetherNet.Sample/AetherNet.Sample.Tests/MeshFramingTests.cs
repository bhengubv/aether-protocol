// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

public class MeshFramingTests
{
    private static byte[] Bytes(int size) =>
        Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();

    // ── Frame size ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(23)]      // Bluetooth default, before negotiation
    [InlineData(185)]     // common negotiated value
    [InlineData(512)]
    [InlineData(517)]     // produced 514-byte writes, refused every time
    [InlineData(1024)]    // a stack claiming more than the protocol allows
    public void Fragment_never_exceeds_one_attribute(int mtu)
    {
        foreach (var frame in MeshFraming.Fragment(Bytes(4096), mtu, messageId: 7))
            Assert.True(frame.Length <= MeshFraming.MaxAttributeValue,
                $"a {frame.Length}-byte frame at mtu {mtu} would be silently refused");
    }

    [Fact]
    public void Fragment_splits_further_on_a_smaller_mtu()
    {
        var few = MeshFraming.Fragment(Bytes(2000), mtu: 512, messageId: 1).Count;
        var many = MeshFraming.Fragment(Bytes(2000), mtu: 64, messageId: 1).Count;

        Assert.True(many > few);
    }

    [Fact]
    public void Fragment_emits_one_frame_for_an_empty_message() =>
        Assert.Single(MeshFraming.Fragment([], mtu: 185, messageId: 0));

    [Theory]
    [InlineData(23)]
    [InlineData(517)]
    public void UsablePayload_leaves_room_for_the_header(int mtu) =>
        Assert.True(MeshFraming.UsablePayload(mtu) > 0);

    // ── Reassembly ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]     // either side of a single frame
    [InlineData(5000)]
    public void Reassembler_restores_the_original_message(int size)
    {
        var original = Bytes(size);
        var reassembler = new MeshFraming.Reassembler();

        byte[]? whole = null;
        foreach (var frame in MeshFraming.Fragment(original, mtu: 185, messageId: 3))
            whole = reassembler.Accept(frame) ?? whole;

        Assert.Equal(original, whole);
    }

    [Fact]
    public void Reassembler_accepts_fragments_out_of_order()
    {
        var original = Bytes(900);
        var reassembler = new MeshFraming.Reassembler();

        byte[]? whole = null;
        foreach (var frame in MeshFraming.Fragment(original, mtu: 185, messageId: 9).Reverse())
            whole = reassembler.Accept(frame) ?? whole;

        Assert.Equal(original, whole);
    }

    [Fact]
    public void Reassembler_returns_nothing_until_the_last_piece()
    {
        var frames = MeshFraming.Fragment(Bytes(900), mtu: 185, messageId: 4).ToArray();
        var reassembler = new MeshFraming.Reassembler();

        for (var i = 0; i < frames.Length - 1; i++)
            Assert.Null(reassembler.Accept(frames[i]));

        Assert.NotNull(reassembler.Accept(frames[^1]));
    }

    [Fact]
    public void Reassembler_keeps_concurrent_messages_apart()
    {
        var a = Encoding.UTF8.GetBytes(new string('a', 700));
        var b = Encoding.UTF8.GetBytes(new string('b', 700));
        var reassembler = new MeshFraming.Reassembler();

        var fa = MeshFraming.Fragment(a, mtu: 185, messageId: 1).ToArray();
        var fb = MeshFraming.Fragment(b, mtu: 185, messageId: 2).ToArray();

        byte[]? gotA = null, gotB = null;
        for (var i = 0; i < Math.Max(fa.Length, fb.Length); i++)   // interleaved, as a radio delivers
        {
            if (i < fa.Length) gotA = reassembler.Accept(fa[i]) ?? gotA;
            if (i < fb.Length) gotB = reassembler.Accept(fb[i]) ?? gotB;
        }

        Assert.Equal(a, gotA);
        Assert.Equal(b, gotB);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x02 })]                    // truncated header
    [InlineData(new byte[] { 0x02, 1, 5, 0, 2, 0 })]     // index beyond the count
    public void Reassembler_ignores_a_malformed_frame(byte[] frame) =>
        Assert.Null(new MeshFraming.Reassembler().Accept(frame));

    // ── Handshake ─────────────────────────────────────────────────────────────

    [Fact]
    public void Handshake_carries_the_rotating_address()
    {
        var frame = MeshFraming.HandshakeFor("abc123def456");

        Assert.Equal(MeshFraming.FrameHandshake, frame[0]);
        Assert.Equal("abc123def456", MeshFraming.ReadHandshake(frame));
    }

    [Fact]
    public void ReadHandshake_rejects_a_fragment()
    {
        foreach (var frame in MeshFraming.Fragment(Bytes(50), mtu: 185, messageId: 1))
            Assert.Null(MeshFraming.ReadHandshake(frame));
    }

    [Fact]
    public void FrameKinds_are_distinct()
    {
        byte[] kinds =
        [
            MeshFraming.FrameHandshake, MeshFraming.FrameFragment,
            MeshFraming.FramePing, MeshFraming.FramePong,
        ];

        Assert.Equal(kinds.Length, kinds.Distinct().Count());
    }
}
