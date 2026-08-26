// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The microphone is asked for when somebody calls, and at no other moment.
///
/// <para>
/// A permission prompt is the app asking a person to trust it, and the answer they give depends
/// entirely on whether the request makes sense at that instant. "Aether wants your microphone",
/// arriving while somebody is handing an app to a friend, is not a question with an obvious yes —
/// and for something whose whole argument is that it takes nothing it does not need, it is close to
/// self-refuting.
/// </para>
///
/// <para>
/// Measured on a P30: a microphone prompt appeared during a Touch My Blood session, and nothing in
/// the code accounts for it. These tests do not explain that; they make the guarantee real, so the
/// next time it happens the cause is somewhere these tests do not reach rather than somewhere they
/// were never pointed.
/// </para>
/// </summary>
public class MicrophoneIsOnlyAskedForWhenCallingTests
{
    /// <summary>An ear on the audio device: counts every time somebody asks for the microphone.</summary>
    private sealed class CountingAudio : IAudioIo
    {
        public int Asked { get; private set; }

        public bool IsPresent => true;
        public bool IsAvailable => true;
        public bool IsRunning { get; private set; }
        public string? UnavailableReason => null;

        public Task<bool> EnsurePermissionAsync()
        {
            Asked++;
            return Task.FromResult(true);
        }

        public event Action<short[]>? FrameCaptured { add { } remove { } }

        public Task<bool> StartAsync(int sampleRateHz, int frameDurationMs,
            CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.FromResult(true);
        }

        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public void Play(short[] pcm) { }
        public void StartRinging(string callerTag) { }
        public void StopRinging() { }
        public void HoldCall(string? peerTag) { }
        public void ReleaseCall() { }
        public bool SpeakerphoneOn { get => false; set { } }
        public bool CanSwitchSpeaker => false;
    }

    private static GroupCallService Build(out CountingAudio audio)
    {
        audio = new CountingAudio();
        return new GroupCallService(
            new FakeIdentity("AAAAA-11111"),
            new FakeSignalProtocol(),
            audio,
            video: null,
            radio: new FakeRadioMesh("AAAAA-11111"),
            store: null);
    }

    /// <summary>
    /// <b>Constructing it asks for nothing.</b>
    /// </summary>
    /// <remarks>
    /// The call services are warmed at startup so a call can ring before anybody opens a screen. That
    /// is worth doing and it must cost nothing — a warm-up that puts a permission dialog in front of
    /// somebody who has just opened the app is the worst possible first impression.
    /// </remarks>
    [Fact]
    public void Starting_the_app_does_not_ask_for_the_microphone()
    {
        using var calls = Build(out var audio);

        Assert.Equal(0, audio.Asked);
    }

    /// <summary>
    /// And it is still not asked after the radio has been listened to for a while.
    /// </summary>
    /// <remarks>
    /// Subscribing to the mesh is the other thing warm-up does. Traffic arriving on it must not be
    /// able to trigger a prompt — otherwise somebody else's phone decides when this one asks its
    /// owner for a microphone.
    /// </remarks>
    [Fact]
    public async Task Traffic_on_the_radio_cannot_trigger_a_prompt()
    {
        using var calls = Build(out var audio);

        await Task.Delay(50);

        Assert.Equal(0, audio.Asked);
    }

    /// <summary>Nor does simply disposing of it.</summary>
    [Fact]
    public void Shutting_down_does_not_ask_either()
    {
        var calls = Build(out var audio);
        calls.Dispose();

        Assert.Equal(0, audio.Asked);
    }

    /// <summary>
    /// Answering a call that is not ringing asks for nothing.
    /// </summary>
    /// <remarks>
    /// The guard order matters: check whether there is anything to join BEFORE asking for hardware.
    /// Reversed, a stray join request from anywhere in the UI produces a permission dialog for a call
    /// that does not exist.
    /// </remarks>
    [Fact]
    public async Task Joining_nothing_asks_for_nothing()
    {
        using var calls = Build(out var audio);

        Assert.False(await calls.JoinAsync());
        Assert.Equal(0, audio.Asked);
    }

    /// <summary>
    /// Calling a group with nobody in it asks for nothing.
    /// </summary>
    /// <remarks>
    /// Same rule from the other side. There is no point holding a microphone for a call that has
    /// nobody to make, so the emptiness is checked first and the person is never asked.
    /// </remarks>
    [Fact]
    public async Task Calling_an_empty_group_asks_for_nothing()
    {
        using var calls = Build(out var audio);

        Assert.False(await calls.StartAsync("nobody-here"));
        Assert.Equal(0, audio.Asked);
    }

    /// <summary>Leaving a call nobody is in asks for nothing.</summary>
    [Fact]
    public async Task Leaving_nothing_asks_for_nothing()
    {
        using var calls = Build(out var audio);

        await calls.LeaveAsync();

        Assert.Equal(0, audio.Asked);
    }
}
