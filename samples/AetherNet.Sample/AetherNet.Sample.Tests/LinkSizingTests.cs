// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Sizing media to a link that is measured rather than believed.
///
/// <para>
/// Every bandwidth figure this app trusted turned out to be arithmetic: BLE published 2 Mbps and
/// delivered 11 kbps one way, a voice note was described from the bitrate the encoder was asked for
/// and measured ten times that, and Wi-Fi Direct still reports a flat 250 Mbps nothing has checked.
/// These cover the replacement — what actually crossed, and how hard the link worked to carry it.
/// </para>
/// </summary>
public class LinkSizingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static LinkQuality Sending(int frames, int bytes, double ms, bool sent = true)
    {
        var q = new LinkQuality();
        for (var i = 0; i < frames; i++)
            q.Record(bytes, TimeSpan.FromMilliseconds(ms), sent, T0.AddMilliseconds(i * 20));
        return q;
    }

    // ── saying nothing until there is something to say ─────────────────────

    /// <summary>
    /// A fresh link has carried nothing, so it knows nothing. Reporting a number here is how a guess
    /// becomes a fact — the exact move that produced every wrong figure this replaces.
    /// </summary>
    [Fact]
    public void A_link_that_has_carried_nothing_claims_nothing()
    {
        var quiet = new LinkQuality();

        Assert.False(quiet.HasEnough(T0));
        Assert.Equal(0, quiet.ThroughputBps(T0));
        Assert.Equal(0, quiet.Strain(T0));
    }

    [Fact]
    public void One_or_two_sends_are_a_coincidence_not_a_measurement()
    {
        var barely = Sending(frames: 3, bytes: 1000, ms: 5);

        Assert.False(barely.HasEnough(T0.AddMilliseconds(60)));
        Assert.Equal(0, barely.ThroughputBps(T0.AddMilliseconds(60)));
    }

    // ── throughput is a floor, never a capacity ────────────────────────────

    /// <summary>
    /// Twenty frames of a thousand bytes in four hundred milliseconds is real traffic and produces a
    /// real number. It says what crossed — not what could have.
    /// </summary>
    [Fact]
    public void What_crossed_is_reported_once_there_is_enough_of_it()
    {
        var busy = Sending(frames: 20, bytes: 1000, ms: 5);

        var bps = busy.ThroughputBps(T0.AddMilliseconds(400));

        Assert.True(bps > 0, "traffic crossed and nothing was reported");
        Assert.InRange(bps, 300_000, 500_000);   // 20KB over ~0.4s ≈ 400kbps
    }

    // ── strain: the honest signal ──────────────────────────────────────────

    /// <summary>A link taking a few milliseconds per send is not working hard.</summary>
    [Fact]
    public void A_comfortable_link_reports_no_strain()
    {
        var easy = Sending(frames: 20, bytes: 1000, ms: 4);

        Assert.Equal(0, easy.Strain(T0.AddMilliseconds(400)), 1);
    }

    /// <summary>
    /// Sends taking far longer than a frame interval mean the next frame is queueing behind this one.
    /// That is congestion, and it shows up before anything is lost.
    /// </summary>
    [Fact]
    public void Slow_sends_show_as_strain_before_anything_is_lost()
    {
        var struggling = Sending(frames: 20, bytes: 1000, ms: 120);

        Assert.True(struggling.Strain(T0.AddMilliseconds(400)) > 0.5,
            "sends taking 120ms each should read as a link in trouble");
    }

    /// <summary>A refusal is the loudest thing a link can say. Everything refused is not "slow".</summary>
    [Fact]
    public void Refusals_read_as_a_link_that_is_gone()
    {
        var refusing = Sending(frames: 20, bytes: 1000, ms: 1, sent: false);

        Assert.True(refusing.Strain(T0.AddMilliseconds(400)) > 0.9);
        Assert.Equal(0, refusing.ThroughputBps(T0.AddMilliseconds(400)));   // nothing actually crossed
    }

    /// <summary>The window moves. A link that was bad a minute ago is not bad now.</summary>
    [Fact]
    public void Trouble_ages_out_of_the_window()
    {
        var was = Sending(frames: 20, bytes: 1000, ms: 200);

        Assert.Equal(0, was.Strain(T0.AddMinutes(1)));
    }

    // ── what to do about it ────────────────────────────────────────────────

    /// <summary>Down fast: a link in trouble needs room now, not after the call has broken up.</summary>
    [Fact]
    public void Video_halves_when_the_link_is_failing()
    {
        var next = MediaBitrate.Video(current: 800_000, strain: 0.8);

        Assert.Equal(400_000, next);
    }

    /// <summary>
    /// Up slow: finding the limit repeatedly is a stutter every time.
    /// </summary>
    /// <remarks>
    /// Expressed against the ceiling rather than a number. It was written as "from 400,000, expect
    /// more than 400,000" — true while the ceiling was 800 kbps for a 720p picture, and false the
    /// moment the ceiling came down to 400 kbps to match the 640x360 the browser actually captures.
    /// A test that pins today's constant fails on a deliberate change and says nothing about the
    /// behaviour it was meant to protect.
    /// </remarks>
    [Fact]
    public void Video_creeps_back_up_when_the_link_is_comfortable()
    {
        var from = MediaBitrate.VideoCeilingBps / 2;

        var next = MediaBitrate.Video(current: from, strain: 0.0);

        Assert.True(next > from, "a comfortable link should be asked for a little more");
        Assert.True(next <= MediaBitrate.VideoCeilingBps, "and never more than the picture can use");
    }

    /// <summary>
    /// And the ceiling is the one the picture is actually sent at.
    /// </summary>
    /// <remarks>
    /// These drifted apart once already: the encoder captured 640x360 while the ceiling was still the
    /// 800 kbps chosen for 1280x720, so adaptation climbed to twice what the picture needed on every
    /// healthy link. 400 kbps is the WebRTC reference figure for H.264 webcam at that size.
    /// </remarks>
    [Fact]
    public void The_ceiling_matches_the_picture_that_is_sent()
    {
        Assert.Equal(400_000, MediaBitrate.VideoCeilingBps);
        Assert.True(MediaBitrate.VideoCeilingBps > MediaBitrate.VideoFloorBps * 2,
            "there has to be room to adapt between the floor and the ceiling");
    }

    [Fact]
    public void Video_never_exceeds_what_the_picture_can_use()
    {
        var next = MediaBitrate.Video(current: MediaBitrate.VideoCeilingBps, strain: 0.0);

        Assert.Equal(MediaBitrate.VideoCeilingBps, next);
    }

    /// <summary>
    /// Below the floor a picture is unwatchable AND still crowding out the voice. Stopping is the
    /// better answer than limping, because a call with sound is a call and a call without is not.
    /// </summary>
    [Fact]
    public void Video_gives_up_rather_than_crowd_out_the_voice()
    {
        var next = MediaBitrate.Video(current: MediaBitrate.VideoFloorBps, strain: 1.0);

        Assert.Equal(0, next);
    }

    /// <summary>Each extra camera takes a share, so the ceiling per stream comes down with the count.</summary>
    [Fact]
    public void More_cameras_get_a_smaller_share_each()
    {
        var pair = MediaBitrate.Video(current: 0, strain: 0.0, people: 2);
        var crowd = MediaBitrate.Video(current: 0, strain: 0.0, people: 5);

        Assert.True(crowd < pair, "five cameras must not each get what two would");
    }

    // ── voice is protected ─────────────────────────────────────────────────

    /// <summary>
    /// The strain that halves video leaves voice alone. Video is cut first precisely so the voice
    /// does not have to be.
    /// </summary>
    [Fact]
    public void Voice_holds_where_video_is_already_halving()
    {
        Assert.Equal(400_000, MediaBitrate.Video(800_000, strain: 0.8));
        Assert.Equal(MediaBitrate.VoiceCeilingBps, MediaBitrate.Voice(MediaBitrate.VoiceCeilingBps, strain: 0.6));
    }

    [Fact]
    public void Voice_gives_ground_only_when_there_is_nothing_left_to_give()
    {
        var next = MediaBitrate.Voice(MediaBitrate.VoiceCeilingBps, strain: 0.9);

        Assert.True(next < MediaBitrate.VoiceCeilingBps);
        Assert.True(next >= MediaBitrate.VoiceFloorBps, "voice must stay intelligible rather than stop");
    }

    /// <summary>
    /// The floor is a floor, not a resting place. However bad the link gets voice stays intelligible,
    /// and when the link recovers the voice recovers with it.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Voice_never_drops_below_intelligible(double strain)
    {
        var next = MediaBitrate.Voice(MediaBitrate.VoiceFloorBps, strain);

        Assert.InRange(next, MediaBitrate.VoiceFloorBps, MediaBitrate.VoiceCeilingBps);
    }

    /// <summary>Voice is always given up after the picture, never before it.</summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(0.6)]
    [InlineData(0.7)]
    public void Video_is_always_sacrificed_before_voice(double strain)
    {
        var video = MediaBitrate.Video(MediaBitrate.VideoCeilingBps, strain);
        var voice = MediaBitrate.Voice(MediaBitrate.VoiceCeilingBps, strain);

        Assert.True(video < MediaBitrate.VideoCeilingBps, "the picture should already be giving way");
        Assert.Equal(MediaBitrate.VoiceCeilingBps, voice);
    }

    // ── the gate ───────────────────────────────────────────────────────────

    /// <summary>
    /// A link already working hard on voice alone does not get a camera as well — adding one would
    /// take the call down with it.
    /// </summary>
    [Fact]
    public void A_struggling_link_does_not_get_video()
    {
        Assert.False(MediaBitrate.WorthVideo(strain: 0.5));
        Assert.False(MediaBitrate.WorthVideo(strain: 1.0));
    }

    /// <summary>
    /// A comfortable link gets the benefit of the doubt, because the alternative is never finding out.
    ///
    /// <para>
    /// This replaces a test that asked whether the link had already carried enough. That question is
    /// unanswerable in the only case that matters: during a voice call a link carries about 24 kbps
    /// because that is all anyone offered it, so "has it carried 120 kbps" is no, forever, and video
    /// could never start. Measured on device before this changed — a link that had moved seven
    /// thousand voice frames without a single refusal was told it could not manage a camera.
    /// </para>
    /// </summary>
    [Fact]
    public void A_comfortable_link_gets_the_benefit_of_the_doubt()
    {
        Assert.True(MediaBitrate.WorthVideo(strain: 0.0));
        Assert.True(MediaBitrate.WorthVideo(strain: 0.1));
    }

    /// <summary>
    /// More cameras is more to go wrong, so a crowded call needs a calmer link before it will add one.
    /// </summary>
    [Fact]
    public void A_link_good_for_two_may_not_be_good_for_five()
    {
        Assert.True(MediaBitrate.WorthVideo(strain: 0.2, people: 2));
        Assert.False(MediaBitrate.WorthVideo(strain: 0.2, people: 5));
    }
}
