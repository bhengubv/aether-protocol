// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The other radio, played against a reader without a phone in the room.
///
/// <para>
/// NFC-F is empty in a way NFC-A is not. Measured on a stock Redmi: a tap on NFC-A gets answered by
/// X's profile service, because it registers the same identifier we do. On this radio there is no
/// contest, and no assumption about who wins a race.
/// </para>
///
/// <para>
/// The format is unforgiving in three specific places, and each one fails as a tap that does nothing:
/// the attribute block carries a checksum a reader verifies before it trusts anything; the message
/// length is three bytes rather than two; and a block list element is two bytes or three depending on
/// one bit, so reading it the wrong way answers with the wrong part of the message.
/// </para>
/// </summary>
public class Type3TagTests
{
    private static readonly byte[] Id = [0x02, 0xFE, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66];

    private static Type3Tag Armed(byte[]? message) => new() { Id = Id, Offer = message };

    /// <summary>A CHECK for one service and a list of blocks, as a reader sends it.</summary>
    private static byte[] ReadBlocks(params int[] blocks)
    {
        var packet = new List<byte> { 0, 0x06 };
        packet.AddRange(Id);
        packet.Add(1);                 // one service
        packet.Add(0x0B); packet.Add(0x00);   // service 0x000B, little-endian on the wire
        packet.Add((byte)blocks.Length);

        foreach (var b in blocks)
        {
            // Two-byte element: top bit set, then a one-byte block number.
            packet.Add(0x80);
            packet.Add((byte)b);
        }

        packet[0] = (byte)packet.Count;
        return [.. packet];
    }

    private static byte[] BlockOf(byte[] reply, int index) =>
        reply[(13 + index * Type3Tag.BlockSize)..(13 + (index + 1) * Type3Tag.BlockSize)];

    private static bool IsOk(byte[]? reply) =>
        reply is { Length: >= 12 } && reply[10] == 0x00 && reply[11] == 0x00;

    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An emulated tag's identity must start 0x02 0xFE, and the platform enforces it.
    /// </summary>
    [Fact]
    public void The_tag_identity_is_marked_as_emulated()
    {
        var tag = new Type3Tag();

        Assert.Equal(0x02, tag.Id[0]);
        Assert.Equal(0xFE, tag.Id[1]);
        Assert.Equal(8, tag.Id.Length);
    }

    /// <summary>
    /// A packet addressed to a different tag gets silence, not an answer.
    /// </summary>
    /// <remarks>
    /// Two tags in one field is the reader's problem to resolve, and answering for somebody else turns
    /// a recoverable collision into corrupt data.
    /// </remarks>
    [Fact]
    public void A_packet_for_another_tag_is_ignored()
    {
        var packet = ReadBlocks(0);
        packet[3] = 0xEE;                       // somebody else's identity

        Assert.Null(Armed([1, 2, 3]).Process(packet));
    }

    // ── The attribute block ──────────────────────────────────────────────────

    /// <summary>
    /// <b>The checksum is verified by the reader before it trusts anything else.</b>
    /// </summary>
    [Fact]
    public void The_attribute_block_checksums_itself()
    {
        var block = Armed(new byte[40]).Attributes();

        var sum = 0;
        for (var i = 0; i < 14; i++) sum += block[i];

        Assert.Equal((byte)(sum >> 8), block[14]);
        Assert.Equal((byte)sum, block[15]);
    }

    /// <summary>
    /// The length occupies three bytes — the whole reason this radio is worth the work.
    /// </summary>
    /// <remarks>
    /// A Type 4 tag states its length in two bytes, which is where "64 KB is a hard ceiling" came
    /// from. It was one format's field width, quoted as though it were physics.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(1_000_000)]
    public void The_length_is_carried_in_three_bytes(int size)
    {
        var block = Armed(new byte[size]).Attributes();
        var stated = (block[11] << 16) | (block[12] << 8) | block[13];

        Assert.Equal(size, stated);
    }

    /// <summary>Past two bytes, a Type 4 tag could not have said this at all.</summary>
    [Fact]
    public void A_message_larger_than_the_old_ceiling_is_describable()
    {
        var block = Armed(new byte[100_000]).Attributes();
        var stated = (block[11] << 16) | (block[12] << 8) | block[13];

        Assert.True(stated > ushort.MaxValue, "the point of this radio is that it can say this");
        Assert.Equal(100_000, stated);
    }

    /// <summary>Writing is not offered, and the tag says so rather than failing later.</summary>
    [Fact]
    public void The_tag_is_read_only_and_admits_it()
    {
        var block = Armed([1, 2, 3]).Attributes();

        Assert.Equal(0x00, block[2]);    // no blocks writable per command
        Assert.Equal(0x00, block[10]);   // read-only flag
    }

