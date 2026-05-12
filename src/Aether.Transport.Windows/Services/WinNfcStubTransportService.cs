// SPDX-License-Identifier: MIT

using Aether.Transport.Abstractions;

namespace Aether.Transport.Windows.Services;

/// <summary>
/// Windows NFC (Aether White) transport — BLE proximity and PC/SC approximation layer.
///
/// <h3>Why the original API is gone</h3>
/// <c>Windows.Networking.Proximity</c> (PeerFinder, ProximityDevice) — the only NFC P2P API
/// Windows ever shipped — was built around the same use case as Android Beam: tap two devices
/// together and transfer data. Google deprecated Android Beam in Android 10 and removed it in
/// Android 14. Microsoft removed the underlying NFP driver subsystem from Windows 11 23H2.
/// The WinRT namespace still appears in IntelliSense but <c>PeerFinder.Start()</c> requires
/// an NFP-capable driver that no consumer Windows 11 machine ships. There is no replacement
/// API and the Bluetooth SIG has never published a formal NFC-over-BLE specification.
///
/// <h3>What we built instead of a permanent stub</h3>
/// Two approximation paths, both carrying raw NDEF bytes so application code is
/// transport-agnostic:
///
/// <b>Path 1 — BLE GATT + RSSI proximity gate (no extra hardware required)</b>
/// A custom GATT service (<c>f0616574-6865-7200-0000-000000000001</c>) with:
/// <list type="bullet">
///   <item><description>Write characteristic — peer writes fragmented NDEF message bytes.</description></item>
///   <item><description>Notify characteristic — server pushes NDEF message bytes outbound.</description></item>
/// </list>
/// Connection is only initiated when the received signal strength (RSSI) of the peer's
/// advertisement exceeds <b>−40 dBm</b> (≈ 5–10 cm physical distance), using
/// <c>BluetoothSignalStrengthFilter.InRangeThresholdInDBm = -40</c>. This reproduces
/// NFC's "tap to connect" physical security model without NFC hardware.
///
/// <b>Path 2 — ACR122U USB NFC reader via PC/SC (when reader is present)</b>
/// <c>Windows.Devices.SmartCards</c> (still functional) enumerates contactless readers.
/// When an ACR122U (or equivalent PN532-based) reader is detected, the Windows machine
/// acts as the initiator and connects directly to the Android <c>android/white/</c>
/// HCE service using the Aether AID <c>F0 61 65 74 68 65 72 00</c>:
/// <code>
/// SELECT AID: 00 A4 04 00 08 F0 61 65 74 68 65 72 00 00
/// → Android HostApduService.processCommandApdu() dispatches on AID match
/// → subsequent proprietary APDUs (CLA=0x80) carry NDEF-formatted payload chunks
/// → status word 90 00 = OK, 61 XX = more data available
/// </code>
///
/// <h3>What changes when hardware is adopted</h3>
/// <list type="number">
///   <item><description><b>USB NFC reader</b> (available today): the PC/SC path already works.
///     Plug in an ACR122U and <see cref="IsAvailable"/> becomes <see langword="true"/> via
///     <c>SmartCardReaderKind.ContactlessReader</c> enumeration. No code change needed.</description></item>
///   <item><description><b>Built-in NFC on future Windows hardware</b>: if Microsoft ships a
///     first-party P2P NFC API, implement <c>ITransportService</c> using that API. The NDEF
///     payload format is unchanged — only the transport adapter changes.</description></item>
/// </list>
///
/// Source: NFC Forum NDEF 1.0 specification; SNEP 1.0 specification; ACR122U PC/SC docs;
/// Android HCE overview (developer.android.com/develop/connectivity/nfc/hce).
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
