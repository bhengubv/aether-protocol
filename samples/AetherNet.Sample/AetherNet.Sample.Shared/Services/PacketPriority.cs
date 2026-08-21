// SPDX-License-Identifier: MIT

using AetherNet.Protocol;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Which lane a packet travels in.
///
/// <para>
/// Everything used to go down one queue in the order it was offered, and a link is not fast enough for
/// that to be harmless. Measured on these phones: an attachment chunk is 36,778 bytes and a voice frame
/// is 149 to 391. One chunk holds the wire for about a third of a second, and every voice frame offered
/// during it waits — so a call breaks up for as long as anything is transferring, however much
/// bandwidth there is. No bitrate is low enough to fix a single-lane queue.
/// </para>
/// </summary>
public enum SendLane
{
    /// <summary>
    /// Late is the same as lost. A voice frame that arrives after its moment has passed is discarded by
    /// the far side anyway, so there is nothing to gain by making it wait behind anything.
    /// </summary>
    RealTime = 0,

    /// <summary>
    /// Somebody is waiting for it — a message, a receipt, a call being set up. Late is annoying rather
    /// than useless, so it yields to speech but not to a file.
    /// </summary>
    Interactive = 1,

    /// <summary>
    /// Nobody is watching the clock. Attachments and app packages get whatever is left, and they resume
    /// if the link drops, so being pushed aside costs them nothing.
    /// </summary>
    Bulk = 2,
}

/// <summary>
/// Sorting packets into lanes by what they are.
///
/// <para>
/// This is only possible because packets are typed. The app used to send everything as
/// <see cref="PacketType.Data"/> with a nine-byte string marker inside the encrypted payload, which
/// meant nothing outside the two endpoints could tell a phone call from a file transfer — not this
/// send path, not a relay, not another implementation of the protocol. Typing them is what makes the
/// lanes possible at all.
/// </para>
/// </summary>
public static class PacketPriority
{
    /// <summary>Which lane this kind of packet belongs in.</summary>
    public static SendLane Lane(PacketType type) => type switch
    {
        // Speech and pictures, in flight. These have a deadline measured in tens of milliseconds.
        PacketType.VoiceCall or PacketType.VoicePtt or PacketType.VideoFrame
            or PacketType.StreamSegment or PacketType.ScreenShare => SendLane.RealTime,

        // Setting a call up is worth as much as the call: a key or an answer arriving late is a call
        // that never starts.
        PacketType.VoiceSignaling or PacketType.VideoSignaling or PacketType.VideoCall
            or PacketType.GroupVideoSignaling => SendLane.RealTime,

        // Bulk. Content-addressed, chunked and resumable — designed to be interrupted.
        PacketType.ChunkData or PacketType.ChunkRequest or PacketType.ChunkBitmap
            or PacketType.StreamAnnounce or PacketType.TorrentMetadata => SendLane.Bulk,

        // Everything a person is actually waiting on.
        _ => SendLane.Interactive,
    };

    /// <summary>How many lanes there are — for anything that needs one queue per lane.</summary>
    public const int Lanes = 3;
}
