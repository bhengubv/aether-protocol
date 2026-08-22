// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using AetherNet.Voice.Models;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The order things happen in a video call, between two phones.
///
/// <para>
/// Every fault in this area was found by holding two handsets, and almost none were in a single line —
/// they were in sequence, and between two ends. A camera announced before it was running. A frame
/// handler attached a second time so every frame went out twice. A flag set from a packet the phone
/// could not act on, which hid the whole app behind a picture that was never coming. A camera that
/// gave up with nobody told, leaving the other end on a frozen face for the rest of the call.
/// </para>
///
/// <para>
/// None of that needs a camera to catch. It needs two services wired to each other through the real
/// packet path, and a device that can be told to fail and asked afterwards what it was asked to do.
/// The bugs below are all real, all shipped, and all found on hardware first — which is the point.
/// </para>
/// </summary>
public class VideoCallFlowTests
{
    /// <summary>One phone.</summary>
    private sealed class Phone
    {
        public FakeIdentity Me { get; }
        public FakeSignalProtocol Signal { get; } = new();
        public FakeVideoIo Video { get; } = new();
        public FakeRadioMesh Radio { get; }
        public CallService Calls { get; }

        public Phone(string tag)
        {
            Me = new FakeIdentity(tag);
            Radio = new FakeRadioMesh(tag) { LinkBandwidthBps = 250_000_000 };
            Radio.Radios = [new RadioInfo("Wi-Fi Direct", true)];
            Radio.Link();
            Calls = new CallService(Me, Signal, new SilentAudio(), Video, radio: Radio);
        }

        public string Tag => Me.AetherTag;
    }

    /// <summary>
    /// Two phones, wired to each other.
    /// </summary>
    /// <remarks>
    /// The same shape as the pair on the desk: everything one sends the other receives, through the
    /// real packet path — the dispatch, the marker matching, the gating, all of it. The faults this
    /// file exists for are faults between two ends, and one service talking to itself cannot express
    /// them.
    /// </remarks>
    private sealed class Pair
    {
        public Phone A { get; } = new("AAAAA-11111");
        public Phone B { get; } = new("BBBBB-22222");

        public Pair()
        {
            A.Radio.Peer = B.Radio;
            B.Radio.Peer = A.Radio;

            // Both ends know each other, which is what being in a Circle means.
            A.Signal.OpenSessionWith(B.Tag);
            B.Signal.OpenSessionWith(A.Tag);
            A.Radio.IdentifyPeer(B.Tag);
            B.Radio.IdentifyPeer(A.Tag);
        }

        /// <summary>A rings B, B answers, and both settle into a connected call.</summary>
        public async Task ConnectAsync()
        {
            await A.Calls.CallAsync(B.Tag);
            await Settle();
            await B.Calls.AnswerAsync();
            await Settle();
        }

        /// <summary>
        /// Let the asynchronous handlers run.
        /// </summary>
        /// <remarks>
        /// Packets are handled with fire-and-forget continuations, exactly as on a phone, so asserting
        /// the instant after sending is asserting against a half-delivered call.
        /// </remarks>
        public static async Task Settle()
        {
            for (var i = 0; i < 15; i++) await Task.Delay(10);
        }
    }

    private sealed class SilentAudio : IAudioIo
    {
        public bool IsPresent => true;
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool IsRunning => false;
        public bool SpeakerphoneOn { get; set; }
        public bool CanSwitchSpeaker => true;

        public event Action<short[]>? FrameCaptured;

        public void StartRinging(string callerTag) { }
        public void StopRinging() { }
        public void HoldCall(string? peerTag) { }
        public void ReleaseCall() { }
        public Task<bool> EnsurePermissionAsync() => Task.FromResult(true);
        public Task<bool> StartAsync(int s, int f, CancellationToken ct = default) => Task.FromResult(true);
        public void Play(short[] pcm) { }
        public Task StopAsync() { _ = FrameCaptured; return Task.CompletedTask; }
    }

    // ── the call itself ────────────────────────────────────────────────────

