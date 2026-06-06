// SPDX-License-Identifier: MIT
package aethernet.bench

import aethernet.models.RouteEntry
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketSerializer
import aethernet.protocol.PacketType
import aethernet.routing.InMemoryRouteStore
import aethernet.security.SignalProtocol
import kotlinx.benchmark.Benchmark
import kotlinx.benchmark.BenchmarkMode
import kotlinx.benchmark.Mode
import kotlinx.benchmark.OutputTimeUnit
import kotlinx.benchmark.Scope
import kotlinx.benchmark.Setup
import kotlinx.benchmark.State
import kotlinx.coroutines.runBlocking
import org.bouncycastle.crypto.agreement.X25519Agreement
import org.bouncycastle.crypto.generators.X25519KeyPairGenerator
import org.bouncycastle.crypto.params.X25519KeyGenerationParameters
import org.bouncycastle.crypto.params.X25519PrivateKeyParameters
import org.bouncycastle.crypto.params.X25519PublicKeyParameters
import java.security.SecureRandom
import java.time.Instant
import java.util.UUID
import java.util.concurrent.TimeUnit
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * kotlinx-benchmark harness for the Kotlin aether-protocol hot paths.
 *
 * Mirrors the C# `AetherNet.Benchmarks` suite, the Go `go/bench` harness,
 * the Python `python/benchmarks/test_benchmark.py`, the C
 * `c/benchmarks/` runner, and the TypeScript `typescript/benchmarks/bench.ts`
 * — same eleven hot paths so a regression in any language shows up as a
 * delta against the committed baseline.
 *
 * Eleven cases:
 *
 *   - x25519Agree                 — one ECDH agreement (X3DH inner loop).
 *   - hkdfSha256_64Bytes          — KDF_RK (Signal §5.2) per ratchet step.
 *   - x3dhEstablish               — full pre-key bundle process; 4 X25519 + HKDF.
 *   - signalEncrypt               — steady-state Encrypt; HMAC chain + AES-GCM.
 *   - signalDecrypt               — steady-state Decrypt.
 *   - packetSerialize             — wire serialiser, 50-byte payload.
 *   - packetSerialize_large       — wire serialiser, 10KB payload.
 *   - packetDeserialize           — wire deserialiser.
 *   - packetRoundTrip             — single-number regression detector.
 *   - routeStore_lookup           — cached-route hot path.
 *   - routeStore_save             — install a new route entry.
 *
 * Run from `kotlin/`:
 *
 *     ./gradlew benchmark
 *
 * The kotlinx-benchmark plugin prints a JMH-format summary table to
 * stdout, ready to paste into BENCHMARKS.md or a CI baseline diff
 * comment. Configured via `benchmark { configurations { named("main") {} } }`
 * in `build.gradle.kts`: warmups=3, iterations=5, iterationTime=500ms,
 * mode=avgt (average time per op), output=microseconds.
 *
 * The harness only calls exported APIs from the protocol package and
 * the BouncyCastle primitives the production code uses, so the numbers
 * are directly comparable to the other-language runs.
 */
@State(Scope.Benchmark)
@BenchmarkMode(Mode.AverageTime)
@OutputTimeUnit(TimeUnit.MICROSECONDS)
open class Benchmarks {

    // ─── Shared fixtures ───────────────────────────────────────────────────

    private val rng = SecureRandom()

    // x25519Agree fixture
    private lateinit var x25519MyPriv: ByteArray
    private lateinit var x25519PeerPub: ByteArray

    // hkdf fixture
    private lateinit var hkdfIkm: ByteArray
    private lateinit var hkdfSalt: ByteArray
    private lateinit var hkdfInfo: ByteArray

    // SignalProtocol fixtures (warmed: X3DH already done, first PreKey
    // message already through, so encrypt/decrypt benches measure the
    // steady-state chain step rather than X3DH cost).
    private lateinit var aliceWarmed: SignalProtocol
    private lateinit var bobWarmed: SignalProtocol

    // Per-iteration state for signalDecrypt — each iter consumes a
    // freshly-encrypted payload (the receive ratchet advances, so
    // re-decrypting the same bytes is invalid). State held in a
    // mutable cell that the bench rotates per call.
    private lateinit var aliceForDecrypt: SignalProtocol
    private lateinit var bobForDecrypt: SignalProtocol

    // Wire-format fixtures
    private lateinit var smallPacket: MeshPacket
    private lateinit var largePacket: MeshPacket
    private lateinit var smallWire: ByteArray

    // Route store fixtures
    private lateinit var routeStoreForLookup: InMemoryRouteStore
    private lateinit var routeStoreForSave: InMemoryRouteStore
    private lateinit var routeSaveExpires: Instant

