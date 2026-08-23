// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// The bits every stream-oriented radio needs: how a frame is written, how a socket is tuned, and
/// how one peer's outbound traffic is kept in lanes.
///
/// <para>
/// This is lifted from <see cref="AndroidWifiDirectTransportService"/>, where all of it was learned
/// the hard way over several days of two phones failing in ways that looked like a flaky radio and
/// were not. Every rule below cost something to find, so a second transport re-deriving them was
/// never going to end anywhere good — it would have ended in the same three bugs, found again, in a
/// different file.
/// </para>
///
/// <para>
/// Wi-Fi Direct still carries its own copy. Converging it onto this is a change to a radio that
/// currently works, and that belongs after the LAN leg has been proven on both handsets — not in the
/// same commit.
/// </para>
/// </summary>
internal static class Framing
{
    /// <summary>The largest frame that will be read before the connection is treated as garbage.</summary>
    /// <remarks>
    /// Matches Wi-Fi Direct. A length is the first thing read off a socket and the last thing that
    /// should ever be trusted: without a ceiling, four bytes from a mis-framed or hostile sender
    /// allocate whatever they say.
    /// </remarks>
    public const int MaxFrame = 64 * 1024 * 1024;

    /// <summary>Write one frame: [4-byte little-endian length][payload].</summary>
    public static async Task WriteFrameAsync(NetworkStream s, byte[] payload)
    {
        var header = new byte[4];
        BitConverter.TryWriteBytes(header, payload.Length); // little-endian on all supported platforms
        await s.WriteAsync(header).ConfigureAwait(false);
        await s.WriteAsync(payload).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Read one frame, or null when the peer has gone or is talking nonsense.</summary>
    public static async Task<byte[]?> ReadFrameAsync(NetworkStream s)
    {
        var header = await ReadExactAsync(s, 4).ConfigureAwait(false);
        if (header is null) return null;
        var len = BitConverter.ToInt32(header, 0);
        if (len <= 0 || len > MaxFrame) return null;
        return await ReadExactAsync(s, len).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream s, int count)
    {
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(off, count - off)).ConfigureAwait(false);
            if (n <= 0) return null;
            off += n;
        }
        return buf;
    }

    /// <summary>
    /// Stop the kernel hiding congestion from us.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A TCP write returns as soon as the kernel has taken a copy, not when the radio has sent
    /// anything. With a default send buffer of a megabyte or more and video frames of a few
    /// kilobytes, a hundred frames can be sitting in the kernel — several seconds of video — while
    /// every write looks instant and the link reports no strain at all. The lane bound is then
    /// meaningless: it keeps six frames and the kernel keeps a hundred behind it.
    /// </para>
    /// <para>
    /// 16KB is three or four video frames, about a quarter of a second. Small enough that a write
    /// blocks while the radio is genuinely behind — which is what turns congestion into a
    /// measurement instead of a delay — and small enough that the delay it imposes is not worth
    /// naming. NoDelay stops Nagle adding a wait of its own to frames that are already whole.
    /// </para>
    /// </remarks>
    public static void Tighten(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            client.SendBufferSize = 16 * 1024;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Info("AetherLAN", "could not tighten the socket: " + ex.Message);
        }
    }
}

/// <summary>
/// One peer's socket and the queues feeding it.
///
/// <para>
/// A frame is a length followed by a payload, written with three separate awaits. Two sends running
/// at once interleave those writes, and the far side then reads a length out of the middle of
/// somebody else's payload — so it closes the connection, and this side sees "Broken pipe" on a
/// socket that was perfectly healthy a moment ago. Attachments send their chunks concurrently, so
/// the moment anything larger than a text message crossed, the link tore itself down and rebuilt.
/// It looked like a flaky radio for days. Hence one pump per link, and never a bare write.
/// </para>
/// </summary>
internal sealed record MeshLink(TcpClient Client, NetworkStream Stream)
{
    /// <summary>
    /// One queue per lane, so speech never waits behind a file.
    /// </summary>
    /// <remarks>
    /// A single queue meant a 36KB attachment chunk held the wire for about a third of a second while
    /// every voice frame offered during it sat behind it. The call broke up for as long as anything
    /// was transferring, and no bitrate was low enough to help — the problem was the order, not the
    /// volume.
    /// </remarks>
    public ConcurrentQueue<byte[]>[] Lanes { get; } = [new(), new(), new(), new()];

    /// <summary>Wakes the pump when something is queued.</summary>
    public SemaphoreSlim Ready { get; } = new(0);

    /// <summary>
    /// How much may wait in the bulk lane before the oldest is dropped. Attachments resume, so a
    /// dropped chunk is asked for again; an unbounded queue on a slow link is a phone running out of
    /// memory instead.
    /// </summary>
    public const int BulkDepth = 64;

    /// <summary>
    /// How many video frames may wait before the lane is cleared.
    /// </summary>
    /// <remarks>
    /// Six, which at fifteen frames a second is under half a second of slack. Enough to ride out a
    /// brief stall in the radio; far too little to accumulate the delay that made a call feel like a
    /// recording.
    /// </remarks>
    public const int VideoDepth = 6;

    /// <summary>
    /// Queue one frame in its lane, dropping whatever the lane's rules say may be dropped.
    /// </summary>
    /// <returns>
    /// How many frames were thrown away to make room — 0 normally, and worth logging when it is not.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Video drops in WHOLE GOPs, never one frame at a time.</b> H.264 without temporal layering
    /// is a chain: every P-frame is decoded against the one before it. Discarding the oldest frame
    /// and keeping the rest hands the far side a chain with a link missing, and its decoder then
    /// accepts every subsequent frame and produces NOTHING from them — silently, with no error to
    /// react to, until the next keyframe.
    /// </para>
    /// <para>
    /// Measured over a three-minute call: frames arriving fell 11.6/s to 4/s while frames drawn fell
    /// 10.8/s to ZERO, with decodeErrors 0 throughout. The receiver was being fed continuously and
    /// could not use any of it. Clearing the lane instead costs at most one keyframe interval and the
    /// far side comes back cleanly rather than staying broken.
    /// </para>
    /// </remarks>
    public int Enqueue(byte[] frame, SendLane lane)
    {
        var queue = Lanes[(int)lane];
        queue.Enqueue(frame);

        var dropped = 0;
        if (PacketPriority.MayDropOldest(lane))
        {
            if (lane == SendLane.Video)
            {
                if (queue.Count > VideoDepth)
                    while (queue.TryDequeue(out _)) dropped++;
            }
            else
            {
                while (queue.Count > BulkDepth && queue.TryDequeue(out _)) dropped++;
            }
        }

        Ready.Release();
        return dropped;
    }

    /// <summary>
    /// Take the next frame owed, strictly in priority order, or null when every lane is empty.
    /// </summary>
    /// <remarks>
    /// Deliberately not fair. A file has nobody waiting on any particular chunk and resumes if it is
    /// interrupted; speech has a deadline measured in tens of milliseconds and is worthless past it.
    /// Starving bulk while a call is in progress is the correct outcome, not a bug — the transfer
    /// carries on the moment the call ends.
    /// </remarks>
    public byte[]? NextFrame()
    {
        for (var lane = 0; lane < PacketPriority.Lanes; lane++)
            if (Lanes[lane].TryDequeue(out var frame))
                return frame;
        return null;
    }
}
#endif
