// SPDX-License-Identifier: MIT

package aethernet.transport.webrtc

import aethernet.content.appendJsonString
import aethernet.transport.TransportService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import org.slf4j.LoggerFactory
import java.io.Closeable

/**
 * Carries WebRTC SDP/ICE signalling over an existing [TransportService] — typically the AetherNet
 * QUIC/HTTP relay, but the radio mesh works too — so two distant peers can negotiate a direct data
 * channel without a dedicated signalling server. Once the channel is open, the media and app traffic
 * flow peer-to-peer; only the short handshake ever touches the relay.
 *
 * This is the transport-backed sibling of [InMemorySignalingBus]. The in-memory bus only routes
 * within a single process; this carrier rides a real [TransportService], so two *separate devices*
 * can complete the offer/answer/ICE exchange. It plugs into [WebRtcTransport]'s existing `signaling`
 * seam with no interface change.
 *
 * Each signal is framed with a 4-byte magic prefix and a compact JSON body. Inbound bytes on the
 * underlying transport that lack the prefix are ignored — they are ordinary application traffic, not
 * signalling. Give this a transport whose [TransportService.dataReceived] is dedicated to signalling
 * (e.g. a relay connection reserved for control traffic), so the prefixed control frames never reach
 * the application data path.
 *
 * ## Wire compatibility
 * The frame is **byte-identical** to the C# [`RelayWebRtcSignaling`] reference: the same `AWS1` magic
 * prefix followed by JSON whose field names, casing, ordering and null-omission match what C#'s
 * `System.Text.Json` emits for `WebRtcSignal` (`FromUhid`, `ToUhid`, `Type` as an integer, then the
 * optional `Sdp` / `Candidate`, always-present `SdpMLineIndex`, and optional `SdpMid`). A Kotlin node
 * and a C# node can therefore complete a handshake across the relay.
 *
 * @param channel     The transport whose data path carries the framed signalling to and from the peer.
 * @param localUhid   This node's UHID. Signals framed for the peer carry it; inbound signals not
 *                    addressed to it are dropped. Optional — if empty, no addressee filtering is done
 *                    (the underlying transport already delivers only this node's traffic).
 */
