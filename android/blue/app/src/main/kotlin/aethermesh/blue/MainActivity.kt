package aethermesh.blue

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.Intent
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
 * Minimal UI for the Aether BLE GATT peripheral node.
 *
 * Tap START to begin advertising and accepting connections.
 * The log view shows incoming packet summaries and connection events.
 */
class MainActivity : AppCompatActivity(), AetherMeshGattServer.Listener {

    private lateinit var btnStart: Button
    private lateinit var btnStop: Button
    private lateinit var tvLog: TextView
    private lateinit var scrollLog: ScrollView
    private lateinit var gattServer: AetherMeshGattServer

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Minimal programmatic layout — no XML needed for a test node.
        setContentView(buildLayout())

        gattServer = AetherMeshGattServer(applicationContext)
        gattServer.addListener(this)

        btnStart.setOnClickListener { requestPermissionsThenStart() }
        btnStop.setOnClickListener {
            gattServer.stop()
            btnStart.isEnabled = true
            btnStop.isEnabled = false
        }

        log("Aether BLE Node ready.")
        log("Service UUID: 61657468-6572-0001-0000-000000000000")
        log("Tap START to advertise.")
    }

    override fun onDestroy() {
        gattServer.stop()
        gattServer.removeListener(this)
        super.onDestroy()
    }

    // ── AetherMeshGattServer.Listener ─────────────────────────────────────────────

    override fun onStatusChanged(status: String) = runOnUiThread { log("[Status] $status") }
    override fun onPacketReceived(summary: String) = runOnUiThread { log("[Packet] $summary") }

    // ── Permissions ───────────────────────────────────────────────────────────

    private val permLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { results ->
        val allGranted = results.values.all { it }
        if (allGranted) startGattServer()
        else log("ERROR: BLE permissions denied.")
    }

    private fun requestPermissionsThenStart() {
        val needed = blePermissions().filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (needed.isEmpty()) startGattServer()
        else permLauncher.launch(needed.toTypedArray())
    }

    private fun blePermissions(): List<String> = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
        listOf(
            Manifest.permission.BLUETOOTH_ADVERTISE,
            Manifest.permission.BLUETOOTH_CONNECT,
            Manifest.permission.BLUETOOTH_SCAN
        )
    } else {
        listOf(
            Manifest.permission.BLUETOOTH,
            Manifest.permission.BLUETOOTH_ADMIN,
            Manifest.permission.ACCESS_FINE_LOCATION
        )
    }

    // ── Start / stop ─────────────────────────────────────────────────────────

    private fun startGattServer() {
        val btManager = getSystemService(BLUETOOTH_SERVICE) as BluetoothManager
        if (!btManager.adapter.isEnabled) {
            enableBtLauncher.launch(Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE))
            return
        }
        gattServer.start()
        btnStart.isEnabled = false
        btnStop.isEnabled = true
        log("GATT server started — advertising as 'Aether'")
    }

    private val enableBtLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { if (it.resultCode == RESULT_OK) startGattServer() else log("Bluetooth not enabled.") }

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

        val title = TextView(this).apply {
            text = "Aether BLE Node"
            textSize = 22f
            setPadding(0, 0, 0, 16)
        }
        root.addView(title)

        val row = android.widget.LinearLayout(this).apply {
            orientation = android.widget.LinearLayout.HORIZONTAL
        }

        btnStart = Button(this).apply {
            text = "START"
            layoutParams = android.widget.LinearLayout.LayoutParams(0,
                android.widget.LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        btnStop = Button(this).apply {
            text = "STOP"
            isEnabled = false
            layoutParams = android.widget.LinearLayout.LayoutParams(0,
                android.widget.LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        }
        row.addView(btnStart)
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
