package aethernet.teal

import android.annotation.SuppressLint
import android.bluetooth.*
import android.bluetooth.le.*
import android.content.Context
import android.os.Build
import android.os.ParcelUuid
import android.util.Log
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList

// ── Aether SLE (NearLink-over-BLE) UUIDs ─────────────────────────────────────
// Must match SleGattConstants.cs on the Windows/.NET side, and the HarmonyOS
// harmonyos/teal/ node, exactly. NearLink's SSAP protocol is API-analogous to
// GATT, so we register the SLE service/properties as a GATT service/characteristics.
//   Service     61657468-6572-0003-0000-000000000000  (aether, type=SLE)
//   Data  (W)   61657468-6572-0003-0001-000000000000  (central → peripheral, SSAP write)
//   Notify(N)   61657468-6572-0003-0002-000000000000  (peripheral → central, SSAP notify)
private val SLE_SERVICE_UUID = UUID.fromString("61657468-6572-0003-0000-000000000000")
private val SLE_DATA_UUID    = UUID.fromString("61657468-6572-0003-0001-000000000000")
private val SLE_NOTIFY_UUID  = UUID.fromString("61657468-6572-0003-0002-000000000000")
private val CCCD_UUID        = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")

private const val TAG = "AetherNetSLE"

/**
 * Aether Teal (NearLink) transport — a real SSAP-over-BLE-GATT peripheral.
 *
 * On standard Android, NearLink silicon is not present. NearLink's application protocol
 * (SSAP — SparkLink Service Access Protocol) is structurally identical to Bluetooth GATT:
 * the same Services → Properties → Descriptors model, the same notify/indicate semantics.
 * This service implements SSAP as a thin façade over BLE GATT using the canonical Aether
 * SLE UUIDs, so every Android node participates in the Aether Teal mesh today.
 *
 * Role: the Android phone is the GATT **server** (peripheral/advertiser); the Windows node
 * ([WinNearLinkBleTransportService]) is the GATT **client** (central/scanner), matching the
 * rest of the Aether transport family.
 *
 * Protocol:
 *   1. Phone advertises [SLE_SERVICE_UUID] + local name "Aether-Teal".
 *   2. Central connects and subscribes to [SLE_NOTIFY_UUID] notifications.
 *   3. Central writes a fragmented Aether wire-format packet to [SLE_DATA_UUID];
 *      this service reassembles the frames (see [Framer]) and surfaces the message.
 *   4. [send] fragments an outbound message and notifies every connected central.
 *
 * What the approximation cannot do: NearLink's radio (BPSK/QPSK/8PSK + Polar/HARQ,
 * 1/2/4 MHz channels) is incompatible with BLE's GFSK. Nodes running this class interoperate
 * with other Aether Teal nodes on the same BLE approximation, not with real NearLink hardware.
 * The HarmonyOS harmonyos/teal/ app uses the real @kit.NearLinkKit SDK for genuine hardware.
 */
@SuppressLint("MissingPermission")
class AetherNetSleService(private val context: Context) {

    interface Listener {
        fun onStatusChanged(status: String)
        fun onPacketReceived(summary: String)
    }

    private val listeners = CopyOnWriteArrayList<Listener>()
    fun addListener(l: Listener) { listeners.add(l) }
    fun removeListener(l: Listener) { listeners.remove(l) }

    private val bluetoothManager =
        context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager
    private val bluetoothAdapter: BluetoothAdapter get() = bluetoothManager.adapter

    private var gattServer: BluetoothGattServer? = null
    private var notifyChar: BluetoothGattCharacteristic? = null
    private val connectedDevices = CopyOnWriteArrayList<BluetoothDevice>()

    // Per-device inbound reassembly buffers, keyed by device address.
    private val reassembly = ConcurrentHashMap<String, Framer.Reassembler>()

    /** Number of centrals currently connected to this SLE peripheral. */
    val connectedPeerCount: Int get() = connectedDevices.size

    // ── GATT server callback ──────────────────────────────────────────────────

    private val gattCallback = object : BluetoothGattServerCallback() {

        override fun onConnectionStateChange(device: BluetoothDevice, status: Int, newState: Int) {
            when (newState) {
                BluetoothProfile.STATE_CONNECTED -> {
                    connectedDevices.add(device)
                    reassembly[device.address] = Framer.Reassembler()
                    notify("Connected: ${device.address}")
                    Log.i(TAG, "Central connected: ${device.address}")
                }
                BluetoothProfile.STATE_DISCONNECTED -> {
                    connectedDevices.remove(device)
                    reassembly.remove(device.address)
                    notify("Disconnected: ${device.address}")
                    Log.i(TAG, "Central disconnected: ${device.address}")
                }
            }
        }

        override fun onCharacteristicWriteRequest(
            device: BluetoothDevice,
            requestId: Int,
            characteristic: BluetoothGattCharacteristic,
            preparedWrite: Boolean,
            responseNeeded: Boolean,
            offset: Int,
            value: ByteArray
        ) {
            if (characteristic.uuid != SLE_DATA_UUID) return

            if (responseNeeded) {
                gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_SUCCESS, 0, null)
            }

            // Reassemble SSAP frames into a complete message before surfacing it.
            val buffer = reassembly.getOrPut(device.address) { Framer.Reassembler() }
            val message = buffer.accumulate(value)
            if (message != null) {
                Log.i(TAG, "RX ${message.size} bytes (reassembled) from ${device.address}")
                listeners.forEach { it.onPacketReceived(parsePacketSummary(message)) }
            }
        }

