// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What the camera hardware is actually doing — the single answer to "is video on".
///
/// <para>
/// There were nine. Whether this phone was sending video could be read from <c>VideoOn</c> on the 1:1
/// call, <c>CameraOn</c> on the group call, and three separate flags inside the camera object itself;
/// whether the other person was sending it could be read from <c>TheirVideoOn</c>, the group's
/// <c>OnCamera</c> set, a per-peer <c>Ready</c> flag and the existence of an overlay. Nine answers
/// across two services and one device, with nothing tying any of them to any other.
/// </para>
///
/// <para>
/// Every fault fixed in this area has been two of those disagreeing. A button saying "Camera on" over
/// a camera that was never opened. A phone told "my camera is on" for a capture session that failed to
/// configure. A person shown "Camera off" while their camera genuinely ran. The variables were not the
/// symptom, they were the mechanism.
/// </para>
///
/// <para>
/// This is now the only thing that knows. The services still hold what they INTEND — a person asked
/// for their camera, or the far end said they turned theirs on — but intent is no longer allowed to
/// stand in for fact: the device says what it is doing and says so out loud when it changes.
/// </para>
/// </summary>
public enum CaptureState
{
    /// <summary>No camera, no encoder, nothing running.</summary>
    Idle = 0,

    /// <summary>Asked for, not yet delivering. Nothing may be promised to anyone from here.</summary>
    Starting = 1,

    /// <summary>Frames are genuinely flowing. This, and only this, is "the camera is on".</summary>
    Capturing = 2,

    /// <summary>Coming down. A camera that arrives now is closed rather than adopted.</summary>
    Stopping = 3,
}

/// <summary>
/// The rules of <see cref="CaptureState"/>, in one place.
///
/// <para>
/// Written down rather than repeated, because they were repeated: the same three comparisons appeared
/// in the camera object, in the 1:1 call service and in the group call service, each spelled slightly
/// differently — and it was the differences that produced the faults. "May it start" was written as
/// "not running", which is also true while it is stopping. "Is it on" was "running" in one place and
/// "the encoder exists" in another.
/// </para>
/// </summary>
public static class CaptureStates
{
    /// <summary>A camera may only be started from a standing stop.</summary>
    /// <remarks>
    /// Not merely "not capturing". Starting while a previous start is still opening the camera, or
    /// while a stop is still closing one, are both ways to end up with two cameras or with none.
    /// </remarks>
    public static bool CanStart(CaptureState state) => state == CaptureState.Idle;

    /// <summary>Frames are genuinely flowing. The only thing that counts as "the camera is on".</summary>
    public static bool IsOn(CaptureState state) => state == CaptureState.Capturing;

    /// <summary>
    /// Whether a camera arriving from the platform should be kept, or closed on the spot.
    /// </summary>
    /// <remarks>
    /// <c>openCamera</c> answers on a callback, and by the time it does the call may be over.
    /// Adopting one then is how a camera ends up running with nothing holding it.
    /// </remarks>
    public static bool ShouldAdopt(CaptureState state) => state == CaptureState.Starting;