    [Fact]
    public async Task Two_phones_can_get_into_a_call()
    {
        var p = new Pair();
        await p.ConnectAsync();

        Assert.Equal(CallState.Connected, p.A.Calls.Current?.State);
        Assert.Equal(CallState.Connected, p.B.Calls.Current?.State);
    }

    // ── a camera is only announced once it is genuinely running ────────────

    /// <summary>
    /// A camera that will not open leaves the call exactly as it was — no flag here, and above all
    /// nothing told to the far end, which would otherwise hold a black tile for the rest of the call.
    /// </summary>
    [Fact]
    public async Task A_camera_that_will_not_open_is_never_announced()
    {
        var p = new Pair();
        await p.ConnectAsync();

        p.A.Video.WillStart = false;
        var ok = await p.A.Calls.SetVideoAsync(true);
        await Pair.Settle();

        Assert.False(ok);
        Assert.False(p.A.Calls.VideoOn);
        Assert.Equal(CaptureState.Idle, p.A.Video.Capture);
        Assert.Equal(0, p.A.Video.FrameListeners);
        Assert.False(p.B.Calls.TheirVideoOn, "B must never hear about a camera that never opened");
    }

    [Fact]
    public async Task A_camera_that_opens_is_seen_by_the_other_phone()
    {
        var p = new Pair();
        await p.ConnectAsync();

        Assert.True(await p.A.Calls.SetVideoAsync(true));
        await Pair.Settle();

        Assert.True(p.A.Calls.VideoOn);
        Assert.Equal(CaptureState.Capturing, p.A.Video.Capture);
        Assert.Equal(1, p.A.Video.FrameListeners);

        Assert.True(p.B.Calls.TheirVideoOn);
        Assert.Equal(1, p.B.Video.ShowIncomingCalls);
    }

    /// <summary>Both at once, which is what a video call actually is.</summary>
    [Fact]
    public async Task Both_cameras_can_be_on_together()
    {
        var p = new Pair();
        await p.ConnectAsync();

        await p.A.Calls.SetVideoAsync(true);
        await p.B.Calls.SetVideoAsync(true);
        await Pair.Settle();

        Assert.True(p.A.Calls.VideoOn && p.A.Calls.TheirVideoOn);
        Assert.True(p.B.Calls.VideoOn && p.B.Calls.TheirVideoOn);
    }

    // ── the worst one: intent outliving the device ─────────────────────────

    /// <summary>
    /// The camera stops by itself, and BOTH phones stop claiming otherwise.
    /// </summary>
    /// <remarks>
    /// SizeToLink shuts the camera when a link cannot carry a picture. Nothing was told, so this phone
    /// went on showing "Camera on" over a stopped camera — and the other one sat watching a frozen last
    /// frame for the rest of the call, because the only thing that would have corrected it was a
    /// camera-off message nobody sent. The second assertion is the one that matters.
    /// </remarks>
    [Fact]
    public async Task A_camera_that_gives_up_is_admitted_to_both_ends()
    {
        var p = new Pair();
        await p.ConnectAsync();
        await p.A.Calls.SetVideoAsync(true);
        await Pair.Settle();
        Assert.True(p.B.Calls.TheirVideoOn);

        p.A.Video.GiveUp();
        await Pair.Settle();

        Assert.False(p.A.Calls.VideoOn);
        Assert.False(p.B.Calls.TheirVideoOn, "B would otherwise sit on a frozen frame for the whole call");
    }

    /// <summary>
    /// And it lets go of the frame handler on the way out.
    /// </summary>
    /// <remarks>
    /// The give-up path cleared the flag and told the far end, and never unsubscribed — so the next
    /// camera-on attached a SECOND handler and every frame went out twice. The trigger for a give-up is
    /// a congested link, so the consequence of congestion was to double the traffic on it.
    /// </remarks>
    [Fact]
    public async Task A_camera_that_gives_up_and_comes_back_does_not_send_twice()
    {
        var p = new Pair();
        await p.ConnectAsync();

        await p.A.Calls.SetVideoAsync(true);
        Assert.Equal(1, p.A.Video.FrameListeners);

        p.A.Video.GiveUp();
        await Pair.Settle();
        Assert.Equal(0, p.A.Video.FrameListeners);

        await p.A.Calls.SetVideoAsync(true);
        Assert.Equal(1, p.A.Video.FrameListeners);
    }

