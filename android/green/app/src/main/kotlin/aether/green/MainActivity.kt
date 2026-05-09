package aether.green

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Button
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat

/**
 * Aether Green — Wi-Fi Direct test node.
 *
 * Tap DISCOVER to start Wi-Fi Direct peer discovery.
 * The framework negotiates Group Owner vs. Client roles automatically.
 * The log shows connection events and received packet summaries.
 * Received packets are echoed back with TTL decremented.
 */
class MainActivity : AppCompatActivity(), AetherWifiDirectService.Listener {

    private lateinit var btnDiscover: Button
    private lateinit var btnStop: Button
    private lateinit var tvLog: TextView
    private lateinit var scrollLog: ScrollView
    private lateinit var wfdService: AetherWifiDirectService

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())

        wfdService = AetherWifiDirectService(applicationContext)
        wfdService.addListener(this)

        btnDiscover.setOnClickListener { requestPermissionsThenDiscover() }
        btnStop.setOnClickListener {
            wfdService.stop()
            btnDiscover.isEnabled = true
            btnStop.isEnabled = false
        }

        log("Aether Green ready.")
        log("Tap DISCOVER to find Wi-Fi Direct peers.")
    }

    override fun onResume() {
        super.onResume()
        @Suppress("DEPRECATION")
        registerReceiver(wfdService.receiver, wfdService.intentFilter)
        wfdService.initialize()
    }

    override fun onPause() {
        super.onPause()
        unregisterReceiver(wfdService.receiver)
    }

    override fun onDestroy() {
        wfdService.stop()
        wfdService.removeListener(this)
        super.onDestroy()
    }

    // ── Listener callbacks ────────────────────────────────────────────────────

    override fun onStatusChanged(status: String)   = runOnUiThread { log("[Status] $status") }
    override fun onPacketReceived(summary: String)  = runOnUiThread { log("[Packet] $summary") }

    // ── Permissions ───────────────────────────────────────────────────────────

    private val permLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { results ->
        if (results.values.all { it }) startDiscovery()
        else log("ERROR: Location/nearby permission denied (required for Wi-Fi Direct).")
    }

    private fun requestPermissionsThenDiscover() {
        val needed = wfdPermissions().filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (needed.isEmpty()) startDiscovery() else permLauncher.launch(needed.toTypedArray())
    }

    private fun wfdPermissions(): List<String> = if (Build.VERSION.SDK_INT >= 33) {
        listOf(Manifest.permission.NEARBY_WIFI_DEVICES)
    } else {
        listOf(Manifest.permission.ACCESS_FINE_LOCATION)
    }

    // ── Start / stop ──────────────────────────────────────────────────────────

    private fun startDiscovery() {
        wfdService.discoverPeers()
        btnDiscover.isEnabled = false
        btnStop.isEnabled = true
    }

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
            text = "Aether Green"
            textSize = 22f
            setPadding(0, 0, 0, 4)
        })
        root.addView(TextView(this).apply {
            text = "Wi-Fi Direct node"
            textSize = 14f
            setPadding(0, 0, 0, 16)
        })

        val row = android.widget.LinearLayout(this).apply {
            orientation = android.widget.LinearLayout.HORIZONTAL
        }
        btnDiscover = Button(this).apply {
            text = "DISCOVER"
            layoutParams = android.widget.LinearLayout.LayoutParams(0,
                android.widget.LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        btnStop = Button(this).apply {
            text = "STOP"
            isEnabled = false
            layoutParams = android.widget.LinearLayout.LayoutParams(0,
                android.widget.LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        row.addView(btnDiscover)
        row.addView(btnStop)
        root.addView(row)

        tvLog = TextView(this).apply {
            textSize = 12f
            fontFeatureSettings = "\"tnum\""
        }
        scrollLog = ScrollView(this).apply {
            layoutParams = android.widget.LinearLayout.LayoutParams(
                android.widget.LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f)
            addView(tvLog)
        }
        root.addView(scrollLog)
        return root
    }
}