    /// <summary>The capacity covers the message rather than being a guess.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(16, 1)]
    [InlineData(17, 2)]
    [InlineData(627, 40)]
    public void The_stated_capacity_covers_the_message(int size, int blocks)
    {
        var block = Armed(new byte[size]).Attributes();

        Assert.Equal(blocks, (block[3] << 8) | block[4]);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>Block zero is the attribute block.</summary>
    [Fact]
    public void Block_zero_is_the_attribute_block()
    {
        var tag = Armed(new byte[40]);
        var reply = tag.Process(ReadBlocks(0));

        Assert.True(IsOk(reply));
        Assert.Equal(tag.Attributes(), BlockOf(reply!, 0));
    }

    /// <summary>And the message starts at block one.</summary>
    [Fact]
    public void The_message_starts_at_block_one()
    {
        var message = new byte[32];
        for (var i = 0; i < message.Length; i++) message[i] = (byte)(i + 1);

        var reply = Armed(message).Process(ReadBlocks(1));

        Assert.True(IsOk(reply));
        Assert.Equal(message[..16], BlockOf(reply!, 0));
    }

    /// <summary>
    /// Several blocks come back in one answer, in the order asked for — not in block order.
    /// </summary>
    /// <remarks>
    /// A reader is entitled to ask out of order, and sorting them helpfully would hand it the right
    /// bytes against the wrong block numbers. Asked backwards here so that returning them in block
    /// order fails.
    /// </remarks>
    [Fact]
    public void Blocks_come_back_in_the_order_they_were_asked_for()
    {
        var message = new byte[48];
        for (var i = 0; i < message.Length; i++) message[i] = (byte)i;

        var reply = Armed(message).Process(ReadBlocks(2, 1));

        Assert.True(IsOk(reply));
        Assert.Equal(2, reply![12]);
        Assert.Equal(message[16..32], BlockOf(reply, 0));
        Assert.Equal(message[0..16], BlockOf(reply, 1));
    }

    /// <summary>
    /// A short final block is padded, because the format has no partial block.
    /// </summary>
    [Fact]
    public void A_short_final_block_is_padded_not_truncated()
    {
        var reply = Armed([9, 9, 9]).Process(ReadBlocks(1));

        Assert.True(IsOk(reply));
        Assert.Equal(Type3Tag.BlockSize, BlockOf(reply!, 0).Length);
        Assert.Equal([9, 9, 9], BlockOf(reply!, 0)[..3]);
        Assert.Equal(new byte[13], BlockOf(reply!, 0)[3..]);
    }

    /// <summary>
    /// <b>A three-byte block-list element addresses blocks past 255.</b>
    /// </summary>
    /// <remarks>
    /// The bit that distinguishes the two forms is the trap: read it backwards and every block number
    /// after the first comes from the wrong offset, which presents as a tap that returns nonsense
    /// rather than a tap that fails.
    /// </remarks>
    [Fact]
    public void A_long_block_element_reaches_past_the_first_two_hundred_and_fifty_six()
    {
        var message = new byte[8000];
        for (var i = 0; i < message.Length; i++) message[i] = (byte)(i % 251);

        // Block 300, expressed as a three-byte element: top bit clear, then little-endian.
        var packet = new List<byte> { 0, 0x06 };
        packet.AddRange(Id);
        packet.Add(1);
        packet.Add(0x0B); packet.Add(0x00);
        packet.Add(1);
        packet.Add(0x00);
        packet.Add(300 & 0xFF); packet.Add(300 >> 8);
        packet[0] = (byte)packet.Count;

        var reply = Armed(message).Process([.. packet]);

        Assert.True(IsOk(reply));
        Assert.Equal(message[(299 * 16)..(300 * 16)], BlockOf(reply!, 0));
    }

    /// <summary>Reading past the end is refused rather than answered with rubbish.</summary>
    [Fact]
    public void Reading_past_the_end_is_refused()
    {
        var reply = Armed([1, 2, 3]).Process(ReadBlocks(9));

        Assert.False(IsOk(reply));
        Assert.Equal(0xFF, reply![10]);
    }

    /// <summary>An empty tag has nothing to give and says so.</summary>
    [Fact]
    public void An_empty_tag_refuses_a_message_read()
    {
        Assert.False(IsOk(Armed(null).Process(ReadBlocks(1))));
    }

    /// <summary>More blocks than we published as readable is refused.</summary>
    [Fact]
    public void Asking_for_more_blocks_than_published_is_refused()
    {
        var many = Enumerable.Range(1, Type3Tag.BlocksPerRead + 1).ToArray();

        Assert.False(IsOk(Armed(new byte[200]).Process(ReadBlocks(many))));
    }

    /// <summary>Writing is refused, not silently accepted.</summary>
    [Fact]
    public void A_write_is_refused()
    {
        var packet = ReadBlocks(1);
        packet[1] = 0x08;                       // UPDATE

        var reply = Armed(new byte[64]).Process(packet);

        Assert.False(IsOk(reply));
        Assert.Equal(0x09, reply![1]);          // answered as an update, refused
    }

    // ── Finishing ────────────────────────────────────────────────────────────

    /// <summary>The tap is reported landed when the last block has gone.</summary>
    [Fact]
    public void The_tap_is_reported_landed_on_the_final_block()
    {
        var tag = Armed(new byte[32]);
        var landed = 0;
        tag.Read += () => landed++;

        Assert.True(IsOk(tag.Process(ReadBlocks(1))));
        Assert.Equal(0, landed);

        Assert.True(IsOk(tag.Process(ReadBlocks(2))));
        Assert.Equal(1, landed);
    }

    /// <summary>A message the size of the provisioning tap walks cleanly, block by block.</summary>
    [Fact]
    public void A_provisioning_sized_message_reassembles()
    {
        var message = new byte[627];
        for (var i = 0; i < message.Length; i++) message[i] = (byte)(i % 251);

        var tag = Armed(message);
        var got = new List<byte>();

        for (var block = 1; got.Count < message.Length; block += Type3Tag.BlocksPerRead)
        {
            var ask = Enumerable.Range(block, Type3Tag.BlocksPerRead)
                .Where(b => (b - 1) * Type3Tag.BlockSize < message.Length)
                .ToArray();

            var reply = tag.Process(ReadBlocks(ask));
            Assert.True(IsOk(reply), $"read at block {block} failed");

            for (var i = 0; i < reply![12]; i++) got.AddRange(BlockOf(reply, i));
        }

        Assert.Equal(message, got.Take(message.Length));
    }
}
