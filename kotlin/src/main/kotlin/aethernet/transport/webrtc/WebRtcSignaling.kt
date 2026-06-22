// SPDX-License-Identifier: MIT

package aethernet.transport.webrtc

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.Closeable
import java.util.concurrent.ConcurrentHashMap

/**
 * The kind of WebRTC signalling message exchanged while a direct link is set up.
 */
enum class WebRtcSignalType {
    /** SDP offer from the initiating peer. */
    OFFER,

    /** SDP answer from the responding peer. */
    ANSWER,

    /** A trickled ICE candidate. */
    ICE_CANDIDATE,
}

/**
 * A single WebRTC signalling message — the SDP offer/answer or an ICE candidate two peers must
 * exchange before a direct data channel can open.
 *
 * Carried by a [WebRtcSignaling] channel (e.g. over the AetherNet QUIC/HTTP relay, the radio mesh,
 * or an SMS ignition link) — never a central signalling server.
 *
 * @property fromUhid       UHID of the node that produced this signal.
 * @property toUhid         UHID of the node this signal is addressed to.
 * @property type           What this signal carries.
 * @property sdp            The SDP text — set for [WebRtcSignalType.OFFER] / [WebRtcSignalType.ANSWER].
 * @property candidate      The ICE candidate string — set for [WebRtcSignalType.ICE_CANDIDATE].
 * @property sdpMid         The SDP mid for the ICE candidate.
 * @property sdpMLineIndex  The SDP m-line index for the ICE candidate (0 for the single data section).
 */
data class WebRtcSignal(
    val fromUhid: String,
    val toUhid: String,
    val type: WebRtcSignalType,
    val sdp: String? = null,
    val candidate: String? = null,
    val sdpMid: String? = null,
    val sdpMLineIndex: Int = 0,
)

/**
 * Carries WebRTC SDP/ICE signalling between two peers by UHID, so a direct data channel can be
 * negotiated without a central signalling server.
 *
 * Any already-reachable channel can back this — the AetherNet QUIC/HTTP relay, the radio mesh, or
 * (for cold first contact between distant peers) an SMS ignition link. The implementation frames
 * signals so the underlying channel only ever forwards opaque bytes.
 */
interface WebRtcSignaling {
    /**
     * Delivers a signalling message to its addressee.
     *
     * @return True if the signal was handed to the underlying channel; false otherwise.
     */
    fun sendSignal(peerUhid: String, signal: WebRtcSignal): Boolean

    /**
     * Registers the handler invoked for signals addressed to the local node.
     * Replaces any previously registered handler.
     */
    fun onSignal(handler: (WebRtcSignal) -> Unit)
}

/**
 * In-process [WebRtcSignaling] bus that routes signals between endpoints by UHID.
 *
 * The reference signalling implementation: it needs no network and no server, so it backs
 * same-process scenarios (multi-node simulations, a single device holding several identities) and
 * the test suite. Production cross-device signalling rides a real transport instead.
 *
 * Each endpoint delivers inbound signals on its own single-consumer coroutine, so signals arrive in
 * send order and never re-enter the sender's call stack — matching the ordered, reliable delivery a
 * real signalling channel provides.
 */
class InMemorySignalingBus : Closeable {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val endpoints = ConcurrentHashMap<String, Endpoint>()

    /** Returns the signalling endpoint for [uhid], creating it once. */
    fun endpoint(uhid: String): WebRtcSignaling =
        endpoints.computeIfAbsent(uhid) { Endpoint() }

    private fun route(signal: WebRtcSignal): Boolean {
        val target = endpoints[signal.toUhid] ?: return false
        return target.deliver(signal)
    }

    override fun close() {
        for (endpoint in endpoints.values) {
            endpoint.close()
        }
        endpoints.clear()
        scope.cancel()
    }

    private inner class Endpoint : WebRtcSignaling {
        // Unbounded, single-consumer queue: ordered, lossless in-process delivery.
        private val inbox = Channel<WebRtcSignal>(Channel.UNLIMITED)

        @Volatile
        private var handler: ((WebRtcSignal) -> Unit)? = null

        init {
            scope.launch {
                for (signal in inbox) {
                    try {
                        handler?.invoke(signal)
                    } catch (_: Throwable) {
                        // a misbehaving handler must not stop the queue
                    }
                }
            }
        }

        override fun sendSignal(peerUhid: String, signal: WebRtcSignal): Boolean = route(signal)

        override fun onSignal(handler: (WebRtcSignal) -> Unit) {
            this.handler = handler
        }

        fun deliver(signal: WebRtcSignal): Boolean =
            scope.isActive && inbox.trySend(signal).isSuccess

        fun close() {
            inbox.close()
        }
    }
}