    /// <summary>However many times it happens.</summary>
    [Fact]
    public async Task Nothing_accumulates_over_many_cycles()
    {
        var p = new Pair();
        await p.ConnectAsync();

        for (var i = 0; i < 5; i++)
        {
            await p.A.Calls.SetVideoAsync(true);
            await p.A.Calls.SetVideoAsync(false);
        }
        await Pair.Settle();

        Assert.Equal(0, p.A.Video.FrameListeners);
        Assert.False(p.B.Calls.TheirVideoOn);

        await p.A.Calls.SetVideoAsync(true);
        Assert.Equal(1, p.A.Video.FrameListeners);
    }

    [Fact]
    public async Task Turning_a_camera_off_lets_go_of_the_handler()
    {
        var p = new Pair();
        await p.ConnectAsync();

        await p.A.Calls.SetVideoAsync(true);
        await p.A.Calls.SetVideoAsync(false);
        await Pair.Settle();

        Assert.False(p.A.Calls.VideoOn);
        Assert.Equal(0, p.A.Video.FrameListeners);
        Assert.False(p.B.Calls.TheirVideoOn);
    }

    // ── one camera, and something else may have it ─────────────────────────

    /// <summary>
    /// A group call holding the camera means the 1:1 call cannot have it, and says so rather than
    /// prompting for a permission and then failing.
    /// </summary>
    [Fact]
    public async Task A_busy_camera_is_not_offered()
    {
        var p = new Pair();
        await p.ConnectAsync();

        p.A.Video.TakenBySomethingElse();

        Assert.False(p.A.Calls.CanSendVideo);
        Assert.Equal("the camera is busy with another call", p.A.Calls.CannotSendVideoReason);
        Assert.False(await p.A.Calls.SetVideoAsync(true));
        Assert.Equal(0, p.A.Video.Starts);
    }

    /// <summary>Ending a call gives the device back, so the next one can have it.</summary>
    [Fact]
    public async Task Ending_a_call_gives_the_camera_back()
    {
        var p = new Pair();
        await p.ConnectAsync();
        await p.A.Calls.SetVideoAsync(true);

        await p.A.Calls.HangUpAsync();
        await Pair.Settle();

        Assert.True(p.A.Video.CanClaim(new object()), "nothing should still be holding it");
        Assert.False(p.A.Calls.VideoOn);
        Assert.Equal(0, p.A.Video.FrameListeners);
    }

    // ── what the far end says ──────────────────────────────────────────────

    /// <summary>
    /// Their camera going off takes their decoder and their tile with it.
    /// </summary>
    /// <remarks>
    /// Forget existed for exactly this and was only ever called by the group service, so a 1:1 call
    /// held both for the rest of the call every time somebody switched off.
    /// </remarks>
    [Fact]
    public async Task Their_camera_going_off_drops_their_picture()
    {
        var p = new Pair();
        await p.ConnectAsync();

        await p.B.Calls.SetVideoAsync(true);
        await Pair.Settle();
        Assert.True(p.A.Calls.TheirVideoOn);

        await p.B.Calls.SetVideoAsync(false);
        await Pair.Settle();

        Assert.False(p.A.Calls.TheirVideoOn);
        Assert.Contains(p.B.Tag, p.A.Video.Forgotten);
    }

    /// <summary>
    /// Believed only as far as this phone can act on it.
    /// </summary>
    /// <remarks>
    /// The flag was set from the packet and the surfaces attempted afterwards, so a claim that failed
    /// left the phone insisting somebody was on camera with nowhere to draw them — and the call screen
    /// hides the app bar, the content and the tab bar to show a picture that was never coming. A blank
    /// screen with nothing behind it.
    /// </remarks>
    [Fact]
    public async Task Their_camera_is_not_believed_if_it_cannot_be_shown()
    {
        var p = new Pair();
        await p.ConnectAsync();
        p.A.Video.TakenBySomethingElse();

        await p.B.Calls.SetVideoAsync(true);
        await Pair.Settle();

        Assert.False(p.A.Calls.TheirVideoOn);
        Assert.Equal(0, p.A.Video.ShowIncomingCalls);
    }

