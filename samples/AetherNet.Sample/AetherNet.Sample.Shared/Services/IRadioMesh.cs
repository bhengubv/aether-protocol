// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>One physical radio the app can link over, and whether it's usable on this device.</summary>
public sealed record RadioInfo(string Name, bool Available);

/// <summary>
/// The app's over-the-air link across ALL of AetherNet's radios — Wi-Fi Direct, BLE, NFC,
/// NearLink, LoRa — each native inside this one APK. The UI picks a radio and links over it.
/// On non-phone hosts this is a no-op (radios are physical).
/// </summary>
public interface IRadioMesh
{
    /// <summary>This device's AetherTag — one identity shared across every radio.</summary>
    string LocalTag { get; }

    /// <summary>Every radio the app carries, with per-device availability.</summary>
    IReadOnlyList<RadioInfo> Radios { get; }

    /// <summary>The currently-selected radio's name.</summary>
    string SelectedRadio { get; }

    /// <summary>Whether the selected radio is usable on this device.</summary>
    bool IsSupported { get; }

    /// <summary>True once a peer has linked over the selected radio.</summary>
    bool IsLinked { get; }

    /// <summary>The linked peer's AetherTag, or null.</summary>
    string? PeerTag { get; }

    /// <summary>Raised whenever the log or link state changes; the UI re-renders on it.</summary>
    event Action? Changed;

    /// <summary>A point-in-time snapshot of the radio log, oldest first.</summary>
    IReadOnlyList<string> Log { get; }

    /// <summary>Choose which radio to link over.</summary>
    void SelectRadio(string name);

    /// <summary>Bring the selected radio up and link to the other phone.</summary>
    void Link();

    /// <summary>Send one real MeshPacket carrying <paramref name="text"/> to the linked peer.</summary>
    Task SendTestAsync(string text);

    /// <summary>Tear the radios down.</summary>
    void Stop();
}