class RelayWebRtcSignaling(
    private val channel: TransportService,
    private val localUhid: String = "",
) : WebRtcSignaling, Closeable {

    private val log = LoggerFactory.getLogger(RelayWebRtcSignaling::class.java)

    // Collects the channel's inbound Flow and dispatches deframed signals. Its own scope so close()
    // tears the collector down deterministically without touching the shared transport.
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val receiveJob: Job

    @Volatile
    private var handler: ((WebRtcSignal) -> Unit)? = null

    init {
        receiveJob = scope.launch {
            channel.dataReceived.collect { (fromUhid, data) ->
                onChannelData(fromUhid, data)
            }
        }
    }

    override fun sendSignal(peerUhid: String, signal: WebRtcSignal): Boolean {
        if (!scope.isActive) return false
        val frame = frame(signal)
        // WebRtcSignaling.sendSignal is fire-and-forget (Boolean, non-suspending) — the WebRTC
        // transport calls it from observer callbacks and never awaits. TransportService.sendAsync is
        // suspending, so we hand the send to the scope. Returning true means "handed to the channel",
        // matching the in-memory bus's trySend contract.
        scope.launch {
            try {
                channel.sendAsync(peerUhid, frame)
            } catch (e: Throwable) {
                log.warn("[WebRTC] relay send of {} signal to {} failed", signal.type, peerUhid, e)
            }
        }
        return true
    }

    override fun onSignal(handler: (WebRtcSignal) -> Unit) {
        this.handler = handler
    }

    private fun onChannelData(fromUhid: String, data: ByteArray) {
        if (!hasMagic(data)) return // ordinary app traffic, not a signalling frame
        val signal = try {
            deframe(data)
        } catch (e: Exception) {
            log.warn("[WebRTC] discarded malformed signalling frame from {}", fromUhid, e)
            return
        }
        // Drop signals not addressed to this node (only when we know our own UHID).
        if (localUhid.isNotEmpty() && signal.toUhid != localUhid) return
        try {
            handler?.invoke(signal)
        } catch (e: Throwable) {
            log.warn("[WebRTC] signal handler threw for frame from {}", fromUhid, e)
        }
    }

    override fun close() {
        receiveJob.cancel()
        scope.cancel()
    }

    companion object {
        // "AWS1" = Aether WebRtc Signal, framing v1. Byte-identical to the C# reference's Magic.
        private val MAGIC = byteArrayOf('A'.code.toByte(), 'W'.code.toByte(), 'S'.code.toByte(), '1'.code.toByte())

        private fun hasMagic(data: ByteArray): Boolean =
            data.size >= MAGIC.size &&
                data[0] == MAGIC[0] && data[1] == MAGIC[1] && data[2] == MAGIC[2] && data[3] == MAGIC[3]

        /** Frames a signal as `AWS1` + canonical JSON body — byte-identical to the C# framing. */
        internal fun frame(signal: WebRtcSignal): ByteArray {
            val body = encodeJson(signal).toByteArray(Charsets.UTF_8)
            val out = ByteArray(MAGIC.size + body.size)
            System.arraycopy(MAGIC, 0, out, 0, MAGIC.size)
            System.arraycopy(body, 0, out, MAGIC.size, body.size)
            return out
        }

        /** Parses a signal from an `AWS1`-prefixed frame. Assumes [hasMagic] already passed. */
        internal fun deframe(data: ByteArray): WebRtcSignal {
            val json = String(data, MAGIC.size, data.size - MAGIC.size, Charsets.UTF_8)
            return decodeJson(json)
        }

        /**
         * Canonical JSON for a signal, byte-identical to what C#'s `System.Text.Json` emits for the
         * `WebRtcSignal` record: PascalCase keys in declaration order (`FromUhid`, `ToUhid`, `Type`,
         * then optional `Sdp` / `Candidate`, always-present `SdpMLineIndex`, then optional `SdpMid`),
         * `Type` as its integer ordinal, and null string fields omitted (C# `WhenWritingNull`).
         */
        internal fun encodeJson(s: WebRtcSignal): String = buildString {
            append("{\"FromUhid\":"); appendJsonString(s.fromUhid)
            append(",\"ToUhid\":"); appendJsonString(s.toUhid)
            append(",\"Type\":").append(s.type.wireValue)
            if (s.sdp != null) { append(",\"Sdp\":"); appendJsonString(s.sdp) }
            if (s.candidate != null) { append(",\"Candidate\":"); appendJsonString(s.candidate) }
            append(",\"SdpMLineIndex\":").append(s.sdpMLineIndex)
            if (s.sdpMid != null) { append(",\"SdpMid\":"); appendJsonString(s.sdpMid) }
            append('}')
        }

        /** Parses the canonical (or any equivalently-keyed) signal JSON. Tolerant of missing optionals. */
        internal fun decodeJson(json: String): WebRtcSignal {
            val o = JSONObject(json)
            return WebRtcSignal(
                fromUhid = o.optString("FromUhid", ""),
                toUhid = o.optString("ToUhid", ""),
                type = webRtcSignalTypeFromWire(o.optInt("Type", 0)),
                sdp = if (o.has("Sdp") && !o.isNull("Sdp")) o.getString("Sdp") else null,
                candidate = if (o.has("Candidate") && !o.isNull("Candidate")) o.getString("Candidate") else null,
                sdpMid = if (o.has("SdpMid") && !o.isNull("SdpMid")) o.getString("SdpMid") else null,
                sdpMLineIndex = o.optInt("SdpMLineIndex", 0),
            )
        }
    }
}

/**
 * Integer wire ordinal for the signal type — must match the C# `WebRtcSignalType` enum values
 * (`Offer = 0`, `Answer = 1`, `IceCandidate = 2`) that `System.Text.Json` serialises numerically.
 */
private val WebRtcSignalType.wireValue: Int
    get() = when (this) {
        WebRtcSignalType.OFFER -> 0
        WebRtcSignalType.ANSWER -> 1
        WebRtcSignalType.ICE_CANDIDATE -> 2
    }

/** Inverse of [wireValue]; unknown ordinals fall back to [WebRtcSignalType.ICE_CANDIDATE]. */
private fun webRtcSignalTypeFromWire(v: Int): WebRtcSignalType = when (v) {
    0 -> WebRtcSignalType.OFFER
    1 -> WebRtcSignalType.ANSWER
    else -> WebRtcSignalType.ICE_CANDIDATE
}