    @Setup
    fun setUp() {
        // x25519
        val (priv, _) = generateX25519KeyPair()
        val (_, peerPub) = generateX25519KeyPair()
        x25519MyPriv = priv
        x25519PeerPub = peerPub

        // hkdf
        hkdfIkm = ByteArray(32).also { rng.nextBytes(it) }
        hkdfSalt = ByteArray(32).also { rng.nextBytes(it) }
        hkdfInfo = "aether-ratchet-rk-v1".toByteArray(Charsets.UTF_8)

        // Warmed Signal pair for encrypt/decrypt steady-state benches.
        aliceWarmed = SignalProtocol()
        bobWarmed = SignalProtocol()
        val bobBundle = bobWarmed.generatePreKeyBundle("bob")
        aliceWarmed.generatePreKeyBundle("alice")
        aliceWarmed.processPreKeyBundle(bobBundle)
        // Drive the first PreKey message through.
        val first = aliceWarmed.encrypt("bob", "warmup".toByteArray())
        bobWarmed.decrypt("alice", first)

        aliceForDecrypt = SignalProtocol()
        bobForDecrypt = SignalProtocol()
        val bb2 = bobForDecrypt.generatePreKeyBundle("bob")
        aliceForDecrypt.generatePreKeyBundle("alice")
        aliceForDecrypt.processPreKeyBundle(bb2)
        val firstD = aliceForDecrypt.encrypt("bob", "warmup".toByteArray())
        bobForDecrypt.decrypt("alice", firstD)

        // Wire packets
        smallPacket = makePacket(50)
        largePacket = makePacket(10_240)
        smallWire = PacketSerializer.serialize(smallPacket)

        // Route stores
        routeStoreForLookup = InMemoryRouteStore()
        runBlocking {
            routeStoreForLookup.save(
                RouteEntry(
                    destinationUhid = "bob-uhid",
                    nextHopUhid = "relay-uhid",
                    hopCount = 2,
                    qualityScore = 90,
                    expiresAt = Instant.now().plusSeconds(3600),
                ),
            )
        }
        routeStoreForSave = InMemoryRouteStore()
        routeSaveExpires = Instant.now().plusSeconds(3600)
    }

    // ─── Crypto primitives ─────────────────────────────────────────────────

    /**
     * One ECDH agreement, the inner-loop primitive of X3DH (4× per session
     * establishment) and DH-ratchet (2× per ratchet step).
     */
    @Benchmark
    fun x25519Agree(): ByteArray = x25519AgreePrim(x25519MyPriv, x25519PeerPub)

    /**
     * KDF_RK per Signal §5.2: 32-byte new root + 32-byte new chain = 64
     * bytes out, called once per DH-ratchet step.
     */
    @Benchmark
    fun hkdfSha256_64Bytes(): ByteArray = hkdfSha256(hkdfIkm, hkdfSalt, hkdfInfo, 64)

    // ─── Signal protocol ───────────────────────────────────────────────────

