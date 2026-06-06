// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AetherNet.Diagnostics;
using AetherNet.Protocol;
using AetherNet.Reputation;
using Microsoft.Extensions.Logging;

namespace AetherNet.Security.Services;

/// <summary>
/// Signs and verifies MeshPacket signatures for authentication and replay protection.
/// Uses Ed25519 via <see cref="ISignalProtocolService"/> for signing operations.
/// Nonce deduplication prevents replay attacks within the freshness window.
/// </summary>
public sealed class PacketSigningService : IPacketSigningService, IDisposable
{
    private const int NonceSizeBytes = 8;
    private const int FreshnessWindowMs = 5 * 60 * 1000; // 5 minutes
    private const int CleanupIntervalMs = 60 * 1000; // 60 seconds

    private readonly ISignalProtocolService _signalProtocol;
    private readonly INodeReputationService? _reputation;
    private readonly ILogger<PacketSigningService> _logger;
    private readonly ConcurrentDictionary<string, long> _seenNonces = new();
    private readonly Timer _cleanupTimer;

    public PacketSigningService(
        ISignalProtocolService signalProtocol,
        ILogger<PacketSigningService> logger,
        INodeReputationService? reputation = null)
    {
        _signalProtocol = signalProtocol ?? throw new ArgumentNullException(nameof(signalProtocol));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _reputation = reputation;
        _cleanupTimer = new Timer(CleanupExpiredNonces, null, CleanupIntervalMs, CleanupIntervalMs);
    }

    /// <inheritdoc />
    public async Task<MeshPacket> SignPacketAsync(MeshPacket packet, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        using var activity = AetherNetTelemetry.ActivitySource.StartActivity("AetherNet.Sign.Packet");
        var stopwatch = ValueStopwatch.StartNew();
        try
        {
            // Fill packet metadata
            packet.PacketNonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            packet.TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            packet.ProtocolVersion = 2;

            // Build signable data and sign
            var signableData = BuildSignableData(packet);
            packet.Signature = await _signalProtocol.SignDataAsync(signableData, ct).ConfigureAwait(false);

            if (activity is not null)
                activity.SetTag("aethernet.packet.type", (int)packet.Type);

            _logger.LogDebug("Signed packet {PacketId} type={Type} src={Source}",
                packet.Id, packet.Type, LogSanitizer.SanitizeUhid(packet.SourceUhid));

            return packet;
        }
        finally
        {
            AetherNetTelemetry.SignVerifyLatency.Record(stopwatch.GetElapsedMilliseconds());
        }
    }

    /// <inheritdoc />
    public Task<bool> VerifyPacketAsync(MeshPacket packet, byte[] senderPublicKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(senderPublicKey);

        using var activity = AetherNetTelemetry.ActivitySource.StartActivity("AetherNet.Verify.Packet");
        var stopwatch = ValueStopwatch.StartNew();
        try
        {
            if (activity is not null)
                activity.SetTag("aethernet.packet.type", (int)packet.Type);

            // Check timestamp freshness
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ageMs = Math.Abs(nowMs - packet.TimestampMs);
            if (ageMs > FreshnessWindowMs)
            {
                AetherNetTelemetry.StaleTimestampsRejected.Add(1);
                if (activity is not null) activity.SetTag("aethernet.packet.valid", false);
                _logger.LogWarning("Packet {PacketId} rejected: timestamp too old ({AgeMs}ms)",
                    packet.Id, ageMs);
                return Task.FromResult(false);
            }

            // Check nonce deduplication. Key by (SourceUhid, nonce) so a collision
            // across different senders does NOT drop legitimate traffic — and so an
            // attacker who pre-registers a nonce against a recipient cannot block
            // the legitimate sender's first packet. (Pre-2026-05-05: keyed by
            // nonce alone, which had both failure modes.)
            var nonceKey = string.Concat(packet.SourceUhid, ":", Convert.ToHexString(packet.PacketNonce));
            if (!_seenNonces.TryAdd(nonceKey, nowMs))
            {
                AetherNetTelemetry.NoncesReplayed.Add(1);
                if (activity is not null) activity.SetTag("aethernet.packet.valid", false);
                _logger.LogWarning("Packet {PacketId} rejected: duplicate nonce from {Source}",
                    packet.Id, LogSanitizer.SanitizeUhid(packet.SourceUhid));
                _ = _reputation?.RecordReplayAttemptAsync(packet.SourceUhid);
                return Task.FromResult(false);
            }

            // Verify signature
            var signableData = BuildSignableData(packet);
            var valid = _signalProtocol.VerifySignature(senderPublicKey, signableData, packet.Signature);

            if (valid)
            {
                AetherNetTelemetry.SignaturesValidated.Add(1);
            }
            else
            {
                AetherNetTelemetry.SignaturesRejected.Add(1);
                _logger.LogWarning("Packet {PacketId} rejected: invalid signature from {Source}",
                    packet.Id, LogSanitizer.SanitizeUhid(packet.SourceUhid));
                _ = _reputation?.RecordSignatureFailureAsync(packet.SourceUhid);
            }

            if (activity is not null)
                activity.SetTag("aethernet.packet.valid", valid);

            return Task.FromResult(valid);
        }
        finally
        {
            AetherNetTelemetry.SignVerifyLatency.Record(stopwatch.GetElapsedMilliseconds());
        }
    }

