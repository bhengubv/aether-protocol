// SPDX-License-Identifier: MIT

package aethernet.profiles

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/**
 * JSON payload for [PacketType.ProfileSync] packets. Wire format: UTF-8 JSON with snake_case keys,
 * field order uhid, display_name, avatar_ref, status_message, updated_at_ms, no whitespace,
 * updated_at_ms a bare integer. Byte-identity is locked by fixtures/profiles/vectors.json. All string
 * fields are always present (empty when unset) — no nulls — so the encoding cannot diverge across
 * languages.
 *
 * **Privacy:** a profile is exchanged *directed* (point-to-point to a specific peer), NOT broadcast to
 * the whole mesh — broadcasting display names to every device in range is exactly the metadata leak the
 * privacy roadmap forbids. A peer you interact with learns your profile; strangers do not.
 *
 * Wire vectors (fixtures/profiles/vectors.json), byte-identical with C#:
 *  - uhid=aether:alice:01, display_name=Alice, avatar_ref=blake3:abc, status_message=available,
 *    updated_at_ms=1700000000000 →
 *    {"uhid":"aether:alice:01","display_name":"Alice","avatar_ref":"blake3:abc","status_message":"available","updated_at_ms":1700000000000}
 *  - uhid=n, display_name="", avatar_ref="", status_message="", updated_at_ms=0 →
 *    {"uhid":"n","display_name":"","avatar_ref":"","status_message":"","updated_at_ms":0}
 */
data class ProfileSyncPayload(
    /** UHID this profile describes (the sender). Self-identifying so a cached profile stays attributable. */
    val uhid: String,
    /** Human-readable display name (empty if unset). */
    val displayName: String = "",
    /** Content-addressed reference to an avatar (e.g. "blake3:…"), empty if none. */
    val avatarRef: String = "",
    /** Free-text status / presence message (empty if unset). */
    val statusMessage: String = "",
    /** Unix timestamp in milliseconds when the profile was last updated by its owner. */
    val updatedAtMs: Long = 0L
) {
    /**
     * Serialize to the canonical UTF-8 JSON wire bytes. Built by hand (no kotlinx.serialization —
     * AOSP Soong forbids it), the same manual string-building approach used by the SOS payload
     * encoder. snake_case keys, field order uhid, display_name, avatar_ref, status_message,
     * updated_at_ms, NO whitespace, all string fields always present, updated_at_ms a bare integer.
     */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"uhid\":\"").append(jsonEscape(uhid)).append("\",")
        sb.append("\"display_name\":\"").append(jsonEscape(displayName)).append("\",")
        sb.append("\"avatar_ref\":\"").append(jsonEscape(avatarRef)).append("\",")
        sb.append("\"status_message\":\"").append(jsonEscape(statusMessage)).append("\",")
        sb.append("\"updated_at_ms\":").append(updatedAtMs)
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    private fun jsonEscape(s: String): String {
        val sb = StringBuilder()
        for (c in s) {
            when (c) {
                '\\' -> sb.append("\\\\")
                '"' -> sb.append("\\\"")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                else -> sb.append(c)
            }
        }
        return sb.toString()
    }
}

/**
 * Exchanges peer profile metadata over [PacketType.ProfileSync]. Profiles are shared *directed* (to a
 * specific peer), not broadcast, for privacy. Received profiles are cached and surfaced via
 * [onProfileUpdated].
 *
 * Mirrors the C# `ProfileService` and the Kotlin [aethernet.heartbeat.HeartbeatService] /
 * [aethernet.sos.SosBroadcastService].
 */
class ProfileService(
    private val sender: MeshSender
) {
    private var local: ProfileSyncPayload = ProfileSyncPayload(uhid = sender.localUhid)
    private val peerProfiles = ConcurrentHashMap<String, ProfileSyncPayload>()

    /**
     * Raised when a peer's profile is received or refreshed. Nullable-lambda event mechanism — matches
     * the other Kotlin services and C# ProfileService.ProfileUpdated.
     */
    var onProfileUpdated: ((ProfileSyncPayload) -> Unit)? = null

    /** Set this node's own profile (stamps [ProfileSyncPayload.updatedAtMs] to now). */
    fun setLocalProfile(displayName: String, avatarRef: String, statusMessage: String) {
        local = ProfileSyncPayload(
            uhid = sender.localUhid,
            displayName = displayName,
            avatarRef = avatarRef,
            statusMessage = statusMessage,
            updatedAtMs = Instant.now().toEpochMilli()
        )
    }

    /** This node's current local profile. */
    fun getLocalProfile(): ProfileSyncPayload = local

    /**
     * Send this node's local profile directly to [peerUhid] via the sender's directed send.
     * Best-effort; returns delivery success.
     */
    suspend fun publishProfileTo(peerUhid: String): Boolean {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }

        val packet = MeshPacket(
            type = PacketType.ProfileSync,
            sourceUhid = sender.localUhid,
            destinationUhid = peerUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = local.toJsonBytes()
        )

        return sender.send(packet, peerUhid)
    }

    /**
     * Process an incoming [PacketType.ProfileSync] packet: cache the sender's profile (keyed by its
     * [ProfileSyncPayload.uhid]) and raise [onProfileUpdated]. Returns false for the wrong packet type,
     * a malformed payload, or our own profile echoed back.
     */
    fun handle(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.ProfileSync) return false

        val json = packet.payload.toString(Charsets.UTF_8)
        val uhid = JsonReader.readString(json, "uhid")
        if (uhid.isNullOrEmpty()) return false

        // Ignore our own profile echoed back.
        if (uhid == sender.localUhid) return false

        val body = ProfileSyncPayload(
            uhid = uhid,
            displayName = JsonReader.readString(json, "display_name") ?: "",
            avatarRef = JsonReader.readString(json, "avatar_ref") ?: "",
            statusMessage = JsonReader.readString(json, "status_message") ?: "",
            updatedAtMs = JsonReader.readLong(json, "updated_at_ms") ?: 0L
        )

        peerProfiles[uhid] = body
        onProfileUpdated?.invoke(body)
        return true
    }

    /** The cached profile for [uhid], or null if none is known. */
    fun getProfile(uhid: String): ProfileSyncPayload? = peerProfiles[uhid]

    /** Snapshot of every peer profile this node has cached. */
    fun getKnownProfiles(): List<ProfileSyncPayload> = peerProfiles.values.toList()
}
