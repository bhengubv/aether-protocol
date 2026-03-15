// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aether.Protocol;
using Microsoft.Extensions.Logging;

namespace Aether.Security.Services;

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
    private readonly ILogger<PacketSigningService> _logger;
    private readonly ConcurrentDictionary<string, long> _seenNonces = new();
    private readonly Timer _cleanupTimer;

    public PacketSigningService(ISignalProtocolService signalProtocol, ILogger<PacketSigningService> logger)
    {
        _signalProtocol = signalProtocol ?? throw new ArgumentNullException(nameof(signalProtocol));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cleanupTimer = new Timer(CleanupExpiredNonces, null, CleanupIntervalMs, CleanupIntervalMs);
    }

    /// <inheritdoc />
    public async Task<MeshPacket> SignPacketAsync(MeshPacket packet, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // Fill packet metadata
        packet.PacketNonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        packet.TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        packet.ProtocolVersion = 2;

        // Build signable data and sign
        var signableData = BuildSignableData(packet);
        packet.Signature = await _signalProtocol.SignDataAsync(signableData, ct).ConfigureAwait(false);

        _logger.LogDebug("Signed packet {PacketId} type={Type} src={Source}",
            packet.Id, packet.Type, LogSanitizer.SanitizeUhid(packet.SourceUhid));

        return packet;
    }

    /// <inheritdoc />
    public Task<bool> VerifyPacketAsync(MeshPacket packet, byte[] senderPublicKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(senderPublicKey);

        // Check timestamp freshness
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ageMs = Math.Abs(nowMs - packet.TimestampMs);
        if (ageMs > FreshnessWindowMs)
        {
            _logger.LogWarning("Packet {PacketId} rejected: timestamp too old ({AgeMs}ms)",
                packet.Id, ageMs);
            return Task.FromResult(false);
        }

        // Check nonce deduplication
        var nonceKey = Convert.ToHexString(packet.PacketNonce);
        if (!_seenNonces.TryAdd(nonceKey, nowMs))
        {
            _logger.LogWarning("Packet {PacketId} rejected: duplicate nonce", packet.Id);
            return Task.FromResult(false);
        }

        // Verify signature
        var signableData = BuildSignableData(packet);
        var valid = _signalProtocol.VerifySignature(senderPublicKey, signableData, packet.Signature);

        if (!valid)
        {
            _logger.LogWarning("Packet {PacketId} rejected: invalid signature from {Source}",
                packet.Id, LogSanitizer.SanitizeUhid(packet.SourceUhid));
        }

        return Task.FromResult(valid);
    }

    /// <summary>
    /// Builds the canonical byte array that is signed/verified for a packet.
    /// Format: PacketNonce || TimestampMs || Type || SourceUhid || DestUhid || SHA256(Payload) || Ttl || Priority
    /// </summary>
    internal static byte[] BuildSignableData(MeshPacket packet)
    {
        var payloadHash = SHA256.HashData(packet.Payload);
        var sourceBytes = Encoding.UTF8.GetBytes(packet.SourceUhid);
        var destBytes = Encoding.UTF8.GetBytes(packet.DestinationUhid);

        // PacketNonce(8) + TimestampMs(8) + Type(1) + SourceLen(4) + Source + DestLen(4) + Dest + PayloadHash(32) + Ttl(4) + Priority(1)
        var totalLength = packet.PacketNonce.Length
            + 8  // TimestampMs
            + 1  // Type
            + 4 + sourceBytes.Length
            + 4 + destBytes.Length
            + 32 // SHA256 hash
            + 4  // Ttl
            + 1; // Priority

        var buffer = new byte[totalLength];
        var offset = 0;

        // PacketNonce
        Buffer.BlockCopy(packet.PacketNonce, 0, buffer, offset, packet.PacketNonce.Length);
        offset += packet.PacketNonce.Length;

        // TimestampMs (big-endian)
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 8), packet.TimestampMs);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(buffer, offset, 8);
        offset += 8;

        // Type
        buffer[offset++] = (byte)packet.Type;

        // SourceUhid (length-prefixed)
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), sourceBytes.Length);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(buffer, offset, 4);
        offset += 4;
        Buffer.BlockCopy(sourceBytes, 0, buffer, offset, sourceBytes.Length);
        offset += sourceBytes.Length;

        // DestinationUhid (length-prefixed)
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), destBytes.Length);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(buffer, offset, 4);
        offset += 4;
        Buffer.BlockCopy(destBytes, 0, buffer, offset, destBytes.Length);
        offset += destBytes.Length;

        // SHA256(Payload)
        Buffer.BlockCopy(payloadHash, 0, buffer, offset, 32);
        offset += 32;

        // Ttl (big-endian)
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), packet.Ttl);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(buffer, offset, 4);
        offset += 4;

        // Priority
        buffer[offset] = packet.Priority;

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
