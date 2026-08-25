// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The other radio — a Type 3 tag, spoken over NFC-F.
///
/// <para>
/// Everything this app has emulated until now is a Type 4 tag on NFC-A: the radio every bank card,
/// every sticker and every tap-to-share app on the handset is already using. Measured here, that is
/// not a crowd we win — a stock Redmi answers a tap with X's profile service, because X registers the
/// same identifier we do and the two phones race for who reads whom.
/// </para>
///
/// <para>
/// <b>NFC-F is empty.</b> It is the Suica radio, and outside Japan essentially nothing on an ordinary
/// handset emulates on it. Different signalling, different addressing — system codes and an NFCID2
/// rather than application identifiers — so the contest we have been losing does not exist there.
/// </para>
///
/// <para>
/// It also carries more. A Type 4 tag states its message length in two bytes, which is where the 64 KB
/// ceiling comes from; a Type 3 tag uses <b>three</b>, so the same field describes up to 16 MB. That is
/// not enough to carry an application and it was never going to be — but it is 256× what we assumed,
/// and the assumption was written down as though it were physics.
/// </para>
///
/// <para>
/// This is the protocol, kept away from any phone so a reader's whole exchange can be played against
/// it in a test. FeliCa framing: a length byte, a command byte, the tag's own eight-byte identity, and
/// then blocks of sixteen bytes addressed by number.
/// </para>
/// </summary>
public sealed class Type3Tag
{
    /// <summary>The system code every NFC Forum Type 3 Tag answers to.</summary>
    /// <remarks>
    /// Not ours and not chosen — a reader polls for this specific value when it is looking for an NDEF
    /// tag on this radio. Suica answers to 0x0003; NDEF is 0x12FC.
    /// </remarks>
    public const ushort NdefSystemCode = 0x12FC;

    /// <summary>The service code for reading NDEF from a tag that will not be written to.</summary>
    public const ushort ReadOnlyService = 0x000B;

    /// <summary>Sixteen bytes per block, always. The format has no other size.</summary>
    public const int BlockSize = 16;

    /// <summary>Block zero is the attribute block; the message itself starts at block one.</summary>
    public const int FirstMessageBlock = 1;

    /// <summary>Read this many blocks in one command, at most.</summary>
    /// <remarks>
    /// Four is what the specification's own examples use and what readers expect. Sixty-four bytes a
    /// go is slow, and it is the format's business rather than ours — the number is published in the
    /// attribute block and a reader holds us to it, exactly as MLe did on the other radio.
    /// </remarks>
    public const byte BlocksPerRead = 4;

    private const byte Check = 0x06;
    private const byte CheckReply = 0x07;
    private const byte Update = 0x08;
    private const byte UpdateReply = 0x09;

    /// <summary>Everything is fine.</summary>
    private const byte Ok1 = 0x00, Ok2 = 0x00;

    /// <summary>The reader asked for something that is not there.</summary>
    private const byte Bad1 = 0xFF, Bad2 = 0xA1;

