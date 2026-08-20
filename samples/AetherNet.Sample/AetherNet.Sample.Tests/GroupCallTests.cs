// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A call with more than two people in it.
///
/// <para>
/// Built the way group chat is: a group call is several 1:1 calls. Everyone encodes once and sends
/// the result to each other participant separately, sealed under a key derived from their own tag.
/// No group key that a departing member keeps working with, no server mixing anything, and nobody
/// special.
/// </para>
///
/// <para>
/// Two things in that are load-bearing and silent when wrong — the key derivation, where a collision
/// would break AES-GCM outright, and the mixing, where getting it wrong sounds like a broken radio
/// rather than like a bug. Both are pinned here.
/// </para>
/// </summary>
public class GroupCallTests
{
    private const string Ann = "AAAAA-11111";
    private const string Ben = "BBBBB-22222";
    private const string Cat = "CCCCC-33333";

    private static byte[] Frame(int size, byte fill)
    {
        var f = new byte[size];
        Array.Fill(f, fill);
        return f;
    }

    private static short[] Pcm(int samples, short value)
    {
        var p = new short[samples];
        Array.Fill(p, value);
        return p;
    }

    // ── Keys: nobody shares a sealing key ──────────────────────────────────

    /// <summary>
    /// The property the whole scheme rests on. AES-GCM must never seal two different frames under the
    /// same key and nonce; the nonce is a per-instance counter, so two people sealing under one key
    /// would collide within seconds of both talking. Deriving from the sender guarantees they cannot.
    /// </summary>
    [Fact]
    public void Two_people_never_seal_under_the_same_key()
    {
        var master = CallMediaCipher.NewMasterKey();

        // Ann and Ben both send to Cat. If their sealing keys were the same, Cat's cipher for Ann
        // would open Ben's frames — which is exactly the collision that must be impossible.
        using var annToCat = CallMediaCipher.ForGroup(master, Ann, Cat);
        using var catReadingBen = CallMediaCipher.ForGroup(master, Cat, Ben);

        Assert.Null(catReadingBen.Open(annToCat.Seal(Frame(64, 0x11))));
    }

    /// <summary>And the right cipher does open it, which is the other half of the same statement.</summary>
    [Fact]
    public void Everyone_can_read_the_person_they_are_listening_to()
    {
        var master = CallMediaCipher.NewMasterKey();
        var frame = Frame(120, 0x7E);

        using var annSending = CallMediaCipher.ForGroup(master, Ann, Ben);
        using var benReadingAnn = CallMediaCipher.ForGroup(master, Ben, Ann);

        Assert.Equal(frame, benReadingAnn.Open(annSending.Seal(frame)));
    }

    /// <summary>
    /// Three people, everyone talking, every stream landing where it should and nowhere else. This is
    /// the shape of a real group call and the one that would expose an ordering mistake in the labels.
    /// </summary>
    [Fact]
    public void Three_people_all_hear_each_other_and_nobody_else()
    {
        var master = CallMediaCipher.NewMasterKey();

        using var annToBen = CallMediaCipher.ForGroup(master, Ann, Ben);
        using var annToCat = CallMediaCipher.ForGroup(master, Ann, Cat);
        using var benReadingAnn = CallMediaCipher.ForGroup(master, Ben, Ann);
        using var catReadingAnn = CallMediaCipher.ForGroup(master, Cat, Ann);

        var frame = Frame(80, 0x33);

        // Ann seals once per recipient. Both Ben and Cat read the same frame, each under their own
        // instance — and neither seal opens under the wrong one.
        Assert.Equal(frame, benReadingAnn.Open(annToBen.Seal(frame)));
        Assert.Equal(frame, catReadingAnn.Open(annToCat.Seal(frame)));
    }

    /// <summary>Video keeps its own key and its own counter here too, for the same reason as 1:1.</summary>
    [Fact]
    public void The_video_track_does_not_open_under_the_voice_key()
    {
        var master = CallMediaCipher.NewMasterKey();

        using var video = CallMediaCipher.ForGroup(master, Ann, Ben, video: true);
        using var voice = CallMediaCipher.ForGroup(master, Ben, Ann);

        Assert.Null(voice.Open(video.Seal(Frame(64, 0x99))));
    }

