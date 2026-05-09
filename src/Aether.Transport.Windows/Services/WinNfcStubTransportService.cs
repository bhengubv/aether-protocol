// SPDX-License-Identifier: MIT

using Aether.Transport.Abstractions;

namespace Aether.Transport.Windows.Services;

/// <summary>
/// Windows stub for the NFC (Aether White) transport.
///
/// <c>Windows.Networking.Proximity</c> — the only NFC API family that existed on Windows —
/// was removed from Windows 11. There is no supported path for NFC proximity on modern Windows.
///
/// <b>PLATFORM BLOCKED:</b> <see cref="IsAvailable"/> is permanently <see langword="false"/>
/// on Windows. The active NFC node is the Android <c>Aether White</c> app (HCE via
/// <c>HostApduService</c>, AID <c>F061657468657200</c>).
/// </summary>
public sealed class WinNfcStubTransportService : ITransportService
{
    /// <inheritdoc />
    public string Name => "Aether White (NFC)";

    /// <inheritdoc />
    public bool IsAvailable => false; // Windows.Networking.Proximity removed in Windows 11

    /// <inheritdoc />
    public long MaxBandwidthBps => 848_000; // NFC 848 kbps max (ISO 14443)

    /// <inheritdoc />
    public int MaxRangeMeters => 0; // ~5 cm — effectively 0 in metres

    /// <inheritdoc />
    public int PowerCostRelative => 3;

    /// <inheritdoc />
    public int MaxConcurrentPeers => 1; // NFC is point-to-point

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public Task<bool> SendAsync(string peerUhid, byte[] data,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Windows.Networking.Proximity NFC APIs were removed in Windows 11. " +
            "Use the Android 'Aether White' app (android/white/) as the NFC node. " +
            "AID: F061657468657200");

    /// <inheritdoc />
    public Task<bool> SendStreamAsync(string peerUhid, Stream stream,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Windows.Networking.Proximity NFC APIs were removed in Windows 11.");

    /// <inheritdoc />
    public bool IsConnected(string peerUhid) => false;
}
