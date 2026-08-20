// SPDX-License-Identifier: MIT

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

// MAUI and Android both have a Button and a TextView-shaped thing in scope here. These are Android
// views on an Android Activity, so say so once rather than qualifying every use.
using Button = Android.Widget.Button;
using TextView = Android.Widget.TextView;
using Color = Android.Graphics.Color;
using Path = System.IO.Path;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// The screen you record a video note on.
///
/// <para>
/// A whole activity rather than something drawn in the conversation, and that is the decision rather
/// than a shortcut. In a Blazor Hybrid app the camera cannot be shown honestly inside the WebView:
/// pushing frames through as images costs a copy and an encode per frame and still looks like a
/// slideshow. The camera writes to a native surface, so the surface is what has to be on screen — and
/// for a note, which is modal anyway, taking the whole screen is also the right shape for the person
/// using it.
/// </para>
///
/// <para>
/// Camera2 and <see cref="MediaRecorder"/> come out of the Android SDK, so this adds no dependency
/// and needs nothing from Google Play Services. The recorder gets the encoded frames directly; they
/// are never handed through managed code, which is what keeps this affordable on a mid-range phone.
/// </para>
/// </summary>
[Activity(
    Label = "Video note",
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode,
    Exported = false)]
public sealed class AetherRecordVideo : Activity, TextureView.ISurfaceTextureListener
{
    /// <summary>Sized for a note on a mesh, not for a camera roll. See <see cref="Bitrate"/>.</summary>
    private const int Width = 640;

    private const int Height = 480;
    private const int Fps = 24;

    /// <summary>
    /// 800 kbps of H.264 at 640x480 — about 100 KB per second.
    ///
    /// <para>
    /// That is deliberately modest. A ten-second video note is roughly a megabyte, which Wi-Fi Direct
    /// moves in well under a second and the measured BLE link takes about twelve minutes over
    /// (PROTOCOL_SPEC §5.5). So a video note is a Wi-Fi Direct feature in practice, and the bitrate is
    /// chosen so that stays true rather than becoming absurd.
    /// </para>
    /// </summary>
    private const int Bitrate = 800_000;

    private const string ExtraMaxSeconds = "max_seconds";

    /// <summary>
    /// Where the answer is delivered. Static because an Activity is constructed by Android, not by us,
    /// so there is no other way to hand a result back to the caller that started it.
    /// </summary>
    private static TaskCompletionSource<(string Path, TimeSpan Duration)?>? _pending;

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private CameraDevice? _camera;
    private CameraCaptureSession? _session;
    private MediaRecorder? _recorder;
    private TextureView? _preview;
    private TextView? _timer;
    private Button? _button;
    private string? _path;
    private DateTime _startedAt;
    private System.Threading.Timer? _ticker;
    private int _maxSeconds = 60;
    private bool _recording;
    private bool _finished;

    /// <summary>
    /// Open the camera screen, wait for a note, and hand back where it landed.
    ///
    /// <para>
    /// Null means the person backed out, which is an ordinary outcome and not an error. The file is
    /// the caller's to read and delete — this screen is finished with it the moment it returns.
    /// </para>
    /// </summary>
    public static async Task<(string Path, TimeSpan Duration)?> CaptureAsync(
        int maxSeconds, CancellationToken cancellationToken = default)
    {
        // One camera, one screen. Two overlapping captures would fight over both, and the second
        // would silently take over the first's completion source.
        await OneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = global::Android.App.Application.Context;
            var pending = new TaskCompletionSource<(string, TimeSpan)?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = pending;

            var intent = new Intent(context, typeof(AetherRecordVideo));
            intent.SetFlags(ActivityFlags.NewTask);
            intent.PutExtra(ExtraMaxSeconds, maxSeconds);
            context.StartActivity(intent);

            using (cancellationToken.Register(() => pending.TrySetResult(null)))
                return await pending.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending = null;
            OneAtATime.Release();
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _maxSeconds = Intent?.GetIntExtra(ExtraMaxSeconds, 60) ?? 60;

        // Built in code rather than in a layout file. It is three views, and keeping it here means the
        // whole screen can be read in one place instead of two.
        var root = new FrameLayout(this);

        _preview = new TextureView(this) { SurfaceTextureListener = this };
        root.AddView(_preview, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _timer = new TextView(this)
        {
            Text = "0:00",
            TextSize = 18,
            Gravity = GravityFlags.Center,
        };
        _timer.SetTextColor(Color.White);
        _timer.SetPadding(0, 48, 0, 0);
        root.AddView(_timer, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));

        _button = new Button(this) { Text = "Record" };
        _button.Click += (_, _) => Toggle();
        var buttonLayout = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Bottom | GravityFlags.CenterHorizontal);
        buttonLayout.BottomMargin = 96;
        root.AddView(_button, buttonLayout);