    /**
     * Full pre-key bundle process: 4 X25519 + HKDF root derivation.
     * One-shot per peer. Each iteration uses a fresh initiator so the
     * session table doesn't grow unbounded.
     */
    @Benchmark
    fun x3dhEstablish() {
        val bundle = bobWarmed.generatePreKeyBundle("bob")
        val alice = SignalProtocol()
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bundle)
    }

    /**
     * Steady-state Encrypt: 1 HMAC chain step + AES-GCM. Sender ratchet
     * pubkey is unchanged across calls (no responder reply has triggered a
     * DH-ratchet step) so the chain advances by one each iter.
     */
    @Benchmark
    fun signalEncrypt() {
        aliceWarmed.encrypt("bob", PLAINTEXT_SMALL)
    }

    /**
     * Steady-state Decrypt. Each iter consumes a freshly-encrypted payload
     * (the receive ratchet advances, so re-decrypting the same bytes is
     * invalid). The encrypt step is part of the measured time — same as
     * the C# / TypeScript benches; they all include the encrypt because
     * isolating decrypt requires an out-of-band ratchet rewind that none
     * of them expose.
     */
    @Benchmark
    fun signalDecrypt() {
        val payload = aliceForDecrypt.encrypt("bob", PLAINTEXT_DECRYPT)
        bobForDecrypt.decrypt("alice", payload)
    }

    // ─── Wire-format serializer ────────────────────────────────────────────

    /**
     * Serialize on a representative 50-byte Data packet — every packet on
     * the mesh runs through this on send.
     */
    @Benchmark
    fun packetSerialize(): ByteArray = PacketSerializer.serialize(smallPacket)

    /**
     * Serialize on a 10KB payload (typical chunked-data or video-frame
     * packet).
     */
    @Benchmark
    fun packetSerialize_large(): ByteArray = PacketSerializer.serialize(largePacket)

    /**
     * Deserialize on a representative wire envelope. Every hop runs this
     * on receive; a regression multiplies across every router.
     */
    @Benchmark
    fun packetDeserialize(): MeshPacket = PacketSerializer.deserialize(smallWire)

    /**
     * Combined Serialize + Deserialize. Single-number regression detector
     * that catches changes in either side.
     */
    @Benchmark
    fun packetRoundTrip(): MeshPacket {
        val wire = PacketSerializer.serialize(smallPacket)
        val got = PacketSerializer.deserialize(wire)
        // Defeat dead-store elimination — touch a field so JIT can't optimise away.
        check(got.sourceUhid.isNotEmpty()) { "unexpected empty sourceUhid" }
        return got
    }

    // ─── Routing ───────────────────────────────────────────────────────────

    /**
     * Cached-route hot path: the steady state for every outbound packet
     * that already has a route.
     */
    @Benchmark
    fun routeStore_lookup(): RouteEntry? = runBlocking {
        routeStoreForLookup.get("bob-uhid")
    }

    /**
     * Install a new route entry — what happens on every successful RREP
     * arrival. The destination UHID is fixed across iterations so the map
     * stays at size 1 and the bench measures the put-on-existing-key path.
     */
    @Benchmark
    fun routeStore_save() {
        runBlocking {
            routeStoreForSave.save(
                RouteEntry(
                    destinationUhid = "dest",
                    nextHopUhid = "hop",
                    hopCount = 1,
                    qualityScore = 100,
                    expiresAt = routeSaveExpires,
                ),
            )
        }
    }

    // ─── Local helpers ─────────────────────────────────────────────────────

    private fun makePacket(payloadSize: Int): MeshPacket = MeshPacket(
        id = UUID.randomUUID(),
        type = PacketType.Data,
        sourceUhid = "alice-uhid-0001",
        destinationUhid = "bob-uhid-0002",
        ttl = 7,
        priority = 1,
        payload = ByteArray(payloadSize).also { rng.nextBytes(it) },
        packetNonce = ByteArray(8).also { rng.nextBytes(it) },
        signature = ByteArray(64).also { rng.nextBytes(it) },
        timestampMs = System.currentTimeMillis(),
        protocolVersion = 2,
    )

    /**
     * Re-implementation of the X25519 agreement helper used by the
     * production [SignalProtocol] — same BouncyCastle primitives, no
     * reach into private internals.
     */
    private fun generateX25519KeyPair(): Pair<ByteArray, ByteArray> {
        val gen = X25519KeyPairGenerator()
        gen.init(X25519KeyGenerationParameters(rng))
        val kp = gen.generateKeyPair()
        val priv = kp.private as X25519PrivateKeyParameters
        val pub = kp.public as X25519PublicKeyParameters
        return priv.encoded to pub.encoded
    }

    private fun x25519AgreePrim(localPriv: ByteArray, remotePub: ByteArray): ByteArray {
        val priv = X25519PrivateKeyParameters(localPriv, 0)
        val pub = X25519PublicKeyParameters(remotePub, 0)
        val agreement = X25519Agreement()
        agreement.init(priv)
        val shared = ByteArray(agreement.agreementSize)
        agreement.calculateAgreement(pub, shared, 0)
        return shared
    }

    /**
     * RFC 5869 HKDF-SHA256 with explicit salt + info. 64-byte output via
     * two HMAC blocks (T1 || T2).
     */
    private fun hkdfSha256(ikm: ByteArray, salt: ByteArray, info: ByteArray, length: Int): ByteArray {
        val extract = Mac.getInstance("HmacSHA256")
        extract.init(SecretKeySpec(salt, "HmacSHA256"))
        val prk = extract.doFinal(ikm)

        val out = ByteArray(length)
        var written = 0
        var prev = ByteArray(0)
        var counter: Byte = 1
        while (written < length) {
            val expand = Mac.getInstance("HmacSHA256")
            expand.init(SecretKeySpec(prk, "HmacSHA256"))
            expand.update(prev)
            expand.update(info)
            expand.update(counter)
            prev = expand.doFinal()
            val take = minOf(prev.size, length - written)
            System.arraycopy(prev, 0, out, written, take)
            written += take
            counter = (counter + 1).toByte()
        }
        return out
    }

    companion object {
        private val PLAINTEXT_SMALL = "hello, mesh".toByteArray(Charsets.UTF_8)
        private val PLAINTEXT_DECRYPT = ByteArray(256).also { java.security.SecureRandom().nextBytes(it) }
    }
}
