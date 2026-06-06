// SPDX-License-Identifier: MIT

namespace AetherNet.Transport.Abstractions;

/// <summary>
/// Forward Error Correction (FEC) codec decorator.
///
/// Implementations wrap a physical transport and add systematic redundancy so
/// that the receiver can reconstruct the original data even if some symbols
/// are lost in transit.  Two concrete implementations are planned:
/// <list type="bullet">
///   <item><see cref="CodecName"/> = "Polar-SCL" — Arıkan polar codes with
///         Successive Cancellation List (SCL) decoding.  Optimal for short
///         blocks (≤ 512 bytes, BLE regime).  Requires DeviceTier ≤ 1.</item>
///   <item><see cref="CodecName"/> = "Raptor-RFC5053" — rateless fountain
///         codes per RFC 5053.  Ideal for asymmetric, high-loss links (LoRa).
///         Requires DeviceTier = 0 (full device).</item>
/// </list>
///
/// The codec is transport-agnostic: it operates purely on byte arrays and has
/// no knowledge of the underlying radio or network stack.  The transport layer
/// calls <see cref="Encode"/> before putting bytes on the wire and
/// <see cref="TryDecode"/> after accumulating received symbols.
/// </summary>
public interface IFecCodec
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Human-readable codec identifier (e.g. "Polar-SCL", "Raptor-RFC5053").
    /// Used in logs and for capability negotiation during the Hello handshake.
    /// </summary>
    string CodecName { get; }

    /// <summary>
    /// Minimum <c>DeviceTier</c> required to run this codec.
    /// <list type="bullet">
    ///   <item>0 = Full (desktop / server / phone) — all codecs supported.</item>
    ///   <item>1 = Constrained (embedded Linux) — Polar-SCL supported.</item>
    ///   <item>2 = Ultra-constrained (bare-metal MCU, &lt; 64 KB RAM) — no FEC.</item>
    /// </list>
    /// </summary>
    byte DeviceTierRequired { get; }

    /// <summary>
    /// Overhead fraction added by this codec (e.g. 0.30 = 30 % overhead).
    /// Used by <see cref="AetherNet.Transport.Services.PredictiveTransportSelector.Rank(int)"/> when scoring transports
    /// that have an active FEC decorator.
    /// </summary>
    double OverheadFraction { get; }

    // ── Encode ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="source"/> into <paramref name="targetSymbolCount"/>
    /// output symbols (repair symbols are appended after the systematic copy).
    ///
    /// For systematic codes the first <c>ceil(source.Length / symbolSize)</c>
    /// output symbols are byte-identical to the input; repair symbols follow.
    /// </summary>
    /// <param name="source">Original data to protect.</param>
    /// <param name="targetSymbolCount">
    ///   Total number of output symbols to produce, including both systematic
    ///   and repair symbols.  Must be ≥ the number of source symbols.
    /// </param>
    /// <returns>Encoded byte array containing all output symbols concatenated.</returns>
    /// <exception cref="ArgumentException">
    ///   Thrown when <paramref name="targetSymbolCount"/> is less than the
    ///   minimum required for this codec and source size.
    /// </exception>
    byte[] Encode(ReadOnlySpan<byte> source, int targetSymbolCount);

    // ── Decode ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to reconstruct the original data from <paramref name="receivedSymbols"/>.
    ///
    /// May succeed with fewer than <paramref name="sourceSymbolCount"/> symbols
    /// (fountain property) or fail if too many were lost.
    /// </summary>
    /// <param name="receivedSymbols">
    ///   Byte arrays of individual received symbols (some may be missing from
    ///   the original encoded set — callers should pass only what arrived).
    /// </param>
    /// <param name="sourceSymbolCount">
    ///   Number of source symbols in the original object.  Required so the
    ///   decoder knows how many bytes to reconstruct.
    /// </param>
    /// <param name="decoded">
    ///   When this method returns <see langword="true"/>, contains the
    ///   reconstructed source bytes.  Otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    ///   <see langword="true"/> if decoding succeeded;
    ///   <see langword="false"/> if insufficient symbols were received.
    /// </returns>
    bool TryDecode(ReadOnlyMemory<byte>[] receivedSymbols, int sourceSymbolCount,
                   out byte[]? decoded);

    // ── Symbol geometry ───────────────────────────────────────────────────────

    /// <summary>
    /// Fixed symbol size in bytes for block codes (e.g. 64 bytes for BLE Polar).
    /// Returns 0 for variable-symbol codecs (e.g. Raptor where symbols equal the
    /// source-block sub-symbol size negotiated per transfer).
    /// </summary>
    int FixedSymbolSizeBytes { get; }
}
