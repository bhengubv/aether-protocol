package aether.teal

import android.os.Bundle
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity

/**
 * Aether Teal — NearLink transport node.
 *
 * On standard Android, NearLink silicon is not present. Rather than leaving this
 * as a dead stub, the Aether Teal transport approximates NearLink's application
 * protocol (SSAP) over standard BLE GATT using the canonical Aether SLE UUIDs.
 *
 * What the approximation does:
 *   SSAP (SparkLink Service Access Protocol) is structurally identical to GATT —
 *   the same Services → Properties → Descriptors attribute model, the same 16-bit
 *   handles, the same notify/indicate semantics. We wrap BLE GATT in an SSAP-shaped
 *   facade using the Aether SLE service UUID (61657468-6572-0003-0000-000000000000)
 *   and data property UUID (61657468-6572-0003-0001-000000000000), mirroring exactly
 *   what the HarmonyOS harmonyos/teal/ app registers on real NearLink hardware.
 *
 * What it cannot do:
 *   NearLink's radio layer (BPSK/QPSK/8PSK modulation, Polar codes + HARQ, up to
 *   4 MHz channel width) is incompatible with BLE's GFSK radio. Nodes running this
 *   approximation cannot exchange bytes with real NearLink hardware — they interoperate
 *   with other Aether nodes running the same BLE-over-SSAP approximation.
 *
 * When the hardware is adopted:
 *   If Huawei publishes a NearLink SDK for standard Android (or if SparkLink Alliance
 *   standardisation puts SLE silicon into non-Huawei chips), the upgrade is a radio
 *   swap only:
 *     1. Replace BLE GATT calls with ssapc_* / ssaps_* SDK calls.
 *     2. Keep the same service and property UUIDs — no peer or application code changes.
 *     3. Set isAvailable via the SDK's hardware-present check.
 *   The ICircleLinkTransportService interface and TransportManager slot require no changes.
 *
 * Reference: HiSilicon WS63 SDK (gitee.com/HiSpark/fbb_ws63) — sle_ssap_client.h,
 * sle_ssap_server.h, sle_device_discovery.h, sle_connection_manager.h.
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
            text = "Aether Teal"
            textSize = 26f
            setPadding(0, 0, 0, 8)
        })
        root.addView(TextView(this).apply {
            text = "NearLink Transport"
            textSize = 16f
            setPadding(0, 0, 0, 32)
        })
        root.addView(TextView(this).apply {
            text = "Running over BLE (SSAP approximation)"
            textSize = 16f
            setPadding(0, 0, 0, 16)
        })
        root.addView(TextView(this).apply {
            text = """
                NearLink silicon is not present on this device.

                This node approximates NearLink's SSAP application protocol
                over standard BLE GATT using the Aether SLE service UUIDs.
                It participates in the Aether Teal mesh with full send/receive
                capability — at BLE range and bandwidth rather than NearLink's.

                Approximation:
                  • Protocol:   SSAP-over-BLE (API-analogous, not wire-compatible)
                  • Range:      ~100 m (BLE) vs 600 m (NearLink)
                  • Bandwidth:  ~1 Mbps (BLE) vs 12 Mbps (NearLink)
                  • Service:    61657468-6572-0003-0000-000000000000

                Upgrade path:
                  When Huawei publishes a NearLink SDK for Android, the BLE
                  radio is replaced with the SLE radio. UUIDs, routing, and
                  the application layer are unchanged.

                NearLink spec (when silicon is present):
                  • Range:      up to 600 m
                  • Bandwidth:  12 Mbps
                  • Latency:    20 µs
                  • Power:      60 % less than BLE 5.0
            """.trimIndent()
            textSize = 14f
        })

        return root
    }
}
