// SPDX-License-Identifier: MIT

using System.Text;
using System.Threading.Channels;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Services;
using AetherNet.Voice;
using AetherNet.Voice.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// A voice call between two phones, with no tower and no server between them.
///
/// <para>
/// The protocol library owns the parts that belong to every implementation — the call state machine,
/// the signalling schema, the jitter buffer. This owns the parts that belong to a phone: the
/// microphone, the codec, and the decision to seal everything before it goes out.
/// </para>
///
/// <para>
/// Both directions run at a fixed pace. Frames leave as the microphone produces them, twenty
/// milliseconds at a time, and arrive to be decoded and played at the same rate. Nothing is buffered
/// beyond what the platform needs, because a call that is a second behind is not a call.
/// </para>
/// </summary>
public sealed class CallService : IDisposable
{
    private readonly IIdentityService _me;
    private readonly IRadioMesh? _radio;
    private readonly ISignalProtocolService _signal;
    private readonly IAudioIo _audio;
    private readonly IVideoIo? _video;
    private readonly FastRadioService? _fastRadio;

    /// <summary>
    /// Chat, only for its session repair. A call hits exactly the wall a message does, and duplicating
    /// the recovery would mean two implementations of the trickiest code in the app drifting apart.
    /// </summary>
    private readonly ChatService? _chat;

    /// <summary>
    /// Where calls are written down. Optional, because a host without a database can still place a
    /// call — it simply will not remember it afterwards.
    /// </summary>
    private readonly AetherNet.Sample.Shared.Data.AetherStore? _store;

    /// <summary>When the call in progress started, so history has a start and not only an end.</summary>
    private DateTime? _startedAt;
    private readonly ILogger _log;

    private IVoiceCallService? _voice;
    private EncryptedMeshSender? _sender;
    private OpusVoiceCodec? _codec;
    private uint _sequence;

    /// <summary>
    /// Frames waiting to go out, and the single task draining them.
    ///
    /// <para>
    /// Four frames is 80ms. It was sixteen — a third of a second — chosen to ride out a radio that
    /// stalls. Measured, this radio does not stall: it takes fifty frames a second at under a
    /// millisecond each. So that depth absorbed nothing and was simply delay a listener heard, on top
    /// of the capture buffer, the playback buffer and the time on air. Deep enough to cover a
    /// scheduling hiccup, shallow enough that nobody waits for it.
    /// </para>
    ///
    /// <para>
    /// This is a jitter absorber, not a bandwidth excuse. These two handsets negotiate an ATT MTU of
    /// 517 and already carry 512-byte packets, which is on the order of 100 kbps — Opus at 24 kbps
    /// needs about 3 kB/s and fits with room to spare. An earlier note in this file claimed BLE could
    /// only manage 5 kbps; that figure came from a link still on the 23-byte default MTU and is wrong
    /// for this one. If frames are not arriving, look for them being dropped before the radio, not for
    /// the radio being too small.
    /// </para>
    /// </summary>
    private Channel<(Guid CallId, byte[] Payload, uint Sequence)>? _outgoing;
    private Task? _pump;
    private const int OutgoingFrameQueue = 4;
    private bool _disposed;

    /// <summary>
    /// Marks the one message that carries a call's media key. Sent inside the session, which is exactly
    /// what a ratchet is good at — a single message, in order, once per call.
    /// </summary>
    private const string KeyMarker = "VOK1";

    /// <summary>What this phone offers. One entry, because we ship one codec and it is the right one.</summary>
    private static readonly string[] Offered = ["opus"];

    public CallService(
        IIdentityService me,
        ISignalProtocolService signal,
        IAudioIo audio,
        IVideoIo? video = null,
        AetherNet.Sample.Shared.Data.AetherStore? store = null,
        ChatService? chat = null,
        IRadioMesh? radio = null,
        ILoggerFactory? loggerFactory = null,
        FastRadioService? fastRadio = null)
    {
        _store = store;
        _chat = chat;
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _video = video;
        _fastRadio = fastRadio;
        _radio = radio;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<CallService>();

        if (_radio is not null) _radio.PacketReceived += OnPacket;
        _audio.FrameCaptured += OnMicrophoneFrame;
    }

    /// <summary>Raised whenever the call's state changes, so the UI can re-render.</summary>
    public event Action? Changed;

    /// <summary>Running commentary on the call path, for the radio log.</summary>
    public event Action<string>? Trace;

    /// <summary>The call in progress, or null when there is none.</summary>
    public VoiceCallSession? Current { get; private set; }

    /// <summary>Who the call is with, from this phone's point of view.</summary>
    public string? PeerTag => Current is null ? null : Current.RemoteUhid(_me.AetherTag);

    /// <summary>
    /// Whether this phone's microphone is muted for the call.
    ///
    /// <para>
    /// Muting drops the frame before it is encoded rather than sending encoded silence. Silence would
    /// be politer to a jitter buffer, but it also means a muted microphone is still being transmitted,
    /// and on a radio this tight that is bandwidth spent saying nothing.
    /// </para>
    /// </summary>
    public bool IsMuted { get; private set; }

    /// <summary>Mute or unmute, and tell the UI.</summary>
    public void SetMuted(bool muted)
    {
        if (IsMuted == muted) return;
        IsMuted = muted;
        T(muted ? "microphone muted" : "microphone live");
        Raise();
    }

    /// <summary>Whether call audio is on the loudspeaker rather than the earpiece.</summary>
    public bool SpeakerphoneOn
    {
        get => _audio.SpeakerphoneOn;
        set { _audio.SpeakerphoneOn = value; Raise(); }
    }

    /// <summary>
    /// The call is running but its screen is put away.
    ///
    /// <para>
    /// Lives here rather than in the call screen because other pages have to make room for the bar
    /// that replaces it. It covered the chat composer on the first run — a call you could not type
    /// underneath, which is most of the point of minimising one.
    /// </para>
    /// </summary>
    public bool IsMinimised { get; private set; }

    /// <summary>Put the call screen away, or bring it back. Never ends the call.</summary>
    public void SetMinimised(bool minimised)
    {
        if (IsMinimised == minimised) return;
        IsMinimised = minimised;
        Raise();
    }

