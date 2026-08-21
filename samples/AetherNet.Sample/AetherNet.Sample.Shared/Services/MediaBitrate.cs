// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// How much media to put on a link, decided from what the link is doing rather than what it claims.
///
/// <para>
/// The rule everywhere else in this app has been to pick a number, write it in a constant, and hope.
/// Video has been a flat 800 kbps since it was written — the same 800 kbps whether it is crossing a
/// five-gigahertz group with the channel to itself or a two-point-four gigahertz one time-slicing
/// against the phone's own access point. It never asked, and it never changed.
/// </para>
///
/// <para>
/// Voice and video are treated separately because they fail in opposite ways. Voice degrades: give
/// Opus less and it sounds worse but keeps working, all the way down to something intelligible at
/// 8 kbps. Video does not degrade, it stalls — an encoder given less than it needs produces frames
/// too late to show, and the decoder waits on a keyframe that has not arrived while the audio it was
/// sharing a link with breaks up too. So video is cut early and hard, and voice is protected.
/// </para>
/// </summary>
public static class MediaBitrate
{
    /// <summary>The most video worth sending on this app's picture size — more buys nothing visible.</summary>
    public const int VideoCeilingBps = 800_000;

    /// <summary>
    /// Below this, video is worse than nothing: too blocky to read a face and still enough traffic to
    /// hurt the voice sharing the link. Under this, turn the camera off rather than limp.
    /// </summary>
    public const int VideoFloorBps = 120_000;

    /// <summary>Opus at its best for speech. Above this is spent on things a voice does not contain.</summary>
    public const int VoiceCeilingBps = 24_000;

    /// <summary>Opus still carries an intelligible voice here. Below it, stop pretending.</summary>
    public const int VoiceFloorBps = 8_000;

    /// <summary>
    /// What the video encoder should be asked for next, given how the link is behaving now.
    /// </summary>
    /// <param name="current">What it is producing at the moment.</param>
    /// <param name="strain">From <see cref="LinkQuality.Strain"/> — 0 comfortable, 1 failing.</param>
    /// <param name="people">How many cameras share the link. Two people is two streams, not one.</param>
    /// <returns>The bitrate to move to, or 0 when video should stop entirely.</returns>
    /// <remarks>
    /// Down fast, up slow — deliberately. Going down late means the call has already broken up, and
    /// nobody thanks you for the frames you sent while it did. Going up quickly means finding the
    /// limit again and again, which is a stutter every time. Halving on strain and creeping up by an
    /// eighth is the same asymmetry every congestion control worth the name settles on.
    /// </remarks>
    public static int Video(int current, double strain, int people = 2)
    {
        var share = Math.Max(people - 1, 1);
        var ceiling = Math.Max(VideoCeilingBps / share, VideoFloorBps);

        if (current <= 0) current = ceiling;

        var next = strain switch
        {
            >= 0.6 => current / 2,                       // it is failing — get out of the way now
            >= 0.3 => (int)(current * 0.75),             // it is working too hard — ease off
            <= 0.1 => (int)(current * 1.125),            // comfortable — take a little more
            _ => current,                                // somewhere in between; leave it alone
        };

        next = Math.Clamp(next, VideoFloorBps / 2, ceiling);

        // Below the floor there is no picture worth the bytes. Say so with 0 rather than sending
        // something unwatchable that still crowds out the voice.
        return next < VideoFloorBps ? 0 : next;
    }

    /// <summary>
    /// What the voice encoder should be asked for. Protected as far as it can be, because a call with
    /// bad sound is a bad call and a call with no sound is not one.
    /// </summary>
    public static int Voice(int current, double strain)
    {
        if (current <= 0) current = VoiceCeilingBps;

        // Every threshold here sits ABOVE the one that cuts video, so the picture is always given up
        // first. Reducing voice at 0.5 while video only halves at 0.6 had it exactly backwards — the
        // half of a call people actually need was being sacrificed to protect the half they can do
        // without.
        var next = strain switch
        {
            >= 0.9 => current / 2,             // video is long gone; this is the last thing to give
            >= 0.75 => (int)(current * 0.8),   // past where video stopped entirely
            <= 0.15 => (int)(current * 1.25),  // room again — take the quality back
            _ => current,
        };

        return Math.Clamp(next, VoiceFloorBps, VoiceCeilingBps);
    }

    /// <summary>
    /// Whether this link is worth putting video on.
    /// </summary>
    /// <param name="strain">From <see cref="LinkQuality.Strain"/> — how hard the link is working now.</param>
    /// <param name="people">How many cameras would share it.</param>
    /// <remarks>
    /// <para>
    /// Asks whether the link is <em>struggling</em>, not how much it has carried. Throughput is a
    /// floor — what actually crossed — and using it as a capacity test is self-fulfilling: during a
    /// voice call the link carries about 24 kbps because that is all anyone offered it, so a test for
    /// "has it carried 120 kbps" answers no, forever, and video can never start. Measured on device:
    /// "Wi-Fi Direct is not carrying enough for video with 2 — voice only", on a link that had just
    /// moved seven thousand voice frames without refusing one.
    /// </para>
    /// <para>
    /// A comfortable link gets the benefit of the doubt, and <see cref="Video"/> takes it straight back
    /// if the picture turns out to be too much — down to stopping the camera. Finding out by trying is
    /// the only honest way to learn a capacity nobody will tell you.
    /// </para>
    /// </remarks>
    public static bool WorthVideo(double strain, int people = 2)
    {
        // Already working hard with voice alone: adding a camera would take the call down with it.
        var ceiling = people >= 4 ? 0.15 : 0.3;
        return strain < ceiling;
    }
}
