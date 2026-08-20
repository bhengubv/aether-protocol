// SPDX-License-Identifier: MIT

using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.Views;
using Android.Widget;
using AetherNet.Sample.Shared.Services;
using Java.Nio;

using Color = Android.Graphics.Color;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Live video on a phone: camera to H.264 out, H.264 to screen in.
///
/// <para>
/// The whole point of this class is that a frame never becomes managed memory. The camera captures
/// straight into the encoder's input surface, and the decoder draws straight onto a view's surface.
/// Only the encoded bytes — a few kilobytes a frame — are ever touched by C#, which is what makes a
/// video call affordable on a mid-range handset.
/// </para>
///
/// <para>
/// The views are native, layered over the Blazor WebView rather than drawn inside it. That is the
/// hybrid render decision: Blazor keeps the controls, and the two things that must not be copied per
/// frame stay on surfaces the platform owns. See <see cref="IVideoIo"/>.
/// </para>
///
/// <para>
/// <b>Unverified on hardware.</b> This compiles and the shape follows the documented MediaCodec
/// contract, but no video call has been placed between two handsets yet. Until one has, treat the
/// sizing and the keyframe interval below as reasoned starting points rather than measurements —
/// PROTOCOL_SPEC §5.5 records what has actually been measured, and video is not in it.
/// </para>
/// </summary>
public sealed class AndroidVideoIo : IVideoIo, IDisposable
{
    private const string Mime = "video/avc";   // H.264, the one encoder every Android phone has

    /// <summary>
    /// 640x480 at 20 fps and 800 kbps.
    ///
    /// <para>
    /// Chosen against the radio rather than against the screen. Wi-Fi Direct measured comfortably
    /// above three megabits, and video has to leave room for the voice it shares the link with; a
    /// bigger picture would look better right up to the moment it started eating the call.
    /// </para>
    /// </summary>
    private const int Width = 640;

    private const int Height = 480;
    private const int Fps = 20;
    private const int BitrateBps = 800_000;

    /// <summary>
    /// A keyframe every second.
    ///
    /// <para>
    /// More often than a recording would use, because this is not a recording. A receiver that joins
    /// late — the camera went on mid-call, a frame was lost — can draw nothing until the next
    /// keyframe, so the gap between them is how long someone stares at a blank rectangle.
    /// </para>
    /// </summary>
    private const int KeyframeIntervalSeconds = 1;

    private readonly object _gate = new();

    private CameraDevice? _camera;
    private CameraCaptureSession? _session;
    private MediaCodec? _encoder;
    private MediaCodec? _decoder;
    private Surface? _encoderInput;
    private FrameLayout? _overlay;
    private SurfaceView? _remoteView;
    private TextureView? _localView;
    private Thread? _drain;
    private volatile bool _running;
    private bool _decoderReady;
    private bool _useFrontCamera = true;
    private byte[]? _codecConfig;
    private bool _disposed;

