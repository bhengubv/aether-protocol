// SPDX-License-Identifier: MIT

using AetherNet.Browser;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// This app's radios, as the four facts and one verb a browser needs.
///
/// <para>
/// The second of the two seams. <see cref="IRadioMesh"/> knows about channels, bandwidth, lanes,
/// wire addresses and which physical radio is selected; <see cref="IMeshLink"/> knows none of that,
/// and a browser that did could not be lifted onto a device whose radios work differently.
/// </para>
/// </summary>
public sealed class RadioMeshLink : IMeshLink
{
    private readonly IRadioMesh _radio;

    public RadioMeshLink(IRadioMesh radio)
    {
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _radio.Changed += () => Changed?.Invoke();
        _radio.PacketReceived += bytes => PacketReceived?.Invoke(bytes);
    }

    public bool IsSupported => _radio.IsSupported;

    public bool IsLinked => _radio.IsLinked;

    /// <summary>What the link is called in words — "Wi-Fi Direct", "Bluetooth" — because a reader sees it.</summary>
    public string Name => _radio.SelectedRadio;

    public void Link() => _radio.Link();

    public event Action? Changed;

    public event Action<byte[]>? PacketReceived;

    public Task<bool> SendAsync(byte[] packetBytes) => _radio.SendPacketAsync(packetBytes);
}
