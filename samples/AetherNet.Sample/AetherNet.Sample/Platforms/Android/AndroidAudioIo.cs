// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content.PM;
using Android.Media;
using AetherNet.Sample.Shared.Services;
using AndroidX.Core.Content;
using AndroidApp = Android.App.Application;
using Stream = Android.Media.Stream;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// The microphone and earpiece on an Android phone.
///
/// <para>
/// Both ends are opened with the <b>voice communication</b> profiles rather than the general-purpose
/// ones. That is not cosmetic: <see cref="AudioSource.VoiceCommunication"/> is what asks the platform
/// for echo cancellation, noise suppression and automatic gain — without it, each phone's speaker is
/// picked straight back up by its own microphone and the two ends howl at each other within a second
/// of connecting. The same reason picks <see cref="Stream.VoiceCall"/> for output, which also routes
/// to the earpiece and follows the in-call volume keys.
/// </para>
/// </summary>
public sealed class AndroidAudioIo : IAudioIo, IDisposable
{
    private readonly object _gate = new();

    private AudioManager? _audioManager;
    private Mode _restoreMode = Mode.Normal;
    private bool _restoreSpeaker;

    private readonly object _ringGate = new();
    private Ringtone? _ringtone;

    private AudioRecord? _mic;
    private AudioTrack? _speaker;
    private CancellationTokenSource? _capture;
    private Task? _captureLoop;
    private bool _disposed;

    public bool IsRunning { get; private set; }

    public bool IsPresent => HasMicrophone && !_disposed;

    public bool IsAvailable => HasMicrophone && HasPermission && !_disposed;

    public string? UnavailableReason =>
        !HasMicrophone ? "this phone has no microphone"
        : !HasPermission ? "needs permission to use the microphone"
        : null;

    private static bool HasMicrophone =>
        AndroidApp.Context.PackageManager?.HasSystemFeature(PackageManager.FeatureMicrophone) == true;

    private static bool HasPermission =>
        ContextCompat.CheckSelfPermission(AndroidApp.Context, global::Android.Manifest.Permission.RecordAudio)
        == Permission.Granted;

    public event Action<short[]>? FrameCaptured;

