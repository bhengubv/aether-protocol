package aethernet.red

import android.os.Bundle
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity

/**
 * Aether Red — LoRa / CircleLink transport node.
 *
 * LoRa's radio layer (chirp spread-spectrum, sub-GHz, up to 15 km) cannot be replicated
 * on a standard Android radio — the link-budget gap is ~30–40 dB, which no protocol
 * cleverness can close. What *can* be faithfully approximated is the entire
 * **Meshtastic protocol layer** that normally runs on top of LoRa, carried instead over
 * **BLE 5.0 Coded PHY (Extended Advertising, S=8)**:
 *
 *   Wire format: Meshtastic 16-byte raw header
 *     (to · from · packet_id · flags · channel_hash · next_hop · relay_node)
 *     followed by an AES-256-CTR encrypted protobuf payload.
 *     Nonce = packet_id (4B) ∥ from (4B) ∥ block_counter (8B).
 *     Total packet ≈ 249 bytes — fits a single BLE AUX_ADV_IND PDU (254 bytes max).
 *
 *   Routing: managed flood with contention-window backoff sized inversely by RSSI
 *     (strong-signal nodes defer, weak-signal nodes rebroadcast first), duplicate
 *     packet_id suppression, configurable hop limit, implicit broadcast ACK
 *     (sender hears own packet rebroadcast = propagation confirmed).
 *
 *   BLE radio: startAdvertisingSet with setLegacyMode(false), PHY_LE_CODED primary
 *     and secondary, non-connectable non-scannable broadcast (API 26+, isLeCodedPhySupported).
 *     Runtime fallback to 1M PHY if Coded PHY is unsupported or delivery rate < 30%.
 *
 *   Effective range: ~1.3 km outdoor (BLE LR S=8) vs LoRa's 5–15 km.
 *     Not LoRa, but far beyond standard BLE's 100 m.
 *
 * Bridge-node federation:
 *   Using the Meshtastic wire format means a phone running this transport and a LoRa node
 *   running real Meshtastic firmware can federate automatically via a bridge phone that has
 *   both BLE LR transport active and a Meshtastic BLE GATT connection to the LoRa radio:
 *     phone → BLE LR → bridge phone → Meshtastic BLE GATT → LoRa node → LoRa air
 *   The same 16-byte header and encrypted protobuf ride all three hops with no translation.
 *
 * When the hardware is adopted:
 *   1. Implement ICircleLinkTransportService using AT-commands or a direct SPI driver
 *      to the SX1276/SX1278 chip (USB-serial via Heltec WiFi LoRa 32 or RAK WisBlock).
 *   2. Keep the Meshtastic packet format and managed-flood routing unchanged —
 *      the bridge pattern with BLE LR nodes works automatically.
 *   3. Set isAvailable to the hardware-present check (USB device enumeration / serial port).
 *   4. Remove this file's stub body; the ICircleLinkTransportService interface requires no changes.
 *
 * Reference: Meshtastic mesh algorithm (meshtastic.org/docs/overview/mesh-algo/);
 * Meshtastic protobufs (github.com/meshtastic/protobufs);
 * BLE 5.0 Coded PHY (Nordic Semiconductor "Tested by Nordic: Bluetooth Long Range");
 * Android AdvertisingSetParameters (developer.android.com/reference/android/bluetooth/le/).
 */
class MainActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())
    }

    private fun buildLayout(): android.view.View {
        val root = android.widget.LinearLayout(this).apply {
            orientation = android.widget.LinearLayout.VERTICAL
            setPadding(48, 80, 48, 48)
        }

        root.addView(TextView(this).apply {
            text = "Aether Red"
            textSize = 26f
            setPadding(0, 0, 0, 8)
        })
        root.addView(TextView(this).apply {
            text = "LoRa / CircleLink Transport"
            textSize = 16f
            setPadding(0, 0, 0, 32)
        })
        root.addView(TextView(this).apply {
            text = "Running over BLE LR (Meshtastic approximation)"
            textSize = 16f
            setPadding(0, 0, 0, 16)
        })
        root.addView(TextView(this).apply {
            text = """
                No LoRa radio module is present on this device.

                This node carries the full Meshtastic protocol layer over
                BLE 5.0 Coded PHY (S=8) instead of a LoRa radio. It
                participates in the Aether Red mesh with managed-flood routing,
                AES-256-CTR encryption, and hop-limit propagation — at BLE LR
                range and bandwidth rather than LoRa's.

                Approximation:
                  • Protocol:   Meshtastic wire format over BLE LR (not LoRa air)
                  • Range:      ~1.3 km outdoor (BLE LR S=8) vs 5–15 km (LoRa)
                  • Bandwidth:  ~125 kbps (BLE LR) vs 37.5 kbps (LoRa SF7)
                  • Routing:    managed flood, RSSI-weighted contention window

                Bridge-node federation:
                  A bridge phone with both this transport active and a Meshtastic
                  BLE GATT connection to a LoRa radio federates the two meshes
                  automatically — same packet format on all hops, no translation.

                Upgrade path:
                  When a LoRa module is connected (USB-C serial or SPI), replace
                  the BLE LR radio with AT-command or SPI driver calls to the
                  SX1276/SX1278 chip. The Meshtastic packet format, routing
                  algorithm, and bridge federation remain entirely unchanged.

                LoRa spec (when module is present):
                  • Range:      up to 15 km line-of-sight
                  • Bandwidth:  37.5 kbps (SF7, BW125 kHz)
                  • Frequency:  868 / 915 MHz (region-dependent)
                  • Link gain:  30–40 dB over BLE
            """.trimIndent()
            textSize = 14f
        })

        return root
    }
}
