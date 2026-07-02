// SPDX-License-Identifier: MIT

package aethernet.transport.webrtc

import aethernet.transport.PerTransportMetrics
import aethernet.transport.TransportService
import dev.onvoid.webrtc.CreateSessionDescriptionObserver
import dev.onvoid.webrtc.PeerConnectionFactory
import dev.onvoid.webrtc.PeerConnectionObserver
import dev.onvoid.webrtc.RTCAnswerOptions
import dev.onvoid.webrtc.RTCConfiguration
import dev.onvoid.webrtc.RTCDataChannel
import dev.onvoid.webrtc.RTCDataChannelBuffer
import dev.onvoid.webrtc.RTCDataChannelInit
import dev.onvoid.webrtc.RTCDataChannelObserver
import dev.onvoid.webrtc.RTCDataChannelState
import dev.onvoid.webrtc.RTCIceCandidate
import dev.onvoid.webrtc.RTCIceServer
import dev.onvoid.webrtc.RTCOfferOptions
import dev.onvoid.webrtc.RTCPeerConnection
import dev.onvoid.webrtc.RTCPeerConnectionState
import dev.onvoid.webrtc.RTCRtpReceiver
import dev.onvoid.webrtc.RTCRtpTransceiver
import dev.onvoid.webrtc.RTCSdpType
import dev.onvoid.webrtc.RTCSessionDescription
import dev.onvoid.webrtc.SetSessionDescriptionObserver
import dev.onvoid.webrtc.media.MediaStream
import dev.onvoid.webrtc.media.audio.AudioDeviceModule
import dev.onvoid.webrtc.media.audio.AudioLayer
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.withTimeoutOrNull
import org.slf4j.LoggerFactory
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap

/**
 * Direct peer-to-peer transport over a WebRTC data channel
 * ([dev.onvoid.webrtc], a JVM wrapper around Google's native libwebrtc).
 *
 * NAT traversal is handled by ICE/STUN, with WebRTC's own TURN as last resort. The initial SDP/ICE
 * handshake is carried by an injected [WebRtcSignaling] channel (e.g. the AetherNet relay), so no
 * central signalling server is required. Implements [TransportService] so the transport ranker can
 * place it between the radio mesh (cheap, proximity) and the QUIC/HTTP relay (last resort) — a
 * direct internet path is used when one can be negotiated, otherwise the relay carries the traffic.
 *
 * This is the Kotlin/JVM sibling of the SIPSorcery-backed C# transport and the pion-backed Go
 * transport, giving the JVM core its first real, internet-capable transport (the others are
 * in-process simulations).
 *
 * Received bytes are surfaced on [dataReceived] as `(senderUhid, data)` pairs — the same
 * [kotlinx.coroutines.flow.Flow] idiom `InProcessTransport` uses.
 *
 * @param localUhid  This node's UHID.
 * @param signaling  The signalling channel carrying SDP/ICE to and from peers.
 * @param iceServers `null` uses the serverless default of NO ICE servers (host-candidate-only ICE)
 *                   — it never contacts a STUN/TURN server, and links form on the same LAN or when
 *                   a peer has a public address. For NAT traversal without a server, route through
 *                   the circuit-relay-v2 transport (peers relay for peers). Pass an explicit list to
 *                   opt into STUN/TURN; an explicit empty list keeps host-candidate-only ICE.
 */
