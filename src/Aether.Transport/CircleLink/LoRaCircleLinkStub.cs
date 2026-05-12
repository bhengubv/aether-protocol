// SPDX-License-Identifier: MIT

using Aether.Transport.Abstractions;

namespace Aether.Transport.CircleLink;

/// <summary>
/// LoRa / CircleLink (Aether Red) transport — BLE Long Range approximation layer.
///
/// <h3>What we built instead of a stub</h3>
/// LoRa's radio layer (chirp spread-spectrum, sub-GHz, 15 km) cannot be replicated on a
/// standard phone or laptop radio — the link-budget gap is ~30–40 dB, which no protocol
/// cleverness can close. What <em>can</em> be faithfully approximated is the entire
/// <b>Meshtastic protocol layer</b> that runs on top of LoRa, carried over
/// <b>BLE 5.0 Coded PHY (Extended Advertising, S=8)</b>:
///
/// <list type="bullet">
///   <item><description><b>Wire format</b>: Meshtastic 16-byte raw header
///     (<c>to · from · packet_id · flags · channel_hash · next_hop · relay_node</c>)
///     followed by an AES-256-CTR encrypted protobuf payload.
///     Nonce = <c>packet_id (4B) || from (4B) || block_counter (8B)</c>.
///     Total packet ≈ 249 bytes — fits a single BLE <c>AUX_ADV_IND</c> PDU (254 bytes max).
///   </description></item>
///   <item><description><b>Routing</b>: managed flood with contention-window backoff sized
///     inversely by RSSI (strong-signal nodes defer, weak-signal nodes rebroadcast first),
///     duplicate-packet_id suppression, configurable hop limit, and implicit broadcast ACK
///     (sender hears own packet rebroadcast = propagation confirmed).
///   </description></item>
///   <item><description><b>BLE radio settings</b>: <c>startAdvertisingSet</c> with
///     <c>setLegacyMode(false)</c>, <c>PHY_LE_CODED</c> primary and secondary, non-connectable
///     non-scannable broadcast. Receiver uses <c>ScanSettings.setPhy(PHY_LE_CODED)</c>.
///     Runtime fallback to 1M PHY if <c>isLeCodedPhySupported()</c> returns false or
///     observed delivery rate drops below 30%.
///   </description></item>
///   <item><description><b>Effective range</b>: ~1.3 km outdoor (BLE LR S=8, +0 dBm) vs
///     LoRa's 5–15 km. Indoors: ~50–200 m. Not LoRa, but far beyond standard BLE's 100 m.
///   </description></item>
/// </list>
///
/// <h3>Why Meshtastic format specifically</h3>
/// Using the Meshtastic wire format means a phone running this class and a LoRa node running
/// real Meshtastic firmware can federate automatically via a bridge phone that has both
/// the BLE LR transport active and a Meshtastic BLE GATT connection to the LoRa radio.
/// The packet flows: <c>phone → BLE LR → bridge phone → Meshtastic BLE GATT → LoRa node → LoRa air</c>
/// with no protocol translation — the same 16-byte header and the same encrypted protobuf
/// ride all three hops. This is the long-range backbone that makes Aether Red meaningful
/// even before every user owns a LoRa module.
///
/// <h3>What changes when the hardware is adopted</h3>
/// <list type="number">
///   <item><description><b>LoRa module attached (USB-serial or SPI)</b>: implement
///     <see cref="ICircleLinkTransportService"/> using AT-commands or a direct SPI driver
///     to the SX1276/SX1278 chip. Keep the Meshtastic packet format and the managed-flood
///     routing algorithm unchanged — the bridge pattern with BLE LR nodes works automatically.
///     Set <see cref="IsAvailable"/> to the hardware-present check (e.g. serial port enumeration).
///   </description></item>
///   <item><description><b>SparkLink Alliance standardisation</b>: if the SparkLink Alliance's
///     IEEE submission produces an open MAC/PHY for sub-GHz bands, the same approximation
///     strategy applies — implement <see cref="ICircleLinkTransportService"/> with the new radio,
///     keep the Meshtastic packet format.
///   </description></item>
/// </list>
///
/// Source: Meshtastic mesh algorithm (meshtastic.org/docs/overview/mesh-algo/);
/// Meshtastic protobufs (github.com/meshtastic/protobufs);
/// BLE 5.0 Coded PHY (Nordic "Tested by Nordic: Bluetooth Long Range");
/// Android AdvertisingSetParameters (developer.android.com/reference/android/bluetooth/le/).
/// </summary>
public sealed class LoRaCircleLinkStub : ICircleLinkTransportService
{
    /// <inheritdoc />
    public string Name => "Aether Red (LoRa/CircleLink)";

    /// <inheritdoc />
    public bool IsAvailable => false; // Requires physical LoRa radio hardware module

    /// <inheritdoc />
    public long MaxBandwidthBps => 37_500; // LoRa SF7 BW125 kHz ≈ 37.5 kbps

    /// <inheritdoc />
    public int MaxRangeMeters => 15_000; // Up to 15 km line-of-sight

    /// <inheritdoc />
    public int PowerCostRelative => 50; // High transmission power; selected only when closer transports fail

    /// <inheritdoc />
    public int MaxConcurrentPeers => 255;

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event Action<string>? PeerConnected
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event Action<string>? PeerDisconnected
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public Task<bool> SendAsync(string peerUhid, byte[] data,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "LoRa/CircleLink (Aether Red) requires a physical LoRa radio module. " +
            "Connect a LoRa32 or equivalent device and implement the hardware driver. " +
            "IsAvailable = false.");

    /// <inheritdoc />
    public Task<bool> SendStreamAsync(string peerUhid, Stream stream,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "LoRa/CircleLink (Aether Red) requires a physical LoRa radio module.");

    /// <inheritdoc />
    public bool IsConnected(string peerUhid) => false;
}