    // ── turning mine off while theirs is on ────────────────────────────────

    /// <summary>
    /// Their picture survives me turning my own camera off — the device stops sending, it does not
    /// tear the screen down.
    /// </summary>
    [Fact]
    public async Task Turning_my_camera_off_does_not_black_out_theirs()
    {
        var p = new Pair();
        await p.ConnectAsync();

        await p.B.Calls.SetVideoAsync(true);
        await Pair.Settle();
        await p.A.Calls.SetVideoAsync(true);
        await Pair.Settle();

        await p.A.Calls.SetVideoAsync(false);
        await Pair.Settle();

        Assert.True(p.A.Calls.TheirVideoOn, "B is still showing A theirs");
        Assert.Equal(1, p.A.Video.SendStops);
        Assert.Equal(0, p.A.Video.FullStops);
    }

    // ── not yet bitten, and the shape says they could ──────────────────────

    /// <summary>
    /// A camera cannot be turned on before the other person has picked up.
    /// </summary>
    /// <remarks>
    /// Anticipated rather than observed. The call screen only draws the camera button once connected,
    /// so nothing reaches this today — but the guard is one clause in a condition, and the moment
    /// anything else can reach it (a headset button, an accessibility action, a bug in the markup) a
    /// camera would open for a call nobody has answered, and the far end would be told about it.
    /// </remarks>
    [Fact]
    public async Task A_camera_cannot_be_turned_on_before_they_answer()
    {
        var p = new Pair();
        await p.A.Calls.CallAsync(p.B.Tag);
        await Pair.Settle();

        Assert.False(await p.A.Calls.SetVideoAsync(true), "the call is still ringing");
        Assert.False(p.A.Calls.VideoOn);
        Assert.Equal(0, p.A.Video.Starts);
    }

    /// <summary>
    /// Asking twice for a camera that is already on changes nothing.
    /// </summary>
    /// <remarks>
    /// Anticipated. A double tap, a re-render dispatching a stale handler, a retry — all reach the
    /// same method, and the version of it that attached a handler without detaching first turned this
    /// into every frame going out twice. The early return covers it today; this pins that it stays
    /// covered.
    /// </remarks>
    [Fact]
    public async Task Asking_twice_for_a_camera_that_is_on_changes_nothing()
    {
        var p = new Pair();
        await p.ConnectAsync();

        await p.A.Calls.SetVideoAsync(true);
        await p.A.Calls.SetVideoAsync(true);
        await Pair.Settle();

        Assert.Equal(1, p.A.Video.FrameListeners);
        Assert.Equal(1, p.A.Video.Starts);
    }

    /// <summary>
    /// The OTHER person hanging up releases this phone's camera.
    /// </summary>
    /// <remarks>
    /// Anticipated, and the more likely half of the pair: hanging up here runs EndAsync directly,
    /// while a hangup arriving over the radio takes a different route into the same teardown. A camera
    /// left running after the far end rings off is the failure people notice, because the indicator
    /// stays lit.
    /// </remarks>
    [Fact]
    public async Task Them_hanging_up_releases_this_phone_camera()
    {
        var p = new Pair();
        await p.ConnectAsync();
        await p.A.Calls.SetVideoAsync(true);
        await Pair.Settle();

        await p.B.Calls.HangUpAsync();
        await Pair.Settle();

        Assert.Null(p.A.Calls.Current);
        Assert.False(p.A.Calls.VideoOn);
        Assert.Equal(CaptureState.Idle, p.A.Video.Capture);
        Assert.Equal(0, p.A.Video.FrameListeners);
        Assert.True(p.A.Video.CanClaim(new object()), "the device must be free for the next call");
    }

