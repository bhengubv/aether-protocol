// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The video track on a call.
///
/// <para>
/// A video call is not a second call — it is the call that already works, with a camera on it. That
/// keeps ringing, answering, the media key, the foreground service and hanging up exactly as they
/// are, and leaves only what is genuinely new: a second stream of frames, its own key, and a way to
/// say whether the camera is on.
/// </para>
///
/// <para>
/// Both of those are load-bearing in ways that are invisible until they are wrong, so they are pinned
/// here.
/// </para>
/// </summary>
public class VideoCallTests
{
    private static byte[] Frame(int size, byte fill)
    {
        var f = new byte[size];
        Array.Fill(f, fill);
        return f;
    }

    // ── Two tracks, two keys ───────────────────────────────────────────────

    /// <summary>
    /// Audio and video share a call and a master secret. They must not share a cipher.
    ///
    /// <para>
    /// The nonce is a per-cipher counter, so two tracks sealing through one instance would be safe on
    /// the nonce and broken on the replay window: the receiver keeps the highest counter it has seen,
    /// and two interleaved streams each look like the other replaying old frames. Half of every track
    /// would be discarded, silently, as an attack.
    /// </para>
    /// </summary>
    [Fact]
    public void The_video_track_does_not_open_under_the_audio_key()
    {
        var master = CallMediaCipher.NewMasterKey();

        using var videoOut = new CallMediaCipher(master, iAmTheCaller: true, video: true);
        using var audioIn = new CallMediaCipher(master, iAmTheCaller: false);

        var sealed_ = videoOut.Seal(Frame(64, 0xAB));

        Assert.Null(audioIn.Open(sealed_));
    }

    [Fact]
    public void And_the_audio_track_does_not_open_under_the_video_key()
    {
        var master = CallMediaCipher.NewMasterKey();

        using var audioOut = new CallMediaCipher(master, iAmTheCaller: true);
        using var videoIn = new CallMediaCipher(master, iAmTheCaller: false, video: true);

        Assert.Null(videoIn.Open(audioOut.Seal(Frame(64, 0xCD))));
    }

    /// <summary>Each track does open under its own, which is the other half of the same statement.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_track_opens_under_its_own_key(bool video)
    {
        var master = CallMediaCipher.NewMasterKey();
        var frame = Frame(200, 0x5A);

        using var sender = new CallMediaCipher(master, iAmTheCaller: true, video: video);
        using var receiver = new CallMediaCipher(master, iAmTheCaller: false, video: video);

        Assert.Equal(frame, receiver.Open(sender.Seal(frame)));
    }

    /// <summary>
    /// And the two tracks keep separate counters, which is the point of separating them. Interleaving
    /// them through one cipher is what would look like a replay attack; here neither notices the
    /// other at all.
    /// </summary>
    [Fact]
    public void The_two_tracks_do_not_disturb_each_other()
    {
        var master = CallMediaCipher.NewMasterKey();

        using var audioOut = new CallMediaCipher(master, iAmTheCaller: true);
        using var videoOut = new CallMediaCipher(master, iAmTheCaller: true, video: true);
        using var audioIn = new CallMediaCipher(master, iAmTheCaller: false);
        using var videoIn = new CallMediaCipher(master, iAmTheCaller: false, video: true);

        // Video runs far ahead of audio, exactly as it does in a real call: bigger frames, more of
        // them, and both sides sealing at their own pace.
        for (var i = 0; i < 50; i++)
        {
            var audio = Frame(60, (byte)i);
            var video = Frame(900, (byte)i);

            Assert.Equal(audio, audioIn.Open(audioOut.Seal(audio)));

            for (var j = 0; j < 3; j++)
                Assert.Equal(video, videoIn.Open(videoOut.Seal(video)));
        }
    }

    /// <summary>
    /// A direction still gets its own key within a track — the original rule, checked again for
    /// video, because reusing one key both ways is the one thing AES-GCM must never do.
    /// </summary>
    [Fact]
    public void A_direction_still_gets_its_own_key_on_the_video_track()
    {
        var master = CallMediaCipher.NewMasterKey();

        using var caller = new CallMediaCipher(master, iAmTheCaller: true, video: true);
        using var alsoCaller = new CallMediaCipher(master, iAmTheCaller: true, video: true);

        // Two phones that both think they are the caller cannot read each other. That is the failure
        // the role-derived direction exists to prevent, and it must hold on this track too.
        Assert.Null(alsoCaller.Open(caller.Seal(Frame(32, 0x11))));
    }

    // ── The bar for offering a camera ──────────────────────────────────────

