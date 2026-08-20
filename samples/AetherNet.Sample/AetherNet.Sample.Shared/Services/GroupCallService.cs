// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Data;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// A call with more than two people in it.
///
/// <para>
/// Built the same way group chat is: <b>a group call is several 1:1 calls</b>. Everyone encodes once
/// and sends the result to each other participant separately, sealed under a key derived from their
/// own tag. There is no group key that a departing member keeps working with, no server mixing
/// anything, and no participant who is special.
/// </para>
///
/// <para>
/// That shape is also what makes the cost honest, and the cost is the whole story here. Outbound
/// bandwidth grows with the group, and so does the number of decoders — and it is the decoders that
/// run out first. PROTOCOL_SPEC §10.10 works this through: mid-range phones expose two to four
/// concurrent H.264 decoders, so group VIDEO caps at around three to five people whatever the radio
/// says, while group VOICE is cheap enough to be limited by nothing in practice. The two are
/// therefore capped separately, and the video cap is discovered from the hardware rather than assumed.
/// </para>
///
/// <para>
/// Media rides ordinary <see cref="PacketType.Data"/> packets behind a marker rather than the voice
/// and video packet types. Those belong to the 1:1 <see cref="CallService"/>, which opens them under
/// its own key; a group frame arriving there would fail to decrypt and look exactly like a tampered
/// packet. Four bytes of marker keeps the two paths from ever being confused for one another.
/// </para>
/// </summary>
public sealed class GroupCallService : IDisposable
{
    /// <summary>Signalling: invite, accept, decline, leave, camera.</summary>
    private const string SignalMarker = "GCS1";

    /// <summary>One frame of somebody talking.</summary>
    private const string VoiceMarker = "GCA1";

    /// <summary>One frame of somebody on camera.</summary>
    private const string VideoMarker = "GCV1";

    private readonly IIdentityService _me;
    private readonly ISignalProtocolService _signal;
    private readonly IAudioIo _audio;
    private readonly IVideoIo? _video;
    private readonly IRadioMesh? _radio;
    private readonly AetherStore? _store;
    private readonly ILogger _log;

    private readonly ConcurrentDictionary<string, Participant> _participants = new(StringComparer.Ordinal);

    private byte[]? _master;
    private OpusVoiceCodec? _codec;
    private AudioMixer? _mixer;
    private Timer? _pump;
    private bool _disposed;

    /// <summary>One other person in the call, and the two ciphers that read them.</summary>
    private sealed class Participant(string tag) : IDisposable
    {
        public string Tag { get; } = tag;

        /// <summary>Null until they accept — an invited person is not yet a participant.</summary>
        public CallMediaCipher? Voice { get; set; }

        public CallMediaCipher? Video { get; set; }

        /// <summary>Whether they have accepted, as opposed to merely having been asked.</summary>
        public bool Joined { get; set; }

        /// <summary>Whether their camera is on.</summary>
        public bool CameraOn { get; set; }

        /// <summary>When they were last heard from, which is how the video cap picks who to drop.</summary>
        public DateTime LastHeard { get; set; } = DateTime.UtcNow;

        /// <summary>Whether this phone is currently decoding their video, as opposed to only their voice.</summary>
        public bool ShowingVideo { get; set; }

        public void Dispose()
        {
            Voice?.Dispose();
            Video?.Dispose();
        }
    }

    public GroupCallService(
        IIdentityService me,
        ISignalProtocolService signal,
        IAudioIo audio,
        IVideoIo? video = null,
        IRadioMesh? radio = null,
        AetherStore? store = null,
        ILoggerFactory? loggerFactory = null)
    {
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _video = video;
        _radio = radio;
        _store = store;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<GroupCallService>();

        if (_radio is not null) _radio.PacketReceived += OnPacket;
        _audio.FrameCaptured += OnMicrophoneFrame;
    }

    /// <summary>Running commentary, for the radio log.</summary>
    public event Action<string>? Trace;

    /// <summary>Anything changed that a screen would want to redraw.</summary>
    public event Action? Changed;

    /// <summary>Someone is calling this group. Raised once, when the first invite arrives.</summary>
    public event Action<string>? IncomingCall;

    /// <summary>The group being called, or null when there is no group call.</summary>
    public string? GroupId { get; private set; }

    /// <summary>Which call, so a second invitation cannot be mistaken for the first.</summary>
    public string? CallId { get; private set; }

    /// <summary>True once this phone is in the call, as opposed to merely being invited to it.</summary>
    public bool Joined { get; private set; }

