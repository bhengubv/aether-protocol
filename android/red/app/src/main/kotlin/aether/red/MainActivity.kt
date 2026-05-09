package aether.red

import android.os.Bundle
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity

/**
 * Aether Red — LoRa / CircleLink stub node.
 *
 * LoRa requires a physical radio module (Heltec LoRa32, RAK WisBlock, SX1276 breakout).
 * IsAvailable = false until hardware is connected.
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
            text = "⚠  Hardware blocked"
            textSize = 18f
            setPadding(0, 0, 0, 16)
        })
        root.addView(TextView(this).apply {
            text = """
                LoRa / CircleLink requires a physical radio module.

                No LoRa hardware was detected on this device.

                IsAvailable = false

                To activate this transport:
                  1. Connect a LoRa module (Heltec WiFi LoRa 32,
                     RAK WisBlock, or Semtech SX1276) via USB-C serial or SPI.
                  2. Implement a driver extending ICircleLinkTransportService.
                  3. Set IsAvailable = true once the module responds.

                Spec (when hardware present):
                  • Range:      up to 15 km line-of-sight
                  • Bandwidth:  37.5 kbps (SF7, BW125 kHz)
                  • Frequency:  868 / 915 MHz (region-dependent)
            """.trimIndent()
            textSize = 14f
        })

        return root
    }
}