        SetContentView(root);
    }

    // ── The camera ────────────────────────────────────────────────────────────

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) => OpenCamera();

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        Teardown();
        return true;
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }

    public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

    private void OpenCamera()
    {
        try
        {
            var manager = (CameraManager?)GetSystemService(CameraService);
            var id = FrontCameraId(manager);
            if (manager is null || id is null)
            {
                Finish(null);
                return;
            }

            manager.OpenCamera(id, new Opened(this), null);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherNote", "could not open the camera: " + ex);
            Finish(null);
        }
    }

    /// <summary>
    /// The selfie camera, because a video note is somebody talking to you. Falls back to whatever the
    /// phone does have — a device with only a rear camera should still be able to send one.
    /// </summary>
    private static string? FrontCameraId(CameraManager? manager)
    {
        var ids = manager?.GetCameraIdList();
        if (ids is null || ids.Length == 0) return null;

        foreach (var id in ids)
        {
            var facing = (int?)(manager!.GetCameraCharacteristics(id)
                .Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue();

            if (facing == (int)LensFacing.Front) return id;
        }

        return ids[0];
    }

    /// <summary>Android hands the open camera back here; nothing else uses this callback.</summary>
    private sealed class Opened(AetherRecordVideo screen) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera)
        {
            screen._camera = camera;
            screen.StartPreview();
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            screen._camera = null;
            screen.Finish(null);
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            global::Android.Util.Log.Error("AetherNote", "camera error: " + error);
            camera.Close();
            screen._camera = null;
            screen.Finish(null);
        }
    }

    private void StartPreview()
    {
        var texture = _preview?.SurfaceTexture;
        if (_camera is null || texture is null) return;

        try
        {
            texture.SetDefaultBufferSize(Width, Height);
            var surface = new Surface(texture);

            var request = _camera.CreateCaptureRequest(CameraTemplate.Preview);
            request.AddTarget(surface);

            _camera.CreateCaptureSession(
                new List<Surface> { surface },
                new SessionReady(this, request.Build()),
                null);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherNote", "could not start the preview: " + ex);
            Finish(null);
        }
    }

    private sealed class SessionReady(AetherRecordVideo screen, CaptureRequest request)
        : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            screen._session = session;
            try { session.SetRepeatingRequest(request, null, null); }
            catch (Exception ex) { global::Android.Util.Log.Error("AetherNote", "preview refused: " + ex); }
        }

        public override void OnConfigureFailed(CameraCaptureSession session) => screen.Finish(null);
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    private void Toggle()
    {
        if (_recording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        var texture = _preview?.SurfaceTexture;
        if (_camera is null || texture is null || _recording) return;

        try
        {
            _session?.Close();
            _session = null;

            _path = Path.Combine(CacheDir?.AbsolutePath ?? FileSystem.CacheDirectory,
                "note-" + Guid.NewGuid().ToString("N") + ".mp4");

            var recorder = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? new MediaRecorder(this)
                : new MediaRecorder();

            recorder.SetAudioSource(AudioSource.Mic);
            recorder.SetVideoSource(VideoSource.Surface);
            recorder.SetOutputFormat(OutputFormat.Mpeg4);
            recorder.SetOutputFile(_path);
            recorder.SetVideoEncodingBitRate(Bitrate);
            recorder.SetVideoFrameRate(Fps);
            recorder.SetVideoSize(Width, Height);
            recorder.SetVideoEncoder(VideoEncoder.H264);
            recorder.SetAudioEncoder(AudioEncoder.Aac);
            recorder.SetAudioChannels(1);
            recorder.SetAudioSamplingRate(16_000);
            recorder.SetAudioEncodingBitRate(24_000);

            // The cap is set on the recorder as well as watched on the clock. A recorder that stops
            // itself always leaves a valid file; a process killed mid-write does not.
            recorder.SetMaxDuration(_maxSeconds * 1000);
            recorder.Prepare();

            texture.SetDefaultBufferSize(Width, Height);
            var previewSurface = new Surface(texture);
            var recorderSurface = recorder.Surface!;

            var request = _camera.CreateCaptureRequest(CameraTemplate.Record);
            request.AddTarget(previewSurface);
            request.AddTarget(recorderSurface);

            _recorder = recorder;

            _camera.CreateCaptureSession(
                new List<Surface> { previewSurface, recorderSurface },
                new RecordingReady(this, request.Build()),
                null);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherNote", "could not start recording: " + ex);
            Finish(null);
        }
    }

    private sealed class RecordingReady(AetherRecordVideo screen, CaptureRequest request)
        : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            screen._session = session;
            try
            {
                session.SetRepeatingRequest(request, null, null);
                screen.Rolling();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("AetherNote", "recording refused: " + ex);
                screen.Finish(null);
            }
        }

        public override void OnConfigureFailed(CameraCaptureSession session) => screen.Finish(null);
    }

    /// <summary>The camera is feeding the recorder; start it and start counting.</summary>
    private void Rolling()
    {
        _recorder?.Start();
        _recording = true;
        _startedAt = DateTime.UtcNow;

        RunOnUiThread(() => { if (_button is not null) _button.Text = "Stop"; });

        _ticker = new System.Threading.Timer(_ =>
        {
            var elapsed = DateTime.UtcNow - _startedAt;
            RunOnUiThread(() =>
            {
                if (_timer is not null)
                    _timer.Text = elapsed.Minutes + ":" + elapsed.Seconds.ToString("00");
            });

            if (elapsed.TotalSeconds >= _maxSeconds) RunOnUiThread(StopRecording);
        }, null, 200, 200);
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;

        var elapsed = DateTime.UtcNow - _startedAt;
        _ticker?.Dispose();
        _ticker = null;

        var recorder = _recorder;
        _recorder = null;

        try
        {
            recorder?.Stop();
            recorder?.Release();
        }
        catch (Exception ex)
        {
            // Nothing was captured — stopped almost immediately. There is no note, and the file it
            // left behind is not playable, so it is thrown away rather than sent.
            global::Android.Util.Log.Info("AetherNote", "nothing recorded: " + ex.Message);
            try { recorder?.Release(); } catch { /* already gone */ }
            Finish(null);
            return;
        }

        Finish(_path is null ? null : (_path, elapsed));
    }

    // ── Leaving ───────────────────────────────────────────────────────────────

    /// <summary>Backing out is an answer too — the caller gets null and the conversation reopens.</summary>
    public override void OnBackPressed()
    {
        if (_recording) StopRecording();
        else Finish(null);
    }

    protected override void OnPause()
    {
        base.OnPause();

        // Something took the screen — a call, the home button. A half-written note is not worth
        // keeping, and the camera must be given back before whatever took over asks for it.
        if (_recording) StopRecording();
        else if (!_finished) Finish(null);
    }

    /// <summary>Hand the answer back exactly once, release the camera, and close.</summary>
    private void Finish((string Path, TimeSpan Duration)? result)
    {
        if (_finished) return;
        _finished = true;

        Teardown();
        _pending?.TrySetResult(result);

        RunOnUiThread(Finish);
    }

    private void Teardown()
    {
        _ticker?.Dispose();
        _ticker = null;

        try { _session?.Close(); } catch { /* already gone */ }
        _session = null;

        try { _recorder?.Release(); } catch { /* already gone */ }
        _recorder = null;

        try { _camera?.Close(); } catch { /* already gone */ }
        _camera = null;
    }
}
