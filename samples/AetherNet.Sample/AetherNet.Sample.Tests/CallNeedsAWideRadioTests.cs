// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A call is refused when no radio on this phone could ever carry one.
///
/// <para>
/// Measured on real handsets, Bluetooth moves about eleven kilobits and a call needs about
/// twenty-four (PROTOCOL_SPEC §5.5). Placing one over Bluetooth alone does not produce a poor call —
/// it produces one that rings, answers, connects and stays completely silent, with both people
/// watching a running timer and believing it works. That is the worst failure this app can have,
/// because nothing anywhere says anything is wrong.
/// </para>
///
/// <para>
/// The distinction these pin is the one that makes the check safe: the <b>current</b> link being
/// narrow is normal and must not refuse anything, because Wi-Fi Direct is brought up by the call
/// itself and brokered over Bluetooth. Only a phone with no wide radio at all is told no.
/// </para>
/// </summary>
public class CallNeedsAWideRadioTests
{
    private const long Ble = 11_000;              // measured 2026-08-20
    private const long WifiDirect = 250_000_000;  // as declared

    /// <summary>A phone with a microphone, and nothing else to say.</summary>
    private sealed class PresentAudio : IAudioIo
    {
        public bool IsPresent => true;
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool IsRunning => false;
        public bool SpeakerphoneOn { get; set; }
        public bool CanSwitchSpeaker => true;

        public event Action<short[]>? FrameCaptured;

        public void StartRinging(string callerTag) { }
        public void StopRinging() { }
        public void HoldCall(string? peerTag) { }
        public void ReleaseCall() { }
        public Task<bool> EnsurePermissionAsync() => Task.FromResult(true);
        public Task<bool> StartAsync(int s, int f, CancellationToken ct = default) => Task.FromResult(true);
        public void Play(short[] pcm) { }
        public Task StopAsync() { _ = FrameCaptured; return Task.CompletedTask; }
    }

    /// <summary>A phone that has Wi-Fi Direct hardware, or does not.</summary>
    private sealed class Group(bool supported) : IWifiDirectGroup
    {
        /// <summary>Nothing to narrate in a test double.</summary>
        public event Action<string>? Status { add { } remove { } }

        public bool IsSupported => supported;
        public Task<WifiDirectCredentials?> HostAsync(CancellationToken ct = default)
            => Task.FromResult<WifiDirectCredentials?>(null);
        public Task<WifiDirectCredentials?> HostAsync(WifiDirectCredentials? wanted, CancellationToken ct = default)
            => Task.FromResult<WifiDirectCredentials?>(null);
        public Task<bool> JoinAsync(WifiDirectCredentials c, CancellationToken ct = default) => Task.FromResult(false);
        public Task LeaveAsync() => Task.CompletedTask;
        public event Action? GroupLost { add { } remove { } }
    }

    private static CallService APhone(long linkBps, bool linked = true, bool hasWifiDirect = true)
    {
        var me = new FakeIdentity();
        var signal = new FakeSignalProtocol();
        var radio = new FakeRadioMesh(me.AetherTag) { LinkBandwidthBps = linkBps };
        if (linked) radio.Link();

        // Having the radio at all is the capability now — there is no broker in front of it. The radio
        // brings itself up, so "narrow link today, wide radio present" is read straight off the mesh.
        radio.Radios = hasWifiDirect
            ? [new RadioInfo("Wi-Fi Direct", true), new RadioInfo("BLE", true)]
            : [new RadioInfo("BLE", true)];

        return new CallService(me, signal, new PresentAudio(), radio: radio);
    }

    // ── refusing what cannot work ──────────────────────────────────────────

    /// <summary>
    /// Bluetooth only, on a phone with no Wi-Fi Direct — nothing wider is coming, so the call is
    /// refused rather than rung.
    /// </summary>
    [Fact]
    public void A_phone_with_only_bluetooth_cannot_call()
    {
        var call = APhone(Ble, hasWifiDirect: false);

        Assert.False(call.CanCall);
    }

    /// <summary>
    /// And it says why, in words someone holding the phone can act on. "Cannot call" alone would send
    /// them to settings; naming Bluetooth and offering the voice note sends them somewhere useful.
    /// </summary>
    [Fact]
    public void And_it_says_why_in_plain_words()
    {
        var reason = APhone(Ble, hasWifiDirect: false).CannotCallReason;

        Assert.NotNull(reason);
        Assert.Contains("Bluetooth", reason);
        Assert.Contains("voice note", reason);
    }

    // ── never refusing what would have worked ──────────────────────────────

    /// <summary>
    /// The case that must NOT be refused, and the reason this check is a capability question rather
    /// than a bandwidth question. At the moment of tapping Call, Bluetooth is very often the only link
    /// there is — Wi-Fi Direct is brought up by the call, over Bluetooth. Refusing here would refuse
    /// every first call between two phones.
    /// </summary>
    [Fact]
    public void A_narrow_link_today_does_not_refuse_a_phone_that_has_wifi_direct()
    {
        var call = APhone(Ble, hasWifiDirect: true);

        Assert.True(call.CanCall);
        Assert.Null(call.CannotCallReason);
    }

    [Fact]
    public void A_wide_link_can_always_call()
    {
        var call = APhone(WifiDirect, hasWifiDirect: true);

        Assert.True(call.CanCall);
    }

    /// <summary>
    /// A radio already on a wide link can call even with no broker at all — the link it has is the
    /// point, not the one it could arrange.
    /// </summary>
    [Fact]
    public void A_wide_link_needs_no_broker()
        => Assert.True(APhone(WifiDirect, hasWifiDirect: false).CanCall);

    /// <summary>
    /// A radio that will not say gets the benefit of the doubt, matching the codec. Every fake and
    /// every desktop radio reports zero, and refusing on silence would refuse all of them.
    /// </summary>
    [Fact]
    public void A_radio_that_reports_nothing_is_not_refused()
        => Assert.True(APhone(0, hasWifiDirect: false).CanCall);

    // ── the older reasons still come first ─────────────────────────────────

    /// <summary>
    /// Nobody connected is the more useful thing to say, and the more true one — there is no link to
    /// judge the width of. The narrow-radio message must not displace it.
    /// </summary>
    [Fact]
    public void Nothing_connected_is_still_the_first_thing_said()
    {
        var call = APhone(Ble, linked: false, hasWifiDirect: false);

        Assert.False(call.CanCall);
        Assert.Equal("no phone is connected to this one", call.CannotCallReason);
    }
}
