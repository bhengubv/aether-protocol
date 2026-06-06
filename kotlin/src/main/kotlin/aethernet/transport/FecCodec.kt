// SPDX-License-Identifier: MIT

package aethernet.transport

/**
 * Forward Error Correction (FEC) codec interface.
 *
 * Implementations:
 *  - `PolarSCLCodec`       — Arıkan polar codes + SCL decoder (BLE, ≤ 512 B blocks).
 *  - `RaptorRFC5053Codec`  — Rateless Raptor fountain codes per RFC 5053 (LoRa).
 *
 * The codec is transport-agnostic. The transport layer calls [encode] before
 * writing to the wire and [tryDecode] after accumulating received symbols.
 */
interface FecCodec {

    // ── Identity ──────────────────────────────────────────────────────────────

    /** Human-readable identifier, e.g. `"Polar-SCL"` or `"Raptor-RFC5053"`. */
    val codecName: String

    /**
     * Minimum DeviceTier required to run this codec:
     *  - 0 = Full (desktop / server / phone) — all codecs supported.
     *  - 1 = Constrained (embedded Linux, low-RAM) — Polar-SCL supported.
     *  - 2 = Ultra-constrained (bare-metal MCU) — no FEC.
     */
    val deviceTierRequired: Byte

    /**
     * Fractional redundancy added by encoding (e.g. `0.30` = 30 %).
     * Used by the predictive selector when scoring FEC-decorated transports.
     */
    val overheadFraction: Double

    /**
     * Fixed symbol size in bytes for block codes (e.g. 64 for BLE Polar).
     * Returns `0` for variable-symbol codecs (e.g. Raptor).
     */
    val fixedSymbolSizeBytes: Int

    // ── Encode ────────────────────────────────────────────────────────────────

    /**
     * Encode [source] into [targetSymbolCount] concatenated output symbols.
     *
     * For systematic codes the first ⌈source.size / symbolSize⌉ output symbols
     * are byte-identical to the input; repair symbols follow.
     *
     * @param source            Original data to protect.
     * @param targetSymbolCount Total output symbols (≥ number of source symbols).
     * @return Encoded bytes (all output symbols concatenated).
     * @throws IllegalArgumentException If [targetSymbolCount] is too small.
     */
    fun encode(source: ByteArray, targetSymbolCount: Int): ByteArray

    // ── Decode ────────────────────────────────────────────────────────────────

    /**
     * Attempt to reconstruct source from [receivedSymbols].
     *
     * May succeed with fewer than [sourceSymbolCount] symbols (fountain property)
     * or return `null` if too many were lost.
     *
     * @param receivedSymbols   Individual received symbol byte arrays.
     * @param sourceSymbolCount Number of source symbols in the original object.
     * @return Reconstructed bytes on success, or `null` on failure.
     */
    fun tryDecode(receivedSymbols: List<ByteArray>, sourceSymbolCount: Int): ByteArray?
}
