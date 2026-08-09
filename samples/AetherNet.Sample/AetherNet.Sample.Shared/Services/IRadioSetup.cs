// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>How a radio actually stands on this device, after checking rather than assuming.</summary>
public enum RadioState
{
    /// <summary>Not checked yet.</summary>
    Unknown,

    /// <summary>The hardware isn't here. Nothing the user can do — say so and move on.</summary>
    Unsupported,

    /// <summary>Present, but the app hasn't been given permission yet.</summary>
    NeedsPermission,

    /// <summary>Permission granted, but something on the device is switched off (Bluetooth, Location…).</summary>
    NeedsSystemToggle,

    /// <summary>Present and usable, but only partly — e.g. this phone can find others but can't be found.</summary>
    Partial,

    /// <summary>Fully working.</summary>
    Ready,
}

/// <summary>One radio's setup status, in language a person can act on.</summary>
/// <param name="Name">Radio name as the picker shows it (Wi-Fi Direct, BLE, NFC…).</param>
/// <param name="State">Where it stands right now.</param>
/// <param name="Detail">One honest line — what's wrong, or what it can do.</param>
/// <param name="ActionLabel">What the button should say, or null when there's nothing to press.</param>
/// <param name="Required">Whether Connection is unusable without it.</param>
public sealed record RadioStatus(
    string Name,
    RadioState State,
    string Detail,
    string? ActionLabel,
    bool Required)
{
    public bool IsBlocking => Required && State is RadioState.NeedsPermission or RadioState.NeedsSystemToggle;
}

/// <summary>
/// Gets this device's radios genuinely ready, rather than trusting that a granted permission means a
/// working radio. Every check <b>verifies the capability by exercising it</b> — the Android 12 trap is
/// that <c>BLUETOOTH_ADVERTISE</c> can be missing and the BLE peripheral role then fails silently, so
/// the app looks linked-and-idle while doing nothing at all.
/// </summary>
public interface IRadioSetup
{
    /// <summary>True on a host with real radios (a phone).</summary>
    bool IsPhone { get; }

    /// <summary>Current status of every radio, freshly checked.</summary>
    Task<IReadOnlyList<RadioStatus>> CheckAsync();

    /// <summary>
    /// Ask for whatever <paramref name="radioName"/> still needs (permissions, or opening the right
    /// system settings screen), then re-check and return the new status.
    /// </summary>
    Task<RadioStatus> RequestAsync(string radioName);
}

/// <summary>
/// Fallback for hosts with no radios — the Web head and desktop. Honest rather than empty: it says
/// the radios are physical so the wizard can explain instead of showing a dead screen.
/// </summary>
public sealed class NullRadioSetup : IRadioSetup
{
    public bool IsPhone => false;

    public Task<IReadOnlyList<RadioStatus>> CheckAsync() =>
        Task.FromResult<IReadOnlyList<RadioStatus>>(new[]
        {
            new RadioStatus("Wi-Fi Direct", RadioState.Unsupported, "Radios only exist on a phone.", null, Required: false),
            new RadioStatus("BLE", RadioState.Unsupported, "Radios only exist on a phone.", null, Required: false),
        });

    public Task<RadioStatus> RequestAsync(string radioName) =>
        Task.FromResult(new RadioStatus(radioName, RadioState.Unsupported, "Radios only exist on a phone.", null, Required: false));
}