    /// <summary>This tag's identity on the radio. Must begin 0x02 0xFE for an emulated one.</summary>
    /// <remarks>
    /// Not a convention we picked: the platform reserves that prefix for card emulation so that an
    /// emulated tag can never be mistaken for a real FeliCa card, and it rejects any other value.
    /// </remarks>
    public byte[] Id { get; init; } = [0x02, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>What the next tap presents, or null when nothing is offered.</summary>
    public byte[]? Offer { get; set; }

    /// <summary>Raised when a reader has taken every block of the message.</summary>
    public event Action? Read;

    /// <summary>
    /// The attribute block — sixteen bytes telling a reader what it is holding.
    /// </summary>
    /// <remarks>
    /// The length lives in three bytes here, which is the whole difference from the other radio. The
    /// checksum is a plain sum of the preceding fourteen bytes and a reader will reject the tag
    /// outright if it disagrees, so it is computed rather than written down.
    /// </remarks>
    public byte[] Attributes()
    {
        var length = Offer?.Length ?? 0;
        var capacity = (ushort)Math.Max(1, (length + BlockSize - 1) / BlockSize);

        var block = new byte[BlockSize];
        block[0] = 0x10;                       // version 1.0
        block[1] = BlocksPerRead;              // Nbr — blocks per read
        block[2] = 0;                          // Nbw — writing is not offered
        block[3] = (byte)(capacity >> 8);      // Nmaxb
        block[4] = (byte)capacity;
        // bytes 5..8 unused
        block[9] = 0x00;                       // WriteFlag — not mid-write
        block[10] = 0x00;                      // RWFlag — read only
        block[11] = (byte)(length >> 16);      // Ln, three bytes
        block[12] = (byte)(length >> 8);
        block[13] = (byte)length;

        var sum = 0;
        for (var i = 0; i < 14; i++) sum += block[i];
        block[14] = (byte)(sum >> 8);
        block[15] = (byte)sum;

        return block;
    }

    /// <summary>
    /// Answer one packet from a reader.
    /// </summary>
    /// <returns>The reply, or null when the packet is not for us and should be ignored.</returns>
    public byte[]? Process(byte[]? packet)
    {
        if (packet is null || packet.Length < 10) return null;

        var command = packet[1];

        // The reader names which tag it is talking to. Answering for somebody else is worse than
        // silence — two tags in one field is a situation the reader is entitled to resolve itself.
        for (var i = 0; i < 8; i++)
            if (packet[2 + i] != Id[i]) return null;

        return command switch
        {
            Check => Reply(packet),
            Update => Refuse(UpdateReply),
            _ => null,
        };
    }

    /// <summary>
    /// A read: walk the block list and hand back sixteen bytes for each.
    /// </summary>
    private byte[]? Reply(byte[] packet)
    {
        var at = 10;
        if (at >= packet.Length) return null;

        // Service list. We publish one service and check nothing else, but the count still has to be
        // stepped over correctly or every block number after it is read from the wrong offset.
        var services = packet[at++];
        if (services == 0 || at + services * 2 > packet.Length) return null;
        at += services * 2;

        if (at >= packet.Length) return null;
        var wanted = packet[at++];
        if (wanted == 0 || wanted > BlocksPerRead) return Refuse(CheckReply);

        var blocks = new List<int>(wanted);

        for (var i = 0; i < wanted; i++)
        {
            if (at >= packet.Length) return Refuse(CheckReply);

            // Top bit set means the element is two bytes and the block number is one; clear means
            // three bytes with a little-endian pair. Reading this the wrong way round is the classic
            // way to answer a Type 3 read with the wrong part of the message.
            var brief = (packet[at] & 0x80) != 0;
            at++;

            if (brief)
            {
                if (at >= packet.Length) return Refuse(CheckReply);
                blocks.Add(packet[at++]);
            }
            else
            {
                if (at + 1 >= packet.Length) return Refuse(CheckReply);
                blocks.Add(packet[at] | (packet[at + 1] << 8));
                at += 2;
            }
        }

        var data = new byte[blocks.Count * BlockSize];
        var last = false;

        for (var i = 0; i < blocks.Count; i++)
        {
            if (!Block(blocks[i], out var block, out var reachedEnd)) return Refuse(CheckReply);
            block.CopyTo(data, i * BlockSize);
            last |= reachedEnd;
        }

        var reply = new byte[13 + data.Length];
        reply[0] = (byte)reply.Length;
        reply[1] = CheckReply;
        Id.CopyTo(reply, 2);
        reply[10] = Ok1;
        reply[11] = Ok2;
        reply[12] = (byte)blocks.Count;
        data.CopyTo(reply, 13);

        if (last) Read?.Invoke();

        return reply;
    }

    /// <summary>One block of the tag, attribute block included.</summary>
    private bool Block(int number, out byte[] block, out bool reachedEnd)
    {
        block = new byte[BlockSize];
        reachedEnd = false;

        if (number == 0)
        {
            Attributes().CopyTo(block, 0);
            reachedEnd = Offer is not { Length: > 0 };
            return true;
        }

        if (Offer is not { Length: > 0 } message) return false;

        var from = (number - FirstMessageBlock) * BlockSize;
        if (from >= message.Length) return false;

        // Short blocks are padded rather than truncated: the format has no notion of a partial block,
        // and a reader that asked for sixteen bytes will read sixteen whatever we send.
        var take = Math.Min(BlockSize, message.Length - from);
        Array.Copy(message, from, block, 0, take);

        reachedEnd = from + take >= message.Length;
        return true;
    }

    /// <summary>Say no, in the shape the reader expects a no to arrive in.</summary>
    private byte[] Refuse(byte command)
    {
        var reply = new byte[12];
        reply[0] = (byte)reply.Length;
        reply[1] = command;
        Id.CopyTo(reply, 2);
        reply[10] = Bad1;
        reply[11] = Bad2;
        return reply;
    }

    /// <summary>The phones came apart. The next tap starts again.</summary>
    public void Deactivated() { }
}
