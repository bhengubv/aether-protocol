// SPDX-License-Identifier: MIT

namespace AetherNet.Transport;

/// <summary>
/// Fixed GATT UUIDs for the Aether White (NFC) transport's BLE-proximity approximation.
///
/// <para>
/// Windows removed the only NFC P2P API (<c>Windows.Networking.Proximity</c>) in Windows 11,
/// so the Aether White transport reproduces NFC's "tap to connect" physical-security model
/// over standard BLE GATT, gated on a very strong signal (<see cref="ProximityThresholdDbm"/>).
/// </para>
///
/// <para>
/// The UUID base reuses the Aether NFC Application ID <c>F0 61 65 74 68 65 72 00</c>
/// (<c>f0616574-6865-7200</c> = "ðaether\0") so the same identity threads through both the
/// real PC/SC NFC reader path (SELECT AID) and this BLE approximation. The trailing 16 bits
/// select the attribute: <c>0001</c> service, <c>0002</c> write, <c>0003</c> notify.
/// </para>
///
/// These values are canonical and must match the Android <c>android/white/</c> node and any
/// other Aether White implementation byte-for-byte.
/// </summary>
public static class NfcGattConstants
{
    /// <summary>
    /// Primary Aether White (NFC-over-BLE) GATT service UUID. Built from the Aether NFC AID
    /// <c>F061657468657200</c> so scanners and the PC/SC path share one identity.
    /// </summary>
    public static readonly Guid ServiceUuid = new("f0616574-6865-7200-0000-000000000001");

    /// <summary>
    /// Write characteristic — the central writes fragmented NDEF message bytes here
    /// (write-without-response for low latency). Counterpart to NFC's SNEP PUT.
    /// </summary>
    public static readonly Guid WriteCharacteristic = new("f0616574-6865-7200-0000-000000000002");

    /// <summary>
    /// Notify characteristic — the peripheral pushes inbound NDEF message bytes to the central.
    /// Notify-only. Counterpart to NFC's SNEP GET response.
    /// </summary>
    public static readonly Guid NotifyCharacteristic = new("f0616574-6865-7200-0000-000000000003");

    /// <summary>
    /// Standard Bluetooth SIG Client Characteristic Configuration Descriptor (CCCD).
    /// Written with 0x0001 to enable notifications on <see cref="NotifyCharacteristic"/>.
    /// </summary>
    public static readonly Guid CccdUuid = new("00002902-0000-1000-8000-00805f9b34fb");

    /// <summary>
    /// RSSI in-range threshold, in dBm, above which a peer is considered "tapped".
    /// −40 dBm corresponds to roughly 5–10 cm of physical separation, reproducing NFC's
    /// proximity-as-security model without NFC hardware. A peer advertising below this
    /// strength is ignored so a connection is only ever made when the devices are touching.
    /// </summary>
    public const short ProximityThresholdDbm = -40;

    /// <summary>Human-readable Aether White local name included in BLE advertisements.</summary>
    public const string LocalName = "Aether-White";
}
