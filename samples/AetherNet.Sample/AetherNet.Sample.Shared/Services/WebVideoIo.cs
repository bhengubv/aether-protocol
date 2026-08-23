// SPDX-License-Identifier: MIT

using Microsoft.JSInterop;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Live video, in the layer every head already shares.
///
/// <para>
/// This is the whole of the platform work now. Capture, encode, decode and draw happen in a JavaScript
/// module in the Shared project, which means one implementation serves the Android app, the web head,
/// and — the reason it matters — an iOS head that does not exist yet. The native Android version it
/// replaces could only ever serve one of those three.
/// </para>
///
/// <para>
/// It is also far less code, because most of what the native version did was work the browser had
/// already done. <c>getUserMedia</c> returns a stream that is already the right way up, so the sensor
/// orientation, the rotation matrices and the rotation byte on the wire are all simply unnecessary.
/// Mirroring a self-view is one CSS rule. The picture is a DOM element, so it lays out with the
/// controls rather than underneath them — no overlay, no transparent WebView, no hidden tab bar.
/// </para>
///
/// <para>
/// What is left here is the seam: <see cref="IVideoIo"/> keeps its shape, the call services keep their
/// ownership and state rules, and this marshals between them and the module.
/// </para>
/// </summary>
public sealed class WebVideoIo : IVideoIo, IAsyncDisposable
{
    private const string ModulePath = "./_content/AetherNet.Sample.Shared/js/aether-video.js";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DeviceClaim _claim = new();

    /// <summary>
    /// The frame channel, when this head has one.
    /// </summary>
    /// <remarks>
    /// Frames do not belong on the JavaScript bridge. It is one message channel shared by every
    /// interop call in both directions and by the renderer's dispatcher, and it saturated at about
    /// four frames a second each way on a Redmi Note 9 — past which the answers stopped coming back
    /// entirely. A loopback WebSocket carries binary, has nothing else on it, and is full duplex.
    /// </remarks>
    private readonly IVideoBridge _bridge;

    private bool _bridgeWired;

    private IJSObjectReference? _module;
    private DotNetObjectReference<WebVideoIo>? _self;
    private volatile CaptureState _capture = CaptureState.Idle;
    private volatile bool _disposed;

    /// <summary>
    /// The renderer's dispatcher. Every call into JavaScript goes through it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a nicety. Frames arrive on radio threads — <c>Play</c> is reached from the packet handler
    /// twenty times a second — and an Android WebView requires every <c>evaluateJavascript</c> on the
    /// UI thread. Blazor does not marshal that for you: calling the runtime from a background thread
    /// is undefined, and the failure is the kind that looks like a flaky radio rather than a threading
    /// bug. Capture from the encoder callback and state changes from JavaScript arrive on their own
    /// threads too.
    /// </para>
    /// <para>
    /// The dispatcher is the framework's own answer to this, and it is the reason this type takes one
    /// alongside the runtime rather than just the runtime.
    /// </para>
    /// </remarks>
    private Func<Func<Task>, Task>? _onPage;

    /// <summary>What the module reported it can do, once asked. Null until then.</summary>
    private Capabilities? _caps;

    /// <summary>
    /// The page's JavaScript runtime, handed over when a page appears.
    /// </summary>
    /// <remarks>
    /// Not taken in the constructor, and that is not a style choice. <c>IJSRuntime</c> is scoped —
    /// one per circuit on a server-rendered head — while the call services that own this are
    /// singletons, so constructing it with one makes the container refuse to build at all. The web
    /// head is what caught that: on the phone there is a single scope, and it would have looked
    /// perfectly fine right up until the day somebody ran the web app.
    /// </remarks>
    private IJSRuntime? _js;

    public WebVideoIo(IVideoBridge? bridge = null) => _bridge = bridge ?? new NoVideoBridge();