        override fun onDescriptorWriteRequest(
            device: BluetoothDevice,
            requestId: Int,
            descriptor: BluetoothGattDescriptor,
            preparedWrite: Boolean,
            responseNeeded: Boolean,
            offset: Int,
            value: ByteArray
        ) {
            if (descriptor.uuid == CCCD_UUID) {
                if (responseNeeded) {
                    gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_SUCCESS, 0, null)
                }
                val enabled = value.contentEquals(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE)
                Log.i(TAG, "CCCD ${if (enabled) "enabled" else "disabled"} by ${device.address}")
                notify(if (enabled) "Notifications enabled by ${device.address}" else "Notifications disabled")
            }
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    fun start() {
        gattServer = bluetoothManager.openGattServer(context, gattCallback)?.also { server ->
            val service = buildService()
            server.addService(service)
            notifyChar = service.getCharacteristic(SLE_NOTIFY_UUID)
        }

        startAdvertising()
        notify("Advertising as 'Aether-Teal' (SSAP over BLE)...")
        Log.i(TAG, "SLE server started. Service UUID: $SLE_SERVICE_UUID")
    }

    fun stop() {
        stopAdvertising()
        gattServer?.close()
        gattServer = null
        connectedDevices.clear()
        reassembly.clear()
        notify("Stopped")
    }

    // ── Send (outbound SSAP notify) ─────────────────────────────────────────────

    /**
     * Sends [data] to every connected central as one or more SSAP frames over the
     * notify property. Returns the number of centrals the message was dispatched to.
     */
    fun send(data: ByteArray): Int {
        val rx = notifyChar ?: return 0
        val server = gattServer ?: return 0
        val frames = Framer.frame(data)
        var dispatched = 0
        for (device in connectedDevices) {
            var ok = true
            for (frame in frames) {
                if (!notifyDevice(server, device, rx, frame)) { ok = false; break }
            }
            if (ok) dispatched++
        }
        Log.i(TAG, "Sent ${data.size} bytes in ${frames.size} frame(s) to $dispatched central(s)")
        return dispatched
    }

