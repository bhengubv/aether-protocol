// SPDX-License-Identifier: MIT

using Android.Content;
using Android.Media;
using Android.OS;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Recording a note on a phone.
///
/// <para>
/// Voice goes straight through <see cref="MediaRecorder"/>, which writes a real container on its own
/// thread and never touches this app's audio path. That separation matters: a call holds the
/// microphone in a very particular mode for its whole duration, and a note trying to share it would
/// either break the call or silently record nothing. So they are kept apart, and starting a note
/// during a call is refused outright rather than attempted.
/// </para>
///
/// <para>
/// Video is a screen, not a method — see <see cref="AetherRecordVideo"/>. There has to be somewhere
/// to see what the camera is pointing at, and in a Blazor Hybrid app the honest way to show a camera
/// is a native surface rather than frames pushed into a WebView.
/// </para>
/// </summary>
public sealed class AndroidMediaCapture : IMediaCapture, IDisposable
{
    /// <summary>
    /// Ogg/Opus arrived in <see cref="MediaRecorder"/> at API 29. Below that the phone records AAC in
    /// an MP4 container, which has been there since forever. Both are notes; both play.
    /// </summary>
    private static bool CanWriteOpus => Build.VERSION.SdkInt >= BuildVersionCodes.Q;

    /// <summary>
    /// Sized for speech on a scarce radio rather than for fidelity. At 24 kbps a minute of voice is
    /// about 180 KB — instant over Wi-Fi Direct, a couple of minutes over the measured BLE link, and
    /// comfortably good enough for someone talking.
    /// </summary>
    private const int VoiceBitrateBps = 24_000;

    private const int VoiceSampleRateHz = 16_000;

    private readonly IAudioIo? _audio;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MediaRecorder? _recorder;
    private string? _path;
    private DateTime _startedAt;
    private Timer? _ticker;
    private CancellationTokenSource? _cap;
    private bool _disposed;

    /// <param name="audio">
    /// The call's audio path, when there is one. Only ever asked whether a call is running — a note
    /// and a call cannot share the microphone, and finding that out by recording silence is no good.
    /// </param>
    public AndroidMediaCapture(IAudioIo? audio = null) => _audio = audio;

    private static Context Ctx => global::Android.App.Application.Context;