    /// <summary>Give the device the page it lives in. A new page replaces the old one.</summary>
    /// <param name="onPage">
    ///   Runs work on the page's own thread. A component's <c>InvokeAsync</c> is exactly this, and it
    ///   already runs inline when the caller is on the right thread — so a call that does not need
    ///   marshalling does not pay for it.
    /// </param>
    public void Attach(IJSRuntime js, Func<Func<Task>, Task> onPage)
    {
        lock (_fields)
        {
            if (_disposed || ReferenceEquals(_js, js)) return;

            // A different page means the imported module and the callbacks belong to something that
            // is gone. Drop them rather than calling into it.
            _js = js;
            _onPage = onPage;
            _module = null;
            _caps = null;
        }
    }

    /// <summary>Guards the handful of fields that more than one thread touches.</summary>
    private readonly object _fields = new();

    /// <summary>
    /// Run something against the module, on the thread the framework requires.
    /// </summary>
    /// <remarks>
    /// Everything that reaches JavaScript goes through here. <c>CheckAccess</c> first, so a call that
    /// is already on the right thread is not bounced through the queue for nothing — which matters
    /// when it happens twenty times a second.
    /// </remarks>
    private Task OnPageAsync(Func<IJSObjectReference, Task> work)
    {
        IJSObjectReference? module;
        Func<Func<Task>, Task>? onPage;
        lock (_fields) { module = _module; onPage = _onPage; }

        if (_disposed || module is null) return Task.CompletedTask;
        return onPage is null ? Guarded(module, work) : onPage(() => Guarded(module, work));
    }

    /// <summary>
    /// Start something on the page's thread and do not wait for it to come back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the frame paths, and the difference is not small. <see cref="OnPageAsync"/> awaits the
    /// round trip INSIDE the dispatcher, which turns every frame into a blocking call on the UI
    /// thread and lets each one hold the queue until the WebView has answered. Measured on device
    /// with that version running: the canvas was being drawn 8 times a second on one phone and
    /// <b>0.4</b> times a second on the other, against twenty frames arriving. A quarter-second
    /// slideshow, and the same UI thread janking for the better part of a second at a time.
    /// </para>
    /// <para>
    /// The thread requirement is that the call be MADE on the dispatcher, not that anyone wait for
    /// its answer. Starting it and returning satisfies the WebView and leaves the queue free for the
    /// next frame — which is what a real-time path needs, since a frame that has waited its turn is
    /// already too late to be worth showing.
    /// </para>
    /// </remarks>
    private void PostToPage(Func<IJSObjectReference, Task> work)
    {
        IJSObjectReference? module;
        Func<Func<Task>, Task>? onPage;
        lock (_fields) { module = _module; onPage = _onPage; }

        if (_disposed || module is null) return;

        if (onPage is null) { _ = Guarded(module, work); return; }

        _ = onPage(() =>
        {
            _ = Guarded(module, work);
            return Task.CompletedTask;
        });
    }

    private static async Task Guarded(IJSObjectReference module, Func<IJSObjectReference, Task> work)
    {
        try { await work(module).ConfigureAwait(false); }
        catch (JSDisconnectedException) { /* the page went first */ }
        catch (ObjectDisposedException) { /* so did we */ }
        catch (TaskCanceledException) { /* the circuit is closing */ }
    }

    // ── what this device can do ───────────────────────────────────────────────

    /// <summary>The module's answer to "can this browser carry a video call at all".</summary>
    private sealed record Capabilities(
        bool Secure, bool GetUserMedia, bool WebCodecs, bool FrameCallback, bool Codec, int Cameras)
    {
        /// <summary>Every part has to be there. A missing one is a call that cannot start.</summary>
        public bool Usable => Secure && GetUserMedia && WebCodecs && FrameCallback && Codec;

        /// <summary>Which part is missing, in words a person can act on.</summary>
        public string Missing =>
            !Secure ? "this page is not a secure context, so the camera cannot be opened"
            : !GetUserMedia ? "this device has no camera the browser can reach"
            : !WebCodecs ? "this browser cannot encode video (WebCodecs is missing)"
            : !FrameCallback ? "this browser cannot pace video frames"
            : !Codec ? "this device has no H.264 encoder the browser can use"
            : "video is available";
    }

