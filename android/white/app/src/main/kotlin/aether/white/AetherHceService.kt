package aether.white

import android.nfc.cardemulation.HostApduService
import android.os.Bundle
import android.util.Log

// ── Aether White AID ──────────────────────────────────────────────────────────
// F0  = Proprietary AID prefix (ISO 7816)
// 6165746865 72 = "aether" in ASCII
// 00  = NUL terminator
// Full: F0 61 65 74 68 65 72 00  (8 bytes)
private const val TAG        = "AetherHCE"
private const val AETHER_AID = "F061657468657200"

private val SELECT_AID_HEADER = byteArrayOf(0x00, 0xA4.toByte(), 0x04, 0x00)
private val SUCCESS_SW        = byteArrayOf(0x90.toByte(), 0x00)
private val FAILURE_SW        = byteArrayOf(0x6F.toByte(), 0x00)

/**
 * Aether White HCE service — NFC host card emulation.
 *
 * Registers AID [AETHER_AID] with the Android NFC stack.  When an NFC reader
 * (or another HCE-capable phone using an NFC reader app) sends a SELECT AID
 * command, this service responds with SW 9000 (success).  Any subsequent APDU
 * is treated as an incoming Aether wire-format packet; the raw bytes are handed
 * to registered [Listener]s and echoed back with SW 9000.
 *
 * Usage:
 *   AetherHceService.listener = myListener   // set from MainActivity
 */
class AetherHceService : HostApduService() {

    /** Listener interface for status and packet events. */
    interface Listener {
        fun onStatusChanged(status: String)
        fun onPacketReceived(summary: String)
    }

    companion object {
        // Static listener so MainActivity can receive events without a bound service.
        @Volatile var listener: Listener? = null
    }

    // ── HostApduService ───────────────────────────────────────────────────────

    override fun processCommandApdu(commandApdu: ByteArray, extras: Bundle?): ByteArray {
        val hex = commandApdu.joinToString(" ") { "%02X".format(it) }
        Log.d(TAG, "APDU RX: $hex")

        return when {
            isSelectAid(commandApdu) -> {
                Log.i(TAG, "SELECT AID $AETHER_AID — OK")
                listener?.onStatusChanged("NFC reader connected — AID selected")
                SUCCESS_SW
            }
            else -> {
                // Treat as raw Aether packet data
                val summary = parsePacketSummary(commandApdu)
                Log.i(TAG, "Packet: $summary")
                listener?.onPacketReceived(summary)
                // Echo the data back (+ SW 9000)
                commandApdu + SUCCESS_SW
            }
        }
    }

    override fun onDeactivated(reason: Int) {
        val reasonStr = when (reason) {
            DEACTIVATION_LINK_LOSS      -> "link loss"
            DEACTIVATION_DESELECTED     -> "deselected"
            else                         -> "reason=$reason"
        }
        Log.i(TAG, "HCE deactivated: $reasonStr")
        listener?.onStatusChanged("NFC field lost ($reasonStr)")
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private fun isSelectAid(apdu: ByteArray): Boolean {
        if (apdu.size < SELECT_AID_HEADER.size + 2) return false
        for (i in SELECT_AID_HEADER.indices) {
            if (apdu[i] != SELECT_AID_HEADER[i]) return false
        }
        val lc  = apdu[4].toInt() and 0xFF
        if (apdu.size < 5 + lc) return false
        val aid = apdu.slice(5 until 5 + lc).joinToString("") { "%02X".format(it) }
        return aid.equals(AETHER_AID, ignoreCase = true)
    }

    private fun parsePacketSummary(data: ByteArray): String {
        if (data.size < 2) return "${data.size}B"
        val cla = "%02X".format(data[0])
        val ins = "%02X".format(data[1])
        return "CLA=$cla INS=$ins ${data.size}B total"
    }
}