    /// <summary>Whether this device can switch between earpiece and loudspeaker at all.</summary>
    public bool CanSwitchSpeaker => _audio.CanSwitchSpeaker;

    /// <summary>How long the call has been connected, or null when no call is connected.</summary>
    public TimeSpan? Duration => Current is { State: CallState.Connected, ConnectedAt: { } at }
        ? DateTime.UtcNow - at
        : null;

    /// <summary>
    /// Whether this phone placed the call.
    ///
    /// <para>
    /// The media cipher derives a different key for each direction, so both ends must agree on which
    /// of them is "the caller". That is decided by who dialled — never by who happened to mint the
    /// key, which is what it used to be inferred from.
    /// </para>
    /// </summary>
    private bool IAmTheCaller =>
        Current is { } c && string.Equals(c.CallerUhid, _me.AetherTag, StringComparison.Ordinal);

    /// <summary>
    /// Whether a call is worth offering. Deliberately does <b>not</b> require the microphone permission
    /// — that is asked for on the tap, and gating the button on it would hide the only way to grant it.
    /// </summary>
    public bool CanCall => _audio.IsPresent && _radio is { IsLinked: true } && HasARadioWideEnough;

    /// <summary>Why a call cannot be placed right now, in plain words — or null when it can.</summary>
    public string? CannotCallReason =>
        !_audio.IsPresent ? _audio.UnavailableReason ?? "no microphone"
        : _radio is not { IsLinked: true } ? "no phone is connected to this one"
        : !HasARadioWideEnough ? "the only connection to this phone is Bluetooth, which is too slow for a call — send a voice note instead"
        : null;

    /// <summary>
    /// Is there any radio on this phone that could carry a call — now or in a moment?
    ///
    /// <para>
    /// Measured, Bluetooth moves about eleven kilobits between two handsets and a call needs fifty
    /// (PROTOCOL_SPEC §5.5). Placing one over it does not produce a poor call; it produces a call
    /// that rings, answers, connects and stays silent, with both people believing it works. Refusing
    /// up front and naming the reason is kinder than that by a wide margin.
    /// </para>
    ///
    /// <para>
    /// The current link being narrow is <b>not</b> enough to refuse, because at the moment of asking
    /// it usually is: Wi-Fi Direct is brought up by the call itself, brokered over Bluetooth. So this
    /// refuses only when no wide radio exists on this phone at all — nothing is coming.
    /// </para>
    /// </summary>
    private bool HasARadioWideEnough =>
        OpusVoiceCodec.CanCarryCall(_radio?.LinkBandwidthBps ?? 0) ||
        // A radio that can carry a call but has not linked yet is still a phone that can call.
        _radio?.Radios.Any(r => r.Name == "Wi-Fi Direct" && r.Available) == true;

    // ── Placing and answering ─────────────────────────────────────────────────

    public async Task<bool> CallAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        if (Current is not null || string.IsNullOrEmpty(peerTag)) return false;

        // Ask for the microphone here, where the reason for asking is obvious, rather than during
        // setup where it is one more prompt in a queue nobody reads.
        if (!await _audio.EnsurePermissionAsync().ConfigureAwait(false))
        {
            T($"cannot call — {_audio.UnavailableReason ?? "no microphone"}");
            Raise();
            return false;
        }

        if (!CanCall) { T($"cannot call — {CannotCallReason}"); return false; }

        // Nothing leaves this phone unencrypted, so there has to be a session first. Chat builds one
        // on first contact; a call placed before anyone has ever spoken has to wait for that.
        if (!_signal.HasSession(peerTag))
        {
            T($"cannot call {peerTag} — no secure session yet; send a message first");
            return false;
        }

        // Voice needs about 24 kbps each way and BLE carries about eleven in one direction, so the call
        // brings up the wide pipe on its way out. Not fatal if it does not come up — the call still
        // rings, and if the far end can be reached at all the signalling gets there; only the audio
        // would struggle. CanCall has already refused the case where nothing wide could ever arrive.

        var voice = Voice();
        Current = await voice.PlaceAsync(peerTag, Offered, cancellationToken).ConfigureAwait(false);

        // The media key is NOT sent here. The offer is very often the first thing this pair has ever
        // encrypted to each other, and the far side has no session until it decrypts that offer —
        // decrypting is what builds it. Sending a second message a few hundred milliseconds behind
        // lands it on a session still being constructed, and it does not open. Watched on device
        // 2026-08-18 with identities minutes old: the offer and the key both refused, every time,
        // while chat over the same pair worked because chat sends one message and waits.
        //
        // So the key goes out when they answer, by which point both sides have a live session.
        // The protocol fails the call outright when no radio would take the offer. Do not claim to be
        // calling in that case — there is nothing ringing anywhere.
        if (Current is { State: CallState.Failed })
        {
            T($"could not call {peerTag} — no radio took the offer");
            Current = null;
            Raise();
            return false;
        }

