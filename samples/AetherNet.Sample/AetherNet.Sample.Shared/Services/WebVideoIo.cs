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

    private readonly IJSRuntime _js;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DeviceClaim _claim = new();

    private IJSObjectReference? _module;
    private DotNetObjectReference<WebVideoIo>? _self;
    private volatile CaptureState _capture = CaptureState.Idle;
    private bool _disposed;

    /// <summary>What the module reported it can do, once asked. Null until then.</summary>
    private Capabilities? _caps;

    public WebVideoIo(IJSRuntime js) => _js = js;

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

        try
        {
            var module = await ModuleAsync().ConfigureAwait(false);
            return _caps = await module.InvokeAsync<Capabilities>("capabilities").ConfigureAwait(false);
        }
        catch (Exception)
        {
            return _caps = new Capabilities(false, false, false, false, false, 0);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answers from what has been asked so far rather than blocking. Anything that genuinely needs the
    /// answer — starting a camera — awaits <see cref="AskAsync"/> instead, and the first render of a
    /// call screen must not stall on a device enumeration.
    /// </remarks>
    public bool IsPresent => _caps?.Usable ?? true;

    /// <inheritdoc />
    public string? UnavailableReason => _caps is { Usable: false } caps ? caps.Missing : null;

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
        CaptureChanged?.Invoke(next);
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
    public void ReceiveChunk(byte[] frame)
    {
        if (_disposed || frame.Length == 0) return;
        FrameEncoded?.Invoke(frame);
    }

    /// <inheritdoc />
    public void Play(string from, byte[] encodedFrame)
    {
        if (_disposed || string.IsNullOrEmpty(from) || encodedFrame.Length == 0) return;
        if (_module is not { } module) return;

        // Fire and forget on purpose. A frame is worthless a moment after it was captured, so waiting
        // for the decode to be accepted would only make the next one later.
        _ = module.InvokeVoidAsync("play", from, encodedFrame);
    }

    /// <inheritdoc />
    public void Forget(string who)
    {
        if (_disposed || string.IsNullOrEmpty(who)) return;
        _ = _module?.InvokeVoidAsync("forget", who);
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
            if (!(await AskAsync().ConfigureAwait(false)).Usable) return false;

            var module = await ModuleAsync().ConfigureAwait(false);
            _self ??= DotNetObjectReference.Create(this);

            // The module reports Starting and then Capturing through ReceiveState, so the state is set
            // by what actually happened rather than by this method having returned.
            return await module.InvokeAsync<bool>("start", cancellationToken, _self, _self, _front)
                .ConfigureAwait(false);
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
    /// Nothing to do. The surfaces are elements the call screen renders, so there is no overlay to
    /// bring up and nothing to layer over anything — which is the point of moving here.
    /// </remarks>
    public Task ShowIncomingAsync() => Task.CompletedTask;

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

        _ = _module?.InvokeVoidAsync("sizeToLink", wanted);
        _bitrateBps = wanted;
    }

    private int _bitrateBps;

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

    private async ValueTask<IJSObjectReference> ModuleAsync()
        => _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ModulePath).ConfigureAwait(false);

    private async Task CallAsync(string method)
    {
        if (_disposed || _module is not { } module) return;

        try { await module.InvokeVoidAsync(method).ConfigureAwait(false); }
        catch (JSDisconnectedException) { /* the page went first */ }
        catch (ObjectDisposedException) { /* so did we */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await CallAsync("stopAll").ConfigureAwait(false);

        if (_module is not null)
        {
            try { await _module.DisposeAsync().ConfigureAwait(false); }
            catch (JSDisconnectedException) { /* the page went first */ }
        }

        _self?.Dispose();
        _gate.Dispose();
    }
}
