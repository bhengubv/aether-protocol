// SPDX-License-Identifier: MIT

namespace AetherNet.Transport;

/// <summary>
/// Fixed GATT UUIDs for the Aether Teal (NearLink) transport's SSAP-over-BLE approximation.
///
/// <para>
/// NearLink's application protocol — SSAP (SparkLink Service Access Protocol) — is structurally
/// identical to Bluetooth GATT: the same Services → Properties → Descriptors attribute model,
/// the same notify/indicate semantics, the same UUID format. Where NearLink silicon is absent
/// (every non-HarmonyOS device), the Aether Teal transport implements SSAP as a thin façade over
/// standard BLE GATT using the canonical Aether SLE UUIDs below.
/// </para>
///
/// <para>
/// The UUID base encodes "aether" (<c>61 65 74 68 65 72</c>) like <see cref="BleGattConstants"/>,
/// with type field <c>0003</c> designating the SLE (NearLink) profile. The fourth group selects
/// the SSAP property: <c>0000</c> service, <c>0001</c> data/write, <c>0002</c> notify. The service
/// is discriminated from the Aether Blue (BLE) profile by the advertised <em>service</em> UUID —
/// Blue advertises <c>…-0001-…</c>, Teal advertises <c>…-0003-…</c> — so a scanner filtering on
/// one never triggers on the other.
/// </para>
///
/// These values match the HarmonyOS <c>harmonyos/teal/</c> node (which registers the same UUIDs
/// against the real <c>@kit.NearLinkKit</c> SDK) and the Android <c>android/teal/</c> node, so an
/// upgrade to real NearLink silicon is a radio swap only — no peer or application code changes.
/// </summary>
public static class SleGattConstants
{
    /// <summary>Primary Aether SLE (NearLink-over-BLE) service UUID.</summary>
    public static readonly Guid ServiceUuid = new("61657468-6572-0003-0000-000000000000");

    /// <summary>
    /// SSAP data property — the central writes fragmented Aether wire-format packets here
    /// (write-without-response for low latency), mirroring an SSAP write request.
    /// </summary>
    public static readonly Guid DataCharacteristic = new("61657468-6572-0003-0001-000000000000");

    /// <summary>
    /// SSAP notify property — the peripheral pushes inbound packets to the central,
    /// mirroring an SSAP notification.
    /// </summary>
    public static readonly Guid NotifyCharacteristic = new("61657468-6572-0003-0002-000000000000");

    /// <summary>
    /// Standard Bluetooth SIG Client Characteristic Configuration Descriptor (CCCD).
    /// Written with 0x0001 to enable notifications on <see cref="NotifyCharacteristic"/>.
    /// </summary>
    public static readonly Guid CccdUuid = new("00002902-0000-1000-8000-00805f9b34fb");

    /// <summary>Human-readable Aether Teal local name included in BLE advertisements.</summary>
    public const string LocalName = "Aether-Teal";
}
