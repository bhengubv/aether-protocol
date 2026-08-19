// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The microphone and the earpiece — the only two things in a call that are not software.
///
/// <para>
/// Capture raises whole frames of linear PCM, 16-bit mono, at the rate the call agreed. Playback takes
/// the same. Everything between the two is the mesh's problem.
/// </para>
/// </summary>
public interface IAudioIo
{
    /// <summary>
    /// Whether the hardware is physically here — regardless of whether we may use it yet.
    ///
    /// <para>
    /// Kept apart from <see cref="IsAvailable"/> so the call button can be offered on a phone that has
    /// a microphone but has not been asked for it yet. Gating the button on the permission would put
    /// the only way to grant it behind the permission itself.
    /// </para>
    /// </summary>
    bool IsPresent { get; }

    /// <summary>Whether this host has a microphone and speaker it is allowed to use right now.</summary>
    bool IsAvailable { get; }

    /// <summary>Why not, in the words of someone holding the phone — or null when it is available.</summary>
    string? UnavailableReason { get; }

    /// <summary>True between <see cref="StartAsync"/> and <see cref="StopAsync"/>.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Ring, because a phone that does not make a sound has not really rung.
    ///
    /// <para>
    /// A call arriving with nothing but a banner is only answered by someone already looking at the
    /// screen, which is the one person who did not need telling. Uses the phone's own ringtone rather
    /// than shipping one, so it matches every other call the person gets and respects the silent
    /// switch and volume they have already chosen.
    /// </para>
    /// </summary>
    void StartRinging();

    /// <summary>Stop ringing — answered, declined, or the caller gave up.</summary>
    void StopRinging();

    /// <summary>
    /// One frame from the microphone, exactly <c>sampleRateHz × frameDurationMs / 1000</c> samples.
    /// Raised on a capture thread, so a handler must not block — a call's audio is a hard real-time
    /// budget, and time spent here is time the next frame is not being read.
    /// </summary>
    event Action<short[]>? FrameCaptured;

    /// <summary>
    /// Ask for whatever this host needs before a call can be placed, and wait for the answer.
    ///
    /// <para>
    /// Asked at the moment someone taps Call, rather than during setup, because that is the moment the
    /// reason for it is obvious. Returns whether the microphone may now be used.
    /// </para>
    /// </summary>
    Task<bool> EnsurePermissionAsync();

    /// <summary>Open the microphone and speaker for a call at this rate and frame size.</summary>
    Task<bool> StartAsync(int sampleRateHz, int frameDurationMs, CancellationToken cancellationToken = default);

    /// <summary>Play one frame. Frames are expected to arrive at the pace they were captured.</summary>
    void Play(short[] pcm);

    /// <summary>Close the microphone and speaker. Safe to call when not running.</summary>
    Task StopAsync();
}

/// <summary>
/// Stands in on hosts with no audio hardware to speak of — the Web head, desktop. A call there is
/// honestly impossible rather than silently silent.
/// </summary>
public sealed class NullAudioIo : IAudioIo
{
    public bool IsPresent => false;
    public bool IsAvailable => false;
    public string? UnavailableReason => "this device has no microphone Aether can use";
    public bool IsRunning => false;

    public event Action<short[]>? FrameCaptured { add { } remove { } }

    public Task<bool> EnsurePermissionAsync() => Task.FromResult(false);

    public Task<bool> StartAsync(int sampleRateHz, int frameDurationMs, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public void Play(short[] pcm) { }
    public void StartRinging() { }
    public void StopRinging() { }
    public Task StopAsync() => Task.CompletedTask;
}
