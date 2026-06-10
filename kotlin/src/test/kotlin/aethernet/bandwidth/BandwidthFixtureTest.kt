// SPDX-License-Identifier: MIT

package aethernet.bandwidth

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.double
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.DynamicTest.dynamicTest
import org.junit.jupiter.api.TestFactory
import java.io.File
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNull

/**
 * Drives the Kotlin ABMF implementation through the cross-language numeric
 * conformance corpus at `tests/cross-language/bandwidth-fixtures.json` — the
 * SAME corpus every other AetherNet SDK consumes. This is a direct mirror of
 * the C# reference driver in
 * `AetherNet.Core.Tests/Bandwidth/BandwidthFixtureTests.cs`.
 *
 * If a fixture passes here and fails in another language port, the port is
 * wrong — not the corpus. Integer/string fields are asserted EXACTLY; the
 * floating-point fields (srttMs, rttVarMs, rtPropMs, lossRate) are asserted
 * within [tol] (= the corpus `toleranceAbs`).
 */
class BandwidthFixtureTest {

    private val corpus: JsonObject by lazy { loadCorpus() }
    private val tol: Double by lazy { corpus["toleranceAbs"]!!.jsonPrimitive.double }

    private fun loadCorpus(): JsonObject {
        // CWD is kotlin/ when Gradle runs tests; the corpus is two levels up.
        // Walk up to handle deeper test runners (IDE, classpath jar, etc.).
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "tests/cross-language/bandwidth-fixtures.json")
            if (candidate.exists()) {
                return Json.parseToJsonElement(candidate.readText()).jsonObject
            }
            dir = dir?.parentFile ?: return@repeat
        }
        error(
            "Could not locate tests/cross-language/bandwidth-fixtures.json walking up " +
                "from ${File(".").canonicalPath}"
        )
    }

    private fun parseConfidence(s: String): BandwidthConfidence = when (s) {
        "None" -> BandwidthConfidence.NONE
        "Low" -> BandwidthConfidence.LOW
        "Medium" -> BandwidthConfidence.MEDIUM
        "High" -> BandwidthConfidence.HIGH
        else -> error("bad confidence $s")
    }

    // ── probeAck ──────────────────────────────────────────────────────────────

    @TestFactory
    fun `probeAck rtt and owd exact`(): List<DynamicTest> =
        corpus["probeAck"]!!.jsonArray.map { fixture ->
            val f = fixture.jsonObject
            val name = f.string("name")
            dynamicTest("probeAck: $name") {
                val ack = BandwidthProbeAck(
                    sequence = 1u,
                    senderSendUs = f.long("senderSendUs"),
                    receiverReceiveUs = f.long("receiverReceiveUs"),
                    receiverSendUs = f.long("receiverSendUs"),
                    senderReceiveUs = f.long("senderReceiveUs"),
                    probeBytes = f.int("probeBytes"),
                )

                assertEquals(
                    f.long("expectRttUs"), ack.rtt.toNanos() / 1_000L,
                    "rtt µs for $name",
                )
                assertEquals(
                    f.long("expectForwardOwdUs"), ack.forwardOwd.toNanos() / 1_000L,
                    "forwardOwd µs for $name",
                )
            }
        }

    // ── rto ───────────────────────────────────────────────────────────────────

    @TestFactory
    fun `rto clamped matches rfc6298`(): List<DynamicTest> =
        corpus["rto"]!!.jsonArray.map { fixture ->
            val f = fixture.jsonObject
            val name = f.string("name")
            dynamicTest("rto: $name") {
                val sample = BandwidthSample(
                    transportName = "T",
                    btlBwBps = 1_000_000,
                    availableBps = 900_000,
                    bdpBytes = 1000,
                    srtt = Duration.ofNanos((f.double("srttMs") * 1_000_000.0).toLong()),
                    rttVar = Duration.ofNanos((f.double("rttVarMs") * 1_000_000.0).toLong()),
                    rtProp = Duration.ofMillis(10),
                    lossRate = 0.0,
                    phyCapBps = 0L,
                    confidence = BandwidthConfidence.HIGH,
                    measuredAt = Instant.now(),
                )

                assertEquals(
                    f.double("expectRtoMs"), sample.rto.toNanos() / 1_000_000.0, 0.1,
                    "rto ms for $name",
                )
            }
        }

    // ── phyCap ─────────────────────────────────────────────────────────────────

    @TestFactory
    fun `phyCap from rssi exact`(): List<DynamicTest> =
        corpus["phyCap"]!!.jsonArray.map { fixture ->
            val f = fixture.jsonObject
            val name = f.string("name")
            dynamicTest("phyCap: $name") {
                val e = BandwidthEstimator("T", 10_000_000_000L)
                e.applyPhyHint(f.int("rssiDbm"))
                assertEquals(
                    f.long("expectCapBps"), e.currentSample.phyCapBps,
                    "phyCapBps for $name",
                )
            }
        }

    // ── estimator ──────────────────────────────────────────────────────────────

    @TestFactory
    fun `estimator drives to expected sample`(): List<DynamicTest> =
        corpus["estimator"]!!.jsonArray.map { fixture ->
            val f = fixture.jsonObject
            val name = f.string("name")
            dynamicTest("estimator: $name") {
                val e = BandwidthEstimator(f.string("transport"), f.long("maxBps"))

                for (opEl in f["ops"]!!.jsonArray) {
                    val op = opEl.jsonObject
                    when (op.string("op")) {
                        "delivery" -> e.recordDelivery(
                            op.int("bytes"),
                            op.long("sendUs"),
                            op.long("deliverUs"),
                        )
                        "loss" -> e.recordLoss(op.int("bytes"))
                        "phyHint" -> e.applyPhyHint(op.int("rssiDbm"))
                        "gossip" -> e.warmFromGossip(
                            op.long("btlBwBps"),
                            Duration.ofNanos((op.double("rtPropMs") * 1_000_000.0).toLong()),
                            parseConfidence(op.string("confidence")),
                        )
                        else -> error("unknown op")
                    }
                }

                val s = e.currentSample
                val exp = f["expect"]!!.jsonObject

                // Integer / enum fields — exact.
                exp.longOrNull("btlBwBps")?.let { assertEquals(it, s.btlBwBps, "btlBwBps for $name") }
                exp.longOrNull("effectiveBps")?.let { assertEquals(it, s.effectiveBps, "effectiveBps for $name") }
                exp.longOrNull("availableBps")?.let { assertEquals(it, s.availableBps, "availableBps for $name") }
                exp.longOrNull("bdpBytes")?.let { assertEquals(it, s.bdpBytes, "bdpBytes for $name") }
                exp.longOrNull("phyCapBps")?.let { assertEquals(it, s.phyCapBps, "phyCapBps for $name") }
                exp.stringOrNull("confidence")?.let {
                    assertEquals(parseConfidence(it), s.confidence, "confidence for $name")
                }

                // Float fields — tolerance.
                exp.doubleOrNull("srttMs")?.let {
                    assertEquals(it, s.srtt.toNanos() / 1_000_000.0, tol, "srttMs for $name")
                }
                exp.doubleOrNull("rttVarMs")?.let {
                    assertEquals(it, s.rttVar.toNanos() / 1_000_000.0, tol, "rttVarMs for $name")
                }
                exp.doubleOrNull("rtPropMs")?.let {
                    assertEquals(it, s.rtProp.toNanos() / 1_000_000.0, tol, "rtPropMs for $name")
                }
                exp.doubleOrNull("lossRate")?.let {
                    assertEquals(it, s.lossRate, tol, "lossRate for $name")
                }
            }
        }

    // ── director ───────────────────────────────────────────────────────────────

    @TestFactory
    fun `director recommends expected transport`(): List<DynamicTest> =
        corpus["director"]!!.jsonArray.map { fixture ->
            val f = fixture.jsonObject
            val name = f.string("name")
            dynamicTest("director: $name") {
                val director = BandwidthDirector()

                // Register one estimator per declared transport. Use a generous maxBps
                // so the PHY default does not cap the gossip-seeded values.
                for (t in f["register"]!!.jsonArray) {
                    director.register(
                        BandwidthEstimator(t.jsonPrimitive.content, 10_000_000_000L),
                    )
                }

                for (gEl in f["gossips"]!!.jsonArray) {
                    val g = gEl.jsonObject
                    director.applyGossip(
                        BandwidthGossipPayload(
                            peerUhid = g.string("peerUhid"),
                            transportName = g.string("transport"),
                            btlBwBps = g.long("btlBwBps"),
                            rtPropUs = g.long("rtPropUs"),
                            confidence = parseConfidence(g.string("confidence")),
                            measuredAt = Instant.now(),
                        ),
                    )
                }

                val rec = f["recommend"]!!.jsonObject
                val result = director.recommendTransport(
                    rec.string("peerUhid"),
                    rec.long("payloadBytes"),
                )

                val expectEl = f["expectTransport"]!!
                if (expectEl is JsonNull) {
                    assertNull(result, "expected null transport for $name")
                } else {
                    assertEquals(expectEl.jsonPrimitive.content, result, "transport for $name")
                }
            }
        }

    // ── JSON helpers ────────────────────────────────────────────────────────

    private fun JsonObject.string(key: String): String = this[key]!!.jsonPrimitive.content
    private fun JsonObject.long(key: String): Long = this[key]!!.jsonPrimitive.long
    private fun JsonObject.int(key: String): Int = this[key]!!.jsonPrimitive.int
    private fun JsonObject.double(key: String): Double = this[key]!!.jsonPrimitive.double

    private fun JsonObject.longOrNull(key: String): Long? = this[key]?.jsonPrimitive?.long
    private fun JsonObject.doubleOrNull(key: String): Double? = this[key]?.jsonPrimitive?.double
    private fun JsonObject.stringOrNull(key: String): String? = this[key]?.jsonPrimitive?.content
}