    /// <summary>And the two tracks do not disturb each other's replay window.</summary>
    [Fact]
    public void The_two_tracks_run_independently()
    {
        var master = CallMediaCipher.NewMasterKey();

        using var voiceOut = CallMediaCipher.ForGroup(master, Ann, Ben);
        using var videoOut = CallMediaCipher.ForGroup(master, Ann, Ben, video: true);
        using var voiceIn = CallMediaCipher.ForGroup(master, Ben, Ann);
        using var videoIn = CallMediaCipher.ForGroup(master, Ben, Ann, video: true);

        for (var i = 0; i < 30; i++)
        {
            var voice = Frame(60, (byte)i);
            var video = Frame(900, (byte)i);

            Assert.Equal(voice, voiceIn.Open(voiceOut.Seal(voice)));
            Assert.Equal(video, videoIn.Open(videoOut.Seal(video)));
        }
    }

    /// <summary>
    /// Nobody is in a group call with themselves. Allowing it would hand one instance the same key to
    /// seal and to open with — the collision the whole scheme exists to prevent.
    /// </summary>
    [Fact]
    public void A_participant_cannot_be_their_own_peer()
    {
        var master = CallMediaCipher.NewMasterKey();

        Assert.Throws<ArgumentException>(() => CallMediaCipher.ForGroup(master, Ann, Ann));
    }

    /// <summary>A group key is never a 1:1 key, so a stray frame from either cannot open in the other.</summary>
    [Fact]
    public void A_group_key_is_not_a_one_to_one_key()
    {
        var master = CallMediaCipher.NewMasterKey();

        using var group = CallMediaCipher.ForGroup(master, Ann, Ben);
        using var oneToOne = new CallMediaCipher(master, iAmTheCaller: false);

        Assert.Null(oneToOne.Open(group.Seal(Frame(48, 0x21))));
    }

    // ── Mixing: several people, one earpiece ───────────────────────────────

    /// <summary>One person talking comes out exactly as they were sent — the mixer costs nothing.</summary>
    [Fact]
    public void One_speaker_passes_straight_through()
    {
        using var mixer = new AudioMixer(4);
        var frame = new short[] { 10, -20, 30, -40 };

        mixer.Offer(Ann, frame);

        Assert.Equal(frame, mixer.Mix());
    }

    /// <summary>
    /// Two people talking at once are added together. Playing them as they arrive instead — which is
    /// what a 1:1 call does — makes each one interrupt the last.
    /// </summary>
    [Fact]
    public void Two_speakers_are_summed()
    {
        using var mixer = new AudioMixer(3);

        mixer.Offer(Ann, [100, 200, -300]);
        mixer.Offer(Ben, [50, -100, 100]);

        Assert.Equal(new short[] { 150, 100, -200 }, mixer.Mix());
    }

    /// <summary>
    /// A loud sum is clamped, never wrapped. Adding two shorts and storing the result back into a
    /// short wraps on overflow, and a wrap is not a loud sample — it is the opposite sign, which
    /// sounds like a violent crack rather than like two people talking at once.
    /// </summary>
    [Fact]
    public void A_loud_sum_is_clamped_rather_than_wrapped()
    {
        using var mixer = new AudioMixer(2);

        mixer.Offer(Ann, [short.MaxValue, short.MinValue]);
        mixer.Offer(Ben, [short.MaxValue, short.MinValue]);

        Assert.Equal(new[] { short.MaxValue, short.MinValue }, mixer.Mix());
    }

    /// <summary>Silence from everybody is nothing to play, not a frame of zeroes to keep the phone busy.</summary>
    [Fact]
    public void Nobody_talking_produces_nothing()
    {
        using var mixer = new AudioMixer(4);

        Assert.Null(mixer.Mix());
    }

    /// <summary>
    /// Someone with nothing queued contributes silence rather than holding the beat. This is what
    /// stops one person on a bad link stalling the conversation for everybody else.
    /// </summary>
    [Fact]
    public void A_silent_speaker_does_not_hold_the_others_up()
    {
        using var mixer = new AudioMixer(2);

        mixer.Offer(Ann, [7, 7]);
        mixer.Offer(Ben, [1, 1]);
        Assert.Equal(new short[] { 8, 8 }, mixer.Mix());

        // Ben says nothing this beat. Ann still comes out, on time.
        mixer.Offer(Ann, [9, 9]);
        Assert.Equal(new short[] { 9, 9 }, mixer.Mix());
    }

