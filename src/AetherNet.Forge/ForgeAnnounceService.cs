// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Forge;

/// <summary>
/// Wire payload for <see cref="PacketType.ForgeAnnounce"/> (41) — a node broadcasts this when it caches
/// a new package artifact, so mesh peers with the <c>aethernet.forge/v1</c> capability learn where the
/// artifact lives. Field order: package_id, content_hash, size_bytes, announced_at_ms. snake_case keys
/// pinned by <see cref="JsonPropertyNameAttribute"/>, ms + size as bare integers.
/// Byte-identity gate: fixtures/forge/vectors.json.
/// </summary>
public sealed class ForgeAnnouncePayload
{
    [JsonPropertyName("package_id")] public string PackageId { get; set; } = string.Empty;
    [JsonPropertyName("content_hash")] public string ContentHash { get; set; } = string.Empty;
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("announced_at_ms")] public long AnnouncedAtMs { get; set; }
}

/// <summary>
/// Binds <see cref="PacketType.ForgeAnnounce"/> (41) to the mesh: broadcast a freshly-cached artifact
/// announcement, and surface inbound announcements via <see cref="AnnounceReceived"/> (the host records
/// them in <c>IForgeService</c>). Transport for the aether-forge package-cache extension.
/// </summary>
public interface IForgeAnnounceService
{
    /// <summary>Raised when a forge announcement arrives from a peer.</summary>
    event EventHandler<ForgeAnnouncePayload>? AnnounceReceived;

    /// <summary>Announce a cached artifact to mesh peers. Returns the number of peers reached.</summary>
    Task<int> BroadcastAsync(string packageId, string contentHash, long sizeBytes, long announcedAtMs, CancellationToken cancellationToken = default);

    /// <summary>Process an inbound <see cref="PacketType.ForgeAnnounce"/>. Returns false on wrong type or malformed payload.</summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ForgeAnnounceService : IForgeAnnounceService
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IMeshSender _sender;
    private readonly ILogger<ForgeAnnounceService> _logger;

    public event EventHandler<ForgeAnnouncePayload>? AnnounceReceived;

    public ForgeAnnounceService(IMeshSender sender, ILogger<ForgeAnnounceService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<ForgeAnnounceService>.Instance;
    }

    /// <inheritdoc />
    public async Task<int> BroadcastAsync(string packageId, string contentHash, long sizeBytes, long announcedAtMs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        var payload = new ForgeAnnouncePayload
        {
            PackageId = packageId,
            ContentHash = contentHash ?? string.Empty,
            SizeBytes = sizeBytes,
            AnnouncedAtMs = announcedAtMs,
        };
        var packet = new MeshPacket
        {
            Type = PacketType.ForgeAnnounce,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = "*",
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };
        var delivered = await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("ForgeAnnounce {Pkg} broadcast to {N} peers", packageId, delivered);
        return delivered;
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.ForgeAnnounce)
            return Task.FromResult(false);

        ForgeAnnouncePayload? body;
        try
        {
            body = JsonSerializer.Deserialize<ForgeAnnouncePayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "ForgeAnnounce from {Source}: malformed payload — dropped", packet.SourceUhid);
            return Task.FromResult(false);
        }
        if (body is null || string.IsNullOrEmpty(body.PackageId))
            return Task.FromResult(false);

        AnnounceReceived?.Invoke(this, body);
        return Task.FromResult(true);
    }
}
