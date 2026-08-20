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
    private readonly WifiDirectBroker? _wifiDirect;

    /// <summary>
    /// Chat, only for its session repair. A call hits exactly the wall a message does, and duplicating
    /// the recovery would mean two implementations of the trickiest code in the app drifting apart.
    /// </summary>
    private readonly ChatService? _chat;
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
        ChatService? chat = null,
        WifiDirectBroker? wifiDirect = null,
        IRadioMesh? radio = null,
        ILoggerFactory? loggerFactory = null)
    {
        _chat = chat;
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _wifiDirect = wifiDirect;
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
    /// Whether a call is worth offering. Deliberately does <b>not</b> require the microphone permission
    /// — that is asked for on the tap, and gating the button on it would hide the only way to grant it.
    /// </summary>
    public bool CanCall => _audio.IsPresent && _radio is { IsLinked: true };

    /// <summary>Why a call cannot be placed right now, in plain words — or null when it can.</summary>
    public string? CannotCallReason =>
        !_audio.IsPresent ? _audio.UnavailableReason ?? "no microphone"
        : _radio is not { IsLinked: true } ? "no phone is connected to this one"
        : null;

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

        // Voice needs about 24 kbps and BLE carries about five, so the call brings up the wide pipe on
        // its way out. Not fatal if it does not come up — the call still rings, and if the far end can
        // be reached at all the signalling gets there; only the audio would struggle.
        if (_wifiDirect is { IsUp: false })
            _ = _wifiDirect.BringUpAsync(peerTag, cancellationToken);

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

        // The answering side does NOT bring the pipe up. The caller hosts and its key is already on
        // its way over BLE; hosting here as well would create a second group and neither phone would
        // be in the other's.

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

    // ── The audio path ────────────────────────────────────────────────────────

    private async Task<bool> StartAudioAsync(CancellationToken cancellationToken)
    {
        // Claim the phone for the duration of the call. Without this Android takes the microphone back
        // the moment the person opens anything else — the call goes silent, or the process is killed
        // outright, and the far end is never told.
        _audio.HoldCall(PeerTag);

        _codec?.Dispose();
        _codec = new OpusVoiceCodec();
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
                Type = PacketType.Data,
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
        if (packet.Type == PacketType.Data &&
            packet.Payload is { } p && p.Length > KeyMarker.Length &&
            Encoding.UTF8.GetString(p, 0, KeyMarker.Length) == KeyMarker)
        {
            _ = ReceiveKeyAsync(packet.SourceUhid, p);
            return;
        }

        if (packet.Type is not (PacketType.VoiceSignaling or PacketType.VoiceCall)) return;
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
            _sender!.Media?.Dispose();
            _sender.Media = new CallMediaCipher(master, iAmTheCaller: false);
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
        T($"incoming call from {session.CallerUhid}");
        _audio.StartRinging(session.CallerUhid);
        Raise();
    }

    private void OnCallConnected(object? sender, VoiceCallSession session)
    {
        Current = session;
        _ = ConnectCallerAudioAsync(session);
    }

    /// <summary>The caller only opens the microphone once the callee has actually picked up.</summary>
    private async Task ConnectCallerAudioAsync(VoiceCallSession session)
    {
        // They answered, so their session is live and a second message will open. Mint the call's key
        // and hand it over before a single frame goes out — frames are sealed with it, and one sent
        // beforehand would simply be dropped at the far end.
        var peer = session.RemoteUhid(_me.AetherTag);
        var master = CallMediaCipher.NewMasterKey();
        _sender!.Media?.Dispose();
        _sender.Media = new CallMediaCipher(master, iAmTheCaller: true);
        await SendKeyAsync(peer, master, CancellationToken.None).ConfigureAwait(false);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(master);

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

    private async Task EndAsync()
    {
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
