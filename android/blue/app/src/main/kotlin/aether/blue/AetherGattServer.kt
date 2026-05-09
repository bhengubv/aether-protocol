package aether.blue

import android.annotation.SuppressLint
import android.bluetooth.*
import android.bluetooth.le.*
import android.content.Context
import android.os.Build
import android.os.ParcelUuid
import android.util.Log
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList

// ── Aether GATT UUIDs ────────────────────────────────────────────────────────
// Must match BleGattConstants.cs on the Windows side exactly.
private val SERVICE_UUID    = UUID.fromString("61657468-6572-0001-0000-000000000000")
private val TX_CHAR_UUID    = UUID.fromString("61657468-6572-0002-0000-000000000000") // central writes here
private val RX_CHAR_UUID    = UUID.fromString("61657468-6572-0003-0000-000000000000") // we notify here
private val CCCD_UUID       = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")

private const val TAG = "AetherGATT"

/**
 * BLE GATT peripheral (server) for the Aether mesh protocol RF bring-up test.
 *
 * Role: Android phone acts as the GATT server (peripheral/advertiser).
 * The Windows node acts as the GATT client (central/scanner).
 *
 * Protocol:
 *   1. Phone advertises [SERVICE_UUID] + local name "Aether".
 *   2. Windows connects and subscribes to [RX_CHAR_UUID] notifications.
 *   3. Windows writes an Aether wire-format packet to [TX_CHAR_UUID].
 *   4. Phone receives the packet, parses the header, logs it, and notifies
 *      [RX_CHAR_UUID] with an echo response so the Windows node can verify
 *      the round-trip.
 */
@SuppressLint("MissingPermission")
class AetherGattServer(private val context: Context) {

    interface Listener {
        fun onStatusChanged(status: String)
        fun onPacketReceived(summary: String)
    }

    private val listeners = CopyOnWriteArrayList<Listener>()
    fun addListener(l: Listener) { listeners.add(l) }
    fun removeListener(l: Listener) { listeners.remove(l) }

    private val bluetoothManager = context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager
    private val bluetoothAdapter: BluetoothAdapter get() = bluetoothManager.adapter

    private var gattServer: BluetoothGattServer? = null
    private var rxChar: BluetoothGattCharacteristic? = null
    private val connectedDevices = CopyOnWriteArrayList<BluetoothDevice>()

    // ── GATT server callback ──────────────────────────────────────────────────

