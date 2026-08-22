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
    /// 960x540 at 20 fps, sized to the shape of a phone screen as much as to the radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was 640x480 — four by three, the shape of a television from 1995. Turned a quarter to be
    /// drawn upright it becomes 480x640, and a phone screen is about 1080x2100. Nothing about those
    /// two rectangles agrees: fit it and it floats in the middle of the screen, fill it and half the
    /// picture is thrown away.
    /// </para>
    /// <para>
    /// Sixteen by nine turned upright is 540x960 — an aspect of 0.5625 against the screen's 0.514,
    /// which is close enough that it very nearly fills it with almost nothing cropped. It is also a
    /// wider picture for the same shape, so it is sharper on the way out as well as better fitted.
    /// Still chosen against the radio: video has to leave room for the voice it shares the link with,
    /// and <see cref="SizeToLink"/> takes it straight back down if that turns out to be optimistic.
    /// </para>
    /// </remarks>
    /// <summary>
    /// What to ask for before the camera has been asked what it can do — and the size every remote
    /// tile is opened at, since a decoder learns its real size from the stream anyway.
    /// </summary>
    private const int Width = 1280;

    private const int Height = 720;

    /// <summary>
    /// What this camera will actually deliver, chosen from its own list rather than picked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A capture session is configured against surfaces of a specific size, and a size the camera does
    /// not publish is rejected outright — <c>onConfigureFailed</c>, no detail, no fallback. Measured:
    /// asking for 960x540, a perfectly reasonable-looking 16:9, produced "capture session would not
    /// configure" on merlin and no camera at all. 640x480 had worked only because it is one of the few
    /// sizes every camera in the world supports.
    /// </para>
    /// <para>
    /// So the size is no longer chosen here. The camera publishes what it can do and the closest thing
    /// to a phone-shaped picture is taken from that list.
    /// </para>
    /// </remarks>
    private volatile int _capWidth = Width;

    private volatile int _capHeight = Height;
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
        MediaCodec? encoder;
        lock (_gate) encoder = _encoder;
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

            // Stop SENDING, not everything. This was a full stop, which also released every decoder
            // and took the overlay down — so a link that got tight killed the picture coming IN as
            // well as the one going out, and the screen fell back to the avatar mid-call. Two-way
            // video is precisely what makes a link tight enough to reach this line, so the one case
            // that triggers it was the one case it broke worst.
            _ = StopSendingAsync();
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

    // Written on the UI thread, read from radio threads deciding whether there is anywhere to draw.
    // Volatile so a radio thread cannot go on seeing a grid that the UI thread has already removed.
    private volatile FrameLayout? _overlay;
    private volatile GridLayout? _grid;
    private volatile TextureView? _localView;

    private Thread? _drain;
    private volatile bool _running;

    /// <summary>
    /// Set the moment a stop begins, so a camera that opens after we stopped wanting it is closed
    /// rather than left running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>openCamera</c> is asynchronous: the device arrives later on <c>OnOpened</c>, and until it
    /// does there is nothing to close. A stop inside that window therefore has no reference to release,
    /// and the camera opens afterwards with nobody holding it.
    /// </para>
    /// <para>
    /// This is a real window in the code, and the guard is worth having — but it has NOT been seen on
    /// a phone, and an earlier version of this comment claimed it had. The "camera held for
    /// seventy-eight minutes" it cited came from grepping <c>dumpsys media.camera</c> for
    /// "Device 1 is open", which appears under a heading reading
    /// <c>**********Dumpsys from previous open session**********</c> — a retained snapshot of the
    /// LAST session, not live state. The live answer is <c>Active Camera Clients</c>, and on both
    /// phones it is empty whenever no call is running. Every CONNECT in the camera service's own event
    /// log has a matching DISCONNECT.
    /// </para>
    /// </remarks>
    private volatile bool _stopping;

    /// <summary>Completed when the camera is genuinely delivering frames, or has genuinely failed.</summary>
    private TaskCompletionSource<bool>? _opening;

    private bool _useFrontCamera = true;
    private byte[]? _codecConfig;
    private int _decoderCap = -1;
    private volatile bool _disposed;

    /// <summary>How far this phone's camera sensor is rotated from the way the person is holding it.</summary>
    private volatile int _captureRotation;

    /// <summary>
    /// How long to wait for the camera before calling it a failure.
    /// </summary>
    /// <remarks>
    /// Long enough for a cold open on the slowest phone here, short enough that a camera another app
    /// is holding does not leave someone staring at a button that has visibly done nothing.
    /// </remarks>
    private static readonly TimeSpan CameraOpenTimeout = TimeSpan.FromSeconds(6);

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

        /// <summary>
        /// Held for the whole of a decode and the whole of a release, so the two cannot overlap.
        /// </summary>
        /// <remarks>
        /// Its own lock rather than the class-wide one. Frames arrive on a radio thread and tiles are
        /// torn down on the UI thread, so <c>DequeueInputBuffer</c> could be running against a codec
        /// another thread had just released — a use-after-free in native code, which surfaces as a
        /// process abort with no managed stack. Sharing the class-wide gate would instead put a radio
        /// thread behind whatever the camera is doing, at fifty frames a second.
        /// </remarks>
        public readonly object Gate = new();

        public TextureView? View { get; set; }
        public MediaCodec? Decoder { get; set; }
        public volatile bool Ready;

        /// <summary>Degrees to rotate this person's picture so it is drawn the way up they are holding their phone.</summary>
        public int Rotation;

        /// <summary>The size their camera is actually sending, so the picture can be drawn in its own proportions.</summary>
        public int VideoWidth = Width;

        public int VideoHeight = Height;
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
            lock (_gate)
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
    }

    public event Action<byte[]>? FrameEncoded;

    public async Task<bool> EnsurePermissionAsync()
        => await Permissions.RequestAsync<Permissions.Camera>().ConfigureAwait(false) == PermissionStatus.Granted;

    // ── Coming up ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Open this phone's camera and start sending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every failure here unwinds with <see cref="StopSendingAsync"/>, never <see cref="StopAsync"/>.
    /// The difference is the whole point: a full stop clears every decoder and takes the overlay down,
    /// so a camera of mine that would not open used to destroy a picture of theirs that was already
    /// working — the surfaces went, the page turned opaque again, and the person watching lost the
    /// call they could see in order to be told about the one they could not send.
    /// </para>
    /// <para>
    /// It also waits for the camera to actually arrive. <c>openCamera</c> returns immediately and
    /// reports the outcome later on a callback, so returning true at that point was a guess — and a
    /// guess that got announced to the far end as "my camera is on", leaving them holding a black tile
    /// for a camera that never opened.
    /// </para>
    /// </remarks>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _running || !IsPresent) return false;

        try
        {
            _stopping = false;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                AddOverlay();

                // The overlay may already be up from watching someone else, in which case the corner
                // was deliberately hidden. There is a picture to put in it now.
                var local = _localView;
                if (local is not null) local.Visibility = ViewStates.Visible;
            }).ConfigureAwait(false);

            if (!StartEncoder()) { await StopSendingAsync().ConfigureAwait(false); return false; }

            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _opening = opened;

            if (!OpenCamera()) { await StopSendingAsync().ConfigureAwait(false); return false; }

            using var deadline = new CancellationTokenSource(CameraOpenTimeout);
            using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            using (link.Token.Register(() => opened.TrySetResult(false)))
            {
                if (!await opened.Task.ConfigureAwait(false))
                {
                    global::Android.Util.Log.Error("AetherVideo", "the camera never started delivering");
                    await StopSendingAsync().ConfigureAwait(false);
                    return false;
                }
            }

            _running = true;
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "could not start video: " + ex);
            await StopSendingAsync().ConfigureAwait(false);
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
        // Idempotent, and it has to be. Two entry points reach here now — starting this phone's
        // camera, and bringing up surfaces to watch somebody else's — and a second overlay does not
        // replace the first, it covers it: an opaque black FrameLayout on top of the one holding the
        // live tile, with _grid repointed at the new empty one. Measured on the P30: the far end's
        // decoder kept running and drawing, per-frame codec telemetry and all, into a view nobody
        // could see, while the screen showed the avatar.
        if (_disposed || _overlay is not null) return;

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
            var format = MediaFormat.CreateVideoFormat(Mime, _capWidth, _capHeight)!;
            format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
            format.SetInteger(MediaFormat.KeyBitRate, StartingBitrateBps);
            _bitrateBps = StartingBitrateBps;
            format.SetInteger(MediaFormat.KeyFrameRate, Fps);
            format.SetInteger(MediaFormat.KeyIFrameInterval, KeyframeIntervalSeconds);

            // Without this the bitrate is a suggestion, and these encoders ignore it. Measured on
            // merlin: asked for 800 kbps, delivered 9 to 12 kilobytes a frame at roughly thirty frames
            // a second — 2.6 megabits, more than three times the request, in each direction at once,
            // on a link measured at about 2.5 megabits total. Everything downstream was then trying to
            // manage congestion caused by an encoder that was never listening.
            try
            {
                format.SetInteger(MediaFormat.KeyBitrateMode,
                    (int)global::Android.Media.BitrateMode.Cbr);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Info("AetherVideo", "no bitrate mode control: " + ex.Message);
            }

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
            // Under the gate: this is reached from the caller's thread, from the camera's own
            // callbacks and from a stop running concurrently, and an unguarded lazy build races into
            // two HandlerThreads — one of which then owns callbacks nobody ever quits.
            lock (_gate)
            {
                if (_cameraHandler is not null) return _cameraHandler;

                _cameraThread = new global::Android.OS.HandlerThread("aether-camera");
                _cameraThread.Start();
                return _cameraHandler = new global::Android.OS.Handler(_cameraThread.Looper!);
            }
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

            if (facing != (int)wanted) continue;

            NoteRotation(manager, id, front: facing == (int)LensFacing.Front);
            return id;
        }

        NoteRotation(manager!, ids[0], front: false);
        return ids[0];
    }

    /// <summary>
    /// Work out which way up this camera's pictures come out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A camera sensor is soldered to the board at a fixed angle — 90° on most phones, 270° on some —
    /// and it delivers frames in that orientation regardless of how the phone is being held. Nothing in
    /// this class ever asked. The encoder was configured 640×480 landscape, the sensor handed it a
    /// landscape frame, and everyone saw a picture lying on its side.
    /// </para>
    /// <para>
    /// It cannot be fixed at the encoder. <c>KEY_ROTATION</c> is metadata a container carries, and this
    /// sends a bare H.264 elementary stream with no container, so the angle would be dropped on the
    /// wire. Rotating the pixels instead would mean a GL pass between the camera and the encoder's
    /// input surface, which is the one copy per frame this whole class exists to avoid. So the angle
    /// travels beside the video as a number, and whoever draws it turns their own surface — free, on
    /// the compositor, and correct for both ends at once.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Take the most phone-shaped picture this camera actually offers.
    /// </summary>
    /// <remarks>
    /// Nearest to sixteen by nine, and no wider than 1280 — a bigger picture on this link buys blur
    /// rather than detail, because the bitrate is what is scarce, not the pixels. Ties go to the wider
    /// one. If the camera will not answer, the default stands and the session either configures or
    /// says so honestly.
    /// </remarks>
    private void NoteCaptureSize(CameraManager manager, string id)
    {
        try
        {
            var map = manager.GetCameraCharacteristics(id)
                .Get(CameraCharacteristics.ScalerStreamConfigurationMap)
                as global::Android.Hardware.Camera2.Params.StreamConfigurationMap;

            var sizes = map?.GetOutputSizes(Java.Lang.Class.FromType(typeof(MediaCodec)));
            if (sizes is null || sizes.Length == 0) return;

            global::Android.Util.Size? best = null;
            var bestScore = double.MaxValue;

            foreach (var size in sizes)
            {
                if (size.Width > 1280 || size.Height > 1280) continue;

                // How far from 16:9, plus a gentle preference for the larger of two equally-shaped
                // options.
                var aspect = Math.Abs((size.Width / (double)size.Height) - (16.0 / 9.0));
                var score = aspect - (size.Width / 100000.0);

                if (score >= bestScore) continue;
                bestScore = score;
                best = size;
            }

            if (best is null) return;

            _capWidth = best.Width;
            _capHeight = best.Height;
            global::Android.Util.Log.Info("AetherVideo",
                $"camera {id} offers {sizes.Length} sizes — capturing at {_capWidth}x{_capHeight}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Info("AetherVideo", "could not ask about capture sizes: " + ex.Message);
        }
    }

    private void NoteRotation(CameraManager manager, string id, bool front)
    {
        NoteCaptureSize(manager, id);

        try
        {
            var sensor = (manager.GetCameraCharacteristics(id)
                .Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer)?.IntValue() ?? 0;

            var display = Platform.CurrentActivity?.WindowManager?.DefaultDisplay?.Rotation switch
            {
                SurfaceOrientation.Rotation90 => 90,
                SurfaceOrientation.Rotation180 => 180,
                SurfaceOrientation.Rotation270 => 270,
                _ => 0,
            };

            _captureRotation = VideoRotation.ForCapture(sensor, display, front);

            global::Android.Util.Log.Info("AetherVideo",
                $"camera {id} sensor {sensor}°, display {display}° → picture needs {_captureRotation}°");
        }
        catch (Exception ex)
        {
            _captureRotation = 0;
            global::Android.Util.Log.Info("AetherVideo", "could not read the sensor orientation: " + ex.Message);
        }
    }

    /// <inheritdoc />
    public int CaptureRotation => _captureRotation;

    /// <inheritdoc />
    public int CaptureWidth => _capWidth;

    /// <inheritdoc />
    public int CaptureHeight => _capHeight;

    /// <inheritdoc />
    public void SetRemoteRotation(string who, int degrees, int videoWidth, int videoHeight)
    {
        if (string.IsNullOrEmpty(who)) return;

        var stream = _streams.GetOrAdd(who, w => new Stream_(w));
        stream.Rotation = VideoRotation.Normalise(degrees);
        if (videoWidth > 0 && videoHeight > 0)
        {
            stream.VideoWidth = videoWidth;
            stream.VideoHeight = videoHeight;
        }

        var view = stream.View;
        if (view is not null)
            MainThread.BeginInvokeOnMainThread(
                () => Turn(view, stream.Rotation, stream.VideoWidth, stream.VideoHeight));
    }

    /// <summary>
    /// Turn a surface so its picture is the right way up, in its true proportions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three steps, and skipping any of them produces a picture that is visibly wrong in a different
    /// way. A TextureView stretches whatever buffer it is given to fill itself, so a 1280x720 frame is
    /// already squashed into a 1080x2100 rectangle before any transform runs. Rotating THAT and then
    /// scaling it by the view's own aspect — which is what this did — produced a flattened horizontal
    /// band across the middle of the screen with black above and below.
    /// </para>
    /// <para>
    /// So: undo the stretch first, then rotate, then scale the now-correctly-shaped picture to fit the
    /// view. It needs the video's real dimensions to do the first step, which is why they travel
    /// alongside the angle rather than being assumed.
    /// </para>
    /// </remarks>
    private static void Turn(TextureView view, int degrees, int videoWidth, int videoHeight)
    {
        try
        {
            var w = view.Width;
            var h = view.Height;
            if (w <= 0 || h <= 0 || videoWidth <= 0 || videoHeight <= 0) return;

            float cx = w / 2f, cy = h / 2f;
            var matrix = new Matrix();

            // 1. Undo the stretch. After this the picture is in its own proportions, centred, and
            //    smaller than the view.
            matrix.PostScale(videoWidth / (float)w, videoHeight / (float)h, cx, cy);

            // 2. Turn it the right way up.
            matrix.PostRotate(degrees, cx, cy);

            // 3. Grow it back until it fits. A quarter turn swaps which dimension is which, and
            //    getting that wrong is what makes a portrait picture come out letterboxed.
            var turnedWidth = degrees % 180 == 0 ? videoWidth : videoHeight;
            var turnedHeight = degrees % 180 == 0 ? videoHeight : videoWidth;

            var scale = Math.Min(w / (float)turnedWidth, h / (float)turnedHeight);
            matrix.PostScale(scale, scale, cx, cy);

            view.SetTransform(matrix);
            view.Invalidate();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Info("AetherVideo", "could not turn a surface: " + ex.Message);
        }
    }

    private sealed class Opened(AndroidVideoIo video) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera)
        {
            // The call may already be over. This callback is the FIRST moment there is a device to
            // close, so a stop that ran while the camera was opening had nothing to act on and left it
            // held — for as long as the process lived.
            if (video._disposed || video._stopping)
            {
                try { camera.Close(); } catch { /* nothing to close */ }
                video._opening?.TrySetResult(false);
                return;
            }

            lock (video._gate) video._camera = camera;
            video.StartCapture();
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            lock (video._gate) video._camera = null;
            video._opening?.TrySetResult(false);
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            global::Android.Util.Log.Error("AetherVideo", "camera error: " + error);
            camera.Close();
            lock (video._gate) video._camera = null;
            video._opening?.TrySetResult(false);
        }
    }

    /// <summary>Point the camera at both the encoder and the little local preview.</summary>
    private void StartCapture()
    {
        CameraDevice? camera;
        Surface? input;
        lock (_gate) { camera = _camera; input = _encoderInput; }
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
            var local = _localView;
            var texture = local?.SurfaceTexture;
            if (texture is not null)
            {
                texture.SetDefaultBufferSize(_capWidth, _capHeight);
                targets.Add(new Surface(texture));

                // Seeing yourself sideways is the same bug as the far end seeing you sideways, and it
                // is the one you notice first because it is your own face.
                var turn = _captureRotation;
                int vw = _capWidth, vh = _capHeight;
                if (local is not null) MainThread.BeginInvokeOnMainThread(() => Turn(local, turn, vw, vh));
            }
            else if (_localView is not null && _localView.SurfaceTextureListener is null)
            {
                _localView.SurfaceTextureListener = new PreviewReady(this);
            }

            var request = camera.CreateCaptureRequest(CameraTemplate.Record);
            foreach (var target in targets) request.AddTarget(target);

            // The camera decides the frame rate, not the encoder. KEY_FRAME_RATE is only what the
            // encoder budgets FOR — hand it thirty frames a second when it planned for twenty and it
            // spends fifty per cent more than it was asked for, every second.
            try
            {
                request.Set(CaptureRequest.ControlAeTargetFpsRange,
                    new global::Android.Util.Range(Java.Lang.Integer.ValueOf(Fps), Java.Lang.Integer.ValueOf(Fps)));
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Info("AetherVideo", "could not pin the frame rate: " + ex.Message);
            }

            // Stamped, because a session that is being replaced still delivers its callbacks. The
            // preview arriving late closes one session and opens another, and the closed one's
            // OnConfigured then fires — adopting it would overwrite the live session with a dead one
            // and configure a closed session, which throws "Session has been closed".
            var generation = System.Threading.Interlocked.Increment(ref _captureGeneration);
            camera.CreateCaptureSession(targets, new Streaming(this, request.Build(), generation), CameraThread);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "could not start capture: " + ex);
            _opening?.TrySetResult(false);
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

            lock (video._gate) video._session = session;

            try
            {
                session.SetRepeatingRequest(request, null, video.CameraThread);

                // Frames are flowing. THIS is the moment the camera is genuinely on — not when
                // openCamera returned, which is what used to be reported as success.
                video._opening?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("AetherVideo", "capture refused: " + ex);
                video._opening?.TrySetResult(false);
            }
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            global::Android.Util.Log.Error("AetherVideo", "capture session would not configure");
            video._opening?.TrySetResult(false);
        }
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

        // The whole decode happens under the stream's lock, because releasing a codec out from under
        // a thread that is mid-DequeueInputBuffer is a native crash, not an exception.
        lock (stream.Gate)
        {
        var decoder = stream.Decoder;
        if (decoder is null || !stream.Ready) return;

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

            // The angle usually arrives before the surface does — the camera-on signal beats the first
            // frame, which is what builds the tile. Applying it here is what makes it stick.
            if (stream.View is { } view) Turn(view, stream.Rotation, stream.VideoWidth, stream.VideoHeight);
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

            lock (stream.Gate)
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
        lock (stream.Gate)
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

    /// <inheritdoc />
    /// <remarks>
    /// Only the overlay — the camera is deliberately left shut. Drawing someone else's picture needs
    /// a surface and nothing more; opening this phone's camera to get one is what had the receiving
    /// handset reporting "Device 1 is open" while its own UI said the camera was off.
    /// </remarks>
    public async Task ShowIncomingAsync()
    {
        if (_disposed || _grid is not null) return;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            AddOverlay();

            // Watching is not sending. The corner is where this phone's own picture goes, and there is
            // no picture to put in it.
            //
            // INVISIBLE, never GONE. A gone view is not laid out, and a TextureView that is not laid
            // out never gets a SurfaceTexture — so the camera has nothing to point the preview at when
            // it does start. Measured: the phone that was watching first turned its own camera on,
            // sent video perfectly well and showed itself nothing, while the other phone's corner was
            // fine. Invisible keeps the surface alive behind an empty rectangle.
            var local = _localView;
            if (local is not null) local.Visibility = ViewStates.Invisible;
        }).ConfigureAwait(false);
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
            CameraCaptureSession? session;
            CameraDevice? camera;
            lock (_gate)
            {
                session = _session; _session = null;
                camera = _camera; _camera = null;
            }

            try { session?.Close(); } catch { /* already gone */ }
            try { camera?.Close(); } catch { /* already gone */ }

            // The far end is told again after this: the back camera is mounted at its own angle, so
            // flipping changes which way up this phone's picture arrives.
            OpenCamera();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AetherVideo", "could not switch camera: " + ex);
        }
    }

    // ── Going away ────────────────────────────────────────────────────────────

    /// <summary>
    /// Stop THIS phone's camera, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sending and watching are separate things, and tearing them down together was a real fault
    /// twice over. A camera of mine that failed to open ran the full stop and took the far end's live
    /// picture with it; and turning my camera off while theirs was still on ran no stop at all, so the
    /// camera stayed open and encoding with the button reading "Camera off".
    /// </para>
    /// <para>
    /// This closes the camera, the session, the encoder and the thread that drains it. Decoders,
    /// tiles and the overlay are left exactly as they are, because somebody else may still be on
    /// screen.
    /// </para>
    /// </remarks>
    public async Task StopSendingAsync()
    {
        // Before anything else: a camera still opening must be closed by its own callback, and this is
        // the flag that tells it so.
        _stopping = true;
        _running = false;
        _opening?.TrySetResult(false);

        CameraCaptureSession? session;
        CameraDevice? camera;
        global::Android.OS.HandlerThread? thread;
        MediaCodec? encoder;
        Surface? input;
        Thread? drain;

        lock (_gate)
        {
            session = _session; _session = null;
            camera = _camera; _camera = null;
            thread = _cameraThread; _cameraThread = null; _cameraHandler = null;
            encoder = _encoder; _encoder = null;
            input = _encoderInput; _encoderInput = null;
            drain = _drain; _drain = null;
            _codecConfig = null;
        }

        try { session?.Close(); } catch { /* already gone */ }
        try { camera?.Close(); } catch { /* already gone */ }

        // The camera thread outlives the camera unless it is stopped, and a HandlerThread left running
        // per call is a thread leaked per call.
        try { thread?.QuitSafely(); } catch { /* already gone */ }

        // The drain thread exits when it sees the encoder go; joining it first means the encoder is
        // never released underneath a thread still reading from it.
        if (drain is not null)
        {
            try { drain.Join(500); } catch { /* it will exit on its own */ }
        }

        try { encoder?.Stop(); } catch { /* nothing was ever encoded */ }
        try { encoder?.Release(); } catch { /* already gone */ }
        try { input?.Release(); } catch { /* already gone */ }

        // The little corner picture is this phone's own, so it goes when the camera does — hidden
        // rather than removed, so its surface survives for the next time the camera comes on.
        var local = _localView;
        if (local is not null)
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                try { local.Visibility = ViewStates.Invisible; } catch { /* the overlay went first */ }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Stop everything: this phone's camera, everybody else's picture, and the surfaces they were
    /// drawn on. For the end of a call, not for turning a camera off.
    /// </summary>
    public async Task StopAsync()
    {
        await StopSendingAsync().ConfigureAwait(false);

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
