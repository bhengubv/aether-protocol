// SPDX-License-Identifier: MIT
namespace Aether.Market.Models;

/// <summary>Transport used for co-presence Proof-of-Vicinity exchange.</summary>
public enum PoVTransportType : byte
{
    /// <summary>Bluetooth Low Energy (short range — prevents remote forgery).</summary>
    Ble = 0,
    /// <summary>Near-Field Communication (requires physical proximity).</summary>
    Nfc = 1,
    /// <summary>Huawei NearLink (short range, similar to BLE).</summary>
    NearLink = 2,
}
