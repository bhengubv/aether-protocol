// SPDX-License-Identifier: MIT

using System.IO.Compression;

namespace AetherNet.Messaging;

/// <summary>
/// Optional Brotli payload compression for <see cref="MessagingService"/>.
///
/// Compression sits between the application-level plaintext and the cipher:
/// large content/voice frames are compressed before encryption and decompressed
/// after decryption, so the wire only ever sees encrypted ciphertext. Bandwidth
/// savings only — does not affect crypto, the wire envelope, or fixtures.
///
/// **Migration note:** the on-the-plaintext flag byte that selects compressed
/// vs uncompressed is unconditional. A peer running pre-this-change code will
/// misinterpret that flag byte as the first byte of the application payload.
/// Adopters set <see cref="Enabled"/> to <c>false</c> until the rollout
/// completes (or until the version-negotiation handshake gates compression on
/// a "compression-brotli" capability).
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>
    /// If true, payloads at or above <see cref="MinSizeBytes"/> are Brotli-compressed
    /// before encryption. The compression flag byte is always present in the
    /// plaintext envelope — see the migration note above.
    ///
    /// Defaults to <c>false</c> until all 8 language implementations have shipped
    /// matching compression decode support AND the version-negotiation handshake
    /// gates compression on a peer-advertised "compression-brotli" capability.
    /// Hosts can opt in by setting this to <c>true</c> only when they're certain
    /// every peer they'll talk to has been upgraded.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum plaintext size (in bytes) at which compression is attempted. Below
    /// this threshold the flag is set to 0x00 and the payload is shipped raw.
    /// Default 256 — small payloads rarely benefit and the Brotli header alone is
    /// a few bytes. Must be non-negative.
    /// </summary>
    public int MinSizeBytes { get; set; } = 256;

    /// <summary>
    /// Brotli compression level. Default <see cref="CompressionLevel.Optimal"/>.
    /// Use <see cref="CompressionLevel.Fastest"/> on CPU-constrained nodes if
    /// the bandwidth savings still justify the codec cost.
    /// </summary>
    public CompressionLevel Level { get; set; } = CompressionLevel.Optimal;
}