    private val gattCallback = object : BluetoothGattServerCallback() {

        override fun onConnectionStateChange(device: BluetoothDevice, status: Int, newState: Int) {
            when (newState) {
                BluetoothProfile.STATE_CONNECTED -> {
                    connectedDevices.add(device)
                    notify("Connected: ${device.address}")
                    Log.i(TAG, "Central connected: ${device.address}")
                }
                BluetoothProfile.STATE_DISCONNECTED -> {
                    connectedDevices.remove(device)
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
            if (characteristic.uuid != TX_CHAR_UUID) return

            if (responseNeeded) {
                gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_SUCCESS, 0, null)
            }

            handleIncomingPacket(device, value)
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
            rxChar = service.getCharacteristic(RX_CHAR_UUID)
        }

        startAdvertising()
        notify("Advertising as 'Aether'...")
        Log.i(TAG, "GATT server started. Service UUID: $SERVICE_UUID")
    }

    fun stop() {
        stopAdvertising()
        gattServer?.close()
        gattServer = null
        connectedDevices.clear()
        notify("Stopped")
    }

    // ── Service / characteristic construction ─────────────────────────────────

    private fun buildService(): BluetoothGattService {
        val service = BluetoothGattService(SERVICE_UUID, BluetoothGattService.SERVICE_TYPE_PRIMARY)

        // TX: central writes Aether packets here (write-without-response for low latency)
        val tx = BluetoothGattCharacteristic(
            TX_CHAR_UUID,
            BluetoothGattCharacteristic.PROPERTY_WRITE or
            BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE,
            BluetoothGattCharacteristic.PERMISSION_WRITE
        )
        service.addCharacteristic(tx)

        // RX: we notify the central with response packets
        val rx = BluetoothGattCharacteristic(
            RX_CHAR_UUID,
            BluetoothGattCharacteristic.PROPERTY_NOTIFY,
            BluetoothGattCharacteristic.PERMISSION_READ
        )
        val cccd = BluetoothGattDescriptor(
            CCCD_UUID,
            BluetoothGattDescriptor.PERMISSION_READ or BluetoothGattDescriptor.PERMISSION_WRITE
        )
        rx.addDescriptor(cccd)
        service.addCharacteristic(rx)

        return service
    }

    // ── Advertising ───────────────────────────────────────────────────────────

    private var advertiser: BluetoothLeAdvertiser? = null

    private fun startAdvertising() {
        advertiser = bluetoothAdapter.bluetoothLeAdvertiser

        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
            .setConnectable(true)
            .setTimeout(0) // advertise until stop() is called
            .build()

        val data = AdvertiseData.Builder()
            .addServiceUuid(ParcelUuid(SERVICE_UUID))
            .setIncludeDeviceName(false) // included in scan response below
            .build()

        val scanResponse = AdvertiseData.Builder()
            .setIncludeDeviceName(true) // "Aether" local name
            .build()

        // Set local name to "Aether" before advertising
        // Note: requires BLUETOOTH_ADMIN; on API 31+ use BluetoothAdapter.setName
        try { bluetoothAdapter.name = "Aether" } catch (_: Exception) { }

        advertiser?.startAdvertising(settings, data, scanResponse, advertiseCallback)
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

    // ── Packet handling ───────────────────────────────────────────────────────

    /**
     * Parses the Aether wire-format header from [data] and sends an echo response.
     *
     * Wire format (all ints little-endian) — matches PacketSerializer.cs exactly:
     *   [1]  protocol_version   (should be 0x02)
     *   [1]  packet_type        (0x03 = Data)
     *   [16] packet_id          GUID bytes
     *   [1]  priority
     *   [4]  ttl                int32 LE
     *   [8]  timestamp_ms       int64 LE
     *   [2]  source_uhid_len    uint16 LE
     *   [N]  source_uhid        UTF-8
     *   [2]  dest_uhid_len
     *   [N]  dest_uhid
     *   [2]  nonce_len
     *   [N]  nonce
     *   [4]  payload_len        int32 LE
     *   [N]  payload
     *   [2]  sig_len
     *   [N]  signature
     *
     * Minimum header before variable fields = 1+1+16+1+4+8 = 31 bytes.
     */
    private fun handleIncomingPacket(device: BluetoothDevice, data: ByteArray) {
        Log.i(TAG, "RX ${data.size} bytes from ${device.address}")

        val summary = if (data.size >= 31) {
            val buf = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)
            val version  = buf.get().toInt() and 0xFF
            val type     = buf.get().toInt() and 0xFF
            buf.position(18)                             // skip 16-byte GUID
            val priority = buf.get().toInt() and 0xFF
            val ttl      = buf.int

            val versionOk = version == 2
            "v=$version${if (versionOk) "✓" else "?"} type=$type pri=$priority ttl=$ttl total=${data.size}B"
        } else {
            "${data.size} bytes (too short for Aether header, need ≥31)"
        }

        Log.i(TAG, "Packet: $summary")
        listeners.forEach { it.onPacketReceived(summary) }

        // Echo the packet back with TTL decremented (offset 18+1+4 = bytes 19-22 are TTL).
        // TTL is at fixed offset 19 in the wire format (after ver[1]+type[1]+guid[16]+pri[1]).
        val response = data.copyOf()
        if (response.size >= 24) {
            val ttlOffset = 19
            val currentTtl = ByteBuffer.wrap(response, ttlOffset, 4).order(ByteOrder.LITTLE_ENDIAN).int
            val newTtl = maxOf(0, currentTtl - 1)
            ByteBuffer.wrap(response, ttlOffset, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(newTtl)
        }
        notifyRx(device, response)
    }

    private fun notifyRx(device: BluetoothDevice, data: ByteArray) {
        val rx = rxChar ?: return
        val sent = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            gattServer?.notifyCharacteristicChanged(device, rx, false, data)
        } else {
            @Suppress("DEPRECATION")
            rx.value = data
            @Suppress("DEPRECATION")
            gattServer?.notifyCharacteristicChanged(device, rx, false)
        }
        Log.i(TAG, "RX notification sent to ${device.address}: $sent (${data.size} bytes)")
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private fun notify(status: String) {
        listeners.forEach { it.onStatusChanged(status) }
    }
}