    /// <summary>True while a call is ringing here and has not been answered.</summary>
    public bool IsRinging { get; private set; }

    /// <summary>True while this phone's camera is on.</summary>
    public bool CameraOn { get; private set; }

    /// <summary>Everyone in the call who has actually joined, this phone included.</summary>
    public IReadOnlyList<string> Participants =>
        [.. _participants.Values.Where(p => p.Joined).Select(p => p.Tag).Order(StringComparer.Ordinal),
            .. Joined ? new[] { _me.AetherTag } : []];

    /// <summary>Who has their camera on right now.</summary>
    public IReadOnlyList<string> OnCamera =>
        [.. _participants.Values.Where(p => p is { Joined: true, CameraOn: true }).Select(p => p.Tag)];

    /// <summary>
    /// How many people this phone can show video for, discovered from the hardware.
    ///
    /// <para>
    /// Asked of the video path rather than assumed, because §10.10 makes that a requirement: a decoder
    /// that will not configure shows nothing at all, so a phone that guesses high does not degrade —
    /// it displays a blank rectangle and gives no reason.
    /// </para>
    /// </summary>
    public int VideoCap => _video?.MaxConcurrentStreams ?? 0;

    /// <summary>
    /// Whether video is worth offering: enough radio for everyone in the call, and a decoder each.
    ///
    /// <para>
    /// Voice has no equivalent gate. It is two orders of magnitude cheaper, and a group voice call is
    /// limited by nothing these phones actually run into.
    /// </para>
    /// </summary>
    public bool CanSendVideo =>
        Joined &&
        _video is { IsPresent: true } &&
        Participants.Count <= VideoCap + 1 &&
        (_radio?.LinkBandwidthBps ?? 0) >= VideoBudget.RequiredBps(Participants.Count);

    /// <summary>Why the camera is not on offer, in plain words — or null when it is.</summary>
    public string? CannotSendVideoReason
    {
        get
        {
            if (!Joined) return "not in a call";
            if (_video is not { IsPresent: true }) return _video?.UnavailableReason ?? "this device has no camera";

            var people = Participants.Count;
            if (people > VideoCap + 1)
                return $"this phone can show video for {VideoCap} at a time, and there are {people - 1} others";

            var needed = VideoBudget.RequiredBps(people);
            return (_radio?.LinkBandwidthBps ?? 0) < needed
                ? $"{_radio?.LinkRadio ?? "this radio"} cannot carry video for {people} — voice only"
                : null;
        }
    }

    // ── Starting and joining ──────────────────────────────────────────────────