    /// <summary>
    /// Builds the canonical byte array that is signed/verified for a packet.
    /// Wire format (must match every other language implementation's signable-data layout):
    ///   Nonce(8) || TimestampMs(8 LE i64) || Type(4 LE i32) || SourceLen(4 LE i32) || Source ||
    ///   DestLen(4 LE i32) || Dest || SHA256(Payload)(32) || Ttl(4 LE i32) || Priority(4 LE i32)
    ///
    /// Pre-2026-05-02 this method used big-endian + 1-byte Type/Priority, which broke
    /// signature interop with every other language. Fixed to little-endian + 4-byte i32s
    /// to match Go / Python / Rust / Kotlin / Swift / TS / C.
    /// </summary>
    public static byte[] BuildSignableData(MeshPacket packet)
    {
        var payloadHash = SHA256.HashData(packet.Payload);
        var sourceBytes = Encoding.UTF8.GetBytes(packet.SourceUhid);
        var destBytes = Encoding.UTF8.GetBytes(packet.DestinationUhid);

        // Nonce(8) + TimestampMs(8) + Type(4) + SourceLen(4) + Source + DestLen(4) + Dest + PayloadHash(32) + Ttl(4) + Priority(4)
        var totalLength = packet.PacketNonce.Length
            + 8  // TimestampMs (i64 LE)
            + 4  // Type (i32 LE)
            + 4 + sourceBytes.Length
            + 4 + destBytes.Length
            + 32 // SHA256 hash
            + 4  // Ttl (i32 LE)
            + 4; // Priority (i32 LE)

        var buffer = new byte[totalLength];
        var offset = 0;

        // PacketNonce
        Buffer.BlockCopy(packet.PacketNonce, 0, buffer, offset, packet.PacketNonce.Length);
        offset += packet.PacketNonce.Length;

        // TimestampMs (8 bytes, little-endian int64)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), packet.TimestampMs);
        offset += 8;

        // Type (4 bytes, little-endian int32 — was 1 byte, fixed 2026-05-02 to match other languages)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), (int)packet.Type);
        offset += 4;

        // SourceUhid (4-byte LE length + UTF-8 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), sourceBytes.Length);
        offset += 4;
        Buffer.BlockCopy(sourceBytes, 0, buffer, offset, sourceBytes.Length);
        offset += sourceBytes.Length;

        // DestinationUhid (4-byte LE length + UTF-8 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), destBytes.Length);
        offset += 4;
        Buffer.BlockCopy(destBytes, 0, buffer, offset, destBytes.Length);
        offset += destBytes.Length;

        // SHA256(Payload)
        Buffer.BlockCopy(payloadHash, 0, buffer, offset, 32);
        offset += 32;

        // Ttl (4 bytes, little-endian int32)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), packet.Ttl);
        offset += 4;

        // Priority (4 bytes, little-endian int32 — was 1 byte, fixed 2026-05-02 to match other languages)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), packet.Priority);
        offset += 4;

        return buffer;
    }

    private void CleanupExpiredNonces(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - FreshnessWindowMs;
        var removed = 0;

        foreach (var kvp in _seenNonces)
        {
            if (kvp.Value < cutoff)
            {
                if (_seenNonces.TryRemove(kvp.Key, out _))
                    removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogDebug("Nonce cleanup: removed {Count} expired entries, {Remaining} remaining",
                removed, _seenNonces.Count);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}
