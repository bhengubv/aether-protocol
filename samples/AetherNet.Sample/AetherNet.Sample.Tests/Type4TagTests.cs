// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The tap, played out command by command.
///
/// <para>
/// A reader does not ask a tag for "the message". It selects an application by name, reads a
/// capability container to find out where the message lives, selects that file, and reads it in
/// pieces — and if any single step answers wrongly the two phones touch and nothing happens, with no
/// error anywhere to say why. That is the worst possible failure for a feature whose whole promise is
/// that it just works when you hold two phones together, and it is untestable by holding two phones
/// together because the failure looks the same as not having touched properly.
/// </para>
///
/// <para>
/// So the reader is played here instead.
/// </para>
/// </summary>
public class Type4TagTests
{
    private const string Who = "Y6TK9-EW9KK";

    private static Type4Tag Armed(string who = Who) => new() { Offer = Ndef.Tag(who) };

    // ── The commands a real reader sends ─────────────────────────────────────

    private static byte[] SelectApplication() =>
        [0x00, 0xA4, 0x04, 0x00, 0x07, .. Type4Tag.NdefApplication, 0x00];

    private static byte[] SelectFile(byte[] id) =>
        [0x00, 0xA4, 0x00, 0x0C, 0x02, .. id];

    private static byte[] ReadBinary(int offset, int length) =>
        [0x00, 0xB0, (byte)(offset >> 8), (byte)(offset & 0xFF), (byte)length];

    private static bool IsOk(byte[] r) =>
        r.Length >= 2 && r[^2] == 0x90 && r[^1] == 0x00;

    private static byte[] Body(byte[] r) => r[..^2];

    /// <summary>Walk the tag exactly as a reader does, and return the NDEF message it yielded.</summary>
    private static byte[] ReadTheTag(Type4Tag tag, int chunk = 0x3B)
    {
        Assert.True(IsOk(tag.Process(SelectApplication())), "the application was not selected");

        Assert.True(IsOk(tag.Process(SelectFile(Type4Tag.CapabilityContainerId))), "no capability container");
        var cc = Body(tag.Process(ReadBinary(0, 15)));
        Assert.Equal(15, cc.Length);

        // The reader believes the CC, so the CC has to be right: it says which file holds the message.
        var fileId = new[] { cc[9], cc[10] };
        Assert.Equal(Type4Tag.NdefFileId, fileId);

        Assert.True(IsOk(tag.Process(SelectFile(fileId))), "the NDEF file was not selected");

        // Length first, two bytes, big-endian.
        var header = Body(tag.Process(ReadBinary(0, 2)));
        var length = (header[0] << 8) | header[1];

        return Reassemble(tag, length, chunk);
    }

    /// <summary>
    /// Read <paramref name="length"/> bytes out of the selected NDEF file, and give up rather than
    /// spin.
    /// </summary>
    /// <remarks>
    /// Bounded deliberately. A tag that reports the wrong length — which is precisely what a broken
    /// one does — otherwise leaves the reader asking for bytes past the end forever. A test that
    /// hangs tells you nothing; a test that fails tells you what broke.
    /// </remarks>
    private static byte[] Reassemble(Type4Tag tag, int length, int chunk = 0x3B)
    {
        Assert.InRange(length, 1, 4096);

        var message = new List<byte>(length);
        var offset = 2;

        while (message.Count < length)
        {
            var take = Math.Min(chunk, length - message.Count);
            var part = Body(tag.Process(ReadBinary(offset, take)));
            Assert.True(part.Length > 0,
                $"the tag stopped answering {message.Count} bytes into a message it said was {length}");
            message.AddRange(part);
            offset += part.Length;
        }

        return message.ToArray();
    }

    [Fact]
    public void A_reader_that_follows_the_spec_gets_the_address()
    {
        Assert.Equal(Who, Ndef.ReadTag(ReadTheTag(Armed())));
    }

    [Theory]
    [InlineData(1)]     // one byte at a time — pathological, and legal
    [InlineData(15)]
    [InlineData(0x3B)]  // what the capability container advertises
    [InlineData(255)]   // the most a single READ BINARY can ask for
    public void The_address_survives_however_the_reader_chops_it_up(int chunk)
    {
        // Readers differ. Some take the advertised maximum, some are conservative, and a message that
        // only reassembles at one particular chunk size is a tap that works on one phone.
        Assert.Equal(Who, Ndef.ReadTag(ReadTheTag(Armed(), chunk)));
    }

    [Fact]
    public void A_full_length_tag_still_crosses()
    {
        // An AetherTag is eleven characters with the dash. Short, but let us not discover a ceiling in
        // somebody's kitchen.
        Assert.Equal("ZZZZZ-ZZZZZ", Ndef.ReadTag(ReadTheTag(Armed("ZZZZZ-ZZZZZ"))));
    }

    [Fact]
    public void The_tap_lands_exactly_once_and_only_when_the_message_was_taken()
    {
        var tag = Armed();
        var landed = 0;
        tag.Read += () => landed++;

        tag.Process(SelectApplication());
        tag.Process(SelectFile(Type4Tag.CapabilityContainerId));
        tag.Process(ReadBinary(0, 15));
        Assert.Equal(0, landed);                      // reading the container is not a tap

        ReadTheTagFrom(tag);
        Assert.Equal(1, landed);
    }

    private static void ReadTheTagFrom(Type4Tag tag) => ReadFromSelected(tag);

    // ── Everything else that touches the phone ───────────────────────────────