    private fun notifyDevice(
        server: BluetoothGattServer,
        device: BluetoothDevice,
        rx: BluetoothGattCharacteristic,
        frame: ByteArray
    ): Boolean = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        server.notifyCharacteristicChanged(device, rx, false, frame) == BluetoothStatusCodes.SUCCESS
    } else {
        @Suppress("DEPRECATION")
        rx.value = frame
        @Suppress("DEPRECATION")
        server.notifyCharacteristicChanged(device, rx, false)
    }

    // ── Service / characteristic construction ─────────────────────────────────

    private fun buildService(): BluetoothGattService {
        val service = BluetoothGattService(SLE_SERVICE_UUID, BluetoothGattService.SERVICE_TYPE_PRIMARY)

        // SSAP data property: central writes Aether packets here (write-without-response).
        val data = BluetoothGattCharacteristic(
            SLE_DATA_UUID,
            BluetoothGattCharacteristic.PROPERTY_WRITE or
            BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE,
            BluetoothGattCharacteristic.PERMISSION_WRITE
        )
        service.addCharacteristic(data)

        // SSAP notify property: we notify the central with outbound packets.
        val notify = BluetoothGattCharacteristic(
            SLE_NOTIFY_UUID,
            BluetoothGattCharacteristic.PROPERTY_NOTIFY,
            BluetoothGattCharacteristic.PERMISSION_READ
        )
        val cccd = BluetoothGattDescriptor(
            CCCD_UUID,
            BluetoothGattDescriptor.PERMISSION_READ or BluetoothGattDescriptor.PERMISSION_WRITE
        )
        notify.addDescriptor(cccd)
        service.addCharacteristic(notify)

        return service
    }

    // ── Advertising ───────────────────────────────────────────────────────────

    private var advertiser: BluetoothLeAdvertiser? = null

    private fun startAdvertising() {
        advertiser = bluetoothAdapter.bluetoothLeAdvertiser

        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
            .setConnectable(true)
            .setTimeout(0)
            .build()

        val advData = AdvertiseData.Builder()
            .addServiceUuid(ParcelUuid(SLE_SERVICE_UUID))
            .setIncludeDeviceName(false)
            .build()

        val scanResponse = AdvertiseData.Builder()
            .setIncludeDeviceName(true)
            .build()

        try { bluetoothAdapter.name = "Aether-Teal" } catch (_: Exception) { }

        advertiser?.startAdvertising(settings, advData, scanResponse, advertiseCallback)
    }

    private fun stopAdvertising() {
        advertiser?.stopAdvertising(advertiseCallback)
        advertiser = null
    }

    private val advertiseCallback = object : AdvertiseCallback() {
        override fun onStartSuccess(settingsInEffect: AdvertiseSettings) {
            Log.i(TAG, "Advertising started")
        }
        override fun onStartFailure(errorCode: Int) {
            notify("Advertising failed: error $errorCode")
            Log.e(TAG, "Advertising failed: $errorCode")
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private fun notify(status: String) {
        listeners.forEach { it.onStatusChanged(status) }
    }

    companion object {
        /**
         * Parses the Aether wire-format fixed header (matches PacketSerializer.cs) and
         * returns a human-readable summary. Exposed `internal` for unit testing without
         * an Android context.
         */
        internal fun parsePacketSummary(data: ByteArray): String =
            if (data.size >= 31) {
                val version  = data[0].toInt() and 0xFF
                val type     = data[1].toInt() and 0xFF
                val priority = data[18].toInt() and 0xFF // skip 16-byte GUID after version+type
                val ttl = (data[19].toInt() and 0xFF) or
                    ((data[20].toInt() and 0xFF) shl 8) or
                    ((data[21].toInt() and 0xFF) shl 16) or
                    ((data[22].toInt() and 0xFF) shl 24)
                val versionOk = version == 2
                "v=$version${if (versionOk) "✓" else "?"} type=$type pri=$priority ttl=$ttl total=${data.size}B"
            } else {
                "${data.size} bytes (too short for Aether header, need ≥31)"
            }
    }

    /**
     * SSAP-over-BLE framing — wire-identical to BleGattFramer.cs so the Windows central and
     * this Android peripheral interoperate byte-for-byte.
     *
     * Frame format (little-endian): [2] frame_count [2] frame_index [N] payload (N ≤ mtu-4).
     */
    object Framer {
        private const val HEADER = 4

        fun frame(data: ByteArray, mtu: Int = 1024): List<ByteArray> {
            require(mtu > HEADER) { "MTU must exceed $HEADER" }
            val maxPayload = mtu - HEADER
            val count = if (data.isEmpty()) 1 else (data.size + maxPayload - 1) / maxPayload
            val frames = ArrayList<ByteArray>(count)
            for (i in 0 until count) {
                val offset = i * maxPayload
                val len = minOf(maxPayload, data.size - offset).coerceAtLeast(0)
                val frame = ByteArray(HEADER + len)
                frame[0] = (count and 0xFF).toByte()
                frame[1] = ((count shr 8) and 0xFF).toByte()
                frame[2] = (i and 0xFF).toByte()
                frame[3] = ((i shr 8) and 0xFF).toByte()
                if (len > 0) System.arraycopy(data, offset, frame, HEADER, len)
                frames.add(frame)
            }
            return frames
        }

        /**
         * Accumulates inbound frames and yields the reassembled message when the full
         * sequence has arrived. An index-0 frame starts a fresh message.
         */
        class Reassembler {
            private val frames = ArrayList<ByteArray>()

            fun accumulate(frame: ByteArray): ByteArray? {
                if (frame.size >= HEADER && frame[2].toInt() == 0 && frame[3].toInt() == 0) {
                    frames.clear()
                }
                frames.add(frame)

                val first = frames.firstOrNull() ?: return null
                if (first.size < HEADER) return null
                val expected = (first[0].toInt() and 0xFF) or ((first[1].toInt() and 0xFF) shl 8)
                if (frames.size != expected) return null

                // Validate ordering, then concatenate payloads.
                var total = 0
                for (i in frames.indices) {
                    val f = frames[i]
                    if (f.size < HEADER) return null
                    val fc = (f[0].toInt() and 0xFF) or ((f[1].toInt() and 0xFF) shl 8)
                    val fi = (f[2].toInt() and 0xFF) or ((f[3].toInt() and 0xFF) shl 8)
                    if (fc != expected || fi != i) return null
                    total += f.size - HEADER
                }
                val result = ByteArray(total)
                var w = 0
                for (f in frames) {
                    val len = f.size - HEADER
                    if (len > 0) { System.arraycopy(f, HEADER, result, w, len); w += len }
                }
                frames.clear()
                return result
            }
        }
    }
}
