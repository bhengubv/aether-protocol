package aether.teal

import android.os.Bundle
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity

/**
 * Aether Teal — NearLink stub node.
 *
 * NearLink (Huawei) is hardware-blocked on all current non-HarmonyOS devices.
 * IsAvailable = false.
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
            text = "⚠  Hardware blocked"
            textSize = 18f
            setPadding(0, 0, 0, 16)
        })
        root.addView(TextView(this).apply {
            text = """
                NearLink requires the Huawei NearLink SDK.

                The SDK is available only on HarmonyOS / OpenHarmony devices
                with NearLink silicon.  This device is not supported.

                IsAvailable = false

                When the SDK ships for standard Android, this stub will be
                replaced with a real implementation.

                Spec (when available):
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