    /// <summary>
    /// Call a group.
    ///
    /// <para>
    /// The master key is minted here and sent to each member inside their own session, exactly as the
    /// 1:1 call key is. Everyone derives their own sealing key from it and their own tag, so no two
    /// participants ever seal under the same key however many join.
    /// </para>
    /// </summary>
    public async Task<bool> StartAsync(string groupId, CancellationToken cancellationToken = default)
    {
        if (_disposed || Joined || IsRinging || string.IsNullOrEmpty(groupId)) return false;
        if (_store is null || _radio is null) return false;

        var members = _store.GetGroupMembers(groupId).Where(m => m != _me.AetherTag).ToArray();
        if (members.Length == 0)
        {
            T("nobody else is in that group");
            return false;
        }

        if (!await _audio.EnsurePermissionAsync().ConfigureAwait(false))
        {
            T("cannot call — " + (_audio.UnavailableReason ?? "no microphone"));
            return false;
        }

        GroupId = groupId;
        CallId = Guid.NewGuid().ToString("N");
        _master = CallMediaCipher.NewMasterKey();

        // The starter is in their own call from the outset. Waiting for an accept from yourself is
        // the kind of thing that leaves a call with nobody in it.
        Joined = true;

        foreach (var m in members) _participants[m] = new Participant(m);

        if (!await StartAudioAsync(cancellationToken).ConfigureAwait(false))
        {
            await LeaveAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var sent = 0;
        foreach (var m in members)
        {
            if (await SendSignalAsync(m, GroupCallEnvelope.Invite, cancellationToken).ConfigureAwait(false)) sent++;
        }

        T($"calling {groupId} — invited {sent} of {members.Length}");
        Raise();

        if (sent == 0)
        {
            // Nothing reached anybody. Saying "calling" over a radio that took none of the invitations
            // is the same lie the 1:1 path used to tell.
            T("nobody could be reached");
            await LeaveAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>Answer a ringing group call.</summary>
    public async Task<bool> JoinAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || Joined || !IsRinging || _master is null) return false;

        if (!await _audio.EnsurePermissionAsync().ConfigureAwait(false))
        {
            T("cannot join — " + (_audio.UnavailableReason ?? "no microphone"));
            return false;
        }

        _audio.StopRinging();
        IsRinging = false;
        Joined = true;

        if (!await StartAudioAsync(cancellationToken).ConfigureAwait(false))
        {
            await LeaveAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Tell everyone, not only whoever invited us. Every phone keeps its own membership list, so a
        // joiner who told only the starter would be invisible to the rest of the call.
        foreach (var p in _participants.Values.ToArray())
            await SendSignalAsync(p.Tag, GroupCallEnvelope.Accept, cancellationToken).ConfigureAwait(false);

        T("joined");
        Raise();
        return true;
    }

    /// <summary>Turn down a ringing group call. The call carries on without this phone.</summary>
    public async Task DeclineAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRinging) return;

        _audio.StopRinging();

        foreach (var p in _participants.Values.ToArray())
            await SendSignalAsync(p.Tag, GroupCallEnvelope.Decline, cancellationToken).ConfigureAwait(false);

        await EndAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Leave. Everyone else stays in the call.
    ///
    /// <para>
    /// The difference from a 1:1 hang-up, and the reason there is no "end the call for everybody": a
    /// group conversation is not the starter's to close. It ends when the last person leaves it.
    /// </para>
    /// </summary>
    public async Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        if (GroupId is null) return;

        foreach (var p in _participants.Values.ToArray())
            await SendSignalAsync(p.Tag, GroupCallEnvelope.Leave, cancellationToken).ConfigureAwait(false);

        await EndAsync().ConfigureAwait(false);
    }

    // ── The camera ────────────────────────────────────────────────────────────

    /// <summary>Turn this phone's camera on or off, and tell everyone in the call.</summary>
    public async Task<bool> SetCameraAsync(bool on, CancellationToken cancellationToken = default)
    {
        if (!Joined) return false;
        if (on && !CanSendVideo)
        {
            T("cannot send video — " + CannotSendVideoReason);
            return false;
        }

        if (CameraOn == on) return true;

        if (on)
        {
            if (_video is null) return false;
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

            _video.FrameEncoded += OnEncodedVideo;
        }
        else if (_video is not null)
        {
            _video.FrameEncoded -= OnEncodedVideo;
            if (OnCamera.Count == 0) await _video.StopAsync().ConfigureAwait(false);
        }

        CameraOn = on;

        foreach (var p in _participants.Values.Where(p => p.Joined).ToArray())
            await SendSignalAsync(p.Tag, GroupCallEnvelope.Camera, cancellationToken).ConfigureAwait(false);

        Raise();
        return true;
    }

    // ── Media out ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A frame off the microphone: encode once, seal per person, send to each.
    ///
    /// <para>
    /// Encoding once and sealing many times is the whole economy of this. Encoding per recipient would
    /// multiply the most expensive step by the size of the call for no benefit — the bytes are
    /// identical, only the seal differs.
    /// </para>
    /// </summary>
    private void OnMicrophoneFrame(short[] pcm)
    {
        if (!Joined || _codec is null || _audio is null) return;

        try
        {
            var encoded = _codec.Encode(pcm);
            _ = FanOutAsync(VoiceMarker, encoded, video: false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "group voice encode");
        }
    }

    private void OnEncodedVideo(byte[] frame)
    {
        if (!CameraOn) return;
        _ = FanOutAsync(VideoMarker, frame, video: true);
    }