    /// <summary>
    /// Asked once, lazily, and remembered.
    /// </summary>
    /// <remarks>
    /// Deliberately not asked during startup. It touches <c>enumerateDevices</c>, and doing that while
    /// the app is warming up competes with the work a person is actually waiting on.
    /// </remarks>
    private async ValueTask<Capabilities> AskAsync()
    {
        if (_caps is { } known) return known;
        if (await ModuleAsync().ConfigureAwait(false) is null)
            return _caps = new Capabilities(false, false, false, false, false, 0);

        Capabilities? found = null;
        await OnPageAsync(async m =>
            found = await m.InvokeAsync<Capabilities>("capabilities").ConfigureAwait(false))
            .ConfigureAwait(false);

        return _caps = found ?? new Capabilities(false, false, false, false, false, 0);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answers from what has been asked so far rather than blocking. Anything that genuinely needs the
    /// answer — starting a camera — awaits <see cref="AskAsync"/> instead, and the first render of a
    /// call screen must not stall on a device enumeration.
    /// </remarks>
    public bool IsPresent => _js is not null && (_caps?.Usable ?? true);

    /// <inheritdoc />
    public string? UnavailableReason =>
        _js is null ? "there is no page to show video in"
        : _caps is { Usable: false } caps ? caps.Missing
        : null;

    // ── the one state ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public CaptureState Capture => _capture;

    /// <inheritdoc />
    public bool IsRunning => CaptureStates.IsOn(_capture);

    /// <inheritdoc />
    public event Action<CaptureState>? CaptureChanged;

    /// <summary>
    /// The module telling us what the camera is doing. Called from JavaScript.
    /// </summary>
    /// <remarks>
    /// The camera stops for ordinary reasons the .NET side cannot see — a track ending, an encoder
    /// failing, another tab or app taking the device. This is how intent is stopped from outliving it.
    /// </remarks>
    /// <remarks>
    /// Arrives from JavaScript, and the listeners are the call services — which subscribe and
    /// unsubscribe from wherever a camera button was handled. The handler is snapshotted so an
    /// unsubscribe landing between the null check and the call cannot turn this into a crash.
    /// </remarks>
    [JSInvokable]
    public void ReceiveState(string state)
    {
        var next = state switch
        {
            "Starting" => CaptureState.Starting,
            "Capturing" => CaptureState.Capturing,
            "Stopping" => CaptureState.Stopping,
            _ => CaptureState.Idle,
        };

        if (_capture == next) return;
        _capture = next;

        var listeners = CaptureChanged;
        listeners?.Invoke(next);
    }

    // ── frames ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public event Action<byte[]>? FrameEncoded;

    /// <summary>One encoded frame from this device's camera. Called from JavaScript.</summary>
    /// <remarks>
    /// Twenty times a second, a few kilobytes each. It is a direct in-process call in a hybrid app —
    /// there is no serialization boundary here worth avoiding — and the frame goes straight on to the
    /// mesh from the handler, which is where the send lane takes over.
    /// </remarks>
    [JSInvokable]
    public void ReceiveChunk(byte[] frame) => OnFrameFromPage(frame);

    /// <summary>
    /// One encoded frame from this device's camera, however it arrived.
    /// </summary>
    /// <remarks>
    /// Over the bridge when this head has one, and through JavaScript interop when it does not. The
    /// two paths converge here so nothing downstream has to know which was used.
    /// </remarks>
    private void OnFrameFromPage(byte[] frame)
    {
        if (_disposed || frame.Length == 0) return;

        var listeners = FrameEncoded;
        listeners?.Invoke(frame);
    }

    /// <inheritdoc />
    public void Play(string from, byte[] encodedFrame)
    {
        if (_disposed || string.IsNullOrEmpty(from) || encodedFrame.Length == 0) return;

        // The bridge, when the page is on it. No dispatcher, no base64, and nothing else sharing it.
        if (_bridge.PageConnected)
        {
            _bridge.SendToPage(from, encodedFrame);
            return;
        }

        if (_module is null)
        {
            // Frames arriving with nowhere to put them. Load the module and drop this one — the next
            // is at most a keyframe away, and a frame held while an import completes is stale anyway.
            //
            // Belt as well as braces: ShowIncomingAsync loads it when their camera is announced, and
            // this catches the case where frames arrive before that announcement does. Dropping
            // silently for the whole call, which is what happened, is not something to leave depending
            // on one message arriving first.
            _ = ModuleAsync();
            return;
        }

        // Fire and forget on purpose. A frame is worthless a moment after it was captured, so waiting
        // for the decode to be accepted would only make the next one later. It still has to reach the
        // page on the page's own thread — this arrives on a radio thread.
        PostToPage(m => m.InvokeVoidAsync("play", from, encodedFrame).AsTask());
    }

    /// <inheritdoc />
    public void Forget(string who)
    {
        if (_disposed || string.IsNullOrEmpty(who)) return;
        PostToPage(m => m.InvokeVoidAsync("forget", who).AsTask());
    }

    // ── coming up and going away ──────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// There is no separate permission step in the browser: <c>getUserMedia</c> IS the request, and the
    /// prompt appears at the moment the camera is asked for. This checks the device can do the job at
    /// all, which is the honest equivalent — and the reason a person is given when it cannot.
    /// </remarks>
    public async Task<bool> EnsurePermissionAsync() => (await AskAsync().ConfigureAwait(false)).Usable;

    /// <inheritdoc />
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !CaptureStates.CanStart(_capture)) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CaptureStates.CanStart(_capture)) return false;
            if (await ModuleAsync().ConfigureAwait(false) is null) return false;
            if (!(await AskAsync().ConfigureAwait(false)).Usable) return false;

            var bridge = await BridgeAsync().ConfigureAwait(false);

            _self ??= DotNetObjectReference.Create(this);

            // The module reports Starting and then Capturing through ReceiveState, so the state is set
            // by what actually happened rather than by this method having returned.
            var started = false;
            await OnPageAsync(async m =>
                started = await m.InvokeAsync<bool>("start", cancellationToken, _self, _self, _front, bridge)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            return started;
        }
        catch (Exception)
        {
            ReceiveState("Idle");
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task StopSendingAsync() => await CallAsync("stopSending").ConfigureAwait(false);

    /// <inheritdoc />
    public async Task StopAsync() => await CallAsync("stopAll").ConfigureAwait(false);

    private bool _front = true;

    /// <inheritdoc />
    public void SwitchCamera()
    {
        if (_disposed) return;
        _front = !_front;
        _ = CallAsync("switchCamera");
    }

    // ── the screen ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// There are no surfaces to bring up — they are elements the call screen renders — but there IS
    /// the module, and this is the moment to make sure it exists.
    /// </para>
    /// <para>
    /// It is imported lazily on first use, and the only thing that used to import it was starting a
    /// camera. So a phone that never turned its own camera on never loaded the module, and every
    /// frame the other person sent was dropped on arrival because there was nowhere to send it:
    /// <see cref="Play"/> returns immediately when there is no module. Watching without sending —
    /// somebody showing you something — was impossible, and it failed in total silence.
    /// </para>
    /// <para>
    /// Measured: merlin sat in a call with the P30 sending 9 frames a second at the radio, its own
    /// canvas present and TheirVideoOn true, and played 0.0 for six minutes.
    /// </para>
    /// </remarks>
    public async Task ShowIncomingAsync() => await ModuleAsync().ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// Also nothing. Whether a picture is on screen is decided by the call screen's own markup, from
    /// the same state everything else reads. It does not need telling twice.
    /// </remarks>
    public void ShowRemote(bool visible) { }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing either. The stream arrives display-oriented, and a self-view is mirrored by CSS on the
    /// element rather than by transforming pixels.
    /// </remarks>
    public void SetRemoteRotation(string who, int degrees, int videoWidth, int videoHeight) { }

    // ── sizing ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void SizeToLink(double strain, int people)
    {
        if (_disposed || !CaptureStates.IsOn(_capture)) return;

        var wanted = MediaBitrate.Video(BitrateBps, strain, people);

        // Below the floor there is no picture worth the bytes, and one that is unwatchable still
        // crowds out the voice — which is the half of a call people actually need.
        if (wanted <= 0) { _ = StopSendingAsync(); return; }

        PostToPage(m => m.InvokeVoidAsync("sizeToLink", wanted).AsTask());
        _bitrateBps = wanted;
    }

    private volatile int _bitrateBps;

    /// <inheritdoc />
    public int BitrateBps => CaptureStates.IsOn(_capture) ? (_bitrateBps > 0 ? _bitrateBps : 400_000) : 0;

    /// <inheritdoc />
    /// <remarks>
    /// Four, and a screen limit rather than a hardware one. The browser will decode as many streams as
    /// the silicon allows, and nobody can tell one face from another past four on a handset.
    /// </remarks>
    public int MaxConcurrentStreams => 4;

    // ── who is driving ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Claim(object owner) => !_disposed && _claim.Claim(owner);

    /// <inheritdoc />
    public bool HeldBy(object owner) => _claim.HeldBy(owner);

    /// <inheritdoc />
    public bool CanClaim(object owner) => !_disposed && _claim.CanClaim(owner);

    /// <inheritdoc />
    public async Task ReleaseAsync(object owner)
    {
        if (_claim.Release(owner)) await StopAsync().ConfigureAwait(false);
    }

    // ── plumbing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Import the module, on the page's thread, once.
    /// </summary>
    /// <remarks>
    /// The import is itself a call into JavaScript, so it has the same thread requirement as every
    /// other one — and it is reached from <see cref="StartAsync"/>, which a call service invokes from
    /// wherever the camera button happened to be handled.
    /// </remarks>
    /// <summary>
    /// Bring the frame channel up and describe it to the page, or say there is not one.
    /// </summary>
    /// <remarks>
    /// Wired once. The bridge outlives any single call — the page holds its socket open for as long
    /// as it is loaded — so subscribing per call would attach a handler per call.
    /// </remarks>
    private async Task<VideoBridgeEndpoint?> BridgeAsync()
    {
        var endpoint = await _bridge.StartAsync().ConfigureAwait(false);
        if (endpoint is null) return null;

        lock (_fields)
        {
            if (!_bridgeWired)
            {
                _bridge.FrameFromPage += OnFrameFromPage;
                _bridgeWired = true;
            }
        }

        return endpoint;
    }

    private async Task<IJSObjectReference?> ModuleAsync()
    {
        IJSRuntime? js;
        Func<Func<Task>, Task>? onPage;
        IJSObjectReference? already;
        lock (_fields) { js = _js; onPage = _onPage; already = _module; }

        if (already is not null) return already;
        if (js is null) return null;

        IJSObjectReference? imported = null;

        async Task Import()
        {
            try { imported = await js.InvokeAsync<IJSObjectReference>("import", ModulePath).ConfigureAwait(false); }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
        }

        if (onPage is null) await Import().ConfigureAwait(false);
        else await onPage(Import).ConfigureAwait(false);

        if (imported is null) return null;

        lock (_fields) { _module ??= imported; return _module; }
    }

    private Task CallAsync(string method)
        => OnPageAsync(m => m.InvokeVoidAsync(method).AsTask());

    public async ValueTask DisposeAsync()
    {
        IJSObjectReference? module;
        lock (_fields)
        {
            if (_disposed) return;

            // Set BEFORE the teardown, so anything arriving from a radio thread or from JavaScript
            // while this runs turns into a no-op rather than a call into something half gone.
            _disposed = true;
            module = _module;
            _module = null;
        }

        if (module is not null)
        {
            try
            {
                await Guarded(module, m => m.InvokeVoidAsync("stopAll").AsTask()).ConfigureAwait(false);
                await module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException) { /* the page went first */ }
            catch (ObjectDisposedException) { /* so did we */ }
        }

        if (_bridgeWired) _bridge.FrameFromPage -= OnFrameFromPage;
        (_bridge as IDisposable)?.Dispose();

        _self?.Dispose();
        _gate.Dispose();
    }
}
