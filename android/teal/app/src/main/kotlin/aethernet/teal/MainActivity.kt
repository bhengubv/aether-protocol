package aethernet.teal

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
 * Aether Teal — NearLink transport node.
 *
 * NearLink silicon is not present on standard Android. Rather than a dead stub, this node
 * runs a real SSAP-over-BLE-GATT peripheral ([AetherNetSleService]): it advertises the Aether
 * SLE service, accepts central connections, reassembles inbound SSAP frames, and notifies
 * outbound ones — participating in the Aether Teal mesh at BLE range/bandwidth.
 *
 * SSAP (SparkLink Service Access Protocol) is structurally identical to GATT (Services →
 * Properties → Descriptors, notify/indicate), so the BLE GATT calls here map 1:1 onto the
 * NearLink SDK's ssaps_* (server) and ssapc_* (client) calls. The canonical Aether SLE UUIDs
 * (61657468-6572-0003-…) match the Windows WinNearLinkBleTransportService and the HarmonyOS
 * harmonyos/teal/ node, so adopting real NearLink silicon is a radio swap only — UUIDs,
 * routing, and application code are unchanged.
 *
 * What it cannot do: NearLink's radio (BPSK/QPSK/8PSK + Polar/HARQ, up to 4 MHz channels) is
 * incompatible with BLE GFSK, so this node interoperates with other Aether Teal nodes on the
 * same BLE approximation, not with genuine NearLink hardware.
 *
 * Reference: HiSilicon WS63 SDK (gitee.com/HiSpark/fbb_ws63) — sle_ssap_client.h,
 * sle_ssap_server.h, sle_device_discovery.h, sle_connection_manager.h.
 */
class MainActivity : AppCompatActivity(), AetherNetSleService.Listener {

    private lateinit var btnStart: Button
    private lateinit var btnStop: Button
    private lateinit var tvLog: TextView
    private lateinit var scrollLog: ScrollView
    private lateinit var sleService: AetherNetSleService

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())

        sleService = AetherNetSleService(applicationContext)
        sleService.addListener(this)

        btnStart.setOnClickListener { requestPermissionsThenStart() }
        btnStop.setOnClickListener {
            sleService.stop()
            btnStart.isEnabled = true
            btnStop.isEnabled = false
        }

        log("Aether Teal — NearLink over BLE (SSAP approximation).")
        log("SLE service: 61657468-6572-0003-0000-000000000000")
        log("Tap START to advertise.")
    }

    override fun onDestroy() {
        sleService.stop()
        sleService.removeListener(this)
        super.onDestroy()
    }

    // ── AetherNetSleService.Listener ─────────────────────────────────────────────

    override fun onStatusChanged(status: String) = runOnUiThread { log("[Status] $status") }
    override fun onPacketReceived(summary: String) = runOnUiThread { log("[Packet] $summary") }

    // ── Permissions ───────────────────────────────────────────────────────────

    private val permLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { results ->
        if (results.values.all { it }) startSleService()
        else log("ERROR: BLE permissions denied.")
    }

    private fun requestPermissionsThenStart() {
        val needed = blePermissions().filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (needed.isEmpty()) startSleService()
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

    private fun startSleService() {
        val btManager = getSystemService(BLUETOOTH_SERVICE) as BluetoothManager
        if (!btManager.adapter.isEnabled) {
            enableBtLauncher.launch(Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE))
            return
        }
        sleService.start()
        btnStart.isEnabled = false
        btnStop.isEnabled = true
        log("SLE server started — advertising as 'Aether-Teal'")
    }

    private val enableBtLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { if (it.resultCode == RESULT_OK) startSleService() else log("Bluetooth not enabled.") }

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
            text = "Aether Teal — NearLink (SSAP over BLE)"
            textSize = 20f
            setPadding(0, 0, 0, 16)
        })

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
