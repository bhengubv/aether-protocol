// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Fallback used on hosts without real radios (the Web head, desktop). The radios are physical —
/// they only light up on the phone.
/// </summary>
public sealed class NullRadioMesh : IRadioMesh
{
    public string LocalTag => "—";
    public IReadOnlyList<RadioInfo> Radios { get; } =
        new[] { new RadioInfo("Wi-Fi Direct", false), new RadioInfo("BLE", false) };
    public string SelectedRadio => "Wi-Fi Direct";
    public string LinkRadio => "Wi-Fi Direct";
    public long LinkBandwidthBps => 0;   // no radio here, so nothing to promise
    public bool IsSupported => false;
    public bool IsLinked => false;
    public string? PeerTag => null;

    public event Action? Changed { add { } remove { } }

    public IReadOnlyList<string> Log =>
        new[] { "Radios are physical — run the app on two Android phones to link them over the air." };

    public void IdentifyPeer(string aetherTag) { }
    public void SelectRadio(string name) { }
    public void Link() { }
    public Task SendTestAsync(string text) => Task.CompletedTask;
    public Task<bool> SendPacketAsync(byte[] packetBytes) => Task.FromResult(false);
    public event Action<byte[]>? PacketReceived { add { } remove { } }
    public void Stop() { }
}