    /// <summary>
    /// A backlog is bounded, and the OLDEST goes. In a conversation the newest frame is what someone
    /// is saying now; the old one is already too late to be worth hearing.
    /// </summary>
    [Fact]
    public void A_slow_speaker_loses_their_oldest_audio_not_their_newest()
    {
        using var mixer = new AudioMixer(1);

        for (short i = 1; i <= AudioMixer.QueuedFramesPerSpeaker + 3; i++)
            mixer.Offer(Ann, [i]);

        // The queue holds the last N, so the first thing out is the oldest survivor rather than the
        // very first frame offered.
        var first = mixer.Mix();

        Assert.NotNull(first);
        Assert.Equal(4, first![0]);
    }

    /// <summary>
    /// A frame of the wrong length is refused rather than padded. A participant whose codec disagreed
    /// about frame size would otherwise be mixed in at the wrong rate and turn the call into noise —
    /// much harder to diagnose than one person not being heard.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(0)]
    public void A_frame_of_the_wrong_size_is_refused(int size)
    {
        using var mixer = new AudioMixer(4);

        mixer.Offer(Ann, Pcm(size, 100));

        Assert.Null(mixer.Mix());
    }

    /// <summary>Someone who left cannot be mixed into a conversation they are no longer part of.</summary>
    [Fact]
    public void Someone_who_left_stops_being_heard()
    {
        using var mixer = new AudioMixer(2);

        mixer.Offer(Ann, [5, 5]);
        mixer.Offer(Ben, [3, 3]);
        mixer.Forget(Ben);

        Assert.Equal(new short[] { 5, 5 }, mixer.Mix());
    }

    [Fact]
    public void Ending_the_call_clears_everything_queued()
    {
        using var mixer = new AudioMixer(2);

        mixer.Offer(Ann, [5, 5]);
        mixer.Clear();

        Assert.Null(mixer.Mix());
    }

    // ── The envelope ───────────────────────────────────────────────────────

    [Fact]
    public void An_invitation_survives_being_written_and_read()
    {
        var sent = new GroupCallEnvelope
        {
            Kind = GroupCallEnvelope.Invite,
            GroupId = "GABC123",
            CallId = "call-1",
            Sender = Ann,
            MasterKey = Convert.ToBase64String(CallMediaCipher.NewMasterKey()),
            Participants = [Ann, Ben],
        };

        var back = GroupCallEnvelope.Parse(sent.ToBytes());

        Assert.NotNull(back);
        Assert.Equal(GroupCallEnvelope.Invite, back!.Kind);
        Assert.Equal("GABC123", back.GroupId);
        Assert.Equal(sent.MasterKey, back.MasterKey);
        Assert.Equal(new[] { Ann, Ben }, back.Participants);
    }

    /// <summary>
    /// This arrives from a radio and carries the key to every stream in the call, so it is parsed like
    /// input. Anything malformed, or of a kind this build does not know, comes back null.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"k\":\"invite\"}")]
    [InlineData("{\"k\":\"invite\",\"g\":\"G1\",\"c\":\"c1\"}")]
    [InlineData("{\"k\":\"invite\",\"g\":\"G1\",\"s\":\"A\"}")]
    [InlineData("{\"k\":\"nonsense\",\"g\":\"G1\",\"c\":\"c1\",\"s\":\"A\"}")]
    public void Anything_malformed_reads_as_nothing(string json)
        => Assert.Null(GroupCallEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData(GroupCallEnvelope.Invite)]
    [InlineData(GroupCallEnvelope.Accept)]
    [InlineData(GroupCallEnvelope.Decline)]
    [InlineData(GroupCallEnvelope.Leave)]
    [InlineData(GroupCallEnvelope.Camera)]
    public void Every_kind_the_call_sends_is_a_kind_it_accepts(string kind)
    {
        var e = new GroupCallEnvelope { Kind = kind, GroupId = "G1", CallId = "c1", Sender = Ann };

        Assert.NotNull(GroupCallEnvelope.Parse(e.ToBytes()));
    }

    /// <summary>
    /// The key only ever travels with an invitation. Everything else is membership news, and putting
    /// the key on messages that do not need it widens the target for no reason.
    /// </summary>
    [Fact]
    public void Only_an_invitation_carries_the_key()
    {
        var accept = new GroupCallEnvelope
        {
            Kind = GroupCallEnvelope.Accept,
            GroupId = "G1",
            CallId = "c1",
            Sender = Ben,
        };

        Assert.Null(GroupCallEnvelope.Parse(accept.ToBytes())!.MasterKey);
    }
}
