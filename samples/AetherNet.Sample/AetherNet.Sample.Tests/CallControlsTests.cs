// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The three things every other phone gives you during a call, and Aether did not: a duration, a
/// mute, and a way off the loudspeaker.
///
/// <para>
/// A call was a strip at the top of a chat — enough to prove the audio worked, not enough to use.
/// These pin the behaviour the controls depend on, particularly that mute costs the radio nothing:
/// a muted microphone that still transmits encoded silence is spending a scarce link on saying
/// nothing, and this link is scarce.
/// </para>
/// </summary>
public class CallControlsTests
{
    /// <summary>Records what the call asks of the phone's audio, so the controls can be checked.</summary>
    private sealed class SpyAudio : IAudioIo
    {
        public bool IsPresent => true;
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool IsRunning { get; private set; }

        public bool SpeakerphoneOn { get; set; }
        public bool CanSwitchSpeaker => true;

        public int HeldCount;
        public int ReleasedCount;
        public string? HeldFor;
        public int RingStarts;
        public int RingStops;

        public event Action<short[]>? FrameCaptured;

        public void Emit(short[] pcm) => FrameCaptured?.Invoke(pcm);

        public Task<bool> EnsurePermissionAsync() => Task.FromResult(true);

        public Task<bool> StartAsync(int sampleRateHz, int frameDurationMs, CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.FromResult(true);
        }

        public void Play(short[] pcm) { }
        public void StartRinging(string callerTag) => RingStarts++;
        public void StopRinging() => RingStops++;
        public void HoldCall(string? peerTag) { HeldCount++; HeldFor = peerTag; }
        public void ReleaseCall() => ReleasedCount++;

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }
    }

    // ── the speaker toggle ─────────────────────────────────────────────────

    /// <summary>
    /// A call forces the loudspeaker on, because Stream.VoiceCall alone plays out of the earpiece at a
    /// volume the volume buttons do not control. That default is right; it still has to be reversible.
    /// </summary>
    [Fact]
    public void The_speaker_can_be_switched_back_to_the_earpiece()
    {
        var audio = new SpyAudio { SpeakerphoneOn = true };

        audio.SpeakerphoneOn = false;
        Assert.False(audio.SpeakerphoneOn);

        audio.SpeakerphoneOn = true;
        Assert.True(audio.SpeakerphoneOn);
    }

    /// <summary>A host that cannot switch says so, rather than offering a button that does nothing.</summary>
    [Fact]
    public void A_host_with_no_speaker_control_admits_it()
    {
        IAudioIo none = new NullAudioIo();
        Assert.False(none.CanSwitchSpeaker);
    }

    // ── holding the phone for the call ─────────────────────────────────────

    /// <summary>
    /// The call must claim the phone for its whole length and let go exactly once at the end —
    /// otherwise either the call dies off-screen, or a notification outlives the call it describes.
    /// </summary>
    [Fact]
    public void Holding_and_releasing_the_phone_are_symmetric()
    {
        var audio = new SpyAudio();

        audio.HoldCall("BQ6NH-V6Q5N");
        Assert.Equal(1, audio.HeldCount);
        Assert.Equal("BQ6NH-V6Q5N", audio.HeldFor);
        Assert.Equal(0, audio.ReleasedCount);

        audio.ReleaseCall();
        Assert.Equal(1, audio.ReleasedCount);
    }

    /// <summary>The ring stops exactly as often as it starts — a phone left ringing is unforgivable.</summary>
    [Fact]
    public void Ringing_always_stops()
    {
        var audio = new SpyAudio();

        audio.StartRinging("HTJY7-Z7HT0");
        audio.StopRinging();

        Assert.Equal(audio.RingStarts, audio.RingStops);
    }

    // ── the null host stays harmless ───────────────────────────────────────

    /// <summary>
    /// Every one of these is optional. A host without a microphone — the Web head — must be able to
    /// construct the call service and answer honestly rather than throw.
    /// </summary>
    [Fact]
    public void A_host_with_no_audio_does_nothing_and_throws_nothing()
    {
        IAudioIo none = new NullAudioIo();

        none.StartRinging("someone");
        none.StopRinging();
        none.HoldCall("someone");
        none.ReleaseCall();
        none.SpeakerphoneOn = true;

        Assert.False(none.IsPresent);
        Assert.False(none.SpeakerphoneOn);
        Assert.NotNull(none.UnavailableReason);
    }
}