    /// <summary>
    /// Whether something that believes it is sending video has to be told otherwise.
    /// </summary>
    /// <param name="state">What the device is actually doing.</param>
    /// <param name="intended">Whether the service still believes its camera is on.</param>
    /// <remarks>
    /// <para>
    /// The rule this whole rework exists for: <b>intent may not outlive the device.</b> A camera stops
    /// by itself for ordinary reasons — a link too tight to carry a picture, a failed encoder, the
    /// hardware taken by another app — and every one of those used to leave the button reading
    /// "Camera on" and, far worse, leave the far end watching a frozen last frame for the rest of the
    /// call, because the only thing that would have corrected it was a camera-off message nobody sent.
    /// </para>
    /// <para>
    /// Anything but <see cref="CaptureState.Capturing"/> means give up and say so — including
    /// <see cref="CaptureState.Starting"/>, because a camera that is still opening is not one whose
    /// pictures anybody should be promised.
    /// </para>
    /// </remarks>
    public static bool MustGiveUp(CaptureState state, bool intended) => intended && !IsOn(state);
}

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
    /// <summary>What the hardware is actually doing. The one source of truth.</summary>
    CaptureState Capture => IsRunning ? CaptureState.Capturing : CaptureState.Idle;

    /// <summary>
    /// Raised whenever <see cref="Capture"/> changes, including when the device gives up by itself.
    /// </summary>
    /// <remarks>
    /// The camera stops on its own more often than anyone expects: a link too tight to carry a
    /// picture, an encoder that fails, the hardware taken by another app. Before this existed nothing
    /// was told when that happened — the button went on saying "Camera on" over a camera that had
    /// stopped, and the far end sat watching a frozen last frame for the rest of the call, because
    /// the only thing that would have told it otherwise was a camera-off message that nobody sent.
    /// </remarks>
    event Action<CaptureState>? CaptureChanged;

    /// <summary>Shorthand for <see cref="CaptureState.Capturing"/>.</summary>
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

    /// <summary>
    /// Bring up the surfaces needed to DISPLAY someone else's video, without touching this phone's
    /// camera.
    /// </summary>
    /// <remarks>
    /// This exists because showing a picture and sending one were the same call. Receiving somebody
    /// else's video ran StartAsync purely to get a surface to draw on — and StartAsync opens the
    /// camera. Verified on device: the receiving phone's UI said "Camera" off while the system said
    /// "Device 1 is open". A camera running without the person being told is not a rendering detail.
    /// </remarks>
    Task ShowIncomingAsync() => Task.CompletedTask;

    /// <summary>
    /// Stop this phone's camera without disturbing anybody else's picture.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="StopAsync"/>, which ends the whole thing. Sending and watching are
    /// separate, and running them together was wrong in both directions: a camera that would not open
    /// tore down a picture that was already working, and turning a camera off while the other person's
    /// was still on stopped nothing at all — leaving it open and encoding behind a button reading
    /// "Camera off".
    /// </remarks>
    Task StopSendingAsync() => StopAsync();

    /// <summary>
    /// How far the far end must turn this phone's picture to draw it the way up it was taken.
    /// </summary>
    /// <remarks>
    /// A camera sensor is fixed to the board at an angle and delivers frames that way whatever the
    /// person is doing with the phone. It cannot be corrected at the encoder — this sends a bare H.264
    /// stream with no container to carry the rotation metadata in — and correcting it in pixels would
    /// mean a copy per frame. So it travels as a number and is applied by whoever draws it.
    /// </remarks>
    int CaptureRotation => 0;

    /// <summary>The size this phone's camera is actually delivering, chosen from what it publishes.</summary>
    int CaptureWidth => 1280;

    /// <inheritdoc cref="CaptureWidth"/>
    int CaptureHeight => 720;

    /// <summary>
    /// Draw this person's picture turned by the angle they announced, in the proportions they sent.
    /// </summary>
    /// <remarks>
    /// The size matters as much as the angle. A surface stretches whatever it is given to fill itself,
    /// so without the real dimensions the picture is already distorted before any rotation runs — and
    /// rotating a distorted picture produces a flattened band rather than a turned one.
    /// </remarks>
    void SetRemoteRotation(string who, int degrees, int videoWidth, int videoHeight) { }

    // ── Who is driving ────────────────────────────────────────────────────────

    /// <summary>
    /// Take charge of the camera, the encoder and the surfaces. Refused if something else has them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is one of these objects for the whole app — one camera, one encoder, one overlay, one
    /// table of who is on screen — and it was injected into both the 1:1 call service and the group
    /// call service, each a singleton, each subscribed to the radio from startup, neither aware the
    /// other existed. Between them they had seven places that tore it all down and three that brought
    /// it up.
    /// </para>
    /// <para>
    /// The failure that falls out of that needs no unusual timing: be in a group video call, decline
    /// an unrelated 1:1 call, and the decline runs a full teardown — every decoder released, every
    /// tile removed, the overlay gone. The group call carries on with no picture, and nothing anywhere
    /// reports an error, because from the 1:1 service's point of view it did exactly the right thing.
    /// </para>
    /// <para>
    /// Claiming makes that impossible to express. Reclaiming what you already hold succeeds, so this
    /// is safe to call on every path that needs the camera.
    /// </para>
    /// </remarks>
    bool Claim(object owner) => true;

    /// <summary>Whether this is the thing currently driving the camera.</summary>
    bool HeldBy(object owner) => true;

    /// <summary>Whether a claim would succeed — free, or already yours. For asking without taking.</summary>
    bool CanClaim(object owner) => true;

    /// <summary>
    /// Give it back, and tear it down — but only if it was yours. A release from anything else is
    /// ignored, which is the whole point.
    /// </summary>
    Task ReleaseAsync(object owner) => StopAsync();

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

    /// <inheritdoc />
    public event Action<CaptureState>? CaptureChanged { add { } remove { } }

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
