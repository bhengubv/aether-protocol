// SPDX-License-Identifier: MIT

package aethermesh.extensibility

import aethermesh.models.DtnBundle
import aethermesh.models.SosAlert
import aethermesh.protocol.MeshPacket
import aethermesh.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

private fun makePacket(from: String = "alice"): MeshPacket = MeshPacket(
    type = PacketType.Data,
    sourceUhid = from,
    destinationUhid = "bob",
    payload = "hello".toByteArray(),
)

private fun makeDtnBundle(): DtnBundle = DtnBundle(
    senderUhid = "alice",
    recipientUhid = "bob",
    encryptedPayload = byteArrayOf(0x01, 0x02, 0x03),
)

private fun makeSosAlert(): SosAlert = SosAlert(
    senderUhid = "alice",
    message = "help",
)

// ── NoopIncentiveProvider ─────────────────────────────────────────────────────

class NoopIncentiveProviderTest {

    @Test fun `instantiates without arguments`() {
        assertNotNull(NoopIncentiveProvider())
    }

    @Test fun `recordRelay returns Unit (no exception)`() = runBlocking {
        val p = NoopIncentiveProvider()
        p.recordRelay("alice", makePacket("alice")) // must not throw
    }

    @Test fun `recordRelay called multiple times is safe`() = runBlocking {
        val p = NoopIncentiveProvider()
        repeat(10) { i ->
            p.recordRelay("node-$i", makePacket("node-$i"))
        }
    }

    @Test fun `shouldPrioritize returns false`() = runBlocking {
        val p = NoopIncentiveProvider()
        assertFalse(p.shouldPrioritize(makePacket("alice")))
    }

    @Test fun `shouldPrioritize is always false for multiple packets`() = runBlocking {
        val p = NoopIncentiveProvider()
        for (uhid in listOf("alice", "bob", "carol", "dave", "eve")) {
            assertFalse(p.shouldPrioritize(makePacket(uhid)), "expected false for uhid=$uhid")
        }
    }

    @Test fun `implements IncentiveProvider interface`() {
        val p: IncentiveProvider = NoopIncentiveProvider()
        assertNotNull(p)
    }
}

// ── NoopBackendClient ─────────────────────────────────────────────────────────

class NoopBackendClientTest {

    @Test fun `instantiates without arguments`() {
        assertNotNull(NoopBackendClient())
    }

    @Test fun `relayMessage returns false`() = runBlocking {
        val c = NoopBackendClient()
        assertFalse(c.relayMessage("alice", "bob", byteArrayOf(1, 2, 3), 0))
    }

    @Test fun `relayMessage returns false for empty content`() = runBlocking {
        val c = NoopBackendClient()
        assertFalse(c.relayMessage("a", "b", byteArrayOf(), 1))
    }

    @Test fun `relayMessage returns false regardless of priority`() = runBlocking {
        val c = NoopBackendClient()
        for (pri in listOf(0, 1, 5, 100, 255)) {
            assertFalse(c.relayMessage("a", "b", byteArrayOf(1), pri), "expected false for priority=$pri")
        }
    }

    @Test fun `syncDtnBundle returns false`() = runBlocking {
        val c = NoopBackendClient()
        assertFalse(c.syncDtnBundle(makeDtnBundle()))
    }

    @Test fun `syncDtnBundle returns false for multiple bundles`() = runBlocking {
        val c = NoopBackendClient()
        repeat(5) {
            assertFalse(c.syncDtnBundle(makeDtnBundle()))
        }
    }

    @Test fun `syncSos returns false`() = runBlocking {
        val c = NoopBackendClient()
        assertFalse(c.syncSos(makeSosAlert()))
    }

    @Test fun `syncSos returns false for multiple alerts`() = runBlocking {
        val c = NoopBackendClient()
        repeat(5) {
            assertFalse(c.syncSos(makeSosAlert()))
        }
    }

    @Test fun `implements BackendClient interface`() {
        val c: BackendClient = NoopBackendClient()
        assertNotNull(c)
    }
}

// ── NoopFeatureFlagProvider ───────────────────────────────────────────────────

class NoopFeatureFlagProviderTest {

    @Test fun `instantiates without arguments`() {
        assertNotNull(NoopFeatureFlagProvider())
    }

    @Test fun `isEnabled returns true`() = runBlocking {
        val f = NoopFeatureFlagProvider()
        assertTrue(f.isEnabled("any-feature"))
    }

    @Test fun `isEnabled returns true for all known flags`() = runBlocking {
        val f = NoopFeatureFlagProvider()
        val flags = listOf(
            "rlnc", "dtn", "voice", "video", "watch-together",
            "group-voice", "sos", "", "FEATURE_UNDER_DEVELOPMENT"
        )
        for (flag in flags) {
            assertTrue(f.isEnabled(flag), "expected true for flag=$flag")
        }
    }

    @Test fun `implements FeatureFlagProvider interface`() {
        val f: FeatureFlagProvider = NoopFeatureFlagProvider()
        assertNotNull(f)
    }
}