    public bool CanRecordVoice => Ctx.PackageManager?
        .HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureMicrophone) ?? false;

    public bool CanRecordVideo => CanRecordVoice
        && (Ctx.PackageManager?.HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureCameraAny) ?? false);

    public string? UnavailableReason =>
        _audio is { IsRunning: true } ? "you are on a call"
        : !CanRecordVoice ? "this phone has no microphone"
        : null;

    public bool IsRecording => _recorder is not null;

    public TimeSpan Elapsed => IsRecording ? DateTime.UtcNow - _startedAt : TimeSpan.Zero;

    public TimeSpan MaxDuration => TimeSpan.FromMinutes(1);

    public TimeSpan MinDuration => TimeSpan.FromSeconds(1);

    public event Action? Ticked;

    public event Action<RecordedNote?>? Capped;

    // ── Permission ────────────────────────────────────────────────────────────

    public async Task<bool> EnsurePermissionAsync(bool video)
    {
        var mic = await Permissions.RequestAsync<Permissions.Microphone>().ConfigureAwait(false);
        if (mic != PermissionStatus.Granted) return false;

        if (!video) return true;

        var camera = await Permissions.RequestAsync<Permissions.Camera>().ConfigureAwait(false);
        return camera == PermissionStatus.Granted;
    }

    // ── Voice ─────────────────────────────────────────────────────────────────

    public async Task<bool> StartVoiceAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || IsRecording) return false;

        // A call owns the microphone for its whole duration. Starting a note on top of one does not
        // fail loudly on Android — it produces an empty file, or takes the call's audio away, and
        // either way the person only finds out afterwards.
        if (_audio is { IsRunning: true }) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRecording) return false;

            var extension = CanWriteOpus ? ".ogg" : ".m4a";
            _path = Path.Combine(FileSystem.CacheDirectory, "note-" + Guid.NewGuid().ToString("N") + extension);

            // The context-taking constructor is required from API 31 and merely preferred below it, so
            // this branch is about the API level rather than about taste.
            var recorder = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? new MediaRecorder(Ctx)
                : new MediaRecorder();

            recorder.SetAudioSource(AudioSource.Mic);

            if (CanWriteOpus)
            {
                recorder.SetOutputFormat(OutputFormat.Ogg);
                recorder.SetAudioEncoder(AudioEncoder.Opus);
            }
            else
            {
                recorder.SetOutputFormat(OutputFormat.Mpeg4);
                recorder.SetAudioEncoder(AudioEncoder.Aac);
            }

            recorder.SetAudioChannels(1);                 // speech; stereo doubles the bytes for nothing
            recorder.SetAudioSamplingRate(VoiceSampleRateHz);
            recorder.SetAudioEncodingBitRate(VoiceBitrateBps);
            recorder.SetOutputFile(_path);

            recorder.Prepare();
            recorder.Start();

            _recorder = recorder;
            _startedAt = DateTime.UtcNow;

            // Stop at the cap ourselves rather than trusting the screen to. Whoever is holding the
            // button is watching a timer, not a clock, and MaxDuration exists because of the radio.
            _cap = new CancellationTokenSource();
            _ = StopAtTheCapAsync(_cap.Token);

            _ticker = new Timer(_ => Ticked?.Invoke(), null, 100, 100);
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherNote", "could not start recording: " + ex);
            Discard();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecordedNote?> StopVoiceAsync()
    {
        if (_disposed) return null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var recorder = _recorder;
            var path = _path;
            if (recorder is null || path is null) return null;

            var elapsed = DateTime.UtcNow - _startedAt;
            _recorder = null;
            _path = null;
            StopTicking();

            try
            {
                recorder.Stop();
            }
            catch (Exception ex)
            {
                // Stop throws when nothing was ever captured — a tap rather than a hold. There is no
                // note in that case, and the file left behind will not play.
                global::Android.Util.Log.Info("AetherNote", "nothing recorded: " + ex.Message);
                recorder.Release();
                Delete(path);
                return null;
            }

            recorder.Release();

            // Below the floor it was a mis-tap. Deciding that here rather than in the screen means
            // every caller gets the same answer, including the timer that stops us at the cap.
            if (elapsed < MinDuration)
            {
                Delete(path);
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            Delete(path);

            if (bytes.Length == 0) return null;

            // What the encoder actually did, not what it was asked to do.
            //
            // SetAudioEncodingBitRate is a hint and a good number of encoders quietly ignore it: a
            // nine-second note asked to be 24 kbps came out 91 KB, which is 81 kbps — three and a half
            // times over. Nothing errors, so the only way to know is to weigh the result. It is logged
            // every time because the figure varies by phone, and a transfer time calculated from the
            // requested bitrate is a transfer time that is wrong on the device it matters on.
            var actualBps = elapsed.TotalSeconds > 0
                ? (long)(bytes.Length * 8 / elapsed.TotalSeconds)
                : 0;
            global::Android.Util.Log.Info("AetherNote",
                $"note: {bytes.Length:N0}B over {elapsed.TotalSeconds:0.0}s = {actualBps:N0} bps " +
                $"(asked for {VoiceBitrateBps:N0})");

            return new RecordedNote(
                bytes,
                CanWriteOpus ? ChatMessage.VoiceNote : ChatMessage.VoiceNoteAac,
                elapsed);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherNote", "could not finish recording: " + ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync()
    {
        if (_disposed) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try { Discard(); }
        finally { _gate.Release(); }
    }

    // ── Video ─────────────────────────────────────────────────────────────────

    public async Task<RecordedNote?> RecordVideoAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !CanRecordVideo) return null;
        if (_audio is { IsRunning: true }) return null;

        var captured = await AetherRecordVideo
            .CaptureAsync((int)MaxDuration.TotalSeconds, cancellationToken)
            .ConfigureAwait(false);

        if (captured is null) return null;

        var (path, duration) = captured.Value;
        try
        {
            if (duration < MinDuration) return null;

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return bytes.Length == 0 ? null : new RecordedNote(bytes, ChatMessage.VideoNote, duration);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherNote", "could not read the video note: " + ex);
            return null;
        }
        finally
        {
            Delete(path);
        }
    }

    // ── Housekeeping ──────────────────────────────────────────────────────────

    /// <summary>
    /// Stop at <see cref="MaxDuration"/> whatever the screen is doing, and say so.
    ///
    /// <para>
    /// The UI has to hear about this. Without it the button is still held down over a recorder that
    /// stopped a while ago, and letting go produces nothing with no explanation.
    /// </para>
    /// </summary>
    private async Task StopAtTheCapAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(MaxDuration, token).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            return;   // stopped or cancelled first, which is the ordinary case
        }

        if (IsRecording) Capped?.Invoke(await StopVoiceAsync().ConfigureAwait(false));
    }

    /// <summary>Tear down a recorder in progress and leave nothing behind. The caller holds the gate.</summary>
    private void Discard()
    {
        StopTicking();

        var recorder = _recorder;
        var path = _path;
        _recorder = null;
        _path = null;

        if (recorder is not null)
        {
            try { recorder.Stop(); } catch { /* nothing was captured, so there is nothing to stop */ }
            try { recorder.Release(); } catch { /* already gone */ }
        }

        if (path is not null) Delete(path);
    }

    private void StopTicking()
    {
        _cap?.Cancel();
        _cap?.Dispose();
        _cap = null;
        _ticker?.Dispose();
        _ticker = null;
    }

    /// <summary>
    /// A note lives in the content store, never as a loose file. The cache copy is working space and
    /// goes the moment it has been read — a conversation's audio must not be sitting where anything
    /// browsing the filesystem can pick it up.
    /// </summary>
    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Discard(); } catch { /* tearing down */ }
        _gate.Dispose();
    }
}
