// SPDX-License-Identifier: MIT
package aethernet.extensibility

import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import kotlin.test.assertEquals

/**
 * Tests for the v1.2.0 addition [IncentiveProvider.recordCreatorTip] (Issue #61).
 * Mirrors the C# IncentiveProviderCreatorTipTests.cs suite.
 */
class IncentiveProviderCreatorTipTest {

    @Test fun `recordCreatorTip default impl is noop and returns Unit`() = runBlocking {
        val provider: IncentiveProvider = DefaultProvider()

        // No throw, returns immediately.
        provider.recordCreatorTip("creator-uhid", BigDecimal("5.00"), "deadbeef")
    }

    @Test fun `recordCreatorTip custom impl receives arguments verbatim`() = runBlocking {
        val capturer = CapturingProvider()
        val provider: IncentiveProvider = capturer

        provider.recordCreatorTip("creator-zulu", BigDecimal("12.50"), "rootHash-abc")

        assertEquals(1, capturer.tips.size)
        val (creator, amount, hash) = capturer.tips[0]
        assertEquals("creator-zulu", creator)
        assertEquals(BigDecimal("12.50"), amount)
        assertEquals("rootHash-abc", hash)
    }

    @Test fun `recordCreatorTip and recordRelay are independent recording paths`() = runBlocking {
        val capturer = CapturingProvider()
        val provider: IncentiveProvider = capturer

        provider.recordCreatorTip("author", BigDecimal("1.00"), "h1")
        provider.recordRelay("node-uhid", MeshPacket(type = PacketType.Data))

        // Both recorded separately; the relay path doesn't pollute the tip stream and vice versa.
        assertEquals(1, capturer.tips.size)
        assertEquals(1, capturer.relays.size)
    }

    /** Uses every default method on the interface. */
    private class DefaultProvider : IncentiveProvider

    private class CapturingProvider : IncentiveProvider {
        val tips: MutableList<Triple<String, BigDecimal, String>> = mutableListOf()
        val relays: MutableList<Pair<String, MeshPacket>> = mutableListOf()

        override suspend fun recordCreatorTip(creatorUhid: String, amount: BigDecimal, contentHash: String) {
            tips += Triple(creatorUhid, amount, contentHash)
        }

        override suspend fun recordRelay(localUhid: String, packet: MeshPacket) {
            relays += Pair(localUhid, packet)
        }
    }
}
