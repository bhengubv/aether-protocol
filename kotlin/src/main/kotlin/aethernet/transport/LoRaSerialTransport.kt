// SPDX-License-Identifier: MIT

package aethernet.transport

import com.fazecast.jSerialComm.SerialPort
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import java.io.InputStream
import java.io.OutputStream
import java.util.concurrent.ConcurrentHashMap
import kotlin.concurrent.thread

/** Configuration for a RYLR-class serial LoRa module. */
data class LoRaOptions(
    val portName: String,            // "COM5" or "/dev/ttyUSB0" — required
    val baudRate: Int = 115200,
    val address: Int = 1,            // this node's LoRa address (1-65535)
    val networkId: Int = 18,         // RYLR network id
    val bandHz: Long = 868_500_000L, // EU868; US915 = 915_000_000
    val spreadingFactor: Int = 9,    // 7-12
    val bandwidthIndex: Int = 7,     // 7=125kHz, 8=250, 9=500
    val codingRate: Int = 1,         // 1=4/5
    val preambleLength: Int = 12,
)

/**
 * Real LoRa (Aether Red / CircleLink) transport over a serial-attached RYLR-class SX127x/SX126x
 * module, via jSerialComm (JVM/desktop). Mirrors the C#/Go/Rust/C `LoRaSerialTransport`: opens the
 * serial port, configures the radio, sends with `AT+SEND`, and surfaces inbound `+RCV` frames.
 *
 * Like [aethernet.transport.webrtc.WebRtcTransport] this is a JVM/desktop transport pulling a
 * third-party JVM dependency (`com.fazecast:jSerialComm`); it is NOT part of the AOSP Soong core
 * subset. On Android, a serial-attached LoRa module is driven through the USB-host API at the app
 * layer instead.
 *
 * Verification status: real driver; compiles under the Gradle/JVM build. Runtime-UNVERIFIED — not
 * exercised against a physical module.
 */
class LoRaSerialTransport(private val options: LoRaOptions) : TransportService {

    override val name = "Aether Red (LoRa/CircleLink)"
    override val maxBandwidthBps = 37_500L     // SF7/BW125 ~= 37.5 kbps
    override val maxRangeMeters = 15_000        // up to ~15 km LOS
    override val powerCostRelative = 8          // high TX power (1-10 scale)
    override val maxConcurrentPeers = 255

    /** Non-null metrics instance (overrides the nullable default). */
    override val metrics = PerTransportMetrics()

    @Volatile private var available = false
    @Volatile private var running = false
    private var port: SerialPort? = null
    private var output: OutputStream? = null
    private var reader: Thread? = null
    private val peerAddrs = ConcurrentHashMap<String, Int>()

    private val mutableDataReceived = MutableSharedFlow<Pair<String, ByteArray>>(
        replay = 0,
        extraBufferCapacity = 100,
    )
    override val dataReceived: Flow<Pair<String, ByteArray>> = mutableDataReceived.asSharedFlow()

    override val isAvailable: Boolean get() = available

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    /** Opens the serial port, configures the radio, and starts the reader thread. */
    fun open() {
        if (available) return
        val p = SerialPort.getCommPort(options.portName)
        p.setBaudRate(options.baudRate)
        p.setComPortTimeouts(SerialPort.TIMEOUT_READ_SEMI_BLOCKING, 1000, 0)
        check(p.openPort()) { "lora: cannot open ${options.portName}" }
        port = p
        output = p.outputStream
        configure()
        running = true
        available = true
        val input = p.inputStream
        reader = thread(start = true, isDaemon = true, name = "lora-reader") { readLoop(input) }
    }

    private fun configure() {
        for (cmd in listOf(
            "AT+ADDRESS=${options.address}",
            "AT+NETWORKID=${options.networkId}",
            "AT+BAND=${options.bandHz}",
            "AT+PARAMETER=${options.spreadingFactor},${options.bandwidthIndex}," +
                "${options.codingRate},${options.preambleLength}",
        )) {
            writeRaw((cmd + "\r\n").toByteArray(Charsets.US_ASCII))
        }
    }

    private fun writeRaw(bytes: ByteArray) {
        val out = output ?: return
        out.write(bytes)
        out.flush()
    }

    override fun close() {
        running = false
        available = false
        output = null
        port?.closePort()
        port = null
    }

    // ── TransportService ───────────────────────────────────────────────────────

    /** Map an AetherNet peer UHID to a numeric LoRa node address (1-65535) for directed sends. */
    fun registerPeer(peerUhid: String, address: Int) {
        if (peerUhid.isNotEmpty()) peerAddrs[peerUhid] = address
    }

    override suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean {
        val p = port
        if (!available || p == null || data.isEmpty()) {
            metrics.recordSample(0L, success = false, bytesTransferred = 0L)
            return false
        }
        val addr = peerAddrs[peerUhid] ?: 0 // 0 = broadcast (managed-flood mesh)
        // Hex-encode so the payload survives the AT text protocol; length field is the hex length.
        val hex = data.joinToString("") { "%02X".format(it) }
        val cmd = "AT+SEND=$addr,${hex.length},$hex\r\n".toByteArray(Charsets.US_ASCII)
        return try {
            writeRaw(cmd)
            metrics.recordSample(0L, success = true, bytesTransferred = data.size.toLong())
            true
        } catch (e: Exception) {
            metrics.recordSample(0L, success = false, bytesTransferred = 0L)
            false
        }
    }

    override suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean =
        sendAsync(peerUhid, data)

    override fun isConnected(peerUhid: String): Boolean = available // connectionless broadcast medium

    // ── Receive ────────────────────────────────────────────────────────────────

    private fun readLoop(input: InputStream) {
        val line = StringBuilder()
        val chunk = ByteArray(256)
        while (running) {
            val n = try {
                input.read(chunk)
            } catch (e: Exception) {
                break
            }
            if (n < 0) break
            if (n == 0) continue
            for (i in 0 until n) {
                val c = chunk[i].toInt().toChar()
                if (c == '\n' || c == '\r') {
                    if (line.isNotEmpty()) {
                        handleLine(line.toString().trim())
                        line.setLength(0)
                    }
                } else {
                    line.append(c)
                }
            }
        }
    }

    private fun handleLine(line: String) {
        // RYLR inbound frame: +RCV=<address>,<length>,<hexdata>,<rssi>,<snr>
        if (!line.startsWith("+RCV=")) return
        val parts = line.substring(5).split(",")
        if (parts.size < 3) return
        val addr = parts[0].toIntOrNull() ?: return
        val hex = parts[2]
        if (hex.length % 2 != 0) return
        val data = ByteArray(hex.length / 2)
        for (i in data.indices) {
            val byte = hex.substring(i * 2, i * 2 + 2).toIntOrNull(16) ?: return
            data[i] = byte.toByte()
        }
        mutableDataReceived.tryEmit(Pair(addr.toString(), data))
    }
}
