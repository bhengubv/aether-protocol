package aether.green

import android.annotation.SuppressLint
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.wifi.p2p.WifiP2pInfo
import android.net.wifi.p2p.WifiP2pManager
import android.util.Log
import java.io.InputStream
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.CopyOnWriteArrayList

private const val TAG      = "AetherWFD"
private const val TCP_PORT = 8888

/**
 * Wi-Fi Direct GATT-equivalent for the Aether Green transport node.
 *
 * Role: either Group Owner (server, accepts TCP on 8888) or Client (connects TCP to the GO).
 * The role is decided by the Android Wi-Fi Direct framework after `connect()`.
 *
 * Protocol:
 *   1. Device advertises (discoverPeers) and optionally connects to a specific peer.
 *   2. On group formation, GO starts a `ServerSocket(8888)`; client connects TCP to GO.
 *   3. Packets are framed: `[4-byte LE length][payload]`.
 *   4. On receive, the node echoes the packet back with TTL decremented.
 */
@SuppressLint("MissingPermission")
class AetherWifiDirectService(private val context: Context) {

    interface Listener {
        fun onStatusChanged(status: String)
        fun onPacketReceived(summary: String)
    }

    private val listeners = CopyOnWriteArrayList<Listener>()
    fun addListener(l: Listener)    { listeners.add(l) }
    fun removeListener(l: Listener) { listeners.remove(l) }

    private val manager: WifiP2pManager by lazy {
        context.getSystemService(Context.WIFI_P2P_SERVICE) as WifiP2pManager
    }
    private var channel: WifiP2pManager.Channel? = null
    private var serverSocket: ServerSocket? = null

    // ── Broadcast receiver ────────────────────────────────────────────────────

    val receiver: BroadcastReceiver = object : BroadcastReceiver() {
        override fun onReceive(ctx: Context, intent: Intent) {
            when (intent.action) {
                WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION -> {
                    val state = intent.getIntExtra(WifiP2pManager.EXTRA_WIFI_STATE, -1)
                    notify(if (state == WifiP2pManager.WIFI_P2P_STATE_ENABLED)
                        "Wi-Fi Direct enabled" else "Wi-Fi Direct disabled")
                }
                WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION -> {
                    @Suppress("DEPRECATION")
                    val info = intent.getParcelableExtra<WifiP2pInfo>(
                        WifiP2pManager.EXTRA_WIFI_P2P_INFO)
                    if (info != null) handleConnectionInfo(info)
                }
                WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION -> {
                    manager.requestPeers(channel) { peers ->
                        notify("Discovered ${peers.deviceList.size} Wi-Fi Direct peer(s)")
                    }
                }
            }
        }
    }

    val intentFilter = IntentFilter().apply {
        addAction(WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION)
        addAction(WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION)
        addAction(WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION)
        addAction(WifiP2pManager.WIFI_P2P_THIS_DEVICE_CHANGED_ACTION)
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    fun initialize() {
        channel = manager.initialize(context, context.mainLooper, null)
        notify("Wi-Fi Direct initialised. Service UUID: aether.green")
        notify("Tap DISCOVER to start peer discovery.")
    }

    fun discoverPeers() {
        manager.discoverPeers(channel, object : WifiP2pManager.ActionListener {
            override fun onSuccess()            = notify("Peer discovery started...")
            override fun onFailure(reason: Int) = notify("Peer discovery failed (reason=$reason)")
        })
    }

    fun stop() {
        serverSocket?.runCatching { close() }
        manager.removeGroup(channel, null)
        notify("Stopped")
    }

    // ── Connection handling ───────────────────────────────────────────────────

    private fun handleConnectionInfo(info: WifiP2pInfo) {
        if (!info.groupFormed) { notify("Group not formed yet"); return }

        if (info.isGroupOwner) {
            notify("Role: Group Owner — starting TCP server on port $TCP_PORT")
            Thread(::runServer).start()
        } else {
            val go = info.groupOwnerAddress?.hostAddress ?: "192.168.49.1"
            notify("Role: Client — connecting to GO at $go:$TCP_PORT")
            Thread { runClient(go) }.start()
        }
    }

    // ── GO: TCP server ────────────────────────────────────────────────────────

    private fun runServer() {
        try {
            serverSocket = ServerSocket(TCP_PORT)
            notify("TCP server listening on port $TCP_PORT")
            while (true) {
                val client = serverSocket?.accept() ?: break
                notify("Peer connected: ${client.inetAddress.hostAddress}")
                Thread { handleSocket(client) }.start()
            }
        } catch (e: Exception) {
            if (serverSocket?.isClosed == false)
                Log.e(TAG, "Server error", e)
        }
    }

    // ── Client: TCP connect to GO ─────────────────────────────────────────────

    private fun runClient(goAddress: String) {
        try {
            val socket = Socket(goAddress, TCP_PORT)
            notify("Connected to Group Owner")
            handleSocket(socket)
        } catch (e: Exception) {
            notify("Client connection failed: ${e.message}")
        }
    }

    // ── Per-socket read/echo loop ─────────────────────────────────────────────

    private fun handleSocket(socket: Socket) {
        val input  = socket.getInputStream()
        val output = socket.getOutputStream()
        try {
            while (!socket.isClosed) {
                val data    = readPacket(input) ?: break
                val summary = parsePacketSummary(data)
                notify("[Packet] $summary")
                listeners.forEach { it.onPacketReceived(summary) }

                // Echo with TTL decremented (offset 19 in the Aether wire format)
                val echo = data.copyOf()
                if (echo.size >= 24) {
                    val ttl = ByteBuffer.wrap(echo, 19, 4).order(ByteOrder.LITTLE_ENDIAN).int
                    ByteBuffer.wrap(echo, 19, 4).order(ByteOrder.LITTLE_ENDIAN)
                        .putInt(maxOf(0, ttl - 1))
                }
                writePacket(output, echo)
            }
        } catch (e: Exception) {
            notify("Socket closed: ${e.message}")
        } finally {
            socket.runCatching { close() }
        }
    }

    // ── Framing ───────────────────────────────────────────────────────────────

    private fun readPacket(input: InputStream): ByteArray? {
        val lenBuf = ByteArray(4)
        var read = 0
        while (read < 4) {
            val n = input.read(lenBuf, read, 4 - read)
            if (n < 0) return null
            read += n
        }
        val len = ByteBuffer.wrap(lenBuf).order(ByteOrder.LITTLE_ENDIAN).int
        if (len <= 0 || len > 64 * 1024 * 1024) return null
        val data = ByteArray(len)
        var offset = 0
        while (offset < len) {
            val n = input.read(data, offset, len - offset)
            if (n < 0) return null
            offset += n
        }
        return data
    }

    private fun writePacket(output: OutputStream, data: ByteArray) {
        output.write(ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(data.size).array())
        output.write(data)
        output.flush()
    }

    private fun parsePacketSummary(data: ByteArray): String {
        if (data.size < 31) return "${data.size}B (too short for Aether header)"
        val buf      = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)
        val version  = buf.get().toInt() and 0xFF
        val type     = buf.get().toInt() and 0xFF
        buf.position(18)
        val priority = buf.get().toInt() and 0xFF
        val ttl      = buf.int
        return "v=$version type=$type pri=$priority ttl=$ttl total=${data.size}B"
    }

    private fun notify(status: String) {
        Log.i(TAG, status)
        listeners.forEach { it.onStatusChanged(status) }
    }
}
