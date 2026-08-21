// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The camera and the screen, for the duration of a video call.
///
/// <para>
/// This is the hybrid render decision made concrete. Live video does not go through the WebView: the
/// camera writes into an encoder surface and the decoder writes onto a view surface, and neither
/// frame ever becomes managed memory. Pushing frames through Blazor would cost a copy, a PNG encode
/// and a base64 round trip per frame, and on a mid-range phone that is a slideshow rather than a
/// call. So the video is native views layered over the WebView, and Blazor keeps the controls.
/// </para>
///
/// <para>
/// Only encoded frames cross this interface. Decoding belongs to whatever has a screen, which is why
/// <see cref="Play"/> takes bytes off the radio rather than pixels — a host with no screen never
/// builds a decoder at all.
/// </para>
/// </summary>
public interface IVideoIo
{
    /// <summary>Whether this host has a camera and somewhere to draw.</summary>
    bool IsPresent { get; }

    /// <summary>Why not, in the words of someone holding the phone — or null when it does.</summary>
    string? UnavailableReason { get; }

    /// <summary>True between a successful <see cref="StartAsync"/> and its stop.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Tell the encoder how hard the link is working, so the picture can size itself to it.
    /// </summary>
    /// <param name="strain">0 comfortable, 1 failing — from <see cref="IRadioMesh.LinkStrain"/>.</param>
    /// <param name="people">How many cameras share the link.</param>
    /// <remarks>
    /// Safe to call constantly and cheap when nothing needs to change. Video that does not adapt does
    /// not degrade gracefully — it stalls, and takes the audio sharing the link down with it.
    /// </remarks>
    void SizeToLink(double strain, int people) { }

    /// <summary>What the encoder is producing right now, or 0 when it is not running.</summary>
    int BitrateBps => 0;

    /// <summary>Ask for the camera, at the moment the person taps the camera button.</summary>
    Task<bool> EnsurePermissionAsync();

    /// <summary>
    /// Put the video surfaces on screen and open the camera.
    ///
    /// <para>
    /// Returns false if it could not, in which case nothing was claimed and the call carries on as a
    /// voice call — which is the right outcome. A camera that fails to open must never take the
    /// working call down with it.
    /// </para>
    /// </summary>
    Task<bool> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Close the camera, tear the surfaces down, and give the screen back. Always safe.</summary>
    Task StopAsync();

    /// <summary>
    /// Show or hide the far end's picture.
    ///
    /// <para>
    /// Separate from starting, because the two cameras are independent: they can be showing you while
    /// your own is off, and hiding this the moment their camera goes off is what stops a frozen last
    /// frame sitting there looking like a live picture.
    /// </para>
    /// </summary>
    void ShowRemote(bool visible);

    /// <summary>
    /// How many people this phone can show at once.
    ///
    /// <para>
    /// Discovered from the hardware, never assumed. Each incoming stream needs its own decoder, and a
    /// mid-range phone commonly has two to four; past that <c>MediaCodec</c> does not degrade, it
    /// fails to configure, and a caller who guessed high gets a blank rectangle with no reason given.
    /// PROTOCOL_SPEC §10.10 makes discovering this a requirement rather than a nicety, and it is why
    /// a group video call is capped by silicon long before the radio runs out.
    /// </para>
    /// </summary>
    int MaxConcurrentStreams { get; }

    /// <summary>
    /// One encoded frame off the radio — decode it and draw it as coming from <paramref name="from"/>.
    ///
    /// <para>
    /// Named rather than anonymous because a group call has several people on camera at once, and
    /// each needs its own decoder and its own place on screen. A 1:1 call is simply the case where
    /// there is one name.
    /// </para>
    /// </summary>
    void Play(string from, byte[] encodedFrame);

    /// <summary>
    /// Stop showing someone — they left, turned their camera off, or lost the last decoder to
    /// somebody who is actually talking. Frees the decoder for whoever needs it next.
    /// </summary>
    void Forget(string who);

    /// <summary>Front camera or back. Ignored where there is only one.</summary>
    void SwitchCamera();

    /// <summary>
    /// One encoded frame from this phone's camera, ready to be sealed and sent.
    ///
    /// <para>
    /// Raised on the encoder's thread. A handler must not block: the next frame is already being
    /// captured, and time spent here is time the encoder is not draining.
    /// </para>
    /// </summary>
    event Action<byte[]>? FrameEncoded;
}

/// <summary>
/// Stands in where there is no camera and no native surface — the Web head, desktop.
///
/// <para>
/// Says no rather than pretending, on the same reasoning as everywhere else in this app: a video
/// path that starts and shows nothing is indistinguishable from a broken link, and the few lines
/// here make it impossible instead of discoverable.
/// </para>
/// </summary>
public sealed class NullVideoIo : IVideoIo
{
    public bool IsPresent => false;
    public string? UnavailableReason => "this device has no camera to call with";
    public bool IsRunning => false;

    public Task<bool> EnsurePermissionAsync() => Task.FromResult(false);
    public Task<bool> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task StopAsync() => Task.CompletedTask;
    public int MaxConcurrentStreams => 0;
    public void ShowRemote(bool visible) { }
    public void Play(string from, byte[] encodedFrame) { }
    public void Forget(string who) { }
    public void SwitchCamera() { }

    public event Action<byte[]>? FrameEncoded { add { } remove { } }
}
