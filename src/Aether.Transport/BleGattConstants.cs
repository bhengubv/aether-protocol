// SPDX-License-Identifier: MIT

namespace Aether.Transport;

/// <summary>
/// Fixed GATT UUIDs for the Aether mesh BLE transport.
///
/// The UUID base encodes "aether" (ASCII 0x61 0x65 0x74 0x68 0x65 0x72)
/// in the first six octets, making the service immediately identifiable
/// in any BLE scanner without a lookup table.
///
///   Service  : 61657468-6572-0001-0000-000000000000  (aether, type=service)
///   TX char  : 61657468-6572-0002-0000-000000000000  (central → peripheral write)
///   RX char  : 61657468-6572-0003-0000-000000000000  (peripheral → central notify)
/// </summary>
public static class BleGattConstants
{
    /// <summary>Primary Aether mesh GATT service UUID.</summary>
    public static readonly Guid ServiceUuid = new("61657468-6572-0001-0000-000000000000");

    /// <summary>
    /// TX characteristic UUID — the central (Windows/desktop) writes
    /// Aether wire-format packets here.  Write-without-response for low latency.
    /// </summary>
    public static readonly Guid TxCharacteristic = new("61657468-6572-0002-0000-000000000000");

    /// <summary>
    /// RX characteristic UUID — the peripheral (phone) notifies the central
    /// with response packets.  Notify-only (no read).
    /// </summary>
    public static readonly Guid RxCharacteristic = new("61657468-6572-0003-0000-000000000000");

    /// <summary>
    /// Standard Bluetooth SIG Client Characteristic Configuration Descriptor (CCCD).
    /// Must be written with 0x0001 to enable notifications on <see cref="RxCharacteristic"/>.
    /// </summary>
    public static readonly Guid CccdUuid = new("00002902-0000-1000-8000-00805f9b34fb");

    /// <summary>
    /// Human-readable Aether local name included in BLE advertisements.
    /// Scanners will see "Aether" before resolving the service UUID.
    /// </summary>
    public const string LocalName = "Aether";
}