    /// <summary>
    /// Ask for the microphone, and wait for the answer.
    ///
    /// <para>
    /// MAUI's own <see cref="Permissions"/> completes when the dialog does. The raw
    /// <c>ActivityCompat.RequestPermissions</c> does not — it returns immediately and the caller
    /// re-checks against a stale answer, which is how the radio screen used to insist a permission was
    /// missing seconds after the person had granted it.
    /// </para>
    /// </summary>
    public async Task<bool> EnsurePermissionAsync()
    {
        if (!HasMicrophone) return false;
        if (HasPermission) return true;

        try
        {
            var status = await Permissions.RequestAsync<Permissions.Microphone>().ConfigureAwait(false);
            return status == PermissionStatus.Granted;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> StartAsync(int sampleRateHz, int frameDurationMs, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed || IsRunning) return Task.FromResult(IsRunning);
            if (!IsAvailable) return Task.FromResult(false);

            var frameSamples = sampleRateHz * frameDurationMs / 1000;

            // The platform has a floor on buffer size that varies by device; ask it rather than
            // guessing, then make sure we have room for several frames so a scheduling hiccup does not
            // overrun the buffer and drop audio.
            var minIn = AudioRecord.GetMinBufferSize(sampleRateHz, ChannelIn.Mono, Encoding.Pcm16bit);
            var minOut = AudioTrack.GetMinBufferSize(sampleRateHz, ChannelOut.Mono, Encoding.Pcm16bit);
            if (minIn <= 0 || minOut <= 0) return Task.FromResult(false);

            // Two frames, not four. Every frame held here is 20ms a listener waits, and this sits on top
            // of the send queue and the radio — the delay a person actually hears is the sum. The
            // platform floor still wins where it is larger, which is the only reason for the Max.
            var frameBytes = frameSamples * 2;
            var inBuffer = Math.Max(minIn, frameBytes * 2);
            var outBuffer = Math.Max(minOut, frameBytes * 2);

            try
            {
                // Put the phone in call mode and use the loudspeaker.
                //
                // Stream.VoiceCall alone routes to the EARPIECE — the little speaker you hold against
                // your ear — and its level is the in-call volume stream, which the volume buttons do
                // not touch while the phone is not in a call. So the audio was arriving perfectly and
                // playing, very quietly, out of a speaker nobody was holding to their ear, while the
                // volume was already at maximum on a different stream entirely.
                if (AndroidApp.Context.GetSystemService(global::Android.Content.Context.AudioService)
                        is AudioManager audio)
                {
                    _audioManager = audio;
                    _restoreMode = audio.Mode;
                    _restoreSpeaker = audio.SpeakerphoneOn;
                    audio.Mode = Mode.InCommunication;      // makes the in-call stream the live one
                    audio.SpeakerphoneOn = true;            // and send it out of the loudspeaker
                }

                _mic = new AudioRecord(AudioSource.VoiceCommunication, sampleRateHz,
                    ChannelIn.Mono, Encoding.Pcm16bit, inBuffer);
                _speaker = new AudioTrack(Stream.VoiceCall, sampleRateHz,
                    ChannelOut.Mono, Encoding.Pcm16bit, outBuffer, AudioTrackMode.Stream);

                if (_mic.State != State.Initialized || _speaker.State != AudioTrackState.Initialized)
                {
                    ReleaseLocked();
                    return Task.FromResult(false);
                }

                _mic.StartRecording();
                _speaker.Play();
            }
            catch (Exception)
            {
                ReleaseLocked();
                return Task.FromResult(false);
            }

            IsRunning = true;
            _capture = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _captureLoop = Task.Run(() => CaptureAsync(frameSamples, _capture.Token));
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Read whole frames and hand them out. Runs on its own thread because it must keep pace with the
    /// microphone: fall behind and the platform's buffer overruns, which is heard as chopped speech.
    /// </summary>
    private void CaptureAsync(int frameSamples, CancellationToken token)
    {
        var frame = new short[frameSamples];

        while (!token.IsCancellationRequested)
        {
            AudioRecord? mic;
            lock (_gate) mic = _mic;
            if (mic is null) return;

            int read;
            try { read = mic.Read(frame, 0, frameSamples); }
            catch (Exception) { return; }

            if (read <= 0) continue;

            // A short read is a partial frame. Passing it on would encode as though it were whole and
            // the far end would hear the call speed up, so it is dropped.
            if (read != frameSamples) continue;

            var copy = new short[frameSamples];
            Array.Copy(frame, copy, frameSamples);
            try { FrameCaptured?.Invoke(copy); } catch { /* a handler must never kill the mic */ }
        }
    }

    /// <summary>
    /// Ring with the phone's own ringtone.
    ///
    /// <para>
    /// Deliberately not a tone we ship. Using the ringtone the person has already chosen means the
    /// call sounds like every other call they get, and it inherits everything they have already told
    /// the phone: their volume, their silent switch, their Do Not Disturb. A bundled tone would
    /// override all of that and be wrong in a meeting.
    /// </para>
    /// </summary>
    public void StartRinging(string callerTag)
    {
        // Ring the SCREEN as well as the speaker — otherwise the call only reaches someone already
        // looking at Aether, which is the one person who does not need telling.
        AetherIncomingCall.Show(string.IsNullOrEmpty(callerTag) ? "Someone" : callerTag);

        lock (_ringGate)
        {
            if (_ringtone is not null) return;
            try
            {
                var uri = RingtoneManager.GetActualDefaultRingtoneUri(AndroidApp.Context, RingtoneType.Ringtone)
                          ?? RingtoneManager.GetDefaultUri(RingtoneType.Ringtone);
                if (uri is null) return;

                _ringtone = RingtoneManager.GetRingtone(AndroidApp.Context, uri);
                if (_ringtone is null) return;
                _ringtone.Looping = true;                 // a call rings until it is dealt with
                _ringtone.Play();
            }
            catch (Exception)
            {
                // A phone that will not ring is not a reason to drop the call — the banner still shows.
                _ringtone = null;
            }
        }
    }

    /// <inheritdoc />
    public void HoldCall(string? peerTag) => AetherCallService.Start(peerTag);

    /// <inheritdoc />
    public void ReleaseCall() => AetherCallService.Stop();

    /// <inheritdoc />
    public void StopRinging()
    {
        AetherIncomingCall.Dismiss();

        lock (_ringGate)
        {
            try { _ringtone?.Stop(); } catch (Exception) { /* already gone */ }
            _ringtone = null;
        }
    }

    public void Play(short[] pcm)
    {
        if (pcm is null || pcm.Length == 0) return;

        AudioTrack? speaker;
        lock (_gate) speaker = IsRunning ? _speaker : null;
        if (speaker is null) return;

        try { speaker.Write(pcm, 0, pcm.Length); }
        catch (Exception) { /* the call is going away; silence is the right outcome */ }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? capture;
        Task? loop;

        lock (_gate)
        {
            if (!IsRunning) return;
            IsRunning = false;
            capture = _capture;
            loop = _captureLoop;
            _capture = null;
            _captureLoop = null;
        }

        try { capture?.Cancel(); } catch { }

        // Let the capture thread notice before the microphone is taken out from under it.
        if (loop is not null)
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }

        lock (_gate) ReleaseLocked();
        capture?.Dispose();
    }

    private void ReleaseLocked()
    {
        try { if (_mic?.RecordingState == RecordState.Recording) _mic.Stop(); } catch { }
        try { _mic?.Release(); } catch { }
        _mic?.Dispose();
        _mic = null;

        try { if (_speaker?.PlayState == PlayState.Playing) _speaker.Stop(); } catch { }
        try { _speaker?.Release(); } catch { }
        _speaker?.Dispose();
        _speaker = null;

        // Give the phone back exactly how we found it. Leaving it in call mode with the speakerphone
        // on would follow the person out of the app and into their music.
        if (_audioManager is { } audio)
        {
            try { audio.SpeakerphoneOn = _restoreSpeaker; } catch { }
            try { audio.Mode = _restoreMode; } catch { }
            _audioManager = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
    }
}
#endif
