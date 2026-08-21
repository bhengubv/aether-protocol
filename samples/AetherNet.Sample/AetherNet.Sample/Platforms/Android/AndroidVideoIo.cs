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
    /// <summary>
    /// Where the encoder starts. It does not stay here — see <see cref="SizeToLink"/>.
    /// </summary>
    /// <remarks>
    /// This was a const that nothing ever changed, so every call pushed the same 800 kbps whether it
    /// had a five-gigahertz channel to itself or was time-slicing against the phone's own access
    /// point. It is now only an opening bid.
    /// </remarks>
    private const int StartingBitrateBps = MediaBitrate.VideoCeilingBps;

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

    /// <summary>
    /// How many faces are worth putting on a phone screen at once.
    ///
    /// <para>
    /// A limit of the display, not of the silicon. Six tiles on a handset is already a two-by-three
    /// grid of thumbnails; past that nobody can tell who is who, and the decoders spent on them buy
    /// nothing.
    /// </para>
    /// </summary>
    private const int UsefulTilesOnAPhoneScreen = 6;

    private readonly object _gate = new();

    private CameraDevice? _camera;
    private CameraCaptureSession? _session;
    private MediaCodec? _encoder;
    private int _bitrateBps;
    private DateTimeOffset _lastSized = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public int BitrateBps => _running ? _bitrateBps : 0;

    /// <summary>
    /// How often the picture may change size. Every frame would be chasing noise; once a second is
    /// fast enough to get out of the way of a link going bad and slow enough to be stable.
    /// </summary>
    private static readonly TimeSpan SizeEvery = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public void SizeToLink(double strain, int people)
    {
        var encoder = _encoder;
        if (!_running || encoder is null) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastSized < SizeEvery) return;
        _lastSized = now;

        var wanted = MediaBitrate.Video(_bitrateBps, strain, people);

        // Below the floor there is no picture worth the bytes, and continuing to send an unwatchable
        // one crowds out the voice — which is the half of a call people actually need.
        if (wanted <= 0)
        {
            global::Android.Util.Log.Info("AetherVideo",
                $"link too tight for video (strain {strain:0.00}) — stopping the camera");
            _ = StopAsync();
            return;
        }

        // A tenth either way is not worth reconfiguring the encoder for.
        if (Math.Abs(wanted - _bitrateBps) < _bitrateBps / 10) return;

        try
        {
            // setParameters, not a reconfigure: the encoder keeps running and the stream keeps its
            // sequence, so the far side sees a change in quality rather than a gap.
            using var change = new global::Android.OS.Bundle();
            change.PutInt(MediaCodec.ParameterKeyVideoBitrate, wanted);
            encoder.SetParameters(change);

            global::Android.Util.Log.Info("AetherVideo",
                $"video {_bitrateBps / 1000}k → {wanted / 1000}k (strain {strain:0.00}, {people} on the link)");
            _bitrateBps = wanted;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "could not change the bitrate: " + ex);
        }
    }
    private Surface? _encoderInput;
    private FrameLayout? _overlay;
    private GridLayout? _grid;
    private TextureView? _localView;
    private Thread? _drain;
    private volatile bool _running;
    private bool _useFrontCamera = true;
    private byte[]? _codecConfig;
    private int _decoderCap = -1;
    private bool _disposed;

    /// <summary>
    /// One person on camera: their decoder, and the tile it draws on.
    ///
    /// <para>
    /// A TextureView rather than a SurfaceView for each. Several SurfaceViews in one window fight over
    /// z-order and the losers vanish entirely, which in a group call means a participant who is
    /// present, decoding, and simply invisible — one of the harder failures to explain.
    /// </para>
    /// </summary>
    private sealed class Stream_(string who)
    {
        public string Who { get; } = who;
        public TextureView? View { get; set; }
        public MediaCodec? Decoder { get; set; }
        public bool Ready { get; set; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Stream_> _streams =
        new(StringComparer.Ordinal);

    public bool IsPresent => global::Android.App.Application.Context.PackageManager?
        .HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureCameraAny) ?? false;

    public string? UnavailableReason => IsPresent ? null : "this phone has no camera";

    public bool IsRunning => _running;

    /// <summary>
    /// How many H.264 streams this phone can decode at once — asked of the hardware, not assumed.
    ///
    /// <para>
    /// <c>GetMaxSupportedInstances</c> is the platform answering the question directly, which is worth
    /// far more than any table of handsets. Asked this way, the two test phones declare sixteen
    /// (Kirin 710) and thirty-two (MT6768) — not the two to four an earlier version of this code
    /// assumed. See PROTOCOL_SPEC §10.10; the assumption was wrong by an order of magnitude, and the
    /// decoder count turns out not to be what caps a group video call on this hardware.
    /// </para>
    ///
    /// <para>
    /// It is still an upper bound rather than a promise: the vendor declares it at a reference
    /// resolution, with nothing else running, and thermal and memory pressure arrive long before
    /// thirty-two. One instance is subtracted for this phone's own encoder, which competes for the
    /// same pool on most parts, and a phone that will not answer gets two — showing fewer people is
    /// recoverable, and configuring a decoder that fails is not.
    /// </para>
    /// </summary>
    public int MaxConcurrentStreams
    {
        get
        {
            if (_decoderCap >= 0) return _decoderCap;

            var found = 2;
            try
            {
                var list = new MediaCodecList(MediaCodecListKind.RegularCodecs);
                foreach (var info in list.GetCodecInfos() ?? [])
                {
                    if (info.IsEncoder) continue;
                    if (!info.GetSupportedTypes()!.Any(t => string.Equals(t, Mime, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var instances = info.GetCapabilitiesForType(Mime)?.MaxSupportedInstances ?? 0;
                    if (instances > found) found = instances;
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Info("AetherVideo", "could not ask about decoders: " + ex.Message);
            }

            // Leave one for our own encoder. The upper bound is a SCREEN limit, not a decoder one:
            // these phones declare sixteen and thirty-two, and nobody can usefully look at that many
            // faces on a handset. Naming it for what it is stops the next person reading it as a
            // hardware claim, which is how the wrong number got in here to begin with.
            _decoderCap = Math.Clamp(found - 1, 1, UsefulTilesOnAPhoneScreen);
            global::Android.Util.Log.Info("AetherVideo", "this phone can decode " + _decoderCap + " streams at once");
            return _decoderCap;
        }
    }

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

        // A grid rather than one big picture, because a group call has several. With one person in it
        // the grid is one cell filling the screen, so the 1:1 case costs nothing.
        _grid = new GridLayout(activity) { ColumnCount = 1, RowCount = 1 };
        _overlay.AddView(_grid, new FrameLayout.LayoutParams(
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

        // …and below an OPAQUE WebView is below a wall. The page goes see-through in CSS while a
        // camera is on, but a WebView paints its own background underneath the page regardless, so
        // the video was drawn correctly, decoded correctly, and covered up. Both phones showed the
        // chat list while a live camera streamed behind it.
        SetWebViewTransparent(content, true);
    }

    /// <summary>
    /// Let the video behind the page be seen — or put the page's own background back.
    /// </summary>
    /// <remarks>
    /// Found by walking the view tree rather than held as a reference: the WebView belongs to MAUI's
    /// BlazorWebView and is rebuilt when the handler is, so anything cached here would eventually
    /// point at a view that is no longer on screen.
    /// </remarks>
    private static void SetWebViewTransparent(ViewGroup root, bool transparent)
    {
        for (var i = 0; i < root.ChildCount; i++)
        {
            var child = root.GetChildAt(i);
            if (child is global::Android.Webkit.WebView web)
            {
                web.SetBackgroundColor(transparent ? Color.Transparent : Color.Black);
                continue;
            }
            if (child is ViewGroup group) SetWebViewTransparent(group, transparent);
        }
    }

    /// <summary>Build the H.264 encoder and take its input surface for the camera to draw onto.</summary>
    private bool StartEncoder()
    {
        try
        {
            var format = MediaFormat.CreateVideoFormat(Mime, Width, Height)!;
            format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
            format.SetInteger(MediaFormat.KeyBitRate, StartingBitrateBps);
            _bitrateBps = StartingBitrateBps;
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

    /// <summary>
    /// The thread camera2 delivers its callbacks on.
    ///
    /// <para>
    /// Every camera2 entry point takes a Handler, and passing null means "use the calling thread's
    /// Looper". Video is started from a background thread, which has none — so the P30 happened to
    /// work and merlin threw <c>IllegalArgumentException: No handler given, and current thread has no
    /// looper!</c> straight out of <c>openCamera</c>. Same code, same call, opposite outcome, decided
    /// entirely by which thread the caller arrived on.
    /// </para>
    /// <para>
    /// A thread of our own settles it, and is what camera2 wants anyway: callbacks land off the UI
    /// thread, so opening a session cannot stall the interface.
    /// </para>
    /// </summary>
    private global::Android.OS.Handler CameraThread
    {
        get
        {
            if (_cameraHandler is not null) return _cameraHandler;

            _cameraThread = new global::Android.OS.HandlerThread("aether-camera");
            _cameraThread.Start();
            return _cameraHandler = new global::Android.OS.Handler(_cameraThread.Looper!);
        }
    }

    private int _captureGeneration;
    private global::Android.OS.HandlerThread? _cameraThread;
    private global::Android.OS.Handler? _cameraHandler;

    private bool OpenCamera()
    {
        try
        {
            var manager = (CameraManager?)global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.CameraService);

            var id = PickCamera(manager);
            if (manager is null || id is null) return false;

            manager.OpenCamera(id, new Opened(this), CameraThread);
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

            // A TextureView has no SurfaceTexture until Android has laid it out, and the session is
            // built the moment the camera opens — so whether the preview is included is a race
            // between the camera and the view system. The P30 won it and showed a preview; merlin
            // lost it and the person holding the phone could not see themselves at all, with nothing
            // logged either way.
            //
            // Losing the race must not cost the preview permanently, so the session is rebuilt when
            // the surface turns up.
            var texture = _localView?.SurfaceTexture;
            if (texture is not null)
            {
                texture.SetDefaultBufferSize(Width, Height);
                targets.Add(new Surface(texture));
            }
            else if (_localView is not null && _localView.SurfaceTextureListener is null)
            {
                _localView.SurfaceTextureListener = new PreviewReady(this);
            }

            var request = camera.CreateCaptureRequest(CameraTemplate.Record);
            foreach (var target in targets) request.AddTarget(target);

            // Stamped, because a session that is being replaced still delivers its callbacks. The
            // preview arriving late closes one session and opens another, and the closed one's
            // OnConfigured then fires — adopting it would overwrite the live session with a dead one
            // and configure a closed session, which throws "Session has been closed".
            var generation = ++_captureGeneration;
            camera.CreateCaptureSession(targets, new Streaming(this, request.Build(), generation), CameraThread);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "could not start capture: " + ex);
        }
    }

    /// <summary>
    /// The local preview's surface has arrived. Rebuild the capture session so it is included.
    /// </summary>
    /// <remarks>
    /// Only ever fires when the preview lost the race with the camera. Closing the old session first
    /// matters: a camera can hold one session at a time, and configuring a second over a live one is
    /// refused rather than replacing it.
    /// </remarks>
    private sealed class PreviewReady(AndroidVideoIo video) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            try
            {
                video._session?.Close();
                video._session = null;
                video.StartCapture();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("AetherVideo", "could not add the local preview: " + ex);
            }
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) => true;
        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }

    private sealed class Streaming(AndroidVideoIo video, CaptureRequest request, int generation)
        : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            // A callback from a session we have already moved on from. Close it and leave the live
            // one alone.
            if (generation != video._captureGeneration)
            {
                try { session.Close(); } catch { /* already gone */ }
                return;
            }

            video._session = session;
            try { session.SetRepeatingRequest(request, null, video.CameraThread); }
            catch (Exception ex) { global::Android.Util.Log.Error("AetherVideo", "capture refused: " + ex); }
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
            => global::Android.Util.Log.Error("AetherVideo", "capture session would not configure");
    }

    // ── Playing what arrives ──────────────────────────────────────────────────

    public void Play(string from, byte[] encodedFrame)
    {
        if (_disposed || string.IsNullOrEmpty(from)) return;
        if (encodedFrame is null || encodedFrame.Length == 0) return;

        var stream = _streams.GetOrAdd(from, w => new Stream_(w));

        // The tile has to exist and be laid out before a decoder can be pointed at it, and that has
        // to happen on the UI thread. The frames that arrive in the meantime are dropped, which costs
        // a fraction of a second at the start of someone appearing — and they appear on the next
        // keyframe anyway, which is never more than a second away.
        if (!stream.Ready)
        {
            if (stream.View is null) MainThread.BeginInvokeOnMainThread(() => AddTile(stream));
            return;
        }

        var decoder = stream.Decoder;
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

    /// <summary>
    /// Give someone a tile and a decoder, and re-lay the grid around them.
    ///
    /// <para>
    /// Runs on the UI thread — a TextureView cannot be added anywhere else, and its surface does not
    /// exist until Android has laid it out, which is why the decoder is built in the listener below
    /// rather than here.
    /// </para>
    /// </summary>
    private void AddTile(Stream_ stream)
    {
        if (_disposed || stream.View is not null) return;

        var activity = Platform.CurrentActivity;
        if (activity is null) return;

        // Somebody else turned their camera on and this phone did not. The surfaces to draw them on
        // were only ever built as a side effect of starting our OWN camera, so a person who joined a
        // video call to watch had nowhere to watch it: every frame arrived, found no grid, and was
        // dropped. On screen that was a blank window with call controls floating on it, while the
        // radio moved 155KB/s of video nobody could see.
        if (_grid is null) AddOverlay();
        if (_grid is null) return;

        var view = new TextureView(activity);
        view.SurfaceTextureListener = new TileSurface(this, stream);
        stream.View = view;

        _grid.AddView(view);
        Relayout();
    }

    /// <summary>
    /// Fit however many people are on camera into the screen.
    ///
    /// <para>
    /// A square-ish grid: one fills the screen, two stack, four make a two-by-two. Sizes are set on
    /// every cell rather than left to the layout, because GridLayout does not stretch children on its
    /// own and the default is every tile collapsing to nothing.
    /// </para>
    /// </summary>
    private void Relayout()
    {
        if (_grid is null) return;

        var count = _grid.ChildCount;
        if (count == 0) return;

        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling(count / (double)columns);

        _grid.ColumnCount = columns;
        _grid.RowCount = rows;

        var width = _grid.Width > 0 ? _grid.Width / columns : ViewGroup.LayoutParams.MatchParent;
        var height = _grid.Height > 0 ? _grid.Height / rows : ViewGroup.LayoutParams.MatchParent;

        for (var i = 0; i < count; i++)
        {
            var child = _grid.GetChildAt(i);
            if (child is null) continue;

            child.LayoutParameters = new GridLayout.LayoutParams
            {
                Width = width,
                Height = height,
            };
        }
    }

    /// <summary>A tile's surface exists; point a decoder at it.</summary>
    private sealed class TileSurface(AndroidVideoIo video, Stream_ stream) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            surface.SetDefaultBufferSize(Width, Height);
            video.StartDecoder(stream, new Surface(surface));
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            video.StopDecoder(stream);
            return true;
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }

    private void StartDecoder(Stream_ stream, Surface surface)
    {
        try
        {
            var format = MediaFormat.CreateVideoFormat(Mime, Width, Height)!;

            lock (_gate)
            {
                var decoder = MediaCodec.CreateDecoderByType(Mime);
                decoder!.Configure(format, surface, null, MediaCodecConfigFlags.None);
                decoder.Start();

                stream.Decoder = decoder;
                stream.Ready = true;
            }
        }
        catch (Exception ex)
        {
            // This is the failure MaxConcurrentStreams exists to avoid — one decoder too many, and the
            // platform refuses rather than degrading. Said out loud so it is not a silent blank tile.
            global::Android.Util.Log.Error("AetherVideo",
                "no decoder left for " + stream.Who + " — " + ex.Message);
            stream.Ready = false;
        }
    }

    private void StopDecoder(Stream_ stream)
    {
        lock (_gate)
        {
            stream.Ready = false;
            try { stream.Decoder?.Stop(); } catch { /* nothing was decoded */ }
            try { stream.Decoder?.Release(); } catch { /* already gone */ }
            stream.Decoder = null;
        }
    }

    public void Forget(string who)
    {
        if (string.IsNullOrEmpty(who) || !_streams.TryRemove(who, out var stream)) return;

        StopDecoder(stream);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (stream.View is not null) _grid?.RemoveView(stream.View);
                stream.View = null;
                Relayout();
            }
            catch { /* the overlay is going away */ }
        });
    }

    /// <summary>
    /// Show or hide everybody's picture at once.
    ///
    /// <para>
    /// Kept for the 1:1 path, where "the remote" is a single person and hiding them the moment their
    /// camera goes off is what stops a frozen last frame. In a group the same thing is done per
    /// person by <see cref="Forget"/>.
    /// </para>
    /// </summary>
    public void ShowRemote(bool visible)
    {
        var grid = _grid;
        if (grid is null) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { grid.Visibility = visible ? ViewStates.Visible : ViewStates.Invisible; }
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

        // The camera thread outlives the camera unless it is stopped, and a HandlerThread left running
        // per call is a thread leaked per call.
        try { _cameraThread?.QuitSafely(); } catch { /* already gone */ }
        _cameraThread = null;
        _cameraHandler = null;

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

        foreach (var stream in _streams.Values) StopDecoder(stream);
        _streams.Clear();

        await MainThread.InvokeOnMainThreadAsync(RemoveOverlay).ConfigureAwait(false);
    }

    private void RemoveOverlay()
    {
        try
        {
            if (_overlay?.Parent is ViewGroup parent)
            {
                // Put the page's background back before the overlay goes, or every screen after this
                // call renders over a black window.
                SetWebViewTransparent(parent, false);
                parent.RemoveView(_overlay);
            }
        }
        catch { /* the activity went first */ }

        _overlay = null;
        _grid = null;
        _localView = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { StopAsync().GetAwaiter().GetResult(); } catch { /* tearing down */ }
    }
}