    public bool IsPresent => global::Android.App.Application.Context.PackageManager?
        .HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureCameraAny) ?? false;

    public string? UnavailableReason => IsPresent ? null : "this phone has no camera";

    public bool IsRunning => _running;

    public event Action<byte[]>? FrameEncoded;

    public async Task<bool> EnsurePermissionAsync()
        => await Permissions.RequestAsync<Permissions.Camera>().ConfigureAwait(false) == PermissionStatus.Granted;

    // ── Coming up ─────────────────────────────────────────────────────────────

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _running || !IsPresent) return false;

        try
        {
            await MainThread.InvokeOnMainThreadAsync(AddOverlay).ConfigureAwait(false);

            if (!StartEncoder()) { await StopAsync().ConfigureAwait(false); return false; }
            if (!OpenCamera()) { await StopAsync().ConfigureAwait(false); return false; }

            _running = true;
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "could not start video: " + ex);
            await StopAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Put the two surfaces over the WebView: the far end filling the screen, this phone in a corner.
    ///
    /// <para>
    /// Added to the activity's own content view rather than to anything Blazor owns, because a
    /// SurfaceView punches a hole through everything above it in the window. Layered over, it is the
    /// video; layered under, it is invisible.
    /// </para>
    /// </summary>
    private void AddOverlay()
    {
        var activity = Platform.CurrentActivity;
        if (activity is null) return;

        var content = activity.FindViewById<FrameLayout>(global::Android.Resource.Id.Content);
        if (content is null) return;

        _overlay = new FrameLayout(activity);
        _overlay.SetBackgroundColor(Color.Black);

        _remoteView = new SurfaceView(activity);
        _remoteView.Holder?.AddCallback(new RemoteSurface(this));
        _overlay.AddView(_remoteView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // The local preview is a TextureView rather than a second SurfaceView: two surface views in
        // one window fight over z-order, and the one that loses disappears entirely.
        _localView = new TextureView(activity);
        var corner = new FrameLayout.LayoutParams(
            (int)(120 * activity.Resources!.DisplayMetrics!.Density),
            (int)(160 * activity.Resources.DisplayMetrics.Density),
            GravityFlags.Top | GravityFlags.End);
        corner.TopMargin = (int)(24 * activity.Resources.DisplayMetrics.Density);
        corner.RightMargin = corner.TopMargin;
        _overlay.AddView(_localView, corner);

        // Below the WebView in z-order so the call controls Blazor draws stay on top and tappable.
        content.AddView(_overlay, 0, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    /// <summary>Build the H.264 encoder and take its input surface for the camera to draw onto.</summary>
    private bool StartEncoder()
    {
        try
        {
            var format = MediaFormat.CreateVideoFormat(Mime, Width, Height)!;
            format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
            format.SetInteger(MediaFormat.KeyBitRate, BitrateBps);
            format.SetInteger(MediaFormat.KeyFrameRate, Fps);
            format.SetInteger(MediaFormat.KeyIFrameInterval, KeyframeIntervalSeconds);

            _encoder = MediaCodec.CreateEncoderByType(Mime);
            _encoder!.Configure(format, null, null, MediaCodecConfigFlags.Encode);
            _encoderInput = _encoder.CreateInputSurface();
            _encoder.Start();

            _drain = new Thread(DrainEncoder) { IsBackground = true, Name = "aether-video-encode" };
            _drain.Start();
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "no H.264 encoder: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Pull encoded frames out of the encoder and hand them up.
    ///
    /// <para>
    /// Its own thread on purpose. This blocks waiting for output, and doing that anywhere near the UI
    /// thread in a Blazor Hybrid app freezes the whole window — the dispatcher, the WebView and the
    /// Android main thread are one thread here.
    /// </para>
    /// </summary>
    private void DrainEncoder()
    {
        var info = new MediaCodec.BufferInfo();

        while (_running || _encoder is not null)
        {
            MediaCodec? encoder;
            lock (_gate) encoder = _encoder;
            if (encoder is null) return;

            try
            {
                var index = encoder.DequeueOutputBuffer(info, 10_000);
                if (index < 0) continue;

                var buffer = encoder.GetOutputBuffer(index);
                if (buffer is not null && info.Size > 0)
                {
                    var frame = new byte[info.Size];
                    buffer.Position(info.Offset);
                    buffer.Get(frame, 0, info.Size);

                    // SPS and PPS arrive once, before any picture. The far end cannot decode a single
                    // frame without them, so they are kept and put in front of every keyframe — a
                    // phone whose camera went on mid-call, or that lost the first frame, would
                    // otherwise never recover.
                    if ((info.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                        _codecConfig = frame;
                    else if ((info.Flags & MediaCodecBufferFlags.KeyFrame) != 0 && _codecConfig is not null)
                        FrameEncoded?.Invoke([.. _codecConfig, .. frame]);
                    else
                        FrameEncoded?.Invoke(frame);
                }

                encoder.ReleaseOutputBuffer(index, render: false);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Info("AetherVideo", "encoder drain stopped: " + ex.Message);
                return;
            }
        }
    }

    private bool OpenCamera()
    {
        try
        {
            var manager = (CameraManager?)global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.CameraService);

            var id = PickCamera(manager);
            if (manager is null || id is null) return false;

            manager.OpenCamera(id, new Opened(this), null);
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "could not open the camera: " + ex);
            return false;
        }
    }

    /// <summary>The selfie camera by default — a video call is your face — falling back to any.</summary>
    private string? PickCamera(CameraManager? manager)
    {
        var ids = manager?.GetCameraIdList();
        if (ids is null || ids.Length == 0) return null;

        var wanted = _useFrontCamera ? LensFacing.Front : LensFacing.Back;

        foreach (var id in ids)
        {
            var facing = (manager!.GetCameraCharacteristics(id)
                .Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue();

            if (facing == (int)wanted) return id;
        }

        return ids[0];
    }

    private sealed class Opened(AndroidVideoIo video) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera)
        {
            video._camera = camera;
            video.StartCapture();
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            video._camera = null;
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            global::Android.Util.Log.Error("AetherVideo", "camera error: " + error);
            camera.Close();
            video._camera = null;
        }
    }

    /// <summary>Point the camera at both the encoder and the little local preview.</summary>
    private void StartCapture()
    {
        var camera = _camera;
        var input = _encoderInput;
        if (camera is null || input is null) return;

        try
        {
            var targets = new List<Surface> { input };

            var texture = _localView?.SurfaceTexture;
            if (texture is not null)
            {
                texture.SetDefaultBufferSize(Width, Height);
                targets.Add(new Surface(texture));
            }

            var request = camera.CreateCaptureRequest(CameraTemplate.Record);
            foreach (var target in targets) request.AddTarget(target);

            camera.CreateCaptureSession(targets, new Streaming(this, request.Build()), null);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "could not start capture: " + ex);
        }
    }

    private sealed class Streaming(AndroidVideoIo video, CaptureRequest request)
        : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            video._session = session;
            try { session.SetRepeatingRequest(request, null, null); }
            catch (Exception ex) { global::Android.Util.Log.Error("AetherVideo", "capture refused: " + ex); }
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
            => global::Android.Util.Log.Error("AetherVideo", "capture session would not configure");
    }

    // ── Playing what arrives ──────────────────────────────────────────────────

    /// <summary>The remote surface is only usable once Android says it exists.</summary>
    private sealed class RemoteSurface(AndroidVideoIo video) : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder) => video.StartDecoder(holder.Surface);
        public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }
        public void SurfaceDestroyed(ISurfaceHolder holder) => video.StopDecoder();
    }

    private void StartDecoder(Surface? surface)
    {
        if (surface is null) return;

        try
        {
            var format = MediaFormat.CreateVideoFormat(Mime, Width, Height)!;

            lock (_gate)
            {
                _decoder = MediaCodec.CreateDecoderByType(Mime);
                _decoder!.Configure(format, surface, null, MediaCodecConfigFlags.None);
                _decoder.Start();
                _decoderReady = true;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "no H.264 decoder: " + ex);
        }
    }

    private void StopDecoder()
    {
        lock (_gate)
        {
            _decoderReady = false;
            try { _decoder?.Stop(); } catch { /* already gone */ }
            try { _decoder?.Release(); } catch { /* already gone */ }
            _decoder = null;
        }
    }

    public void Play(byte[] encodedFrame)
    {
        if (_disposed || encodedFrame is null || encodedFrame.Length == 0) return;

        MediaCodec? decoder;
        lock (_gate)
        {
            if (!_decoderReady) return;
            decoder = _decoder;
        }
        if (decoder is null) return;

        try
        {
            // Zero timeout on purpose. A video frame is worthless a moment after it was captured, so
            // if the decoder has no room the frame is dropped rather than waited on — waiting would
            // back the radio thread up behind a decoder that is already behind.
            var index = decoder.DequeueInputBuffer(0);
            if (index < 0) return;

            var buffer = decoder.GetInputBuffer(index);
            if (buffer is null) return;

            buffer.Clear();
            buffer.Put(encodedFrame);
            decoder.QueueInputBuffer(index, 0, encodedFrame.Length, 0, MediaCodecBufferFlags.None);

            // Render everything the decoder has ready. render:true is what actually draws it onto the
            // surface — the decode alone puts nothing on screen.
            var info = new MediaCodec.BufferInfo();
            int output;
            while ((output = decoder.DequeueOutputBuffer(info, 0)) >= 0)
                decoder.ReleaseOutputBuffer(output, render: true);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Info("AetherVideo", "dropped a frame: " + ex.Message);
        }
    }

    public void ShowRemote(bool visible)
    {
        var view = _remoteView;
        if (view is null) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { view.Visibility = visible ? ViewStates.Visible : ViewStates.Invisible; }
            catch { /* the overlay is going away */ }
        });
    }

    public void SwitchCamera()
    {
        if (!_running) return;

        _useFrontCamera = !_useFrontCamera;

        try
        {
            _session?.Close();
            _session = null;
            _camera?.Close();
            _camera = null;
            OpenCamera();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "could not switch camera: " + ex);
        }
    }

    // ── Going away ────────────────────────────────────────────────────────────

    public async Task StopAsync()
    {
        _running = false;

        try { _session?.Close(); } catch { /* already gone */ }
        _session = null;

        try { _camera?.Close(); } catch { /* already gone */ }
        _camera = null;

        MediaCodec? encoder;
        lock (_gate) { encoder = _encoder; _encoder = null; }

        // The drain thread exits when it sees the encoder go; joining it first means the encoder is
        // never released underneath a thread still reading from it.
        if (_drain is not null)
        {
            try { _drain.Join(500); } catch { /* it will exit on its own */ }
            _drain = null;
        }

        try { encoder?.Stop(); } catch { /* nothing was ever encoded */ }
        try { encoder?.Release(); } catch { /* already gone */ }

        try { _encoderInput?.Release(); } catch { /* already gone */ }
        _encoderInput = null;
        _codecConfig = null;

        StopDecoder();

        await MainThread.InvokeOnMainThreadAsync(RemoveOverlay).ConfigureAwait(false);
    }

    private void RemoveOverlay()
    {
        try
        {
            if (_overlay?.Parent is ViewGroup parent) parent.RemoveView(_overlay);
        }
        catch { /* the activity went first */ }

        _overlay = null;
        _remoteView = null;
        _localView = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { StopAsync().GetAwaiter().GetResult(); } catch { /* tearing down */ }
    }
}