    [Fact]
    public void A_reader_asking_for_somebody_elses_application_gets_nothing()
    {
        // Tap-to-pay terminals, transit gates, and the other apps that share this handset's NFC.
        var tag = Armed();
        byte[] someoneElse = [0x00, 0xA4, 0x04, 0x00, 0x07, 0xA0, 0x00, 0x00, 0x00, 0x03, 0x10, 0x10, 0x00];
        Assert.Equal(Type4Tag.FileNotFound, tag.Process(someoneElse));
    }

    [Fact]
    public void A_tag_that_is_not_armed_offers_nothing()
    {
        var tag = new Type4Tag();   // nothing on offer
        Assert.Equal(Type4Tag.FileNotFound, tag.Process(SelectApplication()));
        Assert.Equal(Type4Tag.FileNotFound, tag.Process(SelectFile(Type4Tag.NdefFileId)));
    }

    [Fact]
    public void The_message_cannot_be_read_before_the_application_is_selected()
    {
        // Otherwise a reader that skipped a step reads two zero bytes and concludes the tag is empty
        // — which is indistinguishable, from the outside, from a tap that did not connect.
        var tag = Armed();
        Assert.Equal(Type4Tag.FileNotFound, tag.Process(SelectFile(Type4Tag.NdefFileId)));
    }

    [Fact]
    public void What_is_being_offered_is_frozen_for_the_length_of_one_tap()
    {
        // A reader takes the message across several reads. If the offer changed underneath it, the
        // taker assembles half of one address and half of another — a URL that resolves to nothing.
        var tag = Armed();
        tag.Process(SelectApplication());

        tag.Offer = Ndef.Tag("AAAAA-AAAAA");

        Assert.Equal(Who, Ndef.ReadTag(ReadFromSelected(tag)));
    }

    private static byte[] ReadFromSelected(Type4Tag tag)
    {
        Assert.True(IsOk(tag.Process(SelectFile(Type4Tag.NdefFileId))), "the NDEF file was not selected");
        var header = Body(tag.Process(ReadBinary(0, 2)));
        Assert.Equal(2, header.Length);
        return Reassemble(tag, (header[0] << 8) | header[1]);
    }

    [Fact]
    public void Taking_the_phones_apart_starts_the_next_tap_from_the_beginning()
    {
        var tag = Armed();
        tag.Process(SelectApplication());
        tag.Process(SelectFile(Type4Tag.NdefFileId));

        tag.Deactivated();

        // A half-finished conversation must not be resumable by whatever touches the phone next.
        Assert.Equal(Type4Tag.FileNotFound, tag.Process(SelectFile(Type4Tag.NdefFileId)));
        Assert.Equal(Type4Tag.FileNotFound, tag.Process(ReadBinary(0, 2)));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x00, 0xA4 })]
    [InlineData(new byte[] { 0x00, 0xA4, 0x04 })]
    public void A_truncated_command_is_refused_rather_than_indexed_into(byte[] apdu)
    {
        // This is fed by hardware in somebody else's pocket. It does not get to throw.
        Assert.NotNull(Armed().Process(apdu));
    }

    [Fact]
    public void Nonsense_never_throws()
    {
        var tag = Armed();
        var random = new Random(20260823);

        for (var i = 0; i < 2000; i++)
        {
            var apdu = new byte[random.Next(0, 40)];
            random.NextBytes(apdu);
            var answer = tag.Process(apdu);
            Assert.True(answer.Length >= 2, "every answer is at least a status word");
        }
    }

    [Fact]
    public void An_unknown_instruction_says_so_rather_than_guessing()
    {
        Assert.Equal(Type4Tag.NotSupported, Armed().Process([0x00, 0xFF, 0x00, 0x00]));
    }

    [Fact]
    public void Reading_past_the_end_is_refused()
    {
        var tag = Armed();
        tag.Process(SelectApplication());
        tag.Process(SelectFile(Type4Tag.CapabilityContainerId));
        Assert.Equal(Type4Tag.WrongParameters, tag.Process(ReadBinary(9999, 10)));
    }

    // ── The capability container the reader believes ─────────────────────────

    [Fact]
    public void The_capability_container_says_what_the_spec_says_it_says()
    {
        var cc = Type4Tag.CapabilityContainer;

        Assert.Equal(15, cc.Length);
        Assert.Equal(15, (cc[0] << 8) | cc[1]);              // CCLEN describes itself
        Assert.Equal(0x20, cc[2]);                           // mapping version 2.0
        Assert.Equal(0x04, cc[7]);                           // NDEF File Control TLV
        Assert.Equal(0x06, cc[8]);                           // ...of length 6
        Assert.Equal(Type4Tag.NdefFileId, new[] { cc[9], cc[10] });
        Assert.Equal(0x00, cc[13]);                          // read granted
        Assert.Equal(0xFF, cc[14]);                          // write denied — a tag, not a noticeboard
    }

    [Fact]
    public void The_advertised_read_size_is_one_a_single_command_can_actually_ask_for()
    {
        // A READ BINARY carries its length in one byte. Advertising more than 255 invites a reader to
        // ask for something it cannot express, and what happens then is the reader's business.
        var cc = Type4Tag.CapabilityContainer;
        var mle = (cc[3] << 8) | cc[4];
        Assert.InRange(mle, 1, 255);
    }

    [Fact]
    public void A_real_invite_fits_in_the_file_the_container_advertises()
    {
        var cc = Type4Tag.CapabilityContainer;
        var maxFile = (cc[11] << 8) | cc[12];
        var message = Ndef.Tag("ZZZZZ-ZZZZZ");
        Assert.True(message.Length + 2 < maxFile,
            $"the message is {message.Length + 2} bytes against an advertised {maxFile}");
    }
}
