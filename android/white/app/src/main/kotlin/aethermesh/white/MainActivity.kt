package aethermesh.white

import android.nfc.NfcAdapter
import android.os.Bundle
import android.widget.ScrollView
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity

/**
 * Aether White — NFC HCE test node.
 *
 * The HCE service ([AetherMeshHceService]) handles all NFC interactions automatically.
 * This activity just shows status and packet summaries.
 *
 * AID: F061657468657200  (0xF0 prefix + "aether" ASCII + NUL)
 * When another NFC device selects this AID, the service echoes Aether packets back.
 */
class MainActivity : AppCompatActivity(), AetherMeshHceService.Listener {

    private lateinit var tvLog: TextView
    private lateinit var scrollLog: ScrollView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())

        AetherMeshHceService.listener = this

        val nfcAvailable = NfcAdapter.getDefaultAdapter(this) != null
        log("Aether White ready.")
        log("AID: F061657468657200")
        if (nfcAvailable) {
            log("NFC hardware detected.")
            log("Hold an NFC device or reader close to receive packets.")
        } else {
            log("WARNING: NFC hardware not found or disabled.")
        }
    }

    override fun onDestroy() {
        AetherMeshHceService.listener = null
        super.onDestroy()
    }

    // ── Listener callbacks ────────────────────────────────────────────────────

    override fun onStatusChanged(status: String)   = runOnUiThread { log("[Status] $status") }
    override fun onPacketReceived(summary: String)  = runOnUiThread { log("[Packet] $summary") }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private fun log(msg: String) {
        tvLog.append("$msg\n")
        scrollLog.post { scrollLog.fullScroll(ScrollView.FOCUS_DOWN) }
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    private fun buildLayout(): android.view.View {
        val root = android.widget.LinearLayout(this).apply {
            orientation = android.widget.LinearLayout.VERTICAL
            setPadding(32, 64, 32, 32)
        }

        root.addView(TextView(this).apply {
            text = "Aether White"
            textSize = 22f
            setPadding(0, 0, 0, 4)
        })
        root.addView(TextView(this).apply {
            text = "NFC HCE node  •  AID: F061657468657200"
            textSize = 13f
            setPadding(0, 0, 0, 16)
        })

        tvLog = TextView(this).apply { textSize = 12f }
        scrollLog = ScrollView(this).apply {
            layoutParams = android.widget.LinearLayout.LayoutParams(
                android.widget.LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f)
            addView(tvLog)
        }
        root.addView(scrollLog)
        return root
    }
}