    /// <summary>
    /// Video is roughly 800 kbps against voice's 24, on a radio that still has to carry the voice.
    /// The floor is well above the voice floor, and deliberately so — this is the number that decides
    /// whether a camera button appears at all.
    /// </summary>
    [Fact]
    public void Video_needs_far_more_of_the_link_than_voice_does()
    {
        Assert.True(CallService.MinLinkBpsForVideo > OpusVoiceCodec.MinLinkBpsForCall * 10);
        Assert.True(CallService.MinLinkBpsForVideo >= 3_000_000);
    }

    /// <summary>
    /// Wi-Fi Direct clears it; nothing else measured on these phones comes close. That is the whole
    /// finding restated as a number the code can act on — see PROTOCOL_SPEC §5.5.
    /// </summary>
    [Theory]
    [InlineData(250_000_000, true)]   // Wi-Fi Direct, as declared
    [InlineData(11_000, false)]       // BLE, measured 2026-08-20
    [InlineData(1_000_000, false)]    // a megabit is not enough for video plus its voice
    [InlineData(0, false)]            // a radio that will not say does not get the benefit here
    public void Only_a_wide_radio_clears_the_video_bar(long linkBps, bool expected)
        => Assert.Equal(expected, linkBps >= CallService.MinLinkBpsForVideo);

    /// <summary>
    /// Note the asymmetry with voice, which is deliberate. An unknown link is given the benefit of
    /// the doubt for a call — refusing on no evidence is worse than trying. Video is not: it is
    /// expensive enough that guessing wrong takes the working voice call down with it.
    /// </summary>
    [Fact]
    public void An_unknown_link_may_carry_voice_but_not_video()
    {
        Assert.True(OpusVoiceCodec.CanCarryCall(0));
        Assert.False(0 >= CallService.MinLinkBpsForVideo);
    }

    // ── What a group costs ─────────────────────────────────────────────────

    /// <summary>
    /// The gate the call applies must agree with the budget it is derived from. Two different numbers
    /// for the same thing is how a spec and an implementation quietly stop describing each other.
    /// </summary>
    [Fact]
    public void The_video_gate_matches_the_budget_for_two_people()
        => Assert.True(CallService.MinLinkBpsForVideo >= VideoBudget.RequiredBps(2));

    /// <summary>
    /// There is no server to fan out through, so every phone sends to every other one. Both
    /// directions grow with the group, and a call that is comfortable at two is several times as
    /// expensive at five. These figures are the table in PROTOCOL_SPEC §10.10.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public void A_group_costs_a_stream_for_every_other_person(int participants)
    {
        var expected = ((VideoBudget.StreamBps * (participants - 1)) + VideoBudget.VoiceBps) * 3;

        Assert.Equal(expected, VideoBudget.RequiredBps(participants));
    }

    /// <summary>A group grows what it costs, always — never less for more people.</summary>
    [Fact]
    public void A_bigger_group_never_costs_less()
    {
        var costs = Enumerable.Range(2, 10).Select(VideoBudget.RequiredBps).ToArray();

        Assert.Equal(costs.OrderBy(c => c), costs);
    }

    /// <summary>Fewer than two people is not a call, and costs nothing.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    public void Nobody_to_call_costs_nothing(int participants)
        => Assert.Equal(0, VideoBudget.RequiredBps(participants));

    /// <summary>
    /// The finding that matters most about group video: the mesh is not what stops it. Wi-Fi Direct
    /// has bandwidth for a large group on paper, and the phones do not have the decoders for one.
    /// </summary>
    [Fact]
    public void Silicon_binds_before_the_radio_does()
    {
        const long wifiDirect = 250_000_000;

        var byRadio = VideoBudget.LargestGroupTheLinkCouldCarry(wifiDirect);
        var actual = VideoBudget.LargestGroup(wifiDirect);

        Assert.True(byRadio > VideoBudget.AssumedDecoderCap,
            "the radio should not be the binding constraint on Wi-Fi Direct");
        Assert.Equal(VideoBudget.AssumedDecoderCap, actual);
    }

    /// <summary>And on a narrow radio the radio does bind, which is the other half of that.</summary>
    [Theory]
    [InlineData(11_000)]       // BLE, measured
    [InlineData(1_000_000)]    // not even two people
    public void A_narrow_radio_carries_no_group_video_at_all(long linkBps)
        => Assert.Equal(0, VideoBudget.LargestGroup(linkBps));

    /// <summary>
    /// A phone that reports its own decoder count is believed over the assumed one. The assumption is
    /// a starting point for hardware nobody has asked yet, never an answer to prefer.
    /// </summary>
    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(8, 8)]
    [InlineData(0, 0)]
    public void A_measured_decoder_count_wins_over_the_assumed_one(int cap, int expected)
        => Assert.Equal(expected, VideoBudget.LargestGroup(250_000_000, decoderCap: cap));
}
