// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Reading a message that does not fit in one response.
///
/// <para>
/// <b>The bug this exists for.</b> The capability container publishes MLe — the most data the tag will
/// return in one response — and the read path ignored it. Le is a single byte in which zero means 256,
/// so a reader saying "as much as you have" was handed the entire file in one frame regardless of size.
/// A 108-byte message survives that; a 629-byte one does not. The transfer fails, the reader retries,
/// and the tap appears to do nothing at all: measured as six seconds of retries ending in the
/// platform's "couldn't read tag" toast, while the tag's own log showed a read that began and never
/// completed.
/// </para>
///
/// <para>
/// So these tests walk a long message the way a real reader does, one bounded read at a time, and
/// assert the two things that were wrong: that no response exceeds what was promised, and that the
/// whole message still arrives.
/// </para>
/// </summary>
public class Type4TagLongMessageTests
{
    private static readonly byte[] SelectApplication =
        [0x00, 0xA4, 0x04, 0x00, 0x07, 0xD2, 0x76, 0x00, 0x00, 0x85, 0x01, 0x01, 0x00];

    private static byte[] SelectFile(byte[] id) =>
        [0x00, 0xA4, 0x00, 0x0C, 0x02, id[0], id[1]];

    private static byte[] ReadAt(int offset, int le) =>
        [0x00, 0xB0, (byte)(offset >> 8), (byte)(offset & 0xFF), (byte)le];

    private static bool IsOk(byte[] r) => r.Length >= 2 && r[^2] == 0x90 && r[^1] == 0x00;

    /// <summary>A message the size of the provisioning tap, with recognisable content.</summary>
    private static byte[] LongMessage()
    {
        var message = new byte[627];
        for (var i = 0; i < message.Length; i++) message[i] = (byte)(i % 251);
        return message;
    }

    private static Type4Tag Armed(byte[] message)
    {
        var tag = new Type4Tag { Offer = message };

        Assert.True(IsOk(tag.Process(SelectApplication)), "the application should select");
        Assert.True(IsOk(tag.Process(SelectFile(Type4Tag.NdefFileId))), "the NDEF file should select");

        return tag;
    }

    /// <summary>
    /// <b>The fix.</b> Asking for everything gets at most what was promised.
    /// </summary>
    [Fact]
    public void A_read_never_returns_more_than_the_capability_container_promised()
    {
        var tag = Armed(LongMessage());

        // Le of zero is ISO 7816-4 for "up to 256" — the exact request that used to hand back the
        // whole file.
        var response = tag.Process(ReadAt(0, 0x00));

        Assert.True(IsOk(response));
        Assert.True(response.Length - 2 <= Type4Tag.MostPerRead,
            $"returned {response.Length - 2} bytes having promised at most {Type4Tag.MostPerRead}");
    }

    /// <summary>And that holds however greedily it is asked, at every offset.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(600)]
    public void No_offset_can_be_talked_into_an_over_long_response(int offset)
    {
        var tag = Armed(LongMessage());

        foreach (var le in new[] { 0x00, 0xFF, 0xFE })
        {
            var response = tag.Process(ReadAt(offset, le));
            Assert.True(response.Length - 2 <= Type4Tag.MostPerRead,
                $"offset {offset}, le {le:X2}: returned {response.Length - 2} bytes");
        }
    }

    /// <summary>
    /// The whole message still arrives, walked the way a reader walks it.
    /// </summary>
    /// <remarks>
    /// Bounding responses is only half the fix — a tag that never over-runs but also never finishes is
    /// the same dead tap. The first two bytes are the length, exactly as a reader reads them, and the
    /// rest must reassemble to what was offered.
    /// </remarks>
    [Fact]
    public void The_whole_message_arrives_across_several_reads()
    {
        var message = LongMessage();
        var tag = Armed(message);

        var header = tag.Process(ReadAt(0, 2));
        Assert.True(IsOk(header));

        var declared = (header[0] << 8) | header[1];
        Assert.Equal(message.Length, declared);

        var got = new List<byte>();
        var at = 2;
        var reads = 0;

        while (got.Count < declared && reads < 100)
        {
            var response = tag.Process(ReadAt(at, Type4Tag.MostPerRead));
            Assert.True(IsOk(response), $"read at {at} failed");

            var payload = response.Length - 2;
            Assert.True(payload > 0, $"read at {at} returned nothing — a reader would retry forever");

            got.AddRange(response[..payload]);
            at += payload;
            reads++;
        }

        Assert.Equal(message, got);
    }

    /// <summary>
    /// The whole thing fits in a handful of exchanges rather than a dozen.
    /// </summary>
    /// <remarks>
    /// Every exchange is a chance for two phones held together by hand to lose contact. The published
    /// MLe was 59 bytes, which made this message eleven round trips; it is now 246, which is three.
    /// </remarks>
    [Fact]
    public void A_provisioning_sized_message_takes_only_a_few_reads()
    {
        var message = LongMessage();
        var tag = Armed(message);

        var reads = 0;
        var at = 0;

        while (at < message.Length + 2)
        {
            var response = tag.Process(ReadAt(at, 0x00));
            at += response.Length - 2;
            reads++;
        }

        Assert.True(reads <= 4, $"the walk took {reads} reads; it should take no more than four");
    }

    /// <summary>The completion fires once, when the last byte has actually gone.</summary>
    /// <remarks>
    /// This is what tells the giver the tap landed. Firing it early makes a failed handover look
    /// successful, which is worse than saying nothing.
    /// </remarks>
    [Fact]
    public void The_tap_is_reported_landed_exactly_once_at_the_end()
    {
        var message = LongMessage();
        var tag = Armed(message);

        var landed = 0;
        tag.Read += () => landed++;

        var at = 0;
        while (at < message.Length + 2)
        {
            if (at + Type4Tag.MostPerRead < message.Length + 2)
                Assert.Equal(0, landed);

            var response = tag.Process(ReadAt(at, Type4Tag.MostPerRead));
            at += response.Length - 2;
        }

        Assert.Equal(1, landed);
    }

    /// <summary>
    /// The proven Wi-Fi tap is unchanged by any of it.
    /// </summary>
    /// <remarks>
    /// That one has worked against a stock handset repeatedly — the receiving phone raised its own
    /// "connect to this network?" dialog. It must still come back in one read.
    /// </remarks>
    [Fact]
    public void The_proven_wifi_message_still_arrives_in_a_single_read()
    {
        var wifi = WifiHandover.Message("DIRECT-Aether Y6TK9-EW9KK", "8QK2M4TVXR7NPJ3W");
        var tag = Armed(wifi);

        var landed = 0;
        tag.Read += () => landed++;

        var response = tag.Process(ReadAt(0, 0x00));

        Assert.True(IsOk(response));
        Assert.Equal(wifi.Length + 2, response.Length - 2);
        Assert.Equal(1, landed);
    }
}
