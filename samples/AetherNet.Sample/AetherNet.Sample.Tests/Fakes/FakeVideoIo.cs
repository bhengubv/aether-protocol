// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;

namespace AetherNet.Sample.Tests.Fakes;

/// <summary>
/// A camera that does what it is told, and says what it was asked.
///
/// <para>
/// Every video fault this app has had was found by holding two phones, and most of them were in the
/// order things happen rather than in any one line: a camera announced before it was running, a
/// handler attached twice, a flag believed when the device could not act on it. None of that needs a
/// camera to test — it needs something that can be told to fail, told to give up halfway, and asked
/// afterwards what it was actually asked to do.
/// </para>
/// </summary>
public sealed class FakeVideoIo : IVideoIo
{
    private readonly DeviceClaim _claim = new();

    /// <summary>Whether a camera exists at all.</summary>
    public bool IsPresent { get; set; } = true;

    public string? UnavailableReason => IsPresent ? null : "no camera in this fake";

    /// <summary>Set false to make <see cref="StartAsync"/> refuse, as a camera in use by another app does.</summary>
    public bool WillStart { get; set; } = true;

    /// <summary>Set false to make the permission request refuse.</summary>
    public bool HasPermission { get; set; } = true;

    // ── the one state ─────────────────────────────────────────────────────

    public CaptureState Capture { get; private set; } = CaptureState.Idle;

    public bool IsRunning => CaptureStates.IsOn(Capture);

    public event Action<CaptureState>? CaptureChanged;

    /// <summary>
    /// The device stopping without being asked — a link too tight, an encoder that failed, the
    /// hardware taken by something else.
    /// </summary>
    /// <remarks>
    /// The case that produced the worst bug in this area: nothing was told, so the button went on
    /// saying "Camera on" and the far end sat watching a frozen frame for the rest of the call.
    /// </remarks>
    public void GiveUp() => MoveTo(CaptureState.Idle);

    private void MoveTo(CaptureState next)
    {
        if (Capture == next) return;
        Capture = next;
        CaptureChanged?.Invoke(next);
    }

    // ── what was asked of it ──────────────────────────────────────────────

    /// <summary>How many times the camera has been started.</summary>
    public int Starts { get; private set; }

    /// <summary>How many times this phone's camera alone has been stopped.</summary>
    public int SendStops { get; private set; }

    /// <summary>How many times everything has been torn down.</summary>
    public int FullStops { get; private set; }

    /// <summary>Everyone whose picture has been dropped, in order.</summary>
    public List<string> Forgotten { get; } = [];

    /// <summary>Everyone whose frames have been handed over, in order.</summary>
    public List<string> Played { get; } = [];

    /// <summary>How many times surfaces have been asked for to watch somebody else.</summary>
    public int ShowIncomingCalls { get; private set; }

    /// <summary>
    /// How many handlers are attached to <see cref="FrameEncoded"/>.
    /// </summary>
    /// <remarks>
    /// The point of counting. A path that attaches without detaching sends every frame twice, and the
    /// trigger for that path was a congested link — so the consequence of congestion was to double the
    /// traffic on it.
    /// </remarks>
    public int FrameListeners => FrameEncoded?.GetInvocationList().Length ?? 0;

    // ── the interface ─────────────────────────────────────────────────────

    public event Action<byte[]>? FrameEncoded;

    public void Raise(byte[] frame) => FrameEncoded?.Invoke(frame);

    public Task<bool> EnsurePermissionAsync() => Task.FromResult(HasPermission);

    public Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!CaptureStates.CanStart(Capture) || !IsPresent) return Task.FromResult(false);

        Starts++;
        MoveTo(CaptureState.Starting);

        if (!WillStart)
        {
            MoveTo(CaptureState.Idle);
            return Task.FromResult(false);
        }

        MoveTo(CaptureState.Capturing);
        return Task.FromResult(true);
    }

    public Task StopSendingAsync()
    {
        SendStops++;
        MoveTo(CaptureState.Idle);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        FullStops++;
        MoveTo(CaptureState.Idle);
        return Task.CompletedTask;
    }

    public Task ShowIncomingAsync()
    {
        ShowIncomingCalls++;
        return Task.CompletedTask;
    }

    public void ShowRemote(bool visible) { }

    public void Play(string from, byte[] encodedFrame) => Played.Add(from);

    public void Forget(string who) => Forgotten.Add(who);

    public void SwitchCamera() { }

    public void SizeToLink(double strain, int people) { }

    public int MaxConcurrentStreams => 4;

    public bool Claim(object owner) => _claim.Claim(owner);

    public bool HeldBy(object owner) => _claim.HeldBy(owner);

    public bool CanClaim(object owner) => _claim.CanClaim(owner);

    public Task ReleaseAsync(object owner)
        => _claim.Release(owner) ? StopAsync() : Task.CompletedTask;

    /// <summary>Take the device on something else's behalf, so a claim from the code under test fails.</summary>
    public void TakenBySomethingElse() => _claim.Claim(new object());
}