    /// <summary>Send one already-encoded frame to everybody, each under their own key.</summary>
    private async Task FanOutAsync(string marker, byte[] frame, bool video)
    {
        if (_radio is null || frame.Length == 0) return;

        foreach (var p in _participants.Values)
        {
            if (!p.Joined) continue;

            var cipher = video ? p.Video : p.Voice;
            if (cipher is null) continue;

            try
            {
                var sealedFrame = cipher.Seal(frame);
                var payload = new byte[marker.Length + sealedFrame.Length];
                Encoding.UTF8.GetBytes(marker).CopyTo(payload, 0);
                sealedFrame.CopyTo(payload, marker.Length);

                await _radio.SendPacketAsync(PacketSerializer.Serialize(new MeshPacket
                {
                    Type = PacketType.Data,
                    SourceUhid = _me.AetherTag,
                    DestinationUhid = p.Tag,
                    Ttl = 1,           // one hop; a relayed group call is not a group call
                    Payload = payload,
                })).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "group frame to {Peer}", p.Tag);
            }
        }
    }

    // ── Media in ──────────────────────────────────────────────────────────────

    private void OnPacket(byte[] bytes)
    {
        if (_disposed) return;

        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return; }

        if (packet.Type != PacketType.Data || packet.Payload is not { } payload) return;
        if (payload.Length <= SignalMarker.Length) return;

        var marker = Encoding.UTF8.GetString(payload, 0, SignalMarker.Length);
        var from = packet.SourceUhid;
        if (string.IsNullOrEmpty(from)) return;

        switch (marker)
        {
            case SignalMarker: _ = OnSignalAsync(from, payload); break;
            case VoiceMarker: OnVoiceFrame(from, payload); break;
            case VideoMarker: OnVideoFrame(from, payload); break;
        }
    }

    private void OnVoiceFrame(string from, byte[] payload)
    {
        if (!Joined || _codec is null || _mixer is null) return;
        if (!_participants.TryGetValue(from, out var p) || p.Voice is null) return;

        try
        {
            if (p.Voice.Open(payload.AsSpan(VoiceMarker.Length)) is not { } frame) return;

            p.LastHeard = DateTime.UtcNow;

            // Decoded here and mixed rather than played, because there is one speaker and several
            // people. See AudioMixer — playing each stream as it lands makes every speaker interrupt
            // the last.
            _mixer.Offer(from, _codec.Decode(frame));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "group voice from {Peer}", from);
        }
    }

    private void OnVideoFrame(string from, byte[] payload)
    {
        if (!Joined || _video is null) return;
        if (!_participants.TryGetValue(from, out var p) || p.Video is null) return;
        if (!p.ShowingVideo) return;   // over the decoder cap; their voice still arrives

        try
        {
            if (p.Video.Open(payload.AsSpan(VideoMarker.Length)) is not { } frame) return;

            p.LastHeard = DateTime.UtcNow;
            _video.Play(from, frame);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "group video from {Peer}", from);
        }
    }

    // ── Signalling ────────────────────────────────────────────────────────────

    private async Task<bool> SendSignalAsync(string peerTag, string kind, CancellationToken cancellationToken)
    {
        if (_radio is null || GroupId is null || CallId is null) return false;

        try
        {
            var envelope = new GroupCallEnvelope
            {
                Kind = kind,
                GroupId = GroupId,
                CallId = CallId,
                Sender = _me.AetherTag,
                CameraOn = CameraOn,
                Participants = [.. Participants],

                // The key travels only with an invitation, and only inside the recipient's own
                // session. Anyone holding it can decrypt every stream in the call.
                MasterKey = kind == GroupCallEnvelope.Invite && _master is not null
                    ? Convert.ToBase64String(_master)
                    : null,
            };

            var sealedBody = await _signal
                .EncryptAsync(peerTag, envelope.ToBytes(), cancellationToken)
                .ConfigureAwait(false);

            var body = AetherNet.Messaging.EncryptedPayloadCodec.Serialize(sealedBody);
            var payload = new byte[SignalMarker.Length + body.Length];
            Encoding.UTF8.GetBytes(SignalMarker).CopyTo(payload, 0);
            body.CopyTo(payload, SignalMarker.Length);

            return await _radio.SendPacketAsync(PacketSerializer.Serialize(new MeshPacket
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
            _log.LogDebug(ex, "group call signal {Kind} to {Peer}", kind, peerTag);
            return false;
        }
    }

    private async Task OnSignalAsync(string from, byte[] payload)
    {
        GroupCallEnvelope? envelope;
        try
        {
            var sealedBody = AetherNet.Messaging.EncryptedPayloadCodec.Deserialize(
                payload.AsSpan(SignalMarker.Length).ToArray());
            var body = await _signal.DecryptAsync(from, sealedBody).ConfigureAwait(false);
            envelope = GroupCallEnvelope.Parse(body);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "group call signal from {Peer} would not open", from);
            return;
        }

        if (envelope is null) return;

        // A signal claiming to be from someone else is ignored. The session says who sent it, and
        // that is the only thing that can be trusted here.
        if (!string.Equals(envelope.Sender, from, StringComparison.Ordinal)) return;

        _radio?.IdentifyPeer(from);

        switch (envelope.Kind)
        {
            case GroupCallEnvelope.Invite: await OnInviteAsync(from, envelope).ConfigureAwait(false); break;
            case GroupCallEnvelope.Accept: OnAccept(from, envelope); break;
            case GroupCallEnvelope.Decline: OnGone(from, envelope, "declined"); break;
            case GroupCallEnvelope.Leave: OnGone(from, envelope, "left"); break;
            case GroupCallEnvelope.Camera: OnCameraChanged(from, envelope); break;
        }
    }

    private async Task OnInviteAsync(string from, GroupCallEnvelope envelope)
    {
        // Already in this call — this is a second invitation from someone who has not heard our
        // accept, so re-send it rather than treating it as a new call.
        if (Joined && CallId == envelope.CallId)
        {
            await SendSignalAsync(from, GroupCallEnvelope.Accept, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (Joined || IsRinging) return;                        // busy in another call
        if (envelope.MasterKey is null) return;

        byte[] master;
        try { master = Convert.FromBase64String(envelope.MasterKey); }
        catch (FormatException) { return; }

        if (master.Length != CallMediaCipher.KeyBytes) return;

        GroupId = envelope.GroupId;
        CallId = envelope.CallId;
        _master = master;

        // Everyone the inviter says is in the call, plus the inviter. A joiner who only knew the
        // inviter would be deaf to everyone else until they each happened to speak.
        foreach (var tag in (envelope.Participants ?? []).Append(from).Distinct(StringComparer.Ordinal))
        {
            if (tag == _me.AetherTag) continue;
            _participants[tag] = new Participant(tag) { Joined = tag == from };
        }

        KeyEveryone();

        IsRinging = true;
        _audio.StartRinging(from);
        IncomingCall?.Invoke(envelope.GroupId);
        T($"{from} is calling {envelope.GroupId}");
        Raise();
    }

    private void OnAccept(string from, GroupCallEnvelope envelope)
    {
        if (CallId != envelope.CallId) return;

        var p = _participants.GetOrAdd(from, t => new Participant(t));
        p.Joined = true;
        p.LastHeard = DateTime.UtcNow;

        // Someone we had never been told about — they were invited by a third phone. Key them now.
        if (p.Voice is null) KeyOne(p);

        // And learn about anybody else they know is here, for the same reason.
        foreach (var tag in envelope.Participants ?? [])
        {
            if (tag == _me.AetherTag || _participants.ContainsKey(tag)) continue;
            var other = _participants.GetOrAdd(tag, t => new Participant(t));
            KeyOne(other);
        }

        ChooseWhoToShow();
        T($"{from} joined");
        Raise();
    }

    private void OnGone(string from, GroupCallEnvelope envelope, string what)
    {
        if (CallId != envelope.CallId) return;

        if (_participants.TryRemove(from, out var p))
        {
            p.Dispose();
            _mixer?.Forget(from);
            _video?.Forget(from);
        }

        T($"{from} {what}");

        // A call with nobody left in it is over. Without this, the last person sits in an empty call
        // holding a microphone open.
        if (Joined && _participants.Values.All(x => !x.Joined))
        {
            T("everyone else has gone");
            _ = EndAsync();
            return;
        }

        ChooseWhoToShow();
        Raise();
    }

    private void OnCameraChanged(string from, GroupCallEnvelope envelope)
    {
        if (CallId != envelope.CallId) return;
        if (!_participants.TryGetValue(from, out var p)) return;

        p.CameraOn = envelope.CameraOn;
        p.LastHeard = DateTime.UtcNow;

        ChooseWhoToShow();
        T($"{from}'s camera is {(envelope.CameraOn ? "on" : "off")}");
        Raise();
    }

    // ── Keys, and who gets shown ──────────────────────────────────────────────

    private void KeyEveryone()
    {
        foreach (var p in _participants.Values) KeyOne(p);
    }

    /// <summary>
    /// Give one participant their pair of ciphers.
    ///
    /// <para>
    /// Both tracks at once, and while their camera is still off, for the same reason the 1:1 call
    /// keys both: a camera switched on mid-call would otherwise put its first frames ahead of their
    /// key, which reads as a camera that does not work.
    /// </para>
    /// </summary>
    private void KeyOne(Participant p)
    {
        if (_master is null || p.Tag == _me.AetherTag) return;

        p.Voice?.Dispose();
        p.Video?.Dispose();
        p.Voice = CallMediaCipher.ForGroup(_master, _me.AetherTag, p.Tag);
        p.Video = CallMediaCipher.ForGroup(_master, _me.AetherTag, p.Tag, video: true);
    }

    /// <summary>
    /// Pick whose video this phone will decode.
    ///
    /// <para>
    /// The cap is the number of hardware decoders, and going over it does not degrade — a decoder
    /// that will not configure shows nothing at all, with no error anyone can act on. So the choice is
    /// made here, deliberately, and the people who do not fit stay in the call as voice.
    /// </para>
    ///
    /// <para>
    /// Most recently heard wins, which in a conversation means whoever is talking. It is the same rule
    /// every video-conferencing product settles on, for the same reason: the person speaking is the
    /// person you want to see.
    /// </para>
    /// </summary>
    private void ChooseWhoToShow()
    {
        var cap = VideoCap;

        var show = _participants.Values
            .Where(p => p is { Joined: true, CameraOn: true })
            .OrderByDescending(p => p.LastHeard)
            .Take(Math.Max(0, cap))
            .Select(p => p.Tag)
            .ToHashSet(StringComparer.Ordinal);

        var dropped = 0;
        foreach (var p in _participants.Values)
        {
            var wanted = show.Contains(p.Tag);
            if (p.ShowingVideo && !wanted) { _video?.Forget(p.Tag); dropped++; }
            p.ShowingVideo = wanted;
        }

        if (dropped > 0)
            T($"showing {show.Count} of {OnCamera.Count} cameras — this phone can decode {cap} at once");
    }

    // ── Audio path ────────────────────────────────────────────────────────────

    private async Task<bool> StartAudioAsync(CancellationToken cancellationToken)
    {
        _audio.HoldCall(GroupId);

        // Sized to the radio and to the size of the call: a group shares one link between several
        // streams, so each stream gets less of it than a 1:1 call would.
        var linkBps = _radio?.LinkBandwidthBps ?? 0;
        var people = Math.Max(2, _participants.Count + 1);
        var bitrate = OpusVoiceCodec.BitrateFor(linkBps <= 0 ? 0 : linkBps / (people - 1));

        _codec?.Dispose();
        _codec = new OpusVoiceCodec(bitrateBps: bitrate);

        _mixer?.Dispose();
        _mixer = new AudioMixer(_codec.FrameSamples);

        KeyEveryone();

        var ok = await _audio
            .StartAsync(_codec.SampleRateHz, _codec.FrameDurationMs, cancellationToken)
            .ConfigureAwait(false);

        if (!ok)
        {
            T("could not open the microphone — " + (_audio.UnavailableReason ?? "unavailable"));
            return false;
        }

        // The mixer is pumped on the frame clock rather than on arrival, so several people talking at
        // once come out summed instead of interrupting each other.
        _pump = new Timer(_ => PumpMix(), null, _codec.FrameDurationMs, _codec.FrameDurationMs);

        T($"group audio up at {bitrate / 1000}kbps for {people} people");
        return true;
    }

    private void PumpMix()
    {
        if (_disposed || !Joined) return;

        try
        {
            if (_mixer?.Mix() is { } frame) _audio.Play(frame);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "group mix");
        }
    }

    // ── Ending ────────────────────────────────────────────────────────────────

    private async Task EndAsync()
    {
        Joined = false;
        IsRinging = false;
        CameraOn = false;
        GroupId = null;
        CallId = null;

        _audio.StopRinging();
        _audio.ReleaseCall();
        try { await _audio.StopAsync().ConfigureAwait(false); } catch { /* already stopped */ }

        _pump?.Dispose();
        _pump = null;

        if (_video is not null)
        {
            _video.FrameEncoded -= OnEncodedVideo;
            try { await _video.StopAsync().ConfigureAwait(false); } catch { /* the call is over */ }
        }

        foreach (var p in _participants.Values) p.Dispose();
        _participants.Clear();

        _mixer?.Dispose();
        _mixer = null;
        _codec?.Dispose();
        _codec = null;

        // The master belongs to one call. A fresh call gets a fresh one, so a recording of this one
        // can never be replayed into the next.
        if (_master is not null)
        {
            CryptographicOperations.ZeroMemory(_master);
            _master = null;
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

        _audio.FrameCaptured -= OnMicrophoneFrame;
        if (_radio is not null) _radio.PacketReceived -= OnPacket;

        try { EndAsync().GetAwaiter().GetResult(); } catch { /* tearing down */ }
    }
}
