// SPDX-License-Identifier: MIT

package aethernet.bittorrent

import java.io.ByteArrayOutputStream

/**
 * DHT primitives (BEP-5): the 160-bit Kademlia node id with XOR distance, compact
 * node (26-byte) and peer (6-byte) records, and a k-bucket routing table. The Kotlin
 * port of `go/bittorrent/dht.go`.
 */

/** A 160-bit Kademlia node identifier (BEP-5). */
class NodeID(val bytes: ByteArray) {
    init {
        require(bytes.size == 20) { "NodeID must be 20 bytes, got ${bytes.size}" }
    }

    /** The XOR distance between two node ids. */
    fun distanceTo(other: NodeID): NodeID {
        val d = ByteArray(20)
        for (i in 0 until 20) d[i] = (bytes[i].toInt() xor other.bytes[i].toInt()).toByte()
        return NodeID(d)
    }

    /** Orders node ids / distances by unsigned big-endian value. */
    fun compareTo(other: NodeID): Int = compareBytesUnsigned(bytes, other.bytes)

    /** Counts the leading zero bits (0..160). */
    fun leadingZeros(): Int {
        for (i in 0 until 20) {
            val by = bytes[i].toInt() and 0xff
            if (by != 0) return i * 8 + Integer.numberOfLeadingZeros(by) - 24
        }
        return 160
    }

    override fun equals(other: Any?): Boolean =
        other is NodeID && bytes.contentEquals(other.bytes)

    override fun hashCode(): Int = bytes.contentHashCode()
}

/** A routable DHT node: its id and IPv4 endpoint (the 4 raw address bytes + port). */
class DhtContact(val id: NodeID, val ip: ByteArray, val port: Int) {
    init {
        require(ip.size == 4) { "DHT contact IP must be 4 bytes" }
    }
}

/** An IPv4 peer endpoint (compact peer, BEP-23): the 4 raw address bytes + port. */
class PeerAddr(val ip: ByteArray, val port: Int) {
    init {
        require(ip.size == 4) { "peer IP must be 4 bytes" }
    }

    companion object {
        /** Builds a peer from a dotted-quad IPv4 string and a port. */
        fun fromString(ip: String, port: Int): PeerAddr {
            val parts = ip.split(".")
            require(parts.size == 4) { "invalid IPv4 address: $ip" }
            val bytes = ByteArray(4)
            for (i in 0 until 4) {
                val octet = parts[i].toInt()
                require(octet in 0..255) { "invalid IPv4 octet in $ip" }
                bytes[i] = octet.toByte()
            }
            return PeerAddr(bytes, port)
        }
    }
}

/** Serializes contacts as 26-byte records (20 id + 4 IPv4 + 2 port BE). */
fun encodeCompactNodes(contacts: List<DhtContact>): ByteArray {
    val out = ByteArrayOutputStream(contacts.size * 26)
    for (c in contacts) {
        out.write(c.id.bytes)
        out.write(c.ip)
        writeU16BE(out, c.port)
    }
    return out.toByteArray()
}

/** Parses 26-byte compact node records. */
fun decodeCompactNodes(data: ByteArray): List<DhtContact> {
    require(data.size % 26 == 0) { "compact nodes length ${data.size} is not a multiple of 26" }
    val out = ArrayList<DhtContact>(data.size / 26)
    var i = 0
    while (i < data.size) {
        val id = NodeID(data.copyOfRange(i, i + 20))
        val ip = data.copyOfRange(i + 20, i + 24)
        val port = u16BE(data, i + 24)
        out.add(DhtContact(id, ip, port))
        i += 26
    }
    return out
}

/** Serializes peers as 6-byte records (4 IPv4 + 2 port BE). */
fun encodeCompactPeers(peers: List<PeerAddr>): ByteArray {
    val out = ByteArrayOutputStream(peers.size * 6)
    for (p in peers) {
        out.write(p.ip)
        writeU16BE(out, p.port)
    }
    return out.toByteArray()
}

/** Parses 6-byte compact peer records. */
fun decodeCompactPeers(data: ByteArray): List<PeerAddr> {
    require(data.size % 6 == 0) { "compact peers length ${data.size} is not a multiple of 6" }
    val out = ArrayList<PeerAddr>(data.size / 6)
    var i = 0
    while (i < data.size) {
        val ip = data.copyOfRange(i, i + 4)
        val port = u16BE(data, i + 4)
        out.add(PeerAddr(ip, port))
        i += 6
    }
    return out
}

/** The Kademlia bucket size. */
const val DHT_K = 8

/**
 * A Kademlia routing table of 160 k-buckets indexed by shared prefix length.
 * The Kotlin port of the Go `RoutingTable`.
 */
class RoutingTable(private val self: NodeID) {
    private val buckets = Array(160) { ArrayList<DhtContact>() }

    private fun bucketIndex(id: NodeID): Int {
        val lz = self.distanceTo(id).leadingZeros()
        return if (lz >= 160) 159 else lz
    }

    /** Inserts or refreshes a contact; returns false if it is us or the bucket is full. */
    fun tryAdd(c: DhtContact): Boolean {
        if (c.id == self) return false
        val b = buckets[bucketIndex(c.id)]
        for (i in b.indices) {
            if (b[i].id == c.id) {
                b[i] = c
                return true
            }
        }
        if (b.size < DHT_K) {
            b.add(c)
            return true
        }
        return false
    }

    /** Returns up to [count] contacts nearest to [target] by XOR distance. */
    fun closestTo(target: NodeID, count: Int): List<DhtContact> {
        val all = ArrayList<DhtContact>()
        for (b in buckets) all.addAll(b)
        all.sortWith(Comparator { i, j ->
            i.id.distanceTo(target).compareTo(j.id.distanceTo(target))
        })
        return if (count < all.size) all.subList(0, count).toList() else all
    }

    /** The total number of contacts. */
    fun count(): Int = buckets.sumOf { it.size }
}