        _startedAt = DateTime.UtcNow;   // history needs a start, not only an end
        T($"calling {peerTag}");
        Raise();
        StopRinging();
        _ringingOut = new CancellationTokenSource();
        _ = GiveUpUnlessAnsweredAsync(Current, peerTag, _ringingOut.Token);
        return true;
    }

    /// <summary>How long to ring before giving up. Long enough to reach a phone in a pocket.</summary>
    private static readonly TimeSpan RingFor = TimeSpan.FromSeconds(45);

    private CancellationTokenSource? _ringingOut;

    private void StopRinging()
    {
        _ringingOut?.Cancel();
        _ringingOut?.Dispose();
        _ringingOut = null;
    }

    /// <summary>
    /// Give an unanswered call an ending.
    ///
    /// <para>
    /// Nothing used to time an outgoing call out, so a call that reached nobody showed "Calling…" for
    /// as long as the person was willing to look at it — five minutes, on the run that found this. A
    /// call that is not going to be answered has to say so, or the screen is simply lying about what
    /// the radio is doing.
    /// </para>
    /// </summary>
    private async Task GiveUpUnlessAnsweredAsync(VoiceCallSession session, string peerTag, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RingFor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }   // answered, or hung up

        if (Current is null || Current.Id != session.Id || Current.State != CallState.Outgoing) return;

        T($"{peerTag} did not answer after {RingFor.TotalSeconds:0}s — giving up");
        await HangUpAsync(HangupReason.Timeout).ConfigureAwait(false);
    }

    public async Task AnswerAsync(CancellationToken cancellationToken = default)
    {
        if (Current is not { State: CallState.Incoming } session) return;

        // Picked up — stop ringing before anything else, because that is the part the person hears.
        _audio.StopRinging();

        // The person answering needs the microphone every bit as much as the person who dialled. This
        // was asked for only when placing a call, so answering one went straight to opening a device
        // nobody had been given permission to open, failed, and dropped the call as a network fault —
        // which is a confusing way to say "we never asked".
        if (!await _audio.EnsurePermissionAsync().ConfigureAwait(false))
        {
            T($"cannot answer — {_audio.UnavailableReason ?? "no microphone"}");
            await DeclineAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // The answering side does NOT bring the pipe up. The caller hosts; hosting here as well would
        // create a second group and neither phone would be in the other's.

        // Mint the call's key HERE, before answering, so this side can seal a frame the instant it
        // picks up.
        //
        // The caller used to mint it on RECEIVING the answer, which left this side unable to send
        // anything until that key came back — measured at ~300ms of "voice frame DROPPED — the call
        // has no media key" straight after pickup, which is exactly when someone starts talking.
        // Minting here costs the caller nothing: the key rides alongside the answer it was already
        // waiting for.
        var master = CallMediaCipher.NewMasterKey();
        Voice();
        KeyBothTracks(master);
        await SendKeyAsync(session.RemoteUhid(_me.AetherTag), master, cancellationToken).ConfigureAwait(false);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(master);

        if (!await StartAudioAsync(cancellationToken).ConfigureAwait(false))
        {
            await HangUpAsync(HangupReason.NetworkFailure, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Voice().AnswerAsync(session.Id, "opus", OpusVoiceCodec.DefaultSampleRateHz, cancellationToken)
            .ConfigureAwait(false);

        session.State = CallState.Connected;
        session.Codec = "opus";
        session.SampleRateHz = OpusVoiceCodec.DefaultSampleRateHz;
        T($"answered {PeerTag}");
        Raise();
    }

    public async Task DeclineAsync(CancellationToken cancellationToken = default)
    {
        if (Current is not { State: CallState.Incoming } session) return;
        await Voice().DeclineAsync(session.Id, HangupReason.Declined, cancellationToken).ConfigureAwait(false);
        await EndAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Hang up — stop talking first, then say goodbye.
    ///
    /// <para>
    /// The order matters and used to be the other way round. A live call keeps the radio's queue full
    /// of audio; sending the hangup while the microphone was still running put it behind half a second
    /// of speech on a radio that was already behind, so the far phone stayed on the call — still
    /// playing, still sending — long after this one had hung up, and sometimes never heard about it at
    /// all.
    /// </para>
    ///
    /// <para>
    /// Stopping the audio first empties the queue, so the one packet that actually matters goes out on
    /// a clear radio.
    /// </para>
    /// </summary>
    public async Task HangUpAsync(HangupReason reason = HangupReason.Normal, CancellationToken cancellationToken = default)
    {
        if (Current is not { } session) return;

        // Silence first: stop the microphone and drop whatever is still queued for it.
        StopRinging();
        _audio.StopRinging();
        try { await _audio.StopAsync().ConfigureAwait(false); } catch { }
        _outgoing?.Writer.TryComplete();

        try { await Voice().HangupAsync(session.Id, reason, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "hangup"); }

        await EndAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Key both tracks from the one master secret.
    ///
    /// <para>
    /// Video is keyed even while the camera is off, and deliberately so. Turning it on mid-call is a
    /// single signalling message; if the key were minted at that moment, the first frames would arrive
    /// ahead of it and be discarded — which reads exactly like a camera that does not work.
    /// </para>
    ///
    /// <para>
    /// Direction comes from the call's ROLE, never from who happened to send the key. It was hardcoded
    /// once, which held only while the caller always minted — the moment the answering side mints, so
    /// it can speak the instant it picks up, both ends derive the same direction keys and neither can
    /// read the other.
    /// </para>
    /// </summary>
    private void KeyBothTracks(ReadOnlySpan<byte> master)
    {
        _sender!.Media?.Dispose();
        _sender.Video?.Dispose();
        _sender.Media = new CallMediaCipher(master, iAmTheCaller: IAmTheCaller);
        _sender.Video = new CallMediaCipher(master, iAmTheCaller: IAmTheCaller, video: true);
    }

    // ── The video track ───────────────────────────────────────────────────────

    /// <summary>Marks the message that says whether this phone has its camera on.</summary>
    private const string VideoMarker = "VID1";

    /// <summary>Camera on, camera off. One byte, because that is all it says.</summary>
    private const byte CameraOn = 1;

    /// <summary>True while this phone is sending video.</summary>
    public bool VideoOn { get; private set; }

    /// <summary>True while the other phone is sending video.</summary>
    public bool TheirVideoOn { get; private set; }

    /// <summary>
    /// One encoded video frame from the other phone, already decrypted and in call order.
    ///
    /// <para>
    /// Raised on a radio thread and carrying H.264 rather than pixels — decoding is the platform's
    /// job, and doing it here would put a decoder on the receive path of every host, including the
    /// ones with no screen to draw on.
    /// </para>
    /// </summary>
    public event Action<byte[]>? VideoFrameReceived;

    /// <summary>
    /// What a video call <b>costs</b> a radio.
    ///
    /// <para>
    /// Video is roughly 800 kbps against voice's 24, and it still has to carry the voice. With the
    /// same one-third margin the codec uses, that is about three megabits. Wi-Fi Direct clears it
    /// comfortably; nothing else measured here comes close (PROTOCOL_SPEC §5.5).
    /// </para>
    ///
    /// <para>
    /// <b>This is no longer the gate</b>, and it should not be used as one. Comparing it against a
    /// link's measured throughput cannot ever pass mid-call, because a call in progress is carrying
    /// voice and only voice — see <see cref="CanSendVideo"/>. It is kept as the costing the spec and
    /// <see cref="VideoBudget"/> are checked against.
    /// </para>
    /// </summary>
    public const long MinLinkBpsForVideo = 3_000_000;

    /// <summary>
    /// Whether video is worth attempting on the radio actually carrying this call.
    ///
    /// <para>
    /// Offering a camera button on a link that cannot carry one does not produce poor video. It
    /// produces a frozen picture and ruins the audio that was working a moment ago, because the
    /// frames crowd out the voice they share the radio with.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Asks whether the link is <em>struggling</em>, not how much it has carried. This used to test
    /// measured throughput against a floor, which cannot ever pass during a call: the link is carrying
    /// about 24 kbps because voice is all anyone is offering it, so "is it doing three megabits" is no,
    /// forever. The camera button stayed disabled and every tap did nothing, silently, on a link that
    /// had just moved nine thousand voice frames without refusing one. The group call had the same bug;
    /// this is its twin, and it was missed when that one was fixed.
    /// </remarks>
    public bool CanSendVideo =>
        Current is { State: CallState.Connected } &&
        _video is { IsPresent: true } &&
        MediaBitrate.WorthVideo(_radio?.LinkStrain ?? 0);

    /// <summary>Why the camera is not on offer, in plain words — or null when it is.</summary>
    public string? CannotSendVideoReason =>
        Current is not { State: CallState.Connected } ? "not in a call"
        : _video is not { IsPresent: true } ? _video?.UnavailableReason ?? "this device has no camera"
        : !CanSendVideo ? (_radio?.LinkRadio ?? "this radio") + " is working too hard for video — voice only"
        : null;

    /// <summary>
    /// Turn this phone's camera on or off, and tell the other phone either way.
    ///
    /// <para>
    /// Telling them matters as much as the frames do. A phone that simply stops receiving video
    /// cannot tell a camera that was switched off from a link that has died, and it must not sit
    /// showing a frozen last frame as though everything were fine.
    /// </para>
    /// </summary>
    public async Task<bool> SetVideoAsync(bool on, CancellationToken cancellationToken = default)
    {
        if (Current is not { State: CallState.Connected } call) return false;

        if (on && !CanSendVideo)
        {
            T("cannot send video — " + CannotSendVideoReason);
            return false;
        }

        if (VideoOn == on) return true;

        if (on)
        {
            if (_video is null) return false;

            // Asked for here, at the tap, rather than during setup — this is the moment the reason is
            // obvious. A refusal leaves the voice call exactly as it was.
            if (!await _video.EnsurePermissionAsync().ConfigureAwait(false))
            {
                T("cannot send video — the camera was not allowed");
                return false;
            }

            if (!await _video.StartAsync(cancellationToken).ConfigureAwait(false))
            {
                T("cannot send video — " + (_video.UnavailableReason ?? "the camera would not open"));
                return false;
            }

            _video.FrameEncoded += OnEncodedFrame;
            _video.ShowRemote(TheirVideoOn);
        }
        else if (_video is not null)
        {
            _video.FrameEncoded -= OnEncodedFrame;

            // Only give the SCREEN back when neither camera is on — turning mine off while they are
            // still showing me theirs must not black their picture out. The camera itself stops
            // either way: unhooking the frame event only stops them being sent, and this used to
            // leave the camera open and encoding for the rest of the call with the button reading
            // "Camera off".
            if (TheirVideoOn) await _video.StopSendingAsync().ConfigureAwait(false);
            else await _video.StopAsync().ConfigureAwait(false);
        }

        VideoOn = on;
        await AnnounceVideoAsync(call.RemoteUhid(_me.AetherTag), on, cancellationToken).ConfigureAwait(false);
        Raise();
        return true;
    }

    /// <summary>Flip between the front and back cameras mid-call.</summary>
    /// <remarks>
    /// The far end is told again afterwards. The two lenses are mounted at different angles and the
    /// front one is mirrored, so a flip changes which way up this phone's picture arrives — announcing
    /// only on/off would leave them watching a correct picture upside down.
    /// </remarks>
    public void SwitchCamera()
    {
        _video?.SwitchCamera();

        if (!VideoOn || Current is not { State: CallState.Connected } call) return;

        _ = Task.Run(async () =>
        {
            try
            {
                // The new lens is opened asynchronously; its angle is not known the instant the flip
                // is asked for.
                await Task.Delay(400).ConfigureAwait(false);
                await AnnounceVideoAsync(call.RemoteUhid(_me.AetherTag), true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) { _log.LogDebug(ex, "re-announcing the camera angle"); }
        });
    }

    /// <summary>
    /// A frame off this phone's encoder. Sent without waiting: the encoder's thread must keep
    /// draining, and a frame held here is a frame not being encoded behind it.
    /// </summary>
    private void OnEncodedFrame(byte[] frame) => _ = SendVideoFrameAsync(frame);

    /// <summary>
    /// Send one encoded video frame.
    ///
    /// <para>
    /// Dropped rather than queued when the camera is off or the call has ended. A video frame is
    /// worthless a moment after it was captured, so there is nothing to be gained by holding one —
    /// unlike a voice frame, which at least still carries a word somebody said.
    /// </para>
    /// </summary>
    public async Task SendVideoFrameAsync(byte[] encoded, CancellationToken cancellationToken = default)
    {
        if (!VideoOn || encoded is null || encoded.Length == 0) return;
        if (Current is not { State: CallState.Connected } call) return;

        var remote = call.RemoteUhid(_me.AetherTag);
        if (string.IsNullOrEmpty(remote)) return;

        try
        {
            var packet = new MeshPacket
            {
                Type = PacketType.VideoFrame,
                SourceUhid = _me.AetherTag,
                DestinationUhid = remote,
                Ttl = 1,                   // one hop; a relayed video call is not a video call
                Payload = encoded,
            };

            await _sender!.SendAsync(packet, remote, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "video frame");
        }
    }

    /// <summary>Tell the other phone the camera went on or off. Inside the session, like the key.</summary>
    private async Task AnnounceVideoAsync(string peerTag, bool on, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(peerTag) || _radio is null) return;

        try
        {
            // Two bytes now: whether the camera is on, and which way up its pictures come out. A
            // reader that only knows the first byte still gets the camera state right, which is what
            // keeps this compatible with a phone running the older build.
            var turn = VideoRotation.ToWire(_video?.CaptureRotation ?? 0);

            var sealedBody = await _signal
                .EncryptAsync(peerTag, new[] { on ? CameraOn : (byte)0, turn }, cancellationToken)
                .ConfigureAwait(false);

            var body = AetherNet.Messaging.EncryptedPayloadCodec.Serialize(sealedBody);
            var payload = new byte[VideoMarker.Length + body.Length];
            System.Text.Encoding.UTF8.GetBytes(VideoMarker).CopyTo(payload, 0);
            body.CopyTo(payload, VideoMarker.Length);

            await _radio.SendPacketAsync(PacketSerializer.Serialize(new MeshPacket
            {
                // Typed, so the send path can put it in the real-time lane. While this was Data
                // it queued behind attachment chunks like everything else.
                Type = PacketType.VideoFrame,
                SourceUhid = _me.AetherTag,
                DestinationUhid = peerTag,
                Ttl = 1,      // the same one hop the key takes; a camera state is between two phones
                Payload = payload,
            })).ConfigureAwait(false);

            T("camera " + (on ? "on" : "off"));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "video announce");
        }
    }

    /// <summary>They turned their camera on or off.</summary>
    private async Task ReceiveVideoStateAsync(string? from, byte[] payload)
    {
        if (string.IsNullOrEmpty(from)) return;

        try
        {
            var sealedBody = AetherNet.Messaging.EncryptedPayloadCodec.Deserialize(
                payload.AsSpan(VideoMarker.Length).ToArray());
            var body = await _signal.DecryptAsync(from, sealedBody).ConfigureAwait(false);

            TheirVideoOn = body.Length > 0 && body[0] == CameraOn;

            // A phone on the older build sends one byte and no angle. Zero is the right answer for it:
            // it is what was being drawn before, so nothing gets worse.
            _video?.SetRemoteRotation(from, body.Length > 1 ? VideoRotation.FromWire(body[1]) : 0);

            // Their camera going on when mine is off still needs somewhere to draw, so the surfaces
            // come up for their picture alone — and ONLY the surfaces. This used to call StartAsync,
            // which opens this phone's camera as well: the person was shown "Camera off" while their
            // camera was genuinely running. Showing a picture and sending one are different things.
            if (TheirVideoOn && _video is { IsPresent: true })
                await _video.ShowIncomingAsync().ConfigureAwait(false);

            _video?.ShowRemote(TheirVideoOn);

            if (!TheirVideoOn && !VideoOn && _video is { IsRunning: true })
                await _video.StopAsync().ConfigureAwait(false);

            _radio?.IdentifyPeer(from);
            T("their camera is " + (TheirVideoOn ? "on" : "off"));
            Raise();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "video state");
        }
    }

    // ── The audio path ────────────────────────────────────────────────────────

    private async Task<bool> StartAudioAsync(CancellationToken cancellationToken)
    {
        // Claim the phone for the duration of the call. Without this Android takes the microphone back
        // the moment the person opens anything else — the call goes silent, or the process is killed
        // outright, and the far end is never told.
        _audio.HoldCall(PeerTag);
        IsMuted = false;   // every call starts with the microphone live

        // Size the codec to the radio actually carrying the call, not to a constant. This is what
        // lets a call survive moving between radios: the bitrate follows the link down and back up
        // instead of asking a narrow one for a wide one's throughput.
        var linkBps = _radio?.LinkBandwidthBps ?? 0;
        var bitrate = OpusVoiceCodec.BitrateFor(linkBps);

        _codec?.Dispose();
        _codec = new OpusVoiceCodec(bitrateBps: bitrate);

        // Say which of two very different situations this is. Below the floor the bitrate has been
        // clamped UP — the encoder is being asked for more than the link can move — and the audio
        // will not arrive. That reads identically to a dead microphone in the log unless it is named
        // here, and it cost a full device session to work out once already.
        if (!OpusVoiceCodec.CanCarryCall(linkBps))
            T($"WARNING: on {_radio?.LinkRadio ?? "this radio"} at {linkBps / 1000}kbps — a call needs "
              + $"{OpusVoiceCodec.MinLinkBpsForCall / 1000}kbps, so expect silence until a wider radio takes over");
        else if (bitrate != OpusVoiceCodec.DefaultBitrateBps)
            T($"encoding at {bitrate / 1000}kbps — {_radio?.LinkRadio ?? "the radio"} reports {linkBps / 1000}kbps");
        _sequence = 0;

        // One queue and one sender per call. DropOldest is what keeps the microphone from outrunning
        // the radio without ever blocking the capture thread or growing without limit.
        _outgoing = Channel.CreateBounded<(Guid, byte[], uint)>(
            new BoundedChannelOptions(OutgoingFrameQueue)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
        _pump = Task.Run(() => PumpOutgoingAsync(_outgoing.Reader));

        var ok = await _audio
            .StartAsync(_codec.SampleRateHz, _codec.FrameDurationMs, cancellationToken)
            .ConfigureAwait(false);

        if (!ok) T($"could not open the microphone — {_audio.UnavailableReason ?? "unavailable"}");
        return ok;
    }

    /// <summary>
    /// A frame off the microphone: encode it and hand it to the sender.
    ///
    /// <para>
    /// This runs on the capture thread and the next frame is already being recorded, so it must not
    /// wait for the radio — that would stall the microphone and chop the speech. It used to say so by
    /// dropping the send task on the floor and moving on, one task per frame. At fifty frames a second
    /// against a radio that cannot carry them, the unfinished ones pile up: two phones each reached
    /// eight thread-pool threads and a quarter of a million bytes of queued audio within five seconds,
    /// and Android killed both processes for memory mid-call. The call connected every time and died
    /// before a word could be heard.
    /// </para>
    ///
    /// <para>
    /// So the frame goes into a small fixed queue that one sender drains. The trade the old comment
    /// described is now actually enforced: when the queue is full the OLDEST frame is discarded, never
    /// the newest, because a late voice frame is worth less than the one behind it. What it no longer
    /// does is pretend the radio can keep up.
    /// </para>
    /// </summary>
    private void OnMicrophoneFrame(short[] pcm)
    {
        if (Current is not { State: CallState.Connected } session || _codec is null || _voice is null) return;
        if (IsMuted) return;   // drop it here — a muted microphone should cost the radio nothing

        try
        {
            var encoded = _codec.Encode(pcm);
            var sequence = unchecked(_sequence++);
            // Bounded and DropOldest, so this never blocks and never grows.
            _outgoing?.Writer.TryWrite((session.Id, encoded, sequence));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "encoding a microphone frame");
        }
    }

    /// <summary>
    /// Put queued frames on the air, one at a time, for as long as the call lasts.
    ///
    /// <para>
    /// One frame in flight at a time is the point. The radio decides the pace; the microphone does not
    /// get to outrun it, and nothing accumulates while it is slow.
    /// </para>
    /// </summary>
    private async Task PumpOutgoingAsync(ChannelReader<(Guid CallId, byte[] Payload, uint Sequence)> frames)
    {
        // Say what is happening to the audio, once a second, in one line. A call where every frame is
        // quietly discarded looks exactly like a call that is working — the state machine is happy, the
        // microphone is open, the screen says connected, and nobody can hear anything. This path has
        // now hidden a failure three times in one day; it does not get to do it silently again.
        var sent = 0;
        var failed = 0;
        var slowestMs = 0L;
        var totalMs = 0L;
        string? lastFailure = null;
        var window = System.Diagnostics.Stopwatch.StartNew();

        void Report()
        {
            // Once a second, with the audio loop already awake — the picture is sized here rather than
            // on its own timer because this is the thread that knows whether frames are getting out.
            SizeMediaToLink();

            if (sent == 0 && failed == 0) return;
            // How long a frame takes to hand to the radio is the whole story: fifty a second means
            // twenty milliseconds each, and anything near a second means the audio cannot work no
            // matter how good the codec or how wide the link.
            var each = sent > 0 ? totalMs / sent : 0;
            T(failed == 0
                ? $"audio out: {sent} frames, {each}ms each (slowest {slowestMs}ms)"
                : $"audio out: {sent} sent ({each}ms each), {failed} not sent — {lastFailure ?? "the radio would not take them"}");
            sent = 0;
            failed = 0;
            slowestMs = 0;
            totalMs = 0;
            window.Restart();
        }

        try
        {
            await foreach (var frame in frames.ReadAllAsync().ConfigureAwait(false))
            {
                if (_voice is null) continue;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await _voice.SendFrameAsync(frame.CallId, frame.Payload, frame.Sequence)
                        .ConfigureAwait(false);
                    sent++;
                    totalMs += clock.ElapsedMilliseconds;
                    if (clock.ElapsedMilliseconds > slowestMs) slowestMs = clock.ElapsedMilliseconds;
                }
                catch (Exception ex)
                {
                    // One frame that would not go is not a reason to stop sending the rest — but it is
                    // a reason to say so.
                    failed++;
                    lastFailure = $"{ex.GetType().Name}: {ex.Message}";
                    _log.LogDebug(ex, "sending a voice frame");
                }

                if (window.ElapsedMilliseconds >= 1000) Report();
            }
        }
        catch (OperationCanceledException) { /* the call ended */ }

        Report();
    }

    private void OnFrameReceived(object? sender, VoiceFrame frame)
    {
        if (Current is not { State: CallState.Connected } session || session.Id != frame.CallId) return;
        if (_codec is null) return;

        try
        {
            _audio.Play(_codec.Decode(frame.EncodedPayload));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "decoding a voice frame");
        }
    }

    // ── The wire ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the call service on first use, wrapping the radio in a sender that seals everything.
    ///
    /// <para>
    /// Built lazily because a call needs the radio's identity to be settled, and on a phone that only
    /// happens once the app is properly up.
    /// </para>
    /// </summary>
    private IVoiceCallService Voice()
    {
        if (_voice is not null) return _voice;

        IMeshSender radioSender = _radio is not null
            ? new RadioMeshSender(_me.AetherTag, _radio)
            : new NullMeshSender(_me.AetherTag);

        _sender = new EncryptedMeshSender(radioSender, _signal, T);
        var voice = new VoiceCallService(_sender, new RoutingService(_sender));

        voice.IncomingCall += OnIncomingCall;
        voice.CallConnected += OnCallConnected;
        voice.CallEnded += OnCallEnded;
        voice.FrameReceived += OnFrameReceived;

        return _voice = voice;
    }

    /// <summary>Hand the call's media key over, sealed by the session like any other message.</summary>
    private async Task SendKeyAsync(string peerTag, byte[] master, CancellationToken cancellationToken)
    {
        if (_radio is null) return;

        try
        {
            var sealedKey = await _signal.EncryptAsync(peerTag, master, cancellationToken).ConfigureAwait(false);
            var serialized = AetherNet.Messaging.EncryptedPayloadCodec.Serialize(sealedKey);

            var payload = new byte[KeyMarker.Length + serialized.Length];
            Encoding.UTF8.GetBytes(KeyMarker).CopyTo(payload, 0);
            serialized.CopyTo(payload, KeyMarker.Length);

            await _radio.SendPacketAsync(PacketSerializer.Serialize(new MeshPacket
            {
                Type = PacketType.VoiceSignaling,
                SourceUhid = _me.AetherTag,
                DestinationUhid = peerTag,
                Ttl = 1,
                Payload = payload,
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not send the call key to {Peer}", peerTag);
        }
    }

    private void OnPacket(byte[] bytes)
    {
        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return; }

        // The key handoff rides an ordinary data packet, as the mesh-web and the Wi-Fi Direct broker do.
        if (packet.Type is PacketType.Data or PacketType.VideoFrame or PacketType.VoiceSignaling or PacketType.VoiceCall &&
            packet.Payload is { } p && p.Length > KeyMarker.Length &&
            Encoding.UTF8.GetString(p, 0, KeyMarker.Length) == KeyMarker)
        {
            _ = ReceiveKeyAsync(packet.SourceUhid, p);
            return;
        }

        // So does the camera going on and off. It has to be told, rather than inferred from frames
        // stopping: a phone cannot otherwise tell a switched-off camera from a dead link, and would
        // sit showing a frozen last frame as though the call were fine.
        if (packet.Type is PacketType.Data or PacketType.VideoFrame or PacketType.VoiceSignaling or PacketType.VoiceCall &&
            packet.Payload is { } v && v.Length > VideoMarker.Length &&
            Encoding.UTF8.GetString(v, 0, VideoMarker.Length) == VideoMarker)
        {
            _ = ReceiveVideoStateAsync(packet.SourceUhid, v);
            return;
        }

        if (packet.Type is not (PacketType.VoiceSignaling or PacketType.VoiceCall or PacketType.VideoFrame)) return;
        _ = HandleVoiceAsync(packet);
    }

    /// <summary>
    /// Take the media key for a call somebody is placing to us.
    ///
    /// <para>
    /// Only accepted if it opens under their ratchet — this key decides what audio this phone will
    /// accept as the other person's voice, so a forged one would let anyone in earshot speak as them.
    /// </para>
    /// </summary>
    private async Task ReceiveKeyAsync(string? from, byte[] payload)
    {
        if (string.IsNullOrEmpty(from)) return;

        try
        {
            var sealedKey = AetherNet.Messaging.EncryptedPayloadCodec.Deserialize(
                payload.AsSpan(KeyMarker.Length).ToArray());
            var master = await _signal.DecryptAsync(from, sealedKey).ConfigureAwait(false);
            if (master.Length != CallMediaCipher.KeyBytes) return;

            // Building the sender is what creates it if this is the first thing we have seen from them.
            Voice();
            KeyBothTracks(master);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(master);

            _radio?.IdentifyPeer(from);
            T($"got the call key from {from}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open a call key from {Peer}", from);
            T($"call key from {from} would not open");
        }
    }

    /// <summary>
    /// Open an inbound voice packet and hand it to the call service.
    ///
    /// <para>
    /// The sender is named by the packet header, which is a claim — but the payload will only open
    /// under that peer's ratchet, so a packet claiming to be from someone it is not simply fails to
    /// decrypt and is dropped. That is the same rule the chat path follows.
    /// </para>
    /// </summary>
    private async Task HandleVoiceAsync(MeshPacket packet)
    {
        var from = packet.SourceUhid;
        if (string.IsNullOrEmpty(from)) return;

        // Media opens under the call's own key; signalling opens under the ratchet. Frames must not go
        // near the ratchet — fifty a second outrun it and every one of them fails.
        MeshPacket? opened;
        if (packet.Type == PacketType.VideoFrame)
        {
            // Handed straight on, still encoded. Decoding belongs to whatever has a screen, and doing
            // it here would put a decoder on the receive path of every host that has none.
            Voice();
            opened = _sender!.OpenMedia(packet);
            if (opened?.Payload is not { Length: > 0 } frame) return;

            VideoFrameReceived?.Invoke(frame);
            _video?.Play(from, frame);
            return;
        }

        if (packet.Type == PacketType.VoiceCall)
        {
            Voice();
            opened = _sender!.OpenMedia(packet);
            if (opened is null) return;      // a lost frame is ordinary; not worth a line each time
        }
        else
        {
            // Deliberately no HasSession check here.
            //
            // Sessions are not symmetric while one is being built. The caller establishes theirs from
            // the callee's pre-key bundle and can encrypt immediately; the callee has nothing until it
            // *receives* that first message, because processing the pre-key material is what creates
            // the responder's side. Gating on HasSession therefore drops the one message that would
            // have created the session — and the call rings out while the offer sits discarded. The
            // chat path never had this check, which is why messages worked and calls did not.
            string? why = null;
            opened = await EncryptedMeshSender
                .UnsealAsync(packet, _signal, from, CancellationToken.None, r => why = r)
                .ConfigureAwait(false);
            if (opened is null)
            {
                T($"call signalling from {from} dropped — {why ?? "the payload would not open"}" +
                  $" (session={_signal.HasSession(from)})");

                // A tag mismatch with a session present means the two sides hold sessions that do not
                // agree — both established one as initiator, so each seals under a root key the other
                // has never seen. It is not recoverable by retrying; the dead session has to go and a
                // new one be built from a fresh bundle. Chat has done this for months, which is the
                // only reason sending a message first appeared to make calling work.
                if (_chat is not null && why?.Contains("AuthenticationTagMismatch", StringComparison.Ordinal) == true)
                {
                    T($"repairing the session with {from} and trying the call again");
                    await _chat.RepairAsync(from).ConfigureAwait(false);
                }
                return;
            }
        }

        // Now that a payload from them has opened, the radio can stop calling them a wire address.
        _radio?.IdentifyPeer(from);

        try { await Voice().HandleAsync(opened).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "handling a voice packet"); }
    }

    private void OnIncomingCall(object? sender, VoiceCallSession session)
    {
        // One call at a time. A second offer while busy is declined rather than silently ignored, so
        // the caller hears something instead of ringing out.
        if (Current is not null)
        {
            _ = Voice().DeclineAsync(session.Id, HangupReason.Busy);
            return;
        }

        Current = session;
        _startedAt = DateTime.UtcNow;   // a call that is never answered still happened
        T($"incoming call from {session.CallerUhid}");
        _audio.StartRinging(session.CallerUhid);
        Raise();
    }

    private void OnCallConnected(object? sender, VoiceCallSession session)
    {
        // Watched on device 2026-08-20: the ANSWERER's running time reset to zero about three minutes
        // into a call that was otherwise healthy at fifty frames a second, while the caller's stayed
        // correct. None of the places Current is assigned should be reachable mid-call, so this says
        // out loud when one is, rather than leaving the next run to guess again.
        if (Current is { } existing && existing.Id == session.Id && existing.ConnectedAt != session.ConnectedAt)
            T($"connect time MOVED {existing.ConnectedAt:HH:mm:ss} to {session.ConnectedAt:HH:mm:ss} on call {session.Id}");
        else if (Current is { } other && other.Id != session.Id)
            T($"call REPLACED {other.Id} with {session.Id}");

        Current = session;
        _ = ConnectCallerAudioAsync(session);
    }

    /// <summary>The caller only opens the microphone once the callee has actually picked up.</summary>
    private async Task ConnectCallerAudioAsync(VoiceCallSession session)
    {
        // The key is minted by the ANSWERING side and travels with the answer, so by the time this
        // runs the cipher is already in place. Nothing to mint here — and nothing to wait for, which
        // is the point: this side can speak the moment it hears they picked up.
        if (!await StartAudioAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await HangUpAsync(HangupReason.NetworkFailure).ConfigureAwait(false);
            return;
        }

        T($"connected to {PeerTag} — {session.Codec} at {session.SampleRateHz}Hz");
        Raise();
    }

    private void OnCallEnded(object? sender, VoiceCallSession session)
    {
        if (Current?.Id != session.Id) return;
        T($"call ended ({session.HangupReason})");
        _ = EndAsync();
    }

    /// <summary>
    /// Write the call down, once, as it ends.
    ///
    /// <para>
    /// Recorded here rather than at each of the several places a call can finish — hung up, declined,
    /// rung out, network failure — because every one of them ends here, and a history that misses the
    /// unusual endings is exactly the history nobody can trust. A call that never connected is stored
    /// with ConnectedMs 0, which is what makes it missed; there is no separate flag to fall out of
    /// step.
    /// </para>
    /// </summary>
    private void RecordCall()
    {
        if (_store is null || Current is not { } call || _startedAt is not { } started) return;
        _startedAt = null;   // one row per call, however many ways it ends

        try
        {
            var peer = call.RemoteUhid(_me.AetherTag);
            if (string.IsNullOrEmpty(peer)) return;

            _store.SaveCall(new AetherNet.Sample.Shared.Data.CallRecord(
                Id: call.Id.ToString(),
                PeerTag: peer,
                Outgoing: IAmTheCaller,
                StartedMs: new DateTimeOffset(started, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                ConnectedMs: call.ConnectedAt is { } c
                    ? new DateTimeOffset(c, TimeSpan.Zero).ToUnixTimeMilliseconds()
                    : 0,
                EndedMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Reason: call.HangupReason.ToString()));
        }
        catch (Exception ex)
        {
            // Never let bookkeeping take down the teardown of a call.
            _log.LogDebug(ex, "recording the call");
        }
    }

    private async Task EndAsync()
    {
        RecordCall();             // write it down before the state it describes is cleared
        StopRinging();
        _audio.StopRinging();     // answered, declined, or they gave up — either way, stop making noise
        _audio.ReleaseCall();     // and let the phone go back to being an ordinary phone
        Current = null;
        try { await _audio.StopAsync().ConfigureAwait(false); } catch { }

        // Close the queue and let the sender finish. Anything still waiting is stale speech from a
        // call that is over, so it goes with it.
        _outgoing?.Writer.TryComplete();
        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { /* a wedged radio must not hold up hanging up */ }
            catch (Exception ex) { _log.LogDebug(ex, "draining the outgoing voice queue"); }
        }
        _outgoing = null;
        _pump = null;

        _codec?.Dispose();
        _codec = null;

        // The media key belongs to one call and must not outlive it — a fresh call gets a fresh key,
        // so a recording of this one can never be replayed into the next.
        if (_sender is not null)
        {
            _sender.Media?.Dispose();
            _sender.Media = null;
            _sender.Video?.Dispose();
            _sender.Video = null;
        }

        // The cameras go with it. Leaving these set means the next call opens believing it is already
        // showing video, and the screen draws over a stream nobody is sending. The surfaces come down
        // too — a video overlay outliving its call covers the whole app with a black rectangle.
        VideoOn = false;
        TheirVideoOn = false;
        IsMinimised = false;   // a new call must never open behind the last one's bar
        if (_video is not null)
        {
            _video.FrameEncoded -= OnEncodedFrame;
            try { await _video.StopAsync().ConfigureAwait(false); } catch { /* the call is over anyway */ }
        }

        Raise();
    }

    private void T(string message)
    {
        Trace?.Invoke(message);
        _log.LogInformation("{Message}", message);
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_radio is not null) _radio.PacketReceived -= OnPacket;
        _audio.FrameCaptured -= OnMicrophoneFrame;
        _codec?.Dispose();
    }

    /// <summary>
    /// Size the picture and the voice to what the link is actually doing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Video first and hardest. It does not degrade — an encoder given less than it needs produces
    /// frames too late to show, and the decoder then waits on a keyframe while the audio sharing the
    /// link breaks up too. Cutting the picture is what buys the voice room, so it is cut first.
    /// </para>
    /// <para>
    /// Nothing here consults a bandwidth figure. Strain rises when sends start queueing, which happens
    /// before anything is lost and needs no capacity to be known — and every capacity figure this app
    /// has trusted turned out to be arithmetic wearing a measurement's clothes.
    /// </para>
    /// </remarks>
    private void SizeMediaToLink()
    {
        if (_radio is null) return;

        // Once a second for as long as the call lasts, so the link is never put away underneath one.
        _fastRadio?.Wake();

        var strain = _radio.LinkStrain;

        // Two people on a 1:1 call: this phone's camera and theirs.
        _video?.SizeToLink(strain, people: 2);

        var wanted = MediaBitrate.Voice(_voiceBitrateBps, strain);
        if (wanted == _voiceBitrateBps) return;

        _voiceBitrateBps = wanted;
        T($"voice → {wanted / 1000}k (strain {strain:0.00})");
    }

    /// <summary>What the voice codec is being asked for. Starts at its best and gives ground last.</summary>
    private int _voiceBitrateBps = MediaBitrate.VoiceCeilingBps;
}

/// <summary>Stands in when there is no radio at all, so the Web head can construct the service.</summary>
internal sealed class NullMeshSender(string localUhid) : IMeshSender
{
    public string LocalUhid { get; } = localUhid;
    public string? LocalGeohash => null;
    public IReadOnlyList<AetherNet.Models.PeerInfo> GetConnectedPeers() => [];
    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
    public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

}