    /// <summary>
    /// And their camera state does not survive the call it belonged to.
    /// </summary>
    /// <remarks>
    /// Anticipated. TheirVideoOn drives whether the whole screen turns into a picture; carrying it
    /// into the next call would open that call already believing somebody is on camera, drawing over
    /// a stream nobody is sending.
    /// </remarks>
    [Fact]
    public async Task Their_camera_state_does_not_outlive_the_call()
    {
        var p = new Pair();
        await p.ConnectAsync();

        await p.B.Calls.SetVideoAsync(true);
        await Pair.Settle();
        Assert.True(p.A.Calls.TheirVideoOn);

        await p.B.Calls.HangUpAsync();
        await Pair.Settle();

        Assert.False(p.A.Calls.TheirVideoOn);
    }

    /// <summary>
    /// Both cameras coming on at the same instant, rather than one after the other.
    /// </summary>
    /// <remarks>
    /// Anticipated. Every hardware test so far has tapped one phone and then the other, seconds apart.
    /// Two people reaching for the button together is the ordinary case, and it exercises a claim, a
    /// device start and an inbound camera-state packet arriving at once on each side.
    /// </remarks>
    [Fact]
    public async Task Both_cameras_coming_on_at_once_still_settle()
    {
        var p = new Pair();
        await p.ConnectAsync();

        await Task.WhenAll(
            p.A.Calls.SetVideoAsync(true),
            p.B.Calls.SetVideoAsync(true));
        await Pair.Settle();

        Assert.True(p.A.Calls.VideoOn && p.A.Calls.TheirVideoOn);
        Assert.True(p.B.Calls.VideoOn && p.B.Calls.TheirVideoOn);
        Assert.Equal(1, p.A.Video.FrameListeners);
        Assert.Equal(1, p.B.Video.FrameListeners);
    }

    /// <summary>
    /// A camera that gives up during a call the far end has already left.
    /// </summary>
    /// <remarks>
    /// Anticipated. The give-up path announces a camera-off, and it can fire after Current is null —
    /// a link collapsing is exactly what ends calls, so the two arrive together. It must not throw,
    /// and it must still put the device down.
    /// </remarks>
    [Fact]
    public async Task A_camera_giving_up_after_the_call_ended_is_harmless()
    {
        var p = new Pair();
        await p.ConnectAsync();
        await p.A.Calls.SetVideoAsync(true);
        await Pair.Settle();

        await p.A.Calls.HangUpAsync();
        await Pair.Settle();

        p.A.Video.GiveUp();
        await Pair.Settle();

        Assert.False(p.A.Calls.VideoOn);
        Assert.Equal(0, p.A.Video.FrameListeners);
    }

    /// <summary>
    /// Frames arriving from somebody this phone is not on a call with go nowhere.
    /// </summary>
    /// <remarks>
    /// Anticipated. Media opens under the call key rather than the ratchet, so a stranger's frames
    /// cannot decrypt — but Play is reached before that is proven in some orderings, and drawing an
    /// uninvited picture over somebody's screen is the same class of fault as the camera-state packet
    /// that had no call check.
    /// </remarks>
    [Fact]
    public async Task Frames_from_a_stranger_are_not_drawn()
    {
        var p = new Pair();
        await p.ConnectAsync();
        await p.A.Calls.SetVideoAsync(true);
        await Pair.Settle();

        p.B.Video.Played.Clear();

        // A third party shouting frames at B, who is in a call with A.
        var stranger = new Phone("CCCCC-33333");
        stranger.Radio.Peer = p.B.Radio;
        stranger.Signal.OpenSessionWith(p.B.Tag);
        p.B.Signal.OpenSessionWith(stranger.Tag);
        stranger.Video.Raise([0, 0, 0, 1, 0x65, 9, 9, 9]);
        await Pair.Settle();

        Assert.DoesNotContain(stranger.Tag, p.B.Video.Played);
    }

    // ── frames go where they belong ────────────────────────────────────────

    /// <summary>An encoded frame reaches the other phone's screen.</summary>
    [Fact]
    public async Task A_frame_reaches_the_other_phone()
    {
        var p = new Pair();
        await p.ConnectAsync();
        await p.A.Calls.SetVideoAsync(true);
        await Pair.Settle();

        p.A.Video.Raise([0, 0, 0, 1, 0x65, 1, 2, 3]);
        await Pair.Settle();

        Assert.Contains(p.A.Tag, p.B.Video.Played);
    }
}