class WebRtcTransport(
    private val localUhid: String,
    private val signaling: WebRtcSignaling,
    iceServers: List<RTCIceServer>? = null,
) : TransportService {

    init {
        require(localUhid.isNotEmpty()) { "localUhid required" }
    }

    private val log = LoggerFactory.getLogger(WebRtcTransport::class.java)

    private val iceServers: List<RTCIceServer> = iceServers ?: defaultIceServers()

    // The platform's WebRTC entry point. Native + heavyweight, so one per transport, lazily built
    // and disposed on close.
    //
    // Built with a DUMMY AudioDeviceModule. This is a data-channel-only transport that never
    // carries audio, but the default `PeerConnectionFactory()` makes libwebrtc bring up real audio
    // hardware and hard-abort ("Failed to initialize the ADM") on any host without a usable sound
    // card — a headless Circle OS gateway/relay node, a server, or a CI runner. `kDummyAudio` needs
    // no device, so the data path works everywhere. The ADM is held so it can be disposed on close.
    private var audioModule: AudioDeviceModule? = null
    private val factory: PeerConnectionFactory by lazy {
        val adm = AudioDeviceModule(AudioLayer.kDummyAudio)
        audioModule = adm
        PeerConnectionFactory(adm)
    }

    private val peers = ConcurrentHashMap<String, PeerLink>()

    @Volatile
    private var closed = false

    private val mutableDataReceived = MutableSharedFlow<Pair<String, ByteArray>>(
        replay = 0,
        extraBufferCapacity = 256,
    )

    init {
        signaling.onSignal(::onSignal)
    }

    // ── TransportService ──────────────────────────────────────────────────────

    override val name: String = "WebRTC P2P"
    override val isAvailable: Boolean get() = !closed
    override val maxBandwidthBps: Long = 100_000_000L   // direct link — bounded by the local NIC
    override val maxRangeMeters: Int = 0                 // internet — unbounded
    override val powerCostRelative: Int = 5              // dearer than local radio on the 1-10 scale
    override val maxConcurrentPeers: Int = 256

    /** Non-null metrics instance; always present for this transport. */
    override val metrics: PerTransportMetrics = PerTransportMetrics()

    override val dataReceived: Flow<Pair<String, ByteArray>> = mutableDataReceived.asSharedFlow()

    override suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean {
        if (closed || peerUhid.isEmpty()) return false

        val link = getOrCreateLink(peerUhid, asInitiator = true) ?: return false
        val startMs = System.currentTimeMillis()
        val ok = link.send(data)
        val rttMs = (System.currentTimeMillis() - startMs).coerceAtLeast(1L)
        metrics.recordSample(rttMs, success = ok, bytesTransferred = if (ok) data.size.toLong() else 0L)
        return ok
    }

    override suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean =
        sendAsync(peerUhid, data)

    override fun isConnected(peerUhid: String): Boolean =
        peers[peerUhid]?.isOpen == true

    override fun close() {
        if (closed) return
        closed = true
        for (link in peers.values) {
            link.close()
        }
        peers.clear()
        try {
            factory.dispose()
        } catch (e: Throwable) {
            log.warn("[WebRTC] factory dispose failed", e)
        }
        try {
            audioModule?.dispose()
        } catch (e: Throwable) {
            log.warn("[WebRTC] audio module dispose failed", e)
        }
    }

    // ── Signalling inbound ──────────────────────────────────────────────────────

    private fun onSignal(signal: WebRtcSignal) {
        if (closed || signal.toUhid != localUhid) return
        try {
            when (signal.type) {
                WebRtcSignalType.OFFER -> {
                    val link = getOrCreateLink(signal.fromUhid, asInitiator = false)
                    val sdp = signal.sdp
                    if (link != null && sdp != null) link.acceptOffer(sdp)
                }

                WebRtcSignalType.ANSWER -> {
                    val sdp = signal.sdp
                    if (sdp != null) peers[signal.fromUhid]?.acceptAnswer(sdp)
                }

                WebRtcSignalType.ICE_CANDIDATE ->
                    peers[signal.fromUhid]?.addRemoteCandidate(signal)
            }
        } catch (e: Throwable) {
            log.warn("[WebRTC] signal handling failed from {}", signal.fromUhid, e)
        }
    }

    private fun getOrCreateLink(peerUhid: String, asInitiator: Boolean): PeerLink? {
        if (closed) return null

        val existing = peers[peerUhid]
        if (existing != null && !existing.isClosed) return existing

        val link = PeerLink(peerUhid)
        val winner = peers.putIfAbsent(peerUhid, link)
        if (winner != null) {
            // Lost a race — discard ours, use the winner.
            link.close()
            return winner
        }

        link.start(asInitiator)
        return link
    }

    private fun onPeerData(peerUhid: String, data: ByteArray) {
        // Native callback thread — emit without suspending. extraBufferCapacity makes tryEmit
        // succeed under normal load; if a slow/absent collector ever fills the buffer the oldest
        // pending datum is dropped (best-effort delivery, like the reference transports).
        if (!mutableDataReceived.tryEmit(peerUhid to data)) {
            log.warn("[WebRTC] dataReceived buffer full; dropped {} bytes from {}", data.size, peerUhid)
        }
    }

    // ── One WebRTC connection to a single peer ─────────────────────────────────

    private inner class PeerLink(private val peerUhid: String) : PeerConnectionObserver {

        private val pc: RTCPeerConnection
        private val open = CompletableDeferred<Boolean>()

        @Volatile
        private var channel: RTCDataChannel? = null

        @Volatile
        private var terminated = false

        val isOpen: Boolean get() = channel?.state == RTCDataChannelState.OPEN
        val isClosed: Boolean get() = terminated

        init {
            val config = RTCConfiguration().apply {
                this.iceServers.addAll(this@WebRtcTransport.iceServers)
            }
            pc = factory.createPeerConnection(config, this)
        }

        /** Begins the handshake. The initiator creates the data channel + sends the offer. */
        fun start(asInitiator: Boolean) {
            if (!asInitiator) return // responder waits for the inbound offer (see acceptOffer)

            val dc = pc.createDataChannel(DATA_CHANNEL_LABEL, RTCDataChannelInit())
            attachChannel(dc)

            pc.createOffer(RTCOfferOptions(), object : CreateSessionDescriptionObserver {
                override fun onSuccess(description: RTCSessionDescription) {
                    pc.setLocalDescription(description, object : SetSessionDescriptionObserver {
                        override fun onSuccess() {
                            signaling.sendSignal(
                                peerUhid,
                                WebRtcSignal(
                                    fromUhid = localUhid,
                                    toUhid = peerUhid,
                                    type = WebRtcSignalType.OFFER,
                                    sdp = description.sdp,
                                ),
                            )
                        }

                        override fun onFailure(error: String) {
                            log.warn("[WebRTC] setLocalDescription(offer) failed for {}: {}", peerUhid, error)
                        }
                    })
                }

                override fun onFailure(error: String) {
                    log.warn("[WebRTC] createOffer failed for {}: {}", peerUhid, error)
                }
            })
        }

        fun acceptOffer(sdp: String) {
            val remote = RTCSessionDescription(RTCSdpType.OFFER, sdp)
            pc.setRemoteDescription(remote, object : SetSessionDescriptionObserver {
                override fun onSuccess() {
                    pc.createAnswer(RTCAnswerOptions(), object : CreateSessionDescriptionObserver {
                        override fun onSuccess(description: RTCSessionDescription) {
                            pc.setLocalDescription(description, object : SetSessionDescriptionObserver {
                                override fun onSuccess() {
                                    signaling.sendSignal(
                                        peerUhid,
                                        WebRtcSignal(
                                            fromUhid = localUhid,
                                            toUhid = peerUhid,
                                            type = WebRtcSignalType.ANSWER,
                                            sdp = description.sdp,
                                        ),
                                    )
                                }

                                override fun onFailure(error: String) {
                                    log.warn("[WebRTC] setLocalDescription(answer) failed for {}: {}", peerUhid, error)
                                }
                            })
                        }

                        override fun onFailure(error: String) {
                            log.warn("[WebRTC] createAnswer failed for {}: {}", peerUhid, error)
                        }
                    })
                }

                override fun onFailure(error: String) {
                    log.warn("[WebRTC] setRemoteDescription(offer) failed for {}: {}", peerUhid, error)
                }
            })
        }

        fun acceptAnswer(sdp: String) {
            val remote = RTCSessionDescription(RTCSdpType.ANSWER, sdp)
            pc.setRemoteDescription(remote, object : SetSessionDescriptionObserver {
                override fun onSuccess() = Unit
                override fun onFailure(error: String) {
                    log.warn("[WebRTC] setRemoteDescription(answer) failed for {}: {}", peerUhid, error)
                }
            })
        }

        fun addRemoteCandidate(signal: WebRtcSignal) {
            val candidate = signal.candidate ?: return
            if (candidate.isEmpty()) return
            pc.addIceCandidate(RTCIceCandidate(signal.sdpMid, signal.sdpMLineIndex, candidate))
        }

        private fun attachChannel(dc: RTCDataChannel) {
            channel = dc
            dc.registerObserver(object : RTCDataChannelObserver {
                override fun onBufferedAmountChange(previousAmount: Long) = Unit

                override fun onStateChange() {
                    when (dc.state) {
                        RTCDataChannelState.OPEN -> open.complete(true)
                        RTCDataChannelState.CLOSED -> markClosed()
                        else -> Unit
                    }
                }

                override fun onMessage(buffer: RTCDataChannelBuffer) {
                    val data = buffer.data
                    val bytes = ByteArray(data.remaining())
                    data.get(bytes)
                    onPeerData(peerUhid, bytes)
                }
            })
            // The channel may already be OPEN by the time the observer is registered.
            if (dc.state == RTCDataChannelState.OPEN) open.complete(true)
        }

        // PeerConnectionObserver

        override fun onIceCandidate(candidate: RTCIceCandidate) {
            signaling.sendSignal(
                peerUhid,
                WebRtcSignal(
                    fromUhid = localUhid,
                    toUhid = peerUhid,
                    type = WebRtcSignalType.ICE_CANDIDATE,
                    candidate = candidate.sdp,
                    sdpMid = candidate.sdpMid,
                    sdpMLineIndex = candidate.sdpMLineIndex,
                ),
            )
        }

        override fun onDataChannel(dataChannel: RTCDataChannel) {
            // Responder receives the channel the initiator opened.
            attachChannel(dataChannel)
        }

        override fun onConnectionChange(state: RTCPeerConnectionState) {
            when (state) {
                RTCPeerConnectionState.FAILED,
                RTCPeerConnectionState.DISCONNECTED,
                RTCPeerConnectionState.CLOSED -> markClosed()
                else -> Unit
            }
        }

        override fun onRenegotiationNeeded() = Unit

        override fun onAddTrack(receiver: RTCRtpReceiver, mediaStreams: Array<out MediaStream>) = Unit

        override fun onRemoveTrack(receiver: RTCRtpReceiver) = Unit

        override fun onTrack(transceiver: RTCRtpTransceiver) = Unit

        // Link lifecycle

        private fun markClosed() {
            if (terminated) return
            terminated = true
            open.complete(false)
        }

        suspend fun waitOpen(timeoutMs: Long): Boolean {
            if (isOpen) return true
            if (terminated) return false
            return withTimeoutOrNull(timeoutMs) { open.await() } ?: false
        }

        suspend fun send(data: ByteArray): Boolean {
            if (!waitOpen(CONNECT_TIMEOUT_MS)) return false
            val dc = channel ?: return false
            return try {
                dc.send(RTCDataChannelBuffer(ByteBuffer.wrap(data), true))
                true
            } catch (e: Throwable) {
                log.warn("[WebRTC] send to {} failed", peerUhid, e)
                false
            }
        }

        fun close() {
            markClosed()
            try {
                channel?.close()
            } catch (_: Throwable) { /* best effort */ }
            try {
                channel?.dispose()
            } catch (_: Throwable) { /* best effort */ }
            try {
                pc.close()
            } catch (_: Throwable) { /* best effort */ }
        }
    }

    companion object {
        private const val DATA_CHANNEL_LABEL = "aether"
        private const val CONNECT_TIMEOUT_MS = 20_000L

        /**
         * Serverless default: NO ICE servers, so a node never contacts a STUN/TURN server. Direct
         * links form on the same LAN or when a peer has a public address; for NAT traversal without
         * a server, route through the circuit-relay-v2 transport (peers relay for peers). Callers
         * opt into STUN/TURN by passing an explicit list.
         */
        fun defaultIceServers(): List<RTCIceServer> = emptyList()
    }
}
